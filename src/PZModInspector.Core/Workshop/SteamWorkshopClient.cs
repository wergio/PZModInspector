using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PZModInspector.Core.Model;

namespace PZModInspector.Core.Workshop;

/// <summary>Steam answered 401/403 to a request made with the Web API key: wrong or revoked key.</summary>
public sealed class WorkshopApiKeyRejectedException(int statusCode)
    : Exception($"Steam rejected the Web API key (HTTP {statusCode})")
{
    public int StatusCode { get; } = statusCode;
}

public interface IWorkshopClient
{
    /// <summary>
    /// One entry per requested id, including the ones Steam does not find (Result != 1).
    /// With <paramref name="apiKey"/> the entries also carry the "Required items" (<see cref="WorkshopItemDetails.Children"/>).
    /// </summary>
    Task<IReadOnlyList<WorkshopItemDetails>> GetDetailsAsync(IReadOnlyCollection<ulong> ids, string? apiKey, CancellationToken cancellationToken);
}

/// <summary>
/// Official Steam Web API only, 100 items per request; the Workshop pages on steamcommunity.com are never read
/// (Steam blocks the address after a few quick requests). With a key: IPublishedFileService/GetDetails, which
/// also returns the "Required items". Without: ISteamRemoteStorage/GetPublishedFileDetails, which does not.
/// </summary>
public sealed class SteamWorkshopClient : IWorkshopClient, IDisposable
{
    private const string KeylessUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private const string DetailsUrl = "https://api.steampowered.com/IPublishedFileService/GetDetails/v1/";
    private const int BatchSize = 100;

    private readonly HttpClient _http;

    public SteamWorkshopClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PZModInspector/0.1 (+local tool)");
    }

    public async Task<IReadOnlyList<WorkshopItemDetails>> GetDetailsAsync(IReadOnlyCollection<ulong> ids, string? apiKey, CancellationToken cancellationToken)
    {
        var result = new List<WorkshopItemDetails>();
        foreach (var batch in ids.Distinct().Chunk(BatchSize))
        {
            using var response = string.IsNullOrEmpty(apiKey)
                ? await _http.PostAsync(KeylessUrl, KeylessForm(batch), cancellationToken).ConfigureAwait(false)
                : await _http.GetAsync(DetailsQuery(batch, apiKey), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(apiKey) && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new WorkshopApiKeyRejectedException((int)response.StatusCode);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            result.AddRange(ParseDetails(json.RootElement, DateTimeOffset.UtcNow, withChildren: !string.IsNullOrEmpty(apiKey)));
        }
        return result;
    }

    public void Dispose() => _http.Dispose();

    private static FormUrlEncodedContent KeylessForm(ulong[] batch)
    {
        var form = new List<KeyValuePair<string, string>> { new("itemcount", batch.Length.ToString(CultureInfo.InvariantCulture)) };
        for (var i = 0; i < batch.Length; i++)
            form.Add(new($"publishedfileids[{i}]", batch[i].ToString(CultureInfo.InvariantCulture)));
        return new FormUrlEncodedContent(form);
    }

    /// <summary>The key travels in the query string, as the Web API documents; over HTTPS, and never logged.</summary>
    private static string DetailsQuery(ulong[] batch, string apiKey)
    {
        var url = new StringBuilder(DetailsUrl)
            .Append("?key=").Append(Uri.EscapeDataString(apiKey))
            .Append("&includetags=true&includechildren=true");
        for (var i = 0; i < batch.Length; i++)
            url.Append(CultureInfo.InvariantCulture, $"&publishedfileids%5B{i}%5D={batch[i]}");
        return url.ToString();
    }

    /// <param name="withChildren">The response comes from GetDetails with includechildren: no "children" means none.</param>
    internal static IEnumerable<WorkshopItemDetails> ParseDetails(JsonElement root, DateTimeOffset fetchedAt, bool withChildren = false)
    {
        if (!root.TryGetProperty("response", out var response)
            || !response.TryGetProperty("publishedfiledetails", out var items)
            || items.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in items.EnumerateArray())
        {
            if (!ulong.TryParse(Str(item, "publishedfileid"), out var id))
                continue;
            var result = (int)(Long(item, "result") ?? 0);
            yield return new WorkshopItemDetails
            {
                Id = id,
                Result = result,
                Title = Str(item, "title"),
                // GetPublishedFileDetails calls it "description", GetDetails "file_description".
                Description = Str(item, "description") ?? Str(item, "file_description"),
                CreatorSteamId = Str(item, "creator"),
                TimeCreated = Time(Long(item, "time_created")),
                TimeUpdated = Time(Long(item, "time_updated")),
                FileSize = Long(item, "file_size"),
                PreviewUrl = Str(item, "preview_url"),
                Visibility = (int?)Long(item, "visibility"),
                Banned = Long(item, "banned") is > 0,
                BanReason = Str(item, "ban_reason"),
                Subscriptions = Long(item, "subscriptions"),
                LifetimeSubscriptions = Long(item, "lifetime_subscriptions"),
                Favorited = Long(item, "favorited"),
                Views = Long(item, "views"),
                Tags = item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                    ? tags.EnumerateArray().Select(t => Str(t, "tag")).OfType<string>().ToList()
                    : [],
                Children = Children(item, id, withChildren && result == 1),
                FetchedAtUtc = fetchedAt,
            };
        }
    }

    private static List<ulong>? Children(JsonElement item, ulong id, bool noneMeansEmpty)
    {
        if (!item.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
            return noneMeansEmpty ? [] : null;
        return children.EnumerateArray()
            .Select(c => ulong.TryParse(Str(c, "publishedfileid"), out var child) ? child : 0)
            .Where(child => child != 0 && child != id)
            .Distinct()
            .ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            _ => null,
        } : null;

    private static long? Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(p.GetString(), out var n) => n,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }

    private static DateTimeOffset? Time(long? unix) => unix is > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix.Value) : null;
}
