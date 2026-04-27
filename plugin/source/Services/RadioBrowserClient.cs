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
/// Lightweight client for radio-browser.info — a free, no-auth public
/// directory of ~37k internet radio stations. We use it to populate the
/// "📻 Radio" discovery playlists (Top Hip-Hop, iHeartRadio, etc.) the
/// same way DeezerClient/ItunesClient power the rest of discovery.
///
/// Mirrors: the API has multiple round-robin servers behind
/// `all.api.radio-browser.info`. We resolve to a specific mirror once
/// at construction and reuse it; fall back on next miss if that one
/// goes dark mid-session.
///
/// Docs: https://api.radio-browser.info/
/// </summary>
public class RadioBrowserClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<RadioBrowserClient> _log;
    private readonly ConcurrentDictionary<string, (DateTime Expires, List<RadioStation> Payload)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    // The .info domain isn't always the most reliable — alternates exist at
    // .ch, .de, etc. We pick a mirror at startup and rotate on failure.
    private static readonly string[] Mirrors = new[]
    {
        "https://de1.api.radio-browser.info",
        "https://nl1.api.radio-browser.info",
        "https://de2.api.radio-browser.info",
    };
    private int _mirrorIdx = 0;

    public RadioBrowserClient(IHttpClientFactory httpFactory, ILogger<RadioBrowserClient> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>
    /// Fetch up to `limit` stations matching the given query parameters.
    /// `queryParams` is appended raw to the path — caller is responsible
    /// for URL-encoding values. Common params:
    ///   tag=hip-hop, country=US, codec=MP3, order=clickcount, reverse=true
    /// See https://api.radio-browser.info/#Stations_by_clicks for the full list.
    /// </summary>
    public async Task<List<RadioStation>> SearchStationsAsync(string queryParams, int limit, CancellationToken ct)
    {
        var key = $"search:{queryParams.ToLowerInvariant()}:{limit}";
        if (_cache.TryGetValue(key, out var hit) && hit.Expires > DateTime.UtcNow) return hit.Payload;

        // /json/stations/search?<params>&limit=N — but radio-browser also
        // supports /json/stations/bytag, /json/stations/topclick, etc. The
        // unified `search` endpoint accepts most filters, so we use that.
        var path = $"/json/stations/search?{queryParams}&limit={limit}&hidebroken=true";
        var result = await TryEachMirrorAsync<List<RadioStation>>(path, ct).ConfigureAwait(false)
                     ?? new List<RadioStation>();
        _cache[key] = (DateTime.UtcNow + CacheTtl, result);
        return result;
    }

    /// <summary>
    /// Top-clicked stations globally. Useful default for a "Top Stations"
    /// playlist. Equivalent to /json/stations/topclick/{limit}.
    /// </summary>
    public Task<List<RadioStation>> GetTopClickedAsync(int limit, CancellationToken ct)
        => SearchStationsAsync("order=clickcount&reverse=true", limit, ct);

    /// <summary>
    /// Stations tagged with the given keyword (e.g. "iheartradio",
    /// "hip-hop", "pop"), ordered by popularity.
    /// </summary>
    public Task<List<RadioStation>> GetByTagAsync(string tag, int limit, CancellationToken ct)
        => SearchStationsAsync($"tag={Uri.EscapeDataString(tag)}&order=clickcount&reverse=true", limit, ct);

    /// <summary>
    /// Stations from the given country (ISO 3166-1 alpha-2 code; e.g.
    /// "US"), ordered by popularity.
    /// </summary>
    public Task<List<RadioStation>> GetByCountryAsync(string countryCode, int limit, CancellationToken ct)
        => SearchStationsAsync($"countrycode={Uri.EscapeDataString(countryCode)}&order=clickcount&reverse=true", limit, ct);

    private async Task<T?> TryEachMirrorAsync<T>(string path, CancellationToken ct) where T : class
    {
        // Round-robin through mirrors. On any mirror failure we advance to
        // the next one for the next request — sticky-on-success.
        for (int attempt = 0; attempt < Mirrors.Length; attempt++)
        {
            var mirror = Mirrors[(_mirrorIdx + attempt) % Mirrors.Length];
            var url = mirror + path;
            try
            {
                using var http = _httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                // radio-browser.info requires a User-Agent; their docs ask
                // for "AppName/Version" so they can spot abuse.
                http.DefaultRequestHeaders.UserAgent.ParseAdd("JellyMusicDiscovery/0.1");
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogDebug("radio-browser {Url} → {Status}", url, (int)resp.StatusCode);
                    continue;
                }
                var body = await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct).ConfigureAwait(false);
                if (body is not null)
                {
                    // Stick with this mirror for the next request.
                    _mirrorIdx = (_mirrorIdx + attempt) % Mirrors.Length;
                    return body;
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "radio-browser request failed: {Url}", url);
            }
        }
        return null;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Subset of radio-browser.info's station shape — only the fields we
    /// actually consume. Full schema:
    /// https://api.radio-browser.info/#Struct_Station
    /// </summary>
    public class RadioStation
    {
        [JsonPropertyName("stationuuid")] public string? StationUuid { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("url_resolved")] public string? UrlResolved { get; set; }
        [JsonPropertyName("homepage")] public string? Homepage { get; set; }
        [JsonPropertyName("favicon")] public string? Favicon { get; set; }
        [JsonPropertyName("tags")] public string? Tags { get; set; }              // comma-separated
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("countrycode")] public string? CountryCode { get; set; }
        [JsonPropertyName("language")] public string? Language { get; set; }
        [JsonPropertyName("codec")] public string? Codec { get; set; }
        [JsonPropertyName("bitrate")] public int? Bitrate { get; set; }
        [JsonPropertyName("clickcount")] public int? ClickCount { get; set; }
        [JsonPropertyName("votes")] public int? Votes { get; set; }
    }
}
