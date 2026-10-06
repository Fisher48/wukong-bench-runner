using System.Globalization;

namespace WukongBenchRunner.Config;

public sealed class SettingsBackup : IDisposable
{
    private const string MarkerName = "config-restore.pending";

    private readonly string _originalPath;
    private readonly string _backupPath;
    private readonly string _markerPath;
    private bool _restored;

    private SettingsBackup(string originalPath, string backupPath, string markerPath)
    {
        _originalPath = originalPath;
        _backupPath = backupPath;
        _markerPath = markerPath;
    }

    public string BackupPath => _backupPath;

    public static SettingsBackup Take(string configPath, string workDirectory)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException(
                $"Не найден конфиг бенчмарка: {configPath}" + Environment.NewLine +
                "Запустите Benchmark Tool вручную хотя бы один раз, чтобы он создал настройки, и закройте его.",
                configPath);

        var backupDirectory = Path.Combine(workDirectory, "backup");
        Directory.CreateDirectory(backupDirectory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(backupDirectory, $"GameUserSettings.{stamp}.{Environment.ProcessId}.ini");
        File.Copy(configPath, backupPath, overwrite: true);

        var markerPath = Path.Combine(workDirectory, MarkerName);
        File.WriteAllText(markerPath, configPath + Environment.NewLine + backupPath);

        return new SettingsBackup(configPath, backupPath, markerPath);
    }

    public static bool TryRestorePending(string workDirectory)
    {
        var markerPath = Path.Combine(workDirectory, MarkerName);
        if (!File.Exists(markerPath))
            return false;

        var lines = File.ReadAllLines(markerPath);
        if (lines.Length < 2)
        {
            File.Delete(markerPath);
            return false;
        }

        try
        {
            RestoreFile(lines[0].Trim(), lines[1].Trim());
        }
        catch (IOException)
        {
            return false;
        }

        File.Delete(markerPath);
        return true;
    }

    public void Restore()
    {
        if (_restored)
            return;

        RestoreFile(_originalPath, _backupPath);
        if (File.Exists(_markerPath))
            File.Delete(_markerPath);

        _restored = true;
    }

    public bool IsRestored => File.Exists(_backupPath) && SameContent(_originalPath, _backupPath);

    public void RestoreAndVerify(int attempts = 3, int delayMs = 1500)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            Restore();

            if (IsRestored)
                return;

            _restored = false;
            Thread.Sleep(delayMs);
        }

        throw new IOException(
            $"Не удалось вернуть исходный GameUserSettings.ini ({_originalPath}): после восстановления файл отличается от копии.");
    }

    public void MarkKept()
    {
        if (File.Exists(_markerPath))
            File.Delete(_markerPath);

        _restored = true;
    }

    public void Dispose()
    {
        if (!_restored)
            Restore();
    }

    private static bool SameContent(string originalPath, string backupPath)
    {
        try
        {
            return File.Exists(originalPath)
                   && string.Equals(File.ReadAllText(originalPath), File.ReadAllText(backupPath), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RestoreFile(string originalPath, string backupPath)
    {
        if (!File.Exists(backupPath))
            throw new IOException(
                $"Резервная копия настроек пропала: {backupPath}. Исходный GameUserSettings.ini не восстановлен.");

        var directory = Path.GetDirectoryName(originalPath);
        if (directory is { Length: > 0 })
            Directory.CreateDirectory(directory);

        File.Copy(backupPath, originalPath, overwrite: true);
    }
}
