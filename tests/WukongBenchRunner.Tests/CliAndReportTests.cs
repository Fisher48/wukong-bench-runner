using WukongBenchRunner.Cli;
using WukongBenchRunner.Profiles;
using WukongBenchRunner.Reporting;
using WukongBenchRunner.Results;
using WukongBenchRunner.Running;
using WukongBenchRunner.SystemInfo;

namespace WukongBenchRunner.Tests;

public sealed class CliAndReportTests
{
    [Fact]
    public void Cli_Разбирает_Число_Повторов()
    {
        Assert.Equal(1, CommandLineOptions.Parse(["run"]).RepeatCount);
        Assert.Equal(3, CommandLineOptions.Parse(["run", "--repeat", "3"]).RepeatCount);

        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["run", "--repeat", "0"]));
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["run", "--repeat", "11"]));
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["run", "--repeat", "abc"]));
    }

    [Fact]
    public void Cli_Разбирает_Аргументы()
    {
        var options = CommandLineOptions.Parse(
        [
            "run",
            "--only",
            "gpu",
            "--timeout",
            "25",
            "--gpu-raytracing",
            "--layout-start-x",
            "0.11",
            "--layout-confirm-y",
            "0.6",
            "--install-dir",
            "/tmp/tool",
        ]);

        Assert.Equal("run", options.Command);
        Assert.Equal("gpu", options.OnlyKind);
        Assert.Equal(25, options.TimeoutMinutes);
        Assert.True(options.GpuRayTracing);
        Assert.Equal("/tmp/tool", options.InstallDir);
        Assert.Equal(0.11, options.Layout.StartButtonX);
        Assert.Equal(MenuLayout.Default.StartButtonY, options.Layout.StartButtonY);
        Assert.Equal(MenuLayout.Default.ConfirmButtonX, options.Layout.ConfirmButtonX);
        Assert.Equal(0.6, options.Layout.ConfirmButtonY);
    }

    [Fact]
    public void Cli_Значения_По_Умолчанию()
    {
        var options = CommandLineOptions.Parse([]);

        Assert.Equal("help", options.Command);
        Assert.Equal(15, options.TimeoutMinutes);
        Assert.Null(options.OnlyKind);
        Assert.False(options.GpuRayTracing);
        Assert.Equal(MenuLayout.Default, options.Layout);
        Assert.Equal(0.115, options.Layout.StartButtonX);
        Assert.Equal(0.580, options.Layout.ConfirmButtonY);
    }

    [Fact]
    public void Cli_Ошибка_Без_Значения_Параметра()
    {
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["run", "--only"]));
    }

    [Fact]
    public void Навигация_Меняет_Шаги_По_Кругу()
    {
        var navigator = new MenuNavigator(new NoopInput(), MenuLayout.Default);

        Assert.Equal(MenuStep.SkipIntro, navigator.StepFor(0));
        Assert.Equal(MenuStep.OpenBenchmark, navigator.StepFor(1));
        Assert.Equal(MenuStep.Confirm, navigator.StepFor(2));
        Assert.Equal(MenuStep.SkipIntro, navigator.StepFor(3));
    }

    [Fact]
    public void Отчёт_Показывает_Серию_Прогонов_С_Медианой_И_Разбросом()
    {
        var result = ResultParser.ParseFile(Path.Combine("TestData", "result_sample.json"));
        var system = LinuxSystemInfoProvider.Collect(Path.Combine("TestData", "не существует"), "11.0-100");
        var runs = new[] { result, result, result };

        var report = new RunReport(
            DateTime.Now,
            system,
            new BenchmarkInstallationInfo("/steam", "/install", "/config.ini", "/history", "11.0-100", "XTEST"),
            [
                new PassReport(TestKind.Cpu, "CPU-тест", result, ProfileCatalog.Cpu().Summary, ProfileCatalog.Cpu().Rationale, "cpu_raw.json", null, runs),
            ]);

        var markdown = ReportFormatter.Markdown(report);

        Assert.Contains("Серия прогонов", markdown);
        Assert.Contains("Медиана", markdown);
        Assert.Contains("Разброс", markdown);
    }

    [Fact]
    public void Один_Прогон_Не_Печатает_Серию()
    {
        var result = ResultParser.ParseFile(Path.Combine("TestData", "result_sample.json"));
        var system = LinuxSystemInfoProvider.Collect(Path.Combine("TestData", "не существует"), "11.0-100");

        var report = new RunReport(
            DateTime.Now,
            system,
            new BenchmarkInstallationInfo("/steam", "/install", "/config.ini", "/history", "11.0-100", "XTEST"),
            [
                new PassReport(TestKind.Cpu, "CPU-тест", result, ProfileCatalog.Cpu().Summary, ProfileCatalog.Cpu().Rationale, "cpu_raw.json"),
            ]);

        Assert.DoesNotContain("Серия прогонов", ReportFormatter.Markdown(report));
    }

    [Fact]
    public void Отчёт_Содержит_Обе_Проверки_И_Настройки()
    {
        var result = ResultParser.ParseFile(Path.Combine("TestData", "result_sample.json"));
        var system = LinuxSystemInfoProvider.Collect(Path.Combine("TestData", "не существует"), "11.0-100");

        var report = new RunReport(
            DateTime.Now,
            system,
            new BenchmarkInstallationInfo("/steam", "/install", "/config.ini", "/history", "11.0-100", "XTEST"),
            [
                new PassReport(TestKind.Cpu, "CPU-тест", result, ProfileCatalog.Cpu().Summary, ProfileCatalog.Cpu().Rationale, "cpu_raw.json"),
                new PassReport(TestKind.Gpu, "GPU-тест", result, ProfileCatalog.Gpu().Summary, ProfileCatalog.Gpu().Rationale, "gpu_raw.json"),
            ]);

        var markdown = ReportFormatter.Markdown(report);

        Assert.Contains("## Характеристики компьютера", markdown);
        Assert.Contains("## Результаты", markdown);
        Assert.Contains("CPU-тест", markdown);
        Assert.Contains("GPU-тест", markdown);
        Assert.Contains("Масштаб рендера", markdown);
        Assert.Contains("Почему именно так:", markdown);
        Assert.Contains("## Примечания", markdown);
    }

    [Fact]
    public void Форматирование_Байт_Человекочитаемое()
    {
        Assert.Equal("16 ГБ", LinuxSystemInfoProvider.FormatBytes(16L * 1024 * 1024 * 1024));
        Assert.Equal("512 МБ", LinuxSystemInfoProvider.FormatBytes(512L * 1024 * 1024));
        Assert.Equal("неизвестно", LinuxSystemInfoProvider.FormatBytes(0));
    }

    private sealed class NoopInput : IInputBackend
    {
        public string Name => "noop";
        public bool Activate(WindowInfo window) => true;
        public bool ClickRelative(WindowInfo window, double relativeX, double relativeY) => true;
        public bool PressReturn() => true;
    }

    [Fact]
    public void Lspci_Разбирает_Видео_Устройство_И_Драйвер()
    {
        var lspci = File.ReadAllText(Path.Combine("TestData", "lspci.txt"));

        var devices = LinuxSystemInfoProvider.ParseDisplayDevices(lspci);
        Assert.Single(devices);
        Assert.Contains("Barcelo", devices[0]);
        Assert.Equal("Advanced Micro Devices, Inc. Barcelo", LinuxSystemInfoProvider.CleanDeviceName(devices[0]));

        var drivers = LinuxSystemInfoProvider.ParseDisplayDrivers(lspci);
        Assert.Contains("amdgpu", drivers);
        Assert.DoesNotContain("pcieport", drivers);
    }

    [Fact]
    public void Lspci_Без_Видео_Устройств()
    {
        Assert.Empty(LinuxSystemInfoProvider.ParseDisplayDevices("00:01.0 PCI bridge [0604]: AMD [1022:1234]\n\tKernel driver in use: pcieport\n"));
    }

    [Fact]
    public void Окно_Парсится_Из_Дерева_Xwininfo()
    {
        var tree = File.ReadAllText(Path.Combine("TestData", "xwininfo-tree.txt"));
        var windows = X11WindowFinder.ParseTree(tree);

        var game = windows.First(w => w.Id == 0x4c00003);
        Assert.Equal(1920, game.Width);
        Assert.Equal(1200, game.Height);
        Assert.Equal("b1", game.Name.Trim());

        Assert.Contains(windows, w => w.Id == 0x4c00007 && w.Name.Length == 0);
        Assert.All(windows, w => Assert.True(w.Width > 0 && w.Height > 0));
    }

    [Fact]
    public void Окно_Ищется_По_Имени_Когда_Pid_Неизвестен()
    {
        var tree = File.ReadAllText(Path.Combine("TestData", "xwininfo-tree.txt"));
        var windows = X11WindowFinder.ParseTree(tree);

        var game = windows.FirstOrDefault(w => w.Name.Trim() == "b1");
        Assert.NotNull(game);
        Assert.Equal(1920, game!.Width);
    }

    [Fact]
    public void Загрузка_Cpu_Считается_По_Двум_Снимкам_ProcStat()
    {
        var first = LoadSampler.ParseCpuTimes("cpu  100 0 100 700 100 0 0 0");
        var second = LoadSampler.ParseCpuTimes("cpu  200 0 200 800 200 0 0 0");

        Assert.Equal(1000, first!.Total);
        Assert.Equal(800, first.Idle);

        var busy = LoadSampler.CpuBusyPercent(first, second);
        Assert.NotNull(busy);
        Assert.Equal(50.0, busy!.Value, 3);
    }

    [Fact]
    public void Загрузка_Cpu_Не_Считается_При_Откате_Счётчиков()
    {
        var first = LoadSampler.ParseCpuTimes("cpu  100 0 100 700 100 0 0 0");
        var same = LoadSampler.ParseCpuTimes("cpu  100 0 100 700 100 0 0 0");

        Assert.Null(LoadSampler.CpuBusyPercent(first, same));
    }

    [Fact]
    public void Консольный_Отчёт_Не_Рвёт_Буллеты_Посреди_Предложения()
    {
        var result = ResultParser.ParseFile(Path.Combine("TestData", "result_sample.json"));
        var system = LinuxSystemInfoProvider.Collect(Path.Combine("TestData", "не существует"), "11.0-100");

        var longReason = "обоснование достаточно длинное, чтобы перенестись на следующую строку " +
                         "и при этом не превратиться в отдельный буллет с новым маркером";

        var report = new RunReport(
            DateTime.Now,
            system,
            new BenchmarkInstallationInfo("/steam", "/install", "/config.ini", "/history", "11.0-100", "XTEST"),
            [
                new PassReport(TestKind.Cpu, "CPU-тест", result, ProfileCatalog.Cpu().Summary, [longReason], "cpu_raw.json"),
            ]);

        var output = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(output);

        try
        {
            ConsoleReport.Write(report);
        }
        finally
        {
            Console.SetOut(previous);
        }

        var lines = output.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var bulletIndex = lines.FindIndex(l => l.StartsWith("    - "));

        Assert.True(bulletIndex >= 0);
        Assert.StartsWith("    - ", lines[bulletIndex]);
        Assert.StartsWith("      ", lines[bulletIndex + 1]);
        Assert.DoesNotContain("- ", lines[bulletIndex + 1]);
        Assert.Contains(longReason[..30], string.Join(" ", lines.Skip(bulletIndex).Take(3)));
    }
}
