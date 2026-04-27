using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace JellyMusicDiscovery;

public static class Tags
{
    public const string Stub = "mdiscover-stub";
    public const string Requested = "mdiscover-requested";
}

public static class ProviderIds
{
    public const string MusicBrainzArtist = "MusicBrainzArtist";
    public const string MusicBrainzAlbum = "MusicBrainzAlbum";
    public const string MusicBrainzReleaseGroup = "MusicBrainzReleaseGroup";
    public const string MusicBrainzTrack = "MusicBrainzTrack";
    public const string MusicBrainzRecording = "MusicBrainzRecording";

    // Deezer IDs are non-standard but Jellyfin's BaseItem.ProviderIds is a free
    // string-keyed dictionary so we can store anything we want here.
    public const string DeezerArtist = "DeezerArtist";
    public const string DeezerAlbum = "DeezerAlbum";
    public const string DeezerTrack = "DeezerTrack";
}

public static class BaseItemExtensions
{
    public static bool IsDiscoveryStub(this BaseItem item) =>
        item?.Tags is { Length: > 0 } tags && tags.Contains(Tags.Stub);

    public static bool IsAlreadyRequested(this BaseItem item) =>
        item?.Tags is { Length: > 0 } tags && tags.Contains(Tags.Requested);

    public static void MarkAsStub(this BaseItem item)
    {
        var set = new HashSet<string>(item.Tags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase) { Tags.Stub };
        item.Tags = set.ToArray();
        // Don't touch LockedFields — Jellyfin's BaseItemMetadataFields table has a
        // composite unique constraint that bites when we try to lock fields here.
    }

    public static void MarkAsRequested(this BaseItem item)
    {
        var set = new HashSet<string>(item.Tags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase) { Tags.Stub, Tags.Requested };
        item.Tags = set.ToArray();
    }
}
