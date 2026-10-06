using System.Text;

namespace WukongBenchRunner.Discovery;

public sealed class VdfNode
{
    private readonly Dictionary<string, VdfNode> _children = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Key, string Value)> _pairs = new();

    public string? Value { get; private set; }
    public string? Key { get; private set; }
    public IReadOnlyDictionary<string, VdfNode> Children => _children;
    public IReadOnlyList<(string Key, string Value)> Pairs => _pairs;

    internal VdfNode(string? key) => Key = key;

    internal VdfNode(string key, string value) : this(key) => Value = value;

    internal void SetPair(string key, string value)
    {
        Value ??= value;
        _pairs.Add((key, value));
    }

    internal VdfNode GetOrAddChild(string key)
    {
        if (!_children.TryGetValue(key, out var child))
        {
            child = new VdfNode(key);
            _children[key] = child;
        }

        return child;
    }

    public VdfNode? Child(string key)
    {
        if (_children.TryGetValue(key, out var node))
            return node;

        foreach (var (pairKey, pairValue) in _pairs)
        {
            if (string.Equals(pairKey, key, StringComparison.OrdinalIgnoreCase))
                return new VdfNode(key, pairValue);
        }

        return null;
    }

    public string? ChildValue(string key) => Child(key)?.Value;

    public string? this[string key] => ChildValue(key);

    public IEnumerable<VdfNode> Nodes() => _children.Values;
}

public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode(null);
        var stack = new Stack<VdfNode>();
        var current = root;
        var tokens = Tokenize(text);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.Kind == TokenKind.CloseBrace)
            {
                if (stack.Count > 0)
                    current = stack.Pop();
                continue;
            }

            if (token.Kind == TokenKind.OpenBrace || token.Kind != TokenKind.String || i + 1 >= tokens.Count)
                continue;

            var next = tokens[i + 1];
            if (next.Kind == TokenKind.String)
            {
                current.SetPair(token.Value!, next.Value!);
                i++;
            }
            else if (next.Kind == TokenKind.OpenBrace)
            {
                var child = current.GetOrAddChild(token.Value!);
                stack.Push(current);
                current = child;
                i++;
            }
        }

        return root;
    }

    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    private enum TokenKind
    {
        String,
        OpenBrace,
        CloseBrace,
        Comment,
        Other,
    }

    private readonly record struct Token(TokenKind Kind, string? Value);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '{')
            {
                tokens.Add(new Token(TokenKind.OpenBrace, null));
                i++;
                continue;
            }

            if (c == '}')
            {
                tokens.Add(new Token(TokenKind.CloseBrace, null));
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                tokens.Add(new Token(TokenKind.Comment, null));
                continue;
            }

            if (c == '"')
            {
                var builder = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                        i++;
                    builder.Append(text[i]);
                    i++;
                }

                i++;
                tokens.Add(new Token(TokenKind.String, builder.ToString()));
                continue;
            }

            var bare = new StringBuilder();
            while (i < text.Length && !char.IsWhiteSpace(c: text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"')
            {
                bare.Append(text[i]);
                i++;
            }

            tokens.Add(new Token(TokenKind.Other, bare.ToString()));
        }

        return tokens;
    }
}
