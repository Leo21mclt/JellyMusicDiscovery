using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Services;

/// <summary>
/// Tiny client for the public LRC LIB API (lrclib.net) — same source
/// the LrcLib Jellyfin plugin uses, but called directly so we can serve
/// lyrics for our stub tracks (which don't flow through the normal
/// library/provider pipeline).
/// </summary>
public class LrcLibClient
{
    private const string Base = "https://lrclib.net";
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LrcLibClient> _log;
    // Lyrics never change; cache by (artist|title|album|duration) for the
    // life of the process. A null result is also cached so we don't repeat
    // lookups for tracks LrcLib doesn't have.
    private readonly ConcurrentDictionary<string, LrcResult?> _cache = new();

    public LrcLibClient(IHttpClientFactory httpFactory, ILogger<LrcLibClient> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<LrcResult?> GetAsync(string artist, string title, string? album, int? durationSec, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;
        var key = $"{artist}|{title}|{album}|{durationSec}".ToLowerInvariant();
        if (_cache.TryGetValue(key, out var hit)) return hit;

        try
        {
            var url = $"{Base}/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
            if (!string.IsNullOrWhiteSpace(album)) url += $"&album_name={Uri.EscapeDataString(album)}";
            if (durationSec is int d && d > 0) url += $"&duration={d}";

            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(8);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("JellyMusicDiscovery/0.1 (Jellyfin plugin)");
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 = LrcLib has no lyrics for this track; cache the null.
                _log.LogDebug("[lrclib] {Artist} - {Title}: {Status}", artist, title, (int)resp.StatusCode);
                _cache[key] = null;
                return null;
            }
            var result = await resp.Content.ReadFromJsonAsync<LrcResult>(JsonOpts, ct).ConfigureAwait(false);
            _cache[key] = result;
            _log.LogInformation("[lrclib] cached lyrics for {Artist} - {Title} (synced={Synced} plain={Plain})",
                artist, title,
                !string.IsNullOrEmpty(result?.SyncedLyrics),
                !string.IsNullOrEmpty(result?.PlainLyrics));
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[lrclib] lookup failed for {Artist} - {Title}", artist, title);
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public class LrcResult
    {
        public long Id { get; set; }
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("albumName")] public string? AlbumName { get; set; }
        // LrcLib returns duration as a floating-point seconds value
        // (e.g. 204.355917). Use double here, not int, or deserialize fails.
        public double? Duration { get; set; }
        public bool Instrumental { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    }
}
