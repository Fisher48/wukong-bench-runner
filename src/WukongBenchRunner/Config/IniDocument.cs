using System.Text;
using System.Text.RegularExpressions;

namespace WukongBenchRunner.Config;

public sealed class IniDocument
{
    private readonly List<IniLine> _lines = new();

    private IniDocument()
    {
    }

    public static IniDocument Parse(string text)
    {
        var document = new IniDocument();

        var currentSection = string.Empty;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = IniLine.Parse(raw);

            if (line.IsSection)
                currentSection = line.Name ?? string.Empty;
            else
                line.Section = currentSection;

            document._lines.Add(line);
        }

        return document;
    }

    public static IniDocument ParseFile(string path) => Parse(File.ReadAllText(path));

    public string ToText()
    {
        var builder = new StringBuilder();

        foreach (var line in _lines)
        {
            builder.Append(line.IsBlank ? string.Empty : line.Raw);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is { Length: > 0 })
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, ToText(), new UTF8Encoding(false));
    }

    public bool HasSection(string section) => _lines.Any(l => l.IsSection && Matches(l, section));

    public string? Get(string section, string key)
    {
        var matches = _lines.Where(l => l.IsPair && InSection(l, section) && Matches(l, key)).ToList();

        return matches.Count > 0 ? matches[^1].Value : null;
    }

    public IReadOnlyList<string> GetAll(string section, string key) =>
        _lines.Where(l => l.IsPair && InSection(l, section) && Matches(l, key))
            .Select(l => l.Value ?? string.Empty)
            .ToList();

    public void Set(string section, string key, string value)
    {
        var existing = _lines.Where(l => l.IsPair && InSection(l, section) && Matches(l, key)).ToList();
        if (existing.Count > 0)
        {
            foreach (var line in existing)
                line.SetValue(value);

            return;
        }

        var sectionIndex = _lines.FindLastIndex(l => l.IsSection && Matches(l, section));
        if (sectionIndex < 0)
        {
            if (_lines.Count > 0)
                _lines.Add(new IniLine(string.Empty));

            _lines.Add(new IniLine($"[{section}]"));
            _lines.Add(new IniLine($"{key}={value}"));
            return;
        }

        var insertAt = _lines.FindIndex(sectionIndex + 1, l => l.IsSection);
        if (insertAt < 0)
            insertAt = _lines.Count;

        _lines.Insert(insertAt, new IniLine($"{key}={value}"));
    }

    public int Remove(string section, string key)
    {
        var removed = 0;

        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].IsPair && InSection(_lines[i], section) && Matches(_lines[i], key))
            {
                _lines.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }

    private static bool Matches(IniLine line, string name) =>
        string.Equals(line.Name, name, StringComparison.OrdinalIgnoreCase);

    private static bool InSection(IniLine line, string section) =>
        string.Equals(line.Section, section, StringComparison.OrdinalIgnoreCase);

    private sealed class IniLine
    {
        private static readonly Regex SectionPattern = new(@"^\s*\[(?<name>[^\]]+)\]\s*$", RegexOptions.Compiled);
        private static readonly Regex PairPattern = new(@"^\s*(?<name>[^=]+?)\s*=\s*(?<value>.*?)\s*$", RegexOptions.Compiled);

        public IniLine(string raw)
        {
            Raw = raw;
            var section = SectionPattern.Match(raw);
            if (section.Success)
            {
                Kind = LineKind.Section;
                Name = section.Groups["name"].Value.Trim();
                return;
            }

            var pair = PairPattern.Match(raw);
            if (pair.Success)
            {
                Kind = LineKind.Pair;
                Name = pair.Groups["name"].Value;
                Value = pair.Groups["value"].Value;
                return;
            }

            Kind = string.IsNullOrWhiteSpace(raw) ? LineKind.Blank : LineKind.Comment;
        }

        public LineKind Kind { get; }
        public string Raw { get; private set; }
        public string? Name { get; }
        public string? Section { get; set; }
        public string? Value { get; private set; }
        public bool IsSection => Kind == LineKind.Section;
        public bool IsPair => Kind == LineKind.Pair;
        public bool IsBlank => Kind == LineKind.Blank;

        public static IniLine Parse(string raw) => new(raw);

        public void SetValue(string value)
        {
            Raw = $"{Name}={value}";
            Value = value;
        }
    }

    private enum LineKind
    {
        Blank,
        Comment,
        Section,
        Pair,
    }
}
