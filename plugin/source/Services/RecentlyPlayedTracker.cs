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
/// In-memory record of "the stub tracks each user just played", used to
/// inject our streamed plays into Jellyfin's Recently Played UI rows.
/// Disk-persisted with the same debounced-flush pattern as DiscoveryItemCache,
/// so recently-played history survives Jellyfin restarts.
///
/// File: &lt;plugin-data-dir&gt;/recently-played.json
/// </summary>
public class RecentlyPlayedTracker
{
    public class Play
    {
        public Guid TrackId { get; set; }
        public Guid? AlbumId { get; set; }
        public DateTime PlayedAt { get; set; }
    }

    // Per-user ring buffer. We keep the last 50 plays per user; UI rows
    // typically show 10-20 so this is plenty.
    private const int MaxPerUser = 50;
    private readonly ConcurrentDictionary<Guid, List<Play>> _byUser = new();

    private readonly ILogger<RecentlyPlayedTracker> _log;
    private readonly string? _persistFile;
    private readonly Timer? _saveTimer;
    private readonly object _saveLock = new();
    private int _dirty;
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(2);

    public RecentlyPlayedTracker(ILogger<RecentlyPlayedTracker> log)
    {
        _log = log;
        try
        {
            var dir = Plugin.Instance?.DataFolderPath;
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                _persistFile = Path.Combine(dir, "recently-played.json");
                Load();
                _saveTimer = new Timer(_ => SaveIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-recents] init failed; running in-memory only");
        }
    }

    public void RecordPlay(Guid userId, Guid trackId, Guid? albumId)
    {
        var list = _byUser.GetOrAdd(userId, _ => new List<Play>());
        lock (list)
        {
            list.RemoveAll(p => p.TrackId == trackId);
            list.Insert(0, new Play { TrackId = trackId, AlbumId = albumId, PlayedAt = DateTime.UtcNow });
            if (list.Count > MaxPerUser) list.RemoveRange(MaxPerUser, list.Count - MaxPerUser);
        }
        ScheduleSave();
    }

    public IReadOnlyList<(Guid TrackId, Guid? AlbumId, DateTime PlayedAt)> GetRecent(Guid userId, int limit)
    {
        if (!_byUser.TryGetValue(userId, out var list)) return Array.Empty<(Guid, Guid?, DateTime)>();
        lock (list)
        {
            return list.Take(limit).Select(p => (p.TrackId, p.AlbumId, p.PlayedAt)).ToArray();
        }
    }

    private void Load()
    {
        if (_persistFile is null || !File.Exists(_persistFile)) return;
        try
        {
            var json = File.ReadAllText(_persistFile);
            if (string.IsNullOrWhiteSpace(json)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<Guid, List<Play>>>(json, JsonOpts);
            if (loaded is null) return;
            foreach (var kv in loaded) _byUser[kv.Key] = kv.Value;
            _log.LogInformation("[mdiscover-recents] loaded {N} users' recent-plays from disk", loaded.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-recents] load failed; starting fresh");
        }
    }

    private void ScheduleSave()
    {
        if (_saveTimer is null) return;
        Interlocked.Exchange(ref _dirty, 1);
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
                var snapshot = _byUser.ToDictionary(
                    kv => kv.Key,
                    kv =>
                    {
                        lock (kv.Value) return kv.Value.ToList();
                    });
                var tmp = _persistFile + ".tmp";
                using (var fs = File.Create(tmp))
                {
                    JsonSerializer.Serialize(fs, snapshot, JsonOpts);
                }
                File.Move(tmp, _persistFile, overwrite: true);
                _log.LogDebug("[mdiscover-recents] saved {N} users to disk", snapshot.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[mdiscover-recents] save failed");
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };
}
