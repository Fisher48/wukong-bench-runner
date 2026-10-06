using WukongBenchRunner.Results;
using WukongBenchRunner.Running;

namespace WukongBenchRunner.Tests;

public sealed class ResultParserTests
{
    private static readonly string SamplePath = Path.Combine("TestData", "result_sample.json");

    [Fact]
    public void Парсер_Читает_Основные_Метрики_Из_Реального_Файла()
    {
        var result = ResultParser.ParseFile(SamplePath);

        Assert.Equal(24.0, result.FpsAverage);
        Assert.Equal(20.0, result.FpsMinimum);
        Assert.Equal(27.0, result.FpsMaximum);
        Assert.Equal(22.0, result.Fps95);
        Assert.Equal("1920 × 1200", result.Applied.ScreenResolution);
        Assert.Equal(60, result.Applied.RenderScalePercent);
        Assert.Equal(1, result.Applied.QualityLevel);
        Assert.Equal(0, result.Applied.RayTracing);
        Assert.Equal(1, result.Applied.FrameGeneration);
        Assert.Equal("1.0.3.14649", result.GameVersion);
        Assert.Equal("AMD Ryzen 5 5625U with Radeon Graphics", result.CpuModel);
        Assert.Equal("AMD Radeon Graphics (RADV RENOIR)", result.GpuModel);
        Assert.Equal("16GB", result.SystemMemory);
    }

    [Fact]
    public void Парсер_Считает_Статистику_По_Records()
    {
        var result = ResultParser.ParseFile(SamplePath);
        var stats = result.FrameStats;

        Assert.Equal(400, stats.Frames);
        Assert.Equal(25.9645, stats.AverageFps, 3);
        Assert.Equal(26.0109, stats.MedianFps, 3);
        Assert.Equal(24.8939, stats.OnePercentLowFps, 3);
        Assert.Equal(24.8609, stats.PointOnePercentLowFps, 3);
        Assert.Equal(26.9320, stats.P95Fps, 3);
        Assert.Equal(24.8609, stats.MinFps, 3);
        Assert.Equal(27.3169, stats.MaxFps, 3);
        Assert.Equal(38.5142, stats.AverageFrameMs, 3);
    }

    [Fact]
    public void Парсер_Помечает_Загрузку_Cpu_Gpu_Как_Ненадёжную_Под_Proton()
    {
        var result = ResultParser.ParseFile(SamplePath);

        Assert.False(result.CpuLoadReliable);
        Assert.False(result.GpuLoadReliable);
        Assert.Contains("Proton", result.CpuLoadNote);
        Assert.Equal(1.0, result.ReportedCpuAverage);
    }

    [Fact]
    public void Парсер_Ошибка_На_Неполном_Файле()
    {
        var root = Path.Combine(Path.GetTempPath(), "wbr-parse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var partial = Path.Combine(root, "1799999999");

        try
        {
            File.WriteAllText(partial, "{\"FPSAvg\": 30, \"Records\": [{\"FrameRate\": 30.0}");
            Assert.Throws<ResultParseException>(() => ResultParser.ParseFile(partial));

            File.WriteAllText(partial, "{\"FPSAvg\": 30, \"Records\": []}");
            Assert.Throws<ResultParseException>(() => ResultParser.ParseFile(partial));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Процентиль_Считается_По_Индексу_Отсортированного_Массива()
    {
        var values = Enumerable.Range(1, 100).Select(i => (double)i).ToList();

        Assert.Equal(51, ResultParser.Percentile(values, 0.5));
        Assert.Equal(2, ResultParser.Percentile(values, 0.01));
        Assert.Equal(96, ResultParser.Percentile(values, 0.95));
        Assert.Equal(100, ResultParser.Percentile(values, 1.0));
        Assert.Equal(50.5, ResultParser.Median(values));
    }

    [Fact]
    public void HistoryWatcher_Находит_Только_Новые_Файлы()
    {
        var root = Path.Combine(Path.GetTempPath(), "wbr-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var watcher = new HistoryWatcher(root);
            var known = watcher.Snapshot();
            Assert.Empty(known);
            Assert.Null(watcher.TryFindNew(known));

            var newFile = Path.Combine(root, "1799999999");
            File.Copy(SamplePath, newFile);

            var found = watcher.TryFindNew(known);
            Assert.NotNull(found);
            Assert.Equal(24.0, found!.FpsAverage);
            Assert.Null(watcher.TryFindNew(watcher.Snapshot()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
