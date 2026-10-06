namespace WukongBenchRunner.SystemInfo;

public readonly record struct CpuTimes(long Total, long Idle);

public sealed record LoadSample(
    int Samples,
    double? AverageGpuBusyPercent,
    double? PeakGpuBusyPercent,
    double? AverageCpuBusyPercent,
    double? PeakCpuBusyPercent,
    double? LastMinuteGpuBusyPercent,
    double? LastMinuteCpuBusyPercent,
    double? MeasureWindowGpuBusyPercent,
    double? MeasureWindowCpuBusyPercent,
    double WindowSeconds)
{
    public bool Available => AverageGpuBusyPercent is not null || AverageCpuBusyPercent is not null;
}

public sealed class LoadSampler(TimeSpan? interval = null) : IDisposable
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(1);
    private readonly List<(DateTimeOffset At, double? Gpu, double Cpu)> _samples = [];
    private readonly string? _gpuBusyPath = FindGpuBusyPath();
    private CpuTimes? _previousCpu;
    private Thread? _thread;
    private volatile bool _running;

    public bool GpuMetricsAvailable => _gpuBusyPath is not null;

    public void Start()
    {
        _running = true;
        _thread = new Thread(Sample)
        {
            IsBackground = true,
            Name = "load-sampler",
        };

        _thread.Start();
    }

    public LoadSample Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(3));

        if (_samples.Count == 0)
            return new LoadSample(0, null, null, null, null, null, null, null, null, 0);

        var gpu = _samples.Where(s => s.Gpu is not null).Select(s => s.Gpu!.Value).ToList();
        var cpu = _samples.Select(s => s.Cpu).ToList();
        var window = _samples.Where(s => s.At >= DateTimeOffset.UtcNow - TimeSpan.FromSeconds(60)).ToList();

        var measurement = BusiestWindow(TimeSpan.FromSeconds(60));

        return new LoadSample(
            _samples.Count,
            gpu.Count > 0 ? gpu.Average() : null,
            gpu.Count > 0 ? gpu.Max() : null,
            cpu.Average(),
            cpu.Max(),
            Average(window.Select(s => s.Gpu)),
            window.Count > 0 ? window.Average(s => s.Cpu) : null,
            Average(measurement.Select(s => s.Gpu)),
            measurement.Count > 0 ? measurement.Average(s => s.Cpu) : null,
            (_samples[^1].At - _samples[0].At).TotalSeconds);
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(3));
    }

    private List<(DateTimeOffset At, double? Gpu, double Cpu)> BusiestWindow(TimeSpan length)
    {
        if (_samples.Count == 0)
            return [];

        var best = new List<(DateTimeOffset At, double? Gpu, double Cpu)>();
        var start = 0;

        for (var end = 0; end < _samples.Count; end++)
        {
            while (_samples[end].At - _samples[start].At > length && start < end)
                start++;

            var window = _samples.Skip(start).Take(end - start + 1).ToList();
            if (window.Count < best.Count)
                continue;

            var score = window.Average(s => s.Gpu ?? 0) + window.Average(s => s.Cpu) / 10.0;
            var bestScore = best.Count == 0 ? double.NegativeInfinity : best.Average(s => s.Gpu ?? 0) + best.Average(s => s.Cpu) / 10.0;

            if (score > bestScore)
                best = window;
        }

        return best;
    }

    private void Sample()
    {
        var first = true;

        while (_running)
        {
            try
            {
                var cpu = ReadCpuBusyPercent();
                var gpu = ReadGpuBusyPercent();

                if (!first || cpu > 0)
                {
                    _samples.Add((DateTimeOffset.UtcNow, gpu, cpu));
                    first = false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            Thread.Sleep(_interval);
        }
    }

    private double ReadCpuBusyPercent()
    {
        var line = ReadCpuStatLine();
        var current = ParseCpuTimes(line);

        double? busy = null;
        if (_previousCpu is { } previous && current.Total > previous.Total)
        {
            var totalDelta = current.Total - previous.Total;
            var idleDelta = current.Idle - previous.Idle;
            busy = Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
        }

        _previousCpu = current;
        return busy ?? 0.0;
    }

    private double? ReadGpuBusyPercent()
    {
        if (_gpuBusyPath is null)
            return null;

        try
        {
            var text = File.ReadAllText(_gpuBusyPath).Trim();
            return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? Math.Clamp(value, 0, 100)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadCpuStatLine()
    {
        using var stream = File.OpenRead("/proc/stat");
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("cpu ", StringComparison.Ordinal))
                return line;
        }

        return "cpu 0 0 0 0 0 0 0 0";
    }

    public static CpuTimes ParseCpuTimes(string line)
    {
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        long total = 0;
        long idle = 0;

        for (var i = 1; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], out var value))
                continue;

            total += value;

            if (i is 4 or 5)
                idle += value;
        }

        return new CpuTimes(total, idle);
    }

    public static double? CpuBusyPercent(CpuTimes previous, CpuTimes current)
    {
        var totalDelta = current.Total - previous.Total;
        if (totalDelta <= 0)
            return null;

        var idleDelta = current.Idle - previous.Idle;
        return Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var list = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return list.Count > 0 ? list.Average() : null;
    }

    private static string? FindGpuBusyPath()
    {
        var root = "/sys/class/drm";
        if (!Directory.Exists(root))
            return null;

        string[] cards;
        try
        {
            cards = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var card in cards.OrderByDescending(IsPrimaryAdapter).ThenBy(c => c, StringComparer.Ordinal))
        {
            var path = Path.Combine(card, "device", "gpu_busy_percent");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static int IsPrimaryAdapter(string cardDirectory)
    {
        try
        {
            var bootVga = File.ReadAllText(Path.Combine(cardDirectory, "device", "boot_vga")).Trim();
            return bootVga == "1" ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
