using WukongBenchRunner.Discovery;

namespace WukongBenchRunner.Tests;

public sealed class DiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wbr-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void VdfParser_Читает_Вложенные_Секции_И_Значения()
    {
        var node = VdfParser.Parse(File.ReadAllText(Path.Combine("TestData", "libraryfolders.vdf")));

        Assert.Equal("/home/<user>/.local/share/Steam", node.Child("libraryfolders")?.Child("0")?.ChildValue("path"));
        Assert.Equal("7956039040917281782", node.Child("libraryfolders")?.Child("0")?.ChildValue("contentid"));
    }

    [Fact]
    public void VdfParser_Читает_Acf_Манифест()
    {
        var appState = VdfParser.ParseFile(Path.Combine("TestData", "appmanifest_3132990.acf")).Child("AppState");

        Assert.NotNull(appState);
        Assert.Equal("Black Myth: Wukong Benchmark Tool", appState!.ChildValue("name"));
        Assert.Equal("Black Myth Wukong Benchmark Tool", appState.ChildValue("installdir"));
        Assert.Equal("3132990", appState.ChildValue("appid"));
    }

    [Fact]
    public void Locate_Находит_Установку_По_Манифесту_И_Каталог_Результатов()
    {
        var library = Path.Combine(_root, "library");
        var steamRoot = Path.Combine(library, "steamapps", "common");
        var installDir = Path.Combine(steamRoot, "Black Myth Wukong Benchmark Tool");
        Directory.CreateDirectory(Path.Combine(installDir, "b1", "Saved", "Config", "Windows"));

        var history = Path.Combine(
            library, "steamapps", "compatdata", "3132990", "pfx", "drive_c", "users", "steamuser",
            "AppData", "Local", "Temp", "b1", "BenchMarkHistory", "Tool");
        Directory.CreateDirectory(history);

        File.WriteAllText(
            Path.Combine(library, "steamapps", "appmanifest_3132990.acf"),
            "\"AppState\" { \"appid\" \"3132990\" \"installdir\" \"Black Myth Wukong Benchmark Tool\" }");

        File.WriteAllText(
            Path.Combine(library, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{library.Replace("\\", "\\\\")}\" }} }}");

        var installation = SteamLocator.Locate(new InstallationOptions { SteamRoot = library });

        Assert.Equal(installDir, installation.InstallDir);
        Assert.Equal(history, installation.HistoryDir);
        Assert.True(installation.PrefixExists);
        Assert.EndsWith(Path.Combine("Saved", "Config", "Windows", "GameUserSettings.ini"), installation.ConfigPath);
        Assert.False(installation.ConfigExists);
    }

    [Fact]
    public void Locate_Уважает_Явный_InstallDir()
    {
        var installDir = Path.Combine(_root, "custom", "Black Myth Wukong Benchmark Tool");
        Directory.CreateDirectory(installDir);

        var installation = SteamLocator.Locate(new InstallationOptions { InstallDir = installDir });

        Assert.Equal(installDir, installation.InstallDir);
        Assert.Equal(Path.GetFullPath(Path.Combine(installDir, "..", "..")), installation.LibraryRoot);
    }

    [Fact]
    public void HistoryCandidates_Первый_Кандидат_Внутри_Proton_Префикса()
    {
        var prefix = Path.Combine(_root, "steamapps", "compatdata", "3132990");
        var candidates = SteamLocator.HistoryCandidates(Path.Combine(_root, "install"), prefix);

        Assert.Contains(Path.Combine(prefix, "pfx", "drive_c", "users", "steamuser", "AppData", "Local", "Temp", "b1", "BenchMarkHistory", "Tool"), candidates);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
