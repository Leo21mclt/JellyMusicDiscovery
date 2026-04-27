using System;
using System.Collections.Generic;
using JellyMusicDiscovery.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace JellyMusicDiscovery;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer)
        : base(appPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    /// <summary>Captured at plugin registration so filters can stamp BaseItemDto.ServerId on synthetic items.</summary>
    public static string? ServerSystemId { get; set; }

    public override string Name => "Music Discovery";

    public override Guid Id => Guid.Parse("7b3a1c84-9a2f-4d66-8c12-5f9a8d7e6b40");

    public override string Description =>
        "Search MusicBrainz inside Jellyfin and request via Lidarr / slskd.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        yield return new PluginPageInfo
        {
            Name = "MusicDiscoveryConfig",
            EnableInMainMenu = true,
            DisplayName = "Music Discovery",
            EmbeddedResourcePath = $"{prefix}.Configuration.configPage.html",
        };
    }
}
