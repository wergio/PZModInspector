using PZModInspector.Core.Settings;

namespace PZModInspector.Tests;

public class SettingsTests
{
    [Fact]
    public void Old_settings_and_cache_are_copied_once_and_left_in_place()
    {
        using var tree = new TestTree();
        var legacy = Path.Combine(tree.Root, "AppData", "PZModInspector");
        var data = Path.Combine(tree.Root, "Profile", "PZModInspector");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "workshop-cache.json"), "cache");

        AppSettings.CopyLegacyData(legacy, data);

        Assert.Equal("{}", File.ReadAllText(Path.Combine(data, "settings.json")));
        Assert.Equal("cache", File.ReadAllText(Path.Combine(data, "workshop-cache.json")));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));

        // Once the new folder exists, nothing is copied again.
        File.Delete(Path.Combine(data, "workshop-cache.json"));
        AppSettings.CopyLegacyData(legacy, data);
        Assert.False(File.Exists(Path.Combine(data, "workshop-cache.json")));
    }
}
