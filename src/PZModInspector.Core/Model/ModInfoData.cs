namespace PZModInspector.Core.Model;

public enum ModInfoErrorKind
{
    ReadError,
    InvalidVersionMin,
    InvalidVersionMax,
    EmptyPack,
    InvalidPack,
    InvalidTileDef,
}

/// <summary>A reason for which the game discards the whole mod.info (ChooseGameInfo.readModInfoAux returns null).</summary>
public sealed record ModInfoError(ModInfoErrorKind Kind, string Detail);

/// <summary>Content of a mod.info file, read with the same rules as ChooseGameInfo.readModInfoAux.</summary>
public sealed class ModInfoData
{
    public string Id { get; set; } = "undefined_id";
    public string Name { get; set; } = "Unnamed Mod";
    public string Description { get; set; } = "";
    public string? Author { get; set; }
    public string? ModVersion { get; set; }
    public string? Category { get; set; }
    public string? Url { get; set; }
    public string? IconPath { get; set; }
    public List<string> PosterPaths { get; } = [];

    /// <summary>Null when the key is missing, like the Java fields. Items are kept untrimmed on purpose.</summary>
    public IReadOnlyList<string>? Require { get; set; }
    public IReadOnlyList<string>? Incompatible { get; set; }
    public IReadOnlyList<string>? LoadAfter { get; set; }
    public IReadOnlyList<string>? LoadBefore { get; set; }

    public GameVersion? VersionMin { get; set; }
    public GameVersion? VersionMax { get; set; }
    public List<string> Packs { get; } = [];
    public List<string> TileDefs { get; } = [];

    /// <summary>Every "key=value" line as written, including keys the game ignores (javaJarFile, modVersion, ...).</summary>
    public List<KeyValuePair<string, string>> RawEntries { get; } = [];

    public List<ModInfoError> Errors { get; } = [];

    public bool IsValid => Errors.Count == 0;

    public string? GetRaw(string key) =>
        RawEntries.LastOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Mods loaded through ZombieBuddy declare their jar here.</summary>
    public string? JavaJarFile => GetRaw("javaJarFile");

    /// <summary>ChooseGameInfo.Mod.isAvailableSelf.</summary>
    public bool IsAvailableFor(GameVersion gameVersion) =>
        !(VersionMin is { } min && min > gameVersion) && !(VersionMax is { } max && max < gameVersion);

    public static IEnumerable<string> Trimmed(IReadOnlyList<string>? ids) =>
        ids is null ? [] : ids.Select(i => i.Trim()).Where(i => i.Length > 0);
}
