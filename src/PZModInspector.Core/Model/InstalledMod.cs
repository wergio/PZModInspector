namespace PZModInspector.Core.Model;

/// <summary>Where the game finds a mod folder; the order matches ZomboidFileSystem.getAllModFolders ("workshop,steam,mods").</summary>
public enum ModOrigin
{
    /// <summary>Zomboid\Workshop\&lt;item&gt;\Contents\mods: items you are preparing for upload.</summary>
    WorkshopStaged,

    /// <summary>steamapps\workshop\content\108600\&lt;id&gt;\mods: subscribed Workshop items.</summary>
    SteamWorkshop,

    /// <summary>Zomboid\mods: mods copied by hand.</summary>
    LocalMods,
}

/// <summary>A mod folder the game would list, with the version folder it would use.</summary>
public sealed class InstalledMod
{
    public required string Id { get; init; }
    public required string FolderName { get; init; }
    public required string Directory { get; init; }
    public required string CommonDirectory { get; init; }
    public required string VersionDirectory { get; init; }
    public required string VersionFolderName { get; init; }
    public required string ModInfoPath { get; init; }
    public required ModOrigin Origin { get; init; }
    public required ModInfoData Info { get; init; }

    /// <summary>Position in the game's folder scan; when two folders declare the same id the lower one wins.</summary>
    public required int ScanOrder { get; init; }

    public ulong? WorkshopId { get; init; }

    public bool HasCommonFolder => System.IO.Directory.Exists(CommonDirectory);
    public bool HasVersionFolder => System.IO.Directory.Exists(VersionDirectory);

    public string Name => Info.Name;

    public override string ToString() => $"{Id} ({Directory})";
}

/// <summary>A folder under a "mods" directory that the game skips (no usable mod.info for the installed game).</summary>
public sealed record IgnoredModFolder(string Directory, ModOrigin Origin, ulong? WorkshopId, string? LegacyId, string? LegacyName)
{
    /// <summary>True when the folder has a mod.info only for earlier versions of the game (root or older version folders).</summary>
    public bool IsForOlderVersions => LegacyId is not null;
}
