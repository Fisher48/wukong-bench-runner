using WukongBenchRunner.Config;
using WukongBenchRunner.SystemInfo;

using System.Text.Json;

namespace WukongBenchRunner.Results;

public sealed record FrameStats(
    int Frames,
    double AverageFps,
    double MedianFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double P95Fps,
    double MinFps,
    double MaxFps,
    double AverageFrameMs);

public sealed record AppliedSettings(
    string ScreenResolution,
    int ScreenMode,
    int QualityLevel,
    int RenderScalePercent,
    int? ViewDistance,
    int? AntiAliasing,
    int? PostProcessing,
    int? ShadowQuality,
    int? TextureQuality,
    int? MaterialQuality,
    int? VegetationQuality,
    int? MotionBlur,
    int RayTracing,
    int Upscaler,
    int FrameGeneration,
    int DirectX12);

public sealed record BenchmarkResult
{
    public required string Path { get; init; }
    public required string TimeStamp { get; init; }
    public required double FpsAverage { get; init; }
    public required double FpsMinimum { get; init; }
    public required double FpsMaximum { get; init; }
    public required double Fps95 { get; init; }
    public required FrameStats FrameStats { get; init; }
    public required AppliedSettings Applied { get; init; }
    public required string GameVersion { get; init; }
    public required string SystemVersion { get; init; }
    public required string CpuModel { get; init; }
    public required string GpuModel { get; init; }
    public required string GpuDriverVersion { get; init; }
    public required string VideoMemory { get; init; }
    public required string SystemMemory { get; init; }
    public double? ReportedCpuAverage { get; init; }
    public double? ReportedGpuAverage { get; init; }
    public bool CpuLoadReliable { get; init; }
    public bool GpuLoadReliable { get; init; }
    public string CpuLoadNote { get; init; } = string.Empty;
    public string GpuLoadNote { get; init; } = string.Empty;
}

public sealed class ResultParseException(string message) : Exception(message);

public static class ResultParser
{
    public static BenchmarkResult ParseFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Parse(stream, path);
        }
        catch (ResultParseException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ResultParseException($"Не удалось прочитать файл результата {path}: {ex.Message}");
        }
    }

    public static BenchmarkResult Parse(Stream stream, string path)
    {
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ResultParseException($"Файл результата {path}: ожидался JSON-объект");

        var frames = ReadFrames(root);
        if (frames.Count == 0)
            throw new ResultParseException($"Файл результата {path}: нет ни одного кадра в Records");

        var cpuLoads = frames.Select(f => f.CpuUsage).ToList();
        var gpuLoads = frames.Select(f => f.GpuUsage).ToList();
        var cpuReliable = cpuLoads.Max() > 2.0;
        var gpuReliable = gpuLoads.Max() > 2.0;

        return new BenchmarkResult
        {
            Path = path,
            TimeStamp = Text(root, "TimeStamp") ?? string.Empty,
            FpsAverage = Number(root, "FPSAvg"),
            FpsMinimum = Number(root, "FPSMin"),
            FpsMaximum = Number(root, "FPSMax"),
            Fps95 = Number(root, "FPS95"),
            FrameStats = ComputeStats(frames),
            Applied = new AppliedSettings(
                ScreenResolution: Text(root, "ScreenResolution") ?? "?",
                ScreenMode: Int(root, "ScreenMode"),
                QualityLevel: Int(root, "QualityLevel"),
                RenderScalePercent: (int)Math.Round(Number(root, "ImageQuality")),
                ViewDistance: NullableInt(root, "ViewDistance"),
                AntiAliasing: NullableInt(root, "AntiAliasing"),
                PostProcessing: NullableInt(root, "PostProcessing"),
                ShadowQuality: NullableInt(root, "ShadowQuality"),
                TextureQuality: NullableInt(root, "TextureQuality"),
                MaterialQuality: NullableInt(root, "MaterialQuality"),
                VegetationQuality: NullableInt(root, "VegetationQuality"),
                MotionBlur: NullableInt(root, "MotionBlur"),
                RayTracing: Int(root, "Rtx"),
                Upscaler: Int(root, "Dlss"),
                FrameGeneration: Int(root, "InsertFrame"),
                DirectX12: Int(root, "Dx12")),
            GameVersion = Text(root, "GameVer") ?? "?",
            SystemVersion = Text(root, "SysVer") ?? "?",
            CpuModel = Clean(Text(root, "CPUModel")) ?? "?",
            GpuModel = Clean(Text(root, "GPUModel")) ?? "?",
            GpuDriverVersion = Text(root, "GpuDriverVer") ?? "?",
            VideoMemory = Text(root, "VideoMemSize") ?? "?",
            SystemMemory = Text(root, "SysMem") ?? "?",
            ReportedCpuAverage = NullableNumber(root, "CPUAvg"),
            ReportedGpuAverage = NullableNumber(root, "GPUAvg"),
            CpuLoadReliable = cpuReliable,
            GpuLoadReliable = gpuReliable,
            CpuLoadNote = Note(cpuReliable),
            GpuLoadNote = Note(gpuReliable),
        };
    }

    public static FrameStats ComputeStats(IReadOnlyList<(double Fps, double CpuUsage, double GpuUsage)> frames)
    {
        var sorted = frames.Select(f => f.Fps).OrderBy(v => v).ToList();
        var average = sorted.Average();

        return new FrameStats(
            Frames: sorted.Count,
            AverageFps: average,
            MedianFps: Median(sorted),
            OnePercentLowFps: Percentile(sorted, 0.01),
            PointOnePercentLowFps: Percentile(sorted, 0.001),
            P95Fps: Percentile(sorted, 0.95),
            MinFps: sorted[0],
            MaxFps: sorted[^1],
            AverageFrameMs: average > 0 ? 1000.0 / average : 0.0);
    }

    public static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
            return 0;

        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    public static double Percentile(IReadOnlyList<double> sorted, double quantile)
    {
        if (sorted.Count == 0)
            return 0;

        var index = (int)(sorted.Count * quantile);
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static string Note(bool reliable) =>
        reliable ? string.Empty : "под Proton счётчики Windows не работают (значение постоянно 1%)";

    private static List<(double Fps, double CpuUsage, double GpuUsage)> ReadFrames(JsonElement root)
    {
        var frames = new List<(double, double, double)>();

        if (!root.TryGetProperty("Records", out var records) || records.ValueKind != JsonValueKind.Array)
            return frames;

        foreach (var record in records.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object)
                continue;

            frames.Add((
                NullableNumber(record, "FrameRate") ?? 0.0,
                NullableNumber(record, "CPUUsage") ?? 0.0,
                NullableNumber(record, "GPUUsage") ?? 0.0));
        }

        return frames;
    }

    private static double Number(JsonElement element, string name) =>
        NullableNumber(element, name)
        ?? throw new ResultParseException($"Файл результата: нет числового поля \"{name}\"");

    private static double? NullableNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static int Int(JsonElement element, string name) => (int)Math.Round(NullableNumber(element, name) ?? 0);

    private static int? NullableInt(JsonElement element, string name) =>
        NullableNumber(element, name) is { } number ? (int)Math.Round(number) : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Clean(string? value)
    {
        if (value is null)
            return null;

        var cleaned = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length == 0 ? null : cleaned;
    }
}

/// <summary>
/// Сверяет запрошенный профиль с тем, что бенчмарк реально применил: масштаб рендера приходит
/// в JSON процентом, а разрешение окна — строкой. Расхождение означает, что профиль не применился
/// (например, игра поправила масштаб под своё окно), и результат тогда не про задуманный режим.
/// </summary>
public static class AppliedCheck
{
    public static IReadOnlyList<string> Warnings(AppliedSettings applied, int requestedScalePercent)
    {
        var warnings = new List<string>();

        if (applied.RenderScalePercent != requestedScalePercent)
        {
            warnings.Add(
                $"бенчмарк применил масштаб рендера {applied.RenderScalePercent}% вместо запрошенных "
                + $"{requestedScalePercent}% - профиль применился не полностью");
        }

        var resolution = LinuxSystemInfoProvider.ParseResolution(applied.ScreenResolution);
        if (resolution is not { } parsed)
        {
            warnings.Add($"не удалось разобрать разрешение окна из отчёта: \"{applied.ScreenResolution}\"");
        }
        else if (parsed.Height % Math.Max(1, applied.RenderScalePercent) != 0
                 && GraphicsSettings.RenderHeightFor(parsed.Height, applied.RenderScalePercent) < parsed.Height)
        {
            warnings.Add(
                $"высота окна {parsed.Height} не делится на масштаб {applied.RenderScalePercent}%, "
                + "то есть рендер считался в неполном размере");
        }

        return warnings;
    }
}
