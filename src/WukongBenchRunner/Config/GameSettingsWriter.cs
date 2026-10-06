namespace WukongBenchRunner.Config;

public sealed record GraphicsSettings
{
    /// <summary>Высота окна игры в пикселях: от неё считается рендер при заданном проценте.</summary>
    public required int ScreenHeight { get; init; }
    public required int RenderScalePercent { get; init; }
    public required int RenderHeight { get; init; }
    public required int QualityMenuValue { get; init; }
    public required int ScalabilityValue { get; init; }
    public required bool VSync { get; init; }
    public required bool FrameRateLimit { get; init; }
    public required bool MotionBlur { get; init; }
    public required bool FrameGeneration { get; init; }
    public required bool RayTracing { get; init; }
    public required string Upscaler { get; init; }
    public required string AntiAliasingUpscaler { get; init; }

    public static int RenderHeightFor(int screenHeight, int renderScalePercent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenHeight);

        return Math.Max(1, screenHeight * renderScalePercent / 100);
    }
}

public static class GameSettingsWriter
{
    public const string MainSection = "/Script/GSGameSettings.GSGameUserSettings";
    public const string ScalabilitySection = "ScalabilityGroups";
    public const string UiSettingKey = "UISettingData";

    private static readonly string[] QualityMenuKeys =
    [
        "ViewDistance",
        "AntiAliasing",
        "PostProcessing",
        "ShadowQuality",
        "TextureQuality",
        "FxQuality",
        "MaterialQuality",
        "VegetationQuality",
        "GlobalIllumination",
        "ReflectionQuality",
    ];

    private static readonly string[] ScalabilityKeys =
    [
        "sg.ViewDistanceQuality",
        "sg.AntiAliasingQuality",
        "sg.ShadowQuality",
        "sg.GlobalIlluminationQuality",
        "sg.ReflectionQuality",
        "sg.PostProcessQuality",
        "sg.TextureQuality",
        "sg.EffectsQuality",
        "sg.FoliageQuality",
        "sg.ShadingQuality",
    ];

    public static void Apply(IniDocument document, GraphicsSettings settings)
    {
        var menu = settings.QualityMenuValue.ToString();

        var uiUpdates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ScreenMode, ScreenRatio и ScreenResolution намеренно не трогаем: бенчмарк при первом
            // запуске сам выбирает разрешение под текущий экран, и подмена этих ключей на
            // пресеты чужого монитора ломает профиль.
            ["WindowFullImageQuality"] = "0",
            ["LockFrameRate"] = settings.FrameRateLimit ? "1" : "0",
            ["Vsync"] = settings.VSync ? "1" : "0",
            ["MotionBlur"] = settings.MotionBlur ? "2" : "0",
            ["SuperResolutionSampling"] = settings.Upscaler,
            ["AntiAliasing"] = menu,
            ["Dlss"] = settings.AntiAliasingUpscaler,
            ["InsertFrame"] = settings.FrameGeneration ? "1" : "0",
            ["Rtx"] = settings.RayTracing ? "1" : "0",
            ["RtxLevel"] = settings.RayTracing ? "2" : "0",
            ["ImageQuality"] = settings.RenderHeight.ToString(),
            ["QualityLevel"] = menu,
        };

        foreach (var key in QualityMenuKeys)
            uiUpdates[key] = menu;

        var uiRaw = document.Get(MainSection, UiSettingKey) ?? "()";
        document.Set(MainSection, UiSettingKey, TupleList.Upsert(uiRaw, uiUpdates));

        document.Set(MainSection, "bUseVSync", Bool(settings.VSync));
        document.Set(MainSection, "FrameRateLimit", settings.FrameRateLimit ? "60.000000" : "0.000000");

        document.Set(ScalabilitySection, "sg.ResolutionQuality", settings.RenderScalePercent.ToString());
        document.Set(ScalabilitySection, "sg.RayTracingQuality", settings.RayTracing ? "2" : "0");

        var scalability = settings.ScalabilityValue.ToString();
        foreach (var key in ScalabilityKeys)
            document.Set(ScalabilitySection, key, scalability);
    }

    private static string Bool(bool value) => value ? "True" : "False";
}
