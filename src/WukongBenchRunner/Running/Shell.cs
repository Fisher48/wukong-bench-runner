using System.Diagnostics;
using System.Text;

namespace WukongBenchRunner.Running;

public sealed record ProcessInfo(int Pid, string CommandLine)
{
    public string DisplayCommandLine => CommandLine.Replace('\0', ' ').Trim();

    public string ExecutableName
    {
        get
        {
            var command = CommandLine.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? CommandLine;
            var separator = command.LastIndexOfAny(['/', '\\']);
            return separator >= 0 ? command[(separator + 1)..] : command;
        }
    }
}

public static class Shell
{
    // Клиент Steam запускается как <SteamRoot>/ubuntu12_32/steam, но FindProcessesByExecutable
    // сравнивает argv[0] с basename, поэтому ищем по имени файла, а не по куску пути.
    private static readonly string[] SteamClientArgs = ["steam", "steam.sh"];

    /// <summary>
    /// Pid главного клиента Steam. Служебные процессы отсеиваются: у них в argv[0] либо --type=,
    /// либо -child-, либо путь внутри steamapps (reaper и steam-launch-wrapper из каталога игры).
    /// </summary>
    public static int? SteamClientPid() =>
        FindSteamClients().Select(p => p.Pid).FirstOrDefault() is var pid && pid > 0 ? pid : null;

    public static IReadOnlyList<ProcessInfo> FindSteamClients() =>
        FindProcessesByExecutable(SteamClientArgs)
            .Where(IsSteamClient)
            .OrderBy(p => p.Pid)
            .ToList();

    /// <summary>
    /// Главный клиент Steam: argv[0] внутри Steam, но не внутри steamapps, и без служебных
    /// аргументов (--type=steamwebhelper, -child-update-ui), иначе это дочерний процесс.
    /// </summary>
    public static bool IsSteamClient(ProcessInfo process)
    {
        var argv0 = process.CommandLine.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        if (argv0.Contains("steamapps", StringComparison.OrdinalIgnoreCase))
            return false;

        if (argv0.Contains("launch-wrapper", StringComparison.OrdinalIgnoreCase)
            || argv0.Contains("reaper", StringComparison.OrdinalIgnoreCase))
            return false;

        if (process.CommandLine.Contains("--type=", StringComparison.Ordinal)
            || process.CommandLine.Contains("-child-", StringComparison.Ordinal))
            return false;

        return argv0.Contains("steam", StringComparison.OrdinalIgnoreCase);
    }

    public static (int ExitCode, string StdOut, string StdErr) Run(
        string fileName,
        IEnumerable<string> arguments,
        IDictionary<string, string>? environment = null,
        int timeoutMs = 20_000)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                info.Environment[key] = value;
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException($"Не удалось запустить {fileName}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException($"Не удалось запустить {fileName}: {ex.Message}", ex);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                TryKill(process);
                throw new TimeoutException($"{fileName} не завершился за {timeoutMs / 1000} с");
            }

            return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
    }

    public static bool HasExecutable(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        return paths.Any(directory =>
        {
            try
            {
                var full = Path.Combine(directory, name);
                return File.Exists(full) && new FileInfo(full).UnixFileMode.HasFlag(UnixFileMode.UserExecute);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        });
    }

    public static IReadOnlyList<ProcessInfo> FindProcessesByExecutable(IEnumerable<string> executableNames)
    {
        var names = executableNames.ToArray();
        var ancestors = AncestorPids();
        var found = new Dictionary<int, ProcessInfo>();

        foreach (var name in names)
        {
            if (!TryPgrep(name, out var pids))
                continue;

            foreach (var pid in pids)
            {
                if (ancestors.Contains(pid) || found.ContainsKey(pid))
                    continue;

                var commandLine = ReadCommandLine(pid);
                if (commandLine is null)
                    continue;

                var info = new ProcessInfo(pid, commandLine);
                if (!names.Any(expected => string.Equals(info.ExecutableName, expected, StringComparison.OrdinalIgnoreCase)))
                    continue;

                found[pid] = info;
            }
        }

        return found.Values.OrderBy(p => p.Pid).ToList();
    }

    public static IReadOnlyList<int> KillByExecutable(IEnumerable<string> executableNames, bool force = false)
    {
        var killed = new List<int>();

        if (!force)
        {
            foreach (var process in FindProcessesByExecutable(executableNames))
            {
                if (TrySignal(process.Pid, "-TERM"))
                    killed.Add(process.Pid);
            }

            if (killed.Count == 0)
                return killed;

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && FindProcessesByExecutable(executableNames).Count > 0)
                Thread.Sleep(250);
        }

        foreach (var process in FindProcessesByExecutable(executableNames))
        {
            if (TrySignal(process.Pid, "-KILL"))
                killed.Add(process.Pid);
        }

        return killed.Distinct().ToList();
    }

    private static bool TrySignal(int pid, string signal)
    {
        try
        {
            return Run("kill", [signal, pid.ToString()], timeoutMs: 5_000).ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return false;
        }
    }

    private static bool TryPgrep(string pattern, out IReadOnlyList<int> pids)
    {
        pids = [];

        try
        {
            var (exitCode, stdout, _) = Run("pgrep", ["-f", "--", pattern]);
            if (exitCode != 0)
                return false;

            pids = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => int.TryParse(line.Trim(), out var pid) ? pid : -1)
                .Where(pid => pid > 0)
                .ToList();

            return pids.Count > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return false;
        }
    }

    private static IReadOnlySet<int> AncestorPids()
    {
        var result = new HashSet<int> { Environment.ProcessId };
        var current = Environment.ProcessId;

        for (var i = 0; i < 16; i++)
        {
            var parent = ReadParentPid(current);
            if (parent <= 0 || !result.Add(parent))
                break;

            current = parent;
        }

        return result;
    }

    private static int ReadParentPid(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var afterName = stat[(stat.LastIndexOf(')') + 1)..].Trim().Split(' ');
            return afterName.Length > 1 && int.TryParse(afterName[1], out var ppid) ? ppid : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IndexOutOfRangeException)
        {
            return 0;
        }
    }

    private static string? ReadCommandLine(int pid)
    {
        try
        {
            var raw = File.ReadAllText($"/proc/{pid}/cmdline").TrimEnd('\0');
            return raw.Length == 0 ? null : raw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }
    }
}
