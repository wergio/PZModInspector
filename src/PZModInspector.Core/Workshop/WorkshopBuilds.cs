using System.Globalization;
using System.Text.RegularExpressions;
using PZModInspector.Core.Model;

namespace PZModInspector.Core.Workshop;

/// <summary>The game builds a Workshop item says it is for, from its "Build 41", "Build 42"... tags. Only the major version counts.</summary>
public static partial class WorkshopBuilds
{
    /// <summary>The majors in the "Build N" tags, in ascending order; empty when the item has none.</summary>
    public static IReadOnlyList<int> FromTags(IEnumerable<string> tags) =>
        tags.Select(t => BuildTag().Match(t))
            .Where(m => m.Success)
            .Select(m => GameVersion.TryParseInt(m.Groups[1].Value))
            .Distinct().Order().ToList();

    /// <summary>
    /// The majors the item is for, when they all come before <paramref name="gameMajor"/>: e.g. [41] for an item tagged only
    /// "Build 41" with Build 42. Null when the item names this major or a later one (a "Build 41" + "Build 42" item is still
    /// needed), or has no "Build" tag.
    /// </summary>
    public static IReadOnlyList<int>? OnlyOlderThan(WorkshopItemDetails? details, int gameMajor)
    {
        if (details is null)
            return null;
        var majors = FromTags(details.Tags);
        return majors.Count > 0 && majors.All(m => m < gameMajor) ? majors : null;
    }

    public static string Format(IReadOnlyList<int> majors) =>
        string.Join(", ", majors.Select(m => m.ToString(CultureInfo.InvariantCulture)));

    [GeneratedRegex(@"^\s*Build\s*([0-9]{1,4})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildTag();
}
