using System.Text.Json;
using PZModInspector.Core.Model;

namespace PZModInspector.Core.Workshop;

/// <summary>Workshop data with an on-disk cache, so Steam is asked at most once an hour.</summary>
public sealed class WorkshopService
{
    public static readonly TimeSpan DetailsMaxAge = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IWorkshopClient _client;
    private readonly string _cachePath;
    private readonly object _lock = new();
    private CacheData _cache;

    /// <summary>Not tried again until the program restarts or the key changes.</summary>
    private string? _rejectedKey;

    public WorkshopService(IWorkshopClient client, string cachePath)
    {
        _client = client;
        _cachePath = cachePath;
        _cache = LoadCache(cachePath);
    }

    public string? LastError { get; private set; }

    /// <summary>Steam refused the key at the last refresh; the data then comes from the keyless API, without "Required items".</summary>
    public bool ApiKeyRejected { get; private set; }

    public DateTimeOffset? LastDetailsFetch
    {
        get { lock (_lock) return _cache.Details.Count == 0 ? null : _cache.Details.Values.Max(d => d.FetchedAtUtc); }
    }

    public WorkshopItemDetails? GetDetails(ulong id)
    {
        lock (_lock) return _cache.Details.GetValueOrDefault(id);
    }

    /// <summary>
    /// Asks Steam for the items not read in the last hour, plus their "Required items" so their title and state are known.
    /// With a key, items read without one are read again to learn their "Required items". At most 100 items per request.
    /// </summary>
    /// <returns>false when Steam could not be reached; the cached data stays usable.</returns>
    public async Task<bool> RefreshDetailsAsync(IEnumerable<ulong> ids, string? apiKey, bool force, CancellationToken cancellationToken)
    {
        apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (apiKey is not null && apiKey == _rejectedKey)
            apiKey = null;
        else
            ApiKeyRejected = false;

        var now = DateTimeOffset.UtcNow;
        List<ulong> toFetch;
        lock (_lock)
        {
            var listed = ids.ToHashSet();
            var required = listed.SelectMany(id => _cache.Details.GetValueOrDefault(id)?.Children ?? []);
            toFetch = listed.Concat(required).Distinct()
                .Where(id => force || IsStale(id, apiKey is not null, now))
                .ToList();
        }
        if (toFetch.Count == 0)
            return true;

        try
        {
            var fetched = await FetchAsync(toFetch, apiKey, cancellationToken).ConfigureAwait(false);
            // "Required items" seen for the first time: one more request for their title and state.
            List<ulong> newRequired;
            lock (_lock)
            {
                newRequired = fetched.SelectMany(d => d.Children ?? [])
                    .Where(id => !_cache.Details.ContainsKey(id))
                    .Distinct()
                    .ToList();
            }
            if (newRequired.Count > 0)
                await FetchAsync(newRequired, ApiKeyRejected ? null : apiKey, cancellationToken).ConfigureAwait(false);
            LastError = null;
            Save();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            LastError = ex.Message;
            Save();
            return false;
        }
    }

    private bool IsStale(ulong id, bool withKey, DateTimeOffset now)
    {
        if (!_cache.Details.TryGetValue(id, out var d))
            return true;
        return now - d.FetchedAtUtc > DetailsMaxAge || (withKey && d.IsAvailable && d.Children is null);
    }

    private async Task<IReadOnlyList<WorkshopItemDetails>> FetchAsync(List<ulong> ids, string? apiKey, CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkshopItemDetails> details;
        try
        {
            details = await _client.GetDetailsAsync(ids, apiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkshopApiKeyRejectedException ex) when (apiKey is not null)
        {
            _rejectedKey = apiKey;
            ApiKeyRejected = true;
            LastError = ex.Message;
            details = await _client.GetDetailsAsync(ids, null, cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            // Read without the key: keep the "Required items" known for the same version of the item.
            foreach (var d in details)
            {
                _cache.Details[d.Id] = d.Children is null && _cache.Details.GetValueOrDefault(d.Id) is { Children: not null } old
                                       && old.TimeUpdated == d.TimeUpdated
                    ? d with { Children = old.Children }
                    : d;
            }
        }
        return details;
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_lock)
                json = JsonSerializer.Serialize(_cache, JsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
        }
    }

    private static CacheData LoadCache(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<CacheData>(File.ReadAllText(path), JsonOptions) is { Version: CacheData.CurrentVersion } data)
                return data;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new CacheData();
    }

    /// <summary>Version 1 also had the Workshop pages read before this used the Web API: they are ignored and dropped at the next save.</summary>
    private sealed class CacheData
    {
        public const int CurrentVersion = 1;
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<ulong, WorkshopItemDetails> Details { get; set; } = [];
    }
}
