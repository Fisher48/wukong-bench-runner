using System.Runtime.InteropServices;

namespace WukongBenchRunner.Discovery;

public sealed record InstallationOptions
{
    public const int BenchmarkAppId = 3132990;

    public string? SteamRoot { get; init; }
    public string? InstallDir { get; init; }
    public string? HistoryDir { get; init; }
}

public sealed record BenchmarkInstallation
{
    public required string SteamRoot { get; init; }
    public required string LibraryRoot { get; init; }
    public required string InstallDir { get; init; }
    public required string ConfigPath { get; init; }
    public required string HistoryDir { get; init; }
    public required IReadOnlyList<string> HistoryCandidates { get; init; }
    public string? PrefixDir { get; init; }
    public string? ProtonVersion { get; init; }
    public string? LauncherExe { get; init; }
    public string? ShippingExe { get; init; }

    public bool PrefixExists => PrefixDir is not null && Directory.Exists(PrefixDir);
    public bool ConfigExists => File.Exists(ConfigPath);
    public bool HistoryExists => Directory.Exists(HistoryDir);
}

public static class SteamLocator
{
    private const string BenchmarkFolder = "Black Myth Wukong Benchmark Tool";

    public static IReadOnlyList<string> SteamRootCandidates()
    {
        var candidates = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var full = Path.GetFullPath(path);
            if (!candidates.Contains(full, StringComparer.Ordinal))
                candidates.Add(full);
        }

        Add(Environment.GetEnvironmentVariable("STEAM_ROOT"));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
            ? dataHome
            : Path.Combine(home, ".local", "share");

        Add(Path.Combine(home, ".steam", "steam"));
        Add(Path.Combine(xdgData, "Steam"));
        Add(Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"));
        Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
        Add(Path.Combine(xdgData, "SteamLinuxRuntime", "steam"));
        Add("/usr/local/share/Steam");
        Add("/usr/share/steam");

        return candidates;
    }

    public static BenchmarkInstallation Locate(InstallationOptions options)
    {
        var roots = options.SteamRoot is { Length: > 0 } root
            ? new List<string> { Path.GetFullPath(root) }
            : SteamRootCandidates().ToList();

        if (options.InstallDir is { Length: > 0 } explicitDir)
            return BuildFromInstallDir(Path.GetFullPath(explicitDir), roots, options);

        var problems = new List<string>();

        foreach (var steamRoot in roots)
        {
            if (!Directory.Exists(steamRoot))
                continue;

            foreach (var libraryRoot in LibraryRoots(steamRoot))
            {
                var manifest = Path.Combine(libraryRoot, "steamapps", $"appmanifest_{InstallationOptions.BenchmarkAppId}.acf");
                if (!File.Exists(manifest))
                    continue;

                var installDirName = VdfParser.ParseFile(manifest).Child("AppState")?.ChildValue("installdir");
                if (installDirName is not { Length: > 0 })
                    installDirName = BenchmarkFolder;

                var installDir = Path.Combine(libraryRoot, "steamapps", "common", NormalizeVdfPath(installDirName));
                if (!Directory.Exists(installDir))
                {
                    problems.Add($"манифест есть, но каталог отсутствует: {installDir}");
                    continue;
                }

                return Build(libraryRoot, installDir, steamRoot, options);
            }

            problems.Add($"{steamRoot}: нет appmanifest_{InstallationOptions.BenchmarkAppId}.acf ни в одной библиотеке");
        }

        throw new InvalidOperationException(
            $"Не найден Black Myth: Wukong Benchmark Tool (AppID {InstallationOptions.BenchmarkAppId})."
            + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p))
            + Environment.NewLine + "Укажите путь вручную: --install-dir \"<библиотека>/steamapps/common/Black Myth Wukong Benchmark Tool\"");
    }

    public static IReadOnlyList<string> LibraryRoots(string steamRoot)
    {
        var libraries = new List<string>();
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");

        if (File.Exists(vdfPath))
        {
            var node = VdfParser.ParseFile(vdfPath).Child("libraryfolders");
            if (node is not null)
            {
                foreach (var entry in node.Nodes())
                {
                    var path = entry.ChildValue("path") ?? entry.Value;
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    var full = NormalizeVdfPath(path);
                    if (Directory.Exists(full) && !libraries.Contains(full, StringComparer.Ordinal))
                        libraries.Add(full);
                }
            }
        }

        if (libraries.Count == 0)
            libraries.Add(steamRoot);

        return libraries;
    }

    public static IReadOnlyList<string> HistoryCandidates(string installDir, string? prefixDir)
    {
        var candidates = new List<string>();

        if (prefixDir is { Length: > 0 })
        {
            var wineRoot = Path.Combine(prefixDir, "pfx");
            candidates.Add(Path.Combine(
                wineRoot, "drive_c", "users", "steamuser", "AppData", "Local", "Temp",
                "b1", "BenchMarkHistory", "Tool"));

            candidates.AddRange(SearchHistoryDirs(wineRoot));
        }

        candidates.Add(Path.Combine(installDir, "b1", "Saved", "BenchMarkHistory", "Tool"));
        candidates.Add(Path.Combine(installDir, "b1", "BenchMarkHistory", "Tool"));

        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> SearchHistoryDirs(string wineRoot)
    {
        var root = Path.Combine(wineRoot, "drive_c", "users");
        if (!Directory.Exists(root))
            return [];

        var found = new List<string>();
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (path, depth) = stack.Pop();

            string[] children;
            try
            {
                children = Directory.GetDirectories(path);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (string.Equals(Path.GetFileName(child), "BenchMarkHistory", StringComparison.OrdinalIgnoreCase))
                {
                    var tool = Path.Combine(child, "Tool");
                    if (Directory.Exists(tool))
                        found.Add(tool);
                    continue;
                }

                if (depth < 6)
                    stack.Push((child, depth + 1));
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static BenchmarkInstallation BuildFromInstallDir(string installDir, IReadOnlyList<string> steamRoots, InstallationOptions options)
    {
        if (!Directory.Exists(installDir))
            throw new InvalidOperationException($"Каталог не найден: {installDir}");

        var libraryRoot = Path.GetFullPath(Path.Combine(installDir, "..", ".."));
        var steamApps = Path.Combine(libraryRoot, "steamapps");
        var steamRoot = steamRoots.FirstOrDefault(r =>
            string.Equals(Path.GetFullPath(Path.Combine(r, "steamapps")), steamApps, StringComparison.Ordinal))
            ?? steamRoots.FirstOrDefault() ?? libraryRoot;

        return Build(libraryRoot, installDir, steamRoot, options);
    }

    private static BenchmarkInstallation Build(string libraryRoot, string installDir, string steamRoot, InstallationOptions options)
    {
        var prefixDir = Path.Combine(libraryRoot, "steamapps", "compatdata", InstallationOptions.BenchmarkAppId.ToString());
        var configPath = Path.Combine(installDir, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini");
        var historyCandidates = HistoryCandidates(installDir, prefixDir);

        var historyDir = options.HistoryDir is { Length: > 0 } explicitHistory
            ? Path.GetFullPath(explicitHistory)
            : historyCandidates.FirstOrDefault(c => Directory.Exists(c)) ?? historyCandidates[0];

        return new BenchmarkInstallation
        {
            SteamRoot = steamRoot,
            LibraryRoot = libraryRoot,
            InstallDir = installDir,
            ConfigPath = configPath,
            HistoryDir = historyDir,
            HistoryCandidates = historyCandidates,
            PrefixDir = prefixDir,
            ProtonVersion = ReadProtonVersion(prefixDir),
            LauncherExe = Path.Combine(installDir, "b1_benchmark.exe"),
            ShippingExe = Path.Combine(installDir, "b1", "Binaries", "Win64", "b1-Win64-Shipping.exe"),
        };
    }

    private static string? ReadProtonVersion(string prefixDir)
    {
        var versionFile = Path.Combine(prefixDir, "version");
        if (File.Exists(versionFile))
        {
            var text = File.ReadAllText(versionFile).Trim();
            if (text.Length > 0)
                return text;
        }

        var configInfo = Path.Combine(prefixDir, "config_info");
        if (File.Exists(configInfo))
        {
            var first = File.ReadLines(configInfo).FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(first))
                return first;
        }

        return null;
    }

    private static string NormalizeVdfPath(string path) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? path.Replace('/', '\\') : path;
}
