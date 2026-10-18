namespace PZModInspector.Core.Environment;

public enum ModListSourceKind
{
    /// <summary>Zomboid\mods\default.txt: the mods loaded at the main menu.</summary>
    DefaultMods,

    /// <summary>Zomboid\Saves\&lt;mode&gt;\&lt;save&gt;\mods.txt.</summary>
    Save,
}

public sealed record ModListSource(
    ModListSourceKind Kind,
    string ModsFilePath,
    string? Mode = null,
    string? SaveName = null,
    string? SaveDirectory = null,
    DateTimeOffset? LastPlayedUtc = null,
    string? ThumbnailPath = null,
    bool IsLatestSave = false)
{
    public string Key => Kind == ModListSourceKind.DefaultMods ? "default" : $"{Mode}/{SaveName}";
}

public static class ModListSources
{
    /// <summary>Multiplayer saves keep their mods on the server, so they are left out.</summary>
    private static readonly HashSet<string> ExcludedModes = new(StringComparer.OrdinalIgnoreCase) { "Multiplayer" };

    public static IReadOnlyList<ModListSource> Find(PzEnvironment env)
    {
        var sources = new List<ModListSource> { new(ModListSourceKind.DefaultMods, env.DefaultModsFile) };
        var latest = ReadLatestSave(env.ZomboidDirectory);

        var saves = new List<ModListSource>();
        foreach (var modeDir in SteamLocator.SafeEnumerateDirectories(env.SavesDirectory))
        {
            var mode = Path.GetFileName(modeDir);
            if (ExcludedModes.Contains(mode))
                continue;
            foreach (var saveDir in SteamLocator.SafeEnumerateDirectories(modeDir))
            {
                if (!File.Exists(Path.Combine(saveDir, "map_ver.bin")) && !File.Exists(Path.Combine(saveDir, "mods.txt")))
                    continue;
                var name = Path.GetFileName(saveDir);
                var thumb = Path.Combine(saveDir, "thumb.png");
                saves.Add(new ModListSource(
                    ModListSourceKind.Save,
                    Path.Combine(saveDir, "mods.txt"),
                    mode,
                    name,
                    saveDir,
                    LastWrite(saveDir),
                    File.Exists(thumb) ? thumb : null,
                    latest is { } l && l.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase) && l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
            }
        }

        sources.AddRange(saves.OrderByDescending(s => s.LastPlayedUtc));
        return sources;
    }

    /// <summary>latestSave.ini: first line the save name, second line the game mode.</summary>
    private static (string Name, string Mode)? ReadLatestSave(string zomboidDir)
    {
        try
        {
            var path = Path.Combine(zomboidDir, "latestSave.ini");
            if (!File.Exists(path))
                return null;
            var lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return lines.Count >= 2 ? (lines[0], lines[1]) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The game rewrites the save files every time it saves, so the newest file gives the last session.</summary>
    private static DateTimeOffset? LastWrite(string saveDir)
    {
        try
        {
            var newest = new DirectoryInfo(saveDir).EnumerateFiles().Select(f => f.LastWriteTimeUtc).DefaultIfEmpty().Max();
            return newest == default ? null : new DateTimeOffset(newest, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
