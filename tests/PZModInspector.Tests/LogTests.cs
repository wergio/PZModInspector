using PZModInspector.Core.Logs;
using PZModInspector.Core.Mods;

namespace PZModInspector.Tests;

public class ConsoleLogParserTests
{
    // Shapes copied from a real console.txt of build 42.21.
    private static readonly string[] Lines =
    [
        "LOG  : General      f:0> loading 9306 zones from map_zone.bin",
        "ERROR: General      f:0 at ImportedSkeleton.collectBoneFrames  > Could not find bone index for node name: \"Bip01_Root\"",
        "ERROR: General      f:3 at ImportedSkeleton.collectBoneFrames  > Could not find bone index for node name: \"Bip01_Root\"",
        "WARN : Lua          f:0 at Lua((MOD: Car Pack (read the FAQ))).Tuning.lua> require(\"Car_Stuffs\") failed",
        "ERROR: General      f:1514 at KahluaThread.flushErrorMessage      > dumping Lua stack trace",
        "-----------------------------------------",
        "STACK TRACE",
        "-----------------------------------------",
        "\tLua((MOD: Car Pack (read the FAQ))).Protection(SVUC_ATATuning2.lua:227)",
        "ERROR: General      f:1514> Lua((MOD: Car Pack (read the FAQ))).Protection> Exception thrown",
        "\tjava.lang.RuntimeException: Object tried to call nil in Protection at KahluaUtil.fail(KahluaUtil.java:100).",
        "\tStack trace:",
        "\t\tse.krka.kahlua.vm.KahluaUtil.fail(KahluaUtil.java:100)",
        "\t\tLua((MOD: Car Pack (read the FAQ))).Protection(SVUC_ATATuning2.lua:227)",
        "WARN : Lua          f:0 at 15+/MP] Bicycle!)).PartAttachmentService.lua> require(\"Bicycle/BicycleContainerManager\") failed",
        "LOG  : Lua          f:1> :	[ConeVisionOutline] serving local player 0",
    ];

    [Fact]
    public void Reads_errors_and_warnings_with_their_stack_traces()
    {
        var entries = ConsoleLogParser.Parse(Lines);

        Assert.Equal(6, entries.Count);
        Assert.Equal(2, entries[0].LineNumber);
        Assert.Equal("ImportedSkeleton.collectBoneFrames", entries[0].Location);
        Assert.Equal(LogSeverity.Warning, entries[2].Severity);

        var dump = entries[3];
        Assert.Equal(1514, dump.Frame);
        Assert.Equal(4, dump.Details.Count);
        var frame = Assert.Single(dump.LuaFrames);
        Assert.Equal(new LuaFrame("Car Pack (read the FAQ)", "Protection", "SVUC_ATATuning2.lua", 227), frame);

        var thrown = entries[4];
        Assert.Null(thrown.Location);
        Assert.StartsWith("java.lang.RuntimeException: Object tried to call nil", thrown.Exception);
        // The frame of the message (no file) and the one of the trace (with file) are the same call.
        Assert.Equal("SVUC_ATATuning2.lua", Assert.Single(thrown.LuaFrames).File);
    }

    [Fact]
    public void Require_warnings_give_the_file_and_cut_names_are_kept_as_ends()
    {
        var entries = ConsoleLogParser.Parse(Lines);

        Assert.Equal(new LuaFrame("Car Pack (read the FAQ)", "Tuning.lua", "Tuning.lua", null), Assert.Single(entries[2].LuaFrames));
        var cut = Assert.Single(entries[5].LuaFrames);
        Assert.Equal("15+/MP] Bicycle!", cut.ModName);
        Assert.True(cut.ModNameIsEnd);
        Assert.Equal("PartAttachmentService.lua", cut.File);
    }

    [Fact]
    public void Logs_of_the_Logs_folder_with_the_time_first_are_read_too()
    {
        // Zomboid\Logs\<date>_DebugLog.txt: the time before each header, not before the stack trace.
        var entries = ConsoleLogParser.Parse(
        [
            "[18-10-26 12:39:19.208] LOG  : General      f:0> 18-10-2026 12:39:19.",
            "[18-10-26 12:43:47.340] ERROR: General      f:2403 at KahluaThread.flushErrorMessage      > dumping Lua stack trace",
            "-----------------------------------------",
            "\tLua((MOD: Car Pack)).Protection(SVUC_ATATuning2.lua:227).",
            "[18-10-26 12:43:47.341] WARN : Lua          f:0 at Lua((MOD: Car Pack)).Tuning.lua> require(\"Car_Stuffs\") failed.",
        ]);

        Assert.Equal(2, entries.Count);
        Assert.Equal(2, entries[0].LineNumber);
        Assert.Equal(2403, entries[0].Frame);
        Assert.Equal("KahluaThread.flushErrorMessage", entries[0].Location);
        Assert.Equal(new LuaFrame("Car Pack", "Protection", "SVUC_ATATuning2.lua", 227), Assert.Single(entries[0].LuaFrames));
        Assert.Equal(LogSeverity.Warning, entries[1].Severity);
        Assert.Equal("require(\"Car_Stuffs\") failed.", entries[1].Message);
    }

    [Fact]
    public void Entries_that_differ_only_by_numbers_are_grouped()
    {
        var groups = ConsoleLogParser.Group(ConsoleLogParser.Parse(Lines));

        Assert.Equal(5, groups.Count);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(2, groups[0].First.LineNumber);
        Assert.Equal(3, groups[0].LastLineNumber);
    }
}

public class LogAnalyzerTests
{
    [Fact]
    public void Links_errors_to_the_mod_and_to_the_copy_of_the_file_that_ran()
    {
        using var tree = new TestTree();
        var core = tree.WorkshopMod(10, "Core", "42", "id=Core", "name=Vehicle Core");
        var pack = tree.WorkshopMod(11, "Pack", "42", "id=Pack", "name=Car Pack (read the FAQ)");
        tree.File("42/media/lua/server/Tuning2/SVUC_ATATuning2.lua", core);
        tree.File("42/media/lua/server/Tuning2/SVUC_ATATuning2.lua", pack);
        var (plan, catalog, files) = Load(tree, "Core", "Pack");

        var findings = Analyze(plan, catalog, files, null,
            "ERROR: General      f:1514> Lua((MOD: Car Pack (read the FAQ))).Protection> Exception thrown",
            "\t\tLua((MOD: Car Pack (read the FAQ))).Protection(SVUC_ATATuning2.lua:227)");

        var finding = Assert.Single(findings);
        Assert.Equal(["Pack"], finding.ModIds);
        var file = Assert.Single(finding.Files);
        Assert.Equal("media/lua/server/tuning2/svuc_atatuning2.lua", file.RelativePath);
        Assert.Equal(["Core", "Pack"], file.Owners.Select(o => o.ModId));
        Assert.Equal("Pack", file.RunningModId);
    }

    [Fact]
    public void Cut_names_and_mod_folders_in_the_text_find_the_mod()
    {
        using var tree = new TestTree();
        var bike = tree.WorkshopMod(20, "Bicycle", "42", "id=BicycleMod", "name=[B42.15+/MP] Bicycle!");
        tree.WorkshopMod(21, "Other", "42", "id=Other", "name=Other");
        var (plan, catalog, files) = Load(tree, "BicycleMod", "Other");

        var findings = Analyze(plan, catalog, files, null,
            "WARN : Lua          f:0 at 15+/MP] Bicycle!)).PartAttachmentService.lua> require(\"Bicycle/X\") failed",
            $"ERROR: General      f:0 at Texture.load > cannot read {bike.Replace('\\', '/')}/42/media/textures/a.png");

        Assert.Equal(["BicycleMod"], findings[0].ModIds);
        Assert.Equal(["BicycleMod"], findings[1].ModIds);
    }

    [Fact]
    public void A_failed_require_says_whether_the_folder_was_not_loaded_yet_or_the_path_is_wrong()
    {
        using var tree = new TestTree();
        var mod = tree.WorkshopMod(30, "Bike", "42", "id=Bike", "name=Bike");
        tree.File("42/media/lua/server/Bicycle/BicycleContainerManager.lua", mod);
        var game = Path.Combine(tree.Root, "game");
        Directory.CreateDirectory(Path.Combine(game, "media", "lua", "shared", "TimedActions"));
        File.WriteAllText(Path.Combine(game, "media", "lua", "shared", "TimedActions", "ISBaseTimedAction.lua"), "x");
        var (plan, catalog, files) = Load(tree, "Bike");

        var findings = Analyze(plan, catalog, files, game,
            "WARN : Lua          f:0 at Lua((MOD:Bike)).A.lua> require(\"Bicycle/BicycleContainerManager\") failed",
            "WARN : Lua          f:0 at Lua((MOD:Bike)).A.lua> require(\"ISBaseTimedAction\") failed",
            "WARN : Lua          f:0 at Lua((MOD:Bike)).A.lua> require(\"Nowhere\") failed");

        var found = Assert.Single(findings[0].Require!.Found);
        Assert.Equal("media/lua/server/bicycle/bicyclecontainermanager.lua", found.RelativePath);
        Assert.Equal("Bike", Assert.Single(found.Owners).ModId);
        Assert.Empty(findings[1].Require!.Found);
        var sameName = Assert.Single(findings[1].Require!.SameName);
        Assert.Equal("media/lua/shared/timedactions/isbasetimedaction.lua", sameName.RelativePath);
        Assert.True(sameName.InGame);
        Assert.Empty(findings[2].Require!.Found);
        Assert.Empty(findings[2].Require!.SameName);
    }

    [Fact]
    public void File_index_finds_files_by_name_ignoring_case()
    {
        using var tree = new TestTree();
        var mod = tree.WorkshopMod(40, "M", "42", "id=M");
        tree.File("42/media/lua/client/Foo/Bar.lua", mod);
        tree.File("common/media/lua/shared/bar.lua", mod);
        var (_, _, files) = Load(tree, "M");

        Assert.Equal(["media/lua/client/foo/bar.lua", "media/lua/shared/bar.lua"], files.FindByName("BAR.lua").Order());
        Assert.Equal("M", Assert.Single(files.Owners("Media\\Lua\\Client\\Foo\\Bar.lua")).ModId);
    }

    private static (LoadPlan Plan, ModCatalog Catalog, FileIndex Files) Load(TestTree tree, params string[] listed)
    {
        var env = tree.Environment();
        var catalog = ModScanner.Scan(env, []);
        var plan = new LoadOrderResolver(catalog, env.GameVersion).Resolve(listed);
        return (plan, catalog, FileIndex.Build(plan.Loaded));
    }

    private static IReadOnlyList<LogFinding> Analyze(LoadPlan plan, ModCatalog catalog, FileIndex files, string? game, params string[] lines) =>
        LogAnalyzer.Analyze(ConsoleLogParser.Group(ConsoleLogParser.Parse(lines)), plan, catalog, files, game);
}
