using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Services;

/// <summary>
/// Owns the conversion of MusicBrainz results to Jellyfin BaseItems and the
/// "stub" lifecycle: created on search, deduplicated against real items, replaced
/// once Lidarr/slskd actually deliver the file.
/// </summary>
public class DiscoveryManager
{
    private static readonly Guid StubNamespace = Guid.Parse("0e9a3c11-2b6a-4f5e-9a3a-77c2b8c9d111");

    private readonly ILibraryManager _library;
    private readonly IItemRepository _items;
    private readonly MusicBrainzClient _mb;
    private readonly DeezerClient _deezer;
    private readonly CoverArtArchiveClient _caa;
    private readonly ILogger<DiscoveryManager> _log;
    private readonly ConcurrentDictionary<string, byte> _ensured = new();
    private Folder? _musicLibraryCache;

    public DiscoveryManager(
        ILibraryManager library,
        IItemRepository items,
        MusicBrainzClient mb,
        DeezerClient deezer,
        CoverArtArchiveClient caa,
        ILogger<DiscoveryManager> log)
    {
        _library = library;
        _items = items;
        _mb = mb;
        _deezer = deezer;
        _caa = caa;
        _log = log;
    }

    /// <summary>
    /// Resolve the user's first Music CollectionFolder. Stubs need a real Folder
    /// parent or ILibraryManager.CreateItem rejects them. Cached after first hit.
    /// </summary>
    private Folder? GetMusicLibraryFolder()
    {
        if (_musicLibraryCache is { } cached) return cached;
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.CollectionFolder },
        };
        var folder = _library.GetItemList(query)
            .OfType<CollectionFolder>()
            .FirstOrDefault(f => string.Equals(f.CollectionType?.ToString(), "music", StringComparison.OrdinalIgnoreCase));
        _musicLibraryCache = folder;
        if (folder is null) _log.LogWarning("No Music CollectionFolder found — stubs cannot be registered.");
        return folder;
    }

    /// <summary>
    /// Wraps ILibraryManager.CreateItem with logging + a fallback to SaveItems
    /// when no parent is available (so we still get DB rows in degraded mode).
    /// </summary>
    private void RegisterStub(BaseItem item, BaseItem? parent, CancellationToken ct)
    {
        if (parent is not null)
        {
            try { _library.CreateItem(item, parent); return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "CreateItem failed for {Name}; falling back to SaveItems", item.Name);
            }
        }
        _items.SaveItems(new[] { item }, ct);
    }

    // -----------------------------------------------------------------
    // Deterministic Guid derivation: same MBID → same Jellyfin item id, so
    // re-running the same search doesn't insert duplicates.
    // -----------------------------------------------------------------
    public static Guid StubGuid(string kind, string mbid)
    {
        Span<byte> input = stackalloc byte[64];
        var ns = StubNamespace.ToByteArray();
        Buffer.BlockCopy(ns, 0, input.ToArray(), 0, 16);
        var bytes = Encoding.UTF8.GetBytes($"{StubNamespace}|{kind}|{mbid}");
        var hash = MD5.HashData(bytes);
        // Set version (3) and variant bits to make it a valid v3 UUID.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }

    // -----------------------------------------------------------------
    // Find existing real item by MBID — if found, we reuse it instead of
    // making a stub, which is how "already on server" detection happens for free.
    // -----------------------------------------------------------------
    public BaseItem? FindByMbid(string providerKey, string mbid)
    {
        if (string.IsNullOrWhiteSpace(mbid)) return null;
        var query = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { { providerKey, mbid } },
            Limit = 1,
        };
        var hit = _library.GetItemList(query).FirstOrDefault();
        return hit;
    }

    // -----------------------------------------------------------------
    // Materialize a release-group (album) stub. Used by both the search
    // filter and the album-detail API. Idempotent on MBID.
    // -----------------------------------------------------------------
    public MusicAlbum EnsureAlbumStub(MbReleaseGroup rg, CancellationToken ct)
    {
        if (FindByMbid(ProviderIds.MusicBrainzReleaseGroup, rg.Id) is MusicAlbum existing)
            return existing;

        var stubId = StubGuid("rg", rg.Id);
        if (_library.GetItemById(stubId) is MusicAlbum already) return already;

        var album = new MusicAlbum
        {
            Id = stubId,
            Name = rg.Title ?? "(unknown album)",
            ProductionYear = ParseYear(rg.FirstReleaseDate),
            PremiereDate = ParseDate(rg.FirstReleaseDate),
            Artists = rg.PrimaryArtistName is { Length: > 0 } an ? new[] { an } : Array.Empty<string>(),
            AlbumArtists = rg.PrimaryArtistName is { Length: > 0 } aa ? new[] { aa } : Array.Empty<string>(),
            DateCreated = DateTime.UtcNow,
            Path = string.Empty,
            IsVirtualItem = false,
        };
        album.SetProviderId(ProviderIds.MusicBrainzReleaseGroup, rg.Id);
        if (rg.PrimaryArtistId is { Length: > 0 } artistMbid)
            album.SetProviderId(ProviderIds.MusicBrainzArtist, artistMbid);
        album.MarkAsStub();

        var coverUrl = _caa.FrontCoverUrlForReleaseGroup(rg.Id);
        if (coverUrl is not null)
            album.ImageInfos = new[] { new ItemImageInfo { Path = coverUrl, Type = ImageType.Primary } };

        // Album parent — prefer artist stub if we have one, else music library.
        BaseItem? parent = rg.PrimaryArtistId is { Length: > 0 } artistMbid2
            ? FindByMbid(ProviderIds.MusicBrainzArtist, artistMbid2)
            : null;
        parent ??= GetMusicLibraryFolder();
        RegisterStub(album, parent, ct);
        _ensured[stubId.ToString("N")] = 1;
        return album;
    }

    // -----------------------------------------------------------------
    // Materialize a recording (track) stub. Idempotent on MBID.
    // -----------------------------------------------------------------
    public Audio EnsureTrackStub(MbRecording rec, MusicAlbum? parentAlbum, CancellationToken ct)
    {
        if (FindByMbid(ProviderIds.MusicBrainzRecording, rec.Id) is Audio existing)
            return existing;

        var stubId = StubGuid("rec", rec.Id);
        if (_library.GetItemById(stubId) is Audio already) return already;

        var track = new Audio
        {
            Id = stubId,
            Name = rec.Title ?? "(unknown track)",
            Album = parentAlbum?.Name ?? rec.Releases?.FirstOrDefault()?.Title,
            Artists = rec.PrimaryArtistName is { Length: > 0 } an ? new[] { an } : Array.Empty<string>(),
            AlbumArtists = rec.PrimaryArtistName is { Length: > 0 } aa ? new[] { aa } : Array.Empty<string>(),
            RunTimeTicks = rec.Duration?.Ticks,
            DateCreated = DateTime.UtcNow,
            Path = string.Empty,
            IsVirtualItem = false,
        };
        if (parentAlbum is not null) track.SetParent(parentAlbum);

        track.SetProviderId(ProviderIds.MusicBrainzRecording, rec.Id);
        if (rec.PrimaryArtistId is { Length: > 0 } artistMbid)
            track.SetProviderId(ProviderIds.MusicBrainzArtist, artistMbid);
        track.MarkAsStub();

        BaseItem? parent = parentAlbum ?? (BaseItem?)GetMusicLibraryFolder();
        RegisterStub(track, parent, ct);
        _ensured[stubId.ToString("N")] = 1;
        return track;
    }

    // -----------------------------------------------------------------
    // Materialize an artist stub. Idempotent on MBID.
    // -----------------------------------------------------------------
    public MusicArtist EnsureArtistStub(MbArtist a, CancellationToken ct)
    {
        if (FindByMbid(ProviderIds.MusicBrainzArtist, a.Id) is MusicArtist existing)
            return existing;

        var stubId = StubGuid("artist", a.Id);
        if (_library.GetItemById(stubId) is MusicArtist already) return already;

        var artist = new MusicArtist
        {
            Id = stubId,
            Name = a.Name ?? "(unknown artist)",
            DateCreated = DateTime.UtcNow,
            Path = string.Empty,
            IsVirtualItem = false,
        };
        artist.SetProviderId(ProviderIds.MusicBrainzArtist, a.Id);
        artist.MarkAsStub();

        RegisterStub(artist, GetMusicLibraryFolder(), ct);
        _ensured[stubId.ToString("N")] = 1;
        return artist;
    }

    // -----------------------------------------------------------------
    // Deezer-sourced stub creators. Same shape as the MB ones but they
    // store a DeezerXxx provider ID and use Deezer image URLs directly.
    // -----------------------------------------------------------------
    public MusicArtist EnsureArtistStubFromDeezer(DeezerClient.DzArtist a, CancellationToken ct)
    {
        if (FindByMbid(ProviderIds.DeezerArtist, a.Id.ToString()) is MusicArtist existing) return existing;

        var stubId = StubGuid("dz-artist", a.Id.ToString());
        if (_library.GetItemById(stubId) is MusicArtist already) return already;

        var artist = new MusicArtist
        {
            Id = stubId,
            Name = a.Name ?? "(unknown artist)",
            DateCreated = DateTime.UtcNow,
            Path = string.Empty,
            IsVirtualItem = false,
        };
        artist.SetProviderId(ProviderIds.DeezerArtist, a.Id.ToString());
        artist.MarkAsStub();

        var image = a.PictureXl ?? a.PictureBig;
        if (!string.IsNullOrEmpty(image))
            artist.ImageInfos = new[] { new ItemImageInfo { Path = image, Type = ImageType.Primary } };

        RegisterStub(artist, GetMusicLibraryFolder(), ct);
        return artist;
    }

    public MusicAlbum EnsureAlbumStubFromDeezer(DeezerClient.DzAlbum dz, CancellationToken ct)
    {
        if (FindByMbid(ProviderIds.DeezerAlbum, dz.Id.ToString()) is MusicAlbum existing) return existing;

        var stubId = StubGuid("dz-album", dz.Id.ToString());
        if (_library.GetItemById(stubId) is MusicAlbum already) return already;

        var artistName = dz.Artist?.Name;
        var album = new MusicAlbum
        {
            Id = stubId,
            Name = dz.Title ?? "(unknown album)",
            ProductionYear = ParseYear(dz.ReleaseDate),
            PremiereDate = ParseDate(dz.ReleaseDate),
            Artists = artistName is { Length: > 0 } ? new[] { artistName } : Array.Empty<string>(),
            AlbumArtists = artistName is { Length: > 0 } ? new[] { artistName } : Array.Empty<string>(),
            DateCreated = DateTime.UtcNow,
            Path = string.Empty,
            IsVirtualItem = false,
        };
        album.SetProviderId(ProviderIds.DeezerAlbum, dz.Id.ToString());
        if (dz.Artist is not null)
            album.SetProviderId(ProviderIds.DeezerArtist, dz.Artist.Id.ToString());
        album.MarkAsStub();

        var cover = dz.CoverXl ?? dz.CoverBig;
        if (!string.IsNullOrEmpty(cover))
            album.ImageInfos = new[] { new ItemImageInfo { Path = cover, Type = ImageType.Primary } };

        BaseItem? parent = dz.Artist is not null
            ? FindByMbid(ProviderIds.DeezerArtist, dz.Artist.Id.ToString())
            : null;
        parent ??= GetMusicLibraryFolder();
        RegisterStub(album, parent, ct);
        return album;
    }

    public Audio EnsureTrackStubFromDeezer(DeezerClient.DzTrack t, MusicAlbum? parentAlbum, CancellationToken ct)
    {
        if (FindByMbid(ProviderIds.DeezerTrack, t.Id.ToString()) is Audio existing) return existing;

        var stubId = StubGuid("dz-track", t.Id.ToString());
        if (_library.GetItemById(stubId) is Audio already) return already;

        var artistName = t.Artist?.Name;
        var track = new Audio
        {
            Id = stubId,
            Name = t.Title ?? "(unknown track)",
            Album = parentAlbum?.Name ?? t.Album?.Title,
            Artists = artistName is { Length: > 0 } ? new[] { artistName } : Array.Empty<string>(),
            AlbumArtists = artistName is { Length: > 0 } ? new[] { artistName } : Array.Empty<string>(),
            RunTimeTicks = t.Duration.HasValue ? TimeSpan.FromSeconds(t.Duration.Value).Ticks : null,
            IndexNumber = t.TrackPosition,
            ParentIndexNumber = t.DiskNumber,
            DateCreated = DateTime.UtcNow,
            Path = string.Empty,
            IsVirtualItem = false,
        };
        if (parentAlbum is not null) track.SetParent(parentAlbum);

        track.SetProviderId(ProviderIds.DeezerTrack, t.Id.ToString());
        if (t.Artist is not null)
            track.SetProviderId(ProviderIds.DeezerArtist, t.Artist.Id.ToString());
        if (t.Album is not null)
            track.SetProviderId(ProviderIds.DeezerAlbum, t.Album.Id.ToString());
        track.MarkAsStub();

        BaseItem? parent = parentAlbum
            ?? (t.Album is not null ? FindByMbid(ProviderIds.DeezerAlbum, t.Album.Id.ToString()) : null)
            ?? (BaseItem?)GetMusicLibraryFolder();
        RegisterStub(track, parent, ct);
        return track;
    }

    // -----------------------------------------------------------------
    // Garbage collection: remove unrequested stubs older than the
    // configured TTL. Called by the scheduled task.
    // -----------------------------------------------------------------
    public int GcOldStubs()
    {
        var cfg = Plugin.Instance!.Configuration;
        var cutoff = DateTime.UtcNow.AddHours(-cfg.StubGcMaxAgeHours);
        var query = new InternalItemsQuery { Tags = new[] { Tags.Stub } };
        var stubs = _library.GetItemList(query);
        var removed = 0;
        foreach (var s in stubs)
        {
            if (s.DateCreated > cutoff) continue;
            if (s.IsAlreadyRequested()) continue;
            try { _library.DeleteItem(s, new DeleteOptions { DeleteFileLocation = false }); removed++; }
            catch (Exception ex) { _log.LogWarning(ex, "Failed deleting stub {Id}", s.Id); }
        }
        return removed;
    }

    private static int? ParseYear(string? date)
    {
        if (date is null || date.Length < 4) return null;
        return int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
    }

    private static DateTime? ParseDate(string? date)
    {
        if (string.IsNullOrEmpty(date)) return null;
        // MB returns YYYY, YYYY-MM, or YYYY-MM-DD.
        foreach (var fmt in new[] { "yyyy-MM-dd", "yyyy-MM", "yyyy" })
            if (DateTime.TryParseExact(date, fmt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
                return d.ToUniversalTime();
        return null;
    }
}
