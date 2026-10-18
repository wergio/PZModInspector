using System.Globalization;
using PZModInspector.Core.Analysis;
using PZModInspector.Core.Environment;
using PZModInspector.Core.Model;
using PZModInspector.Core.Mods;
using PZModInspector.Core.Settings;
using PZModInspector.Core.Workshop;

namespace PZModInspector.Core.Localization;

public sealed record InfoRow(string Label, string Value, string? Link = null);

/// <summary>A mod folder the game skips.</summary>
/// <param name="Name">The name in mod.info, or the folder name when there is none.</param>
/// <param name="WorkshopId">The subscribed Workshop item the folder belongs to; null for Zomboid\mods and staged items.</param>
/// <param name="WorkshopTitle">From the Workshop cache, when known.</param>
/// <param name="Note">Set when the Workshop item has mods the game loads too: unsubscribing would remove them.</param>
public sealed record IgnoredFolderRow(string Name, string Id, string Reason, string Directory, ulong? WorkshopId, string? WorkshopTitle, string Note)
{
    public string? WorkshopUrl => WorkshopId is { } id ? WorkshopLinks.Item(id) : null;

    /// <summary>The text of the Workshop link: the title, or the number when the title is not known.</summary>
    public string WorkshopText => WorkshopTitle ?? WorkshopId?.ToString() ?? "";
}

/// <param name="SelectId">Mod to jump to when the row is clicked (null when it is not in the list).</param>
public sealed record DependencyRow(string Id, string Name, string Status, Severity? Severity, string? Link, string? SelectId);

public sealed record DependencyGroup(string Title, IReadOnlyList<DependencyRow> Items);

/// <summary>The localized rows shown for a mod and for the environment, the same in the window and in pzmi.</summary>
public static class DetailRows
{
    private static Loc L => Loc.Instance;

    public static string NameOf(InspectionResult result, ModCatalog catalog, string id) =>
        result.Find(id)?.DisplayName ?? catalog.Find(id)?.Name ?? id;

    public static List<InfoRow> ModInfo(ModEntry entry, InspectionResult result, ModCatalog catalog)
    {
        var rows = new List<InfoRow>();
        var mod = entry.Mod;
        var ws = entry.Workshop;

        // Names in mod.info often start with a space.
        void Add(string key, string? value, string? link = null)
        {
            if (!string.IsNullOrWhiteSpace(value))
                rows.Add(new InfoRow(L[key], value.Trim(), link));
        }

        Add("Info.Name", entry.DisplayName);
        Add("Info.Id", entry.Id);
        Add("Info.Author", mod?.Info.Author);
        // The Web API gives only the author's Steam id: the name is on the profile page.
        if (ws?.CreatorSteamId is { } sid)
            Add("Info.WorkshopAuthor", L["Info.WorkshopAuthor.Profile"], WorkshopLinks.Profile(sid));
        Add("Info.ModVersion", mod?.Info.ModVersion);
        if (mod is not null)
        {
            var (min, max) = (mod.Info.VersionMin, mod.Info.VersionMax);
            Add("Info.GameVersions", min is null && max is null ? L["Info.AnyVersion"] : $"{min?.ToString() ?? "…"} – {max?.ToString() ?? "…"}");
            Add("Info.Category", mod.Info.Category);
            Add("Info.VersionFolder", mod.HasVersionFolder ? $"{mod.VersionFolderName} + common" : "common");
            Add("Info.Origin", Text.Origin(mod.Origin));
            Add("Info.Path", mod.Directory);
            Add("Info.JavaJar", mod.Info.JavaJarFile);
            Add("Info.Url", mod.Info.Url);
        }
        Add("Info.ListIndex", entry.ListIndex?.ToString(L.Culture));
        Add("Info.LoadIndex", entry.LoadIndex?.ToString(L.Culture) ?? L["Status.NotLoaded"]);
        Add("Info.AutoLoadedBy", entry.AutoLoadedBy is { } by ? NameOf(result, catalog, by) : null);

        if (entry.WorkshopId is { } wid)
        {
            Add("Info.WorkshopId", wid.ToString(L.Culture), WorkshopLinks.Item(wid));
            if (entry.Hint is { } hint)
                Add("Info.HintSource", hint.Source);
        }
        if (ws is { IsAvailable: true })
        {
            Add("Info.WorkshopTitle", ws.Title);
            Add("Info.Created", Text.Date(ws.TimeCreated));
            Add("Info.WorkshopUpdated", Text.Date(ws.TimeUpdated), entry.WorkshopId is { } w ? WorkshopLinks.ChangeNotes(w) : null);
        }
        else if (ws is not null)
        {
            Add("Info.WorkshopState", L.Format("Info.WorkshopResult", ws.Result));
        }
        if (entry.Install is { } install)
        {
            Add("Info.InstalledVersion", Text.Date(install.TimeUpdated));
            Add("Info.Size", Text.Size(install.Size ?? ws?.FileSize));
        }
        if (ws is { IsAvailable: true })
        {
            Add("Info.Subscriptions", Text.Number(ws.Subscriptions));
            Add("Info.Favorited", Text.Number(ws.Favorited));
            Add("Info.Views", Text.Number(ws.Views));
            Add("Info.Tags", string.Join(", ", ws.Tags));
            Add("Info.Visibility", Text.Visibility(ws.Visibility));
        }
        return rows;
    }

    public static List<DependencyGroup> Dependencies(ModEntry entry, InspectionResult result, ModCatalog catalog)
    {
        var groups = new List<DependencyGroup>();
        var info = entry.Mod?.Info;

        DependencyRow ModRow(string id, Func<ModEntry?, (string Status, Severity? Severity)> status)
        {
            var other = result.Find(id);
            var (text, severity) = status(other);
            var wid = other?.WorkshopId ?? catalog.Find(id)?.WorkshopId;
            return new DependencyRow(id, NameOf(result, catalog, id), text, severity, wid is { } w ? WorkshopLinks.Item(w) : null, other?.Id);
        }

        (string, Severity?) Required(ModEntry? e) => e switch
        {
            { IsLoaded: true } => (L.Format("DepStatus.Loaded", e.LoadIndex), null),
            { Mod: not null } => (L["DepStatus.InstalledNotLoaded"], Severity.Error),
            _ => (L["DepStatus.Missing"], Severity.Error),
        };

        void AddGroup(string key, IEnumerable<DependencyRow> rows)
        {
            var list = rows.ToList();
            if (list.Count > 0)
                groups.Add(new DependencyGroup(L[key], list));
        }

        AddGroup("Dep.Requires", ModInfoData.Trimmed(info?.Require).Distinct().Select(id => ModRow(id, e =>
            e is null && catalog.Find(id) is not null ? (L["DepStatus.InstalledNotLoaded"], Severity.Error) : Required(e))));
        AddGroup("Dep.RequiredBy", entry.RequiredBy.Select(id => ModRow(id, e => (e?.LoadIndex is { } i ? L.Format("DepStatus.Loaded", i) : "", null))));
        AddGroup("Dep.Incompatible", ModInfoData.Trimmed(info?.Incompatible).Distinct().Select(id => ModRow(id, e =>
            e is { IsLoaded: true } ? (L["DepStatus.Active"], Severity.Error) : (L["DepStatus.NotActive"], null))));
        AddGroup("Dep.LoadAfter", ModInfoData.Trimmed(info?.LoadAfter).Distinct().Select(id => ModRow(id, e =>
            e?.LoadIndex is { } i ? (L.Format("DepStatus.Loaded", i), i > (entry.LoadIndex ?? int.MaxValue) ? Severity.Warning : null) : (L["DepStatus.NotActive"], null))));
        AddGroup("Dep.LoadBefore", ModInfoData.Trimmed(info?.LoadBefore).Distinct().Select(id => ModRow(id, e =>
            e?.LoadIndex is { } i ? (L.Format("DepStatus.Loaded", i), i < (entry.LoadIndex ?? 0) ? Severity.Warning : null) : (L["DepStatus.NotActive"], null))));

        AddGroup("Dep.WorkshopRequired", entry.WorkshopRequired.Select(item =>
        {
            var installed = catalog.WorkshopItemFolders.Contains(item.Id);
            var modOfItem = catalog.ByWorkshopItem.GetValueOrDefault(item.Id)?.Select(m => result.Find(m.Id)).FirstOrDefault(e => e is not null);
            // The Workshop checks are on the first mod of the item.
            var notNeeded = !installed && result.Entries.Where(e => e.WorkshopId == entry.WorkshopId).SelectMany(e => e.Issues)
                .Any(i => i.Code == IssueCode.WorkshopRequiredItemOlderBuild && i.Args[1] == item.Id.ToString(CultureInfo.InvariantCulture));
            return new DependencyRow(item.Id.ToString(L.Culture), item.Title,
                installed ? L["DepStatus.Installed"]
                    : item.OlderBuilds is { } builds ? L.Format("DepStatus.NotSubscribedOlderBuild", WorkshopBuilds.Format(builds))
                    : L["DepStatus.NotSubscribed"],
                installed || notNeeded ? null : Severity.Warning,
                WorkshopLinks.Item(item.Id), modOfItem?.Id);
        }));
        return groups;
    }

    public static List<InfoRow> Environment(Inspector inspector)
    {
        var env = inspector.Environment;
        var catalog = inspector.Catalog;
        var notFound = L["Env.NotFound"];
        var rows = new List<InfoRow> { new(L["Env.GameVersion"], env.GameVersion.ToString()) };
        if (env.GameBuildId is { } build)
            rows.Add(new(L["Env.GameBuild"], $"{build} ({Text.Date(env.GameLastUpdated)})"));
        rows.Add(new(L["Env.Steam"], env.SteamRoot ?? notFound, env.SteamRoot));
        rows.Add(new(L["Env.Game"], env.GameDirectory ?? notFound, env.GameDirectory));
        rows.Add(new(L["Env.Workshop"], env.WorkshopContentDirectory ?? notFound, env.WorkshopContentDirectory));
        rows.Add(new(L["Env.Zomboid"], env.ZomboidDirectory + (env.ZomboidDirectoryFromLaunchOptions ? " (-cachedir)" : ""), env.ZomboidDirectory));
        rows.Add(new(L["Env.ZombieBuddy"], env.ZombieBuddyInstalled ? L["Env.ZombieBuddy.Yes"] : L["Env.ZombieBuddy.No"]));
        rows.Add(new(L["Env.Mods"], L.Format("Env.ModsCount", catalog.Mods.Count, catalog.WorkshopItemFolders.Count,
            catalog.IgnoredFolders.Count + catalog.InvalidMods.Count)));
        rows.Add(new(L["Env.Cache"], AppSettings.DataDirectory, AppSettings.DataDirectory));
        return rows;
    }

    /// <summary>Folders the game skips: only for earlier game versions, no mod.info for this one, or a mod.info it rejects (with the reason).</summary>
    public static List<IgnoredFolderRow> IgnoredFolders(Inspector inspector)
    {
        var catalog = inspector.Catalog;
        var noModInfo = L.Format("Ignored.NoModInfo", inspector.Environment.GameVersion.Major);
        var itemsWithMods = catalog.Mods.Select(m => m.WorkshopId).OfType<ulong>().ToHashSet();
        IgnoredFolderRow Row(string name, string id, string reason, string directory, ulong? workshopId) =>
            new(name, id, reason, directory, workshopId, workshopId is { } w ? inspector.Workshop.GetDetails(w)?.Title : null,
                workshopId is { } item && itemsWithMods.Contains(item) ? L["Ignored.ItemHasMods"] : "");
        return
        [
            .. catalog.IgnoredFolders.Select(folder => Row(folder.LegacyName ?? Path.GetFileName(folder.Directory),
                folder.LegacyId ?? "", folder.IsForOlderVersions ? L["Ignored.OlderVersions"] : noModInfo, folder.Directory, folder.WorkshopId)),
            .. catalog.InvalidMods.Select(invalid => Row(invalid.Info.Name is { Length: > 0 } n ? n : Path.GetFileName(invalid.Directory),
                invalid.Info.Id, $"{L["ModInfoError." + invalid.Info.Errors[0].Kind]}: {invalid.Info.Errors[0].Detail}",
                invalid.Directory, invalid.WorkshopId)),
        ];
    }
}
