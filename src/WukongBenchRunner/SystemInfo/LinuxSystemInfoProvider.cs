using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WukongBenchRunner.Running;

namespace WukongBenchRunner.SystemInfo;

public sealed record SystemSnapshot(
    string OperatingSystem,
    string Kernel,
    string Architecture,
    string CpuModel,
    int CpuThreads,
    int CpuCores,
    string MemoryTotal,
    string SwapTotal,
    string GpuModel,
    string GpuDriver,
    string GpuMemoryNote,
    string DisplayServer,
    string DisplayResolution,
    string DisplayRefresh,
    string SteamVersion,
    string ProtonVersion,
    string ToolVersion)
{
    public int? DisplayHeight => LinuxSystemInfoProvider.ParseResolution(DisplayResolution)?.Height;

    public IReadOnlyList<(string Key, string Value)> Rows =>
    [
        ("Процессор", CpuModel),
        ("Ядра / потоки", $"{CpuCores} / {CpuThreads}"),
        ("Видеокарта", GpuModel),
        ("Видеодрайвер", GpuDriver),
        ("Видеопамять", GpuMemoryNote),
        ("Оперативная память", MemoryTotal),
        ("Файл подкачки", SwapTotal),
        ("ОС", OperatingSystem),
        ("Ядро", Kernel),
        ("Архитектура", Architecture),
        ("Графическая сессия", DisplayServer),
        ("Разрешение экрана", $"{DisplayResolution} @ {DisplayRefresh}"),
        ("Версия Steam", SteamVersion),
        ("Версия Proton", ProtonVersion),
        ("Версия Benchmark Tool", ToolVersion),
    ];
}

public static class LinuxSystemInfoProvider
{
    private static readonly Regex CpuModelPattern = new(@"^model name\s*:\s*(?<name>.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex CpuCoresPattern = new(@"^cpu cores\s*:\s*(?<cores>\d+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex PciDevicePattern = new(@"^(?<slot>[0-9a-fA-F]{2}:[0-9a-fA-F]{2}\.\d)\s+(?<rest>.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ConnectedOutputPattern = new(@"^(?<name>\S+)\s+connected\b.*?(?<width>\d+)x(?<height>\d+)\+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex CurrentModePattern = new(@"^\s+(?<mode>\d+x\d+)\s+(?<rate>[\d.]+)\s*\*", RegexOptions.Compiled);

    public static SystemSnapshot Collect(string installDir, string? protonVersion)
    {
        var cpuInfo = ReadText("/proc/cpuinfo");
        var memInfo = ReadText("/proc/meminfo");

        return new SystemSnapshot(
            OperatingSystem: ReadOsName(),
            Kernel: KernelVersion(),
            Architecture: RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            CpuModel: Match(cpuInfo, CpuModelPattern, "неизвестно"),
            CpuThreads: CountMatches(cpuInfo, "^processor\\s*:"),
            CpuCores: PhysicalCores(cpuInfo),
            MemoryTotal: FormatBytes(ReadMemInfo(memInfo, "MemTotal")),
            SwapTotal: FormatBytes(ReadMemInfo(memInfo, "SwapTotal")),
            GpuModel: GpuModel(),
            GpuDriver: GpuDriver(),
            GpuMemoryNote: GpuMemory(),
            DisplayServer: DisplayServer(),
            DisplayResolution: DisplayResolution(),
            DisplayRefresh: DisplayRefresh(),
            SteamVersion: SteamVersion(installDir),
            ProtonVersion: protonVersion ?? "неизвестно",
            ToolVersion: ToolVersion(installDir));
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string ReadOsName()
    {
        var osRelease = ReadText("/etc/os-release");
        var name = ExtractValue(osRelease, "PRETTY_NAME");

        if (name is null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Environment.OSVersion.VersionString;

        return name ?? "неизвестно";
    }

    private static string? ExtractValue(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith(key + "=", StringComparison.Ordinal))
                continue;

            var value = line[(key.Length + 1)..].Trim().Trim('"');
            return value.Length > 0 ? value : null;
        }

        return null;
    }

    private static string KernelVersion() => Run("uname", ["-r"]) ?? "неизвестно";

    private static string GpuModel()
    {
        var models = ParseDisplayDevices(Run("lspci", ["-nn"]) ?? string.Empty)
            .Select(CleanDeviceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return models.Count > 0 ? string.Join(" + ", models) : "неизвестно";
    }

    public static IReadOnlyList<string> ParseDisplayDevices(string lspciOutput) =>
        ParsePciDevices(lspciOutput)
            .Where(device => IsDisplayClass(device.Description))
            .Select(device => device.Description)
            .ToList();

    public static IReadOnlyList<string> ParseDisplayDrivers(string lspciOutput) =>
        ParsePciDevices(lspciOutput)
            .Where(device => IsDisplayClass(device.Description))
            .Select(device => Regex.Match(device.Details, @"Kernel driver in use:\s*(?<driver>\S+)"))
            .Where(match => match.Success)
            .Select(match => match.Groups["driver"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string CleanDeviceName(string description)
    {
        var name = description;

        var classMarker = Regex.Match(name, @"\]\s*:\s*(?<model>.+)$");
        if (classMarker.Success)
            name = classMarker.Groups["model"].Value;

        name = Regex.Replace(name, @"\[[^\]]*\]", " ");
        name = Regex.Replace(name, @"\s*\(rev [^)]+\)", " ");
        name = Regex.Replace(name, @"\s{2,}", " ");
        return name.Trim();
    }

    private static bool IsDisplayClass(string description) =>
        description.Contains("VGA compatible controller", StringComparison.Ordinal) ||
        description.Contains("Display controller", StringComparison.Ordinal) ||
        description.Contains("3D controller", StringComparison.Ordinal);

    private static IReadOnlyList<PciDevice> ParsePciDevices(string lspciOutput)
    {
        var devices = new List<PciDevice>();
        string? current = null;
        var details = new List<string>();

        void Flush()
        {
            if (current is not null)
                devices.Add(new PciDevice(current, string.Join("\n", details)));
        }

        foreach (var line in lspciOutput.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var header = PciDevicePattern.Match(line);
            if (header.Success)
            {
                Flush();
                current = header.Groups["rest"].Value;
                details.Clear();
                continue;
            }

            if (current is not null)
                details.Add(line.Trim());
        }

        Flush();
        return devices;
    }

    private sealed record PciDevice(string Description, string Details);

    private static string GpuDriver()
    {
        var output = Run("lspci", ["-k", "-nn"]) ?? Run("lspci", ["-nn"]) ?? string.Empty;
        var drivers = ParseDisplayDrivers(output);
        var lspciDrivers = drivers.Count > 0 ? string.Join(", ", drivers) : "неизвестно";
        var mesa = MesaVersion();

        return mesa is null ? lspciDrivers : $"{lspciDrivers}, {mesa}";
    }

    private static string? MesaVersion()
    {
        foreach (var name in new[] { "libgl1-mesa-dri", "mesa-vulkan-drivers", "libglx-mesa0" })
        {
            var output = Run("dpkg-query", ["-W", "-f=${Version}", name]);
            if (output is { Length: > 0 })
                return $"Mesa {output}";
        }

        return null;
    }

    private static string GpuMemory()
    {
        var output = Run("lspci", ["-v", "-nn"]);
        if (output is null)
            return "неизвестно";

        var match = Regex.Match(output, @"Memory size:\s*(?<size>\d+\s*[MK]B)", RegexOptions.Compiled);
        return match.Success ? match.Groups["size"].Value : "не сообщается (встроенная графика использует общую память)";
    }

    private static string DisplayServer()
    {
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(session))
            parts.Add(session);

        if (!string.IsNullOrWhiteSpace(display))
            parts.Add($"X11 {display}");

        if (!string.IsNullOrWhiteSpace(wayland))
            parts.Add($"Wayland {wayland}");

        return parts.Count > 0 ? string.Join(" + ", parts) : "неизвестно";
    }

    private static string DisplayResolution() => ResolutionFromXrandr(Run("xrandr", ["--current"])) ?? "неизвестно";

    public static (int Width, int Height)? ParseResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return null;

        // бенчмарк пишет "1920 × 1200" с символом умножения, xrandr — "1920x1200"
        var normalized = resolution.Replace('\u00d7', 'x').Replace(" ", string.Empty).ToLowerInvariant();
        var parts = normalized.Split('x');
        if (parts.Length != 2)
            return null;

        return int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height) && width > 0 && height > 0
            ? (width, height)
            : null;
    }

    /// <summary>Высота активного выхода xrandr: от неё считается высота рендера в профилях.</summary>
    public static int? DetectDisplayHeight() =>
        ParseResolution(ResolutionFromXrandr(Run("xrandr", ["--current"])))?.Height;

    public static string? ResolutionFromXrandr(string? output)
    {
        if (output is null)
            return null;

        var match = ConnectedOutputPattern.Match(output);
        return match.Success
            ? $"{match.Groups["width"].Value}x{match.Groups["height"].Value}"
            : null;
    }

    private static string DisplayRefresh()
    {
        var output = Run("xrandr", ["--current"]);
        if (output is null)
            return "неизвестно";

        var lines = output.Split('\n');
        var insidePrimary = false;

        foreach (var line in lines)
        {
            if (line.Contains(" connected", StringComparison.Ordinal))
            {
                insidePrimary = line.Contains("primary", StringComparison.Ordinal);
                continue;
            }

            if (!insidePrimary)
                continue;

            var mode = CurrentModePattern.Match(line);
            if (mode.Success)
                return mode.Groups["rate"].Value + " Гц";
        }

        return "неизвестно";
    }

    private static int PhysicalCores(string cpuInfo)
    {
        var declared = ParseInt(Match(cpuInfo, CpuCoresPattern, "0"));
        if (declared > 0)
            return declared;

        var cores = new HashSet<string>();

        foreach (var block in Regex.Split(cpuInfo, @"^processor\s*:", RegexOptions.Multiline).Skip(1))
        {
            var physical = Regex.Match(block, @"^physical id\s*:\s*(?<id>\d+)", RegexOptions.Multiline);
            var core = Regex.Match(block, @"^core id\s*:\s*(?<id>\d+)", RegexOptions.Multiline);

            if (physical.Success && core.Success)
                cores.Add(physical.Groups["id"].Value + ":" + core.Groups["id"].Value);
        }

        return cores.Count > 0 ? cores.Count : 0;
    }

    private static string SteamVersion(string installDir)
    {
        var file = Path.Combine(Path.GetDirectoryName(installDir) ?? ".", "..", "..", "steamapps", "appmanifest_3132990.acf");
        try
        {
            var text = File.ReadAllText(file);
            var match = Regex.Match(text, "\"buildid\"\\s*\"(?<id>\\d+)\"");
            return match.Success ? "build " + match.Groups["id"].Value : "неизвестно";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "неизвестно";
        }
    }

    private static string ToolVersion(string installDir)
    {
        try
        {
            var exe = Path.Combine(installDir, "b1", "Binaries", "Win64", "b1-Win64-Shipping.exe");
            var file = new FileInfo(exe);
            return file.Exists
                ? $"{Path.GetFileName(exe)}, {file.Length / 1024 / 1024} МБ, изменён {file.LastWriteTime:yyyy-MM-dd}"
                : "не найден исполняемый файл";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "неизвестно";
        }
    }

    private static long ReadMemInfo(string memInfo, string key)
    {
        foreach (var line in memInfo.Split('\n'))
        {
            if (!line.StartsWith(key + ":", StringComparison.Ordinal))
                continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out var kilobytes))
                return kilobytes * 1024;
        }

        return 0;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "неизвестно";

        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string Match(string text, Regex pattern, string fallback)
    {
        var match = pattern.Match(text);
        return match.Success ? match.Groups["name"].Value.Trim() : fallback;
    }

    private static int ParseInt(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static int CountMatches(string text, string pattern) =>
        Regex.Matches(text, pattern, RegexOptions.Multiline).Count;

    private static string? Run(string fileName, IEnumerable<string> arguments)
    {
        try
        {
            var (exitCode, stdout, _) = Shell.Run(fileName, arguments, timeoutMs: 10_000);
            return exitCode == 0 ? stdout.Trim() : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }
}
