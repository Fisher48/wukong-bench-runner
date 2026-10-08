using System.Globalization;
using WukongBenchRunner.Cli;
using WukongBenchRunner.Config;
using WukongBenchRunner.Discovery;
using WukongBenchRunner.Profiles;
using WukongBenchRunner.Reporting;
using WukongBenchRunner.Results;
using WukongBenchRunner.Running;
using WukongBenchRunner.SystemInfo;
using System.Text.Json;

namespace WukongBenchRunner;

public static class Program
{
    private static string DateTimeStamp() =>
        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string[] BenchmarkProcesses = ["b1-Win64-Shipping.exe", "b1_benchmark.exe"];

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        CommandLineOptions options;
        try
        {
            options = CommandLineOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("Ошибка разбора аргументов: " + ex.Message);
            CommandLineOptions.WriteHelp();
            return 2;
        }

        try
        {
            return options.Command switch
            {
                "help" or "--help" or "-h" => Help(),
                "inspect" => Inspect(options),
                "probe" => Probe(options),
                "run" => await RunAsync(options),
                "report" => Report(options),
                _ => Unknown(options.Command),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Прервано пользователем. Исходный GameUserSettings.ini восстановлен.");
            return 130;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or BenchmarkRunException
            or ResultParseException
            or ArgumentException
            or FileNotFoundException
            or TimeoutException
            or IOException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Непредвиденная ошибка: " + ex.GetType().Name + ": " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int Help()
    {
        CommandLineOptions.WriteHelp();
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Неизвестная команда: {command}");
        CommandLineOptions.WriteHelp();
        return 2;
    }

    private static int Inspect(CommandLineOptions options)
    {
        var installation = SteamLocator.Locate(options.ToInstallationOptions());
        var system = LinuxSystemInfoProvider.Collect(installation.InstallDir, installation.ProtonVersion);
        var graphics = GraphicsDiagnostics.Probe();

        Console.WriteLine("Black Myth: Wukong Benchmark Tool - проверка окружения");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"Steam root        : {installation.SteamRoot}");
        Console.WriteLine($"Библиотека Steam  : {installation.LibraryRoot}");
        Console.WriteLine($"Каталог игры      : {installation.InstallDir}  [{(Directory.Exists(installation.InstallDir) ? "есть" : "нет")}]");
        Console.WriteLine($"Конфиг            : {installation.ConfigPath}  [{(installation.ConfigExists ? "есть" : "нет")}]");
        Console.WriteLine($"Proton-префикс    : {installation.PrefixDir}  [{(installation.PrefixExists ? "есть" : "нет")}]");
        Console.WriteLine($"Версия Proton     : {installation.ProtonVersion ?? "неизвестно"}");
        Console.WriteLine($"Запускающий файл  : {installation.LauncherExe}");
        Console.WriteLine($"Исполняемый файл  : {installation.ShippingExe}");
        Console.WriteLine();
        Console.WriteLine("Каталоги с результатами (первый существующий и используемый):");

        foreach (var candidate in installation.HistoryCandidates)
        {
            var mark = string.Equals(candidate, installation.HistoryDir, StringComparison.Ordinal) ? "=>" : "  ";
            var count = Directory.Exists(candidate) ? Directory.GetFiles(candidate).Length.ToString() : "-";
            Console.WriteLine($" {mark} {candidate}  [файлов: {count}]");
        }

        Console.WriteLine();
        Console.WriteLine("Готовность:");
        Console.WriteLine($" Steam запущен            : {SteamRunning()}");
        Console.WriteLine($" DISPLAY                  : {Environment.GetEnvironmentVariable("DISPLAY") ?? "(нет)"}");
        Console.WriteLine($" XTEST доступен           : {XTestInput.IsAvailable}");
        Console.WriteLine($" xprop/xwininfo           : {X11WindowFinder.ToolsAvailable}");
        Console.WriteLine($" xdotool                  : {Shell.HasExecutable("xdotool")}");
        Console.WriteLine($" Бенчмарк не запущен      : {Shell.FindProcessesByExecutable(BenchmarkProcesses).Count == 0}");
        Console.WriteLine($" Координаты меню          : старт ({options.Layout.StartButtonX}, {options.Layout.StartButtonY}), " +
                          $"подтверждение ({options.Layout.ConfirmButtonX}, {options.Layout.ConfirmButtonY})");

        Console.WriteLine();
        Console.WriteLine("Графика:");
        Console.WriteLine($"  {graphics.Summary}");

        if (graphics.Warning is { } warning)
        {
            Console.WriteLine();
            Console.WriteLine(warning);
        }

        Console.WriteLine();
        Console.WriteLine("Характеристики компьютера:");
        foreach (var (key, value) in system.Rows)
            Console.WriteLine($"  {key,-22} {value}");

        if (!installation.ConfigExists)
        {
            Console.WriteLine();
            Console.WriteLine("ВНИМАНИЕ: конфиг не найден. Запустите Benchmark Tool вручную один раз и закройте его.");
        }

        return 0;
    }

    private static int Probe(CommandLineOptions options)
    {
        X11WindowFinder.EnsureTools();

        var installation = SteamLocator.Locate(options.ToInstallationOptions());
        var input = InputBackendFactory.Create();

        Console.WriteLine($"Ввод: {input.Name}");

        var running = Shell.FindProcessesByExecutable(BenchmarkProcesses);
        if (running.Count > 0)
        {
            Console.WriteLine($"Бенчмарк уже запущен (pid={running[0].Pid}), подключаюсь к нему.");
        }
        else
        {
            Console.WriteLine("Запускаю бенчмарк через Steam, окно не трогайте.");
            Shell.Run("steam", ["-applaunch", InstallationOptions.BenchmarkAppId.ToString()]);
        }

        var process = running.Count > 0 ? running[0] : WaitForProcess(TimeSpan.FromMinutes(3));
        Console.WriteLine($"Процесс: pid={process.Pid}");

        WindowInfo? window = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < TimeSpan.FromMinutes(3) && window is null)
        {
            var pids = Shell.FindProcessesByExecutable(BenchmarkProcesses).Select(p => p.Pid).ToHashSet();
            window = X11WindowFinder.FindGameWindow(pids, ["b1", "wukong", "black myth", "steam_app_3132990"]);
            if (window is null)
                Thread.Sleep(2000);
        }

        if (window is null)
        {
            Console.Error.WriteLine("Окно не найдено. Список видимых окон:");
            foreach (var candidate in X11WindowFinder.ListWindows().Where(w => w.IsViewable && w.Width > 200))
                Console.WriteLine($"  0x{candidate.Id:x} pid={candidate.Pid} {candidate.Width}x{candidate.Height} \"{candidate.Name}\"");
            return 1;
        }

        Console.WriteLine($"Окно: 0x{window.Id:x} \"{window.Name}\" pid={window.Pid} " +
                          $"{window.Width}x{window.Height} +{window.AbsoluteX}+{window.AbsoluteY}");

        input.Activate(window);
        Thread.Sleep(500);

        if (options.ProbePressReturn)
        {
            Console.WriteLine("Нажимаю Enter.");
            input.PressReturn();
        }
        else
        {
            var testX = options.ProbeClickX;
            var testY = options.ProbeClickY;
            var pointX = window.AbsoluteX + (int)(window.Width * testX);
            var pointY = window.AbsoluteY + (int)(window.Height * testY);
            Console.WriteLine($"Кликаю в ({testX:0.###}, {testY:0.###}) -> экранные координаты ({pointX}, {pointY}).");
            input.ClickRelative(window, testX, testY);
        }

        if (options.KeepRunning)
        {
            Console.WriteLine("--keep: бенчмарк оставлен запущенным.");
            return 0;
        }

        Console.WriteLine("Закрываю процесс бенчмарка.");
        Shell.KillByExecutable(BenchmarkProcesses);

        return 0;
    }

    private static async Task<int> RunAsync(CommandLineOptions options)
    {
        var outputRoot = Path.GetFullPath(options.OutputDirectory ?? "results");
        var installation = SteamLocator.Locate(options.ToInstallationOptions());

        if (SettingsBackup.TryRestorePending(outputRoot))
            Console.WriteLine("Найден незавершённый прошлый запуск: исходный GameUserSettings.ini восстановлен из резервной копии.");

        var screenHeight = ScreenHeight(options);
        var profiles = SelectProfiles(options, screenHeight);
        var outputDirectory = Path.Combine(outputRoot, DateTimeStamp());
        Directory.CreateDirectory(outputDirectory);

        Console.WriteLine($"Каталог бенчмарка : {installation.InstallDir}");
        Console.WriteLine($"Каталог отчётов   : {outputDirectory}");
        Console.WriteLine("Во время прогона не трогайте мышь и клавиатуру.");

        var graphicsProbe = GraphicsDiagnostics.Probe();
        if (graphicsProbe.Warning is { } graphicsWarning)
        {
            Console.WriteLine();
            Console.WriteLine(graphicsWarning);
        }

        Console.WriteLine();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var passes = new List<PassReport>();
        var appliedWarnings = new List<string>();
        using var backup = SettingsBackup.Take(installation.ConfigPath, outputRoot);

        try
        {
            foreach (var profile in profiles)
            {
                ApplyProfile(installation.ConfigPath, profile);
                Console.WriteLine($"[{profile.Title}] настройки записаны в {installation.ConfigPath}");

                var kindName = profile.Kind.ToString().ToLowerInvariant();
                var runs = new List<BenchmarkResult>();
                LoadSample? lastLoad = null;
                string rawCopy = string.Empty;
                BenchmarkResult? representative = null;

                for (var attempt = 1; attempt <= options.RepeatCount; attempt++)
                {
                    if (options.RepeatCount > 1)
                        Console.WriteLine($"[{profile.Title}] прогон {attempt} из {options.RepeatCount}");

                    var graphics = GraphicsDiagnostics.Probe();
                    var runner = new BenchmarkRunner(
                        installation,
                        options.Layout,
                        TimeSpan.FromMinutes(options.TimeoutMinutes),
                        TimeSpan.FromSeconds(options.WindowTimeoutSeconds),
                        graphics);
                    var result = runner.Run(Write, cancellation.Token);
                    runs.Add(result);

                    var suffix = options.RepeatCount > 1 ? $"_{attempt}" : string.Empty;
                    rawCopy = Path.Combine(outputDirectory, $"{kindName}{suffix}_raw.json");
                    File.Copy(result.Path, rawCopy, overwrite: true);

                    if (runner.LastLoadSample is { } load)
                    {
                        lastLoad = load;
                        File.WriteAllText(Path.Combine(outputDirectory, $"{kindName}{suffix}_load.json"), JsonSerializer.Serialize(load, JsonOptions));
                    }

                    ConsoleReport.WriteResult(result, profile.Title, options.RepeatCount > 1 ? $"прогон {attempt} из {options.RepeatCount}" : null);

                    foreach (var warning in AppliedCheck.Warnings(result.Applied, profile.Settings.RenderScalePercent))
                    {
                        Console.WriteLine($"  ВНИМАНИЕ: {profile.Title}: {warning}");
                        appliedWarnings.Add($"[{profile.Title}] {warning}");
                    }

                    if (options.RepeatCount > 1)
                        Console.WriteLine($"  Итог по прогону: средний FPS по {runs.Count} — {MedianFps(runs).ToString("0.00", CultureInfo.InvariantCulture)}");

                    representative = MedianRun(runs);
                }

                passes.Add(new PassReport(
                    profile.Kind,
                    profile.Title,
                    representative ?? runs[^1],
                    profile.Summary,
                    profile.Rationale,
                    rawCopy,
                    lastLoad,
                    runs));

                File.WriteAllText(Path.Combine(outputDirectory, $"{kindName}_run.log"),
                    $"Профиль: {profile.Title}{Environment.NewLine}Прогонов: {runs.Count}{Environment.NewLine}" +
                    $"Результат: {representative?.Path ?? rawCopy}{Environment.NewLine}" +
                    $"Средний FPS по прогонам: {MedianFps(runs).ToString("0.00", CultureInfo.InvariantCulture)}{Environment.NewLine}" +
                    $"Применено: {representative?.Applied.ScreenResolution}, масштаб {representative?.Applied.RenderScalePercent}%, " +
                    $"качество {representative?.Applied.QualityLevel}{Environment.NewLine}");
            }
        }
        finally
        {
            if (options.KeepConfig)
            {
                Console.WriteLine("--keep-config: оставляю настройки бенчмарка как есть, копия оригинала: " + backup.BackupPath);
                backup.MarkKept();
            }
            else
            {
                Console.WriteLine("Восстанавливаю исходный GameUserSettings.ini.");
                backup.RestoreAndVerify();
            }
        }

        var system = LinuxSystemInfoProvider.Collect(installation.InstallDir, installation.ProtonVersion);
        var report = new RunReport(
            DateTime.Now,
            system,
            new BenchmarkInstallationInfo(
                installation.SteamRoot,
                installation.InstallDir,
                installation.ConfigPath,
                installation.HistoryDir,
                installation.ProtonVersion ?? "неизвестно",
                "XTEST (libX11/libXtst)"),
            passes);

        ReportFormatter.WriteToDirectory(report, outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "environment.txt"), BuildEnvironmentText(installation, appliedWarnings));

        Console.WriteLine();
        ConsoleReport.Write(report);
        Console.WriteLine(outputDirectory);

        return 0;
    }

    private static int Report(CommandLineOptions options)
    {
        if (options.ResultFiles.Count == 0)
        {
            Console.Error.WriteLine("Укажите файлы результатов: --from-results <cpu.json> <gpu.json>");
            return 2;
        }

        var inputPaths = options.ResultFiles
            .Select(Path.GetFullPath)
            .ToList();

        if (inputPaths.Count > 2)
            Console.Error.WriteLine($"Внимание: передано файлов результатов: {inputPaths.Count}, будут использованы первые два.");

        foreach (var path in inputPaths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Файл результатов не найден: {path}");
                return 2;
            }
        }

        var parsed = inputPaths.Select(ResultParser.ParseFile).ToList();

        // Высота окна нужна только для таблицы настроек. В готовом результате она уже есть,
        // поэтому на машине без дисплея (Windows, macOS, CI) xrandr звать не нужно.
        var screenHeight = HeightFromResults(options, parsed);

        var outputRoot = Path.GetFullPath(options.OutputDirectory ?? "results");
        var outputDirectory = Path.Combine(outputRoot, DateTimeStamp());
        Directory.CreateDirectory(outputDirectory);

        var passes = new List<PassReport>();
        var kinds = new[] { TestKind.Cpu, TestKind.Gpu };

        for (var i = 0; i < options.ResultFiles.Count && i < kinds.Length; i++)
        {
            var profile = kinds[i] == TestKind.Cpu
                ? ProfileCatalog.Cpu(options.CpuRenderScale ?? 50, screenHeight)
                : ProfileCatalog.Gpu(options.GpuRayTracing, options.GpuRenderScale ?? 100, screenHeight);

            var result = parsed[i];
            var kindName = kinds[i].ToString().ToLowerInvariant();
            var rawCopy = Path.Combine(outputDirectory, $"{kindName}_raw.json");
            File.Copy(result.Path, rawCopy, overwrite: true);

            passes.Add(new PassReport(kinds[i], profile.Title, result, profile.Summary, profile.Rationale, rawCopy, ReadLoad(Path.GetDirectoryName(result.Path), kindName)));
        }

        string installDir = string.Empty;
        string configPath = "не определён";
        string historyDir = "не определён";
        string? proton = null;

        if (options.InstallDir is { Length: > 0 } dir && Directory.Exists(dir))
            installDir = dir;
        else
        {
            try
            {
                var installation = SteamLocator.Locate(options.ToInstallationOptions());
                installDir = installation.InstallDir;
                configPath = installation.ConfigPath;
                historyDir = installation.HistoryDir;
                proton = installation.ProtonVersion;
            }
            catch (InvalidOperationException)
            {
            }
        }

        var system = LinuxSystemInfoProvider.Collect(installDir, proton);
        var report = new RunReport(
            DateTime.Now,
            system,
            new BenchmarkInstallationInfo("не определён", installDir, configPath, historyDir, proton ?? "неизвестно", "не использовался"),
            passes);

        ReportFormatter.WriteToDirectory(report, outputDirectory);
        ConsoleReport.Write(report);
        Console.WriteLine(outputDirectory);

        return 0;
    }

    private static LoadSample? ReadLoad(string? directory, string kindName)
    {
        if (directory is null)
            return null;

        var path = Path.Combine(directory, $"{kindName}_load.json");
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<LoadSample>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Высота окна, от которой считается высота рендера. Берётся у активного выхода xrandr;
    /// если определить не вышло, требуем --render-height, чтобы не писать в конфиг заведомо неверное число.
    /// </summary>
    private static int HeightFromResults(CommandLineOptions options, IReadOnlyList<BenchmarkResult> parsed)
    {
        foreach (var result in parsed)
        {
            if (LinuxSystemInfoProvider.ParseResolution(result.Applied.ScreenResolution) is { } resolution && resolution.Height > 0)
                return resolution.Height;
        }

        return ScreenHeight(options);
    }

    private static int ScreenHeight(CommandLineOptions options)
    {
        if (options.RenderHeight is { Length: > 0 } manual)
        {
            if (!int.TryParse(manual, out var manualHeight) || manualHeight <= 0)
                throw new ArgumentException($"--render-height требует целое число пикселей > 0, получено: {manual}");

            return manualHeight;
        }

        if (LinuxSystemInfoProvider.DetectDisplayHeight() is { } height)
            return height;

        throw new InvalidOperationException(
            "Не удалось определить разрешение экрана (xrandr не дал подключённый выход)."
            + Environment.NewLine
            + "Укажите высоту окна вручную: --render-height <пиксели> (например, --render-height 1080).");
    }

    private static IReadOnlyList<BenchmarkProfile> SelectProfiles(CommandLineOptions options, int screenHeight) =>
        options.OnlyKind switch
        {
            "cpu" => [ProfileCatalog.Cpu(options.CpuRenderScale ?? 50, screenHeight)],
            "gpu" => [ProfileCatalog.Gpu(options.GpuRayTracing, options.GpuRenderScale ?? 100, screenHeight)],
            null or "" => ProfileCatalog.Default(
                options.GpuRayTracing,
                options.CpuRenderScale,
                options.GpuRenderScale,
                screenHeight),
            _ => throw new ArgumentException($"Неизвестное значение --only: {options.OnlyKind} (ожидается cpu или gpu)"),
        };

    private static void ApplyProfile(string configPath, BenchmarkProfile profile)
    {
        var document = IniDocument.ParseFile(configPath);
        GameSettingsWriter.Apply(document, profile.Settings);
        document.Save(configPath);
    }

    private static string BuildEnvironmentText(BenchmarkInstallation installation, IReadOnlyList<string>? appliedWarnings)
    {
        var graphics = GraphicsDiagnostics.Probe();

        var lines = new List<string>
        {
            $"Steam root       : {installation.SteamRoot}",
            $"Install dir      : {installation.InstallDir}",
            $"Config           : {installation.ConfigPath}",
            $"History dir      : {installation.HistoryDir}",
            $"Proton prefix    : {installation.PrefixDir}",
            $"Proton version   : {installation.ProtonVersion}",
            $"Launcher exe     : {installation.LauncherExe}",
            $"DISPLAY          : {Environment.GetEnvironmentVariable("DISPLAY")}",
            $"XDG_SESSION_TYPE : {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")}",
            $"Steam processes  : {string.Join("; ", Shell.FindProcessesByExecutable(["steam.sh", "steamwebhelper"]).Select(p => p.Pid.ToString()))}",
            $"Graphics         : {graphics.Summary}",
            $"utc now          : {DateTime.UtcNow:O}",
        };

        if (appliedWarnings is { Count: > 0 })
        {
            lines.Add("Applied check   :");
            lines.AddRange(appliedWarnings.Select(warning => $"  - {warning}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static ProcessInfo WaitForProcess(TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            var processes = Shell.FindProcessesByExecutable(BenchmarkProcesses);
            if (processes.Count > 0)
                return processes[0];

            Thread.Sleep(2000);
        }

        throw new BenchmarkRunException("Бенчмарк не запустился за отведённое время.");
    }

    private static double MedianFps(IReadOnlyList<BenchmarkResult> runs)
    {
        var sorted = runs.Select(r => r.FpsAverage).OrderBy(v => v).ToList();
        return ResultParser.Median(sorted);
    }

    /// <summary>Прогон с медианным FPS: при нескольких прогонах это представитель всей серии.</summary>
    private static BenchmarkResult MedianRun(IReadOnlyList<BenchmarkResult> runs) =>
        runs.OrderBy(r => r.FpsAverage).ToList()[runs.Count / 2];

    private static bool SteamRunning() => Shell.SteamClientPid() is not null;

    private static void Write(string message) => Console.WriteLine("  " + message);
}
