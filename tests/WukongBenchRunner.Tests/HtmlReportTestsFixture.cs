using WukongBenchRunner.Profiles;
using WukongBenchRunner.Reporting;
using WukongBenchRunner.Results;
using WukongBenchRunner.SystemInfo;

namespace WukongBenchRunner.Tests;

/// <summary>
/// Общая сборка отчёта для тестов вывода: беру настоящие файлы результатов, чтобы числа
/// в тестах совпадали с числами в примерах.
/// </summary>
internal static class HtmlReportTestsFixture
{
    private static readonly string Samples = "samples";

    public static RunReport Report()
    {
        var cpu = ResultParser.ParseFile(Path.Combine(Samples, "cpu_raw.json"));
        var gpu = ResultParser.ParseFile(Path.Combine(Samples, "gpu_raw.json"));

        var cpuProfile = ProfileCatalog.Cpu(screenHeight: 1200);
        var gpuProfile = ProfileCatalog.Gpu(screenHeight: 1200);

        var passes = new[]
        {
            new PassReport(
                TestKind.Cpu,
                cpuProfile.Title,
                cpu,
                cpuProfile.Summary,
                cpuProfile.Rationale,
                Path.Combine(Samples, "cpu_raw.json"),
                Load("cpu_load.json")),
            new PassReport(
                TestKind.Gpu,
                gpuProfile.Title,
                gpu,
                gpuProfile.Summary,
                gpuProfile.Rationale,
                Path.Combine(Samples, "gpu_raw.json"),
                Load("gpu_load.json")),
        };

        var system = LinuxSystemInfoProvider.Collect(Path.Combine("TestData", "не существует"), "11.0-100");

        return new RunReport(
            DateTime.Now,
            system,
            new BenchmarkInstallationInfo("не определён", "не определён", "не определён", Samples, "11.0-100", "XTEST"),
            passes);
    }

    /// <summary>
    /// Подставляет значение в строку характеристик. Именно модель процессора берётся из
    /// системной команды, поэтому именно сюда может попасть строка со спецсимволами.
    /// </summary>
    public static RunReport WithCpuModel(RunReport report, string value) =>
        report with { System = report.System with { CpuModel = value } };

    public static RunReport WithoutFrameTimes(RunReport report) =>
        report with
        {
            Passes =
            [
                report.Passes[0] with
                {
                    Result = report.Passes[0].Result with
                    {
                        FrameStats = report.Passes[0].Result.FrameStats with { FrameTimes = null },
                    },
                },
                report.Passes[1],
            ],
        };

    private static LoadSample? Load(string name)
    {
        var path = Path.Combine(Samples, name);
        return File.Exists(path)
            ? System.Text.Json.JsonSerializer.Deserialize<LoadSample>(File.ReadAllText(path))
            : null;
    }
}