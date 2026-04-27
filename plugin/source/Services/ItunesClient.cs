using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Services;

/// <summary>
/// Apple's iTunes Search API: free, no key, no User-Agent constraints.
/// We use it strictly as an artwork fallback when Deezer's catalog misses
/// a YT-Music track — Apple's coverage of new releases is consistently
/// ahead of Deezer's, so this picks up most of the long tail.
///
/// The artwork URLs Apple returns are sized "100x100bb.jpg" by default;
/// we rewrite to "1000x1000bb.jpg" to get a high-res cover suitable for
/// Jellyfin's full-size player view.
/// </summary>
public class ItunesClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ItunesClient> _log;
    private readonly ConcurrentDictionary<string, (DateTime Expires, ITunesSearchResponse Payload)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    public ItunesClient(IHttpClientFactory httpFactory, ILogger<ItunesClient> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>
    /// Search Apple's catalog for songs matching the query. Returns at
    /// most `limit` results. Network/parse failures swallow to an empty
    /// result so callers don't need to handle exceptions.
    /// </summary>
    public async Task<ITunesSearchResponse?> SearchSongsAsync(string query, int limit, CancellationToken ct)
    {
        var key = $"song:{query.ToLowerInvariant()}:{limit}";
        if (_cache.TryGetValue(key, out var hit) && hit.Expires > DateTime.UtcNow) return hit.Payload;

        var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&entity=song&limit={limit}";
        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("iTunes {Url} -> {Status}", url, (int)resp.StatusCode);
                return null;
            }
            var body = await resp.Content.ReadFromJsonAsync<ITunesSearchResponse>(JsonOpts, ct).ConfigureAwait(false);
            if (body is not null) _cache[key] = (DateTime.UtcNow + CacheTtl, body);
            return body;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "iTunes request failed: {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Apple returns artwork URLs as "...100x100bb.jpg". Swap to a higher
    /// resolution suitable for full-size album covers. Returns the input
    /// unchanged if it doesn't match the expected pattern.
    /// </summary>
    public static string? UpscaleArtworkUrl(string? url, int target = 1000)
    {
        if (string.IsNullOrEmpty(url)) return url;
        // Replace "100x100bb" with "1000x1000bb" (or whatever target). Apple
        // accepts arbitrary square sizes here.
        return System.Text.RegularExpressions.Regex.Replace(
            url,
            @"\d{2,4}x\d{2,4}bb",
            $"{target}x{target}bb");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public class ITunesSearchResponse
    {
        [JsonPropertyName("resultCount")] public int ResultCount { get; set; }
        [JsonPropertyName("results")] public List<ITunesSong> Results { get; set; } = new();
    }

    public class ITunesSong
    {
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("collectionName")] public string? CollectionName { get; set; }
        [JsonPropertyName("artistId")] public long? ArtistId { get; set; }
        [JsonPropertyName("collectionId")] public long? CollectionId { get; set; }
        [JsonPropertyName("trackId")] public long? TrackId { get; set; }
        [JsonPropertyName("artworkUrl100")] public string? ArtworkUrl100 { get; set; }
        [JsonPropertyName("artworkUrl60")] public string? ArtworkUrl60 { get; set; }
    }
}
