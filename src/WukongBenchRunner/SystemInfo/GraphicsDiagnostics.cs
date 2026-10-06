using System.Runtime.InteropServices;
using WukongBenchRunner.Running;

namespace WukongBenchRunner.SystemInfo;

/// <summary>
/// Проверки, без которых Proton не запустит игру на Wayland/XWayland.
/// DRI3 нужен Vulkan для вывода кадра: без него игра пишет
/// "vulkan: No DRI3 support detected" и завершается сразу после старта.
/// </summary>
public sealed record GraphicsDiagnostics(
    bool Dri3Available,
    bool XTestAvailable,
    string Display,
    IReadOnlyList<string> DrmDevices,
    bool VulkanIcdAvailable,
    bool SteamRunning)
{
    public bool Blocked => !Dri3Available;

    public string Summary =>
        $"DRI3: {(Dri3Available ? "есть" : "нет")}, XTEST: {(XTestAvailable ? "есть" : "нет")}, "
        + $"DRM: {(DrmDevices.Count > 0 ? string.Join(", ", DrmDevices) : "нет")}, "
        + $"Vulkan ICD: {(VulkanIcdAvailable ? "есть" : "нет")}, "
        + $"Steam: {(SteamRunning ? "запущен" : "не запущен")}";

    public string? Warning => Blocked
        ? "Внимание: в текущем XWayland нет расширения DRI3. Proton в этом случае не может вывести кадр, "
          + "игра завершается сразу после старта (код 10). Перезайдите в сессию или используйте Xorg."
        : null;

    public static GraphicsDiagnostics Probe() => Probe(ShellProbeFactory.Default());

    public static GraphicsDiagnostics Probe(ShellProbe probe)
    {
        var display = probe.Display;
        var dri3 = !string.IsNullOrWhiteSpace(display)
                   && ParseHasExtension(probe.Run("xdpyinfo", []), "DRI3");

        return new GraphicsDiagnostics(
            Dri3Available: dri3,
            XTestAvailable: probe.HasLibrary("libXtst.so.6"),
            Display: string.IsNullOrWhiteSpace(display) ? "не задан" : display,
            DrmDevices: ListDrmDevices(),
            VulkanIcdAvailable: VulkanIcdPresent(),
            SteamRunning: Shell.SteamClientPid() is not null);
    }

    public static bool ParseHasExtension(string? xdpyinfoOutput, string extension) =>
        xdpyinfoOutput is not null
        && xdpyinfoOutput.Split('\n').Any(line =>
            line.Trim().StartsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> ListDrmDevices()
    {
        if (!Directory.Exists("/dev/dri"))
            return [];

        return Directory.GetFiles("/dev/dri", "card*")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static bool VulkanIcdPresent() =>
        Directory.Exists("/usr/share/vulkan/icd.d") && Directory.GetFiles("/usr/share/vulkan/icd.d").Length > 0;
}

/// <summary>
/// Откуда берутся данные о графике. Вынесено отдельно, чтобы проверки тестировались без X-сервера.
/// </summary>
public sealed record ShellProbe(
    string Display,
    Func<string, IReadOnlyList<string>, string?> Run,
    Func<string, bool> HasLibrary)
{
}

public static class ShellProbeFactory
{
    public static ShellProbe Default() => new(
        Environment.GetEnvironmentVariable("DISPLAY") ?? string.Empty,
        (fileName, arguments) =>
        {
            try
            {
                return Shell.Run(fileName, arguments).StdOut;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                return null;
            }
        },
        name =>
        {
            try
            {
                if (!NativeLibrary.TryLoad(name, out var handle))
                    return false;

                NativeLibrary.Free(handle);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or BadImageFormatException or DllNotFoundException)
            {
                return false;
            }
        });
}