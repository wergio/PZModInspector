using PZModInspector.Core.Mods;

namespace PZModInspector.Tests;

public class CodeComparerTests
{
    [Fact]
    public void Lua_comments_and_whitespace_do_not_count()
    {
        const string a = """
            -- FWO Fitness Workout Overhaul - Client-Side Script
            local function onPlayerLoad(player)
                if not player then return end -- nothing to do
                --[[ long
                     comment ]]
                local fitness = player:getFitness()
            end
            """;
        const string b = "--[==[ other header ]==]\nlocal function onPlayerLoad( player )\n\tif not player then return end\n\tlocal fitness=player:getFitness( )\nend";

        Assert.Equal(CodeComparer.NormalizeLua(a), CodeComparer.NormalizeLua(b));
    }

    [Theory]
    [InlineData("x = 'a'", "x = 'b'")] // strings are compared exactly
    [InlineData("x = 'a  b'", "x = 'a b'")]
    [InlineData("x = [[a  b]]", "x = [[a b]]")]
    [InlineData("if a = = b then end", "if a == b then end")] // the halves of an operator stay apart
    [InlineData("x = a - -b", "x = a--b")] // "--" would start a comment
    [InlineData("local x", "localx")]
    [InlineData("x = 1 .. 2", "x = 1..2")]
    [InlineData("Registered = true", "TreadmillRegistered = true")]
    public void Lua_code_changes_count(string a, string b) =>
        Assert.NotEqual(CodeComparer.NormalizeLua(a), CodeComparer.NormalizeLua(b));

    [Fact]
    public void A_comment_marker_inside_a_string_is_text()
    {
        Assert.NotEqual(CodeComparer.NormalizeLua("x = '--a'"), CodeComparer.NormalizeLua("x = ''"));
        Assert.Equal(CodeComparer.NormalizeLua("x = \"it's -- here\" -- comment"), CodeComparer.NormalizeLua("x = \"it's -- here\""));
    }

    [Fact]
    public void Script_comments_are_removed_like_the_game_nested_too()
    {
        Assert.Equal("module Base { }", CodeComparer.StripScriptComments("module Base { /* a /* b */ c */}"));
        Assert.Equal(
            CodeComparer.NormalizeScript("module Base\n{\n    item Axe\n    {\n        Weight = 3, /* heavy */\n    }\n}"),
            CodeComparer.NormalizeScript("/* header */ module Base { item Axe { Weight=3, } }"));
        Assert.NotEqual(CodeComparer.NormalizeScript("item Axe { Weight = 3, }"), CodeComparer.NormalizeScript("item Axe { Weight = 4, }"));
    }

    [Fact]
    public void Copies_that_differ_only_in_comments_are_not_conflicts()
    {
        using var tree = new TestTree();
        var a = tree.WorkshopMod(1, "A", "42", "id=A");
        var b = tree.WorkshopMod(2, "B", "42", "id=B", "require=A");
        tree.File("42/media/lua/client/x.lua", a, "-- A\nlocal x = 1");
        tree.File("42/media/lua/client/x.lua", b, "-- B, another comment\nlocal x=1 -- one");
        tree.File("42/media/lua/client/y.lua", a, "local y = 1");
        tree.File("42/media/lua/client/y.lua", b, "local y = 2");

        var plan = new LoadOrderResolver(ModScanner.Scan(tree.Environment()), Core.Model.GameVersion.Parse("42.21")).Resolve(["B"]);

        Assert.Equal("media/lua/client/y.lua", Assert.Single(FileConflictAnalyzer.Analyze(plan.Loaded)).RelativePath);
    }
}
