using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JellyMusicDiscovery.Services;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Controllers;

/// <summary>
/// /mdiscover/* — discovery actions invoked by the web overlay JS.
/// </summary>
[ApiController]
[Route("mdiscover")]
[Authorize]
public class DiscoveryController : ControllerBase
{
    private readonly DiscoveryManager _discovery;
    private readonly DeezerClient _deezer;
    private readonly LidarrClient _lidarr;
    private readonly ILibraryManager _library;
    private readonly DiscoveryItemCache _cache;
    private readonly ILogger<DiscoveryController> _log;

    public DiscoveryController(
        DiscoveryManager discovery,
        DeezerClient deezer,
        LidarrClient lidarr,
        ILibraryManager library,
        DiscoveryItemCache cache,
        ILogger<DiscoveryController> log)
    {
        _discovery = discovery;
        _deezer = deezer;
        _lidarr = lidarr;
        _library = library;
        _cache = cache;
        _log = log;
    }

    /// <summary>
    /// POST /mdiscover/request/album/{itemId}
    /// Requests an album stub via Lidarr. Routes by available provider IDs:
    ///   - MBID present → use the MB-MBID lookup path
    ///   - DeezerAlbum present → use the artist+album text lookup path
    ///   - neither → fall back to the item's Name + Artists
    /// </summary>
    [HttpPost("request/album/{itemId}")]
    public async Task<IActionResult> RequestAlbum(string itemId, CancellationToken ct)
    {
        if (!Guid.TryParse(itemId, out var id)) return BadRequest("invalid id");

        // 1. Try the live library first (real items or stubs we previously persisted).
        var item = _library.GetItemById(id);
        if (item is not null)
        {
            var mbid = item.GetProviderId(ProviderIds.MusicBrainzReleaseGroup);
            LidarrClient.RequestResult res;

            if (!string.IsNullOrEmpty(mbid))
                res = await _lidarr.AddAlbumByMbidAsync(mbid, ct).ConfigureAwait(false);
            else
            {
                var ma = item as MusicAlbum;
                var artist = ma?.AlbumArtists?.FirstOrDefault() ?? ma?.Artists?.FirstOrDefault() ?? string.Empty;
                var album = item.Name ?? string.Empty;
                res = await _lidarr.AddAlbumByTextAsync(artist, album, ct).ConfigureAwait(false);
            }

            return res.Success ? Ok(new { res.Message }) : StatusCode(502, new { res.Message });
        }

        // 2. Fall back to the in-memory cache populated by MusicSearchActionFilter.
        if (_cache.TryGet(id, out var entry) && entry is not null && entry.Kind == "album")
        {
            var res = await _lidarr.AddAlbumByTextAsync(entry.Artist ?? string.Empty, entry.AlbumName ?? string.Empty, ct).ConfigureAwait(false);
            return res.Success ? Ok(new { res.Message }) : StatusCode(502, new { res.Message });
        }

        return NotFound("Unknown item id (not in library or recent search cache).");
    }

    /// <summary>GET /mdiscover/album/{itemId}/tracks — lazy-materialize track stubs for a Deezer album stub.</summary>
    [HttpGet("album/{itemId}/tracks")]
    public async Task<IActionResult> AlbumTracks(string itemId, CancellationToken ct)
    {
        if (!Guid.TryParse(itemId, out var id)) return BadRequest("invalid id");
        if (_library.GetItemById(id) is not MusicAlbum album) return NotFound();

        // Deezer path: pull the actual tracklist from Deezer and materialize each.
        var dzAlbumId = album.GetProviderId(ProviderIds.DeezerAlbum);
        if (!string.IsNullOrEmpty(dzAlbumId) && long.TryParse(dzAlbumId, out var dzId))
        {
            var tracks = await _deezer.GetAlbumTracksAsync(dzId, ct).ConfigureAwait(false);
            if (tracks is null) return Ok(new { count = 0, items = Array.Empty<object>() });
            var stubs = tracks.Data
                .Select(t => _discovery.EnsureTrackStubFromDeezer(t, album, ct))
                .Select(t => new
                {
                    id = t.Id.ToString("N"),
                    name = t.Name,
                    runTimeTicks = t.RunTimeTicks,
                })
                .ToArray();
            return Ok(new { count = stubs.Length, items = stubs });
        }

        // MB path is the original behaviour, kept for `SearchSource = musicbrainz`.
        return Ok(new { count = 0, items = Array.Empty<object>() });
    }

    /// <summary>GET /mdiscover/health — sanity check.</summary>
    [HttpGet("health")]
    [AllowAnonymous]
    public IActionResult Health() => Ok(new
    {
        ok = true,
        plugin = Plugin.Instance?.Name,
        version = "0.1.0",
        searchSource = Plugin.Instance?.Configuration?.SearchSource,
    });
}
