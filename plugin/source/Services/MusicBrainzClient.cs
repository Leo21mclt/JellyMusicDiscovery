using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Services;

/// <summary>
/// MusicBrainz HTTP client with the 1 req/sec rate limit MB requires for the public
/// API, plus a small in-memory LRU-ish cache so repeated keystrokes don't hammer it.
/// </summary>
public class MusicBrainzClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MusicBrainzClient> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _nextSlot = DateTime.MinValue;

    private readonly ConcurrentDictionary<string, (DateTime Expires, object Payload)> _cache = new();

    public MusicBrainzClient(IHttpClientFactory httpFactory, ILogger<MusicBrainzClient> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public Task<MbSearchResult<MbArtist>> SearchArtistsAsync(string query, int limit, CancellationToken ct) =>
        SearchAsync<MbSearchResult<MbArtist>>("artist", query, limit, ct);

    public Task<MbSearchResult<MbReleaseGroup>> SearchReleaseGroupsAsync(string query, int limit, CancellationToken ct) =>
        SearchAsync<MbSearchResult<MbReleaseGroup>>("release-group", query, limit, ct);

    public Task<MbSearchResult<MbRecording>> SearchRecordingsAsync(string query, int limit, CancellationToken ct) =>
        SearchAsync<MbSearchResult<MbRecording>>("recording", query, limit, ct);

    public async Task<MbReleaseGroup?> GetReleaseGroupAsync(string mbid, CancellationToken ct)
    {
        var key = $"rg:{mbid}";
        if (TryCache<MbReleaseGroup>(key, out var hit)) return hit;

        var cfg = Plugin.Instance!.Configuration;
        var url = $"{cfg.MusicBrainzBaseUrl}/release-group/{mbid}?inc=artist-credits+releases&fmt=json";
        var rg = await SendAsync<MbReleaseGroup>(url, ct).ConfigureAwait(false);
        if (rg is not null) Cache(key, rg);
        return rg;
    }

    public async Task<MbRecording?> GetRecordingAsync(string mbid, CancellationToken ct)
    {
        var key = $"rec:{mbid}";
        if (TryCache<MbRecording>(key, out var hit)) return hit;

        var cfg = Plugin.Instance!.Configuration;
        var url = $"{cfg.MusicBrainzBaseUrl}/recording/{mbid}?inc=artist-credits+releases&fmt=json";
        var rec = await SendAsync<MbRecording>(url, ct).ConfigureAwait(false);
        if (rec is not null) Cache(key, rec);
        return rec;
    }

    private async Task<T> SearchAsync<T>(string entity, string query, int limit, CancellationToken ct) where T : class, new()
    {
        var key = $"{entity}:{query.ToLowerInvariant()}:{limit}";
        if (TryCache<T>(key, out var hit)) return hit;

        var cfg = Plugin.Instance!.Configuration;
        var encoded = Uri.EscapeDataString(query);
        var url = $"{cfg.MusicBrainzBaseUrl}/{entity}?query={encoded}&limit={limit}&fmt=json";

        var result = await SendAsync<T>(url, ct).ConfigureAwait(false) ?? new T();
        Cache(key, result);
        return result;
    }

    private async Task<T?> SendAsync<T>(string url, CancellationToken ct) where T : class
    {
        await Throttle(ct).ConfigureAwait(false);

        var cfg = Plugin.Instance!.Configuration;
        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(cfg.MusicBrainzUserAgent);
        req.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("MusicBrainz {Url} -> {Status}", url, (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MusicBrainz request failed: {Url}", url);
            return null;
        }
    }

    private async Task Throttle(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _nextSlot - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _nextSlot = DateTime.UtcNow.AddMilliseconds(1100);
        }
        finally { _gate.Release(); }
    }

    private bool TryCache<T>(string key, out T value) where T : class
    {
        if (_cache.TryGetValue(key, out var entry) && entry.Expires > DateTime.UtcNow && entry.Payload is T t)
        {
            value = t;
            return true;
        }
        value = default!;
        return false;
    }

    private void Cache(string key, object value)
    {
        var ttl = TimeSpan.FromSeconds(Plugin.Instance!.Configuration.MusicBrainzCacheTtlSeconds);
        _cache[key] = (DateTime.UtcNow.Add(ttl), value);
        if (_cache.Count > 1024) PruneOldest();
    }

    private void PruneOldest()
    {
        foreach (var kvp in _cache.OrderBy(k => k.Value.Expires).Take(_cache.Count - 768))
            _cache.TryRemove(kvp.Key, out _);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ---- DTOs ---------------------------------------------------------------

public class MbSearchResult<T>
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("artists")] public List<T>? Artists { get; set; }
    [JsonPropertyName("release-groups")] public List<T>? ReleaseGroups { get; set; }
    [JsonPropertyName("recordings")] public List<T>? Recordings { get; set; }

    public IEnumerable<T> Items => Artists ?? ReleaseGroups ?? Recordings ?? Enumerable.Empty<T>();
}

public class MbArtist
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    [JsonPropertyName("sort-name")] public string? SortName { get; set; }
    public string? Type { get; set; }
    public string? Country { get; set; }
    public int Score { get; set; }
    public string? Disambiguation { get; set; }
}

public class MbReleaseGroup
{
    public string Id { get; set; } = string.Empty;
    public string? Title { get; set; }
    [JsonPropertyName("primary-type")] public string? PrimaryType { get; set; }
    [JsonPropertyName("first-release-date")] public string? FirstReleaseDate { get; set; }
    [JsonPropertyName("artist-credit")] public List<MbArtistCredit>? ArtistCredit { get; set; }
    public List<MbRelease>? Releases { get; set; }
    public int Score { get; set; }
    public string? Disambiguation { get; set; }

    public string? PrimaryArtistName => ArtistCredit?.FirstOrDefault()?.Name ?? ArtistCredit?.FirstOrDefault()?.Artist?.Name;
    public string? PrimaryArtistId => ArtistCredit?.FirstOrDefault()?.Artist?.Id;
}

public class MbRecording
{
    public string Id { get; set; } = string.Empty;
    public string? Title { get; set; }
    public int? Length { get; set; } // milliseconds
    [JsonPropertyName("artist-credit")] public List<MbArtistCredit>? ArtistCredit { get; set; }
    public List<MbRelease>? Releases { get; set; }
    public int Score { get; set; }
    public string? Disambiguation { get; set; }

    public string? PrimaryArtistName => ArtistCredit?.FirstOrDefault()?.Name ?? ArtistCredit?.FirstOrDefault()?.Artist?.Name;
    public string? PrimaryArtistId => ArtistCredit?.FirstOrDefault()?.Artist?.Id;
    public TimeSpan? Duration => Length.HasValue ? TimeSpan.FromMilliseconds(Length.Value) : null;
}

public class MbArtistCredit
{
    public string? Name { get; set; }
    public string? Joinphrase { get; set; }
    public MbArtist? Artist { get; set; }
}

public class MbRelease
{
    public string Id { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Date { get; set; }
    public string? Country { get; set; }
}
