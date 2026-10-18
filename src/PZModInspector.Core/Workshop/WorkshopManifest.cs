using PZModInspector.Core.Model;
using PZModInspector.Core.Parsing;

namespace PZModInspector.Core.Workshop;

/// <summary>steamapps\workshop\appworkshop_108600.acf: what Steam has downloaded and what it knows is newer.</summary>
public sealed class WorkshopManifest
{
    private readonly Dictionary<ulong, WorkshopInstallInfo> _items;

    private WorkshopManifest(Dictionary<ulong, WorkshopInstallInfo> items, IReadOnlyList<ulong> order, bool needsUpdate, bool needsDownload)
    {
        _items = items;
        Order = order;
        NeedsUpdate = needsUpdate;
        NeedsDownload = needsDownload;
    }

    public static WorkshopManifest Empty { get; } = new([], [], false, false);

    /// <summary>Item ids in the order Steam lists them under WorkshopItemsInstalled.</summary>
    public IReadOnlyList<ulong> Order { get; }

    public bool NeedsUpdate { get; }
    public bool NeedsDownload { get; }

    public IReadOnlyCollection<WorkshopInstallInfo> Items => _items.Values;

    public WorkshopInstallInfo? Find(ulong id) => _items.GetValueOrDefault(id);

    public static WorkshopManifest Load(string? path)
    {
        if (path is null)
            return Empty;
        var root = KeyValues.ReadFile(path)?["AppWorkshop"];
        return root is null ? Empty : FromNode(root);
    }

    public static WorkshopManifest FromNode(KvNode appWorkshop)
    {
        var installed = appWorkshop["WorkshopItemsInstalled"];
        var details = appWorkshop["WorkshopItemDetails"];
        var items = new Dictionary<ulong, WorkshopInstallInfo>();
        var order = new List<ulong>();

        foreach (var node in installed?.Children ?? [])
        {
            if (!ulong.TryParse(node.Key, out var id))
                continue;
            var detail = details?[node.Key];
            order.Add(id);
            items[id] = new WorkshopInstallInfo(
                id,
                node.GetLong("size"),
                Time(node.GetLong("timeupdated") ?? detail?.GetLong("timeupdated")),
                Time(detail?.GetLong("latest_timeupdated")),
                Time(detail?.GetLong("timetouched")),
                node.GetString("manifest") ?? detail?.GetString("manifest"),
                detail?.GetString("latest_manifest"));
        }

        return new WorkshopManifest(items, order,
            appWorkshop.GetLong("NeedsUpdate") is > 0,
            appWorkshop.GetLong("NeedsDownload") is > 0);
    }

    private static DateTimeOffset? Time(long? unix) => unix is > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix.Value) : null;
}
