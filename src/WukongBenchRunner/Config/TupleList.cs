using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WukongBenchRunner.Config;

public static class TupleList
{
    private static readonly Regex ItemPattern = new(
        "\"(?<key>[^\"]*)\"\\s*,\\s*\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled);

    public static IReadOnlyList<(string Key, string Value)> Parse(string raw) =>
        ItemPattern.Matches(raw)
            .Select(m => (m.Groups["key"].Value, m.Groups["value"].Value))
            .ToList();

    public static string Build(IEnumerable<(string Key, string Value)> items)
    {
        var builder = new StringBuilder("(");
        var first = true;

        foreach (var (key, value) in items)
        {
            if (!first)
                builder.Append(',');

            builder.Append(CultureInfo.InvariantCulture, $"(\"{key}\", \"{value}\")");
            first = false;
        }

        return builder.Append(')').ToString();
    }

    public static string Upsert(string raw, IReadOnlyDictionary<string, string> updates)
    {
        var items = Parse(raw).ToList();

        foreach (var (key, value) in updates)
        {
            var index = items.FindIndex(i => string.Equals(i.Key, key, StringComparison.Ordinal));
            if (index >= 0)
                items[index] = (key, value);
            else
                items.Add((key, value));
        }

        return Build(items);
    }
}
