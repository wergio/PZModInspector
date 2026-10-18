using System.Text.Json;
using PZModInspector.Core.Analysis;
using PZModInspector.Core.Environment;
using PZModInspector.Core.Model;
using PZModInspector.Core.Mods;
using PZModInspector.Core.Parsing;
using PZModInspector.Core.Workshop;

namespace PZModInspector.Tests;

public class ModAnalyzerTests
{
    private static readonly DateTimeOffset Installed = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static InspectionResult Analyze(TestTree tree, string[] listed, Dictionary<ulong, WorkshopItemDetails>? details = null,
        DateTimeOffset? lastPlayed = null, string manifestIds = "", string gameVersion = "42.21.0")
    {
        var env = tree.Environment(gameVersion);
        var manifest = WorkshopManifest.FromNode(KeyValues.Parse(
            "\"AppWorkshop\" { \"WorkshopItemsInstalled\" { " +
            string.Join(" ", manifestIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => $"\"{id}\" {{ \"timeupdated\" \"{Installed.ToUnixTimeSeconds()}\" }}")) +
            " } }")["AppWorkshop"]!);
        var source = new ModListSource(ModListSourceKind.Save, tree.ModsFile("Saves/Sandbox/Test/mods.txt", listed), "Sandbox", "Test",
            LastPlayedUtc: lastPlayed);
        return ModAnalyzer.Analyze(new AnalysisInput
        {
            Environment = env,
            Catalog = ModScanner.Scan(env, manifest.Order),
            Manifest = manifest,
            Source = source,
            ModsFile = ActiveModsFile.ReadFile(source.ModsFilePath),
            Details = id => details?.GetValueOrDefault(id),
        });
    }

    private static WorkshopItemDetails Details(ulong id, int result = 1, DateTimeOffset? updated = null) =>
        new() { Id = id, Result = result, Title = $"Item {id}", TimeUpdated = updated ?? Installed };

    [Fact]
    public void Missing_mod_is_an_error()
    {
        using var tree = new TestTree();
        var result = Analyze(tree, ["Ghost"]);
        Assert.Contains(result.Find("Ghost")!.Issues, i => i.Code == IssueCode.ModNotInstalled && i.Severity == Severity.Error);
    }

    [Fact]
    public void Newer_workshop_version_means_update_pending()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(10, "A", "42", "id=A");
        var result = Analyze(tree, ["A"], new() { [10] = Details(10, updated: Installed.AddDays(3)) }, manifestIds: "10");
        Assert.Contains(result.Find("A")!.Issues, i => i.Code == IssueCode.UpdatePending);
    }

    [Fact]
    public void Removed_workshop_item_is_reported()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(11, "A", "42", "id=A");
        var result = Analyze(tree, ["A"], new() { [11] = Details(11, result: 9) }, manifestIds: "11");
        Assert.Contains(result.Find("A")!.Issues, i => i.Code == IssueCode.WorkshopItemRemoved && i.Severity == Severity.Warning);
    }

    [Fact]
    public void Update_after_last_session_is_noted()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(12, "A", "42", "id=A");
        var result = Analyze(tree, ["A"], lastPlayed: Installed.AddDays(-1), manifestIds: "12");
        Assert.Contains(result.Find("A")!.Issues, i => i.Code == IssueCode.UpdatedSinceLastPlayed);
    }

    [Fact]
    public void Incompatible_and_auto_loaded_requirements()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(1, "Lib", "42", "id=Lib");
        tree.WorkshopMod(2, "A", "42", "id=A", "require=Lib", "incompatible=B");
        tree.WorkshopMod(3, "B", "42", "id=B");
        var result = Analyze(tree, ["A", "B"]);

        Assert.Contains(result.Find("A")!.Issues, i => i.Code == IssueCode.IncompatibleModActive);
        var lib = result.Find("Lib")!;
        Assert.Null(lib.ListIndex);
        Assert.Equal(1, lib.LoadIndex);
        Assert.Contains(lib.Issues, i => i.Code == IssueCode.RequirementAutoLoaded);
    }

    [Fact]
    public void Missing_requirement_explains_why_the_mod_does_not_load()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(2, "A", "42", "id=A", "require=Nope");
        var entry = Analyze(tree, ["A"]).Find("A")!;
        Assert.False(entry.IsLoaded);
        Assert.Contains(entry.Issues, i => i.Code == IssueCode.RequirementMissing && i.Args[0] == "Nope");
    }

    [Fact]
    public void Required_workshop_item_not_subscribed_is_a_warning()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(20, "A", "42", "id=A");
        tree.WorkshopMod(21, "B", "42", "id=B");
        var details = new Dictionary<ulong, WorkshopItemDetails>
        {
            [20] = Details(20) with { Children = [21, 22] },
            [21] = Details(21),
            [22] = Details(22),
        };
        var entry = Analyze(tree, ["A", "B"], details).Find("A")!;
        Assert.Equal(new[] { "Item 21", "Item 22" }, entry.WorkshopRequired.Select(r => r.Title));
        var issue = Assert.Single(entry.Issues, i => i.Code == IssueCode.WorkshopRequiredItemMissing);
        Assert.Equal("22", issue.Args[1]);
    }

    // Like "Standardized Vehicle Upgrades 3 - Vanilla Addon": the Workshop lists the Build 41 requirements, while the 42.x
    // mod.info requires the Build 42 copies, published as other items.
    private static Dictionary<ulong, WorkshopItemDetails> HybridItem(params string[] requiredTags) => new()
    {
        [30] = Details(30) with { Tags = ["Build 41", "Build 42"], Children = [31] },
        [31] = Details(31) with { Tags = ["Framework", .. requiredTags] },
        [32] = Details(32) with { Tags = ["Build 42"] },
    };

    private static void HybridTree(TestTree tree, string versionFolder = "42")
    {
        tree.WorkshopMod(30, "Addon", versionFolder, "id=Addon", "require=Core");
        tree.WorkshopMod(32, "Core", versionFolder, "id=Core");
    }

    [Theory]
    [InlineData("Build 41")]
    [InlineData("Build 40", "Build 41")]
    [InlineData("build 41")]
    public void Required_item_only_for_older_builds_is_info_when_requires_are_loaded(params string[] tags)
    {
        using var tree = new TestTree();
        HybridTree(tree);
        var entry = Analyze(tree, ["Addon"], HybridItem(tags)).Find("Addon")!;
        Assert.DoesNotContain(entry.Issues, i => i.Code == IssueCode.WorkshopRequiredItemMissing);
        var issue = Assert.Single(entry.Issues, i => i.Code == IssueCode.WorkshopRequiredItemOlderBuild);
        Assert.Equal(Severity.Info, issue.Severity);
        Assert.Equal("31", issue.Args[1]);
        Assert.Equal(string.Join(", ", tags.Select(t => t[^2..])), issue.Args[3]);
    }

    [Theory]
    [InlineData("Build 41", "Build 42")]
    [InlineData("Build 42")]
    [InlineData("Build 43")]
    [InlineData]
    public void Required_item_for_this_build_or_untagged_stays_a_warning(params string[] tags)
    {
        using var tree = new TestTree();
        HybridTree(tree);
        var entry = Analyze(tree, ["Addon"], HybridItem(tags)).Find("Addon")!;
        Assert.Contains(entry.Issues, i => i.Code == IssueCode.WorkshopRequiredItemMissing && i.Severity == Severity.Warning);
        Assert.DoesNotContain(entry.Issues, i => i.Code == IssueCode.WorkshopRequiredItemOlderBuild);
    }

    [Fact]
    public void Required_item_only_for_older_builds_stays_a_warning_when_a_require_is_missing()
    {
        using var tree = new TestTree();
        tree.WorkshopMod(30, "Addon", "42", "id=Addon", "require=Core");
        var entry = Analyze(tree, ["Addon"], HybridItem("Build 41")).Find("Addon")!;
        Assert.Contains(entry.Issues, i => i.Code == IssueCode.WorkshopRequiredItemMissing);
        Assert.DoesNotContain(entry.Issues, i => i.Code == IssueCode.WorkshopRequiredItemOlderBuild);
    }

    [Fact]
    public void Only_the_major_version_of_the_game_counts()
    {
        // With Build 43 an item tagged only "Build 42" is for an older game too; "Build 43" is still needed.
        using var tree = new TestTree();
        HybridTree(tree, "43");
        var older = Analyze(tree, ["Addon"], HybridItem("Build 41", "Build 42"), gameVersion: "43.0").Find("Addon")!;
        Assert.Contains(older.Issues, i => i.Code == IssueCode.WorkshopRequiredItemOlderBuild && i.Args[3] == "41, 42" && i.Args[4] == "43");
        var current = Analyze(tree, ["Addon"], HybridItem("Build 42", "Build 43"), gameVersion: "43.5").Find("Addon")!;
        Assert.Contains(current.Issues, i => i.Code == IssueCode.WorkshopRequiredItemMissing);
    }

    [Fact]
    public void Title_does_not_count_without_build_tags()
    {
        var details = new WorkshopItemDetails { Id = 1, Result = 1, Title = "Tsar's Common Library 2.0 [Only for B41]", Tags = ["Framework"] };
        Assert.Null(WorkshopBuilds.OnlyOlderThan(details, 42));
    }
}

public class WorkshopParsingTests
{
    private const string Key = "0123456789ABCDEF0123456789ABCDEF";
    private static readonly DateTimeOffset Updated = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_published_file_details()
    {
        using var json = JsonDocument.Parse("""
            {"response":{"result":1,"resultcount":2,"publishedfiledetails":[
              {"publishedfileid":"5","result":1,"title":"T","time_updated":1787949502,"file_size":"1024","banned":0,"visibility":0,
               "tags":[{"tag":"Build 42"}]},
              {"publishedfileid":"6","result":9}]}}
            """);
        var items = SteamWorkshopClient.ParseDetails(json.RootElement, DateTimeOffset.UtcNow).ToList();
        Assert.Equal(1024, items[0].FileSize);
        Assert.Equal(["Build 42"], items[0].Tags);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787949502), items[0].TimeUpdated);
        Assert.Null(items[0].Children);
        Assert.False(items[1].IsAvailable);
    }

    [Fact]
    public void Reads_required_items_from_GetDetails()
    {
        using var json = JsonDocument.Parse("""
            {"response":{"publishedfiledetails":[
              {"result":1,"publishedfileid":"5","title":"T","file_description":"D","file_size":"1024","banned":false,"num_children":2,
               "children":[{"publishedfileid":"7","sortorder":0,"file_type":0},{"publishedfileid":"8","sortorder":1,"file_type":0}]},
              {"result":1,"publishedfileid":"6","title":"U","num_children":0},
              {"result":9,"publishedfileid":"9"}]}}
            """);
        var items = SteamWorkshopClient.ParseDetails(json.RootElement, DateTimeOffset.UtcNow, withChildren: true).ToList();
        Assert.Equal(new[] { 7UL, 8UL }, items[0].Children!);
        Assert.Equal("D", items[0].Description);
        Assert.False(items[0].Banned);
        Assert.Empty(items[1].Children!);
        Assert.Null(items[2].Children);
    }

    [Fact]
    public async Task Required_items_are_asked_for_once_with_their_titles()
    {
        using var tree = new TestTree();
        var cache = Path.Combine(tree.Root, "cache.json");
        var client = new FakeClient { Children = { [1] = [10] } };
        var service = new WorkshopService(client, cache);

        Assert.True(await service.RefreshDetailsAsync([1, 2], Key, false, CancellationToken.None));
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(new[] { 1UL, 2UL }, client.Requests[0].Ids.Order());
        Assert.Equal(new[] { 10UL }, client.Requests[1].Ids);
        Assert.All(client.Requests, r => Assert.Equal(Key, r.Key));
        Assert.Equal("Item 10", service.GetDetails(10)!.Title);

        // Within the hour nothing is asked again, also after a restart.
        var reloaded = new WorkshopService(client, cache);
        Assert.True(await reloaded.RefreshDetailsAsync([1, 2], Key, false, CancellationToken.None));
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(new[] { 10UL }, reloaded.GetDetails(1)!.Children!);
    }

    [Fact]
    public async Task Adding_a_key_reads_again_the_items_read_without_it()
    {
        using var tree = new TestTree();
        var client = new FakeClient { Children = { [1] = [10] } };
        var service = new WorkshopService(client, Path.Combine(tree.Root, "cache.json"));

        await service.RefreshDetailsAsync([1], null, false, CancellationToken.None);
        Assert.Null(service.GetDetails(1)!.Children);

        await service.RefreshDetailsAsync([1], Key, false, CancellationToken.None);
        Assert.Equal(new[] { 10UL }, service.GetDetails(1)!.Children!);
        Assert.Equal(3, client.Requests.Count);
    }

    [Fact]
    public async Task Rejected_key_falls_back_to_the_keyless_api_and_is_not_tried_again()
    {
        using var tree = new TestTree();
        var client = new FakeClient { RejectKey = true };
        var service = new WorkshopService(client, Path.Combine(tree.Root, "cache.json"));

        Assert.True(await service.RefreshDetailsAsync([1], Key, false, CancellationToken.None));
        Assert.True(service.ApiKeyRejected);
        Assert.NotNull(service.GetDetails(1));
        Assert.Equal(new[] { Key, null }, client.Requests.Select(r => r.Key));

        Assert.True(await service.RefreshDetailsAsync([1], Key, true, CancellationToken.None));
        Assert.True(service.ApiKeyRejected);
        Assert.Equal(3, client.Requests.Count);
        Assert.Null(client.Requests[^1].Key);
    }

    [Fact]
    public async Task Keyless_refresh_keeps_the_required_items_of_the_same_version()
    {
        using var tree = new TestTree();
        var client = new FakeClient { Children = { [1] = [10] } };
        var service = new WorkshopService(client, Path.Combine(tree.Root, "cache.json"));

        await service.RefreshDetailsAsync([1], Key, false, CancellationToken.None);
        await service.RefreshDetailsAsync([1], null, true, CancellationToken.None);
        Assert.Equal(new[] { 10UL }, service.GetDetails(1)!.Children!);
    }

    [Fact]
    public async Task With_a_key_asks_GetDetails_with_children_and_reports_a_rejected_key()
    {
        var handler = new RecordingHandler("""{"response":{"publishedfiledetails":[{"result":1,"publishedfileid":"5","num_children":0}]}}""");
        using var client = new SteamWorkshopClient(new HttpClient(handler));

        var items = await client.GetDetailsAsync([5, 6], Key, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("api.steampowered.com", request.RequestUri!.Host);
        Assert.Equal("/IPublishedFileService/GetDetails/v1/", request.RequestUri.AbsolutePath);
        var query = request.RequestUri.Query;
        Assert.Contains("key=" + Key, query);
        Assert.Contains("includechildren=true", query);
        Assert.Contains("publishedfileids%5B1%5D=6", query);
        Assert.Empty(items[0].Children!);

        handler.Status = System.Net.HttpStatusCode.Forbidden;
        await Assert.ThrowsAsync<WorkshopApiKeyRejectedException>(() => client.GetDetailsAsync([5], Key, CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_key_posts_to_the_keyless_api()
    {
        var handler = new RecordingHandler("""{"response":{"publishedfiledetails":[{"result":1,"publishedfileid":"5"}]}}""");
        using var client = new SteamWorkshopClient(new HttpClient(handler));

        var items = await client.GetDetailsAsync([5], null, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/ISteamRemoteStorage/GetPublishedFileDetails/v1/", request.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("key=", request.RequestUri.Query);
        Assert.Null(items[0].Children);
    }

    private sealed class RecordingHandler(string json) : HttpMessageHandler
    {
        public System.Net.HttpStatusCode Status { get; set; } = System.Net.HttpStatusCode.OK;
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(json) });
        }
    }

    private sealed class FakeClient : IWorkshopClient
    {
        public Dictionary<ulong, List<ulong>> Children { get; } = [];
        public bool RejectKey { get; init; }
        public List<(ulong[] Ids, string? Key)> Requests { get; } = [];

        public Task<IReadOnlyList<WorkshopItemDetails>> GetDetailsAsync(IReadOnlyCollection<ulong> ids, string? apiKey, CancellationToken cancellationToken)
        {
            Requests.Add((ids.ToArray(), apiKey));
            if (apiKey is not null && RejectKey)
                throw new WorkshopApiKeyRejectedException(403);
            return Task.FromResult<IReadOnlyList<WorkshopItemDetails>>(ids.Select(id => new WorkshopItemDetails
            {
                Id = id,
                Result = 1,
                Title = $"Item {id}",
                TimeUpdated = Updated,
                FetchedAtUtc = DateTimeOffset.UtcNow,
                Children = apiKey is null ? null : (Children.GetValueOrDefault(id) ?? []),
            }).ToList());
        }
    }
}
