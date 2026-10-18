using System.Globalization;
using PZModInspector.Core.Environment;
using PZModInspector.Core.Model;
using PZModInspector.Core.Mods;
using PZModInspector.Core.Parsing;
using PZModInspector.Core.Workshop;

namespace PZModInspector.Core.Analysis;

public sealed record AnalysisInput
{
    public required PzEnvironment Environment { get; init; }
    public required ModCatalog Catalog { get; init; }
    public required WorkshopManifest Manifest { get; init; }
    public required ModListSource Source { get; init; }
    public required ActiveModsFileData? ModsFile { get; init; }
    public Func<ulong, WorkshopItemDetails?> Details { get; init; } = _ => null;
    public IReadOnlyDictionary<string, WorkshopIdHint> Hints { get; init; } = new Dictionary<string, WorkshopIdHint>();
    public IReadOnlyList<FileConflict>? Conflicts { get; init; }
    public bool WorkshopQueryFailed { get; init; }
}

/// <summary>Turns the load plan and the Workshop data into the problems shown to the user.</summary>
public static class ModAnalyzer
{
    public const string ZombieBuddyId = "ZombieBuddy";

    /// <summary>Dates travel as round-trip strings in <see cref="Issue.Args"/>; the UI formats them.</summary>
    public static string DateArg(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static LoadPlan Plan(AnalysisInput input) =>
        new LoadOrderResolver(input.Catalog, input.Environment.GameVersion).Resolve(input.ModsFile?.Mods ?? []);

    public static InspectionResult Analyze(AnalysisInput input, LoadPlan? plan = null)
    {
        var env = input.Environment;
        var catalog = input.Catalog;
        var resolver = new LoadOrderResolver(catalog, env.GameVersion);
        plan ??= resolver.Resolve(input.ModsFile?.Mods ?? []);

        var general = new List<Issue>();
        if (input.ModsFile is null)
            general.Add(Issue.Of(Severity.Error, IssueCode.ModsFileMissing, null, input.Source.ModsFilePath));
        else if (!input.ModsFile.IsVersionSupported)
            general.Add(Issue.Of(Severity.Error, IssueCode.ModsFileVersionUnsupported, null, input.ModsFile.Version?.ToString(CultureInfo.InvariantCulture) ?? "?"));
        if (input.WorkshopQueryFailed)
            general.Add(Issue.Of(Severity.Warning, IssueCode.WorkshopQueryFailed, null));

        // Who requires whom, among the loaded mods.
        var requiredBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var loaded in plan.Loaded)
        {
            foreach (var req in ModInfoData.Trimmed(loaded.Mod.Info.Require))
            {
                if (!requiredBy.TryGetValue(req, out var list))
                    requiredBy[req] = list = [];
                if (!list.Contains(loaded.Id))
                    list.Add(loaded.Id);
            }
        }

        var entries = new List<ModEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var listed = input.ModsFile?.Mods ?? [];
        for (var i = 0; i < listed.Count; i++)
        {
            var id = listed[i];
            if (!seen.Add(id))
            {
                entries.First(e => e.Id == id).Issues.Add(Issue.Of(Severity.Info, IssueCode.ListedTwice, id, (i + 1).ToString(CultureInfo.InvariantCulture)));
                continue;
            }
            entries.Add(CreateEntry(input, plan, id, i + 1, null, requiredBy));
        }
        foreach (var auto in plan.Loaded.Where(l => !l.Listed && seen.Add(l.Id)))
            entries.Add(CreateEntry(input, plan, auto.Id, null, auto.RequiredBy, requiredBy));

        var reportedItems = new HashSet<ulong>();
        foreach (var entry in entries)
        {
            CheckMod(entry, input, resolver, plan);
            if (entry.WorkshopId is { } wid && reportedItems.Add(wid))
                CheckWorkshop(entry, input, plan, entries);
        }
        if (input.Conflicts is { } conflicts)
            AddConflictIssues(entries, conflicts);

        return new InspectionResult
        {
            Source = input.Source,
            Entries = entries,
            GeneralIssues = general,
            Plan = plan,
            Conflicts = input.Conflicts ?? [],
        };
    }

    private static ModEntry CreateEntry(AnalysisInput input, LoadPlan plan, string id, int? listIndex, string? autoLoadedBy,
        Dictionary<string, List<string>> requiredBy)
    {
        var all = input.Catalog.FindAll(id);
        var mod = all.FirstOrDefault();
        var hint = mod is null ? input.Hints.GetValueOrDefault(id) : null;
        var invalid = mod is null ? input.Catalog.FindInvalid(id).FirstOrDefault() : null;
        var legacy = mod is null && invalid is null ? input.Catalog.FindLegacy(id) : null;
        var wid = mod?.WorkshopId ?? invalid?.WorkshopId ?? legacy?.WorkshopId ?? hint?.WorkshopId;
        var details = wid is { } w2 ? input.Details(w2) : null;
        return new ModEntry
        {
            Id = id,
            ListIndex = listIndex,
            LoadIndex = plan.Find(id)?.LoadIndex,
            AutoLoadedBy = autoLoadedBy,
            Mod = mod,
            OtherCopies = all.Skip(1).ToList(),
            WorkshopId = wid,
            Hint = hint,
            LegacyName = invalid?.Name ?? legacy?.LegacyName,
            Install = wid is { } w1 ? input.Manifest.Find(w1) : null,
            Workshop = details,
            WorkshopRequired = details?.Children?
                .Select(child => RequiredItem(input, child))
                .ToList() ?? [],
            RequiredBy = requiredBy.GetValueOrDefault(id) ?? [],
        };
    }

    private static RequiredWorkshopItem RequiredItem(AnalysisInput input, ulong id)
    {
        var details = input.Details(id);
        return new RequiredWorkshopItem(id, details?.Title ?? id.ToString(CultureInfo.InvariantCulture),
            WorkshopBuilds.OnlyOlderThan(details, input.Environment.GameVersion.Major));
    }

    private static void CheckMod(ModEntry entry, AnalysisInput input, LoadOrderResolver resolver, LoadPlan plan)
    {
        var env = input.Environment;
        var catalog = input.Catalog;
        var issues = entry.Issues;
        var id = entry.Id;
        var mod = entry.Mod;

        if (mod is null)
        {
            if (catalog.FindInvalid(id).FirstOrDefault() is { } invalid)
            {
                var error = invalid.Info.Errors[0];
                issues.Add(Issue.Of(Severity.Error, IssueCode.ModInfoInvalid, id, error.Kind.ToString(), error.Detail, invalid.ModInfoPath));
            }
            else if (catalog.FindLegacy(id) is { } legacy)
                issues.Add(Issue.Of(Severity.Error, IssueCode.ModOnlyForOlderVersions, id, legacy.Directory, env.GameVersion.Major.ToString(CultureInfo.InvariantCulture)));
            else
                issues.Add(Issue.Of(Severity.Error, IssueCode.ModNotInstalled, id, entry.WorkshopId?.ToString(CultureInfo.InvariantCulture) ?? ""));
            return;
        }

        var info = mod.Info;
        if (info.VersionMin is { } min && min > env.GameVersion)
            issues.Add(Issue.Of(Severity.Error, IssueCode.GameVersionTooOld, id, min.ToString(), env.GameVersion.ToString()));
        if (info.VersionMax is { } max && max < env.GameVersion)
            issues.Add(Issue.Of(Severity.Error, IssueCode.GameVersionTooNew, id, max.ToString(), env.GameVersion.ToString()));

        var requirementProblem = false;
        foreach (var raw in info.Require ?? [])
        {
            var reqId = raw.Trim();
            var dep = catalog.Find(reqId);
            if (dep is null)
            {
                issues.Add(Issue.Of(Severity.Error, IssueCode.RequirementMissing, id, reqId, input.Hints.GetValueOrDefault(reqId)?.WorkshopId.ToString(CultureInfo.InvariantCulture) ?? ""));
                requirementProblem = true;
            }
            else if (!resolver.IsAvailable(dep))
            {
                issues.Add(Issue.Of(Severity.Error, IssueCode.RequirementUnavailable, id, reqId));
                requirementProblem = true;
            }
            else if (raw.Length > 0 && raw != reqId)
            {
                issues.Add(Issue.Of(Severity.Error, IssueCode.RequirementHasSpaces, id, reqId));
                requirementProblem = true;
            }
        }

        // A listed mod skipped because of a requirement deeper in the chain.
        foreach (var failure in plan.Failures.Where(f => f.ListedId == id && f.FailedId != id))
        {
            if (!requirementProblem)
                issues.Add(Issue.Of(Severity.Error, IssueCode.RequirementUnavailable, id, failure.FailedId.Trim()));
        }

        foreach (var cycle in plan.Cycles.Where(c => c.Contains(id)))
            issues.Add(Issue.Of(Severity.Error, IssueCode.RequirementCycle, id, string.Join(" → ", cycle)));

        if (entry.AutoLoadedBy is { } by)
            issues.Add(Issue.Of(Severity.Info, IssueCode.RequirementAutoLoaded, id, by));

        if (entry.OtherCopies.Count > 0)
            issues.Add(Issue.Of(Severity.Warning, IssueCode.DuplicateModId, id, mod.Directory, string.Join("\n", entry.OtherCopies.Select(c => c.Directory))));

        if (mod.Origin == ModOrigin.SteamWorkshop && mod.WorkshopId is { } wid && input.Manifest.Items.Count > 0 && input.Manifest.Find(wid) is null)
            issues.Add(Issue.Of(Severity.Warning, IssueCode.NotInSteamManifest, id, wid.ToString(CultureInfo.InvariantCulture)));

        // With ZombieBuddy there is nothing to report: the jar is among the details of the mod.
        if (info.JavaJarFile is { Length: > 0 } jar && !(env.ZombieBuddyInstalled && plan.Find(ZombieBuddyId) is not null))
            issues.Add(Issue.Of(Severity.Warning, IssueCode.JavaModWithoutZombieBuddy, id, jar));

        if (entry.LoadIndex is not { } loadIndex)
            return;

        foreach (var other in ModInfoData.Trimmed(info.Incompatible))
        {
            if (plan.Find(other) is not null)
                issues.Add(Issue.Of(Severity.Error, IssueCode.IncompatibleModActive, id, other));
        }
        foreach (var after in ModInfoData.Trimmed(info.LoadAfter))
        {
            if (plan.Find(after) is { } o && o.LoadIndex > loadIndex)
                issues.Add(Issue.Of(Severity.Warning, IssueCode.LoadAfterViolated, id, after));
        }
        foreach (var before in ModInfoData.Trimmed(info.LoadBefore))
        {
            if (plan.Find(before) is { } o && o.LoadIndex < loadIndex)
                issues.Add(Issue.Of(Severity.Warning, IssueCode.LoadBeforeViolated, id, before));
        }
    }

    /// <summary>Checks done once per Workshop item, on the first mod of that item.</summary>
    private static void CheckWorkshop(ModEntry entry, AnalysisInput input, LoadPlan plan, List<ModEntry> entries)
    {
        var wid = entry.WorkshopId!.Value;
        var widText = wid.ToString(CultureInfo.InvariantCulture);
        var issues = entry.Issues;
        var details = entry.Workshop;
        var title = details?.Title ?? entry.DisplayName;

        if (details is not null)
        {
            if (!details.IsAvailable)
                issues.Add(Issue.Of(entry.Mod is null ? Severity.Error : Severity.Warning, IssueCode.WorkshopItemRemoved, entry.Id, widText,
                    details.Result.ToString(CultureInfo.InvariantCulture)));
            if (details.Banned)
                issues.Add(Issue.Of(Severity.Warning, IssueCode.WorkshopItemBanned, entry.Id, widText, details.BanReason ?? ""));
            if (details.Visibility is 1 or 2)
                issues.Add(Issue.Of(Severity.Warning, IssueCode.WorkshopItemNotPublic, entry.Id, widText));
        }

        if (entry.Install?.TimeUpdated is { } local)
        {
            var remote = details is { IsAvailable: true, TimeUpdated: { } t } ? t : entry.Install.LatestTimeUpdated;
            if (remote is { } r && r > local)
                issues.Add(Issue.Of(Severity.Warning, IssueCode.UpdatePending, entry.Id, title, DateArg(local), DateArg(r)));

            if (input.Source is { Kind: ModListSourceKind.Save, LastPlayedUtc: { } played } && local > played)
                issues.Add(Issue.Of(Severity.Info, IssueCode.UpdatedSinceLastPlayed, entry.Id, title, DateArg(local), DateArg(played)));
        }

        // Items that publish an older build too (a root folder for Build 41 plus 42.x) list that build's requirements on the
        // Workshop. One only for older majors is not needed when the require= of every mod of the item, read from the version
        // folder the game uses, are loaded: the game finds those dependencies by mod ID, whatever item they come from.
        var requiresLoaded = new Lazy<bool>(() => RequiresLoaded(wid, plan, entries));
        foreach (var required in entry.WorkshopRequired)
        {
            if (input.Catalog.WorkshopItemFolders.Contains(required.Id))
                continue;
            var requiredId = required.Id.ToString(CultureInfo.InvariantCulture);
            if (required.OlderBuilds is { } builds && requiresLoaded.Value)
                issues.Add(Issue.Of(Severity.Info, IssueCode.WorkshopRequiredItemOlderBuild, entry.Id, required.Title, requiredId, title,
                    WorkshopBuilds.Format(builds), input.Environment.GameVersion.Major.ToString(CultureInfo.InvariantCulture)));
            else
                issues.Add(Issue.Of(Severity.Warning, IssueCode.WorkshopRequiredItemMissing, entry.Id, required.Title, requiredId, title));
        }
    }

    /// <summary>Every installed mod of the Workshop item finds all its require= among the mods the game loads.</summary>
    private static bool RequiresLoaded(ulong workshopId, LoadPlan plan, List<ModEntry> entries)
    {
        var mods = entries.Where(e => e.WorkshopId == workshopId && e.Mod is not null).Select(e => e.Mod!).ToList();
        return mods.Count > 0 && mods.All(m => ModInfoData.Trimmed(m.Info.Require).All(r => plan.Find(r) is not null));
    }

    private static void AddConflictIssues(List<ModEntry> entries, IReadOnlyList<FileConflict> conflicts)
    {
        var byLoser = conflicts
            .SelectMany(c => c.Overridden.Select(o => (Loser: o.ModId, Winner: c.Winner.ModId)))
            .GroupBy(x => x);
        foreach (var group in byLoser)
        {
            var entry = entries.FirstOrDefault(e => e.Id == group.Key.Loser);
            var winner = entries.FirstOrDefault(e => e.Id == group.Key.Winner)?.DisplayName ?? group.Key.Winner;
            entry?.Issues.Add(Issue.Of(Severity.Info, IssueCode.FilesOverridden, entry.Id,
                group.Count().ToString(CultureInfo.InvariantCulture), winner, group.Key.Winner));
        }
    }
}
