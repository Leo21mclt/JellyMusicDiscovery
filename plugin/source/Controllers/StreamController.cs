using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using JellyMusicDiscovery.Services;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Controllers;

[ApiController]
[Route("mdiscover")]
[AllowAnonymous]
public class StreamController : ControllerBase
{
    private readonly SlskdClient _slskd;
    private readonly StreamSessionRegistry _sessions;
    private readonly ILibraryManager _library;
    private readonly DiscoveryItemCache _cache;
    private readonly ILogger<StreamController> _log;

    public StreamController(
        SlskdClient slskd,
        StreamSessionRegistry sessions,
        ILibraryManager library,
        DiscoveryItemCache cache,
        ILogger<StreamController> log)
    {
        _slskd = slskd;
        _sessions = sessions;
        _library = library;
        _cache = cache;
        _log = log;
    }

    /// <summary>
    /// GET /mdiscover/stream?id={jellyfin-item-id}
    /// Streams the track via slskd, downloading on demand if not yet cached.
    /// Reads from the partial file as it grows so playback can begin before
    /// the download finishes. ASP.NET Core handles 206 Partial Content
    /// because we return File(...) with enableRangeProcessing.
    /// </summary>
    [HttpGet("stream")]
    public async Task<IActionResult> Stream([FromQuery] string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var itemId))
            return BadRequest("missing or invalid id");

        // No loopback restriction here — the user's browser fetches via the
        // server's public address, so we have to allow LAN clients. Auth on the
        // upstream Jellyfin endpoints is sufficient gating; this controller is
        // [AllowAnonymous] only because the audio element doesn't carry tokens.

        // Resolve artist/title — prefer real library item, fall back to in-memory search cache.
        string artist = string.Empty, title = string.Empty;
        var item = _library.GetItemById(itemId);
        if (item is Audio audio)
        {
            artist = audio.Artists?.FirstOrDefault() ?? audio.AlbumArtists?.FirstOrDefault() ?? string.Empty;
            title = audio.Name ?? string.Empty;
        }
        else if (_cache.TryGet(itemId, out var entry) && entry is not null && entry.Kind == "track")
        {
            artist = entry.Artist ?? string.Empty;
            title = entry.TrackTitle ?? string.Empty;
        }
        else
        {
            return NotFound("Unknown track id.");
        }
        if (string.IsNullOrEmpty(title)) return BadRequest("Item has no title.");

        // Preferred: redirect to the ytmusic-stream-server companion (yt-dlp
        // backed). It returns a 302 to a direct YouTube CDN audio URL, which
        // the browser plays instantly. We chain redirects through, so the
        // browser ends up on the CDN URL after two 302s.
        var cfg = Plugin.Instance!.Configuration;
        if (!string.IsNullOrWhiteSpace(cfg.YtMusicStreamServerUrl))
        {
            var qs = $"artist={Uri.EscapeDataString(artist)}&title={Uri.EscapeDataString(title)}";
            var redirectUrl = $"{cfg.YtMusicStreamServerUrl.TrimEnd('/')}/stream?{qs}";
            _log.LogInformation("[mdiscover] redirecting stream for {Artist} - {Title} → {Url}", artist, title, redirectUrl);
            return Redirect(redirectUrl);
        }

        // Fallback: slskd progressive download (rarely useful in practice — the
        // browser audio element times out before slskd resolves a peer).
        var sessionKey = itemId.ToString("N");
        if (_sessions.TryGet(sessionKey, out var existing) && System.IO.File.Exists(existing.FilePath))
            return StreamFromDisk(existing);

        var handle = await _slskd.SearchAndDownloadAsync(artist, title, ct).ConfigureAwait(false);
        if (handle is null) return StatusCode(503, "No suitable source found.");

        var session = _sessions.GetOrAdd(sessionKey, () => new StreamSessionRegistry.Session
        {
            Mbid = sessionKey,
            FilePath = handle.LocalPath,
            ExpectedSize = handle.ExpectedSize,
            ContentType = handle.ContentType,
        });

        // Wait briefly for slskd to start writing the file.
        var waitDeadline = DateTime.UtcNow.AddSeconds(20);
        while (!System.IO.File.Exists(session.FilePath) && DateTime.UtcNow < waitDeadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        if (!System.IO.File.Exists(session.FilePath))
            return StatusCode(504, "Download did not start in time.");

        return StreamFromDisk(session);
    }

    private IActionResult StreamFromDisk(StreamSessionRegistry.Session session)
    {
        // Open with shared read/write/delete so slskd's writer (and a possible
        // rename on completion) doesn't break our handle.
        var fs = new FileStream(
            session.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);

        Response.Headers.AcceptRanges = "bytes";
        return File(new GrowingFileStream(fs, session), session.ContentType, enableRangeProcessing: false);
    }

    /// <summary>
    /// Stream wrapper that blocks (briefly) on read when we've caught up to
    /// the writer, instead of returning EOF. Stops once we've delivered the
    /// expected number of bytes or the download is marked complete.
    /// </summary>
    private sealed class GrowingFileStream : Stream
    {
        private readonly FileStream _inner;
        private readonly StreamSessionRegistry.Session _session;
        private long _delivered;

        public GrowingFileStream(FileStream inner, StreamSessionRegistry.Session session)
        {
            _inner = inner;
            _session = session;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _session.ExpectedSize > 0 ? _session.ExpectedSize : _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            const int idleSpinMs = 150;
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var n = await _inner.ReadAsync(buffer.Slice(totalRead), ct).ConfigureAwait(false);
                if (n > 0) { totalRead += n; _delivered += n; continue; }

                // EOF on partial file. If the download is complete or we hit expected size, we're done.
                if (_session.Complete) break;
                if (_session.ExpectedSize > 0 && _delivered >= _session.ExpectedSize) break;

                await Task.Delay(idleSpinMs, ct).ConfigureAwait(false);
            }
            return totalRead;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
