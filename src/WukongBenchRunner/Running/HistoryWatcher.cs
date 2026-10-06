using WukongBenchRunner.Results;

namespace WukongBenchRunner.Running;

public sealed class HistoryWatcher
{
    private readonly string _directory;

    public HistoryWatcher(string directory) => _directory = directory;

    public IReadOnlySet<string> Snapshot() =>
        Directory.Exists(_directory)
            ? Directory.GetFiles(_directory).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public BenchmarkResult? TryFindNew(IReadOnlySet<string> known)
    {
        if (!Directory.Exists(_directory))
            return null;

        var candidates = Directory.GetFiles(_directory)
            .Where(file => !known.Contains(file))
            .OrderByDescending(File.GetLastWriteTimeUtc);

        foreach (var file in candidates)
        {
            try
            {
                return ResultParser.ParseFile(file);
            }
            catch (ResultParseException)
            {
            }
            catch (IOException)
            {
            }
        }

        return null;
    }
}
