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
/// Public Deezer API: no key, no User-Agent, no per-IP daily limit
/// (just a soft ~50 req/5s burst cap that we won't approach with a single
/// user typing in a search box). Used for live search; MB is reserved for
/// the rare cases where Deezer's catalog is missing something.
/// </summary>
public class DeezerClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DeezerClient> _log;
    private readonly ConcurrentDictionary<string, (DateTime Expires, object Payload)> _cache = new();

    public DeezerClient(IHttpClientFactory httpFactory, ILogger<DeezerClient> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public Task<DzSearchResult<DzArtist>> SearchArtistsAsync(string query, int limit, CancellationToken ct) =>
        SearchAsync<DzArtist>("artist", query, limit, ct);

    public Task<DzSearchResult<DzAlbum>> SearchAlbumsAsync(string query, int limit, CancellationToken ct) =>
        SearchAsync<DzAlbum>("album", query, limit, ct);

    public Task<DzSearchResult<DzTrack>> SearchTracksAsync(string query, int limit, CancellationToken ct) =>
        SearchAsync<DzTrack>("track", query, limit, ct);

    public Task<DzAlbum?> GetAlbumAsync(long id, CancellationToken ct) =>
        FetchOneAsync<DzAlbum>($"album/{id}", $"album:{id}", ct);

    public Task<DzArtist?> GetArtistAsync(long id, CancellationToken ct) =>
        FetchOneAsync<DzArtist>($"artist/{id}", $"artist:{id}", ct);

    public Task<DzAlbumTracks?> GetAlbumTracksAsync(long albumId, CancellationToken ct) =>
        FetchOneAsync<DzAlbumTracks>($"album/{albumId}/tracks", $"album:{albumId}:tracks", ct);

    /// <summary>Get all albums for a given artist — used to populate the artist landing page.</summary>
    public Task<DzSearchResult<DzAlbum>?> GetArtistAlbumsAsync(long artistId, int limit, CancellationToken ct) =>
        FetchOneAsync<DzSearchResult<DzAlbum>>($"artist/{artistId}/albums?limit={limit}", $"artist:{artistId}:albums:{limit}", ct);

    /// <summary>
    /// Get an artist's top tracks. Powers shuffle-artist and instant-mix
    /// flows where the client wants a list of audio items keyed by the artist.
    /// </summary>
    public Task<DzSearchResult<DzTrack>?> GetArtistTopTracksAsync(long artistId, int limit, CancellationToken ct) =>
        FetchOneAsync<DzSearchResult<DzTrack>>($"artist/{artistId}/top?limit={limit}", $"artist:{artistId}:top:{limit}", ct);

    /// <summary>
    /// Get artists related to a given artist — Deezer's curated "fans of X
    /// also like" list. Powers the "More Like This" row on artist pages.
    /// </summary>
    public Task<DzSearchResult<DzArtist>?> GetArtistRelatedAsync(long artistId, int limit, CancellationToken ct) =>
        FetchOneAsync<DzSearchResult<DzArtist>>($"artist/{artistId}/related?limit={limit}", $"artist:{artistId}:related:{limit}", ct);

    /// <summary>
    /// Get chart tracks for a genre. genreId=0 means global (all genres).
    /// Powers the "Top Rock", "Top Pop", etc. discovery playlists.
    /// </summary>
    public Task<DzSearchResult<DzTrack>?> GetChartTracksAsync(long genreId, int limit, CancellationToken ct) =>
        FetchOneAsync<DzSearchResult<DzTrack>>($"chart/{genreId}/tracks?limit={limit}", $"chart:{genreId}:tracks:{limit}", ct);

    /// <summary>
    /// Get chart albums for a genre — Deezer's "currently popular" album list,
    /// the closest thing to a "new releases" feed available without auth.
    /// </summary>
    public Task<DzSearchResult<DzAlbum>?> GetChartAlbumsAsync(long genreId, int limit, CancellationToken ct) =>
        FetchOneAsync<DzSearchResult<DzAlbum>>($"chart/{genreId}/albums?limit={limit}", $"chart:{genreId}:albums:{limit}", ct);

    /// <summary>
    /// Get the tracks of a specific Deezer playlist (their own, or any public
    /// playlist by id). Used to expand chart-album lists into track listings
    /// for our synthetic discovery playlists.
    /// </summary>
    public Task<DzAlbumTracks?> GetPlaylistTracksAsync(long playlistId, int limit, CancellationToken ct) =>
        FetchOneAsync<DzAlbumTracks>($"playlist/{playlistId}/tracks?limit={limit}", $"playlist:{playlistId}:tracks:{limit}", ct);

    private async Task<DzSearchResult<T>> SearchAsync<T>(string entity, string query, int limit, CancellationToken ct) where T : class
    {
        var key = $"{entity}:{query.ToLowerInvariant()}:{limit}";
        if (TryCache<DzSearchResult<T>>(key, out var hit)) return hit;

        var url = $"https://api.deezer.com/search/{entity}?q={Uri.EscapeDataString(query)}&limit={limit}";
        var result = await SendAsync<DzSearchResult<T>>(url, ct).ConfigureAwait(false) ?? new DzSearchResult<T>();
        Cache(key, result);
        return result;
    }

    private async Task<T?> FetchOneAsync<T>(string path, string cacheKey, CancellationToken ct) where T : class
    {
        if (TryCache<T>(cacheKey, out var hit)) return hit;
        var result = await SendAsync<T>($"https://api.deezer.com/{path}", ct).ConfigureAwait(false);
        if (result is not null) Cache(cacheKey, result);
        return result;
    }

    private async Task<T?> SendAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("Deezer {Url} -> {Status}", url, (int)resp.StatusCode);
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Deezer request failed: {Url}", url);
            return null;
        }
    }

    private bool TryCache<T>(string key, out T value) where T : class
    {
        if (_cache.TryGetValue(key, out var entry) && entry.Expires > DateTime.UtcNow && entry.Payload is T t)
        { value = t; return true; }
        value = default!;
        return false;
    }

    private void Cache(string key, object value)
    {
        // Shorter TTL than MB — Deezer is fast, freshness matters more than savings.
        _cache[key] = (DateTime.UtcNow.AddMinutes(5), value);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // --- DTOs ----------------------------------------------------------

    public class DzSearchResult<T>
    {
        public List<T> Data { get; set; } = new();
        public int Total { get; set; }
    }

    public class DzArtist
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        [JsonPropertyName("picture_big")] public string? PictureBig { get; set; }
        [JsonPropertyName("picture_xl")] public string? PictureXl { get; set; }
        [JsonPropertyName("nb_album")] public int? AlbumCount { get; set; }
        [JsonPropertyName("nb_fan")] public long? Fans { get; set; }
    }

    public class DzAlbum
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        [JsonPropertyName("cover_big")] public string? CoverBig { get; set; }
        [JsonPropertyName("cover_xl")] public string? CoverXl { get; set; }
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
        [JsonPropertyName("nb_tracks")] public int? TrackCount { get; set; }
        public DzArtist? Artist { get; set; }
        public string? Type { get; set; }
        [JsonPropertyName("record_type")] public string? RecordType { get; set; }
        [JsonPropertyName("explicit_lyrics")] public bool? ExplicitLyrics { get; set; }
    }

    public class DzTrack
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        [JsonPropertyName("title_short")] public string? TitleShort { get; set; }
        public int? Duration { get; set; } // seconds
        public DzArtist? Artist { get; set; }
        public DzAlbum? Album { get; set; }
        [JsonPropertyName("track_position")] public int? TrackPosition { get; set; }
        [JsonPropertyName("disk_number")] public int? DiskNumber { get; set; }
    }

    public class DzAlbumTracks
    {
        public List<DzTrack> Data { get; set; } = new();
        public int Total { get; set; }
    }
}
