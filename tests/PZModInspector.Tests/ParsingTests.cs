using PZModInspector.Core.Model;
using PZModInspector.Core.Parsing;

namespace PZModInspector.Tests;

public class GameVersionTests
{
    [Theory]
    [InlineData("42.21.0", 42, 21, ".0")]
    [InlineData("42.13", 42, 13, "")]
    [InlineData("41.78.16", 41, 78, ".16")]
    public void Parses_like_the_game(string text, int major, int minor, string suffix)
    {
        Assert.True(GameVersion.TryParse(text, out var v));
        Assert.Equal(new GameVersion(major, minor, suffix), v);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("")]
    [InlineData("v42.1")]
    [InlineData("42.1\n")]
    public void Rejects_what_the_game_rejects(string text) => Assert.False(GameVersion.TryParse(text, out _));

    [Theory]
    [InlineData("42", 42000)]
    [InlineData("42.0", 42000)]
    [InlineData("42.13", 42013)]
    [InlineData("42.13.1", 42013)]
    [InlineData("42.1500", 42999)]
    [InlineData("common", 0)]
    [InlineData("media", 0)]
    public void Folder_names_map_to_version_numbers(string name, int expected) =>
        Assert.Equal(expected, GameVersion.FolderNameToInt(name));

    [Fact]
    public void Comparison_ignores_the_suffix()
    {
        Assert.True(GameVersion.Parse("42.21.5") <= GameVersion.Parse("42.21"));
        Assert.True(GameVersion.Parse("42.20") < GameVersion.Parse("42.21.0"));
    }
}

public class ModInfoParserTests
{
    private static ModInfoData Parse(params string[] lines) =>
        ModInfoParser.Parse(lines, @"C:\m\42", @"C:\m\common", _ => false);

    [Fact]
    public void Reads_the_usual_keys()
    {
        var info = Parse(
            "name=My Mod",
            "id=MyMod",
            "author=Someone",
            "modversion=1.2",
            "require=\\LibA,\\LibB,",
            "incompatible=Other",
            "loadModAfter=\\LibA",
            "versionMin=42.13",
            "versionMax=42.99",
            "description=First ",
            "description=second");

        Assert.Equal("MyMod", info.Id);
        Assert.Equal("My Mod", info.Name);
        Assert.Equal("Someone", info.Author);
        Assert.Equal("1.2", info.ModVersion);
        Assert.Equal(["LibA", "LibB"], info.Require);
        Assert.Equal(["Other"], info.Incompatible);
        Assert.Equal(["LibA"], info.LoadAfter);
        Assert.Equal(GameVersion.Parse("42.13"), info.VersionMin);
        Assert.Equal("First second", info.Description);
        Assert.True(info.IsValid);
    }

    [Fact]
    public void Invalid_versionMin_makes_the_game_drop_the_mod()
    {
        var info = Parse("id=Broken", "versionMin=42");
        Assert.False(info.IsValid);
        Assert.Equal(ModInfoErrorKind.InvalidVersionMin, info.Errors[0].Kind);
    }

    [Fact]
    public void Keys_are_matched_case_sensitively_with_contains_like_the_game()
    {
        var info = Parse("id=X", "modVersion=2.0", "modurl=https://example.com");
        Assert.Null(info.ModVersion);
        Assert.Equal("2.0", info.GetRaw("modversion"));
        Assert.Equal("modhttps://example.com", info.Url);
    }

    [Fact]
    public void Require_items_keep_spaces_like_the_game()
    {
        var info = Parse("id=X", "require=A, B");
        Assert.Equal(["A", " B"], info.Require);
    }

    [Fact]
    public void Poster_falls_back_to_common_folder()
    {
        var info = ModInfoParser.Parse(["poster=poster.png", "icon=icon.png"], @"C:\m\42", @"C:\m\common",
            path => path == @"C:\m\42\icon.png");
        Assert.Equal(@"C:\m\common\poster.png", info.PosterPaths.Single());
        Assert.Equal(@"C:\m\42\icon.png", info.IconPath);
    }

    [Fact]
    public void Bad_tiledef_rejects_the_mod()
    {
        Assert.False(Parse("id=X", "tiledef=foo 50").IsValid);
        Assert.True(Parse("id=X", "tiledef=foo 150").IsValid);
    }

    [Fact]
    public void Java_jar_is_read_from_the_raw_entries()
    {
        Assert.Equal("media/java/x.jar", Parse("id=X", "javaJarFile=media/java/x.jar").JavaJarFile);
    }
}

public class ActiveModsFileTests
{
    [Fact]
    public void Reads_mods_and_maps_in_order()
    {
        var data = ActiveModsFile.Parse("VERSION = 1,mods{    mod = B,    mod = \\A,    mod = ,}maps{    map = Riverside,}");
        Assert.True(data.IsVersionSupported);
        Assert.Equal(["B", "A"], data.Mods);
        Assert.Equal(["Riverside"], data.Maps);
    }

    [Fact]
    public void Other_versions_are_ignored_like_in_game()
    {
        var data = ActiveModsFile.Parse("VERSION = 2,mods{ mod = A, }");
        Assert.False(data.IsVersionSupported);
        Assert.Empty(data.Mods);
    }

    [Fact]
    public void Block_comments_are_skipped()
    {
        var data = ActiveModsFile.Parse("VERSION = 1,/* mods { mod = Hidden, } */mods{ mod = A, }");
        Assert.Equal(["A"], data.Mods);
    }
}

public class KeyValuesTests
{
    [Fact]
    public void Reads_nested_nodes_and_escapes()
    {
        var root = KeyValues.Parse("""
            "libraryfolders"
            {
                "0"
                {
                    "path"      "C:\\Games\\Steam"   // comment
                    "apps" { "108600" "123" }
                }
            }
            """);
        var lib = root["libraryfolders"]!["0"]!;
        Assert.Equal(@"C:\Games\Steam", lib.GetString("path"));
        Assert.Equal(123, lib["apps"]!.GetLong("108600"));
    }
}
