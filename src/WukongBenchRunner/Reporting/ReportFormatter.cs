using System.Globalization;
using System.Text;
using WukongBenchRunner.Profiles;
using WukongBenchRunner.Results;
using WukongBenchRunner.SystemInfo;

namespace WukongBenchRunner.Reporting;

public sealed record RunReport(
    DateTime StartedAt,
    SystemSnapshot System,
    BenchmarkInstallationInfo Installation,
    IReadOnlyList<PassReport> Passes);

public sealed record BenchmarkInstallationInfo(
    string SteamRoot,
    string InstallDir,
    string ConfigPath,
    string HistoryDir,
    string ProtonVersion,
    string InputBackend);

public sealed record PassReport(
    TestKind Kind,
    string Title,
    BenchmarkResult Result,
    IReadOnlyList<(string Parameter, string Value)> Settings,
    IReadOnlyList<string> Rationale,
    string RawCopyPath,
    LoadSample? Load = null);

public static class ReportFormatter
{
    public static string Markdown(RunReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Black Myth: Wukong Benchmark Tool - автоматический прогон");
        builder.AppendLine();
        builder.AppendLine($"Дата: {report.StartedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine($"Система: {report.Installation.ProtonVersion} (Proton), ввод: {report.Installation.InputBackend}");
        builder.AppendLine();

        AppendSystem(builder, report.System);
        AppendResults(builder, report.Passes);

        foreach (var pass in report.Passes)
        {
            AppendPass(builder, pass);
        }

        AppendNotes(builder);
        return builder.ToString();
    }

    private static void AppendSystem(StringBuilder builder, SystemSnapshot system)
    {
        builder.AppendLine("## Характеристики компьютера");
        builder.AppendLine();
        builder.AppendLine("| Параметр | Значение |");
        builder.AppendLine("| --- | --- |");

        foreach (var (key, value) in system.Rows)
            builder.AppendLine($"| {key} | {Cell(value)} |");

        builder.AppendLine();
    }

    private static void AppendResults(StringBuilder builder, IReadOnlyList<PassReport> passes)
    {
        builder.AppendLine("## Результаты");
        builder.AppendLine();
        builder.AppendLine("| Тест | FPS (средний) | FPS 95% | FPS 1% low | FPS мин. | FPS макс. | Кадров | Ср. кадр, мс |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (var pass in passes)
        {
            var stats = pass.Result.FrameStats;
            builder.AppendLine(
                $"| {pass.Title} | {Num(pass.Result.FpsAverage)} | {Num(pass.Result.Fps95)} | {Num(stats.OnePercentLowFps)} " +
                $"| {Num(pass.Result.FpsMinimum)} | {Num(pass.Result.FpsMaximum)} | {stats.Frames} | {Num(stats.AverageFrameMs)} |");
        }

        builder.AppendLine();
        builder.AppendLine("Дополнительно из JSON бенчмарка:");
        builder.AppendLine();
        builder.AppendLine("| Тест | Разрешение | Масштаб рендера, % | Качество | Трассировка | Апскейлер | Генерация кадров | Версия игры |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (var pass in passes)
        {
            var applied = pass.Result.Applied;
            builder.AppendLine(
                $"| {pass.Title} | {applied.ScreenResolution} | {applied.RenderScalePercent} | {applied.QualityLevel} " +
                $"| {OnOff(applied.RayTracing)} | {OnOff(applied.Upscaler)} | {OnOff(applied.FrameGeneration)} | {pass.Result.GameVersion} |");
        }

        builder.AppendLine();
        if (passes.Any(p => p.Load is { Available: true }))
        {
            builder.AppendLine();
            builder.AppendLine("Загрузка CPU/GPU, измеренная инструментом во время прогона (Linux, /proc/stat и gpu_busy_percent):");
            builder.AppendLine();
            builder.AppendLine("| Тест | CPU средняя | CPU пик | GPU средняя | GPU пик | CPU окно 60 с | GPU окно 60 с |");
            builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

            foreach (var pass in passes)
            {
                var load = pass.Load;
                builder.AppendLine(
                    $"| {pass.Title} | {Pct(load?.AverageCpuBusyPercent)} | {Pct(load?.PeakCpuBusyPercent)} " +
                    $"| {Pct(load?.AverageGpuBusyPercent)} | {Pct(load?.PeakGpuBusyPercent)} " +
                    $"| {Pct(load?.MeasureWindowCpuBusyPercent)} | {Pct(load?.MeasureWindowGpuBusyPercent)} |");
            }

            builder.AppendLine();
            builder.AppendLine("«Окно 60 с» — самое нагруженное минутное окно прогона, то есть фаза самого измерения.");
            builder.AppendLine();
            builder.AppendLine("Важно: `gpu_busy_percent` у AMD показывает занятость любого движка GPU, а не его загрузку,");
            builder.AppendLine("поэтому 90+% в CPU-тесте не означает, что видеокарда ограничивает кадр. Проверка выполнена");
            builder.AppendLine("контрольным прогоном: при падении масштаба рендера с 50% до 25% (в 4 раза меньше пикселей)");
            builder.AppendLine("FPS не изменился — значит кадр ограничен процессором, а не видеокартой.");
        }

        builder.AppendLine();
        builder.AppendLine("Данные о железе по версии самого бенчмарка:");
        builder.AppendLine();
        builder.AppendLine("| Тест | CPU | GPU | Драйвер | RAM | VRAM |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");

        foreach (var pass in passes)
        {
            var result = pass.Result;
            builder.AppendLine(
                $"| {pass.Title} | {Cell(result.CpuModel)} | {Cell(result.GpuModel)} | {Cell(result.GpuDriverVersion)} " +
                $"| {result.SystemMemory} | {result.VideoMemory} |");
        }

        builder.AppendLine();
    }

    private static void AppendPass(StringBuilder builder, PassReport pass)
    {
        builder.AppendLine($"## {pass.Title}: применённые настройки");
        builder.AppendLine();
        builder.AppendLine("| Параметр | Значение |");
        builder.AppendLine("| --- | --- |");

        foreach (var (parameter, value) in pass.Settings)
            builder.AppendLine($"| {parameter} | {Cell(value)} |");

        builder.AppendLine();
        builder.AppendLine("Почему именно так:");
        builder.AppendLine();

        foreach (var reason in pass.Rationale)
            builder.AppendLine($"- {reason}");

        builder.AppendLine();
        builder.AppendLine($"Сырой файл результата: `{pass.RawCopyPath}`");
        builder.AppendLine();
    }

    private static void AppendNotes(StringBuilder builder)
    {
        builder.AppendLine("## Примечания");
        builder.AppendLine();
        builder.AppendLine("- CPUAvg/GPUAvg из JSON бенчмарка под Proton всегда равны 1%: счётчики Windows " +
            "Performance Counters недоступны из wine, поэтому загрузка CPU/GPU не приводится. Ориентир - FPS и времена кадров.");
        builder.AppendLine("- CPUFrameTime/GPUFrameTime под Proton также искажены (значения не сходятся с FPS), " +
            "поэтому в выводе используются только поля FPS* и пересчитанная статистика по массиву Records.");
        builder.AppendLine("- Инструмент после получения результата принудительно закрывает процесс бенчмарка, " +
            "чтобы игра не переписала GameUserSettings.ini; исходный конфиг восстанавливается из резервной копии.");
    }

    private static string OnOff(int value) => value == 0 ? "выкл." : "вкл.";

    private static string Cell(string? value) => (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Pct(double? value) => value is { } v ? $"{v.ToString("0", CultureInfo.InvariantCulture)}%" : "н/д";

    private static string Num(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    public static void WriteToDirectory(RunReport report, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "report.md"), Markdown(report), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "report.json"), Json(report), new UTF8Encoding(false));
    }

    private static string Json(RunReport report)
    {
        var passes = report.Passes.Select(pass => new
        {
            kind = pass.Kind.ToString().ToLowerInvariant(),
            title = pass.Title,
            settings = pass.Settings.ToDictionary(s => s.Parameter, s => s.Value),
            rationale = pass.Rationale,
            metrics = new
            {
                fpsAverage = pass.Result.FpsAverage,
                fps95 = pass.Result.Fps95,
                fpsMinimum = pass.Result.FpsMinimum,
                fpsMaximum = pass.Result.FpsMaximum,
                frames = pass.Result.FrameStats.Frames,
                framesMinFps = pass.Result.FrameStats.MinFps,
                framesMaxFps = pass.Result.FrameStats.MaxFps,
                framesP95Fps = pass.Result.FrameStats.P95Fps,
                medianFps = pass.Result.FrameStats.MedianFps,
                onePercentLowFps = pass.Result.FrameStats.OnePercentLowFps,
                pointOnePercentLowFps = pass.Result.FrameStats.PointOnePercentLowFps,
                averageFrameMs = pass.Result.FrameStats.AverageFrameMs,
                reportedCpuAverage = pass.Result.ReportedCpuAverage,
                reportedGpuAverage = pass.Result.ReportedGpuAverage,
                cpuLoadReliable = pass.Result.CpuLoadReliable,
                gpuLoadReliable = pass.Result.GpuLoadReliable,
            },
            applied = new
            {
                screenResolution = pass.Result.Applied.ScreenResolution,
                renderScalePercent = pass.Result.Applied.RenderScalePercent,
                qualityLevel = pass.Result.Applied.QualityLevel,
                rayTracing = pass.Result.Applied.RayTracing,
                upscaler = pass.Result.Applied.Upscaler,
                frameGeneration = pass.Result.Applied.FrameGeneration,
            },
            reportedHardware = new
            {
                cpu = pass.Result.CpuModel,
                gpu = pass.Result.GpuModel,
                gpuDriver = pass.Result.GpuDriverVersion,
                systemMemory = pass.Result.SystemMemory,
                videoMemory = pass.Result.VideoMemory,
                gameVersion = pass.Result.GameVersion,
                systemVersion = pass.Result.SystemVersion,
            },
            measuredLoad = pass.Load is null
                ? null
                : new
                {
                    cpuAveragePercent = pass.Load.AverageCpuBusyPercent,
                    cpuPeakPercent = pass.Load.PeakCpuBusyPercent,
                    gpuAveragePercent = pass.Load.AverageGpuBusyPercent,
                    gpuPeakPercent = pass.Load.PeakGpuBusyPercent,
                    cpuLastMinutePercent = pass.Load.LastMinuteCpuBusyPercent,
                    gpuLastMinutePercent = pass.Load.LastMinuteGpuBusyPercent,
                    cpuMeasureWindowPercent = pass.Load.MeasureWindowCpuBusyPercent,
                    gpuMeasureWindowPercent = pass.Load.MeasureWindowGpuBusyPercent,
                    windowSeconds = pass.Load.WindowSeconds,
                    samples = pass.Load.Samples,
                },
            rawResultFile = pass.RawCopyPath,
        }).ToList();

        var payload = new
        {
            generatedAt = report.StartedAt,
            system = report.System.Rows.ToDictionary(r => r.Key, r => r.Value),
            environment = new
            {
                steamRoot = report.Installation.SteamRoot,
                installDir = report.Installation.InstallDir,
                configPath = report.Installation.ConfigPath,
                historyDir = report.Installation.HistoryDir,
                protonVersion = report.Installation.ProtonVersion,
                inputBackend = report.Installation.InputBackend,
            },
            passes,
        };

        return System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        });
    }
}
