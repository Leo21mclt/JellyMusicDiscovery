using System;
using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Services;

/// <summary>
/// Registers synthetic Audio entities in Jellyfin's in-memory library cache
/// so `_libraryManager.GetItemById(stubGuid)` returns something. This is the
/// hook that makes our stubs participate in Jellyfin's normal item flows:
/// session NowPlayingItem updates, playlist add/remove, UserData saves
/// (recently-played, favorites), etc.
///
/// Items are registered with `IsVirtualItem=true` and a synthetic Path so
/// Jellyfin treats them as "remote, no file" rather than expecting a disk
/// scan to find them.
/// </summary>
public class LibraryRegistrar
{
    private readonly ILibraryManager _lib;
    private readonly ILogger<LibraryRegistrar> _log;
    // Process-wide dedup so we don't redundantly register on every single
    // search response. We track a small fingerprint of what was registered
    // so when the entry's metadata improves (e.g. Artist becomes populated
    // after a fresh discover-feed fetch), we can re-register and overwrite
    // the in-memory Audio. Reset on Jellyfin restart (which also wipes the
    // ILibraryManager cache, so the bookkeeping stays in sync).
    private static readonly ConcurrentDictionary<Guid, string> _registered = new();

    public LibraryRegistrar(ILibraryManager lib, ILogger<LibraryRegistrar> log)
    {
        _lib = lib;
        _log = log;
    }

    /// <summary>
    /// Register a stub track entry as a synthetic Audio item. Idempotent.
    /// Re-registers when the entry's artist/album/title fingerprint
    /// changes — this matters for YT Music tracks whose Artist field was
    /// added after the original registration. Returns true on first
    /// registration or update, false if the cached fingerprint matched.
    /// </summary>
    public bool RegisterStubTrack(Guid id, DiscoveryItemCache.Entry entry)
    {
        if (entry.Kind != "track") return false;

        var fingerprint = Fingerprint(entry);
        if (_registered.TryGetValue(id, out var prev) && prev == fingerprint)
            return false;

        try
        {
            var audio = new Audio
            {
                Id = id,
                Name = entry.TrackTitle ?? "(unknown track)",
                Album = entry.AlbumName,
                Artists = !string.IsNullOrEmpty(entry.Artist) ? new[] { entry.Artist! } : Array.Empty<string>(),
                AlbumArtists = !string.IsNullOrEmpty(entry.Artist) ? new[] { entry.Artist! } : Array.Empty<string>(),
                RunTimeTicks = entry.DurationSeconds.HasValue
                    ? (long?)TimeSpan.FromSeconds(entry.DurationSeconds.Value).Ticks
                    : null,
                DateCreated = DateTime.UtcNow,
                // IsVirtualItem=false so PlaylistManager's resolve-items step
                // (which silently drops virtual items) accepts the stub and
                // actually persists it to the playlist's LinkedChildren.
                IsVirtualItem = false,
                // Synthetic path that won't collide with real library files;
                // /mdiscover/ is our prefix, .mp3 matches the format we serve.
                Path = $"/mdiscover/{id:N}.mp3",
            };
            _lib.RegisterItem(audio);
            _registered[id] = fingerprint;
            _log.LogInformation("[mdiscover] {Verb} synthetic Audio for stub {Id}: {Title} — {Artist}",
                prev is null ? "registered" : "updated",
                id, audio.Name, entry.Artist ?? "(unknown artist)");
            return true;
        }
        catch (Exception ex)
        {
            // Registration is opportunistic — failure shouldn't break the
            // rest of the request flow. We just won't get full integration
            // (playlist add, recently-played) for this particular stub.
            _log.LogDebug(ex, "[mdiscover] RegisterStubTrack failed for {Id}", id);
            // Allow retry on next attempt.
            _registered.TryRemove(id, out _);
            return false;
        }
    }

    /// <summary>
    /// Register a stub video entry as a synthetic MusicVideo item. Same
    /// idempotent fingerprint pattern as audio; emits a MusicVideo
    /// instead of Audio so Jellyfin's per-artist Videos tab will surface
    /// it. Used by the artist-videos augmenter to populate music videos
    /// from YouTube.
    /// </summary>
    public bool RegisterStubMusicVideo(Guid id, DiscoveryItemCache.Entry entry)
    {
        if (entry.Kind != "video") return false;

        var fingerprint = "v:" + Fingerprint(entry);
        if (_registered.TryGetValue(id, out var prev) && prev == fingerprint)
            return false;

        try
        {
            var mv = new MusicVideo
            {
                Id = id,
                Name = entry.TrackTitle ?? "(unknown video)",
                Album = entry.AlbumName,
                Artists = !string.IsNullOrEmpty(entry.Artist) ? new[] { entry.Artist! } : Array.Empty<string>(),
                RunTimeTicks = entry.DurationSeconds.HasValue
                    ? (long?)TimeSpan.FromSeconds(entry.DurationSeconds.Value).Ticks
                    : null,
                DateCreated = DateTime.UtcNow,
                IsVirtualItem = false,
                // Distinct path prefix so the audio resolver doesn't try to
                // proxy it as audio. The video resolver matches on
                // /mdiscover-video/ and routes to /video/by-id/{vid}.
                Path = $"/mdiscover-video/{id:N}.mp4",
            };
            _lib.RegisterItem(mv);
            _registered[id] = fingerprint;
            _log.LogInformation("[mdiscover] {Verb} synthetic MusicVideo for stub {Id}: {Title} — {Artist}",
                prev is null ? "registered" : "updated",
                id, mv.Name, entry.Artist ?? "(unknown artist)");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[mdiscover] RegisterStubMusicVideo failed for {Id}", id);
            _registered.TryRemove(id, out _);
            return false;
        }
    }

    /// <summary>
    /// Compact string identifying the entry's identity-relevant fields.
    /// We re-register whenever this changes so a previously empty Artist
    /// can be filled in by a later refresh.
    /// </summary>
    private static string Fingerprint(DiscoveryItemCache.Entry e)
        => string.Concat(
            e.TrackTitle ?? "", "␟",
            e.Artist ?? "", "␟",
            e.AlbumName ?? "", "␟",
            e.DurationSeconds?.ToString() ?? "");
}
