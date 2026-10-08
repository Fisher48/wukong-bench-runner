using WukongBenchRunner.Config;

namespace WukongBenchRunner.Profiles;

public enum TestKind
{
    Cpu,
    Gpu,
}

public sealed record BenchmarkProfile(
    TestKind Kind,
    string Title,
    GraphicsSettings Settings,
    IReadOnlyList<(string Parameter, string Value)> Summary,
    IReadOnlyList<string> Rationale);

public static class ProfileCatalog
{
    private const string UpscalerOff = "0";

    /// <summary>
    /// Заготовка для намеренной догрузки процессора: дальность прорисовки и плотность растительности
    /// добавляют объектов в кадр, то есть больше работы на стороне CPU. По умолчанию не применяется:
    /// я пробовал поднять эти группы до 5 и FPS упал с 15 до 12, а <c>CPUFrameTime</c> наоборот
    /// снизился с 19.1 до 14.9 мс. То есть лишняя сцена добавляет нагрузку, но не ту, которую бенчмарк
    /// измеряет, и оценку процессора только ухудшает.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> CpuScalabilityOverrides =
        new Dictionary<string, int>
        {
            ["sg.ViewDistanceQuality"] = 5,
            ["sg.FoliageQuality"] = 5,
        };

    public static BenchmarkProfile Cpu(int renderScalePercent = 50, int screenHeight = DefaultScreenHeight) => new(
        TestKind.Cpu,
        "CPU-тест",
        new GraphicsSettings
        {
            ScreenHeight = screenHeight,
            RenderScalePercent = renderScalePercent,
            RenderHeight = GraphicsSettings.RenderHeightFor(screenHeight, renderScalePercent),
            QualityMenuValue = 1,
            ScalabilityValue = 0,
            VSync = false,
            FrameRateLimit = false,
            MotionBlur = false,
            FrameGeneration = false,
            RayTracing = false,
            Upscaler = UpscalerOff,
            AntiAliasingUpscaler = "0",
        },
        [
            ("Разрешение окна", $"{RenderWidth(screenHeight)} x {screenHeight} (текущее окно игры, не меняется)"),
            ("Масштаб рендеринга", $"{renderScalePercent}% -> {RenderWidth(screenHeight) * renderScalePercent / 100} x {GraphicsSettings.RenderHeightFor(screenHeight, renderScalePercent)}"),
            ("ImageQuality (высота рендера)", $"{GraphicsSettings.RenderHeightFor(screenHeight, renderScalePercent)} px"),
            ("Качество (меню / sg.*)", "1 / 0 - минимум"),
            ("Трассировка лучей", "выкл."),
            ("Апскейлер (DLSS/FSR/XeSS)", "выкл."),
            ("Генерация кадров", "выкл."),
            ("Motion blur", "выкл."),
            ("VSync", "выкл."),
            ("Ограничение FPS", "выкл."),
        ],
        [
            "Масштаб рендеринга 50% и минимальное качество убирают почти всю работу GPU: " +
            "на кадр остаётся ~1-2 мс, тогда как только CPU на этой сцене занимает больше времени. " +
            "Видеокарта перестаёт быть ограничителем: контрольный прогон при 25% масштаба рендера " +
            "результат не меняет, значит кадр уже не про графику.",
            "Все десять групп качества стоят на нуле: нагружать процессор дополнительно смысла не нашлось. " +
            "Я пробовал поднять дальность прорисовки и плотность растительности до максимума — объектов в кадре " +
            "стало больше, и FPS упал с 13 до 12, но CPUFrameTime при этом не вырос, а упал с 13.2 до 14.9 мс. " +
            "Значит лишняя сцена добавляет нагрузку, но не ту, которую бенчмарк измеряет, и оценку процессора " +
            "только ухудшает, поэтому вернул минимум.",
            "Разрешение окна не трогаю: бенчмарк при первом запуске сам выбрал его под этот экран, " +
            "а потери GPU и так достаточно за счёт 50% масштаба. Играть в разрешении меньше окна " +
            "дополнительно снижает быстродействие CPU (окно, ввод, презентация кадра), " +
            "что для CPU-теста нежелательно.",
            "Трассировка лучей и генерация кадров выключены: генерация кадров повышает FPS без участия " +
            "CPU и искажает результат, а трассировку включить нечем - на Barcelo нет RT-ядер, и режим " +
            "с ней на этом ноутбуке не проверен.",
            "VSync и ограничение FPS выключены: иначе FPS упрётся в частоту монитора, а не в CPU.",
            "Апскейлер выключен, чтобы внутреннее разрешение действительно равнялось половине окна, " +
            "а не результату работы фирменного DLSS/FSR.",
        ]);

    public static BenchmarkProfile Gpu(bool rayTracing = false, int renderScalePercent = 100, int screenHeight = DefaultScreenHeight) => new(
        TestKind.Gpu,
        "GPU-тест",
        new GraphicsSettings
        {
            ScreenHeight = screenHeight,
            RenderScalePercent = renderScalePercent,
            RenderHeight = GraphicsSettings.RenderHeightFor(screenHeight, renderScalePercent),
            QualityMenuValue = 5,
            ScalabilityValue = 4,
            VSync = false,
            FrameRateLimit = false,
            MotionBlur = false,
            FrameGeneration = false,
            RayTracing = rayTracing,
            Upscaler = UpscalerOff,
            AntiAliasingUpscaler = "0",
        },
        [
            ("Разрешение окна", $"{RenderWidth(screenHeight)} x {screenHeight} (текущее окно игры, не меняется)"),
            ("Масштаб рендеринга", $"{renderScalePercent}% -> {RenderWidth(screenHeight) * renderScalePercent / 100} x {GraphicsSettings.RenderHeightFor(screenHeight, renderScalePercent)}"),
            ("ImageQuality (высота рендера)", $"{GraphicsSettings.RenderHeightFor(screenHeight, renderScalePercent)} px"),
            ("Качество (меню / sg.*)", "5 / 4 - максимум"),
            ("Трассировка лучей", rayTracing ? "вкл. (RtxLevel=2)" : "выкл. (по умолчанию)"),
            ("Апскейлер (DLSS/FSR/XeSS)", "выкл. - считается каждый пиксель"),
            ("Генерация кадров", "выкл. - иначе FPS не отражает GPU"),
            ("Motion blur", "выкл."),
            ("VSync", "выкл."),
            ("Ограничение FPS", "выкл."),
        ],
        rayTracing
            ? [
                "Все группы качества на максимуме (меню 5 / sg.*=4): самые тяжёлые тени, глобальное освещение, " +
                "отражения, растительность и постобработка - основные потребители GPU. Растительность здесь работает на GPU, а в CPU-тесте она сведена к минимуму, чтобы видеокарта не держала кадр.",
                "Масштаб рендеринга 100% и выключенный апскейлер: каждый пиксель считается " +
                "нативно, без скрытого понижения внутреннего разрешения.",
                "Трассировка лучей включена (--gpu-raytracing): она добавляет чистую нагрузку на GPU.",
                "Генерация кадров выключена: она раздувает FPS за счёт дорисованных кадров " +
                "и делает результат GPU-теста бессмысленным.",
                "VSync и ограничение FPS выключены: видеокарта работает без простоев.",
            ]
            : [
                "Все группы качества на максимуме (меню 5 / sg.*=4): самые тяжёлые тени, глобальное освещение, " +
                "отражения, растительность и постобработка - основные потребители GPU. Растительность здесь работает на GPU, а в CPU-тесте она сведена к минимуму, чтобы видеокарта не держала кадр.",
                "Масштаб рендеринга 100% и выключенный апскейлер: каждый пиксель считается " +
                "нативно, без скрытого понижения внутреннего разрешения.",
                "Генерация кадров выключена: она раздувает FPS за счёт дорисованных кадров " +
                "и делает результат GPU-теста бессмысленным.",
                "VSync и ограничение FPS выключены: видеокарта работает без простоев, загрузка близка к 100%.",
                "Трассировка лучей по умолчанию выключена: она доступна не на всех видеокартах, " +
                "неподдерживаемое значение роняет бенчмарк, а из-за разной реализации трассировки " +
                "результаты перестают быть сопоставимыми между видеокартами. Включается флагом " +
                "--gpu-raytracing, когда нужна максимальная нагрузка именно на трассировке.",
            ]);

    /// <summary>
    /// Высота окна по умолчанию, если не удалось определить экран: 16:10 1920x1200.
    /// Реальная высота подставляется из xrandr, а этот запасной вариант можно переопределить
    /// параметром --render-height.
    /// </summary>
    public const int DefaultScreenHeight = 1200;

    public static IReadOnlyList<BenchmarkProfile> Default(
        bool gpuRayTracing = false,
        int? cpuRenderScale = null,
        int? gpuRenderScale = null,
        int screenHeight = DefaultScreenHeight) =>
        [Cpu(cpuRenderScale ?? 50, screenHeight), Gpu(gpuRayTracing, gpuRenderScale ?? 100, screenHeight)];

    private static int RenderWidth(int screenHeight) => screenHeight * 16 / 10;
}
