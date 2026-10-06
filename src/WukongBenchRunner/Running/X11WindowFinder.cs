using System.Globalization;
using System.Text.RegularExpressions;

namespace WukongBenchRunner.Running;

public sealed record WindowInfo(
    ulong Id,
    string Name,
    int Pid,
    int AbsoluteX,
    int AbsoluteY,
    int Width,
    int Height,
    bool IsViewable)
{
    public int AbsoluteRight => AbsoluteX + Width;
    public int AbsoluteBottom => AbsoluteY + Height;
}

public sealed record WindowCandidate(ulong Id, string Name, int X, int Y, int Width, int Height)
{
    public long Area => (long)Width * Height;
}

public static class X11WindowFinder
{
    private static readonly Regex TreeLinePattern = new(
        @"^\s+(?<id>0x[0-9a-fA-F]+)\s+(?:""(?<title>[^""]*)""|\(has no name\))(?<rest>.*?)(?<width>\d+)x(?<height>\d+)\+(?<x>-?\d+)\+(?<y>-?\d+)",
        RegexOptions.Compiled);

    private static readonly Regex MapStatePattern = new(@"Map State:\s*(?<state>\w+)", RegexOptions.Compiled);
    private static readonly Regex NumberPattern = new(@"(?<value>-?\d+)", RegexOptions.Compiled);
    private static readonly Regex PidPattern = new(@"_NET_WM_PID\(CARDINAL\)\s*=\s*(?<pid>\d+)", RegexOptions.Compiled);

    private const int MinimumWidth = 640;
    private const int MinimumHeight = 400;
    private const int MaximumInspectedWindows = 40;

    public static bool ToolsAvailable => Shell.HasExecutable("xprop") && Shell.HasExecutable("xwininfo");

    public static IReadOnlyList<WindowCandidate> ParseTree(string treeOutput)
    {
        var candidates = new List<WindowCandidate>();

        foreach (var line in treeOutput.Split('\n'))
        {
            var match = TreeLinePattern.Match(line);
            if (!match.Success)
                continue;

            candidates.Add(new WindowCandidate(
                ulong.Parse(match.Groups["id"].Value.AsSpan(2), NumberStyles.HexNumber),
                match.Groups["title"].Value,
                int.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["height"].Value, CultureInfo.InvariantCulture)));
        }

        return candidates;
    }

    public static WindowInfo? FindGameWindow(IReadOnlySet<int> candidatePids, IReadOnlyList<string> nameHints)
    {
        var windows = ListWindows().Where(w => w.IsViewable).ToList();

        foreach (var window in windows)
        {
            if (window.Pid != 0 && candidatePids.Contains(window.Pid))
                return window;
        }

        return FindByName(windows, nameHints);
    }

    /// <summary>
    /// Запасной поиск по заголовку, когда окно не нашлось по pid. Заголовок бенчмарка короткий и
    /// без следов файлов и оболочки: "b1", "Black Myth: Wukong Benchmark Tool" или steam_app_3132990.
    /// Поэтому здесь не подстрока "wukong" (она совпадает с окном редактора над этим репозиторием),
    /// а начало заголовка с названием игры плюс отсев всего, что похоже на файл или команду.
    /// </summary>
    public static WindowInfo? FindByName(IReadOnlyList<WindowInfo> windows, IReadOnlyList<string> nameHints)
    {
        var gameLike = windows.Where(w => w.IsViewable && LooksLikeGameTitle(w.Name)).ToList();

        // сначала заголовок, начинающийся с названия игры: это надёжнее, чем подстрока в середине
        foreach (var window in gameLike.Where(w => StartsWithGameName(w.Name, nameHints)))
            return window;

        foreach (var hint in nameHints)
        {
            var byName = gameLike.FirstOrDefault(w =>
                w.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
                return byName;
        }

        return null;
    }

    private static bool StartsWithGameName(string title, IReadOnlyList<string> hints)
    {
        var trimmed = title.TrimStart();

        return hints.Any(hint =>
            trimmed.StartsWith(hint, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Black Myth", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("steam_app_", StringComparison.OrdinalIgnoreCase));
    }

    public static bool LooksLikeGameTitle(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var trimmed = name.Trim();

        if (trimmed.Length > 64)
            return false;

        // пути и оболочка: заголовок терминала, а не игры
        foreach (var marker in new[] { "/", "$", ";", "&", "|", "'", "\"" })
        {
            if (trimmed.Contains(marker, StringComparison.Ordinal))
                return false;
        }

        // флаги командной строки ("bash -c", "vim -R")
        if (trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word => word.StartsWith('-')))
            return false;

        // заголовок редактора или файлового менеджера: "project - README.md", "name — file.txt"
        if (trimmed.Contains(".md", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains(".txt", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains(".cs", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\u2014", StringComparison.Ordinal)
            || trimmed.Contains(" - ", StringComparison.Ordinal)
            || trimmed.Contains("[", StringComparison.Ordinal))
            return false;

        // вид "user@host: ~"
        if (trimmed.Contains('@') && trimmed.Contains(':', StringComparison.Ordinal))
            return false;

        return true;
    }

    public static IReadOnlyList<WindowInfo> ListWindows()
    {
        var tree = RunXwinInfo(["-root", "-tree"]);
        if (tree is null)
            return [];

        var pids = Shell.FindProcessesByExecutable(["b1-Win64-Shipping.exe", "b1_benchmark.exe"]).Select(p => p.Pid).ToHashSet();

        var ordered = ParseTree(tree)
            .Where(c => c.Width >= MinimumWidth && c.Height >= MinimumHeight)
            .OrderByDescending(c => pids.Count > 0 && LooksLikeGame(c) ? 1 : 0)
            .ThenByDescending(c => c.Area)
            .Take(MaximumInspectedWindows);

        var windows = new List<WindowInfo>();

        foreach (var candidate in ordered)
        {
            var details = RunXwinInfo(["-id", "0x" + candidate.Id.ToString("x")]);
            var viewable = details is not null && MapStatePattern.Match(details).Groups["state"].Value == "IsViewable";

            windows.Add(new WindowInfo(
                candidate.Id,
                candidate.Name,
                ReadPid(candidate.Id),
                ReadAbsolute(details, "Absolute upper-left X", candidate.X),
                ReadAbsolute(details, "Absolute upper-left Y", candidate.Y),
                candidate.Width,
                candidate.Height,
                viewable));
        }

        return windows;
    }

    private static bool LooksLikeGame(WindowCandidate candidate) =>
        candidate.Name.Contains("b1", StringComparison.OrdinalIgnoreCase) ||
        candidate.Name.Contains("wukong", StringComparison.OrdinalIgnoreCase) ||
        candidate.Name.Contains("steam_app_3132990", StringComparison.OrdinalIgnoreCase);

    private static int ReadPid(ulong windowId)
    {
        var (_, stdout, _) = Shell.Run("xprop", ["-id", "0x" + windowId.ToString("x"), "_NET_WM_PID"]);
        var match = PidPattern.Match(stdout);
        return match.Success && int.TryParse(match.Groups["pid"].Value, out var pid) ? pid : 0;
    }

    private static int ReadAbsolute(string? details, string label, int fallback)
    {
        if (details is null)
            return fallback;

        var index = details.IndexOf(label + ":", StringComparison.Ordinal);
        if (index < 0)
            return fallback;

        var match = NumberPattern.Match(details, index + label.Length);
        return match.Success ? int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture) : fallback;
    }

    private static string? RunXwinInfo(IEnumerable<string> arguments)
    {
        try
        {
            var (exitCode, stdout, _) = Shell.Run("xwininfo", arguments, timeoutMs: 10_000);
            return exitCode == 0 ? stdout : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    public static void EnsureTools()
    {
        if (ToolsAvailable)
            return;

        throw new InvalidOperationException(
            "Не найдены утилиты xprop/xwininfo из пакета x11-utils."
            + Environment.NewLine + "Установите их: sudo apt install x11-utils");
    }
}
