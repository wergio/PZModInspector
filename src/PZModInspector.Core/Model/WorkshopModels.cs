namespace PZModInspector.Core.Model;

/// <summary>What Steam recorded locally for an installed Workshop item (appworkshop_108600.acf).</summary>
public sealed record WorkshopInstallInfo(
    ulong Id,
    long? Size,
    DateTimeOffset? TimeUpdated,
    DateTimeOffset? LatestTimeUpdated,
    DateTimeOffset? TimeTouched,
    string? Manifest,
    string? LatestManifest);

/// <summary>
/// Public Workshop data: IPublishedFileService/GetDetails with a Web API key, otherwise the keyless
/// ISteamRemoteStorage/GetPublishedFileDetails, which has everything except the "Required items".
/// </summary>
public sealed record WorkshopItemDetails
{
    public required ulong Id { get; init; }

    /// <summary>Steam EResult: 1 = OK, 9 = not found (removed, private or hidden).</summary>
    public required int Result { get; init; }

    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? CreatorSteamId { get; init; }
    public DateTimeOffset? TimeCreated { get; init; }
    public DateTimeOffset? TimeUpdated { get; init; }
    public long? FileSize { get; init; }
    public string? PreviewUrl { get; init; }

    /// <summary>0 = public, 1 = friends only, 2 = private, 3 = unlisted.</summary>
    public int? Visibility { get; init; }

    public bool Banned { get; init; }
    public string? BanReason { get; init; }
    public long? Subscriptions { get; init; }
    public long? LifetimeSubscriptions { get; init; }
    public long? Favorited { get; init; }
    public long? Views { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>"Required items" of the Workshop page; null when unknown (read without a Web API key).</summary>
    public IReadOnlyList<ulong>? Children { get; init; }

    public DateTimeOffset FetchedAtUtc { get; init; }

    public bool IsAvailable => Result == 1;
}

/// <param name="OlderBuilds">
/// The game majors the item is for when they all come before the game's (e.g. [41] with Build 42), from its "Build N"
/// Workshop tags; null otherwise.
/// </param>
public sealed record RequiredWorkshopItem(ulong Id, string Title, IReadOnlyList<int>? OlderBuilds = null);

public static class WorkshopLinks
{
    public static string Item(ulong id) => $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";
    public static string ChangeNotes(ulong id) => $"https://steamcommunity.com/sharedfiles/filedetails/changelog/{id}";
    public static string SteamClient(ulong id) => $"steam://url/CommunityFilePage/{id}";
    public static string Profile(string steamId) => $"https://steamcommunity.com/profiles/{steamId}";
}
