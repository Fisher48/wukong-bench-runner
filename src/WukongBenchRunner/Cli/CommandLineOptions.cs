using WukongBenchRunner.Discovery;
using WukongBenchRunner.Running;

namespace WukongBenchRunner.Cli;

public sealed class CommandLineOptions
{
    public string Command { get; private set; } = "help";
    public string? SteamRoot { get; private set; }
    public string? InstallDir { get; private set; }
    public string? HistoryDir { get; private set; }
    public string? OutputDirectory { get; private set; }
    public string? OnlyKind { get; private set; }
    public int TimeoutMinutes { get; private set; } = 15;

    /// <summary>Сколько раз прогнать каждый профиль: медиана по серии устойчивее одного замера.</summary>
    public int RepeatCount { get; private set; } = 1;

    /// <summary>Режим запуска теста: флаг движка, клики по меню или флаг со страховкой кликами.</summary>
    public Running.StartMode StartMode { get; private set; } = Running.StartMode.Auto;

    /// <summary>Сколько ждать результата от флага, прежде чем включить клики по меню.</summary>
    public TimeSpan? FlagGrace { get; private set; }
    public int WindowTimeoutSeconds { get; private set; } = 300;
    public bool GpuRayTracing { get; private set; }
    public double? StartX { get; private set; }
    public double? StartY { get; private set; }
    public double? ConfirmX { get; private set; }
    public double? ConfirmY { get; private set; }
    public IReadOnlyList<string> ResultFiles { get; private set; } = [];
    public bool KeepConfig { get; private set; }
    public bool KeepRunning { get; private set; }
    public double ProbeClickX { get; private set; } = 0.5;
    public double ProbeClickY { get; private set; } = 0.05;
    public bool ProbePressReturn { get; private set; }
    public int? CpuRenderScale { get; private set; }
    public int? GpuRenderScale { get; private set; }
    public string? RenderHeight { get; private set; }

    public MenuLayout Layout => new(
        StartX ?? MenuLayout.Default.StartButtonX,
        StartY ?? MenuLayout.Default.StartButtonY,
        ConfirmX ?? MenuLayout.Default.ConfirmButtonX,
        ConfirmY ?? MenuLayout.Default.ConfirmButtonY);

    public InstallationOptions ToInstallationOptions() => new()
    {
        SteamRoot = SteamRoot,
        InstallDir = InstallDir,
        HistoryDir = HistoryDir,
    };

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();

        if (args.Length > 0 && !args[0].StartsWith('-'))
            options.Command = args[0].ToLowerInvariant();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "--steam-root":
                    options.SteamRoot = Next(args, ref i, arg);
                    break;
                case "--install-dir":
                case "--dir":
                    options.InstallDir = Next(args, ref i, arg);
                    break;
                case "--history-dir":
                    options.HistoryDir = Next(args, ref i, arg);
                    break;
                case "--output":
                    options.OutputDirectory = Next(args, ref i, arg);
                    break;
                case "--only":
                    options.OnlyKind = Next(args, ref i, arg)?.ToLowerInvariant();
                    break;
                case "--render-height":
                    var renderHeight = Next(args, ref i, arg);
                    if (!int.TryParse(renderHeight, out var height) || height <= 0 || height > 16384)
                        throw new ArgumentException(
                            $"--render-height требует целое число пикселей от 1 до 16384, получено: {renderHeight}");
                    options.RenderHeight = renderHeight;
                    break;
                case "--window-timeout":
                    var windowTimeout = Next(args, ref i, arg);
                    if (!int.TryParse(windowTimeout, out var seconds) || seconds < 30 || seconds > 1800)
                        throw new ArgumentException(
                            $"--window-timeout требует целое число секунд от 30 до 1800, получено: {windowTimeout}");
                    options.WindowTimeoutSeconds = seconds;
                    break;
                case "--timeout":
                    var timeout = Next(args, ref i, arg);
                    if (!int.TryParse(timeout, out var minutes) || minutes <= 0)
                        throw new ArgumentException($"--timeout требует целое число минут > 0, получено: {timeout}");

                    options.TimeoutMinutes = minutes;
                    break;
                case "--start-mode":
                    var mode = Next(args, ref i, arg);
                    options.StartMode = mode switch
                    {
                        "auto" => Running.StartMode.Auto,
                        "flag" => Running.StartMode.Flag,
                        "menu" => Running.StartMode.Menu,
                        _ => throw new ArgumentException($"--start-mode ожидает auto, flag или menu, получено: {mode}"),
                    };
                    break;
                case "--flag-timeout":
                    var grace = Next(args, ref i, arg);
                    if (!int.TryParse(grace, out var graceSeconds) || graceSeconds is < 0 or > 600)
                        throw new ArgumentException($"--flag-timeout требует целое число секунд от 0 до 600, получено: {grace}");

                    options.FlagGrace = TimeSpan.FromSeconds(graceSeconds);
                    break;
                case "--repeat":
                    var repeat = Next(args, ref i, arg);
                    if (!int.TryParse(repeat, out var times) || times is < 1 or > 10)
                        throw new ArgumentException($"--repeat требует целое число от 1 до 10, получено: {repeat}");

                    options.RepeatCount = times;
                    break;
                case "--layout-start-x":
                    options.StartX = Fraction(Next(args, ref i, arg), arg);
                    break;
                case "--layout-start-y":
                    options.StartY = Fraction(Next(args, ref i, arg), arg);
                    break;
                case "--layout-confirm-x":
                    options.ConfirmX = Fraction(Next(args, ref i, arg), arg);
                    break;
                case "--layout-confirm-y":
                    options.ConfirmY = Fraction(Next(args, ref i, arg), arg);
                    break;
                case "--gpu-raytracing":
                    options.GpuRayTracing = true;
                    break;
                case "--cpu-render-scale":
                    options.CpuRenderScale = Scale(Next(args, ref i, arg), arg);
                    break;
                case "--gpu-render-scale":
                    options.GpuRenderScale = Scale(Next(args, ref i, arg), arg);
                    break;
                case "--keep-config":
                    options.KeepConfig = true;
                    break;
                case "--keep":
                    options.KeepRunning = true;
                    break;
                case "--press-return":
                    options.ProbePressReturn = true;
                    break;
                case "--click":
                    var point = Next(args, ref i, arg)?.Replace(';', ',');
                    var parts = point?.Split(',');

                    if (parts is not { Length: 2 })
                        throw new ArgumentException($"--click ожидает две доли через запятую, например 0.115,0.452");

                    options.ProbeClickX = Fraction(parts[0], "--click");
                    options.ProbeClickY = Fraction(parts[1], "--click");
                    break;
                case "--from-results":
                    var files = new List<string>();
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        files.Add(args[++i]);

                    options.ResultFiles = files;
                    break;
                case "-h":
                case "--help":
                    options.Command = "help";
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw new ArgumentException($"Неизвестный параметр: {arg}");
                    break;
            }
        }

        return options;
    }

    public static void WriteHelp()
    {
        Console.WriteLine("""
            WukongBenchRunner - автоматический прогон Black Myth: Wukong Benchmark Tool (Steam) с раздельными
            CPU- и GPU-настройками и разбором результатов.

            Команды:
              inspect              показать найденные пути, окружение и состояние готовности
              probe                запустить бенчмарк, показать окно и кликнуть в тестовую точку
              run                  основной сценарий: CPU-тест, затем GPU-тест, затем отчёт
              report               построить отчёт по готовым файлам результатов (без запуска игры)

            Параметры:
              --install-dir <путь>      каталог "Black Myth Wukong Benchmark Tool" (иначе ищется через Steam)
              --steam-root <путь>       каталог Steam (иначе ищется в типовых местах)
              --history-dir <путь>      каталог с файлами результатов (иначе ищется в compatdata/pfx)
              --only cpu|gpu            выполнить только один проход
              --timeout <минут>         таймаут одного прохода (по умолчанию 15)
              --render-height <пиксели>  высота окна для расчёта рендера, если не определилась сама
              --window-timeout <сек>    сколько ждать окна бенчмарка (по умолчанию 300)
              --output <путь>           куда класть отчёты (по умолчанию ./results)
              --gpu-raytracing          включить трассировку лучей в GPU-тесте
              --cpu-render-scale <процент>  переопределить масштаб рендера в CPU-тесте (10..100)
              --gpu-render-scale <процент>  переопределить масштаб рендера в GPU-тесте (10..100)
              --layout-start-x <доля>   координата кнопки "Тест быстродействия" по X (0..1, по умолчанию 0.115)
              --layout-start-y <доля>   ... по Y (0.452)
              --layout-confirm-x <доля> координата кнопки "Подтвердить" по X (0.393)
              --layout-confirm-y <доля> ... по Y (0.580)
              --keep-config             не восстанавливать исходный GameUserSettings.ini
              --click <x,y>             точка клика в probe, в долях окна (по умолчанию 0.5,0.05)
              --press-return            в probe нажать Enter вместо клика
              --keep                   не закрывать бенчмарк после probe
              --from-results <cpu.json> <gpu.json>   построить отчёт без запуска бенчмарка
              --repeat <число>          сколько раз прогнать каждый профиль (1..10, по умолчанию 1)
              --start-mode auto|flag|menu  как запускать тест: движковым флагом, кликами или флагом
                                    со страховкой кликами (по умолчанию auto)
              --flag-timeout <сек>     сколько ждать флаг перед включением кликов (0..600, по умолчанию 30)

            Во время прогона не трогайте мышь и клавиатуру: окно должно оставаться в фокусе.
            """);
    }

    private static string? Next(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Параметру {name} нужно значение");

        index++;
        return args[index];
    }

    private static int Scale(string? text, string name)
    {
        if (!int.TryParse(text, out var value) || value is < 10 or > 100)
            throw new ArgumentException($"{name} требует целое число от 10 до 100, получено: {text}");

        return value;
    }

    private static double Fraction(string? text, string name)
    {
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{name} требует число от 0 до 1, получено: {text}");

        if (value is < 0 or > 1)
            throw new ArgumentException($"{name} требует число от 0 до 1, получено: {text}");

        return value;
    }
}
