using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using JellyMusicDiscovery.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Filters;

/// <summary>
/// Pattern: "intercept the response, append our items".
///
/// We don't try to inject items into Jellyfin's library DB anymore — that fight
/// is too deep (TopParentId / library cache / scanner-only visibility / etc.).
/// Instead we let Jellyfin run its normal search, get the response object, and
/// append fake search hits sourced from Deezer before the response is serialized.
/// Works for both /Items and /Search/Hints. Any client speaking the standard
/// Jellyfin API gets our results for free.
/// </summary>
public class MusicSearchActionFilter : IAsyncResultFilter
{
    private static readonly HashSet<string> MusicTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Audio", "MusicAlbum", "MusicArtist", "AudioBook",
    };

    private readonly DeezerClient _deezer;
    private readonly ItunesClient _itunes;
    private readonly DiscoveryItemCache _cache;
    private readonly LibraryRegistrar _registrar;
    private readonly RecentlyPlayedTracker _recents;
    private readonly DiscoveryPlaylistFeed _discoveryFeed;
    private readonly System.Net.Http.IHttpClientFactory _httpFactory;
    private readonly MediaBrowser.Controller.Net.IAuthorizationContext _authContext;
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
    private readonly ILogger<MusicSearchActionFilter> _log;

    public MusicSearchActionFilter(
        DeezerClient deezer,
        ItunesClient itunes,
        DiscoveryItemCache cache,
        LibraryRegistrar registrar,
        RecentlyPlayedTracker recents,
        DiscoveryPlaylistFeed discoveryFeed,
        System.Net.Http.IHttpClientFactory httpFactory,
        MediaBrowser.Controller.Net.IAuthorizationContext authContext,
        MediaBrowser.Controller.Library.ILibraryManager libraryManager,
        ILogger<MusicSearchActionFilter> log)
    {
        _deezer = deezer;
        _itunes = itunes;
        _cache = cache;
        _registrar = registrar;
        _recents = recents;
        _discoveryFeed = discoveryFeed;
        _httpFactory = httpFactory;
        _authContext = authContext;
        _libraryManager = libraryManager;
        _log = log;
    }

    private string? TryResolveLibraryArtistName(List<Guid> ids)
    {
        foreach (var g in ids)
        {
            try
            {
                var item = _libraryManager.GetItemById(g);
                if (item is not null && !string.IsNullOrWhiteSpace(item.Name)) return item.Name;
            }
            catch { /* swallow — best-effort lookup */ }
        }
        return null;
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext ctx, ResultExecutionDelegate next)
    {
        try
        {
            var should = ShouldInject(ctx, out var term);
            _log.LogInformation("[mdiscover-search] result fire: {Path}{Query} should={Should} term={Term}", ctx.HttpContext.Request.Path, ctx.HttpContext.Request.QueryString, should, term);
            if (should)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.HttpContext.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await TryAugmentAsync(ctx, term!, cts.Token).ConfigureAwait(false);
                _log.LogInformation("[mdiscover-search] cache size after augment: {Size}", _cache.Count);
            }
            else if (ShouldAugmentArtistAlbums(ctx))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.HttpContext.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await TryAugmentArtistAlbumsAsync(ctx, cts.Token).ConfigureAwait(false);
            }
            else if (ShouldAugmentArtistVideos(ctx))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.HttpContext.RequestAborted);
                // YT-Music search calls can be slow on cold cache. Give it a
                // bit more headroom than the album augmenter.
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                await TryAugmentArtistVideosAsync(ctx, cts.Token).ConfigureAwait(false);
            }
            else if (ShouldAugmentRecentlyPlayed(ctx))
            {
                TryAugmentRecentlyPlayed(ctx);
            }
            else if (ShouldAugmentPlaylists(ctx))
            {
                TryAugmentPlaylists(ctx);
            }
        }
        catch (OperationCanceledException) { /* fall through */ }
        catch (Exception ex) { _log.LogWarning(ex, "Search response augmentation failed."); }

        await next().ConfigureAwait(false);
    }

    // -----------------------------------------------------------------
    // Heuristics — when to fire.
    // -----------------------------------------------------------------
    private bool ShouldInject(ResultExecutingContext ctx, out string? term)
    {
        term = null;
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnableSearchInjection) return false;

        var controller = ctx.RouteData.Values.TryGetValue("controller", out var c) ? c?.ToString() : null;
        // Accept three controllers:
        //   Items    — /Items?searchTerm=... (web client)
        //   Search   — /Search/Hints?searchTerm=...
        //   Artists  — /Artists/AlbumArtists?searchTerm=... (Finer/iOS uses this for
        //              the artist autocomplete card when you type in search)
        if (!string.Equals(controller, "Items", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(controller, "Search", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(controller, "Artists", StringComparison.OrdinalIgnoreCase))
            return false;

        term = ExtractSearchTerm(ctx.HttpContext);
        if (string.IsNullOrWhiteSpace(term) || term.Length < 2) return false;

        // For the Items/Search controllers we honor IncludeItemTypes (only fire
        // on music types). The Artists controller doesn't take that param —
        // it's intrinsically about artists, so we always fire there.
        if (string.Equals(controller, "Artists", StringComparison.OrdinalIgnoreCase))
            return true;

        var types = ExtractIncludeItemTypes(ctx.HttpContext);
        if (types.Count > 0 && !types.Any(t => MusicTypes.Contains(t))) return false;

        return true;
    }

    /// <summary>
    /// Detect: an artist's album page (`/Items?albumArtistIds=X&IncludeItemTypes=MusicAlbum`).
    /// Fires only if the response is non-empty (so we know the artist's name)
    /// and the artist guid is NOT one of our stubs (those are handled by
    /// MusicItemDetailActionFilter via direct Deezer lookup).
    /// </summary>
    /// <summary>
    /// Detect "Recently Played" home-screen queries. Both Finer and web
    /// fire this as a sort-by-DatePlayed request, with no explicit
    /// `Filters=IsPlayed` (the sort itself implicitly drops items with no
    /// played-date). Real Finer query observed:
    ///   /Users/{u}/Items?Limit=30&IncludeItemTypes=Audio&ParentId={lib}
    ///                  &Recursive=true&SortBy=DatePlayed,SortName
    ///                  &SortOrder=Descending&...
    /// We trigger on SortBy containing DatePlayed + a music type filter.
    /// </summary>
    private bool ShouldAugmentRecentlyPlayed(ResultExecutingContext ctx)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnableSearchInjection) return false;

        var http = ctx.HttpContext;

        var sortBy = (http.Request.Query.TryGetValue("sortBy", out var sb) ? sb.ToString()
            : http.Request.Query.TryGetValue("SortBy", out var sb2) ? sb2.ToString() : string.Empty);
        if (sortBy.IndexOf("DatePlayed", StringComparison.OrdinalIgnoreCase) < 0) return false;

        // Also require a music type filter — we don't want to fire on
        // recently-played video / book queries.
        var types = ExtractIncludeItemTypes(http);
        if (types.Count == 0 || !types.Any(t => MusicTypes.Contains(t))) return false;

        // Skip if the query has a stub ParentId (e.g. an album-page that's
        // listing its own tracks sorted by play count). Recently-played is
        // always at the library-root level.
        if (TryGetGuidListFromQuery(http, "parentId", out var parentIds))
        {
            foreach (var pid in parentIds)
                if (_cache.TryGet(pid, out _)) return false;
        }

        return ctx.Result is ObjectResult { Value: QueryResult<BaseItemDto> };
    }

    private void TryAugmentRecentlyPlayed(ResultExecutingContext ctx)
    {
        if (ctx.Result is not ObjectResult or || or.Value is not QueryResult<BaseItemDto> qr) return;

        // Resolve user from auth header.
        Guid userId;
        try
        {
            var auth = _authContext.GetAuthorizationInfo(ctx.HttpContext.Request).GetAwaiter().GetResult();
            if (auth?.UserId is null || auth.UserId == Guid.Empty)
            {
                _log.LogInformation("[mdiscover-search] recently-played: no user on auth, skipping");
                return;
            }
            userId = auth.UserId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-search] recently-played: auth lookup failed");
            return;
        }
        _log.LogInformation("[mdiscover-search] recently-played: matched query, user={User} path={Path}{Query}",
            userId, ctx.HttpContext.Request.Path, ctx.HttpContext.Request.QueryString);

        var includeTypes = ExtractIncludeItemTypes(ctx.HttpContext);
        bool wantAudio = includeTypes.Count == 0 || includeTypes.Contains("Audio");
        bool wantAlbum = includeTypes.Count == 0 || includeTypes.Contains("MusicAlbum");
        if (!wantAudio && !wantAlbum) return;

        var limit = 30;
        if (ctx.HttpContext.Request.Query.TryGetValue("Limit", out var lv) && int.TryParse(lv.ToString(), out var lp))
            limit = Math.Min(Math.Max(lp, 1), 50);

        var recent = _recents.GetRecent(userId, limit);
        if (recent.Count == 0)
        {
            _log.LogInformation("[mdiscover-search] recently-played: tracker empty for user {User}, nothing to inject", userId);
            return;
        }
        _log.LogInformation("[mdiscover-search] recently-played: {N} stubs in tracker, wantAudio={A} wantAlbum={Al}", recent.Count, wantAudio, wantAlbum);

        // Dedupe vs items already in the response (in case Jellyfin found
        // overlapping records — unlikely for stubs but safe).
        var existingIds = qr.Items.Select(i => i.Id).ToHashSet();

        var addItems = new List<BaseItemDto>();
        foreach (var p in recent)
        {
            // Pick what to surface based on the requested item type.
            if (wantAlbum && p.AlbumId.HasValue && !existingIds.Contains(p.AlbumId.Value))
            {
                if (_cache.TryGet(p.AlbumId.Value, out var alEntry) && alEntry is { Kind: "album" })
                {
                    var dto = BuildAlbumDtoFromCache(p.AlbumId.Value, alEntry);
                    addItems.Add(dto);
                    existingIds.Add(p.AlbumId.Value);
                    if (addItems.Count >= limit) break;
                    continue;
                }
            }
            if (wantAudio && !existingIds.Contains(p.TrackId))
            {
                if (_cache.TryGet(p.TrackId, out var trEntry) && trEntry is { Kind: "track" })
                {
                    var dto = BuildTrackDtoFromCache(p.TrackId, trEntry);
                    addItems.Add(dto);
                    existingIds.Add(p.TrackId);
                    if (addItems.Count >= limit) break;
                }
            }
        }

        if (addItems.Count == 0) return;

        // Prepend our stubs so the most recent plays bubble to the top of
        // the row (Jellyfin sorted by DatePlayed desc; we just played these
        // so they're newer than anything in qr.Items).
        var combined = addItems.Concat(qr.Items).ToArray();
        qr.Items = combined;
        qr.TotalRecordCount = combined.Length;
        _log.LogInformation("[mdiscover-search] recently-played augment: added {N} stubs for user {User}", addItems.Count, userId);
    }

    /// <summary>
    /// Build a BaseItemDto from a cached album stub (used to surface
    /// recently-played stubs in home-screen queries).
    /// </summary>
    private BaseItemDto BuildAlbumDtoFromCache(Guid id, DiscoveryItemCache.Entry e)
    {
        NameGuidPair[]? artistPair = null;
        if (e.DeezerArtistId is { } dzArId && !string.IsNullOrEmpty(e.Artist))
        {
            var artistGuid = Services.DiscoveryManager.StubGuid("dz-artist", dzArId.ToString());
            artistPair = new[] { new NameGuidPair { Name = e.Artist!, Id = artistGuid } };
        }
        return new BaseItemDto
        {
            Id = id,
            ServerId = Plugin.ServerSystemId,
            Name = e.AlbumName ?? "(unknown album)",
            Type = BaseItemKind.MusicAlbum,
            MediaType = MediaType.Unknown,
            Tags = new[] { Tags.Stub },
            ImageTags = new() { { ImageType.Primary, "deezer-" + id.ToString("N") } },
            ImageBlurHashes = new() { { ImageType.Primary, new Dictionary<string, string>() } },
            PrimaryImageAspectRatio = 1.0,
            Artists = e.Artist is { Length: > 0 } an ? new[] { an } : null,
            AlbumArtist = e.Artist,
            AlbumArtists = artistPair ?? Array.Empty<NameGuidPair>(),
            ArtistItems = artistPair ?? Array.Empty<NameGuidPair>(),
            IsFolder = true,
        };
    }

    private BaseItemDto BuildTrackDtoFromCache(Guid id, DiscoveryItemCache.Entry e)
    {
        NameGuidPair[]? artistPair = null;
        if (e.DeezerArtistId is { } dzArId && !string.IsNullOrEmpty(e.Artist))
        {
            var artistGuid = Services.DiscoveryManager.StubGuid("dz-artist", dzArId.ToString());
            artistPair = new[] { new NameGuidPair { Name = e.Artist!, Id = artistGuid } };
        }
        Guid? albumId = e.DeezerAlbumId is { } dzAlId
            ? Services.DiscoveryManager.StubGuid("dz-album", dzAlId.ToString())
            : (Guid?)null;
        return new BaseItemDto
        {
            Id = id,
            ServerId = Plugin.ServerSystemId,
            Name = e.TrackTitle ?? "(unknown track)",
            Type = BaseItemKind.Audio,
            MediaType = MediaType.Audio,
            Tags = new[] { Tags.Stub },
            ImageTags = new() { { ImageType.Primary, "deezer-" + id.ToString("N") } },
            ImageBlurHashes = new() { { ImageType.Primary, new Dictionary<string, string>() } },
            PrimaryImageAspectRatio = 1.0,
            AlbumPrimaryImageTag = albumId is { } a ? "deezer-" + a.ToString("N") : null,
            AlbumId = albumId,
            ParentId = albumId,
            Artists = e.Artist is { Length: > 0 } an ? new[] { an } : null,
            ArtistItems = artistPair ?? Array.Empty<NameGuidPair>(),
            AlbumArtists = artistPair ?? Array.Empty<NameGuidPair>(),
            Album = e.AlbumName,
            AlbumArtist = e.Artist,
            RunTimeTicks = e.DurationSeconds.HasValue ? (long?)TimeSpan.FromSeconds(e.DurationSeconds.Value).Ticks : null,
            IsFolder = false,
            CanDownload = false,
            LocationType = MediaBrowser.Model.Entities.LocationType.Remote,
        };
    }

    /// <summary>
    /// Detect playlist-list queries. Two patterns to handle:
    ///   1. /Items?IncludeItemTypes=Playlist             (older clients)
    ///   2. /Users/.../Items?ParentId={playlists-view}   (Jellyfin web)
    ///
    /// Web doesn't pass IncludeItemTypes — it uses the user's "Playlists"
    /// view's virtual-folder GUID as ParentId. Rather than hard-code that
    /// guid, we detect the playlist list by inspecting the response: if
    /// the result contains any items of Type=Playlist, we know this is
    /// the playlists view.
    /// </summary>
    private bool ShouldAugmentPlaylists(ResultExecutingContext ctx)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnableSearchInjection) return false;
        if (ctx.Result is not ObjectResult { Value: QueryResult<BaseItemDto> qr }) return false;

        // Pattern 1: explicit IncludeItemTypes=Playlist filter.
        var includeTypes = ExtractIncludeItemTypes(ctx.HttpContext);
        if (includeTypes.Contains("Playlist")) return true;

        // Pattern 2: response itself contains Playlist items — the playlists
        // virtual-folder view. We require at least one playlist already in
        // the result so we don't accidentally inject into unrelated /Items
        // queries that happen to return mixed content.
        if (qr.Items.Any(i => i.Type == BaseItemKind.Playlist))
        {
            // Don't inject if the query has a stub ParentId (we don't want
            // to mix discovery playlists into a stub-album track listing).
            if (TryGetGuidListFromQuery(ctx.HttpContext, "parentId", out var pids))
            {
                foreach (var p in pids)
                    if (_cache.TryGet(p, out _)) return false;
            }
            return true;
        }

        return false;
    }

    private void TryAugmentPlaylists(ResultExecutingContext ctx)
    {
        if (ctx.Result is not ObjectResult or || or.Value is not QueryResult<BaseItemDto> qr) return;

        var feed = _discoveryFeed.All();
        if (feed.Count == 0) return;

        var existingIds = qr.Items.Select(i => i.Id).ToHashSet();
        var addItems = new List<BaseItemDto>();
        foreach (var p in feed)
        {
            if (existingIds.Contains(p.Id)) continue;
            // Build directly here to keep MusicSearchActionFilter independent of
            // MusicItemDetailActionFilter's helpers.
            var imageTags = !string.IsNullOrEmpty(p.ImageUrl)
                ? new Dictionary<ImageType, string> { { ImageType.Primary, "discover-" + p.Id.ToString("N") } }
                : new Dictionary<ImageType, string>();

            addItems.Add(new BaseItemDto
            {
                Id = p.Id,
                ServerId = Plugin.ServerSystemId,
                Name = p.Name,
                Type = BaseItemKind.Playlist,
                MediaType = MediaType.Audio,
                Tags = new[] { Tags.Stub, "Discovery" },
                ImageTags = imageTags,
                ImageBlurHashes = new() { { ImageType.Primary, new Dictionary<string, string>() } },
                PrimaryImageAspectRatio = 1.0,
                ChildCount = p.TrackIds.Count,
                IsFolder = true,
                CanDelete = false,
                CanDownload = false,
                Overview = p.SubLabel,
                DateCreated = p.FetchedAt,
                Genres = Array.Empty<string>(),
                GenreItems = Array.Empty<NameGuidPair>(),
                Studios = Array.Empty<NameGuidPair>(),
                Artists = Array.Empty<string>(),
                ArtistItems = Array.Empty<NameGuidPair>(),
                AlbumArtists = Array.Empty<NameGuidPair>(),
                BackdropImageTags = Array.Empty<string>(),
                LockedFields = Array.Empty<MediaBrowser.Model.Entities.MetadataField>(),
                ProviderIds = new Dictionary<string, string>(),
            });
            existingIds.Add(p.Id);
        }

        if (addItems.Count == 0) return;

        var combined = qr.Items.Concat(addItems).ToArray();
        qr.Items = combined;
        qr.TotalRecordCount = combined.Length;
        _log.LogInformation("[mdiscover-search] playlist augment: appended {N} discovery playlists", addItems.Count);
    }

    private bool ShouldAugmentArtistAlbums(ResultExecutingContext ctx)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnableSearchInjection) return false;

        var http = ctx.HttpContext;
        // Need an artist filter on the query.
        if (!TryGetGuidListFromQuery(http, "albumArtistIds", out var ids)
            && !TryGetGuidListFromQuery(http, "artistIds", out ids))
            return false;
        if (ids.Count == 0) return false;

        // If the artist itself is one of our stubs, the action filter already
        // synthesized the album list — don't double up.
        foreach (var g in ids)
            if (_cache.TryGet(g, out var e) && e is { Kind: "artist" })
                return false;

        // The query must be asking for albums.
        var includeTypes = ExtractIncludeItemTypes(http);
        if (includeTypes.Count > 0 && !includeTypes.Contains("MusicAlbum")) return false;

        return ctx.Result is ObjectResult { Value: QueryResult<BaseItemDto> };
    }

    private async Task TryAugmentArtistAlbumsAsync(ResultExecutingContext ctx, CancellationToken ct)
    {
        if (ctx.Result is not ObjectResult or || or.Value is not QueryResult<BaseItemDto> qr) return;

        // Pull artist name from any existing real album in the response. If the
        // library has at least one album by this artist, it'll have AlbumArtist
        // set — otherwise we'd need a live Items lookup against the artist guid
        // which is fragile. For empty libraries we just skip the augmentation;
        // the user can find the artist via the search-result stub artist instead.
        string? artistName = null;
        foreach (var item in qr.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.AlbumArtist)) { artistName = item.AlbumArtist; break; }
            var first = item.Artists?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)) { artistName = first; break; }
        }
        if (string.IsNullOrWhiteSpace(artistName))
        {
            _log.LogInformation("[mdiscover-search] artist-album augment skipped: no artist name in response");
            return;
        }

        // Find Deezer's artist record: top search hit by name. We're already
        // relying on Deezer's search ranking elsewhere — same trade-off here.
        var artistSearch = await _deezer.SearchArtistsAsync(artistName, 5, ct).ConfigureAwait(false);
        var dzArtist = artistSearch.Data?.FirstOrDefault(a =>
            string.Equals(a.Name, artistName, StringComparison.OrdinalIgnoreCase));
        // Fallback to the top-ranked match if no exact name hit (handles
        // diacritics / capitalization differences).
        dzArtist ??= artistSearch.Data?.FirstOrDefault();
        if (dzArtist is null || dzArtist.Id == 0)
        {
            _log.LogInformation("[mdiscover-search] artist-album augment: no Deezer match for {Artist}", artistName);
            return;
        }

        var albumsResp = await _deezer.GetArtistAlbumsAsync(dzArtist.Id, 100, ct).ConfigureAwait(false);
        var dzAlbums = albumsResp?.Data ?? new();
        if (dzAlbums.Count == 0) return;

        // Dedupe vs library albums already in the response. Lower-cased,
        // whitespace-normalized title compare is good enough for the typical
        // "I already have Enema of the State" case.
        var existingTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in qr.Items)
        {
            var n = NormalizeTitle(item.Name);
            if (!string.IsNullOrEmpty(n)) existingTitles.Add(n);
        }

        var addItems = new List<BaseItemDto>();
        foreach (var al in dzAlbums.Where(IsQualityAlbum))
        {
            var norm = NormalizeTitle(al.Title);
            if (string.IsNullOrEmpty(norm) || existingTitles.Contains(norm)) continue;

            // Stamp parent artist — Deezer's /artist/{id}/albums omits it.
            if (al.Artist is null) al.Artist = new DeezerClient.DzArtist { Id = dzArtist.Id, Name = dzArtist.Name };

            var id = Services.DiscoveryManager.StubGuid("dz-album", al.Id.ToString());
            _cache.Put(id, new DiscoveryItemCache.Entry
            {
                Kind = "album",
                Artist = al.Artist?.Name ?? artistName,
                AlbumName = al.Title,
                DeezerAlbumId = al.Id,
                DeezerArtistId = al.Artist?.Id ?? dzArtist.Id,
                PrimaryImageUrl = al.CoverXl ?? al.CoverBig,
            });
            addItems.Add(BuildAlbumDto(id, al));
            existingTitles.Add(norm);
        }

        if (addItems.Count == 0)
        {
            _log.LogInformation("[mdiscover-search] artist-album augment: no new albums to add for {Artist}", artistName);
            return;
        }

        var combined = qr.Items.Concat(addItems).ToArray();
        qr.Items = combined;
        qr.TotalRecordCount = combined.Length;
        _log.LogInformation("[mdiscover-search] artist-album augment: added {N} stub albums for {Artist}", addItems.Count, artistName);
    }

    // -----------------------------------------------------------------
    // Artist's "Videos" tab — Jellyfin asks for /Items?IncludeItemTypes=
    // MusicVideo&AlbumArtistIds={id}. We hit ytmusic-stream-server's
    // /search/videos?artist=, register synthetic MusicVideo entities,
    // and append them to the response. Playback then routes through our
    // /Audio resolver (extended to handle Kind=video) to /video/by-id.
    // -----------------------------------------------------------------
    private bool ShouldAugmentArtistVideos(ResultExecutingContext ctx)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnableSearchInjection) return false;
        if (string.IsNullOrWhiteSpace(cfg.YtMusicStreamServerUrl)) return false;

        var http = ctx.HttpContext;
        // Music videos can hang off any of three artist-id fields depending
        // on which client/page is asking:
        //   - albumArtistIds  : the artist's "Discography" filter shape
        //   - artistIds       : "Songs" / generic artist filter
        //   - contributingArtistIds : the canonical link for music videos
        //     featuring an artist (web client uses this for the MV section)
        if (!TryGetGuidListFromQuery(http, "albumArtistIds", out var ids)
            && !TryGetGuidListFromQuery(http, "artistIds", out ids)
            && !TryGetGuidListFromQuery(http, "contributingArtistIds", out ids)
            && !TryGetGuidListFromQuery(http, "ContributingArtistIds", out ids))
            return false;
        if (ids.Count == 0) return false;

        var includeTypes = ExtractIncludeItemTypes(http);
        if (!includeTypes.Contains("MusicVideo")) return false;

        _log.LogInformation("[mdiscover-search] artist-video augment will fire: ids={Ids} types={Types}",
            string.Join(",", ids), string.Join(",", includeTypes));
        return ctx.Result is ObjectResult { Value: QueryResult<BaseItemDto> };
    }

    private async Task TryAugmentArtistVideosAsync(ResultExecutingContext ctx, CancellationToken ct)
    {
        if (ctx.Result is not ObjectResult or || or.Value is not QueryResult<BaseItemDto> qr) return;

        // Resolve the artist name. Look in priority order:
        //   1. Stub artist in our cache (Deezer/iTunes-sourced)
        //   2. Any item already in the response (existing local MV's title)
        //   3. The Jellyfin library — real artist looked up by guid
        string? artistName = null;
        var http = ctx.HttpContext;
        List<Guid> ids = new();
        foreach (var key in new[] { "albumArtistIds", "artistIds", "contributingArtistIds", "ContributingArtistIds" })
        {
            if (TryGetGuidListFromQuery(http, key, out ids) && ids.Count > 0) break;
        }
        foreach (var g in ids)
            if (_cache.TryGet(g, out var e) && e is { Kind: "artist", Artist: { Length: > 0 } })
            { artistName = e.Artist; break; }

        if (string.IsNullOrWhiteSpace(artistName))
        {
            foreach (var item in qr.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.AlbumArtist)) { artistName = item.AlbumArtist; break; }
                var first = item.Artists?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first)) { artistName = first; break; }
            }
        }
        if (string.IsNullOrWhiteSpace(artistName))
        {
            // Last resort: try the Jellyfin library — works for real artists
            // who don't have any MV's locally yet (qr.Items is empty for them).
            artistName = TryResolveLibraryArtistName(ids);
        }
        if (string.IsNullOrWhiteSpace(artistName))
        {
            _log.LogInformation("[mdiscover-search] artist-video augment skipped: no artist name");
            return;
        }
        _log.LogInformation("[mdiscover-search] artist-video augment: resolving for '{Artist}'", artistName);

        var cfg = Plugin.Instance!.Configuration;
        var baseUrl = cfg.YtMusicStreamServerUrl.TrimEnd('/');
        var url = $"{baseUrl}/search/videos?artist={Uri.EscapeDataString(artistName!)}&limit=20";

        List<YtVideoSearchResult> videos;
        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(7);
            var resp = await client.GetFromJsonAsync<YtVideoSearchResponse>(url, ct).ConfigureAwait(false);
            videos = resp?.Videos ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-search] artist-video fetch failed for {Artist}", artistName);
            return;
        }

        if (videos.Count == 0)
        {
            _log.LogInformation("[mdiscover-search] artist-video augment: no videos for {Artist}", artistName);
            return;
        }

        // Dedupe vs videos already in the response (in case the user has
        // local MusicVideo files for this artist).
        var existingTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in qr.Items)
        {
            var n = NormalizeTitle(item.Name);
            if (!string.IsNullOrEmpty(n)) existingTitles.Add(n);
        }

        var addItems = new List<BaseItemDto>();
        foreach (var v in videos)
        {
            if (string.IsNullOrEmpty(v.VideoId) || string.IsNullOrEmpty(v.Title)) continue;
            var norm = NormalizeTitle(v.Title);
            if (!string.IsNullOrEmpty(norm) && existingTitles.Contains(norm)) continue;

            var id = Services.DiscoveryManager.StubGuid("yt-video", v.VideoId);
            var entry = new DiscoveryItemCache.Entry
            {
                Kind = "video",
                Artist = v.Artist ?? artistName,
                AlbumName = null,
                TrackTitle = v.Title,
                PrimaryImageUrl = v.Thumbnail,
                DurationSeconds = v.DurationSeconds,
                VideoId = v.VideoId,
            };
            _cache.Put(id, entry);
            _registrar.RegisterStubMusicVideo(id, entry);
            addItems.Add(BuildMusicVideoDto(id, entry));
            existingTitles.Add(norm);
        }

        if (addItems.Count == 0) return;
        var combined = qr.Items.Concat(addItems).ToArray();
        qr.Items = combined;
        qr.TotalRecordCount = combined.Length;
        _log.LogInformation("[mdiscover-search] artist-video augment: added {N} videos for {Artist}", addItems.Count, artistName);
    }

    public static BaseItemDto BuildMusicVideoDto(Guid id, DiscoveryItemCache.Entry e)
    {
        NameGuidPair[]? artistPair = null;
        if (e.Artist is { Length: > 0 } an)
        {
            var artistGuid = Services.DiscoveryManager.StubGuid("name-artist", an);
            artistPair = new[] { new NameGuidPair { Name = an, Id = artistGuid } };
        }

        return new BaseItemDto
        {
            Id = id,
            ServerId = Plugin.ServerSystemId,
            Name = e.TrackTitle ?? "(unknown video)",
            Type = BaseItemKind.MusicVideo,
            MediaType = MediaType.Video,
            Tags = new[] { Tags.Stub },
            ImageTags = StubImageTag(id),
            ImageBlurHashes = EmptyBlurHashes(),
            PrimaryImageAspectRatio = 16.0 / 9.0,
            Artists = e.Artist is { Length: > 0 } a2 ? new[] { a2 } : Array.Empty<string>(),
            ArtistItems = artistPair ?? Array.Empty<NameGuidPair>(),
            AlbumArtists = artistPair ?? Array.Empty<NameGuidPair>(),
            AlbumArtist = e.Artist,
            RunTimeTicks = e.DurationSeconds.HasValue
                ? (long?)TimeSpan.FromSeconds(e.DurationSeconds.Value).Ticks
                : null,
            IsFolder = false,
            CanDownload = false,
            LocationType = MediaBrowser.Model.Entities.LocationType.FileSystem,
            // Without MediaSources the player has no stream URL to fetch
            // and the play button hangs forever. Same Path-based pattern as
            // audio stubs so web routes through /Videos/{id}/... which our
            // stream resolver intercepts and 302s to the Python /video
            // endpoint.
            MediaSources = new[] { BuildMediaSourceForVideo(id, e) },
        };
    }

    /// <summary>
    /// MediaSourceInfo for a synthetic MusicVideo stub. Same shape as the
    /// audio media-source — Path-based, Protocol=File, IsRemote=false —
    /// so the web client takes the universal route and lands on our
    /// stream resolver, which 302s to the Python /video/by-id endpoint.
    /// </summary>
    private static MediaBrowser.Model.Dto.MediaSourceInfo BuildMediaSourceForVideo(Guid id, DiscoveryItemCache.Entry e)
    {
        var runtimeTicks = e.DurationSeconds.HasValue
            ? (long?)TimeSpan.FromSeconds(e.DurationSeconds.Value).Ticks
            : null;

        var videoStream = new MediaBrowser.Model.Entities.MediaStream
        {
            Type = MediaBrowser.Model.Entities.MediaStreamType.Video,
            Codec = "h264",
            Width = 480,
            Height = 360,
            RealFrameRate = 30f,
            Index = 0,
            IsDefault = true,
        };
        var audioStream = new MediaBrowser.Model.Entities.MediaStream
        {
            Type = MediaBrowser.Model.Entities.MediaStreamType.Audio,
            Codec = "aac",
            BitRate = 128_000,
            SampleRate = 44_100,
            Channels = 2,
            ChannelLayout = "stereo",
            Index = 1,
            IsDefault = true,
        };

        return new MediaBrowser.Model.Dto.MediaSourceInfo
        {
            Id = id.ToString("N"),
            Path = $"/data/musicvideos/streamed/{id:N}.mp4",
            Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.File,
            IsRemote = false,
            Type = MediaBrowser.Model.Dto.MediaSourceType.Default,
            Name = e.TrackTitle ?? "(unknown video)",
            Container = "mp4",
            SupportsTranscoding = true,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            RunTimeTicks = runtimeTicks,
            Bitrate = 1_000_000,
            DefaultAudioStreamIndex = 1,
            MediaStreams = new List<MediaBrowser.Model.Entities.MediaStream> { videoStream, audioStream },
            MediaAttachments = Array.Empty<MediaBrowser.Model.Entities.MediaAttachment>(),
            Formats = Array.Empty<string>(),
            RequiredHttpHeaders = new Dictionary<string, string>(),
        };
    }

    // Response shapes for the /search/videos endpoint on ytmusic-stream-server.
    private class YtVideoSearchResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("artist")] public string? Artist { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("count")] public int Count { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("videos")] public List<YtVideoSearchResult> Videos { get; set; } = new();
    }
    private class YtVideoSearchResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("videoId")] public string? VideoId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("title")] public string? Title { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("artist")] public string? Artist { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("durationSeconds")] public int? DurationSeconds { get; set; }
    }

    private static string NormalizeTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // Drop common parenthetical adornments ("Deluxe", "Bonus Edition", "Remastered"...)
        // so re-issues collapse onto the library copy.
        var s = raw.ToLowerInvariant().Trim();
        // Cheap parenthetical strip — "the album (deluxe edition)" → "the album"
        var paren = s.IndexOf('(');
        if (paren > 0) s = s.Substring(0, paren).Trim();
        var bracket = s.IndexOf('[');
        if (bracket > 0) s = s.Substring(0, bracket).Trim();
        return s;
    }

    private static bool TryGetGuidListFromQuery(HttpContext http, string key, out List<Guid> ids)
    {
        ids = new List<Guid>();
        foreach (var k in new[] { key, key.ToLowerInvariant(), char.ToUpper(key[0]) + key.Substring(1) })
        {
            if (http.Request.Query.TryGetValue(k, out var qs))
            {
                foreach (var s in qs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Guid.TryParse(s, out var g)) ids.Add(g);
                if (ids.Count > 0) return true;
            }
        }
        return false;
    }

    private async Task TryAugmentAsync(ResultExecutingContext ctx, string term, CancellationToken ct)
    {
        if (ctx.Result is not ObjectResult or || or.Value is null) return;

        var cfg = Plugin.Instance!.Configuration;
        var limit = cfg.SearchLimit;
        var http = ctx.HttpContext;

        // Inspect mediaTypes / IncludeItemTypes to decide what kinds we're
        // allowed to inject. Jellyfin's web search splits results into tabs:
        //   "Videos" tab → mediaTypes=Video (drop our Audio/Album/Artist
        //                  injections; populate with MusicVideo from YT)
        //   "Audio" / All → audio injection only / everything (existing)
        var mediaTypes = ExtractStringSet(http, "mediaTypes", "MediaTypes");
        bool isVideoOnlySearch = mediaTypes.Contains("Video") && !mediaTypes.Contains("Audio");

        // Video-only search (e.g. the Videos tab of the global search box).
        // Skip the audio-side fans entirely and hit /search/videos for the
        // term. We populate MusicVideo stubs against the YT-Music index.
        if (isVideoOnlySearch)
        {
            if (!string.IsNullOrWhiteSpace(cfg.YtMusicStreamServerUrl)
                && or.Value is QueryResult<BaseItemDto> qrV)
            {
                await AugmentVideoSearchAsync(qrV, term, cfg, ct).ConfigureAwait(false);
            }
            return;
        }

        // Fan out Deezer (artists/albums/tracks) and Apple iTunes (songs only)
        // in parallel. iTunes is treated as track-only — its API doesn't
        // return artists/albums in a way that matches Deezer's shape, and
        // most discovery value is at the song level anyway.
        var artistsTask = _deezer.SearchArtistsAsync(term, limit, ct);
        var albumsTask = _deezer.SearchAlbumsAsync(term, limit, ct);
        var tracksTask = _deezer.SearchTracksAsync(term, limit, ct);
        var itunesTask = cfg.EnableItunesSearch
            ? _itunes.SearchSongsAsync(term, limit, ct)
            : Task.FromResult<ItunesClient.ITunesSearchResponse?>(null);
        await Task.WhenAll(artistsTask, albumsTask, tracksTask, itunesTask).ConfigureAwait(false);

        var artists = artistsTask.Result.Data ?? new();
        var albums = albumsTask.Result.Data ?? new();
        var tracks = tracksTask.Result.Data ?? new();
        var itunesSongs = itunesTask.Result?.Results ?? new();

        switch (or.Value)
        {
            case QueryResult<BaseItemDto> qr:
                AugmentItems(qr, artists, albums, tracks, itunesSongs, ctx.HttpContext);
                break;
            case SearchHintResult sh:
                or.Value = AugmentSearchHints(sh, artists, albums, tracks, itunesSongs);
                break;
        }
    }

    /// <summary>
    /// Populate the Videos tab of search results with YouTube music videos
    /// matching the search term. Reuses the existing artist-videos pipeline:
    /// /search/videos on the stream-server, MusicVideo stub registration,
    /// MusicVideo DTOs.
    /// </summary>
    private async Task AugmentVideoSearchAsync(QueryResult<BaseItemDto> qr, string term,
        Configuration.PluginConfiguration cfg, CancellationToken ct)
    {
        var url = $"{cfg.YtMusicStreamServerUrl.TrimEnd('/')}/search/videos?artist={Uri.EscapeDataString(term)}&limit=20";
        List<YtVideoSearchResult> videos;
        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(7);
            var resp = await client.GetFromJsonAsync<YtVideoSearchResponse>(url, ct).ConfigureAwait(false);
            videos = resp?.Videos ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-search] video-search fetch failed for '{Term}'", term);
            return;
        }
        if (videos.Count == 0)
        {
            _log.LogInformation("[mdiscover-search] video-search: 0 videos for '{Term}'", term);
            return;
        }

        var existingTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in qr.Items)
        {
            var n = NormalizeTitle(item.Name);
            if (!string.IsNullOrEmpty(n)) existingTitles.Add(n);
        }

        var addItems = new List<BaseItemDto>();
        foreach (var v in videos)
        {
            if (string.IsNullOrEmpty(v.VideoId) || string.IsNullOrEmpty(v.Title)) continue;
            var norm = NormalizeTitle(v.Title);
            if (!string.IsNullOrEmpty(norm) && existingTitles.Contains(norm)) continue;

            var id = Services.DiscoveryManager.StubGuid("yt-video", v.VideoId);
            var entry = new DiscoveryItemCache.Entry
            {
                Kind = "video",
                Artist = v.Artist,
                AlbumName = null,
                TrackTitle = v.Title,
                PrimaryImageUrl = v.Thumbnail,
                DurationSeconds = v.DurationSeconds,
                VideoId = v.VideoId,
            };
            _cache.Put(id, entry);
            _registrar.RegisterStubMusicVideo(id, entry);
            addItems.Add(BuildMusicVideoDto(id, entry));
            existingTitles.Add(norm);
        }

        if (addItems.Count == 0) return;
        var combined = qr.Items.Concat(addItems).ToArray();
        qr.Items = combined;
        qr.TotalRecordCount = combined.Length;
        _log.LogInformation("[mdiscover-search] video-search: appended {N} videos for '{Term}'", addItems.Count, term);
    }

    private static HashSet<string> ExtractStringSet(HttpContext http, params string[] keys)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (http.Request.Query.TryGetValue(key, out var qs))
                foreach (var t in qs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    result.Add(t);
        }
        return result;
    }

    // -----------------------------------------------------------------
    // /Items — QueryResult<BaseItemDto>
    // -----------------------------------------------------------------
    private void AugmentItems(QueryResult<BaseItemDto> qr,
        List<DeezerClient.DzArtist> artists,
        List<DeezerClient.DzAlbum> albums,
        List<DeezerClient.DzTrack> tracks,
        List<ItunesClient.ITunesSong> itunesSongs,
        HttpContext httpCtx)
    {
        var addItems = new List<BaseItemDto>();

        // Respect IncludeItemTypes — when the client asks for MusicAlbum only,
        // don't pollute the response with MusicArtist or Audio items (which
        // some clients render under "Albums" because they're indistinct cards).
        var includeTypes = ExtractIncludeItemTypes(httpCtx);
        bool wantArtists = includeTypes.Count == 0 || includeTypes.Contains("MusicArtist");
        bool wantAlbums  = includeTypes.Count == 0 || includeTypes.Contains("MusicAlbum");
        bool wantTracks  = includeTypes.Count == 0 || includeTypes.Contains("Audio") || includeTypes.Contains("AudioBook");

        // The /Artists/AlbumArtists endpoint is artist-only by definition —
        // forcing artist-only here keeps stub albums/tracks from leaking into
        // an autocomplete UI that's expecting artist cards.
        var path = httpCtx.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/Artists", StringComparison.OrdinalIgnoreCase))
        {
            wantArtists = true;
            wantAlbums = false;
            wantTracks = false;
        }

        // Dedupe vs items already in the response. Critical: if the user has a
        // real "Blink-182" artist in their library, we don't want to add a
        // second stub "Blink-182" card. Same for albums/tracks. Also lets the
        // augmented artist-page (real artist) include only the missing albums.
        var existingArtistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingAlbumKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingTrackKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in qr.Items)
        {
            if (item.Type == BaseItemKind.MusicArtist && !string.IsNullOrWhiteSpace(item.Name))
                existingArtistNames.Add(item.Name.Trim());
            else if (item.Type == BaseItemKind.MusicAlbum)
            {
                var key = $"{item.AlbumArtist ?? string.Empty}|{NormalizeTitle(item.Name)}";
                existingAlbumKeys.Add(key);
            }
            else if (item.Type == BaseItemKind.Audio)
            {
                var key = $"{item.AlbumArtist ?? item.Artists?.FirstOrDefault() ?? string.Empty}|{NormalizeTitle(item.Name)}";
                existingTrackKeys.Add(key);
            }
        }

        if (wantArtists)
        {
            foreach (var a in artists.Where(IsQualityArtist))
            {
                if (a.Name is { } an && existingArtistNames.Contains(an.Trim())) continue;
                var id = Services.DiscoveryManager.StubGuid("dz-artist", a.Id.ToString());
                _cache.Put(id, new DiscoveryItemCache.Entry
                {
                    Kind = "artist",
                    Artist = a.Name,
                    DeezerArtistId = a.Id,
                    PrimaryImageUrl = a.PictureXl ?? a.PictureBig,
                });
                addItems.Add(BuildArtistDto(id, a.Name));
            }
        }

        if (wantAlbums)
        {
            foreach (var al in albums.Where(IsQualityAlbum))
            {
                var key = $"{al.Artist?.Name ?? string.Empty}|{NormalizeTitle(al.Title)}";
                if (existingAlbumKeys.Contains(key)) continue;
                var id = Services.DiscoveryManager.StubGuid("dz-album", al.Id.ToString());
                _cache.Put(id, new DiscoveryItemCache.Entry
                {
                    Kind = "album",
                    Artist = al.Artist?.Name,
                    AlbumName = al.Title,
                    DeezerAlbumId = al.Id,
                    DeezerArtistId = al.Artist?.Id,
                    PrimaryImageUrl = al.CoverXl ?? al.CoverBig,
                });
                addItems.Add(BuildAlbumDto(id, al));
            }
        }

        if (wantTracks)
        {
            foreach (var t in tracks)
            {
                // Filter tracks the same way: skip those associated with karaoke /
                // tribute / cover albums (judging by the embedded album metadata).
                if (t.Album is { } talAlbum && !IsQualityAlbum(talAlbum)) continue;
                // And skip tracks whose primary artist is a karaoke producer.
                if (t.Artist is { } tartist && !IsQualityArtist(tartist)) continue;

                // Skip if a real library track with same artist+title already exists.
                var trackKey = $"{t.Artist?.Name ?? string.Empty}|{NormalizeTitle(t.Title)}";
                if (existingTrackKeys.Contains(trackKey)) continue;

                var id = Services.DiscoveryManager.StubGuid("dz-track", t.Id.ToString());
                var trackEntry = new DiscoveryItemCache.Entry
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
                _cache.Put(id, trackEntry);
                // Pre-register a synthetic Audio item so PlaylistManager can
                // find this stub via GetItemById without the user needing to
                // play the track first.
                _registrar.RegisterStubTrack(id, trackEntry);

                // Side-effect: cache the embedded album too, so clicking through
                // to the album works. Apply quality filter to skip karaoke/tribute.
                if (t.Album is { } al && al.Id != 0 && IsQualityAlbum(al))
                {
                    var albumStubId = Services.DiscoveryManager.StubGuid("dz-album", al.Id.ToString());
                    if (!_cache.TryGet(albumStubId, out var existing) || existing is null)
                    {
                        _cache.Put(albumStubId, new DiscoveryItemCache.Entry
                        {
                            Kind = "album",
                            Artist = al.Artist?.Name ?? t.Artist?.Name,
                            AlbumName = al.Title,
                            DeezerAlbumId = al.Id,
                            DeezerArtistId = al.Artist?.Id ?? t.Artist?.Id,
                            PrimaryImageUrl = al.CoverXl ?? al.CoverBig,
                        });
                    }
                }

                addItems.Add(BuildTrackDto(id, t, parentAlbumId: null));

                // Track this so iTunes results that match the same artist+title
                // get deduped against this Deezer hit.
                existingTrackKeys.Add(trackKey);
            }
        }

        // iTunes-sourced tracks (Apple's catalog). Only add when the user
        // hasn't disabled them, when the request is asking for tracks, and
        // when the (artist, title) pair isn't already represented by a
        // library track or a Deezer hit. iTunes gives us no artist or
        // album entity, so we only contribute tracks here.
        if (wantTracks)
        {
            foreach (var s in itunesSongs)
            {
                if (string.IsNullOrEmpty(s.TrackName) || string.IsNullOrEmpty(s.ArtistName)) continue;
                var iTrackKey = $"{s.ArtistName}|{NormalizeTitle(s.TrackName)}";
                if (existingTrackKeys.Contains(iTrackKey)) continue;

                var id = s.TrackId.HasValue
                    ? Services.DiscoveryManager.StubGuid("itunes-track", s.TrackId.Value.ToString())
                    : Services.DiscoveryManager.StubGuid("itunes-track-name", iTrackKey);
                var artwork = ItunesClient.UpscaleArtworkUrl(s.ArtworkUrl100, 1000);
                var trackEntry = new DiscoveryItemCache.Entry
                {
                    Kind = "track",
                    Artist = s.ArtistName,
                    AlbumName = s.CollectionName,
                    TrackTitle = s.TrackName,
                    // No Deezer ids — playback resolves through the YT proxy
                    // by artist+title, same as YT-sourced discovery tracks.
                    PrimaryImageUrl = artwork,
                    DurationSeconds = null,
                };
                _cache.Put(id, trackEntry);
                _registrar.RegisterStubTrack(id, trackEntry);
                addItems.Add(BuildItunesTrackDto(id, s, artwork));
                existingTrackKeys.Add(iTrackKey);
            }
        }

        if (addItems.Count == 0) return;

        var combined = qr.Items.Concat(addItems).ToArray();
        qr.Items = combined;
        qr.TotalRecordCount = combined.Length;
    }

    // -----------------------------------------------------------------
    // /Search/Hints — SearchHintResult
    // -----------------------------------------------------------------
    private SearchHintResult AugmentSearchHints(SearchHintResult sh,
        List<DeezerClient.DzArtist> artists,
        List<DeezerClient.DzAlbum> albums,
        List<DeezerClient.DzTrack> tracks,
        List<ItunesClient.ITunesSong> itunesSongs)
    {
        var hits = new List<SearchHint>();
        var seenTrackKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in artists.Where(IsQualityArtist))
        {
            var id = Services.DiscoveryManager.StubGuid("dz-artist", a.Id.ToString());
            _cache.Put(id, new DiscoveryItemCache.Entry { Kind = "artist", Artist = a.Name, DeezerArtistId = a.Id, PrimaryImageUrl = a.PictureXl ?? a.PictureBig });
            hits.Add(new SearchHint { Id = id, Name = a.Name ?? "", Type = BaseItemKind.MusicArtist, MediaType = MediaType.Unknown });
        }
        foreach (var al in albums.Where(IsQualityAlbum))
        {
            var id = Services.DiscoveryManager.StubGuid("dz-album", al.Id.ToString());
            _cache.Put(id, new DiscoveryItemCache.Entry { Kind = "album", Artist = al.Artist?.Name, AlbumName = al.Title, DeezerAlbumId = al.Id, DeezerArtistId = al.Artist?.Id, PrimaryImageUrl = al.CoverXl ?? al.CoverBig });
            hits.Add(new SearchHint { Id = id, Name = al.Title ?? "", Type = BaseItemKind.MusicAlbum, MediaType = MediaType.Unknown, Album = al.Title, AlbumArtist = al.Artist?.Name });
        }
        foreach (var t in tracks)
        {
            // Skip tracks tied to karaoke / tribute albums or artists.
            if (t.Album is { } talAlbum && !IsQualityAlbum(talAlbum)) continue;
            if (t.Artist is { } tartist && !IsQualityArtist(tartist)) continue;

            var id = Services.DiscoveryManager.StubGuid("dz-track", t.Id.ToString());
            var trackEntry = new DiscoveryItemCache.Entry { Kind = "track", Artist = t.Artist?.Name, AlbumName = t.Album?.Title, TrackTitle = t.Title, DeezerTrackId = t.Id, DeezerAlbumId = t.Album?.Id, DeezerArtistId = t.Artist?.Id, PrimaryImageUrl = t.Album?.CoverBig, DurationSeconds = t.Duration };
            _cache.Put(id, trackEntry);
            _registrar.RegisterStubTrack(id, trackEntry);
            hits.Add(new SearchHint { Id = id, Name = t.Title ?? "", Type = BaseItemKind.Audio, MediaType = MediaType.Audio, Album = t.Album?.Title, AlbumArtist = t.Artist?.Name, RunTimeTicks = t.Duration.HasValue ? (long?)TimeSpan.FromSeconds(t.Duration.Value).Ticks : null });
            seenTrackKeys.Add($"{t.Artist?.Name ?? string.Empty}|{NormalizeTitle(t.Title)}");
        }

        // iTunes-sourced track hints (Apple's catalog), deduped against the
        // Deezer hits above. We only contribute Audio hints — Apple's API
        // shape doesn't give us standalone artist/album entities.
        foreach (var s in itunesSongs)
        {
            if (string.IsNullOrEmpty(s.TrackName) || string.IsNullOrEmpty(s.ArtistName)) continue;
            var key = $"{s.ArtistName}|{NormalizeTitle(s.TrackName)}";
            if (seenTrackKeys.Contains(key)) continue;
            var id = s.TrackId.HasValue
                ? Services.DiscoveryManager.StubGuid("itunes-track", s.TrackId.Value.ToString())
                : Services.DiscoveryManager.StubGuid("itunes-track-name", key);
            var artwork = ItunesClient.UpscaleArtworkUrl(s.ArtworkUrl100, 1000);
            var entry = new DiscoveryItemCache.Entry
            {
                Kind = "track",
                Artist = s.ArtistName,
                AlbumName = s.CollectionName,
                TrackTitle = s.TrackName,
                PrimaryImageUrl = artwork,
                DurationSeconds = null,
            };
            _cache.Put(id, entry);
            _registrar.RegisterStubTrack(id, entry);
            hits.Add(new SearchHint
            {
                Id = id,
                Name = s.TrackName,
                Type = BaseItemKind.Audio,
                MediaType = MediaType.Audio,
                Album = s.CollectionName,
                AlbumArtist = s.ArtistName,
                RunTimeTicks = null,
            });
            seenTrackKeys.Add(key);
        }

        if (hits.Count == 0) return sh;

        var combined = (sh.SearchHints ?? Array.Empty<SearchHint>()).Concat(hits).ToArray();
        // SearchHintResult has read-only Items / TotalRecordCount, so build a new one.
        return new SearchHintResult(combined, combined.Length);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------
    private static string? ExtractSearchTerm(HttpContext http)
    {
        if (http.Request.Query.TryGetValue("searchTerm", out var qs) && !string.IsNullOrWhiteSpace(qs))
            return qs.ToString();
        if (http.Request.Query.TryGetValue("SearchTerm", out var qs2) && !string.IsNullOrWhiteSpace(qs2))
            return qs2.ToString();
        return null;
    }

    private static HashSet<string> ExtractIncludeItemTypes(HttpContext http)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "includeItemTypes", "IncludeItemTypes" })
        {
            if (http.Request.Query.TryGetValue(key, out var qs))
                foreach (var t in qs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    result.Add(t);
        }
        return result;
    }

    // Shared bad-keyword list — applied to album titles, artist names, and tracks.
    // Most of these come from Deezer's habit of cataloguing karaoke / tribute /
    // soundalike compilations as legitimate artists or albums.
    private static readonly string[] BadKeywords = new[]
    {
        "karaoke",
        "tribute",
        "covers of",
        "in the style of",
        "made famous by",
        "originally performed",
        "instrumental versions",
        "music box",        // "Angel's Music Box" tribute compilations
        "soundalike",
    };

    /// <summary>
    /// Filter out low-quality artist search results: karaoke producers, tribute
    /// bands, "music box" compilers, and the catch-all "Various Artists".
    /// Same keyword set as IsQualityAlbum for consistency.
    /// </summary>
    private static bool IsQualityArtist(DeezerClient.DzArtist a)
    {
        if (a is null) return false;
        var name = (a.Name ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name == "various artists" || name == "various") return false;
        foreach (var b in BadKeywords)
        {
            if (name.Contains(b)) return false;
        }
        return true;
    }

    /// <summary>
    /// Filter out low-quality album search results that clutter the UI:
    /// karaoke versions, "tribute to" / "covers of" albums, and Deezer's
    /// `compile` record type (compilations / "Various Artists" garbage).
    /// </summary>
    private static bool IsQualityAlbum(DeezerClient.DzAlbum al)
    {
        // record_type filter — keep only proper releases.
        if (al.RecordType is { } rt &&
            !string.Equals(rt, "album", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rt, "ep", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rt, "single", StringComparison.OrdinalIgnoreCase))
            return false;

        // Reject by title/artist patterns that scream tribute/karaoke/covers.
        var title = (al.Title ?? string.Empty).ToLowerInvariant();
        var artistName = (al.Artist?.Name ?? string.Empty).ToLowerInvariant();
        foreach (var b in BadKeywords)
        {
            if (title.Contains(b)) return false;
            if (artistName.Contains(b)) return false;
        }

        // Reject the "Various Artists" / "Various" tropes — typically compilations
        // we already filtered above, but Deezer sometimes reports them as albums.
        if (artistName == "various artists" || artistName == "various") return false;

        return true;
    }

    private static int? ParseYear(string? date)
    {
        if (date is null || date.Length < 4) return null;
        return int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
    }

    private static Dictionary<ImageType, string> StubImageTag(Guid id) =>
        new() { { ImageType.Primary, "deezer-" + id.ToString("N") } };

    // Empty-but-present blur-hash dict + aspect ratio satisfy the web client's
    // `getScaledImageUrl()` which crashes on undefined when ImageTags is set
    // but the rest of the image metadata is missing.
    private static Dictionary<ImageType, Dictionary<string, string>> EmptyBlurHashes() =>
        new() { { ImageType.Primary, new Dictionary<string, string>() } };

    /// <summary>Build a stub artist DTO. ServerId stamped so client routing works.</summary>
    public static BaseItemDto BuildArtistDto(Guid id, string? name) => new()
    {
        Id = id,
        ServerId = Plugin.ServerSystemId,
        Name = name ?? "(unknown artist)",
        Type = BaseItemKind.MusicArtist,
        MediaType = MediaType.Unknown,
        Tags = new[] { Tags.Stub },
        ImageTags = StubImageTag(id),
        ImageBlurHashes = EmptyBlurHashes(),
        PrimaryImageAspectRatio = 1.0,
        IsFolder = true,
    };

    public static BaseItemDto BuildAlbumDto(Guid id, DeezerClient.DzAlbum al)
    {
        NameGuidPair[]? artistPair = null;
        if (al.Artist is { } artist && !string.IsNullOrEmpty(artist.Name))
        {
            var artistGuid = Services.DiscoveryManager.StubGuid("dz-artist", artist.Id.ToString());
            artistPair = new[] { new NameGuidPair { Name = artist.Name, Id = artistGuid } };
        }

        return new BaseItemDto
        {
            Id = id,
            ServerId = Plugin.ServerSystemId,
            Name = al.Title ?? "(unknown album)",
            Type = BaseItemKind.MusicAlbum,
            MediaType = MediaType.Unknown,
            Tags = new[] { Tags.Stub },
            ImageTags = StubImageTag(id),
            ImageBlurHashes = EmptyBlurHashes(),
            PrimaryImageAspectRatio = 1.0,
            ProviderIds = new Dictionary<string, string> { { ProviderIds.DeezerAlbum, al.Id.ToString() } },
            Artists = al.Artist?.Name is { Length: > 0 } an ? new[] { an } : null,
            AlbumArtist = al.Artist?.Name,
            AlbumArtists = artistPair ?? Array.Empty<NameGuidPair>(),
            ArtistItems = artistPair ?? Array.Empty<NameGuidPair>(),
            ProductionYear = ParseYear(al.ReleaseDate),
            ChildCount = al.TrackCount,
            IsFolder = true,
        };
    }

    /// <summary>
    /// Build a track DTO from an iTunes Search hit. Mirrors BuildTrackDto's
    /// shape so the same UI codepaths (album-art rendering, tap-to-play,
    /// playlist add) work transparently. We don't have a real Deezer or
    /// MusicBrainz artist id, so we synthesize a stable name-based guid for
    /// the ArtistItems/AlbumArtists pair — same approach we use for YT
    /// playlist tracks.
    /// </summary>
    public static BaseItemDto BuildItunesTrackDto(Guid id, ItunesClient.ITunesSong s, string? artworkUrl)
    {
        NameGuidPair[]? artistPair = null;
        if (!string.IsNullOrEmpty(s.ArtistName))
        {
            var artistGuid = s.ArtistId.HasValue
                ? Services.DiscoveryManager.StubGuid("itunes-artist", s.ArtistId.Value.ToString())
                : Services.DiscoveryManager.StubGuid("name-artist", s.ArtistName);
            artistPair = new[] { new NameGuidPair { Name = s.ArtistName, Id = artistGuid } };
        }

        var entry = new DiscoveryItemCache.Entry
        {
            Kind = "track",
            Artist = s.ArtistName,
            AlbumName = s.CollectionName,
            TrackTitle = s.TrackName,
            PrimaryImageUrl = artworkUrl,
            DurationSeconds = null,
        };

        return new BaseItemDto
        {
            Id = id,
            ServerId = Plugin.ServerSystemId,
            Name = s.TrackName ?? "(unknown track)",
            Type = BaseItemKind.Audio,
            MediaType = MediaType.Audio,
            Tags = new[] { Tags.Stub },
            ImageTags = StubImageTag(id),
            ImageBlurHashes = EmptyBlurHashes(),
            PrimaryImageAspectRatio = 1.0,
            ProviderIds = s.TrackId.HasValue
                ? new Dictionary<string, string> { { "AppleMusic", s.TrackId.Value.ToString() } }
                : new Dictionary<string, string>(),
            Artists = s.ArtistName is { Length: > 0 } an ? new[] { an } : null,
            ArtistItems = artistPair ?? Array.Empty<NameGuidPair>(),
            AlbumArtists = artistPair ?? Array.Empty<NameGuidPair>(),
            Album = s.CollectionName,
            AlbumArtist = s.ArtistName,
            IsFolder = false,
            CanDownload = false,
            LocationType = MediaBrowser.Model.Entities.LocationType.FileSystem,
            // MediaSources required for tap-to-play in list views.
            MediaSources = new[] { MusicItemDetailActionFilter.BuildMediaSourceForTrack(id, entry) },
        };
    }

    public static BaseItemDto BuildTrackDto(Guid id, DeezerClient.DzTrack t, Guid? parentAlbumId)
    {
        // The tracklist renderer crashes silently with TypeError if ArtistItems
        // or AlbumArtists is undefined (it does .map(e => e.Id).sort() on both).
        // We synthesize matching NameGuidPair[] for each so the renderer can
        // compare them (it shows an artist column when they differ; same =
        // no extra column, but the array access still has to succeed).
        NameGuidPair[]? artistPair = null;
        if (t.Artist is { } artist && !string.IsNullOrEmpty(artist.Name))
        {
            var artistGuid = Services.DiscoveryManager.StubGuid("dz-artist", artist.Id.ToString());
            artistPair = new[] { new NameGuidPair { Name = artist.Name, Id = artistGuid } };
        }

        // For tracks, the "primary" image really lives on the album. Resolve
        // an album stub guid from either the explicit parent param OR the
        // track's own Deezer.album so search-result tracks (which have no
        // explicit parent) still expose AlbumId. Without AlbumId/ParentId,
        // Finer / iOS won't engage the player on a tap-to-play.
        var resolvedAlbumId = parentAlbumId
            ?? (t.Album is not null
                ? Services.DiscoveryManager.StubGuid("dz-album", t.Album.Id.ToString())
                : (Guid?)null);
        var albumImageTag = resolvedAlbumId is { } a ? "deezer-" + a.ToString("N") : null;

        // Build a cache entry that mirrors what we'd persist, so MediaSource
        // construction goes through the same path as the BuildDtoFromCache
        // track branch — keeping web's tap-to-play handler happy.
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

        return new BaseItemDto
        {
            Id = id,
            ServerId = Plugin.ServerSystemId,
            Name = t.Title ?? "(unknown track)",
            Type = BaseItemKind.Audio,
            MediaType = MediaType.Audio,
            Tags = new[] { Tags.Stub },
            ImageTags = StubImageTag(id),
            ImageBlurHashes = EmptyBlurHashes(),
            PrimaryImageAspectRatio = 1.0,
            AlbumPrimaryImageTag = albumImageTag,
            AlbumId = resolvedAlbumId,
            ProviderIds = new Dictionary<string, string> { { ProviderIds.DeezerTrack, t.Id.ToString() } },
            Artists = t.Artist?.Name is { Length: > 0 } an ? new[] { an } : null,
            ArtistItems = artistPair ?? Array.Empty<NameGuidPair>(),
            AlbumArtists = artistPair ?? Array.Empty<NameGuidPair>(),
            Album = t.Album?.Title,
            AlbumArtist = t.Artist?.Name,
            ParentId = resolvedAlbumId,
            IndexNumber = t.TrackPosition,
            ParentIndexNumber = t.DiskNumber,
            RunTimeTicks = t.Duration.HasValue ? (long?)TimeSpan.FromSeconds(t.Duration.Value).Ticks : null,
            IsFolder = false,
            CanDownload = false,
            // FileSystem (not Remote) so web's tap-to-play handler routes
            // through /Audio/{id}/universal — that's the path our stream
            // resolver intercepts.
            LocationType = MediaBrowser.Model.Entities.LocationType.FileSystem,
            // MediaSources is what tap-to-play reads from the list item
            // before constructing the player URL. Without it, single-click
            // does nothing and only the three-dot menu's Play option works.
            MediaSources = new[] { MusicItemDetailActionFilter.BuildMediaSourceForTrack(id, entry) },
        };
    }
}
