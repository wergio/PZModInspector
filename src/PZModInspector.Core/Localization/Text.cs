using System.Globalization;
using System.Text.RegularExpressions;
using PZModInspector.Core.Environment;
using PZModInspector.Core.Model;
using PZModInspector.Core.Mods;

namespace PZModInspector.Core.Localization;

/// <summary>Turns Core data (issues, dates, sizes, enums) into localized text.</summary>
public static partial class Text
{
    private static Loc L => Loc.Instance;

    public static string Issue(Issue issue)
    {
        var key = "Issue." + issue.Code;
        var args = issue.Args.Select(FormatArg).Cast<object?>().ToArray();

        // Variants for optional arguments.
        if (issue.Code is IssueCode.ModNotInstalled && issue.Args is [""])
            key += ".NoHint";
        else if (issue.Code is IssueCode.RequirementMissing && issue.Args is [_, ""])
            key += ".NoHint";
        else if (issue.Code is IssueCode.ModInfoInvalid && issue.Args.Count > 0)
            args[0] = L["ModInfoError." + issue.Args[0]];
        else if (issue.Code is IssueCode.WorkshopItemBanned && issue.Args is [_, ""])
            key += ".NoReason";
        else if (issue.Code is IssueCode.FilesOverridden && issue.Args is ["1", ..])
            key += ".One";

        return string.Format(L.Culture, L[key], args);
    }

    /// <summary>Why the program closes: no game, unreadable version or a version before 42.</summary>
    public static string GameProblem(PzEnvironment env) => env.GameProblem switch
    {
        Core.Environment.GameProblem.VersionUnreadable => L.Format("Startup.VersionUnreadable", env.GameJarPath),
        Core.Environment.GameProblem.VersionTooOld => L.Format("Startup.VersionTooOld", env.GameVersion),
        _ => L["Startup.GameNotFound"],
    };

    public static string Severity(Severity? severity) => L["Severity." + (severity?.ToString() ?? "Ok")];

    /// <summary>The path; for a translation also the keys that get another text, the first few.</summary>
    public static string ConflictFile(FileConflict conflict)
    {
        if (conflict.Keys is not { Count: > 0 } keys)
            return conflict.RelativePath;
        const int shown = 4;
        var list = string.Join(", ", keys.Take(shown)) + (keys.Count > shown ? ", " + L.Format("Conflicts.MoreKeys", keys.Count - shown) : "");
        return L.Format("Conflicts.Keys", conflict.RelativePath, list);
    }

    public static string SourceTitle(ModListSource source) =>
        source.Kind == ModListSourceKind.DefaultMods ? L["Source.Default"] : $"{source.Mode} / {source.SaveName}";

    public static string Origin(ModOrigin? origin) => L["Origin." + (origin?.ToString() ?? "Missing")];

    public static string Date(DateTimeOffset? value) =>
        value is { } v ? v.ToLocalTime().ToString("g", L.Culture) : L["Common.Unknown"];

    public static string Number(long? value) => value is { } v ? v.ToString("N0", L.Culture) : L["Common.Unknown"];

    public static string Size(long? bytes)
    {
        if (bytes is not { } b)
            return L["Common.Unknown"];
        string[] units = ["B", "KB", "MB", "GB"];
        double size = b;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size.ToString(unit == 0 ? "0" : "0.0", L.Culture)} {units[unit]}";
    }

    public static string Visibility(int? visibility) =>
        visibility is { } v && L.Has($"Visibility.{v}") ? L[$"Visibility.{v}"] : L["Common.Unknown"];

    /// <summary>Workshop descriptions use BBCode; keep the text and the line breaks.</summary>
    public static string StripBbCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var s = BbUrlRegex().Replace(text, "$2 ($1)");
        s = BbTagRegex().Replace(s, "");
        return MultiNewlineRegex().Replace(s.Replace("\r\n", "\n"), "\n\n").Trim();
    }

    private static string FormatArg(string arg)
    {
        if (arg.Length == 0)
            return L["Common.Empty"];
        if (arg.Length >= 20 && arg[4] == '-' && DateTimeOffset.TryParseExact(arg, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return Date(date);
        return arg;
    }

    [GeneratedRegex(@"\[url=([^\]]+)\](.*?)\[/url\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BbUrlRegex();

    [GeneratedRegex(@"\[/?[a-z0-9*]+(=[^\]]*)?\]", RegexOptions.IgnoreCase)]
    private static partial Regex BbTagRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();
}
