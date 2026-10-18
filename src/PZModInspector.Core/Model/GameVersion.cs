using System.Globalization;
using System.Text.RegularExpressions;

namespace PZModInspector.Core.Model;

/// <summary>
/// Game version as Project Zomboid reads it (zombie.core.GameVersion): "major.minor" followed by
/// a free suffix. Comparisons only use major * 1000 + minor, exactly like the game.
/// </summary>
public readonly partial record struct GameVersion(int Major, int Minor, string Suffix) : IComparable<GameVersion>
{
    /// <summary>ChooseGameInfo.minRequiredVersion: version folders below this are never picked.</summary>
    public static readonly GameVersion MinRequired = new(42, 0, "");

    public int ToInt() => Major * 1000 + Minor;

    /// <summary>
    /// The lowest version folder a mod can use with this game: "major.0", never below <see cref="MinRequired"/>.
    /// Folders for earlier major versions are for an older game, like the mod.info in the root was for Build 41.
    /// </summary>
    public GameVersion FirstUsableFolder => Major > MinRequired.Major ? new GameVersion(Major, 0, "") : MinRequired;

    public int CompareTo(GameVersion other) => ToInt().CompareTo(other.ToInt());

    public static bool operator <(GameVersion a, GameVersion b) => a.ToInt() < b.ToInt();
    public static bool operator >(GameVersion a, GameVersion b) => a.ToInt() > b.ToInt();
    public static bool operator <=(GameVersion a, GameVersion b) => a.ToInt() <= b.ToInt();
    public static bool operator >=(GameVersion a, GameVersion b) => a.ToInt() >= b.ToInt();

    /// <summary>Same rules as GameVersion.parse: the whole string must match "digits.digits(anything)".</summary>
    public static bool TryParse(string? text, out GameVersion version)
    {
        version = default;
        if (text is null)
            return false;
        var match = VersionPattern().Match(text);
        if (!match.Success)
            return false;
        version = new GameVersion(TryParseInt(match.Groups[1].Value), TryParseInt(match.Groups[2].Value), match.Groups[3].Value);
        return true;
    }

    public static GameVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"invalid game version \"{text}\"");

    /// <summary>
    /// ZomboidFileSystem.getGameVersionIntFromName: "42" -> 42000, "42.13" -> 42013, "42.13.1" -> 42013,
    /// anything that is not a number (e.g. "common") -> 0.
    /// </summary>
    public static int FolderNameToInt(string? name)
    {
        if (name is null)
            return 0;
        var parts = JavaSplit(name, '.');
        return parts.Count switch
        {
            0 => 0,
            1 => TryParseInt(parts[0]) * 1000,
            _ => TryParseInt(parts[0]) * 1000 + Math.Min(TryParseInt(parts[1]), 999),
        };
    }

    public override string ToString() => $"{Major}.{Minor}{Suffix}";

    public string ToShortString() => $"{Major}.{Minor}";

    /// <summary>PZMath.tryParseInt: Integer.parseInt or the default (0) on failure.</summary>
    internal static int TryParseInt(string s) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>Java String.split semantics for a single-char separator: trailing empty strings are dropped.</summary>
    internal static List<string> JavaSplit(string s, char separator)
    {
        var parts = s.Split(separator).ToList();
        while (parts.Count > 0 && parts[^1].Length == 0)
            parts.RemoveAt(parts.Count - 1);
        if (parts.Count == 0 && s.Length == 0)
            parts.Add("");
        return parts;
    }

    [GeneratedRegex(@"^([0-9]+)\.([0-9]+)(.*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
