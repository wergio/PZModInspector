using PZModInspector.Core.Model;

namespace PZModInspector.Core.Mods;

/// <summary>Every mod folder found on disk, indexed the way the game resolves mod ids.</summary>
public sealed class ModCatalog
{
    private readonly Dictionary<string, List<InstalledMod>> _byId;
    private readonly Dictionary<string, List<InstalledMod>> _invalidById;

    public ModCatalog(
        IReadOnlyList<InstalledMod> mods,
        IReadOnlyList<InstalledMod> invalidMods,
        IReadOnlyList<IgnoredModFolder> ignoredFolders,
        IReadOnlySet<ulong> workshopItemFolders)
    {
        Mods = mods;
        InvalidMods = invalidMods;
        IgnoredFolders = ignoredFolders;
        WorkshopItemFolders = workshopItemFolders;
        _byId = Group(mods);
        _invalidById = Group(invalidMods);
        ByWorkshopItem = mods.Concat(invalidMods)
            .Where(m => m.WorkshopId is not null)
            .GroupBy(m => m.WorkshopId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<InstalledMod>)g.OrderBy(m => m.ScanOrder).ToList());
    }

    /// <summary>Folders with a valid mod.info, in the game's scan order.</summary>
    public IReadOnlyList<InstalledMod> Mods { get; }

    /// <summary>Folders whose mod.info the game rejects (bad versionMin, pack, tiledef...): their id is unknown to the game.</summary>
    public IReadOnlyList<InstalledMod> InvalidMods { get; }

    public IReadOnlyList<IgnoredModFolder> IgnoredFolders { get; }

    /// <summary>Workshop item ids that have a folder in steamapps\workshop\content\108600.</summary>
    public IReadOnlySet<ulong> WorkshopItemFolders { get; }

    public IReadOnlyDictionary<ulong, IReadOnlyList<InstalledMod>> ByWorkshopItem { get; }

    /// <summary>The folder the game uses for this id: the first valid one in scan order (ChooseGameInfo.readModInfo).</summary>
    public InstalledMod? Find(string id) => _byId.TryGetValue(id, out var list) ? list[0] : null;

    /// <summary>All valid folders declaring this id; more than one means a duplicate.</summary>
    public IReadOnlyList<InstalledMod> FindAll(string id) => _byId.TryGetValue(id, out var list) ? list : [];

    public IReadOnlyList<InstalledMod> FindInvalid(string id) => _invalidById.TryGetValue(id, out var list) ? list : [];

    public IgnoredModFolder? FindLegacy(string id) =>
        IgnoredFolders.FirstOrDefault(f => string.Equals(f.LegacyId, id, StringComparison.Ordinal));

    private static Dictionary<string, List<InstalledMod>> Group(IEnumerable<InstalledMod> mods) =>
        mods.GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.ScanOrder).ToList(), StringComparer.Ordinal);
}
