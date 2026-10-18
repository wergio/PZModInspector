using PZModInspector.Core.Model;

namespace PZModInspector.Core.Mods;

/// <param name="RequiredBy">Set when the mod is not in the list and the game loads it because another mod requires it.</param>
public sealed record LoadedMod(string Id, InstalledMod Mod, int LoadIndex, bool Listed, string? RequiredBy);

/// <param name="ListedId">The mod in the list that the game skips.</param>
/// <param name="FailedId">The mod that could not be found or is not available: the listed mod itself or one of its requirements.</param>
public sealed record LoadFailure(string ListedId, string FailedId);

public sealed class LoadPlan
{
    public List<LoadedMod> Loaded { get; } = [];
    public List<LoadFailure> Failures { get; } = [];

    /// <summary>Requirement loops: the game would recurse forever on them.</summary>
    public List<IReadOnlyList<string>> Cycles { get; } = [];

    public LoadedMod? Find(string id) => Loaded.FirstOrDefault(m => m.Id == id);
}

/// <summary>
/// Computes the mods the game really loads and in which order (ZomboidFileSystem.loadMods / loadModAndRequired):
/// every mod is preceded by its requirements, even when they are not in mods.txt; a mod whose requirements cannot
/// be loaded is skipped silently.
/// </summary>
public sealed class LoadOrderResolver
{
    private readonly ModCatalog _catalog;
    private readonly GameVersion _gameVersion;
    private readonly Dictionary<string, bool> _availability = new(StringComparer.Ordinal);

    public LoadOrderResolver(ModCatalog catalog, GameVersion gameVersion)
    {
        _catalog = catalog;
        _gameVersion = gameVersion;
    }

    public LoadPlan Resolve(IReadOnlyList<string> listed)
    {
        var plan = new LoadPlan();
        var ordered = new List<string>();
        var listedSet = listed.ToHashSet(StringComparer.Ordinal);
        var stack = new List<string>();

        string? LoadModAndRequired(string modId, string? parent)
        {
            if (modId.Length == 0 || modId.Equals("examplemod", StringComparison.OrdinalIgnoreCase))
                return null;
            if (ordered.Contains(modId))
                return null;
            if (stack.Contains(modId))
            {
                plan.Cycles.Add([.. stack[stack.IndexOf(modId)..], modId]);
                return null;
            }

            var mod = GetAvailable(modId);
            if (mod is null)
                return modId;

            if (mod.Info.Require is { } require)
            {
                stack.Add(modId);
                try
                {
                    // Untrimmed ids, like the game: "require=A, B" looks for " B" here and fails.
                    foreach (var req in require)
                    {
                        if (LoadModAndRequired(req, modId) is { } failed)
                            return failed;
                    }
                }
                finally
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }

            ordered.Add(modId);
            plan.Loaded.Add(new LoadedMod(modId, mod, ordered.Count, listedSet.Contains(modId),
                listedSet.Contains(modId) ? null : parent));
            return null;
        }

        foreach (var id in listed)
        {
            if (LoadModAndRequired(id, null) is { } failed)
                plan.Failures.Add(new LoadFailure(id, failed));
        }
        return plan;
    }

    /// <summary>ChooseGameInfo.getAvailableModDetails.</summary>
    public InstalledMod? GetAvailable(string modId)
    {
        var mod = _catalog.Find(modId);
        return mod is not null && IsAvailable(mod) ? mod : null;
    }

    /// <summary>ChooseGameInfo.Mod.isAvailable: version range ok and every requirement (recursively) installed and in range.</summary>
    public bool IsAvailable(InstalledMod mod)
    {
        if (_availability.TryGetValue(mod.Id, out var cached))
            return cached;
        var available = mod.Info.IsAvailableFor(_gameVersion) && IsAvailableRequired(mod, [mod.Id]);
        _availability[mod.Id] = available;
        return available;
    }

    private bool IsAvailableRequired(InstalledMod mod, List<string> seen)
    {
        if (mod.Info.Require is not { Count: > 0 } require)
            return true;
        foreach (var raw in require)
        {
            var id = raw.Trim();
            if (seen.Contains(id))
                continue;
            seen.Add(id);
            var dep = _catalog.Find(id);
            if (dep is null || !dep.Info.IsAvailableFor(_gameVersion) || !IsAvailableRequired(dep, seen))
                return false;
        }
        return true;
    }
}
