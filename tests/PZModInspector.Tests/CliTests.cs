using System.Text.Json;
using PZModInspector.Cli;
using PZModInspector.Core.Settings;

namespace PZModInspector.Tests;

/// <summary>PZModInspectorCLI on a fake Zomboid folder. Nothing goes to Steam: the commands read the (empty) cache.</summary>
public class CliTests
{
    [Fact]
    public void Options_go_anywhere_and_free_words_make_one_argument()
    {
        var args = CliArgs.Parse(["--json", "mod", "Car", "Pack", "--source", "Sandbox/Test"]);

        Assert.Equal("mod", args.Command);
        Assert.Equal("Car Pack", args.Text);
        Assert.True(args.Json);
        Assert.Equal("Sandbox/Test", args.Source);
        Assert.Throws<CliUsageException>(() => CliArgs.Parse(["report", "--nope"]));
        Assert.Throws<CliUsageException>(() => CliArgs.Parse(["dance"]));
    }

    [Fact]
    public async Task Version_is_the_one_of_Directory_Build_props()
    {
        using var tree = Tree();
        var (code, output, _) = await Run(tree, "--version");

        Assert.Equal(0, code);
        Assert.Equal("PZModInspectorCLI 1.0.0", output.Trim());
    }

    [Fact]
    public async Task Mod_shows_problems_and_the_files_other_mods_replace()
    {
        using var tree = Tree();
        var (code, output, _) = await Run(tree, "mod", "pack");

        Assert.Equal(0, code);
        Assert.Contains("Car Pack [Pack] · OK", output);
        Assert.Contains("media/lua/server/tuning.lua  ← Vehicle Core [Core] (#1)", output);
    }

    [Fact]
    public async Task File_says_which_copy_the_game_uses()
    {
        using var tree = Tree();
        var (code, output, _) = await Run(tree, "file", "TUNING.lua");

        Assert.Equal(0, code);
        Assert.Contains("1 files match", output);
        Assert.Contains("→ #2 Car Pack [Pack]", output);
        Assert.Contains("  #1 Vehicle Core [Core]", output);
    }

    [Fact]
    public async Task Log_links_the_error_to_the_mod()
    {
        using var tree = Tree();
        var log = Path.Combine(tree.Root, "console.txt");
        File.WriteAllLines(log,
        [
            "ERROR: General      f:9> Lua((MOD: Car Pack)).Protection> Exception thrown",
            "\tjava.lang.RuntimeException: Object tried to call nil in Protection",
            "\t\tLua((MOD: Car Pack)).Protection(tuning.lua:7)",
        ]);
        var (code, output, _) = await Run(tree, "log", log);

        Assert.Equal(0, code);
        Assert.Contains("Linked to mods (1)", output);
        Assert.Contains("Mod: Car Pack [Pack] (#2)", output);
        Assert.Contains("java.lang.RuntimeException", output);
    }

    [Fact]
    public async Task Json_output_is_valid_and_unknown_mods_exit_with_1()
    {
        using var tree = Tree();
        var (code, output, _) = await Run(tree, "report", "--json");
        Assert.Equal(0, code);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(2, json.RootElement.GetProperty("Loaded").GetInt32());

        var (missing, _, error) = await Run(tree, "mod", "Nothing");
        Assert.Equal(1, missing);
        Assert.Contains("No installed mod matches", error);
    }

    /// <summary>Core and Pack both have tuning.lua: Pack is loaded second, so its copy wins.</summary>
    private static TestTree Tree()
    {
        var tree = new TestTree();
        var core = tree.WorkshopMod(10, "Core", "42", "id=Core", "name=Vehicle Core");
        var pack = tree.WorkshopMod(11, "Pack", "42", "id=Pack", "name= Car Pack");
        tree.File("42/media/lua/server/tuning.lua", core, "Tuning = { core = true }");
        tree.File("42/media/lua/server/tuning.lua", pack, "Tuning = { pack = true }");
        tree.ModsFile("Saves/Sandbox/Test/mods.txt", "Core", "Pack");
        return tree;
    }

    private static async Task<(int Code, string Output, string Error)> Run(TestTree tree, params string[] args)
    {
        // Italian in the settings: PZModInspectorCLI speaks English anyway.
        var host = new CliHost(new AppSettings { Language = "it" }, Path.Combine(tree.Root, "cache.json"), () => tree.Environment());
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await PZModInspector.Cli.Cli.RunAsync([.. args, "--source", "Sandbox/Test"], output, error, host);
        return (code, output.ToString(), error.ToString());
    }
}
