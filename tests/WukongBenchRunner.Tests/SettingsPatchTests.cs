using WukongBenchRunner.Config;
using WukongBenchRunner.Profiles;

namespace WukongBenchRunner.Tests;

public sealed class SettingsPatchTests
{
    private static string FixtureIni() => File.ReadAllText(Path.Combine("TestData", "GameUserSettings.ini"));

    [Fact]
    public void Ini_Читает_И_Меняет_Ключи_Не_Трогая_Остальное()
    {
        var document = IniDocument.Parse(FixtureIni());

        Assert.Equal("58", document.Get("ScalabilityGroups", "sg.ResolutionQuality"));
        Assert.Equal("False", document.Get("/Script/GSGameSettings.GSGameUserSettings", "bUseVSync"));

        document.Set("ScalabilityGroups", "sg.ResolutionQuality", "50");
        var text = document.ToText();

        Assert.Contains("sg.ResolutionQuality=50", text);
        Assert.DoesNotContain("sg.ResolutionQuality=58", text);
        Assert.Contains("UISettingData=((\"MainDisplay\", \"0\")", text);
    }

    [Fact]
    public void Переопределение_Группы_Не_Затирает_Остальные()
    {
        var document = IniDocument.Parse(FixtureIni());
        var settings = ProfileCatalog.Cpu(screenHeight: 1200).Settings with
        {
            ScalabilityOverrides = new Dictionary<string, int> { ["sg.ViewDistanceQuality"] = 5 },
        };

        GameSettingsWriter.Apply(document, settings);

        Assert.Equal("5", document.Get(GameSettingsWriter.ScalabilitySection, "sg.ViewDistanceQuality"));
        Assert.Equal("0", document.Get(GameSettingsWriter.ScalabilitySection, "sg.ShadowQuality"));
        Assert.Equal("0", document.Get(GameSettingsWriter.ScalabilitySection, "sg.TextureQuality"));
    }

    [Fact]
    public void Неизвестная_Группа_Качества_Отклоняется()
    {
        var document = IniDocument.Parse(FixtureIni());
        var settings = ProfileCatalog.Cpu(screenHeight: 1200).Settings with
        {
            ScalabilityOverrides = new Dictionary<string, int> { ["sg.NoSuchQuality"] = 5 },
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => GameSettingsWriter.Apply(document, settings));
    }

    [Fact]
    public void TupleList_Обновляет_Существующие_И_Добавляет_Новые_Ключи()
    {
        var raw = "((\"A\", \"1\"),(\"B\", \"2\"))";
        var updated = TupleList.Parse(TupleList.Upsert(raw, new Dictionary<string, string> { ["B"] = "9", ["C"] = "3" }));

        Assert.Equal(new[] { "A", "B", "C" }, updated.Select(i => i.Key));
        Assert.Equal("9", updated.Single(i => i.Key == "B").Value);
        Assert.Equal("3", updated.Single(i => i.Key == "C").Value);
    }

    [Fact]
    public void Cpu_Профиль_Ставит_Низкое_Качество_И_Половину_Рендера()
    {
        var document = IniDocument.Parse(FixtureIni());

        GameSettingsWriter.Apply(document, ProfileCatalog.Cpu(screenHeight: 1200).Settings);

        var ui = TupleList.Parse(document.Get(GameSettingsWriter.MainSection, GameSettingsWriter.UiSettingKey)!);
        string Ui(string key) => ui.Single(i => i.Key == key).Value;

        Assert.Equal("50", document.Get(GameSettingsWriter.ScalabilitySection, "sg.ResolutionQuality"));
        Assert.Equal("600", Ui("ImageQuality"));
        Assert.Equal("0", Ui("SuperResolutionSampling"));
        Assert.Equal("0", Ui("InsertFrame"));
        Assert.Equal("0", Ui("Rtx"));
        Assert.Equal("0", Ui("Vsync"));
        Assert.Equal("0", Ui("LockFrameRate"));
        Assert.Equal("1", Ui("QualityLevel"));

        foreach (var key in new[] { "ViewDistance", "AntiAliasing", "PostProcessing", "ShadowQuality", "TextureQuality", "FxQuality", "MaterialQuality", "VegetationQuality", "GlobalIllumination", "ReflectionQuality" })
            Assert.Equal("1", Ui(key));

        foreach (var key in new[]
                 {
                     "sg.ViewDistanceQuality", "sg.AntiAliasingQuality", "sg.ShadowQuality", "sg.GlobalIlluminationQuality",
                     "sg.ReflectionQuality", "sg.PostProcessQuality", "sg.TextureQuality", "sg.EffectsQuality",
                     "sg.FoliageQuality", "sg.ShadingQuality",
                 })
        {
            Assert.Equal("0", document.Get(GameSettingsWriter.ScalabilitySection, key));
        }

        Assert.Equal("0", document.Get(GameSettingsWriter.ScalabilitySection, "sg.ViewDistanceQuality"));
        Assert.Equal("0", document.Get(GameSettingsWriter.ScalabilitySection, "sg.FoliageQuality"));
    }

    [Fact]
    public void Gpu_Профиль_Ставит_Максимальное_Качество_И_100_Процентов_Рендера()
    {
        var document = IniDocument.Parse(FixtureIni());

        GameSettingsWriter.Apply(document, ProfileCatalog.Gpu(screenHeight: 1200).Settings);

        var ui = TupleList.Parse(document.Get(GameSettingsWriter.MainSection, GameSettingsWriter.UiSettingKey)!);
        string Ui(string key) => ui.Single(i => i.Key == key).Value;

        Assert.Equal("100", document.Get(GameSettingsWriter.ScalabilitySection, "sg.ResolutionQuality"));
        Assert.Equal("1200", Ui("ImageQuality"));
        Assert.Equal("5", Ui("QualityLevel"));
        Assert.Equal("0", Ui("InsertFrame"));
        Assert.Equal("0", Ui("Rtx"));
        Assert.Equal("5", Ui("ViewDistance"));

        Assert.Equal("4", document.Get(GameSettingsWriter.ScalabilitySection, "sg.ViewDistanceQuality"));
        Assert.Equal("4", document.Get(GameSettingsWriter.ScalabilitySection, "sg.PostProcessQuality"));
    }

    [Fact]
    public void Gpu_Профиль_С_Трассировкой_Включает_RayTracing()
    {
        var document = IniDocument.Parse(FixtureIni());

        GameSettingsWriter.Apply(document, ProfileCatalog.Gpu(rayTracing: true).Settings);

        var ui = TupleList.Parse(document.Get(GameSettingsWriter.MainSection, GameSettingsWriter.UiSettingKey)!);

        Assert.Equal("1", ui.Single(i => i.Key == "Rtx").Value);
        Assert.Equal("2", ui.Single(i => i.Key == "RtxLevel").Value);
        Assert.Equal("2", document.Get(GameSettingsWriter.ScalabilitySection, "sg.RayTracingQuality"));
    }

    [Fact]
    public void Неизвестные_Ключи_Конфига_Сохраняются()
    {
        var document = IniDocument.Parse(FixtureIni());
        var before = TupleList.Parse(document.Get(GameSettingsWriter.MainSection, GameSettingsWriter.UiSettingKey)!).Count;

        GameSettingsWriter.Apply(document, ProfileCatalog.Cpu(screenHeight: 1200).Settings);

        var after = TupleList.Parse(document.Get(GameSettingsWriter.MainSection, GameSettingsWriter.UiSettingKey)!);

        Assert.Equal(before, after.Count);
        Assert.Contains(after, i => i.Key == "PrivacyAgreement");
        Assert.Contains(after, i => i.Key == "ScreenBrightness");
        Assert.Contains("LastCPUBenchmarkResult=", document.ToText());
        Assert.Contains("[ScalabilityGroups]", document.ToText());
    }

    [Fact]
    public void Бэкап_И_Восстановление_Возвращают_Исходный_Файл()
    {
        var root = Path.Combine(Path.GetTempPath(), "wbr-backup-" + Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, FixtureIni());

        try
        {
            var backup = SettingsBackup.Take(configPath, root);
            Assert.True(File.Exists(backup.BackupPath));

            File.WriteAllText(configPath, "испорчено");
            backup.Restore();

            Assert.Equal(FixtureIni(), File.ReadAllText(configPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Незавершённый_Прогон_Восстанавливается_При_Следующем_Запуске()
    {
        var root = Path.Combine(Path.GetTempPath(), "wbr-pending-" + Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, FixtureIni());

        try
        {
            SettingsBackup.Take(configPath, root);
            File.WriteAllText(configPath, "испорчено");

            Assert.True(SettingsBackup.TryRestorePending(root));
            Assert.Equal(FixtureIni(), File.ReadAllText(configPath));
            Assert.False(SettingsBackup.TryRestorePending(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Профиль_Считает_Высоту_Рендера_От_Экрана()
    {
        Assert.Equal("540", ProfileCatalog.Cpu(screenHeight: 1080).Settings.RenderHeight.ToString());
        Assert.Equal("1080", ProfileCatalog.Gpu(screenHeight: 1080).Settings.RenderHeight.ToString());
        Assert.Equal("450", ProfileCatalog.Cpu(screenHeight: 900).Settings.RenderHeight.ToString());
        Assert.Equal("600", ProfileCatalog.Cpu(screenHeight: 1200).Settings.RenderHeight.ToString());
        Assert.Equal("1200", ProfileCatalog.Gpu(screenHeight: 1200).Settings.RenderHeight.ToString());
    }

    [Fact]
    public void Разрешение_Окна_Не_Переписывается()
    {
        var document = IniDocument.Parse(FixtureIni());

        var uiBefore = TupleList.Parse(document.Get(GameSettingsWriter.MainSection, GameSettingsWriter.UiSettingKey)!);
        var ratioBefore = uiBefore.Single(i => i.Key == "ScreenRatio").Value;
        var resolutionBefore = uiBefore.Single(i => i.Key == "ScreenResolution").Value;

        GameSettingsWriter.Apply(document, ProfileCatalog.Cpu(screenHeight: 1080).Settings);

        var uiAfter = TupleList.Parse(document.Get(GameSettingsWriter.MainSection, GameSettingsWriter.UiSettingKey)!);

        Assert.Equal(ratioBefore, uiAfter.Single(i => i.Key == "ScreenRatio").Value);
        Assert.Equal(resolutionBefore, uiAfter.Single(i => i.Key == "ScreenResolution").Value);
    }
}
