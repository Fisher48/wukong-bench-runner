using System.Diagnostics;
using WukongBenchRunner.Discovery;
using WukongBenchRunner.Results;
using WukongBenchRunner.SystemInfo;

namespace WukongBenchRunner.Running;

public sealed class BenchmarkRunException(string message) : Exception(message);

public sealed class BenchmarkRunner(
    BenchmarkInstallation installation,
    MenuLayout layout,
    TimeSpan timeout,
    TimeSpan? windowTimeout = null,
    GraphicsDiagnostics? graphics = null)
{
    private const int MaxRestarts = 3;

    private readonly TimeSpan _windowTimeout = windowTimeout ?? TimeSpan.FromMinutes(5);

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
        log("Запуск через Steam: steam -applaunch 3132990");

        Launch();

        var process = WaitForProcess(_windowTimeout, log, cancellationToken);
        log($"Процесс бенчмарка: pid={process.Pid}");

        var window = WaitForWindow(process.Pid, stopwatch.Elapsed, log, cancellationToken);
        log($"Окно: 0x{window.Id:x} \"{window.Name}\" pid={window.Pid} {window.Width}x{window.Height}+{window.AbsoluteX}+{window.AbsoluteY}");

        using var sampler = new LoadSampler();
        sampler.Start();

        var iterations = 0;
        var restarts = 0;
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
                    Launch();
                    process = WaitForProcess(_windowTimeout, log, cancellationToken);
                    window = WaitForWindow(process.Pid, stopwatch.Elapsed, log, cancellationToken);
                    log($"Новое окно: 0x{window.Id:x} pid={window.Pid} {window.Width}x{window.Height}");
                    continue;
                }

                var step = navigator.StepFor(iterations);
                var latest = X11WindowFinder.FindGameWindow(Pids(), WindowNameHints) ?? window;
                navigator.Perform(step, latest);
                iterations++;

                LogProgress(log, step, iterations, stopwatch.Elapsed);

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

        log($"Готово за {stopwatch.Elapsed:mm\\:ss}, итераций меню: {iterations}");

        if (LastLoadSample is { } load)
            log(DescribeLoad(load));

        return result;
    }

    private static void Launch()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "steam",
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("-applaunch");
        startInfo.ArgumentList.Add(InstallationOptions.BenchmarkAppId.ToString());

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

    private WindowInfo WaitForWindow(int pid, TimeSpan sinceStart, Action<string> log, CancellationToken cancellationToken)
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

        log("Окно не найдено по pid, пробуем по заголовку и продолжаю с последним известным окном.");
        return X11WindowFinder.FindGameWindow(pids, WindowNameHints)
               ?? throw new BenchmarkRunException(
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
              + "Проверьте `xdpyinfo | grep -i dri3`; если расширения нет, перезайдите в сессию или используйте Xorg."
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
