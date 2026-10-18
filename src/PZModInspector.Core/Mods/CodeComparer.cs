using System.Text;

namespace PZModInspector.Core.Mods;

/// <summary>
/// Whether two copies of a Lua file or of a script in media/scripts differ only in comments and whitespace: then the game
/// does the same thing whichever copy it uses. Strings are compared exactly; anything unusual counts as a difference.
/// </summary>
public static class CodeComparer
{
    /// <summary>Two-character operators of Lua: a space between their halves must stay ("= =" is not "==").</summary>
    private static readonly HashSet<string> JoiningPairs = new(StringComparer.Ordinal)
    {
        "==", "~=", "<=", ">=", "..", "//", "::", "<<", ">>", "--", "[[", "[=",
    };

    public static bool IsComparable(string path) =>
        path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) || IsScript(path);

    /// <summary>Both files read, normalized and compared; false when one cannot be read.</summary>
    public static bool SameCode(string path, string otherPath)
    {
        try
        {
            var a = Read(path);
            var b = Read(otherPath);
            return path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
                ? NormalizeLua(a) == NormalizeLua(b)
                : NormalizeScript(a) == NormalizeScript(b);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Latin-1 keeps every byte as one char: no decoding that could hide a difference.</summary>
    private static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var start = bytes.AsSpan().StartsWith("﻿"u8) ? 3 : 0;
        return Encoding.Latin1.GetString(bytes, start, bytes.Length - start);
    }

    private static bool IsScript(string path)
    {
        var p = path.Replace('\\', '/');
        return p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && p.Contains("/media/scripts/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Without comments and with only the spaces that change the meaning: between two words ("local x"), between the
    /// halves of an operator, and around a number and a dot. Quoted and long strings are copied as they are.
    /// </summary>
    public static string NormalizeLua(string code)
    {
        var output = new StringBuilder(code.Length);
        var pendingSpace = false;
        var i = 0;

        void Emit(string text)
        {
            if (pendingSpace && output.Length > 0 && NeedsSpace(output[^1], text[0]))
                output.Append(' ');
            pendingSpace = false;
            output.Append(text);
        }

        while (i < code.Length)
        {
            var c = code[i];
            if (c == '-' && i + 1 < code.Length && code[i + 1] == '-')
            {
                // Comment: long (--[==[ ... ]==]) or to the end of the line.
                i += 2;
                if (LongBracketLevel(code, i) is { } level)
                    i = LongBracketEnd(code, i, level);
                else
                {
                    while (i < code.Length && code[i] != '\n')
                        i++;
                }
                pendingSpace = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                i++;
            }
            else if (c is '"' or '\'')
            {
                var start = i++;
                while (i < code.Length && code[i] != c && code[i] != '\n')
                    i += code[i] == '\\' ? 2 : 1;
                i = Math.Min(i + 1, code.Length);
                Emit(code[start..i]);
            }
            else if (c == '[' && LongBracketLevel(code, i) is { } level)
            {
                var end = LongBracketEnd(code, i, level);
                Emit(code[i..end]);
                i = end;
            }
            else
            {
                Emit(c.ToString());
                i++;
            }
        }
        return output.ToString();
    }

    private static bool NeedsSpace(char left, char right) =>
        (IsWord(left) && IsWord(right))
        || JoiningPairs.Contains(new string([left, right]))
        || (char.IsDigit(left) && right == '.')
        || (left == '.' && char.IsDigit(right));

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || c > 127;

    /// <summary>The number of '=' of a long bracket "[==[" starting at <paramref name="i"/>, or null.</summary>
    private static int? LongBracketLevel(string code, int i)
    {
        if (i >= code.Length || code[i] != '[')
            return null;
        var j = i + 1;
        while (j < code.Length && code[j] == '=')
            j++;
        return j < code.Length && code[j] == '[' ? j - i - 1 : null;
    }

    /// <summary>Just after the matching "]==]", or the end of the text when it is missing.</summary>
    private static int LongBracketEnd(string code, int i, int level)
    {
        var close = "]" + new string('=', level) + "]";
        var end = code.IndexOf(close, i + level + 2, StringComparison.Ordinal);
        return end < 0 ? code.Length : end + close.Length;
    }

    /// <summary>
    /// ScriptParser.stripComments (/* */, also nested), then whitespace: runs become one space and go away next to the
    /// characters the parser splits on.
    /// </summary>
    public static string NormalizeScript(string code)
    {
        var text = StripScriptComments(code);
        var output = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace && output.Length > 0 && !IsScriptSeparator(output[^1]) && !IsScriptSeparator(c))
                output.Append(' ');
            pendingSpace = false;
            output.Append(c);
        }
        return output.ToString();
    }

    private static bool IsScriptSeparator(char c) => c is '{' or '}' or '=' or ',';

    /// <summary>The same steps as the game: from the last "*/" back to its "/*", nested comments included.</summary>
    internal static string StripScriptComments(string text)
    {
        var s = text;
        var end = JavaLastIndexOf(s, "*/", s.Length);
        while (end != -1)
        {
            var start = JavaLastIndexOf(s, "/*", end - 1);
            if (start == -1)
                break;
            var innerEnd = JavaLastIndexOf(s, "*/", end - 1);
            while (innerEnd > start)
            {
                var innerStart = start;
                start = JavaLastIndexOf(s, "/*", start - 2);
                if (start == -1)
                    break;
                innerEnd = JavaLastIndexOf(s, "*/", innerStart - 2);
            }
            if (start == -1)
                break;
            s = s.Remove(start, end + 2 - start);
            end = JavaLastIndexOf(s, "*/", start);
        }
        return s;
    }

    /// <summary>Java's lastIndexOf(str, fromIndex): the last match that starts at or before <paramref name="from"/>.</summary>
    private static int JavaLastIndexOf(string s, string value, int from)
    {
        if (from < 0 || s.Length < value.Length)
            return -1;
        var last = Math.Min(from, s.Length - value.Length);
        return s.LastIndexOf(value, last + value.Length - 1, StringComparison.Ordinal);
    }
}
