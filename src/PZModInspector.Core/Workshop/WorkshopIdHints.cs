using System.Text;
using System.Text.RegularExpressions;

namespace PZModInspector.Core.Workshop;

public sealed record WorkshopIdHint(ulong WorkshopId, string? Name, string Source);

/// <summary>
/// Mod id → Workshop item, remembered by other mods ([B42] Mod Manager, Workshop Update Check) in Zomboid\Lua.
/// Used to give a Workshop link for mods that are in mods.txt but no longer on disk. Read only.
/// </summary>
public static partial class WorkshopIdHints
{
    public static IReadOnlyDictionary<string, WorkshopIdHint> Load(string zomboidDirectory)
    {
        var hints = new Dictionary<string, WorkshopIdHint>(StringComparer.Ordinal);
        var lua = Path.Combine(zomboidDirectory, "Lua");
        ReadUpdateCheckFile(Path.Combine(lua, "WUC_ModVersions.txt"), hints);
        ReadModManagerFile(Path.Combine(lua, "ModManager", "ModListData.ini"), hints);
        return hints;
    }

    /// <summary>Tab separated: mod id, name, version, Workshop id, last update.</summary>
    private static void ReadUpdateCheckFile(string path, Dictionary<string, WorkshopIdHint> hints)
    {
        foreach (var line in ReadLines(path))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 4 && parts[0].Length > 0 && ulong.TryParse(parts[3], out var id))
                hints.TryAdd(parts[0], new WorkshopIdHint(id, parts[1].Length > 0 ? parts[1] : null, "Workshop Update Check"));
        }
    }

    /// <summary>A Lua table: ["ModId"] = { ... workshop = { workshopID = 123, ... } ... }.</summary>
    private static void ReadModManagerFile(string path, Dictionary<string, WorkshopIdHint> hints)
    {
        string? current = null;
        foreach (var line in ReadLines(path))
        {
            var key = ModKeyRegex().Match(line);
            if (key.Success)
            {
                current = Regex.Unescape(key.Groups[1].Value);
                continue;
            }
            var id = WorkshopIdRegex().Match(line);
            if (current is not null && id.Success && ulong.TryParse(id.Groups[1].Value, out var wid) && wid > 0)
            {
                hints.TryAdd(current, new WorkshopIdHint(wid, null, "[B42] Mod Manager"));
                current = null;
            }
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8) : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    [GeneratedRegex("""^\s{8}\["((?:[^"\\]|\\.)*)"\]\s*=\s*\{""")]
    private static partial Regex ModKeyRegex();

    [GeneratedRegex(@"^\s*workshopID\s*=\s*(\d+)")]
    private static partial Regex WorkshopIdRegex();
}
