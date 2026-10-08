using System.Diagnostics;
using WukongBenchRunner.Discovery;
using WukongBenchRunner.Results;
using WukongBenchRunner.SystemInfo;

namespace WukongBenchRunner.Running;

public sealed class BenchmarkRunException(string message) : Exception(message);

/// <summary>Как запускать тест: движковым флагом, слепым циклом по меню или сначала флагом.</summary>
public enum StartMode
{
    /// <summary>Сначала <c>-benchmark</c>, и если результат не пошёл — перейти на клики по меню.</summary>
    Auto,

    /// <summary>Только <c>-benchmark</c>. Окно не требуется: флаг сам запускает тест.</summary>
    Flag,

    /// <summary>Как раньше: клики по координатам без движкового флага.</summary>
    Menu,
}

public sealed class BenchmarkRunner(
    BenchmarkInstallation installation,
    MenuLayout layout,
    TimeSpan timeout,
    TimeSpan? windowTimeout = null,
    GraphicsDiagnostics? graphics = null,
    StartMode startMode = StartMode.Auto,
    TimeSpan? flagGrace = null)
{
    private const int MaxRestarts = 3;

    private readonly TimeSpan _windowTimeout = windowTimeout ?? TimeSpan.FromMinutes(5);
    private readonly StartMode _startMode = startMode;
    private readonly TimeSpan _flagGrace = flagGrace ?? TimeSpan.FromSeconds(30);

    /// <summary>True, если в этом запуске движковый флаг <c>-benchmark</c>.</summary>
    public static bool UsesBenchmarkFlag(StartMode mode) => mode != StartMode.Menu;

    public static string[] LaunchArguments(bool withFlag)
    {
        var arguments = new List<string> { "-applaunch", InstallationOptions.BenchmarkAppId.ToString() };

        if (withFlag)
            arguments.Add("-benchmark");

        return [.. arguments];
    }

    private static readonly string[] GameExecutables = ["b1-Win64-Shipping.exe"];
    private static readonly string[] AllBenchmarkExecutables = ["b1-Win64-Shipping.exe", "b1_benchmark.exe"];

    private static readonly string[] WindowNameHints = ["b1", "wukong", "black myth", "steam_app_3132990"];

    private readonly HistoryWatcher _watcher = new(installation.HistoryDir);

    public LoadSample? LastLoadSample { get; private set; }

    public BenchmarkResult Run(Action<string> log, CancellationToken cancellationToken = default)
    {
        if (Shell.FindProcessesByExecutable(AllBenchmarkExecutables).Count > 0)
            throw new BenchmarkRunException(
                "Бенчмарк уже запущен (найден процесс b1-Win64-Shipping.exe). Закройте его и повторите.");

        var input = InputBackendFactory.Create();
        var navigator = new MenuNavigator(input, layout);
        var known = _watcher.Snapshot();
        var stopwatch = Stopwatch.StartNew();

        log($"Каталог результатов: {installation.HistoryDir}");
        log($"Ввод: {navigator.InputName}");
        var withFlag = UsesBenchmarkFlag(_startMode);
        var windowRequired = _startMode == StartMode.Menu;

        log(withFlag
            ? "Запуск через Steam: steam -applaunch 3132990 -benchmark (тест запускает движок, ввод не нужен)"
            : "Запуск через Steam: steam -applaunch 3132990");

        Launch(withFlag);

        var process = WaitForProcess(_windowTimeout, log, cancellationToken);
        log($"Процесс бенчмарка: pid={process.Pid}");

        var window = WaitForWindow(process.Pid, stopwatch.Elapsed, log, cancellationToken, windowRequired);
        log(window is null
            ? "Окно не потребовалось: жду результат от движка."
            : $"Окно: 0x{window.Id:x} \"{window.Name}\" pid={window.Pid} {window.Width}x{window.Height}+{window.AbsoluteX}+{window.AbsoluteY}");

        using var sampler = new LoadSampler();
        sampler.Start();

        var iterations = 0;
        var restarts = 0;
        var fallbackLogged = false;
        var runningLogged = false;
        var benchmarkRunning = false;
        double? lastCpuSeconds = null;
        var flagDeadline = stopwatch.Elapsed + _flagGrace;
        BenchmarkResult? result = null;

        try
        {
            while (stopwatch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_watcher.TryFindNew(known) is { } finished)
                {
                    result = finished;
                    break;
                }

                if (Shell.FindProcessesByExecutable(GameExecutables).Count == 0)
                {
                    if (restarts >= MaxRestarts)
                        throw new BenchmarkRunException(
                            $"Бенчмарк закрылся несколько раз подряд (вероятно, нажатие попало в пункт меню «Выйти»). {Hint()}");

                    restarts++;
                    log($"Процесс бенчмарка исчез, перезапуск #{restarts}");
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                    Launch(withFlag);
                    process = WaitForProcess(_windowTimeout, log, cancellationToken);
                    window = WaitForWindow(process.Pid, stopwatch.Elapsed, log, cancellationToken, windowRequired);
                    log(window is null
                        ? "Окно не потребовалось после перезапуска."
                        : $"Новое окно: 0x{window.Id:x} pid={window.Pid} {window.Width}x{window.Height}");
                    continue;
                }

                // С флагом -benchmark тест идёт сам, и ввод не нужен: во время измерения любое
                // нажатие только мешает. Признак того, что флаг сработал, - процесс начал
                // считать кадры, то есть растёт его процессорное время. Пока оно на месте,
                // игра просто стоит на заставке и ждёт нажатия.
                if (withFlag && !benchmarkRunning)
                    benchmarkRunning = GainedCpuTime(process.Pid, ref lastCpuSeconds);

                if (benchmarkRunning && !runningLogged)
                {
                    runningLogged = true;
                    log("Процесс считает кадры: тест пошёл сам, ввод не трогаю.");
                }

                var graceExpired = stopwatch.Elapsed >= flagDeadline;
                var clicksAllowed = _startMode == StartMode.Menu
                    || (!benchmarkRunning && graceExpired);

                if (clicksAllowed && window is not null)
                {
                    if (withFlag && !fallbackLogged)
                    {
                        fallbackLogged = true;
                        log("Флаг -benchmark не запустил тест: процесс простаивает, переключаюсь на клики по меню.");
                    }

                    var step = navigator.StepFor(iterations);
                    var latest = X11WindowFinder.FindGameWindow(Pids(), WindowNameHints) ?? window;
                    navigator.Perform(step, latest);
                    iterations++;

                    LogProgress(log, step, iterations, stopwatch.Elapsed);
                }
                else if (!clicksAllowed)
                {
                    log(withFlag
                        ? "Жду результат от движка, клики включатся, если процесс останется простаивать."
                        : "Жду результат.");
                }

                Thread.Sleep(TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            TerminateBenchmark();
            LastLoadSample = sampler.Stop();
        }

        if (result is null)
            throw new BenchmarkRunException(
                $"Бенчмарк не записал результат за {timeout.TotalMinutes:0} мин. "
                + "Проверьте, что окно бенчмарка не перекрыто, а координаты меню совпадают: "
                + "при необходимости измените их через --layout.");

        log($"Готово за {stopwatch.Elapsed:mm\\:ss}, итераций меню: {iterations}"
            + (iterations == 0 ? " (прогон шёл без ввода, флаг -benchmark)" : string.Empty));

        if (LastLoadSample is { } load)
            log(DescribeLoad(load));

        return result;
    }

    /// <summary>
    /// Считает, что тест идёт сам: процессорное время процесса должно расти от замера к замеру.
    /// Порог 0.4 секунды за цикл (~3 с) намного больше шума на простое, но в разы меньше
    /// нагрузки, которую даёт идущий тест.
    /// </summary>
    /// <summary>Порог прироста за цикл опроса: больше шума на простое, в разы меньше нагрузки теста.</summary>
    public const double BusyCpuDeltaSeconds = 0.4;

    /// <summary>
    /// Тест идёт сам, если процессорное время между двумя замерами заметно выросло.
    /// Первого замера недостаточно: сравнивать не с чем.
    /// </summary>
    public static bool IsRunningBetween(double? previousSeconds, double currentSeconds) =>
        previousSeconds is { } before && currentSeconds - before >= BusyCpuDeltaSeconds;

    private static bool GainedCpuTime(int pid, ref double? previousSeconds)
    {
        double current;

        try
        {
            using var process = Process.GetProcessById(pid);
            current = process.TotalProcessorTime.TotalSeconds;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }

        var running = IsRunningBetween(previousSeconds, current);
        previousSeconds = current;

        return running;
    }

    private static void Launch(bool withFlag)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "steam",
            UseShellExecute = false,
        };

        foreach (var argument in LaunchArguments(withFlag))
            startInfo.ArgumentList.Add(argument);

        Process.Start(startInfo)?.Dispose();
    }

    private ProcessInfo WaitForProcess(TimeSpan wait, Action<string> log, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var announced = false;

        while (stopwatch.Elapsed < wait)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var processes = Shell.FindProcessesByExecutable(GameExecutables);
            if (processes.Count > 0)
                return processes[0];

            if (!announced)
            {
                log("Ожидание процесса бенчмарка (первый запуск может долго компилировать шейдеры)...");
                announced = true;
            }

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }

        throw new BenchmarkRunException(
            "Бенчмарк не запустился за отведённое время. Проверьте, что он установлен и запускается из Steam.");
    }

    private WindowInfo? WaitForWindow(int pid, TimeSpan sinceStart, Action<string> log, CancellationToken cancellationToken, bool required = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var pids = Pids();

        while (stopwatch.Elapsed < _windowTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var window = X11WindowFinder.FindGameWindow(pids, WindowNameHints);
            if (window is not null)
                return window;

            if (!Contains(pids))
                throw new BenchmarkRunException(DescribeEarlyExit(sinceStart));

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }

        var found = X11WindowFinder.FindGameWindow(pids, WindowNameHints);

        if (found is not null)
            return found;

        if (!required)
            return null;

        throw new BenchmarkRunException(
            $"Не найдено окно бенчмарка (pid {pid}) за {_windowTimeout.TotalSeconds:0} с. {Hint()}");
    }

    private static bool Contains(IReadOnlySet<int> pids) =>
        pids.Count == 0 || Shell.FindProcessesByExecutable(AllBenchmarkExecutables).Count > 0;

    private string DescribeEarlyExit(TimeSpan sinceStart) =>
        $"Процесс бенчмарка завершился через {sinceStart.TotalSeconds:0} с, окно так и не появилось. {Hint()}";

    private string Hint() =>
        graphics is { Dri3Available: false }
            ? "Требуется X11/XWayland с расширением DRI3: Proton без него не может вывести кадр и игра "
              + "завершается сразу после старта (см. сообщение 'vulkan: No DRI3 support detected'). "
              + "Проверьте `xdpyinfo | grep -i dri3`; если расширения нет, нужна сессия Xorg: выйдите из "
              + "системы и выберите на экране входа 'Ubuntu on Xorg' вместо Wayland."
            : "Проверьте, что окно бенчмарка не перекрыто и в фокусе, а координаты меню совпадают (--layout).";

    private static string DescribeLoad(LoadSample sample)
    {
        if (!sample.Available)
            return "Загрузка CPU/GPU недоступна на этой системе";

        var gpu = sample.AverageGpuBusyPercent is { } g ? $"GPU {g:0}% (пик {sample.PeakGpuBusyPercent:0}%)" : "GPU н/д";
        var cpu = sample.AverageCpuBusyPercent is { } c ? $"CPU {c:0}% (пик {sample.PeakCpuBusyPercent:0}%)" : "CPU н/д";
        return $"Средняя загрузка за прогон: {cpu}, {gpu}; отсчётов: {sample.Samples}";
    }

    private static void LogProgress(Action<string> log, MenuStep step, int iterations, TimeSpan elapsed)
    {
        var action = step switch
        {
            MenuStep.SkipIntro => "Enter (пропуск заставки)",
            MenuStep.OpenBenchmark => "клик \"Тест быстродействия\"",
            MenuStep.Confirm => "клик \"Подтвердить\"",
            _ => "?",
        };

        log($"[{elapsed:mm\\:ss}] шаг {iterations}: {action}");
    }

    private static IReadOnlySet<int> Pids() =>
        Shell.FindProcessesByExecutable(AllBenchmarkExecutables).Select(p => p.Pid).ToHashSet();

    private void TerminateBenchmark()
    {
        Shell.KillByExecutable(GameExecutables, force: true);

        var protonWineServer = ProtonWineServerPath();
        if (protonWineServer is null)
            return;

        if (installation.PrefixDir is not { Length: > 0 } prefix)
            return;

        var environment = new Dictionary<string, string>
        {
            ["WINEPREFIX"] = Path.Combine(prefix, "pfx"),
        };

        try
        {
            Shell.Run(protonWineServer, ["-k"], environment, timeoutMs: 15_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
        }
    }

    private string? ProtonWineServerPath()
    {
        if (installation.SteamRoot is not { Length: > 0 } steamRoot)
            return null;

        var common = Path.Combine(steamRoot, "steamapps", "common");
        if (!Directory.Exists(common))
            return null;

        var candidates = Directory.GetDirectories(common, "Proton*", SearchOption.TopDirectoryOnly);
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(candidate, "files", "bin", "wineserver");
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
