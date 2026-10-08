using System.Globalization;
using WukongBenchRunner.Profiles;
using WukongBenchRunner.Reporting;
using WukongBenchRunner.Results;
using WukongBenchRunner.Running;
using WukongBenchRunner.SystemInfo;

namespace WukongBenchRunner;

public static class ConsoleReport
{
    public static void Write(RunReport report)
    {
        var cpu = report.Passes.FirstOrDefault(p => p.Kind == TestKind.Cpu);
        var gpu = report.Passes.FirstOrDefault(p => p.Kind == TestKind.Gpu);

        Line("Black Myth: Wukong Benchmark Tool - автоматический прогон");
        Line(report.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Line(new string('=', 78));

        Line("ХАРАКТЕРИСТИКИ КОМПЬЮТЕРА");
        foreach (var (key, value) in report.System.Rows)
            Line($"  {key,-22} {value}");

        Line();
        Line("РЕЗУЛЬТАТЫ");
        Line($"  {"Тест",-12} {"FPS ср.",9} {"FPS 95%",9} {"1% low",9} {"мин.",8} {"макс.",8} {"кадров",7} {"кадр, мс",9}");
        Line($"  {new string('-', 10)} {new string('-', 9)} {new string('-', 9)} {new string('-', 9)} {new string('-', 8)} {new string('-', 8)} {new string('-', 7)} {new string('-', 9)}");

        foreach (var pass in report.Passes)
        {
            var stats = pass.Result.FrameStats;
            Line($"  {pass.Title,-12} {Num(pass.Result.FpsAverage),9} {Num(pass.Result.Fps95),9} {Num(stats.OnePercentLowFps),9} " +
                 $"{Num(pass.Result.FpsMinimum),8} {Num(pass.Result.FpsMaximum),8} {stats.Frames,7} {Num(stats.AverageFrameMs),9}");
        }

        var withFrameTimes = report.Passes.Where(pass => pass.Result.FrameStats.MedianCpuFrameMs is not null).ToList();
        if (withFrameTimes.Count > 0)
        {
            Line();
            Line("ВРЕМЯ КАДРА ПО ДАННЫМ БЕНЧМАРКА (медиана)");
            Line($"  {"Тест",-12} {"CPU, мс",10} {"GPU, мс",10} {"кадр, мс",10}");

            foreach (var pass in withFrameTimes)
            {
                var stats = pass.Result.FrameStats;
                Line($"  {pass.Title,-12} {Num(stats.MedianCpuFrameMs ?? 0),10} {Num(stats.MedianGpuFrameMs ?? 0),10} {Num(stats.AverageFrameMs),10}");
            }

            Line();
            Line("  CPUFrameTime под Proton настоящий, а GPUFrameTime повторяет обратную величину FPS,");
            Line("  то есть это длина кадра, а не работа видеокарты. Определить узкое место по нему нельзя.");
        }

        if (cpu is not null && gpu is not null)
        {
            var ratio = (cpu.Result.FpsAverage / gpu.Result.FpsAverage).ToString("0.00", CultureInfo.InvariantCulture);
            var isMore = cpu.Result.FpsAverage >= gpu.Result.FpsAverage;
            var factor = isMore ? ratio : (gpu.Result.FpsAverage / cpu.Result.FpsAverage).ToString("0.00", CultureInfo.InvariantCulture);
            var relation = isMore
                ? $"CPU-тест даёт в {factor} раза больше FPS, чем GPU-тест"
                : $"CPU-тест даёт в {factor} раза меньше FPS, чем GPU-тест";

            Line($"  {relation} - профили действительно различаются по тому, что ограничивает кадр.");
        }

        foreach (var pass in report.Passes)
        {
            if (pass.Load is { Available: true } load)
            {
                Line($"  Загрузка во время {pass.Title}: " +
                     $"CPU {Fmt(load.AverageCpuBusyPercent)} (пик {Fmt(load.PeakCpuBusyPercent)}), " +
                     $"GPU {Fmt(load.AverageGpuBusyPercent)} (пик {Fmt(load.PeakGpuBusyPercent)}); " +
                     $"окно измерения 60 с: CPU {Fmt(load.MeasureWindowCpuBusyPercent)}, GPU {Fmt(load.MeasureWindowGpuBusyPercent)}");
            }

            var applied = pass.Result.Applied;
            Line();
            Line($"ПРИМЕНЁННЫЕ НАСТРОЙКИ: {pass.Title.ToUpperInvariant()}");
            foreach (var (parameter, value) in pass.Settings)
                Line($"  {parameter,-30} {value}");

            Line($"  фактически применено бенчмарком: разрешение {applied.ScreenResolution}, " +
                 $"масштаб рендера {applied.RenderScalePercent}%, качество {applied.QualityLevel}, " +
                 $"трассировка {(applied.RayTracing == 0 ? "выкл." : "вкл.")}, " +
                 $"апскейлер {(applied.Upscaler == 0 ? "выкл." : "вкл.")}, " +
                 $"генерация кадров {(applied.FrameGeneration == 0 ? "выкл." : "вкл.")}");
        }

        Line();
        Line("ПОЧЕМУ ТАКИЕ НАСТРОЙКИ");
        foreach (var pass in report.Passes)
        {
            Line($"  {pass.Title}:");
            foreach (var reason in pass.Rationale)
                Wrap(reason, "    - ");
        }

        Line();
        Line("ПРИМЕЧАНИЯ");
        Wrap("CPUAvg/GPUAvg из JSON под Proton всегда 1% (Performance Counters Windows недоступны из wine), " +
             "поэтому загрузка CPU/GPU не выводится.", "  - ");
        Wrap("CPUFrameTime/GPUFrameTime под Proton не согласованы с FPS, поэтому используются только FPS* и статистика по Records.", "  - ");
        Wrap($"Каталог результатов: {report.Installation.HistoryDir}", "  - ");
        Wrap($"Proton: {report.Installation.ProtonVersion}; эмуляция ввода: {report.Installation.InputBackend}", "  - ");

        Line();
        Line("Отчёты сохранены в results/<дата>/: report.md, report.json, *_raw.json");
    }

    public static void WriteResult(BenchmarkResult result, string title)
    {
        Line($"{title}: FPS ср. {Num(result.FpsAverage)}, 95% {Num(result.Fps95)}, мин. {Num(result.FpsMinimum)}, " +
             $"кадров {result.FrameStats.Frames}, разрешение {result.Applied.ScreenResolution}, " +
             $"масштаб рендера {result.Applied.RenderScalePercent}%");
    }

    private static void Line(string text) => Console.WriteLine(text);

    private static void Line() => Console.WriteLine();

    private static void Wrap(string text, string indent)
    {
        const int width = 96;
        var continuation = indent.Replace("- ", "  ", StringComparison.Ordinal);
        var words = text.Split(' ');
        var current = new System.Text.StringBuilder(indent);

        foreach (var word in words)
        {
            if (current.Length > indent.Length && current.Length + word.Length + 1 > width)
            {
                Console.WriteLine(current.ToString());
                current.Clear().Append(continuation);
            }

            if (current.Length > continuation.Length)
                current.Append(' ');

            current.Append(word);
        }

        Console.WriteLine(current.ToString());
    }

    private static string Num(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Fmt(double? value) => value is { } v ? $"{v:0}%" : "н/д";
}
