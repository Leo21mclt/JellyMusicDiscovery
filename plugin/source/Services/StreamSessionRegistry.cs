using System;
using System.Collections.Concurrent;

namespace JellyMusicDiscovery.Services;

/// <summary>
/// Tracks active slskd downloads triggered by a /mdiscover/stream request.
/// Keyed by recording MBID. Used to dedupe simultaneous stream requests for
/// the same track and to find the partial file path on disk.
/// </summary>
public class StreamSessionRegistry
{
    public class Session
    {
        public required string Mbid { get; init; }
        public required string FilePath { get; set; }
        public long ExpectedSize { get; set; }
        public bool Complete { get; set; }
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
        public string ContentType { get; set; } = "audio/mpeg";
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public Session GetOrAdd(string mbid, Func<Session> factory) =>
        _sessions.GetOrAdd(mbid, _ => factory());

    public bool TryGet(string mbid, out Session session) => _sessions.TryGetValue(mbid, out session!);

    public void MarkComplete(string mbid)
    {
        if (_sessions.TryGetValue(mbid, out var s)) s.Complete = true;
    }

    public void Remove(string mbid) => _sessions.TryRemove(mbid, out _);
}
