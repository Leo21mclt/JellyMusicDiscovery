using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
/// Maintains a list of curated "discovery" playlists from external sources
/// (Deezer charts, YouTube Music charts/mood) and exposes them as synthetic
/// Jellyfin playlists. Refreshes periodically (every 6 hours) and persists
/// the snapshot to disk so playlists are available immediately on startup
/// without waiting for the first refresh round-trip.
///
/// File: &lt;plugin-data-dir&gt;/discovery-playlists.json
/// </summary>
public class DiscoveryPlaylistFeed
{
    public class PlaylistDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public string Source { get; set; } = string.Empty;     // "deezer" | "ytmusic"
        public string? SubLabel { get; set; }                  // e.g. "Top 100 Global"
        public List<Guid> TrackIds { get; set; } = new();      // ordered list of stub track guids
        public DateTime FetchedAt { get; set; }
    }

    private readonly DeezerClient _deezer;
    private readonly ItunesClient _itunes;
    private readonly RadioBrowserClient _radio;
    private readonly DiscoveryItemCache _itemCache;
    private readonly LibraryRegistrar _registrar;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DiscoveryPlaylistFeed> _log;
    private readonly ConcurrentDictionary<Guid, PlaylistDto> _byId = new();
    private readonly string? _persistFile;
    private Timer? _refreshTimer;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    public DiscoveryPlaylistFeed(
        DeezerClient deezer,
        ItunesClient itunes,
        RadioBrowserClient radio,
        DiscoveryItemCache itemCache,
        LibraryRegistrar registrar,
        IHttpClientFactory httpFactory,
        ILogger<DiscoveryPlaylistFeed> log)
    {
        _deezer = deezer;
        _itunes = itunes;
        _radio = radio;
        _itemCache = itemCache;
        _registrar = registrar;
        _httpFactory = httpFactory;
        _log = log;

        try
        {
            var dir = Plugin.Instance?.DataFolderPath;
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                _persistFile = Path.Combine(dir, "discovery-playlists.json");
                Load();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-feed] init failed; running in-memory only");
        }

        _log.LogInformation("[mdiscover-feed] constructed; will refresh in 30s and every {H}h", RefreshInterval.TotalHours);
        // Kick off an initial refresh on a delay so the plugin doesn't block
        // startup on Deezer/YT-Music HTTP. If the disk cache already has
        // entries, those serve while the refresh runs.
        _refreshTimer = new Timer(_ => _ = RefreshAsync(CancellationToken.None),
            null, TimeSpan.FromSeconds(30), RefreshInterval);
    }

    /// <summary>
    /// Order: YT Music sections first, then Deezer charts, then Radio,
    /// with anything else falling to the end. Stable name-sort within
    /// each source bucket so the YT-Music section order itself stays
    /// intact (Workout, Party, Commute, etc. as configured upstream).
    /// </summary>
    public IReadOnlyList<PlaylistDto> All() => _byId.Values
        .OrderBy(p => SourcePriority(p.Source))
        .ThenBy(p => p.Name)
        .ToArray();

    private static int SourcePriority(string source) => source switch
    {
        "ytmusic" => 0,
        "deezer"  => 1,
        "radio"   => 2,
        _         => 3,
    };

    public PlaylistDto? Get(Guid id) => _byId.TryGetValue(id, out var p) ? p : null;

    /// <summary>
    /// Refresh from sources. Catches exceptions per-source so a single
    /// flaky upstream doesn't kill the whole batch.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            await RefreshDeezerAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-feed] Deezer refresh failed");
        }

        try
        {
            await RefreshYtMusicAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-feed] YT Music refresh failed");
        }

        try
        {
            await RefreshRadioAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-feed] radio refresh failed");
        }

        // YT Music thumbnails are video screenshots — ugly. Try to upgrade
        // each YT-sourced track's image to a Deezer album cover by matching
        // on artist+title. Best-effort and idempotent (skips already-upgraded
        // entries), so fine to run on every refresh.
        try
        {
            await UpgradeYtImagesFromDeezerAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-feed] image upgrade pass failed");
        }

        Save();
        _log.LogInformation("[mdiscover-feed] refresh complete: {N} playlists total", _byId.Count);
    }

    // -----------------------------------------------------------------
    // Deezer source — chart endpoints, no auth needed.
    // -----------------------------------------------------------------
    // Genre IDs from https://api.deezer.com/genre. We pick the ones the
    // user asked for; "Asian" (75) is the closest stand-in for J-Pop on
    // Deezer (their dataset doesn't separate J-Pop, K-Pop, Mandopop).
    private static readonly (long GenreId, string Name, string SubLabel)[] DeezerCharts = new[]
    {
        (0L,   "🔥 Top 100 Global",     "Most-played tracks worldwide"),
        (152L, "🎸 Top Rock",            "Rock chart"),
        (132L, "🎤 Top Pop",             "Pop chart"),
        (113L, "🎧 Top Electronic",      "Dance & electronic chart"),
        (116L, "🎵 Top Hip-Hop",         "Rap & hip-hop chart"),
        (464L, "🤘 Top Metal",           "Metal chart"),
        // J-Pop intentionally omitted from Deezer — their "Asian" genre is
        // mush of K-Pop / Mandopop / J-Pop. We pull a proper J-Pop chart
        // from YouTube Music's Japan regional chart in the YT Music source.
    };

    // Deezer "Top USA" — a curated editorial playlist (ID stable as of 2025-26).
    // If Deezer ever rotates the ID we'd see an empty result and need to update.
    private const long DeezerTopUsaPlaylistId = 1313621735;

    private async Task RefreshDeezerAsync(CancellationToken ct)
    {
        // US Top 50 — Deezer's chart endpoint is global-only; we use a known
        // US-curated editorial playlist instead.
        try
        {
            var us = await _deezer.GetPlaylistTracksAsync(DeezerTopUsaPlaylistId, 50, ct).ConfigureAwait(false);
            if (us?.Data is { Count: > 0 } usTracks)
            {
                var pl = SynthesizePlaylist("deezer-us-top50", "🇺🇸 Top 50 USA", "Top tracks in the United States", "deezer");
                pl.TrackIds.Clear();
                foreach (var t in usTracks)
                {
                    var stubId = MaterializeTrack(t);
                    if (stubId.HasValue) pl.TrackIds.Add(stubId.Value);
                }
                pl.ImageUrl = usTracks.FirstOrDefault()?.Album?.CoverXl
                    ?? usTracks.FirstOrDefault()?.Album?.CoverBig;
                pl.FetchedAt = DateTime.UtcNow;
                _byId[pl.Id] = pl;
                _log.LogInformation("[mdiscover-feed] Deezer US Top 50 = {N} tracks", pl.TrackIds.Count);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[mdiscover-feed] US Top 50 fetch failed (non-fatal)");
        }

        // Curated chart playlists.
        foreach (var (genreId, name, subLabel) in DeezerCharts)
        {
            var resp = await _deezer.GetChartTracksAsync(genreId, 100, ct).ConfigureAwait(false);
            if (resp?.Data is null || resp.Data.Count == 0) continue;

            var pl = SynthesizePlaylist($"deezer-chart-{genreId}", name, subLabel, "deezer");
            pl.TrackIds.Clear();
            foreach (var t in resp.Data)
            {
                var stubId = MaterializeTrack(t);
                if (stubId.HasValue) pl.TrackIds.Add(stubId.Value);
            }
            // Image: use the first track's album cover.
            pl.ImageUrl = resp.Data.FirstOrDefault()?.Album?.CoverXl
                ?? resp.Data.FirstOrDefault()?.Album?.CoverBig;
            pl.FetchedAt = DateTime.UtcNow;
            _byId[pl.Id] = pl;
            _log.LogInformation("[mdiscover-feed] Deezer chart '{Name}' = {N} tracks", name, pl.TrackIds.Count);
        }

        // "New Releases This Week" — synthesize from chart-albums by
        // expanding the top albums to their tracks. Limit to 5 albums to
        // keep API calls reasonable; that gives ~50 tracks total.
        try
        {
            var albumChart = await _deezer.GetChartAlbumsAsync(0, 10, ct).ConfigureAwait(false);
            if (albumChart?.Data is { Count: > 0 } albums)
            {
                var pl = SynthesizePlaylist("deezer-new-releases", "🆕 New Releases", "Trending new albums", "deezer");
                pl.TrackIds.Clear();
                foreach (var al in albums.Take(8))
                {
                    var tracks = await _deezer.GetAlbumTracksAsync(al.Id, ct).ConfigureAwait(false);
                    if (tracks?.Data is null) continue;
                    foreach (var t in tracks.Data.Take(5)) // pick a few per album
                    {
                        // Track responses from /album/{id}/tracks omit the
                        // album object — stamp it on so the stub track has
                        // proper album metadata.
                        if (t.Album is null)
                            t.Album = new DeezerClient.DzAlbum { Id = al.Id, Title = al.Title, CoverBig = al.CoverBig, CoverXl = al.CoverXl };
                        var stubId = MaterializeTrack(t);
                        if (stubId.HasValue) pl.TrackIds.Add(stubId.Value);
                    }
                }
                pl.ImageUrl = albums.FirstOrDefault()?.CoverXl ?? albums.FirstOrDefault()?.CoverBig;
                pl.FetchedAt = DateTime.UtcNow;
                _byId[pl.Id] = pl;
                _log.LogInformation("[mdiscover-feed] Deezer new releases = {N} tracks", pl.TrackIds.Count);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[mdiscover-feed] new releases build failed (non-fatal)");
        }
    }

    /// <summary>
    /// Convert a Deezer track into a cached stub Audio item, returning the
    /// stub guid. Reuses existing cache entry if present (deterministic guid).
    /// </summary>
    private Guid? MaterializeTrack(DeezerClient.DzTrack t)
    {
        if (t.Id == 0 || string.IsNullOrEmpty(t.Title)) return null;
        var stubId = DiscoveryManager.StubGuid("dz-track", t.Id.ToString());
        var entry = new DiscoveryItemCache.Entry
        {
            Kind = "track",
            Artist = t.Artist?.Name,
            AlbumName = t.Album?.Title,
            TrackTitle = t.Title,
            DeezerTrackId = t.Id,
            DeezerAlbumId = t.Album?.Id,
            DeezerArtistId = t.Artist?.Id,
            PrimaryImageUrl = t.Album?.CoverBig,
            DurationSeconds = t.Duration,
        };
        _itemCache.Put(stubId, entry);
        _registrar.RegisterStubTrack(stubId, entry);

        // Also opportunistically cache the album so click-through works.
        if (t.Album is { Id: > 0 } al)
        {
            var albumStubId = DiscoveryManager.StubGuid("dz-album", al.Id.ToString());
            if (!_itemCache.TryGet(albumStubId, out var existing) || existing is null)
            {
                _itemCache.Put(albumStubId, new DiscoveryItemCache.Entry
                {
                    Kind = "album",
                    Artist = t.Artist?.Name,
                    AlbumName = al.Title,
                    DeezerAlbumId = al.Id,
                    DeezerArtistId = t.Artist?.Id,
                    PrimaryImageUrl = al.CoverXl ?? al.CoverBig,
                });
            }
        }
        return stubId;
    }

    // -----------------------------------------------------------------
    // YouTube Music source — through ytmusic-stream-server's discover
    // endpoints. Auth-free options: charts and mood-based playlists.
    // Personalized "Daily Mixes" require OAuth; we offer "Daily Picks"
    // (curated home-page editorial sections) instead.
    // -----------------------------------------------------------------
    private async Task RefreshYtMusicAsync(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var baseUrl = cfg?.YtMusicStreamServerUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _log.LogDebug("[mdiscover-feed] YT Music skipped: YtMusicStreamServerUrl not set");
            return;
        }

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);

        // The stream server's /discover endpoint returns a list of
        // {section, title, tracks: [{videoId, title, artist, album, duration, thumbnail}]}.
        var url = $"{baseUrl.TrimEnd('/')}/discover/playlists";
        try
        {
            var resp = await http.GetFromJsonAsync<YtMusicDiscoverResponse>(url, ct).ConfigureAwait(false);
            if (resp?.Sections is null || resp.Sections.Count == 0)
            {
                _log.LogInformation("[mdiscover-feed] YT Music discover: 0 sections");
                return;
            }

            foreach (var s in resp.Sections)
            {
                if (string.IsNullOrEmpty(s.Title) || s.Tracks is null || s.Tracks.Count == 0) continue;

                var pl = SynthesizePlaylist($"ytmusic-{s.Section}-{Slug(s.Title)}", s.Title, s.SubLabel, "ytmusic");
                pl.TrackIds.Clear();
                int withArtist = 0;
                foreach (var t in s.Tracks)
                {
                    var stubId = MaterializeYtMusicTrack(t);
                    if (stubId.HasValue) pl.TrackIds.Add(stubId.Value);
                    if (!string.IsNullOrEmpty(t.Artist)) withArtist++;
                }
                pl.ImageUrl = s.Tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.Thumbnail))?.Thumbnail;
                pl.FetchedAt = DateTime.UtcNow;
                _byId[pl.Id] = pl;
                // Log first track's artist so the user can verify the wire payload
                // contains the artist field on a per-section basis.
                var sample = s.Tracks.FirstOrDefault();
                _log.LogInformation(
                    "[mdiscover-feed] YT Music '{Title}' = {N} tracks ({WithArtist} w/ artist); sample: '{ST}' — '{SA}'",
                    s.Title, pl.TrackIds.Count, withArtist,
                    sample?.Title ?? "(none)", sample?.Artist ?? "(null)");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-feed] YT Music fetch failed (server endpoint may not be deployed yet)");
        }
    }

    /// <summary>
    /// Convert a YT-Music track into a stub Audio item. We don't have a
    /// Deezer track id, so we use the YT video id as the stub key. The
    /// playback flow already searches YT Music by artist+title, which
    /// matches the same video on resolve.
    /// </summary>
    private Guid? MaterializeYtMusicTrack(YtMusicDiscoverTrack t)
    {
        if (string.IsNullOrEmpty(t.VideoId) || string.IsNullOrEmpty(t.Title)) return null;
        var stubId = DiscoveryManager.StubGuid("yt-track", t.VideoId);
        var entry = new DiscoveryItemCache.Entry
        {
            Kind = "track",
            Artist = t.Artist,
            AlbumName = t.Album,
            TrackTitle = t.Title,
            // Deezer fields stay null — when the user plays this track our
            // /Audio proxy resolves by artist+title via ytmusic-stream-server,
            // which is the same path Deezer-sourced tracks use.
            PrimaryImageUrl = t.Thumbnail,
            DurationSeconds = t.DurationSeconds,
        };
        _itemCache.Put(stubId, entry);
        _registrar.RegisterStubTrack(stubId, entry);
        return stubId;
    }

    // -----------------------------------------------------------------
    // Internet radio source (radio-browser.info). Each curated section
    // becomes a synthetic playlist whose tracks are radio stations. The
    // /Audio file resolver detects RadioStreamUrl on the cache entry and
    // 302-redirects the player to the upstream stream. No transcoding,
    // no caching — pure passthrough.
    //
    // Sections we ship by default:
    //   📻 Top US Stations           — most-clicked stations in the US
    //   📻 iHeartRadio               — stations tagged iheartradio
    //   📻 Hip-Hop / R&B Radio       — popular by tag
    //   📻 Pop / Top 40 Radio        — popular by tag
    //   📻 Rock Radio                — popular by tag
    //   📻 News & Talk               — popular by tag
    // -----------------------------------------------------------------
    private static readonly (string Key, string Title, string Sub, Func<RadioBrowserClient, CancellationToken, Task<List<RadioBrowserClient.RadioStation>>> Fetch)[] RadioSections = new[]
    {
        ("us-top",      "📻 Top US Stations",      "Most-played in the US",      (Func<RadioBrowserClient, CancellationToken, Task<List<RadioBrowserClient.RadioStation>>>)((c, t) => c.SearchStationsAsync("countrycode=US&order=clickcount&reverse=true", 50, t))),
        // iHeart: the `iheartradio` tag is barely populated in radio-browser
        // (1 station). The reliable identifier is the stream hostname —
        // revma.ihrhls.com is iHeartMedia's CDN. Pull a wide US net and
        // filter client-side.
        ("iheart",      "📻 iHeartRadio",           "iHeartMedia network",        FetchIheartStationsAsync),
        ("hiphop-rnb",  "📻 Hip-Hop / R&B Radio",   "Popular hip-hop streams",    (c, t) => c.SearchStationsAsync("tag=hip%20hop&order=clickcount&reverse=true", 30, t)),
        ("pop",         "📻 Pop / Top 40 Radio",    "Pop hits stations",          (c, t) => c.SearchStationsAsync("tag=pop&order=clickcount&reverse=true", 30, t)),
        ("rock",        "📻 Rock Radio",            "Rock stations",              (c, t) => c.GetByTagAsync("rock", 30, t)),
        ("news-talk",   "📻 News & Talk",           "News & talk stations",       (c, t) => c.SearchStationsAsync("tag=news&order=clickcount&reverse=true", 30, t)),
    };

    /// <summary>
    /// iHeart-network filter: every iHeart-owned station in radio-browser
    /// has a stream URL on `revma.ihrhls.com` (their CDN) regardless of
    /// whether it's tagged iheartradio. We fetch the widest US pool we
    /// can and filter to those URLs. radio-browser's catalog isn't
    /// exhaustive — only ~1 iHeart station per ~13 top US stations is
    /// indexed there — so this is the upper bound for what's available.
    /// </summary>
    private static async Task<List<RadioBrowserClient.RadioStation>> FetchIheartStationsAsync(RadioBrowserClient c, CancellationToken t)
    {
        var pool = await c.SearchStationsAsync("countrycode=US&order=clickcount&reverse=true", 1000, t).ConfigureAwait(false);
        return pool
            .Where(s =>
            {
                var url = (s.UrlResolved ?? s.Url ?? string.Empty).ToLowerInvariant();
                return url.Contains("revma.ihrhls.com") || url.Contains("iheart");
            })
            .Take(80)
            .ToList();
    }

    private async Task RefreshRadioAsync(CancellationToken ct)
    {
        var blocklist = Plugin.Instance?.Configuration?.BlockedRadioStations
            ?? Array.Empty<string>();

        foreach (var (key, title, sub, fetch) in RadioSections)
        {
            try
            {
                var stations = await fetch(_radio, ct).ConfigureAwait(false);
                if (stations.Count == 0) { _log.LogInformation("[mdiscover-feed] radio '{Title}' = 0 stations", title); continue; }

                // Apply user blocklist BEFORE dedup so we don't accidentally
                // pick a blocked station as the canonical name-group winner.
                int preBlock = stations.Count;
                if (blocklist.Length > 0)
                {
                    stations = stations
                        .Where(s => !MatchesBlocklist(s.Name, blocklist))
                        .ToList();
                }

                // Collapse duplicates by normalized name (different bitrate/
                // codec entries for the same station). Keep the variant with
                // the highest clickcount so we pick the most-listened mirror.
                var deduped = stations
                    .GroupBy(s => NormalizeStationName(s.Name))
                    .Select(g => g.OrderByDescending(s => s.ClickCount ?? 0).First())
                    .OrderByDescending(s => s.ClickCount ?? 0)
                    .ToList();

                var pl = SynthesizePlaylist($"radio-{key}", title, sub, "radio");
                pl.TrackIds.Clear();
                int registered = 0;
                foreach (var s in deduped)
                {
                    var stubId = MaterializeRadioStation(s);
                    if (stubId.HasValue) { pl.TrackIds.Add(stubId.Value); registered++; }
                }
                pl.ImageUrl = deduped.FirstOrDefault(x => !string.IsNullOrEmpty(x.Favicon))?.Favicon;
                pl.FetchedAt = DateTime.UtcNow;
                _byId[pl.Id] = pl;
                _log.LogInformation("[mdiscover-feed] radio '{Title}' = {N} stations ({Pre} pre-dedup, {Blocked} blocked)",
                    title, registered, stations.Count, preBlock - stations.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[mdiscover-feed] radio section '{Title}' failed", title);
            }
        }
    }

    /// <summary>
    /// Case- and whitespace-insensitive substring match against the user's
    /// radio blocklist. Whitespace is stripped on both sides so a blocklist
    /// entry of "swr3" catches "SWR3", "SWR 3", and "SWR 3 (128 kbits mp3)"
    /// without needing separate entries for each spelling.
    /// </summary>
    private static bool MatchesBlocklist(string? name, string[] blocklist)
    {
        if (string.IsNullOrEmpty(name) || blocklist.Length == 0) return false;
        var lower = System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"\s+", "");
        foreach (var b in blocklist)
        {
            if (string.IsNullOrWhiteSpace(b)) continue;
            var bn = System.Text.RegularExpressions.Regex.Replace(b.Trim().ToLowerInvariant(), @"\s+", "");
            if (bn.Length > 0 && lower.Contains(bn)) return true;
        }
        return false;
    }

    /// <summary>
    /// Squash variant station names down to a canonical key for dedup.
    /// "SWR3", "SWR3 - 96K AAC", "SWR3 | 48k aac" all collapse to "swr3".
    /// "SWR3 Rock" stays distinct because the meaningful word (Rock) is
    /// what changes. We strip:
    ///   - whitespace and punctuation noise
    ///   - bitrate/codec qualifiers (numbers + k, kbps, mp3, aac, hls, etc.)
    ///   - leading/trailing pipe/dash separators
    /// </summary>
    private static string NormalizeStationName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var s = name.ToLowerInvariant();
        // Drop bitrate/codec qualifiers: "96k aac", "128 kbps", "mp3", "hls", "stream", etc.
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"\b\d+\s*k(bps)?\b|\b(aac|mp3|hls|ogg|opus|flac|stream|hd|hq|low|high|kbps|kbit)\b",
            " ");
        // Drop separators and collapse whitespace.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[|\-—–_·•/\\,\.]+", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s;
    }

    /// <summary>
    /// Convert a radio-browser.info station into a synthetic Audio stub.
    /// We use the station's UUID as the stub key (stable across refreshes
    /// even if the station's name changes). The upstream URL is stored in
    /// RadioStreamUrl so the audio resolver can short-circuit to it.
    /// </summary>
    private Guid? MaterializeRadioStation(RadioBrowserClient.RadioStation s)
    {
        if (string.IsNullOrEmpty(s.StationUuid) || string.IsNullOrEmpty(s.Name)) return null;
        // url_resolved follows redirects on the radio-browser side and
        // gives us the final upstream — preferred over `url`. Fall back
        // to `url` if missing (shouldn't happen for hidebroken=true).
        var stream = !string.IsNullOrEmpty(s.UrlResolved) ? s.UrlResolved : s.Url;
        if (string.IsNullOrEmpty(stream)) return null;

        var stubId = DiscoveryManager.StubGuid("radio-station", s.StationUuid);
        // Build a friendly artist line — country + first tag if we have
        // them — so the row in Jellyfin's track list isn't blank.
        var firstTag = (s.Tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var artistLabel = !string.IsNullOrEmpty(s.Country)
            ? (string.IsNullOrEmpty(firstTag) ? s.Country : $"{s.Country} • {firstTag}")
            : (firstTag ?? "Internet Radio");

        var entry = new DiscoveryItemCache.Entry
        {
            Kind = "track",
            Artist = artistLabel,
            AlbumName = "📻 Radio",
            TrackTitle = s.Name,
            PrimaryImageUrl = !string.IsNullOrEmpty(s.Favicon) ? s.Favicon : null,
            // Live streams have no fixed duration. Leaving null tells the
            // player "unknown length", which all clients handle as a live
            // stream with no scrub bar.
            DurationSeconds = null,
            RadioStreamUrl = stream,
            RadioStationUuid = s.StationUuid,
        };
        _itemCache.Put(stubId, entry);
        _registrar.RegisterStubTrack(stubId, entry);
        return stubId;
    }

    // -----------------------------------------------------------------
    // Image upgrade — for each YT-sourced track that's still showing a
    // YouTube video thumbnail, try to find a matching Deezer track and
    // swap in the album cover. Idempotent: once a track's PrimaryImageUrl
    // points at dzcdn.net, we leave it alone on subsequent passes.
    // -----------------------------------------------------------------
    private async Task UpgradeYtImagesFromDeezerAsync(CancellationToken ct)
    {
        // Snapshot first so concurrent puts during the loop don't trip us up.
        var snap = _itemCache.Snapshot();
        var pending = snap
            .Where(kv => IsYtTrackNeedingUpgrade(kv.Value))
            .ToList();

        if (pending.Count == 0)
        {
            _log.LogInformation("[mdiscover-feed] image upgrade: 0 candidates");
            return;
        }

        _log.LogInformation("[mdiscover-feed] image upgrade: {N} candidates", pending.Count);

        // Bounded concurrency so we don't spam Deezer. 3 in flight worked
        // out to ~15 RPS in practice, comfortably under their public-API
        // tolerance — we tried 6 and got rate-limited mid-pass.
        using var sem = new SemaphoreSlim(3);
        int upgraded = 0, missed = 0;
        var tasks = pending.Select(async kv =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await TryUpgradeOneAsync(kv.Key, kv.Value, ct).ConfigureAwait(false))
                    Interlocked.Increment(ref upgraded);
                else
                    Interlocked.Increment(ref missed);
            }
            finally { sem.Release(); }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        _log.LogInformation("[mdiscover-feed] image upgrade pass complete: {U} upgraded, {M} no-match",
            upgraded, missed);

        // Refresh playlist tile images: each YT playlist's ImageUrl was
        // pinned to the first track's YT video thumbnail at fetch time. Now
        // that the underlying tracks have proper Deezer covers, point the
        // playlist at the first track that ended up with a Deezer cover so
        // the playlist tile stops showing a video screenshot.
        foreach (var pl in _byId.Values.Where(p => p.Source == "ytmusic"))
        {
            foreach (var trackId in pl.TrackIds)
            {
                if (_itemCache.TryGet(trackId, out var te)
                    && te?.PrimaryImageUrl is { Length: > 0 } url
                    && (url.Contains("dzcdn.net", StringComparison.OrdinalIgnoreCase)
                        || url.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase)))
                {
                    pl.ImageUrl = url;
                    break;
                }
            }
        }
    }

    private static bool IsYtTrackNeedingUpgrade(DiscoveryItemCache.Entry e)
    {
        if (e.Kind != "track") return false;
        if (string.IsNullOrEmpty(e.Artist) || string.IsNullOrEmpty(e.TrackTitle)) return false;
        if (e.DeezerTrackId.HasValue || e.DeezerAlbumId.HasValue) return false;
        // Radio stations aren't songs — their "title" is the station name
        // and PrimaryImageUrl is the station favicon. Don't try to find
        // an album cover for them on Deezer/iTunes.
        if (!string.IsNullOrEmpty(e.RadioStreamUrl)) return false;
        var url = e.PrimaryImageUrl ?? string.Empty;
        // Already upgraded if pointing at Deezer's or Apple's CDN. Anything
        // else (YT thumbs at lh3.googleusercontent.com, i.ytimg.com, or
        // null) is still a candidate.
        if (url.Contains("dzcdn.net", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.Contains("mzstatic.com", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private async Task<bool> TryUpgradeOneAsync(Guid stubId, DiscoveryItemCache.Entry e, CancellationToken ct)
    {
        try
        {
            var cleanTitle = NormalizeTitleForSearch(e.TrackTitle!);
            var primaryArtist = NormalizeArtistForSearch(e.Artist!);

            // First try: Deezer with clean artist + title.
            var hits = await _deezer.SearchTracksAsync($"{primaryArtist} {cleanTitle}", 8, ct).ConfigureAwait(false);
            int n1 = hits?.Data?.Count ?? 0;
            var best = PickBestMatch(hits?.Data, primaryArtist, cleanTitle);

            // Second try: Deezer title only — common when the YT artist
            // string is a YT-only alias or carries multiple artists in a way
            // that throws off Deezer's relevance ranking.
            int n2 = 0;
            if (best?.Album is null)
            {
                hits = await _deezer.SearchTracksAsync(cleanTitle, 8, ct).ConfigureAwait(false);
                n2 = hits?.Data?.Count ?? 0;
                best = PickBestMatch(hits?.Data, primaryArtist, cleanTitle);
            }

            // Deezer hit? Take it.
            if (best?.Album?.CoverXl is not null || best?.Album?.CoverBig is not null)
            {
                var updated = new DiscoveryItemCache.Entry
                {
                    Kind = e.Kind,
                    Artist = e.Artist,
                    AlbumName = string.IsNullOrEmpty(e.AlbumName) ? best.Album?.Title : e.AlbumName,
                    TrackTitle = e.TrackTitle,
                    DeezerArtistId = e.DeezerArtistId ?? best.Artist?.Id,
                    DeezerAlbumId = best.Album?.Id,
                    DeezerTrackId = e.DeezerTrackId,
                    PrimaryImageUrl = best.Album?.CoverXl ?? best.Album?.CoverBig,
                    DurationSeconds = e.DurationSeconds,
                    AddedUtc = e.AddedUtc,
                };
                _itemCache.Put(stubId, updated);
                _registrar.RegisterStubTrack(stubId, updated);
                return true;
            }

            // Deezer missed — fall back to Apple. Apple's catalog covers
            // most contemporary releases that Deezer lags on (especially
            // hip-hop / new releases), and the artwork URLs upscale cleanly
            // to 1000x1000.
            var ituneHit = await TryFindItunesAsync(primaryArtist, cleanTitle, ct).ConfigureAwait(false);
            if (ituneHit is not null)
            {
                var artworkUrl = ItunesClient.UpscaleArtworkUrl(ituneHit.ArtworkUrl100, 1000);
                if (!string.IsNullOrEmpty(artworkUrl))
                {
                    // We don't have a Deezer album id, but we do mark the
                    // entry as "upgraded" by setting PrimaryImageUrl to the
                    // Apple CDN — IsYtTrackNeedingUpgrade now also accepts
                    // mzstatic.com as an upgraded marker.
                    var updated = new DiscoveryItemCache.Entry
                    {
                        Kind = e.Kind,
                        Artist = e.Artist,
                        AlbumName = string.IsNullOrEmpty(e.AlbumName) ? ituneHit.CollectionName : e.AlbumName,
                        TrackTitle = e.TrackTitle,
                        DeezerArtistId = e.DeezerArtistId,
                        DeezerAlbumId = e.DeezerAlbumId,
                        DeezerTrackId = e.DeezerTrackId,
                        PrimaryImageUrl = artworkUrl,
                        DurationSeconds = e.DurationSeconds,
                        AddedUtc = e.AddedUtc,
                    };
                    _itemCache.Put(stubId, updated);
                    _registrar.RegisterStubTrack(stubId, updated);
                    return true;
                }
            }

            // Both sources missed — log a sample so we can see why.
            if (System.Random.Shared.Next(20) == 0)
            {
                _log.LogInformation("[mdiscover-feed] miss: '{A}' / '{T}' (deezer q1={N1} q2={N2}, itunes=miss)",
                    primaryArtist, cleanTitle, n1, n2);
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[mdiscover-feed] image upgrade failed for {Stub}", stubId);
            return false;
        }
    }

    /// <summary>
    /// Run two iTunes searches in sequence (artist+title, then title-only)
    /// and pick the best match using the same artist/title scoring logic
    /// we apply to Deezer hits. Returns null if no confident match.
    /// </summary>
    private async Task<ItunesClient.ITunesSong?> TryFindItunesAsync(string artist, string title, CancellationToken ct)
    {
        var resp = await _itunes.SearchSongsAsync($"{artist} {title}", 8, ct).ConfigureAwait(false);
        var pick = PickBestItunesMatch(resp?.Results, artist, title);
        if (pick is not null) return pick;

        resp = await _itunes.SearchSongsAsync(title, 8, ct).ConfigureAwait(false);
        return PickBestItunesMatch(resp?.Results, artist, title);
    }

    private static ItunesClient.ITunesSong? PickBestItunesMatch(List<ItunesClient.ITunesSong>? hits, string artist, string title)
    {
        if (hits is null || hits.Count == 0) return null;
        var aLower = NormalizeArtistForSearch(artist).ToLowerInvariant();
        var tLower = NormalizeTitleForSearch(title).ToLowerInvariant();

        ItunesClient.ITunesSong? winner = null;
        int winnerScore = int.MinValue;
        foreach (var h in hits)
        {
            if (string.IsNullOrEmpty(h.ArtworkUrl100)) continue;
            var ha = NormalizeArtistForSearch(h.ArtistName ?? string.Empty).ToLowerInvariant();
            var ht = NormalizeTitleForSearch(h.TrackName ?? string.Empty).ToLowerInvariant();
            int score = 0;
            if (!string.IsNullOrEmpty(ha))
            {
                if (ha == aLower) score += 50;
                else if (ha.Contains(aLower) || aLower.Contains(ha)) score += 20;
                else score -= 15;
            }
            if (!string.IsNullOrEmpty(ht))
            {
                if (ht == tLower) score += 30;
                else if (ht.Contains(tLower) || tLower.Contains(ht)) score += 15;
                else
                {
                    var overlap = WordOverlap(ht, tLower);
                    if (overlap >= 2) score += 10;
                    else score -= 10;
                }
            }
            if (score > winnerScore) { winnerScore = score; winner = h; }
        }
        return winnerScore < 0 ? null : winner;
    }

    /// <summary>
    /// Score Deezer hits against the YT artist+title and return the best.
    /// Mirrors the scoring approach used elsewhere in the codebase: exact
    /// artist match wins big, substring match also helps; we lean on title
    /// containment to avoid picking a remix or cover with a similar title.
    /// </summary>
    private static DeezerClient.DzTrack? PickBestMatch(List<DeezerClient.DzTrack>? hits, string artist, string title)
    {
        if (hits is null || hits.Count == 0) return null;
        var aLower = NormalizeArtistForSearch(artist).ToLowerInvariant();
        var tLower = NormalizeTitleForSearch(title).ToLowerInvariant();

        DeezerClient.DzTrack? winner = null;
        int winnerScore = int.MinValue;
        foreach (var h in hits)
        {
            var ha = NormalizeArtistForSearch(h.Artist?.Name ?? string.Empty).ToLowerInvariant();
            var ht = NormalizeTitleForSearch(h.Title ?? string.Empty).ToLowerInvariant();
            int score = 0;
            if (!string.IsNullOrEmpty(ha))
            {
                if (ha == aLower) score += 50;
                else if (ha.Contains(aLower) || aLower.Contains(ha)) score += 20;
                else score -= 15;
            }
            if (!string.IsNullOrEmpty(ht))
            {
                if (ht == tLower) score += 30;
                else if (ht.Contains(tLower) || tLower.Contains(ht)) score += 15;
                // Word-overlap check: cheap last-ditch effort to find a
                // partial title match (e.g. "Hey Soul Sister" vs "Hey, Soul
                // Sister"). Counts shared significant tokens (≥3 chars).
                else
                {
                    var overlap = WordOverlap(ht, tLower);
                    if (overlap >= 2) score += 10;
                    else score -= 10;
                }
            }
            if (h.Album?.CoverXl is null && h.Album?.CoverBig is null) score -= 100;
            if (score > winnerScore)
            {
                winnerScore = score;
                winner = h;
            }
        }
        // Don't accept clearly-bad matches (no useful image, or score < 0).
        return winnerScore < 0 ? null : winner;
    }

    /// <summary>
    /// Strip the noise YT Music titles tend to carry — "(Official Video)",
    /// "(Lyric Video)", "(feat. X)" — so search queries match Deezer's
    /// cleaner titles. Conservative: only removes well-known suffixes and
    /// parenthesised qualifiers, never edits the song name itself.
    /// </summary>
    public static string NormalizeTitleForSearch(string title)
    {
        if (string.IsNullOrEmpty(title)) return title;
        var s = title;
        // Drop trailing parenthesised/bracketed annotations whose body
        // contains common YT noise words. We don't strip ALL parentheticals
        // because some are meaningful ("(Acoustic)", "(Live)" — though
        // arguably we still want to find the studio version; left for
        // tuning later).
        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"[\(\[][^()\[\]]*?(official|video|audio|lyric|music\s*video|visualizer|hd|4k|mv)[^()\[\]]*?[\)\]]",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Drop "(feat. ...)" and "[feat. ...]" — Deezer usually carries the
        // feature credit on the artist string instead.
        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"[\(\[]\s*(feat\.?|ft\.?|featuring)[^()\[\]]*[\)\]]",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Collapse extra whitespace, strip trailing dashes/punctuation.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
        s = s.TrimEnd('-', '–', '—', ' ').Trim();
        return s.Length > 0 ? s : title;
    }

    /// <summary>
    /// YT Music joins multiple artists with ", ". Deezer's relevance is
    /// best when the query carries the lead artist only.
    /// </summary>
    public static string NormalizeArtistForSearch(string artist)
    {
        if (string.IsNullOrEmpty(artist)) return artist;
        var idx = artist.IndexOf(',');
        if (idx > 0) return artist[..idx].Trim();
        return artist.Trim();
    }

    /// <summary>
    /// Album-name normalizer. Strips qualifiers Lidarr's text-search doesn't
    /// like ("(Deluxe)", "(Remastered 2009)", "(Bonus Edition)") so the
    /// title we hand off to Lidarr matches the canonical album entry on its
    /// metadata source. Conservative: only removes parenthesised/bracketed
    /// suffixes whose body matches a known noise keyword.
    /// </summary>
    public static string NormalizeAlbumForSearch(string album)
    {
        if (string.IsNullOrEmpty(album)) return album;
        var s = System.Text.RegularExpressions.Regex.Replace(
            album,
            @"[\(\[][^()\[\]]*?(deluxe|remaster(ed)?|bonus|edition|expanded|anniversary|special|reissue|version|explicit|clean)[^()\[\]]*?[\)\]]",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
        s = s.TrimEnd('-', '–', '—', ' ').Trim();
        return s.Length > 0 ? s : album;
    }

    /// <summary>Count of ≥3-char tokens that appear in both strings.</summary>
    private static int WordOverlap(string a, string b)
    {
        var aw = a.Split(new[] { ' ', ',', '.', '\'', '"', '(', ')', '[', ']', '!', '?', ':', ';', '-', '–', '—' },
            StringSplitOptions.RemoveEmptyEntries);
        var bs = new HashSet<string>(b.Split(new[] { ' ', ',', '.', '\'', '"', '(', ')', '[', ']', '!', '?', ':', ';', '-', '–', '—' },
            StringSplitOptions.RemoveEmptyEntries));
        int n = 0;
        foreach (var w in aw)
        {
            if (w.Length >= 3 && bs.Contains(w)) n++;
        }
        return n;
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------
    private PlaylistDto SynthesizePlaylist(string sourceKey, string name, string? subLabel, string source)
    {
        var pid = DiscoveryManager.StubGuid("playlist", sourceKey);
        if (!_byId.TryGetValue(pid, out var pl))
        {
            pl = new PlaylistDto { Id = pid };
        }
        pl.Name = name;
        pl.SubLabel = subLabel;
        pl.Source = source;
        return pl;
    }

    private static string Slug(string s) => new string(
        s.ToLowerInvariant()
         .Where(c => char.IsLetterOrDigit(c) || c == '-')
         .ToArray());

    // -----------------------------------------------------------------
    // Disk persistence
    // -----------------------------------------------------------------
    private void Load()
    {
        if (_persistFile is null || !File.Exists(_persistFile)) return;
        try
        {
            var json = File.ReadAllText(_persistFile);
            if (string.IsNullOrWhiteSpace(json)) return;
            var loaded = JsonSerializer.Deserialize<List<PlaylistDto>>(json, JsonOpts);
            if (loaded is null) return;
            foreach (var p in loaded) _byId[p.Id] = p;
            _log.LogInformation("[mdiscover-feed] loaded {N} discovery playlists from disk", loaded.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-feed] load failed");
        }
    }

    private void Save()
    {
        if (_persistFile is null) return;
        try
        {
            var snapshot = _byId.Values.ToList();
            var tmp = _persistFile + ".tmp";
            using (var fs = File.Create(tmp))
            {
                JsonSerializer.Serialize(fs, snapshot, JsonOpts);
            }
            File.Move(tmp, _persistFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-feed] save failed");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    // YT Music discover-server response shapes.
    private class YtMusicDiscoverResponse
    {
        [JsonPropertyName("sections")]
        public List<YtMusicSection> Sections { get; set; } = new();
    }
    private class YtMusicSection
    {
        [JsonPropertyName("section")] public string Section { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("subLabel")] public string? SubLabel { get; set; }
        [JsonPropertyName("tracks")] public List<YtMusicDiscoverTrack> Tracks { get; set; } = new();
    }
    private class YtMusicDiscoverTrack
    {
        [JsonPropertyName("videoId")] public string VideoId { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("artist")] public string? Artist { get; set; }
        [JsonPropertyName("album")] public string? Album { get; set; }
        [JsonPropertyName("durationSeconds")] public int? DurationSeconds { get; set; }
        [JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }
    }
}
