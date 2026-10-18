using PZModInspector.Core.Environment;
using PZModInspector.Core.Model;
using PZModInspector.Core.Mods;
using PZModInspector.Core.Workshop;

namespace PZModInspector.Core.Analysis;

/// <summary>One row of the result: a mod in the list, or a requirement the game loads on its own.</summary>
public sealed class ModEntry
{
    public required string Id { get; init; }

    /// <summary>1-based position in mods.txt / default.txt; null for mods loaded only as requirements.</summary>
    public int? ListIndex { get; init; }

    /// <summary>1-based position in the real load order; null when the game does not load the mod.</summary>
    public int? LoadIndex { get; init; }

    /// <summary>The mod that pulls this one in, when it is not in the list.</summary>
    public string? AutoLoadedBy { get; init; }

    public InstalledMod? Mod { get; init; }

    /// <summary>Other folders with the same id that the game does not use.</summary>
    public IReadOnlyList<InstalledMod> OtherCopies { get; init; } = [];

    public ulong? WorkshopId { get; init; }

    /// <summary>Where the Workshop id comes from when the mod is not on disk.</summary>
    public WorkshopIdHint? Hint { get; init; }

    /// <summary>Name from a folder the game ignores (build 41 only or rejected mod.info), when nothing better exists.</summary>
    public string? LegacyName { get; init; }

    public WorkshopInstallInfo? Install { get; init; }
    public WorkshopItemDetails? Workshop { get; init; }

    /// <summary>"Required items" of the Workshop page, with the title Steam gives for each; empty when unknown.</summary>
    public IReadOnlyList<RequiredWorkshopItem> WorkshopRequired { get; init; } = [];

    /// <summary>Loaded mods that list this one in require=.</summary>
    public IReadOnlyList<string> RequiredBy { get; init; } = [];

    public List<Issue> Issues { get; } = [];

    public bool IsLoaded => LoadIndex is not null;

    public Severity? WorstSeverity => Issues.Count == 0 ? null : Issues.Max(i => i.Severity);

    public string DisplayName => Mod?.Name ?? LegacyName ?? Hint?.Name ?? Workshop?.Title ?? Id;
}

public sealed class InspectionResult
{
    public required ModListSource Source { get; init; }
    public required IReadOnlyList<ModEntry> Entries { get; init; }
    public required IReadOnlyList<Issue> GeneralIssues { get; init; }
    public required LoadPlan Plan { get; init; }
    public IReadOnlyList<FileConflict> Conflicts { get; init; } = [];

    public IEnumerable<Issue> AllIssues => GeneralIssues.Concat(Entries.SelectMany(e => e.Issues));

    public ModEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);
}
