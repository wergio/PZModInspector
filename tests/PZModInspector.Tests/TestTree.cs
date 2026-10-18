using PZModInspector.Core.Environment;
using PZModInspector.Core.Model;

namespace PZModInspector.Tests;

/// <summary>A throw-away Zomboid + Workshop folder layout under %TEMP%.</summary>
public sealed class TestTree : IDisposable
{
    public TestTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "PZModInspectorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Zomboid);
        Directory.CreateDirectory(WorkshopContent);
    }

    public string Root { get; }
    public string Zomboid => Path.Combine(Root, "Zomboid");
    public string WorkshopContent => Path.Combine(Root, "steamapps", "workshop", "content", "108600");

    public PzEnvironment Environment(string gameVersion = "42.21.0") => new()
    {
        ZomboidDirectory = Zomboid,
        WorkshopContentDirectory = WorkshopContent,
        GameVersion = GameVersion.Parse(gameVersion),
    };

    /// <summary>Creates steamapps\workshop\content\108600\&lt;item&gt;\mods\&lt;folder&gt;\&lt;versionFolder&gt;\mod.info.</summary>
    public string WorkshopMod(ulong item, string folder, string versionFolder, params string[] modInfoLines) =>
        WriteMod(Path.Combine(WorkshopContent, item.ToString(), "mods", folder), versionFolder, modInfoLines);

    public string LocalMod(string folder, string versionFolder, params string[] modInfoLines) =>
        WriteMod(Path.Combine(Zomboid, "mods", folder), versionFolder, modInfoLines);

    public string StagedMod(string item, string folder, string versionFolder, params string[] modInfoLines) =>
        WriteMod(Path.Combine(Zomboid, "Workshop", item, "Contents", "mods", folder), versionFolder, modInfoLines);

    public void File(string relativeToModDir, string modDir, string content = "x")
    {
        var path = Path.Combine(modDir, relativeToModDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }

    public string ModsFile(string relativePath, params string[] ids)
    {
        var path = Path.Combine(Zomboid, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var body = string.Join("\n", ids.Select(id => $"    mod = {id},"));
        System.IO.File.WriteAllText(path, $"VERSION = 1,\n\nmods\n{{\n{body}\n}}\n\nmaps\n{{\n}}\n");
        return path;
    }

    private static string WriteMod(string modDir, string versionFolder, string[] modInfoLines)
    {
        var dir = versionFolder.Length == 0 ? modDir : Path.Combine(modDir, versionFolder);
        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllLines(Path.Combine(dir, "mod.info"), modInfoLines);
        return modDir;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
