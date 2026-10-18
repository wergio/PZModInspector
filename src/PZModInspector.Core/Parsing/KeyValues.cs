using System.Text;

namespace PZModInspector.Core.Parsing;

/// <summary>A node of a Valve KeyValues text file (libraryfolders.vdf, *.acf, localconfig.vdf).</summary>
public sealed class KvNode
{
    public KvNode(string key, string? value = null)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }
    public string? Value { get; }
    public List<KvNode> Children { get; } = [];

    public KvNode? this[string key] =>
        Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public string? GetString(string key) => this[key]?.Value;

    public long? GetLong(string key) => long.TryParse(GetString(key), out var n) ? n : null;
}

public static class KeyValues
{
    public static KvNode? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Parses the whole text; the returned node is a synthetic root whose children are the top-level keys.</summary>
    public static KvNode Parse(string text)
    {
        var tokens = Tokenize(text).ToList();
        var pos = 0;
        var root = new KvNode("");
        ParseChildren(tokens, ref pos, root);
        return root;
    }

    private enum TokenKind { String, Open, Close, Conditional }

    private readonly record struct Token(TokenKind Kind, string Text);

    private static void ParseChildren(List<Token> tokens, ref int pos, KvNode parent)
    {
        while (pos < tokens.Count)
        {
            var token = tokens[pos++];
            if (token.Kind == TokenKind.Close)
                return;
            if (token.Kind != TokenKind.String)
                continue;

            if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Open)
            {
                pos++;
                var node = new KvNode(token.Text);
                ParseChildren(tokens, ref pos, node);
                parent.Children.Add(node);
            }
            else if (pos < tokens.Count && tokens[pos].Kind == TokenKind.String)
            {
                parent.Children.Add(new KvNode(token.Text, tokens[pos++].Text));
            }
            if (pos < tokens.Count && tokens[pos].Kind == TokenKind.Conditional)
                pos++;
        }
    }

    private static IEnumerable<Token> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
            }
            else if (c == '{')
            {
                i++;
                yield return new Token(TokenKind.Open, "{");
            }
            else if (c == '}')
            {
                i++;
                yield return new Token(TokenKind.Close, "}");
            }
            else if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] });
                    }
                    else
                    {
                        sb.Append(text[i]);
                    }
                    i++;
                }
                i++;
                yield return new Token(TokenKind.String, sb.ToString());
            }
            else if (c == '[')
            {
                var end = text.IndexOf(']', i);
                end = end < 0 ? text.Length - 1 : end;
                yield return new Token(TokenKind.Conditional, text[i..(end + 1)]);
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
                    i++;
                yield return new Token(TokenKind.String, text[start..i]);
            }
        }
    }
}
