using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Services;

public class CoverArtArchiveClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CoverArtArchiveClient> _log;

    public CoverArtArchiveClient(IHttpClientFactory httpFactory, ILogger<CoverArtArchiveClient> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>Front cover URL for a release-group MBID, or null if none. Just builds the URL — CAA returns 307s for missing covers, which Jellyfin handles fine if we let it 404 at fetch time.</summary>
    public string? FrontCoverUrlForReleaseGroup(string mbid)
    {
        if (string.IsNullOrWhiteSpace(mbid)) return null;
        var cfg = Plugin.Instance!.Configuration;
        return $"{cfg.CoverArtArchiveBaseUrl.TrimEnd('/')}/release-group/{mbid}/front-500";
    }

    public string? FrontCoverUrlForRelease(string mbid)
    {
        if (string.IsNullOrWhiteSpace(mbid)) return null;
        var cfg = Plugin.Instance!.Configuration;
        return $"{cfg.CoverArtArchiveBaseUrl.TrimEnd('/')}/release/{mbid}/front-500";
    }
}
