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
/// Per-playlist list of stub-track GUIDs that belong to the playlist.
/// Maintained as a separate persistence layer because Jellyfin's normal
/// playlist storage uses path-based resolution (`PlaylistItems` →
/// `<Path>` entries → `_libraryManager.FindByPath(...)`), and our
/// synthetic items only live in the in-memory ItemRegistry — they'd
/// never be found by path on startup.
///
/// File: &lt;plugin-data-dir&gt;/playlist-stubs.json
/// Shape: { "&lt;playlistGuid&gt;": ["&lt;stubGuid&gt;", "&lt;stubGuid&gt;", ...], ... }
/// </summary>
public class PlaylistStubLinks
{
    private readonly ConcurrentDictionary<Guid, List<Guid>> _byPlaylist = new();
    private readonly ILogger<PlaylistStubLinks> _log;
    private readonly string? _persistFile;
    private readonly Timer? _saveTimer;
    private readonly object _saveLock = new();
    private int _dirty;
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    public PlaylistStubLinks(ILogger<PlaylistStubLinks> log)
    {
        _log = log;
        try
        {
            var dir = Plugin.Instance?.DataFolderPath;
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                _persistFile = Path.Combine(dir, "playlist-stubs.json");
                Load();
                _saveTimer = new Timer(_ => SaveIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-playlist] init failed; running in-memory only");
        }
    }

    /// <summary>Append a stub guid to a playlist (no-op if already linked).</summary>
    public void AddLink(Guid playlistId, Guid stubId)
    {
        var list = _byPlaylist.GetOrAdd(playlistId, _ => new List<Guid>());
        lock (list)
        {
            if (!list.Contains(stubId)) list.Add(stubId);
        }
        ScheduleSave();
    }

    /// <summary>Remove a stub guid from a playlist. Returns true if the link existed.</summary>
    public bool RemoveLink(Guid playlistId, Guid stubId)
    {
        if (!_byPlaylist.TryGetValue(playlistId, out var list)) return false;
        bool removed;
        lock (list)
        {
            removed = list.Remove(stubId);
            if (list.Count == 0) _byPlaylist.TryRemove(playlistId, out _);
        }
        if (removed) ScheduleSave();
        return removed;
    }

    /// <summary>Drop the entire playlist's stub linkage (e.g. when playlist deleted).</summary>
    public bool RemovePlaylist(Guid playlistId)
    {
        if (_byPlaylist.TryRemove(playlistId, out _))
        {
            ScheduleSave();
            return true;
        }
        return false;
    }

    /// <summary>Stub-track guids linked to a playlist, in insertion order.</summary>
    public IReadOnlyList<Guid> GetLinks(Guid playlistId)
    {
        if (!_byPlaylist.TryGetValue(playlistId, out var list)) return Array.Empty<Guid>();
        lock (list) return list.ToArray();
    }

    private void Load()
    {
        if (_persistFile is null || !File.Exists(_persistFile)) return;
        try
        {
            var json = File.ReadAllText(_persistFile);
            if (string.IsNullOrWhiteSpace(json)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<Guid, List<Guid>>>(json, JsonOpts);
            if (loaded is null) return;
            int totalLinks = 0;
            foreach (var kv in loaded)
            {
                _byPlaylist[kv.Key] = kv.Value;
                totalLinks += kv.Value.Count;
            }
            _log.LogInformation("[mdiscover-playlist] loaded {Pl} playlists / {Tot} stub links from disk",
                loaded.Count, totalLinks);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-playlist] load failed; starting fresh");
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
                var snapshot = _byPlaylist.ToDictionary(
                    kv => kv.Key,
                    kv => { lock (kv.Value) return kv.Value.ToList(); });
                var tmp = _persistFile + ".tmp";
                using (var fs = File.Create(tmp))
                {
                    JsonSerializer.Serialize(fs, snapshot, JsonOpts);
                }
                File.Move(tmp, _persistFile, overwrite: true);
                _log.LogDebug("[mdiscover-playlist] saved {N} playlists to disk", snapshot.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[mdiscover-playlist] save failed");
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };
}
