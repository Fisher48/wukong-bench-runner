using WukongBenchRunner.Config;
using WukongBenchRunner.Running;
using WukongBenchRunner.SystemInfo;
using WukongBenchRunner.Results;
using WukongBenchRunner.Profiles;

namespace WukongBenchRunner.Tests;

public sealed class RobustnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wbr-robust-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Ini_Дубликаты_Ключей_Читается_Последний_И_Обновляются_Все()
    {
        var document = IniDocument.Parse(
            "[Section]\nValue=1\nOther=keep\nValue=2\nValue=3\n");

        Assert.Equal("3", document.Get("Section", "Value"));
        Assert.Equal(["1", "2", "3"], document.GetAll("Section", "Value"));

        document.Set("Section", "Value", "9");

        Assert.Equal("9", document.Get("Section", "Value"));
        Assert.Equal(["9", "9", "9"], document.GetAll("Section", "Value"));
        Assert.Contains("Other=keep", document.ToText());
    }

    [Fact]
    public void Ini_Имя_Секции_С_Пробелами_В_Скобках_Совпадает()
    {
        var document = IniDocument.Parse("[ /Script/Foo.Bar ]\nKey=1\n");

        Assert.True(document.HasSection("/Script/Foo.Bar"));

        document.Set("/Script/Foo.Bar", "Key", "2");

        Assert.Equal("2", document.Get("/Script/Foo.Bar", "Key"));
        Assert.Equal(1, document.ToText().Split("[ /Script/Foo.Bar ]").Length - 1);
    }

    [Fact]
    public void Ini_Не_Создаёт_Дубликат_Секции_При_Добавлении_Ключа()
    {
        var document = IniDocument.Parse("[A]\nx=1\n[B]\ny=2\n");

        document.Set("B", "z", "3");
        document.Set("C", "w", "4");

        var text = document.ToText();
        Assert.Equal(1, text.Split("[B]").Length - 1);
        Assert.Contains("z=3", text);
        Assert.Contains("[C]\nw=4", text);
    }

    [Fact]
    public void Ini_Удаление_Убирает_Все_Вхождения_Ключа()
    {
        var document = IniDocument.Parse("[A]\nx=1\nx=2\ny=3\n");

        Assert.Equal(2, document.Remove("A", "x"));
        Assert.Null(document.Get("A", "x"));
        Assert.Equal("3", document.Get("A", "y"));
    }

    [Fact]
    public void Cli_Отвергает_Неизвестный_Параметр()
    {
        var error = Assert.Throws<ArgumentException>(() => WukongBenchRunner.Cli.CommandLineOptions.Parse(["run", "--typo", "5"]));
        Assert.Contains("--typo", error.Message);
    }

    [Theory]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "abc")]
    [InlineData("--cpu-render-scale", "500")]
    [InlineData("--window-timeout", "10")]
    [InlineData("--render-height", "0")]
    [InlineData("--render-height", "неизвестно")]
    [InlineData("--window-timeout", "5000")]
    [InlineData("--window-timeout", "abc")]
    [InlineData("--layout-start-x", "5")]
    [InlineData("--layout-confirm-y", "-1")]
    public void Cli_Проверяет_Диапазоны(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => WukongBenchRunner.Cli.CommandLineOptions.Parse(["run", name, value]));
    }

    [Fact]
    public void Cli_Window_Timeout_По_Умолчанию_И_Переопределяется()
    {
        Assert.Equal(300, WukongBenchRunner.Cli.CommandLineOptions.Parse(["run"]).WindowTimeoutSeconds);

        var options = WukongBenchRunner.Cli.CommandLineOptions.Parse(["run", "--window-timeout", "600"]);

        Assert.Equal(600, options.WindowTimeoutSeconds);
    }

    [Fact]
    public void Cli_Принимает_Корректные_Доли_И_Масштабы()
    {
        var options = WukongBenchRunner.Cli.CommandLineOptions.Parse(
            ["run", "--cpu-render-scale", "35", "--gpu-render-scale", "90", "--layout-start-x", "0.12", "--click", "0.115;0.452"]);

        Assert.Equal(35, options.CpuRenderScale);
        Assert.Equal(90, options.GpuRenderScale);
        Assert.Equal(0.12, options.Layout.StartButtonX);
        Assert.Equal(0.115, options.ProbeClickX);
        Assert.Equal(0.452, options.ProbeClickY);
    }

    [Fact]
    public void Cli_From_Results_Останавливается_На_Следующем_Параметре()
    {
        var options = WukongBenchRunner.Cli.CommandLineOptions.Parse(
            ["report", "--from-results", "cpu.json", "gpu.json", "--output", "/tmp/x"]);

        Assert.Equal(["cpu.json", "gpu.json"], options.ResultFiles);
        Assert.Equal("/tmp/x", options.OutputDirectory);
    }

    [Fact]
    public void Бэкап_Не_Трогает_Конфиг_При_KeepConfig()
    {
        var configPath = WriteConfig();
        var backup = SettingsBackup.Take(configPath, _root);

        backup.MarkKept();
        backup.Dispose();

        Assert.Equal("исходное", File.ReadAllText(configPath).Trim());
        Assert.False(File.Exists(Path.Combine(_root, "config-restore.pending")));
    }

    [Fact]
    public void Бэкап_Повторно_Не_Откатывает_После_Пометки()
    {
        var configPath = WriteConfig();
        var backup = SettingsBackup.Take(configPath, _root);

        File.WriteAllText(configPath, "изменённое игрой");
        backup.MarkKept();

        Assert.Equal("изменённое игрой", File.ReadAllText(configPath).Trim());
    }

    [Fact]
    public void Хвостовое_Восстановление_Сообщает_О_Пропавшем_Бэкапе()
    {
        var configPath = WriteConfig();
        Directory.CreateDirectory(Path.Combine(_root, "backup"));

        File.WriteAllText(Path.Combine(_root, "config-restore.pending"),
            configPath + Environment.NewLine + Path.Combine(_root, "backup", "нет-такого.ini"));

        Assert.False(SettingsBackup.TryRestorePending(_root));
        Assert.True(File.Exists(Path.Combine(_root, "config-restore.pending")));
    }

    [Fact]
    public void Имя_Исполняемого_Файла_Берётся_Из_Argv0()
    {
        var linux = new ProcessInfo(1, "/opt/steam/b1-Win64-Shipping.exe\0b1\0");
        var wine = new ProcessInfo(2, "S:\\steamapps\\common\\b1\\Binaries\\Win64\\b1-Win64-Shipping.exe\0b1\0");
        var wrapper = new ProcessInfo(3, "/bin/sh\0-c\0steam-launch-wrapper -- b1-Win64-Shipping.exe\0");
        var grep = new ProcessInfo(4, "grep\0b1-Win64-Shipping.exe\0file.txt\0");

        Assert.Equal("b1-Win64-Shipping.exe", linux.ExecutableName);
        Assert.Equal("b1-Win64-Shipping.exe", wine.ExecutableName);
        Assert.Equal("sh", wrapper.ExecutableName);
        Assert.Equal("grep", grep.ExecutableName);
        Assert.Contains("grep", grep.DisplayCommandLine);
    }

    [Fact]
    public void Поиск_Процессов_Не_Возвращает_Собственный_Процесс()
    {
        var found = Shell.FindProcessesByExecutable(["dotnet", "sh"]);

        Assert.DoesNotContain(found, p => p.Pid == Environment.ProcessId);
    }

    [Fact]
    public void HasExecutable_Проверяет_Право_Запуска()
    {
        Assert.True(Shell.HasExecutable("sh"));
        Assert.False(Shell.HasExecutable("definitely-not-a-real-binary-xyz"));
    }

    [Fact]
    public void Процентиль_Нулевой_Выборки_Не_Падает()
    {
        Assert.Equal(0, LoadSamplerShim.Percentile([], 0.5));
        Assert.Equal(0, LoadSamplerShim.Median([]));
    }


    [Fact]
    public void RestoreAndVerify_Повторяет_Восстановление_Если_Игра_Перезаписала_Файл()
    {
        var configPath = WriteConfig();
        var backup = SettingsBackup.Take(configPath, _root);

        File.WriteAllText(configPath, "настройки прогона");
        backup.RestoreAndVerify(attempts: 3, delayMs: 10);

        Assert.True(backup.IsRestored);
        Assert.Equal("исходное", File.ReadAllText(configPath).Trim());
    }

    [Fact]
    public void RestoreAndVerify_Повторяет_Восстановление_Несколько_Раз()
    {
        var configPath = WriteConfig();
        var backup = SettingsBackup.Take(configPath, _root);

        backup.Restore();
        File.WriteAllText(configPath, "игра перезаписала конфиг");
        backup.RestoreAndVerify(attempts: 3, delayMs: 10);

        Assert.Equal("исходное", File.ReadAllText(configPath).Trim());
    }

    [Theory]
    [InlineData("/home/fisher48/.local/share/Steam/ubuntu12_32/steam\0-srt-logger-opened\0-silent\0", true)]
    [InlineData("/usr/bin/steam\0-silent\0", true)]
    [InlineData("/home/fisher48/.local/share/Steam/ubuntu12_32/steam\0--type=steamwebhelper\0", false)]
    [InlineData("/home/fisher48/.local/share/Steam/ubuntu12_32/steam\0-child-update-ui\0", false)]
    [InlineData("/home/fisher48/.local/share/Steam/ubuntu12_32/reaper\0SteamLaunch\0AppId=3132990\0", false)]
    [InlineData("/home/fisher48/.local/share/Steam/ubuntu12_32/steam-launch-wrapper\0--\0/proton\0", false)]
    [InlineData("/home/fisher48/.local/share/Steam/steamapps/common/Black Myth Wukong Benchmark Tool/b1_benchmark.exe\0", false)]
    public void Клиент_Steam_Отличается_От_Оболочки_И_Игры(string argv, bool isClient)
    {
        Assert.Equal(isClient, Shell.IsSteamClient(new ProcessInfo(1, argv)));
    }

    [Fact]
    public void Имя_Клиента_Steam_Считается_По_Basename_А_Не_По_Пути()
    {
        // клиент запускается как <SteamRoot>/ubuntu12_32/steam, а поиск идёт по имени файла
        var client = new ProcessInfo(1, "/home/fisher48/.local/share/Steam/ubuntu12_32/steam\0-silent\0");

        Assert.Equal("steam", client.ExecutableName);
        Assert.True(Shell.IsSteamClient(client));
    }

    [Theory]
    [InlineData("b1  ", true)]
    [InlineData("Black Myth: Wukong Benchmark Tool", true)]
    [InlineData("b1  ", true)]
    [InlineData("fisher48@fisher48: ~/wukong-bench-runner", false)]
    [InlineData("bash -c dotnet run", false)]
    [InlineData("Steam", true)]
    [InlineData("wukong-bench-runner \u2013 README.md", false)]
    [InlineData("wukong-bench-runner - main.cs", false)]
    [InlineData("notes.txt - Kate", false)]
    [InlineData("[Steam] b1", false)]
    [InlineData("steam_app_3132990", true)]
    [InlineData("  ~/projects/black myth", false)]
    [InlineData("", false)]
    public void Заголовок_Окна_Отличает_Игру_От_Терминала(string title, bool expected)
    {
        Assert.Equal(expected, X11WindowFinder.LooksLikeGameTitle(title));
    }


    [Theory]
    [InlineData("1920x1200", 1920, 1200)]
    [InlineData("1920X1080", 1920, 1080)]
    [InlineData("2560x1440", 2560, 1440)]
    [InlineData("1920 \u00d7 1200", 1920, 1200)]
    [InlineData("неизвестно", 0, 0)]
    [InlineData("", 0, 0)]
    [InlineData(null, 0, 0)]
    public void Разрешение_Экрана_Парсится_Или_Нет(string input, int width, int height)
    {
        var parsed = LinuxSystemInfoProvider.ParseResolution(input);

        if (width == 0)
            Assert.Null(parsed);
        else
            Assert.Equal((width, height), parsed);
    }

    [Fact]
    public void Разрешение_Берётся_Из_Подключённого_Вывода_Xrandr()
    {
        const string output = "Screen 0: minimum 320 x 200, current 1920 x 1200, maximum 16384 x 16384\n"
                              + "eDP-1 connected primary 1920x1200+0+0 (normal left inverted right x axis y axis) 310mm x 200mm\n";

        Assert.Equal("1920x1200", LinuxSystemInfoProvider.ResolutionFromXrandr(output));
        Assert.Equal(1200, LinuxSystemInfoProvider.ParseResolution(LinuxSystemInfoProvider.ResolutionFromXrandr(output))!.Value.Height);
    }

    [Fact]
    public void Проверка_Применённого_Ловит_Несовпадение_Масштаба()
    {
        var ok = Make(50, "1920 × 1200");
        var wrongScale = Make(100, "1920 × 1200");

        Assert.Empty(AppliedCheck.Warnings(ok, 50));
        Assert.Contains(AppliedCheck.Warnings(wrongScale, 50), w => w.Contains("50%") && w.Contains("100%"));
    }

    [Fact]
    public void Проверка_Применённого_Ловит_Нераспознанное_Разрешение()
    {
        var warnings = AppliedCheck.Warnings(Make(50, "?"), 50);

        Assert.Contains(warnings, w => w.Contains("не удалось разобрать разрешение"));
    }

    [Fact]
    public void Проверка_Применённого_Ловит_Неделящее_Окно()
    {
        var warnings = AppliedCheck.Warnings(Make(50, "1921 × 1081"), 50);

        Assert.NotEmpty(warnings);
    }

    private static AppliedSettings Make(int scale, string resolution) =>
        new(
            ScreenResolution: resolution,
            ScreenMode: 1,
            QualityLevel: 1,
            RenderScalePercent: scale,
            ViewDistance: null,
            AntiAliasing: null,
            PostProcessing: null,
            ShadowQuality: null,
            TextureQuality: null,
            MaterialQuality: null,
            VegetationQuality: null,
            MotionBlur: null,
            RayTracing: 0,
            Upscaler: 0,
            FrameGeneration: 0,
            DirectX12: 0);


    [Fact]
    public void Render_Height_Берётся_Из_Параметра_И_Не_Требует_Экрана()
    {
        var options = WukongBenchRunner.Cli.CommandLineOptions.Parse(["run", "--render-height", "1440"]);

        Assert.Equal("1440", options.RenderHeight);
        Assert.Equal("720", ProfileCatalog.Cpu(screenHeight: 1440).Settings.RenderHeight.ToString());
        Assert.Equal("1440", ProfileCatalog.Gpu(screenHeight: 1440).Settings.RenderHeight.ToString());
    }

    [Fact]
    public void Ноль_Пикселей_Не_Считается_Валидной_Высотой()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphicsSettings.RenderHeightFor(0, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphicsSettings.RenderHeightFor(-1080, 50));
        Assert.Equal("1", GraphicsSettings.RenderHeightFor(100, 1).ToString());
    }

    private string WriteConfig()
    {
        var configPath = Path.Combine(_root, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "исходное");
        return configPath;
    }

    [Fact]
    public void Dri3_Определяется_По_Выводу_Xdpyinfo()
    {
        const string withDri3 = "number of extensions: 42\n    DRI3\n    GLX\n    present\n";
        const string withoutDri3 = "number of extensions: 40\n    GLX\n    present\n";

        Assert.True(GraphicsDiagnostics.ParseHasExtension(withDri3, "DRI3"));
        Assert.False(GraphicsDiagnostics.ParseHasExtension(withoutDri3, "DRI3"));
        Assert.False(GraphicsDiagnostics.ParseHasExtension(null, "DRI3"));
    }

    [Fact]
    public void Без_Dri3_Предупреждение_Показывается_Даже_Если_Steam_Не_Запущен()
    {
        var notStarted = new GraphicsDiagnostics(false, true, ":0", ["card1"], true, SteamRunning: false);

        Assert.True(notStarted.Blocked);
        Assert.NotNull(notStarted.Warning);
    }

    [Fact]
    public void Без_Dri3_Показывается_Предупреждение_Про_Код_10()
    {
        var broken = new GraphicsDiagnostics(false, true, ":0", ["card1"], true, false);
        var healthy = new GraphicsDiagnostics(true, true, ":0", ["card1"], true, false);

        Assert.True(broken.Blocked);
        Assert.False(healthy.Blocked);
        Assert.Null(healthy.Warning);
        Assert.Contains("DRI3", broken.Warning!);
        Assert.Contains("10", broken.Warning!);
        Assert.Contains("нет", broken.Summary);
        Assert.Contains("есть", healthy.Summary);
    }

    [Fact]
    public void Диагностика_Графики_Собирается_Через_Подменённый_Источник()
    {
        var probe = new ShellProbe(":0", (_, _) => "number of extensions: 41\n    DRI3\n", _ => true);

        var graphics = GraphicsDiagnostics.Probe(probe);

        Assert.True(graphics.Dri3Available);
        Assert.True(graphics.XTestAvailable);
        Assert.Equal(":0", graphics.Display);
    }

    [Fact]
    public void Пустой_Display_Считается_Отсутствием_Dri3()
    {
        var probe = new ShellProbe("", (_, _) => "    DRI3\n", _ => true);

        var graphics = GraphicsDiagnostics.Probe(probe);

        Assert.False(graphics.Dri3Available);
        Assert.Equal("не задан", graphics.Display);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

internal static class LoadSamplerShim
{
    public static double Percentile(IReadOnlyList<double> sorted, double quantile) =>
        WukongBenchRunner.Results.ResultParser.Percentile(sorted, quantile);

    public static double Median(IReadOnlyList<double> sorted) =>
        WukongBenchRunner.Results.ResultParser.Median(sorted);
}
