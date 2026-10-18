using System.Text;

namespace PZModInspector.Core.Parsing;

public sealed class ActiveModsFileData
{
    public int? Version { get; init; }

    /// <summary>ActiveModsFile.fromString ignores everything unless VERSION is exactly 1.</summary>
    public bool IsVersionSupported => Version == 1;

    public List<string> Mods { get; } = [];
    public List<string> Maps { get; } = [];
}

/// <summary>
/// Reads mods.txt / default.txt like zombie.modding.ActiveModsFile: lines are joined without separators,
/// then parsed as a ScriptParser block ("VERSION = 1, mods { mod = A, mod = B, } maps { ... }").
/// </summary>
public static class ActiveModsFile
{
    public static ActiveModsFileData? ReadFile(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return Parse(string.Concat(File.ReadAllLines(path, Encoding.UTF8)));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static ActiveModsFileData Parse(string contents)
    {
        var pos = 0;
        var root = ParseBlock(StripComments(contents), ref pos);

        int? version = null;
        var versionValue = root.Values.FirstOrDefault(v => v.Key == "VERSION").Value;
        if (versionValue is not null && int.TryParse(versionValue.Trim(), out var n))
            version = n;

        var data = new ActiveModsFileData { Version = version };
        if (!data.IsVersionSupported)
            return data;

        if (root.FindBlock("mods") is { } mods)
        {
            foreach (var (key, value) in mods.Values)
            {
                if (!key.Trim().Equals("mod", StringComparison.OrdinalIgnoreCase))
                    continue;
                var id = value.Trim().Replace("\\", "");
                if (!string.IsNullOrWhiteSpace(id))
                    data.Mods.Add(id);
            }
        }
        if (root.FindBlock("maps") is { } maps)
        {
            foreach (var (key, value) in maps.Values)
            {
                if (key.Trim().Equals("map", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                    data.Maps.Add(value.Trim());
            }
        }
        return data;
    }

    private sealed class Block
    {
        public List<KeyValuePair<string, string>> Values { get; } = [];
        public List<(string Header, Block Block)> Children { get; } = [];

        public Block? FindBlock(string type) =>
            Children.FirstOrDefault(c => c.Header.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == type).Block;
    }

    private static Block ParseBlock(string s, ref int pos)
    {
        var block = new Block();
        var token = new StringBuilder();
        while (pos < s.Length)
        {
            var c = s[pos++];
            switch (c)
            {
                case '{':
                    var child = ParseBlock(s, ref pos);
                    block.Children.Add((token.ToString().Trim(), child));
                    token.Clear();
                    break;
                case '}':
                    AddValue(block, token);
                    return block;
                case ',':
                    AddValue(block, token);
                    break;
                default:
                    token.Append(c);
                    break;
            }
        }
        AddValue(block, token);
        return block;
    }

    private static void AddValue(Block block, StringBuilder token)
    {
        var text = token.ToString();
        token.Clear();
        var eq = text.IndexOf('=');
        if (eq < 0)
            return;
        block.Values.Add(new(text[..eq].Trim(), text[(eq + 1)..].Trim()));
    }

    private static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    break;
                i = end + 1;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
