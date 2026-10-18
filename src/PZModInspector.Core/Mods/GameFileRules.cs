namespace PZModInspector.Core.Mods;

/// <summary>How the game uses a file that more than one mod has.</summary>
public enum FileUse
{
    /// <summary>Through ZomboidFileSystem.activeFileMap: only the copy of the mod loaded last counts (Lua, scripts, textures...).</summary>
    Replaced,

    /// <summary>The game reads the copy of every mod and puts them together: same path, no replacement.</summary>
    Merged,

    /// <summary>The game never reads it.</summary>
    Unused,
}

/// <summary>Which files are not replaced by path, from the decompiled game (build 42.21).</summary>
public static class GameFileRules
{
    private const string TranslateDir = "media/lua/shared/translate/";

    /// <summary>Read from the media folder of every mod (CustomSandboxOptions, ModRegistries, ZomboidFileSystem.loadFileGuidTable, tileDepth).</summary>
    private static readonly HashSet<string> MergedFiles = new(StringComparer.Ordinal)
    {
        "media/sandbox-options.txt",
        "media/registries.lua",
        "media/fileguidtable.xml",
        "media/tilegeometry.txt",
        "media/tiledepthtextureassignments.txt",
    };

    /// <param name="relativePath">Lower-case path relative to the mod's common/version folder, as in <see cref="FileIndex"/>.</param>
    public static FileUse Classify(string relativePath)
    {
        // .git, .gitignore, .vscode...: nothing in the game asks for them.
        if (relativePath.Split('/').Any(part => part.StartsWith('.')))
            return FileUse.Unused;
        // Translator reads Translate/<LANG>/<Name>.json of every mod key by key; the .txt files of Build 41 are not read at all.
        if (relativePath.StartsWith(TranslateDir, StringComparison.Ordinal))
            return relativePath.EndsWith(".json", StringComparison.Ordinal) ? FileUse.Merged : FileUse.Unused;
        if (MergedFiles.Contains(relativePath))
            return FileUse.Merged;
        // AdvancedAnimator reads only the .xml files of these folders, mod by mod.
        if ((relativePath.StartsWith("media/animsets/", StringComparison.Ordinal) || relativePath.StartsWith("media/actiongroups/", StringComparison.Ordinal))
            && !relativePath.EndsWith(".xml", StringComparison.Ordinal))
            return FileUse.Unused;
        return FileUse.Replaced;
    }

    /// <summary>
    /// Translation files whose keys can clash: not Mod.json (Translator.readModTranslation reads it only from its own mod) nor
    /// language.json (the definition of the language).
    /// </summary>
    public static bool IsTranslationTable(string relativePath) =>
        relativePath.StartsWith(TranslateDir, StringComparison.Ordinal)
        && relativePath.EndsWith(".json", StringComparison.Ordinal)
        && !relativePath.EndsWith("/mod.json", StringComparison.Ordinal)
        && !relativePath.EndsWith("/language.json", StringComparison.Ordinal);
}
