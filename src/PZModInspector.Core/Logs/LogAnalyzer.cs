using System.Text.RegularExpressions;
using PZModInspector.Core.Mods;

namespace PZModInspector.Core.Logs;

/// <summary>A file named in a Lua frame, with every loaded mod that has it.</summary>
/// <param name="RunningModId">The mod whose copy ran (from the frame), when it is among the owners.</param>
public sealed record LogFileMatch(string RelativePath, IReadOnlyList<FileOwner> Owners, string? RunningModId);

/// <summary>A Lua file, in the loaded mods (<paramref name="Owners"/>, last = the copy used) and/or in the base game.</summary>
public sealed record LuaFileLocation(string RelativePath, IReadOnlyList<FileOwner> Owners, bool InGame);

/// <summary>
/// A require("module") that failed. The game (LuaManager.GlobalObject.require) looks for media/lua/&lt;folder&gt;/module.lua only
/// in the folders loaded so far: shared, then client, then server.
/// </summary>
/// <param name="Found">The module exists at that path: its folder was not loaded yet when it was required.</param>
/// <param name="SameName">Nothing at that path, but files with the same name elsewhere: the path in require is probably wrong.</param>
public sealed record LogRequireMatch(string Module, IReadOnlyList<LuaFileLocation> Found, IReadOnlyList<LuaFileLocation> SameName);

/// <param name="ModIds">Mods the entry comes from or names: from the Lua frames and from mod folders in the text.</param>
public sealed record LogFinding(LogGroup Group, IReadOnlyList<string> ModIds, IReadOnlyList<LogFileMatch> Files, LogRequireMatch? Require)
{
    public bool IsModRelated => ModIds.Count > 0;
}

/// <summary>Links the log entries to the installed mods, so an error points to the mod and to the copy of the file that ran.</summary>
public static partial class LogAnalyzer
{
    /// <summary>In the order the game adds them to the require search path.</summary>
    private static readonly string[] LuaFolders = ["shared", "client", "server"];

    public static IReadOnlyList<LogFinding> Analyze(IEnumerable<LogGroup> groups, LoadPlan plan, ModCatalog catalog, FileIndex files,
        string? gameDirectory)
    {
        // The log prints mod.info name=: loaded mods win over other installed ones with the same name.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in plan.Loaded.Select(m => (m.Mod.Name, m.Id)).Concat(catalog.Mods.Select(m => (m.Name, m.Id))))
            byName.TryAdd(name.Name.Trim(), name.Id);
        var folders = plan.Loaded.Select(m => m.Mod).Concat(catalog.Mods)
            .Select(m => (Folder: Slashes(m.Directory) + "/", m.Id))
            .DistinctBy(f => f.Folder)
            .ToList();
        var gameLua = new GameLuaFiles(gameDirectory);

        return groups.Select(group =>
        {
            var entry = group.First;
            var modIds = new List<string>();
            var matches = new List<LogFileMatch>();

            foreach (var frame in entry.LuaFrames)
            {
                var modId = frame.ModName is null ? null
                    : frame.ModNameIsEnd ? FindByNameEnd(frame.ModName, plan, catalog)
                    : byName.GetValueOrDefault(frame.ModName);
                if (modId is null)
                    continue;
                if (!modIds.Contains(modId))
                    modIds.Add(modId);
                if (frame.File is null)
                    continue;
                foreach (var path in files.FindByName(Path.GetFileName(frame.File)))
                {
                    var owners = files.Owners(path);
                    if (owners.Any(o => o.ModId == modId) && matches.All(f => f.RelativePath != path))
                        matches.Add(new LogFileMatch(path, owners, modId));
                }
            }

            var text = Slashes(string.Join('\n', new[] { entry.Location, entry.Message }.Concat(entry.Details).OfType<string>()));
            foreach (var (folder, id) in folders)
            {
                if (text.Contains(folder, StringComparison.OrdinalIgnoreCase) && !modIds.Contains(id))
                    modIds.Add(id);
            }

            var require = RequireRegex().Match(entry.Message) is { Success: true } m ? FindModule(m.Groups[1].Value, files, gameLua) : null;
            return new LogFinding(group, modIds, matches, require);
        }).ToList();
    }

    /// <summary>The mod whose name, after its last ".", is <paramref name="end"/> (a loaded one first).</summary>
    private static string? FindByNameEnd(string end, LoadPlan plan, ModCatalog catalog) =>
        plan.Loaded.Select(m => m.Mod).Concat(catalog.Mods)
            .FirstOrDefault(m => m.Name.Trim() is var name && name.Contains('.')
                                 && name[(name.LastIndexOf('.') + 1)..].Trim().Equals(end.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

    private static LogRequireMatch FindModule(string module, FileIndex files, GameLuaFiles game)
    {
        var name = Slashes(module).TrimStart('/').ToLowerInvariant();
        if (!name.EndsWith(".lua", StringComparison.Ordinal))
            name += ".lua";
        var found = LuaFolders.Select(folder => $"media/lua/{folder}/{name}")
            .Select(path => new LuaFileLocation(path, files.Owners(path), game.Contains(path)))
            .Where(l => l.Owners.Count > 0 || l.InGame)
            .ToList();
        if (found.Count > 0)
            return new LogRequireMatch(module, found, []);

        var fileName = name[(name.LastIndexOf('/') + 1)..];
        var sameName = files.FindByName(fileName).Where(p => p.StartsWith("media/lua/", StringComparison.Ordinal))
            .Concat(game.FindByName(fileName))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .Select(path => new LuaFileLocation(path, files.Owners(path), game.Contains(path)))
            .ToList();
        return new LogRequireMatch(module, [], sameName);
    }

    /// <summary>The game's own media/lua files, read once and only when a require failed.</summary>
    private sealed class GameLuaFiles(string? gameDirectory)
    {
        private HashSet<string>? _paths;
        private ILookup<string, string>? _byName;

        public bool Contains(string relativePath) => Paths.Contains(relativePath);

        public IEnumerable<string> FindByName(string fileName)
        {
            _byName ??= Paths.ToLookup(p => p[(p.LastIndexOf('/') + 1)..], StringComparer.Ordinal);
            return _byName[fileName];
        }

        private HashSet<string> Paths => _paths ??= Load();

        private HashSet<string> Load()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            if (gameDirectory is null || !Directory.Exists(Path.Combine(gameDirectory, "media", "lua")))
                return paths;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(Path.Combine(gameDirectory, "media", "lua"), "*.lua", options))
                paths.Add(Slashes(Path.GetRelativePath(gameDirectory, file)).ToLowerInvariant());
            return paths;
        }
    }

    private static string Slashes(string path) => path.Replace('\\', '/');

    [GeneratedRegex("""require\("([^"]+)"\) failed""")]
    private static partial Regex RequireRegex();
}
