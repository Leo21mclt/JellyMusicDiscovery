using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Services;

/// <summary>
/// Maps the synthetic Guid we hand out in search responses back to the Deezer
/// IDs and human-readable artist/title we'll need at request/stream time.
/// Singleton in-memory store with disk-backed persistence so the cache (and
/// therefore playlists, recently-played, and lyrics references) survive
/// Jellyfin restarts.
///
/// Persistence model:
///   - On Put: mark dirty, schedule a debounced flush 2s later.
///   - On flush: serialize the full in-memory dictionary to JSON, write atomically.
///   - On startup: read JSON if it exists, hydrate the dictionary.
///
/// File location: &lt;plugin-data-dir&gt;/discovery-cache.json
/// (e.g. /config/data/plugins/MusicDiscovery_0.1.0.0/discovery-cache.json on the Pi)
/// </summary>
public class DiscoveryItemCache
{
    public class Entry
    {
        public required string Kind { get; init; }            // "artist" | "album" | "track" | "video"
        public string? Artist { get; init; }
        public string? AlbumName { get; init; }
        public string? TrackTitle { get; init; }
        public long? DeezerArtistId { get; init; }
        public long? DeezerAlbumId { get; init; }
        public long? DeezerTrackId { get; init; }
        public string? PrimaryImageUrl { get; init; }
        public int? DurationSeconds { get; init; }
        public DateTime AddedUtc { get; init; } = DateTime.UtcNow;

        // ---------------------------------------------------------------
        // Radio-station-specific fields. Populated when this entry is a
        // synthetic Audio for an internet radio station; the Audio file
        // resolver redirects to RadioStreamUrl instead of going through
        // ytmusic-stream-server. Null for everything else.
        // ---------------------------------------------------------------
        public string? RadioStreamUrl { get; init; }
        public string? RadioStationUuid { get; init; }

        // ---------------------------------------------------------------
        // Music-video-specific fields. Populated when Kind=="video" — the
        // synthetic MusicVideo for a YouTube music video. VideoId is the
        // YouTube videoId; the playback resolver maps that to a yt-dlp
        // direct-MP4 URL via the Python /video/by-id/{videoId} endpoint.
        // ---------------------------------------------------------------
        public string? VideoId { get; init; }
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly ILogger<DiscoveryItemCache> _log;
    private readonly string? _persistFile;
    private readonly Timer? _saveTimer;
    private readonly object _saveLock = new();
    private int _dirty;
    // Debounce window. Tap-typing in the search bar can fire dozens of /Items
    // queries per second; without this we'd thrash disk.
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);

    public DiscoveryItemCache(ILogger<DiscoveryItemCache> log)
    {
        _log = log;
        try
        {
            var dir = Plugin.Instance?.DataFolderPath;
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                _persistFile = Path.Combine(dir, "discovery-cache.json");
                Load();
                _saveTimer = new Timer(_ => SaveIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
            }
            else
            {
                _log.LogWarning("[mdiscover-cache] no plugin data folder available; running in-memory only (won't survive restart)");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-cache] init failed; running in-memory only");
        }
    }

    public int Count => _entries.Count;

    public void Put(Guid id, Entry e)
    {
        _entries[id] = e;
        ScheduleSave();
    }

    public bool TryGet(Guid id, out Entry? entry) => _entries.TryGetValue(id, out entry!);

    /// <summary>Snapshot of all entries — useful for startup reregistration.</summary>
    public IReadOnlyDictionary<Guid, Entry> Snapshot() =>
        _entries.ToDictionary(kv => kv.Key, kv => kv.Value);

    private void Load()
    {
        if (_persistFile is null || !File.Exists(_persistFile)) return;
        try
        {
            var json = File.ReadAllText(_persistFile);
            if (string.IsNullOrWhiteSpace(json)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<Guid, Entry>>(json, JsonOpts);
            if (loaded is null) return;
            foreach (var kv in loaded)
            {
                _entries[kv.Key] = kv.Value;
            }
            _log.LogInformation("[mdiscover-cache] loaded {N} entries from disk", loaded.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-cache] load failed; starting fresh (file may be corrupt)");
        }
    }

    private void ScheduleSave()
    {
        if (_saveTimer is null) return;
        Interlocked.Exchange(ref _dirty, 1);
        // Reset the debounce timer — last write wins. We coalesce bursts of
        // writes (typical during search-result augmentation) into one save.
        _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
    }

    private void SaveIfDirty()
    {
        if (_persistFile is null) return;
        lock (_saveLock)
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
            try
            {
                var snapshot = _entries.ToDictionary(kv => kv.Key, kv => kv.Value);
                var tmp = _persistFile + ".tmp";
                using (var fs = File.Create(tmp))
                {
                    JsonSerializer.Serialize(fs, snapshot, JsonOpts);
                }
                // Atomic rename — no partial writes left around if the process dies mid-flush.
                File.Move(tmp, _persistFile, overwrite: true);
                _log.LogDebug("[mdiscover-cache] saved {N} entries", snapshot.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[mdiscover-cache] save failed");
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };
}
