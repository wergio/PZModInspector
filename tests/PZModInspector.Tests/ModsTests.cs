using PZModInspector.Core.Model;
using PZModInspector.Core.Mods;

namespace PZModInspector.Tests;

public class ModScannerTests
{
    [Fact]
    public void Picks_the_highest_version_folder_not_above_the_game()
    {
        using var tree = new TestTree();
        var dir = tree.WorkshopMod(1, "Mod", "42.13", "id=Mod");
        Directory.CreateDirectory(Path.Combine(dir, "42"));
        Directory.CreateDirectory(Path.Combine(dir, "42.20"));
        Directory.CreateDirectory(Path.Combine(dir, "common"));

        Assert.Equal("42.13", ModScanner.GetVersionFolderName(dir, GameVersion.Parse("42.15")));
        Assert.Equal("42.20", ModScanner.GetVersionFolderName(dir, GameVersion.Parse("42.21.0")));
        Assert.Equal("42.0", ModScanner.GetVersionFolderName(dir, GameVersion.Parse("41.78")));
    }

    [Fact]
    public void Mod_info_in_common_is_enough()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(1, "Mod", "common", "id=CommonOnly", "name=Common only");
        var catalog = ModScanner.Scan(tree.Environment());
        var mod = Assert.Single(catalog.Mods);
        Assert.Equal("CommonOnly", mod.Id);
        Assert.Equal(1UL, mod.WorkshopId);
        Assert.EndsWith(Path.Combine("common", "mod.info"), mod.ModInfoPath);
    }

    [Fact]
    public void Build41_folders_are_ignored_but_remembered()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(2, "Old", "", "id=OldMod", "name=Old mod");
        var catalog = ModScanner.Scan(tree.Environment());
        Assert.Empty(catalog.Mods);
        var ignored = Assert.Single(catalog.IgnoredFolders);
        Assert.True(ignored.IsForOlderVersions);
        Assert.Equal("OldMod", catalog.FindLegacy("OldMod")?.LegacyId);
    }

    [Fact]
    public void Folders_for_an_earlier_major_version_are_ignored_but_remembered()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(3, "Old42", "42.15", "id=Old42", "name=For build 42");
        tree.WorkshopMod(4, "New", "43.0", "id=New43");

        var catalog = ModScanner.Scan(tree.Environment("43.2"));

        Assert.Equal("New43", Assert.Single(catalog.Mods).Id);
        var ignored = Assert.Single(catalog.IgnoredFolders);
        Assert.True(ignored.IsForOlderVersions);
        Assert.Equal("For build 42", ignored.LegacyName);
        Assert.Equal("43.0", ModScanner.GetVersionFolderName(ignored.Directory, GameVersion.Parse("43.2")));
    }

    [Fact]
    public void Text_and_binary_files_are_told_apart()
    {
        using var tree = new TestTree();
        var text = Path.Combine(tree.Root, "notes.unknown");
        var binary = Path.Combine(tree.Root, "data.unknown");
        File.WriteAllText(text, "plain text");
        File.WriteAllBytes(binary, [1, 0, 2]);

        Assert.True(FileKinds.IsText(text));
        Assert.False(FileKinds.IsText(binary));
        Assert.True(FileKinds.IsText("missing.lua"));
        Assert.False(FileKinds.IsText("missing.png"));
    }

    [Fact]
    public void The_game_ModTemplate_is_not_listed_among_ignored_folders()
    {
        using var tree = new TestTree();
        tree.StagedMod("ModTemplate", "ModTemplate", "", "id=ModTemplate");
        tree.LocalMod("ModTemplate", "", "id=ModTemplate");

        var catalog = ModScanner.Scan(tree.Environment());

        // Only the staged one is the game's example: the same name in Zomboid\mods is still reported.
        Assert.Equal(ModOrigin.LocalMods, Assert.Single(catalog.IgnoredFolders).Origin);
    }

    [Fact]
    public void Staged_workshop_wins_over_subscribed_which_wins_over_local_mods()
    {
        using var tree = new TestTree();
        tree.LocalMod("Same", "42", "id=Same", "name=local");
        tree.WorkshopMod(5, "Same", "42", "id=Same", "name=workshop");
        tree.StagedMod("MyUpload", "Same", "42", "id=Same", "name=staged");

        var catalog = ModScanner.Scan(tree.Environment());
        Assert.Equal("staged", catalog.Find("Same")!.Name);
        Assert.Equal(["staged", "workshop", "local"], catalog.FindAll("Same").Select(m => m.Name));
    }

    [Fact]
    public void Invalid_mod_info_is_kept_apart()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(3, "Bad", "42", "id=Bad", "versionMax=nope");
        var catalog = ModScanner.Scan(tree.Environment());
        Assert.Null(catalog.Find("Bad"));
        Assert.Single(catalog.FindInvalid("Bad"));
    }
}

public class LoadOrderResolverTests
{
    private static LoadPlan Resolve(TestTree tree, string gameVersion, params string[] listed) =>
        new LoadOrderResolver(ModScanner.Scan(tree.Environment(gameVersion)), GameVersion.Parse(gameVersion)).Resolve(listed);

    [Fact]
    public void Requirements_load_first_even_when_not_listed()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(1, "Lib", "42", "id=Lib");
        tree.WorkshopMod(2, "App", "42", "id=App", "require=\\Lib");

        var plan = Resolve(tree, "42.21", "App");
        Assert.Equal(["Lib", "App"], plan.Loaded.Select(m => m.Id));
        Assert.False(plan.Loaded[0].Listed);
        Assert.Equal("App", plan.Loaded[0].RequiredBy);
        Assert.Empty(plan.Failures);
    }

    [Fact]
    public void Mod_with_missing_requirement_is_skipped()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(2, "App", "42", "id=App", "require=Missing");
        tree.WorkshopMod(3, "Other", "42", "id=Other");

        var plan = Resolve(tree, "42.21", "App", "Other");
        Assert.Equal(["Other"], plan.Loaded.Select(m => m.Id));
        Assert.Equal(new LoadFailure("App", "App"), Assert.Single(plan.Failures));
    }

    [Fact]
    public void Version_range_is_enforced()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(1, "Old", "42", "id=Old", "versionMax=42.12");
        tree.WorkshopMod(2, "New", "42", "id=New", "versionMin=42.30");
        tree.WorkshopMod(3, "Ok", "42", "id=Ok", "versionMin=42.13", "versionMax=42.99");

        var plan = Resolve(tree, "42.21.0", "Old", "New", "Ok");
        Assert.Equal(["Ok"], plan.Loaded.Select(m => m.Id));
    }

    [Fact]
    public void Spaces_in_require_break_loading_like_in_game()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(1, "A", "42", "id=A");
        tree.WorkshopMod(2, "B", "42", "id=B");
        tree.WorkshopMod(3, "App", "42", "id=App", "require=A, B");

        var plan = Resolve(tree, "42.21", "App");
        Assert.DoesNotContain(plan.Loaded, m => m.Id == "App");
        Assert.Equal(new LoadFailure("App", " B"), Assert.Single(plan.Failures));
    }

    [Fact]
    public void Requirement_cycles_are_detected_instead_of_overflowing()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(1, "A", "42", "id=A", "require=B");
        tree.WorkshopMod(2, "B", "42", "id=B", "require=A");

        var plan = Resolve(tree, "42.21", "A");
        Assert.NotEmpty(plan.Cycles);
    }
}

public class FileConflictAnalyzerTests
{
    [Fact]
    public void Later_mod_wins_and_own_version_folder_is_not_a_conflict()
    {
        using var tree = new TestTree();
        var first = tree.WorkshopMod(1, "First", "42", "id=First");
        var second = tree.WorkshopMod(2, "Second", "42", "id=Second", "require=First");
        tree.File("common/media/lua/client/shared.lua", first, "common");
        tree.File("42/media/lua/client/shared.lua", first, "first");
        tree.File("42/media/lua/client/own.lua", first);
        tree.File("42/media/lua/client/Shared.lua", second, "second");
        tree.File("42/poster.png", second);

        var conflict = Assert.Single(Analyze(tree));

        Assert.Equal("media/lua/client/shared.lua", conflict.RelativePath);
        Assert.Equal("Second", conflict.Winner.ModId);
        Assert.Equal("First", Assert.Single(conflict.Overridden).ModId);
        Assert.Null(conflict.Keys);
    }

    [Fact]
    public void Identical_copies_are_not_conflicts_and_only_different_ones_are_overridden()
    {
        using var tree = new TestTree();
        var a = tree.WorkshopMod(1, "A", "42", "id=A");
        var b = tree.WorkshopMod(2, "B", "42", "id=B", "require=A");
        var c = tree.WorkshopMod(3, "C", "42", "id=C", "require=B");
        tree.File("42/media/textures/same.png", a, "pixels");
        tree.File("42/media/textures/same.png", b, "pixels");
        tree.File("42/media/scripts/car.txt", a, "old");
        tree.File("42/media/scripts/car.txt", b, "new");
        tree.File("42/media/scripts/car.txt", c, "new");

        var conflict = Assert.Single(Analyze(tree, "C"));

        Assert.Equal("media/scripts/car.txt", conflict.RelativePath);
        Assert.Equal("C", conflict.Winner.ModId);
        Assert.Equal("A", Assert.Single(conflict.Overridden).ModId);
    }

    [Fact]
    public void Files_the_game_merges_or_does_not_read_are_not_conflicts()
    {
        using var tree = new TestTree();
        var a = tree.WorkshopMod(1, "A", "42", "id=A");
        var b = tree.WorkshopMod(2, "B", "42", "id=B", "require=A");
        foreach (var (path, i) in new[]
                 {
                     "media/sandbox-options.txt", "media/registries.lua", "media/fileGuidTable.xml", "media/tileGeometry.txt",
                     "media/lua/shared/Translate/EN/ContextMenu_EN.txt", "media/lua/shared/Translate/EN/Mod.json", "media/.git/HEAD",
                     "media/AnimSets/placeholder.txt",
                 }.Select((p, i) => (p, i)))
        {
            tree.File("42/" + path, a, "a" + i);
            tree.File("42/" + path, b, "b" + i);
        }

        Assert.Empty(Analyze(tree, "B"));
    }

    [Fact]
    public void Translations_conflict_only_on_keys_given_another_text()
    {
        using var tree = new TestTree();
        var a = tree.WorkshopMod(1, "A", "42", "id=A");
        var b = tree.WorkshopMod(2, "B", "42", "id=B", "require=A");
        var c = tree.WorkshopMod(3, "C", "42", "id=C", "require=B");
        tree.File("42/media/lua/shared/Translate/EN/UI.json", a, """{ "UI_A": "a", "UI_Same": "s", "UI_Clash": "from A", }""");
        tree.File("42/media/lua/shared/Translate/EN/UI.json", b, "﻿{ // B\n \"UI_B\": \"b\", \"UI_Same\": \"s\", \"UI_Clash\": \"from B\" }");
        tree.File("42/media/lua/shared/Translate/EN/UI.json", c, """{ "UI_Clash": "", "UI_C": "c" }""");

        var conflict = Assert.Single(Analyze(tree, "C"));

        Assert.Equal("media/lua/shared/translate/en/ui.json", conflict.RelativePath);
        // An empty text does not replace one already there: B's text stays.
        Assert.Equal("B", conflict.Winner.ModId);
        Assert.Equal("A", Assert.Single(conflict.Overridden).ModId);
        Assert.Equal(["UI_Clash"], conflict.Keys);
    }

    [Fact]
    public void Copy_uses_tell_used_replaced_identical_merged_and_unused()
    {
        using var tree = new TestTree();
        var a = tree.WorkshopMod(1, "A", "42", "id=A");
        var b = tree.WorkshopMod(2, "B", "42", "id=B", "require=A");
        var c = tree.WorkshopMod(3, "C", "42", "id=C", "require=B");
        tree.File("42/media/lua/client/x.lua", a, "1");
        tree.File("42/media/lua/client/x.lua", b, "2");
        tree.File("42/media/lua/client/x.lua", c, "2");
        tree.File("42/media/lua/shared/Translate/EN/UI.json", a, "{}");
        tree.File("42/media/lua/shared/Translate/EN/UI.json", c, "{}");

        var index = FileIndex.Build(Plan(tree, "C").Loaded);
        const string lua = "media/lua/client/x.lua", json = "media/lua/shared/translate/en/ui.json";

        Assert.Equal([CopyUse.Replaced, CopyUse.ReplacedBySame, CopyUse.Used], index.CopyUses(lua, index.Owners(lua)));
        Assert.Equal([CopyUse.Merged, CopyUse.Merged], index.CopyUses(json, index.Owners(json)));
    }

    private static LoadPlan Plan(TestTree tree, string listed) =>
        new LoadOrderResolver(ModScanner.Scan(tree.Environment()), GameVersion.Parse("42.21")).Resolve([listed]);

    private static IReadOnlyList<FileConflict> Analyze(TestTree tree, string listed = "Second") =>
        FileConflictAnalyzer.Analyze(Plan(tree, listed).Loaded);
}

public class FileIndexMatchTests
{
    private static readonly string[] Paths =
    [
        "42/media/lua/client/Tuning/Tuning.lua",
        "42/media/lua/server/tuning.lua",
        "42/media/lua/server/TuningExtra.lua",
        "common/media/scripts/vehicles/car.txt",
        "common/media/textures/tuning.png",
    ];

    [Fact]
    public void A_name_finds_that_file_anywhere_ignoring_case()
    {
        using var tree = new TestTree();
        var files = Index(tree);

        Assert.Equal(["media/lua/client/tuning/tuning.lua", "media/lua/server/tuning.lua"], files.MatchPaths("TUNING.lua"));
    }

    [Fact]
    public void Without_an_exact_name_the_names_that_contain_it()
    {
        using var tree = new TestTree();
        var files = Index(tree);

        Assert.Equal(["media/lua/server/tuningextra.lua"], files.MatchPaths("extra"));
        Assert.Empty(files.MatchPaths("nothing"));
        Assert.Empty(files.MatchPaths("  "));
    }

    [Fact]
    public void A_path_matches_anywhere_in_the_relative_path()
    {
        using var tree = new TestTree();
        var files = Index(tree);

        Assert.Equal(["media/lua/server/tuning.lua", "media/lua/server/tuningextra.lua"], files.MatchPaths(@"Lua\Server\"));
        Assert.Equal(["media/scripts/vehicles/car.txt"], files.MatchPaths("vehicles/car.txt"));
    }

    [Fact]
    public void Patterns_match_the_whole_name_or_the_end_of_the_path()
    {
        using var tree = new TestTree();
        var files = Index(tree);

        Assert.Equal(["media/lua/client/tuning/tuning.lua", "media/lua/server/tuning.lua", "media/lua/server/tuningextra.lua"],
            files.MatchPaths("tuning*.lua"));
        Assert.Equal(["media/lua/client/tuning/tuning.lua", "media/lua/server/tuning.lua", "media/textures/tuning.png"],
            files.MatchPaths("tuning.???"));
        Assert.Equal(["media/lua/server/tuning.lua", "media/lua/server/tuningextra.lua"], files.MatchPaths("server/*.lua"));
        // With a "/" the pattern starts at a folder: "erver/*" is not the end of any path.
        Assert.Empty(files.MatchPaths("erver/*.lua"));
    }

    private static FileIndex Index(TestTree tree)
    {
        var mod = tree.WorkshopMod(1, "M", "42", "id=M");
        foreach (var path in Paths)
            tree.File(path, mod);
        var catalog = ModScanner.Scan(tree.Environment());
        var plan = new LoadOrderResolver(catalog, GameVersion.Parse("42.21")).Resolve(["M"]);
        return FileIndex.Build(plan.Loaded);
    }
}
