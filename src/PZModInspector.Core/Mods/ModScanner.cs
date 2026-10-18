using PZModInspector.Core.Environment;
using PZModInspector.Core.Model;
using PZModInspector.Core.Parsing;

namespace PZModInspector.Core.Mods;

/// <summary>
/// Lists mod folders like ZomboidFileSystem.getAllModFolders: staged Workshop items, then subscribed Workshop
/// items, then Zomboid\mods. A folder counts only when common\mod.info or &lt;version folder&gt;\mod.info exists.
/// </summary>
public static class ModScanner
{
    public static ModCatalog Scan(PzEnvironment env, IReadOnlyList<ulong>? manifestOrder = null)
    {
        var roots = new List<(string ModsDir, ModOrigin Origin, ulong? WorkshopId)>();

        foreach (var item in SortedDirectories(env.StagedWorkshopDirectory))
            roots.Add((Path.Combine(item, "Contents", "mods"), ModOrigin.WorkshopStaged, null));

        var workshopFolders = new HashSet<ulong>();
        if (env.WorkshopContentDirectory is not null)
        {
            var items = new List<ulong>();
            foreach (var dir in SortedDirectories(env.WorkshopContentDirectory))
            {
                if (ulong.TryParse(Path.GetFileName(dir), out var id))
                    items.Add(id);
            }
            workshopFolders.UnionWith(items);
            foreach (var id in OrderWorkshopItems(items, manifestOrder))
                roots.Add((Path.Combine(env.WorkshopContentDirectory, id.ToString()), ModOrigin.SteamWorkshop, id));
        }

        roots.Add((env.ModsDirectory, ModOrigin.LocalMods, null));

        var mods = new List<InstalledMod>();
        var invalid = new List<InstalledMod>();
        var ignored = new List<IgnoredModFolder>();
        var scanOrder = 0;
        foreach (var (root, origin, workshopId) in roots)
        {
            var modsDir = origin == ModOrigin.SteamWorkshop ? Path.Combine(root, "mods") : root;
            foreach (var modDir in SortedDirectories(modsDir))
            {
                var folderName = Path.GetFileName(modDir);
                if (folderName.Equals("examplemod", StringComparison.OrdinalIgnoreCase))
                    continue;

                var mod = ReadModFolder(modDir, origin, workshopId, env.GameVersion, scanOrder++);
                if (mod is null)
                {
                    // Zomboid\Workshop\ModTemplate: the example the game creates by itself, not worth listing.
                    if (!(origin == ModOrigin.WorkshopStaged && folderName.Equals("ModTemplate", StringComparison.OrdinalIgnoreCase)))
                        ignored.Add(ReadIgnoredFolder(modDir, origin, workshopId, env.GameVersion));
                }
                else if (mod.Info.IsValid)
                    mods.Add(mod);
                else
                    invalid.Add(mod);
            }
        }

        return new ModCatalog(mods, invalid, ignored, workshopFolders);
    }

    /// <summary>
    /// ZomboidFileSystem.getModVersionDirName: the highest numeric folder between the start of the game's major version
    /// (<see cref="GameVersion.FirstUsableFolder"/>, 42.0 for Build 42) and the game version.
    /// </summary>
    public static string GetVersionFolderName(string modDir, GameVersion gameVersion)
    {
        var gameInt = gameVersion.ToInt();
        var first = gameVersion.FirstUsableFolder;
        var best = first.ToInt();
        var bestName = first.ToString();
        foreach (var name in SortedEntryNames(modDir))
        {
            var version = GameVersion.FolderNameToInt(name);
            if (version >= best && version <= gameInt)
            {
                bestName = name;
                best = version;
            }
        }
        return bestName;
    }

    public static InstalledMod? ReadModFolder(string modDir, ModOrigin origin, ulong? workshopId, GameVersion gameVersion, int scanOrder)
    {
        var versionFolder = GetVersionFolderName(modDir, gameVersion);
        var commonDir = Path.Combine(modDir, "common");
        var versionDir = Path.Combine(modDir, versionFolder);
        var versionModInfo = Path.Combine(versionDir, "mod.info");
        var commonModInfo = Path.Combine(commonDir, "mod.info");

        string modInfoPath;
        if (File.Exists(versionModInfo))
            modInfoPath = versionModInfo;
        else if (File.Exists(commonModInfo))
            modInfoPath = commonModInfo;
        else
            return null;

        var info = ModInfoParser.ParseFile(modInfoPath, versionDir, commonDir);
        return new InstalledMod
        {
            Id = info.Id,
            FolderName = Path.GetFileName(modDir),
            Directory = modDir,
            CommonDirectory = commonDir,
            VersionDirectory = versionDir,
            VersionFolderName = versionFolder,
            ModInfoPath = modInfoPath,
            Origin = origin,
            Info = info,
            ScanOrder = scanOrder,
            WorkshopId = workshopId,
        };
    }

    /// <summary>
    /// A mod for an earlier game has its mod.info in the root (Build 41) or only in version folders below
    /// <see cref="GameVersion.FirstUsableFolder"/> (the highest one is read for its id and name).
    /// </summary>
    private static IgnoredModFolder ReadIgnoredFolder(string modDir, ModOrigin origin, ulong? workshopId, GameVersion gameVersion)
    {
        var first = gameVersion.FirstUsableFolder.ToInt();
        var legacyDir = File.Exists(Path.Combine(modDir, "mod.info"))
            ? modDir
            : SortedEntryNames(modDir)
                .Select(name => (Name: name, Version: GameVersion.FolderNameToInt(name)))
                .Where(f => f.Version > 0 && f.Version < first && File.Exists(Path.Combine(modDir, f.Name, "mod.info")))
                .OrderByDescending(f => f.Version)
                .Select(f => Path.Combine(modDir, f.Name))
                .FirstOrDefault();
        if (legacyDir is null)
            return new IgnoredModFolder(modDir, origin, workshopId, null, null);
        var info = ModInfoParser.ParseFile(Path.Combine(legacyDir, "mod.info"), legacyDir, legacyDir);
        return new IgnoredModFolder(modDir, origin, workshopId, info.Id, info.Name);
    }

    /// <summary>
    /// The game asks Steam for the installed items and we cannot know that order; the manifest order is the closest guess.
    /// It only matters when two Workshop items declare the same mod id.
    /// </summary>
    private static IEnumerable<ulong> OrderWorkshopItems(List<ulong> folders, IReadOnlyList<ulong>? manifestOrder)
    {
        var present = folders.ToHashSet();
        var ordered = new List<ulong>();
        if (manifestOrder is not null)
            ordered.AddRange(manifestOrder.Where(present.Contains).Distinct());
        var listed = ordered.ToHashSet();
        ordered.AddRange(folders.Where(id => !listed.Contains(id)).Order());
        return ordered;
    }

    private static IEnumerable<string> SortedDirectories(string path)
    {
        if (!Directory.Exists(path))
            return [];
        return SteamLocator.SafeEnumerateDirectories(path).Order(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SortedEntryNames(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
