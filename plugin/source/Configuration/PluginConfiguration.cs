using MediaBrowser.Model.Plugins;

namespace JellyMusicDiscovery.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool EnableSearchInjection { get; set; } = true;

    public string MusicLibraryRoot { get; set; } = "/music";
    public string DiscoveryDownloadSubdir { get; set; } = ".discovery-downloads";

    /// <summary>"deezer" (default, no auth, fast), "musicbrainz", or "both".</summary>
    public string SearchSource { get; set; } = "deezer";

    public int SearchLimit { get; set; } = 8;

    /// <summary>
    /// Augment search results with hits from Apple's iTunes Search API in
    /// addition to Deezer. Apple's catalog covers contemporary releases
    /// (and more aggressive non-Latin coverage) that Deezer often lags on,
    /// so this surfaces tracks that would otherwise be invisible. Disable
    /// if Apple results turn out to be noisy or duplicate.
    /// </summary>
    public bool EnableItunesSearch { get; set; } = true;

    /// <summary>
    /// Substring blocklist applied to radio station names (case-insensitive).
    /// Any station whose name contains one of these substrings is dropped
    /// from every radio playlist (📻 iHeartRadio, 📻 News & Talk, etc.)
    /// before dedup. Use to filter out specific outlets or topics you don't
    /// want surfacing.
    /// </summary>
    public string[] BlockedRadioStations { get; set; } = new[]
    {
        "fox news",
        "swr3",
        "80s80s",
    };

    // MusicBrainz (still used as a fallback and for canonical-id lookups at request time)
    public string MusicBrainzBaseUrl { get; set; } = "https://musicbrainz.org/ws/2";
    public string MusicBrainzUserAgent { get; set; } = "JellyMusicDiscovery/0.1 ( jellyfin-plugin )";
    public int MusicBrainzCacheTtlSeconds { get; set; } = 900;

    // Cover Art Archive
    public string CoverArtArchiveBaseUrl { get; set; } = "https://coverartarchive.org";

    // Lidarr
    public string LidarrBaseUrl { get; set; } = "http://localhost:8686";
    public string LidarrApiKey { get; set; } = string.Empty;
    public bool LidarrAddMonitored { get; set; } = true;
    public bool LidarrSearchOnAdd { get; set; } = true;

    // ytmusic-stream-server (instant playback via YouTube Music)
    public string YtMusicStreamServerUrl { get; set; } = string.Empty;

    // slskd (still here for fallback / future)
    public string SlskdBaseUrl { get; set; } = "http://localhost:5030";
    public string SlskdApiKey { get; set; } = string.Empty;
    public string[] PreferredFormats { get; set; } = new[] { "flac", "mp3" };
    public int MinBitrateKbps { get; set; } = 192;
    public int SlskdSearchTimeoutSeconds { get; set; } = 12;
    public int SlskdMinPeerSpeedKbps { get; set; } = 100;

    /// <summary>
    /// Path on the JELLYFIN container's filesystem where slskd writes its
    /// downloads. Must be a docker volume mount so Jellyfin can read+move
    /// completed files. The post-download organizer expects to find files
    /// at "{this}/{username}/{filename}" — slskd's default layout.
    /// </summary>
    public string SlskdDownloadsRootPath { get; set; } = "/downloads";

    /// <summary>
    /// Toggle the in-plugin post-download organizer. Default off because
    /// the standalone slskd-organizer container handles this externally
    /// (it has both /portainer/Downloads and /portainer/Music mounted
    /// where Jellyfin's container does not, and runs even when Jellyfin
    /// is down). Flip to true only if you've added the slskd downloads
    /// dir as a docker volume mount to Jellyfin AND disabled the
    /// standalone organizer.
    /// </summary>
    public bool EnableSlskdAutoOrganize { get; set; } = false;

    // Stub lifecycle
    public int StubGcMaxAgeHours { get; set; } = 72;
    public int StubGcKeepIfRequested { get; set; } = 1; // bool-ish; if 1, requested stubs are kept until reconciled
}
