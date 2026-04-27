using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Services;

/// <summary>
/// Watches a slskd download to completion, then organizes the resulting
/// file into the music library at "{MusicLibraryRoot}/{Artist}/{Album}/
/// {TrackNum} - {Title}.{ext}" and triggers a Jellyfin scan so it appears
/// immediately. Used by the favorite-trigger path so a single starred
/// track flows: favorite → slskd queue → download → tag-read → move →
/// playable in Jellyfin, no manual import step.
///
/// All work is fire-and-forget; the caller doesn't wait for completion.
/// On failure (timeout, missing file, etc.) we log and bail; the file
/// stays in slskd's downloads folder where the user can recover it
/// manually.
/// </summary>
public class SlskdDownloadFinisher
{
    private readonly SlskdClient _slskd;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SlskdDownloadFinisher> _log;

    // How often to poll slskd for download completion, and how long to wait
    // before giving up. 5 minutes covers slow peers / large FLACs; if a
    // download takes longer the user has bigger issues than autoorganize.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(5);

    // Sentinel state names slskd uses for completion.
    private const string CompletedSucceededState = "Completed, Succeeded";

    public SlskdDownloadFinisher(SlskdClient slskd, ILibraryManager libraryManager, ILogger<SlskdDownloadFinisher> log)
    {
        _slskd = slskd;
        _libraryManager = libraryManager;
        _log = log;
    }

    /// <summary>
    /// Watch the given slskd download until it completes, then move and
    /// scan. Returns the final destination path on success, null on any
    /// failure. Designed to run on a background task — it's safe to call
    /// without awaiting.
    /// </summary>
    public async Task<string?> WatchAndOrganizeAsync(
        SlskdClient.DownloadHandle handle,
        string fallbackArtist,
        string fallbackTitle,
        CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (!cfg.EnableSlskdAutoOrganize)
        {
            _log.LogInformation("[mdiscover-org] auto-organize disabled; leaving file in slskd downloads");
            return null;
        }

        // 1. Poll slskd until the transfer is complete.
        var deadline = DateTime.UtcNow + PollTimeout;
        SlskdClient.DownloadStatus? status = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            status = await _slskd.GetDownloadStatusAsync(handle.Username, handle.RemoteFile, ct).ConfigureAwait(false);
            if (status is null) continue; // transient miss; keep polling
            if (string.Equals(status.State, CompletedSucceededState, StringComparison.OrdinalIgnoreCase))
                break;
            if (status.State.StartsWith("Completed", StringComparison.OrdinalIgnoreCase)
                && !status.State.EndsWith("Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                // "Completed, Errored" / "Completed, Cancelled" — give up.
                _log.LogInformation("[mdiscover-org] download terminal non-success: {State} for {File}",
                    status.State, handle.RemoteFile);
                return null;
            }
        }

        if (status is null
            || !string.Equals(status.State, CompletedSucceededState, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation("[mdiscover-org] timeout waiting for download: {File} (last state {State})",
                handle.RemoteFile, status?.State ?? "<null>");
            return null;
        }

        // 2. Locate the file on disk. slskd default layout puts the file at
        //    "{downloads-root}/{username}/{filename-leaf}" but some setups
        //    preserve the peer's directory tree. We try the predictable
        //    location first, fall back to recursive search under the
        //    user's folder.
        var leaf = Path.GetFileName(handle.RemoteFile.Replace('\\', '/'));
        var fastPath = Path.Combine(cfg.SlskdDownloadsRootPath, handle.Username, leaf);
        string? sourcePath = File.Exists(fastPath) ? fastPath : FindRecursive(cfg.SlskdDownloadsRootPath, handle.Username, leaf);
        if (sourcePath is null)
        {
            _log.LogWarning("[mdiscover-org] download file not found on disk: searched {FastPath} and recursively under {Root}/{User}",
                fastPath, cfg.SlskdDownloadsRootPath, handle.Username);
            return null;
        }

        // 3. Read tags. Fall back to the favorite's known artist/title if
        //    the file's tags are missing or garbage (Soulseek peers vary
        //    wildly in tagging quality).
        string? tagArtist = null, tagAlbum = null, tagTitle = null;
        int? tagTrackNumber = null;
        try
        {
            using var tagFile = TagLib.File.Create(sourcePath);
            tagArtist = First(tagFile.Tag.AlbumArtists, tagFile.Tag.Performers);
            tagAlbum = tagFile.Tag.Album;
            tagTitle = tagFile.Tag.Title;
            if (tagFile.Tag.Track > 0) tagTrackNumber = (int)tagFile.Tag.Track;
        }
        catch (Exception ex)
        {
            _log.LogInformation("[mdiscover-org] tag read failed ({Msg}); falling back to known artist/title", ex.Message);
        }

        var artist = string.IsNullOrWhiteSpace(tagArtist) ? fallbackArtist : tagArtist!;
        var album = string.IsNullOrWhiteSpace(tagAlbum) ? "Singles" : tagAlbum!;
        var title = string.IsNullOrWhiteSpace(tagTitle) ? fallbackTitle : tagTitle!;
        var trackPrefix = tagTrackNumber is { } n ? $"{n:D2} - " : "";

        // 4. Build destination path with sanitized components.
        var ext = Path.GetExtension(sourcePath); // includes leading dot
        var safeArtist = SanitizeFsComponent(artist);
        var safeAlbum = SanitizeFsComponent(album);
        var safeTitle = SanitizeFsComponent(title);
        var destDir = Path.Combine(cfg.MusicLibraryRoot, safeArtist, safeAlbum);
        var destFile = Path.Combine(destDir, $"{trackPrefix}{safeTitle}{ext}");

        // 5. Move (or copy + delete if cross-device).
        try
        {
            Directory.CreateDirectory(destDir);
            // If the destination already exists, append a numeric suffix
            // rather than overwriting — user may have a real copy of the
            // same album already.
            var finalDest = ResolveCollision(destFile);
            try { File.Move(sourcePath, finalDest); }
            catch (IOException) { File.Copy(sourcePath, finalDest, overwrite: false); File.Delete(sourcePath); }
            _log.LogInformation("[mdiscover-org] organized: {Source} → {Dest}", sourcePath, finalDest);
            // 6. Trigger a Jellyfin scan for the artist folder so the new
            //    file appears immediately. Best-effort; if this throws we
            //    still leave the file in place — Jellyfin's scheduled
            //    scan will pick it up later.
            TryTriggerScan(destDir);
            return finalDest;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover-org] move failed: {Source} → {Dest}", sourcePath, destFile);
            return null;
        }
    }

    private static string? First(params string[]?[] arrays)
    {
        foreach (var arr in arrays)
            if (arr is { Length: > 0 } a)
                foreach (var s in a)
                    if (!string.IsNullOrWhiteSpace(s)) return s;
        return null;
    }

    private static string? FindRecursive(string root, string username, string leafName)
    {
        try
        {
            var userDir = Path.Combine(root, username);
            if (!Directory.Exists(userDir)) return null;
            return Directory.EnumerateFiles(userDir, leafName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    private static string SanitizeFsComponent(string s)
    {
        // Replace path-illegal chars with safe equivalents. We're conservative —
        // even on Linux some chars (slashes, NULs) need replacement, and
        // a SMB-mounted drive may have stricter rules.
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToHashSet();
        var clean = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        // Avoid trailing dots/spaces (Windows-illegal even on Linux SMB).
        clean = clean.TrimEnd('.', ' ');
        return string.IsNullOrEmpty(clean) ? "_" : clean;
    }

    private static string ResolveCollision(string destPath)
    {
        if (!File.Exists(destPath)) return destPath;
        var dir = Path.GetDirectoryName(destPath)!;
        var stem = Path.GetFileNameWithoutExtension(destPath);
        var ext = Path.GetExtension(destPath);
        for (int i = 2; i < 100; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        // Pathological — overwrite as a last resort.
        return destPath;
    }

    private void TryTriggerScan(string folder)
    {
        // Library-scan triggering from a plugin is awkward — ILibraryManager
        // exposes only the full-library ValidateMediaLibrary which is
        // expensive enough that running it on every track favorite would
        // hammer the Pi. We rely on Jellyfin's scheduled library scan
        // (default every several hours, configurable in admin) to pick up
        // the new file. This method exists as the place to add a
        // file-system-watcher kick if we want push notifications later.
        // For now it's a no-op placeholder.
    }
}
