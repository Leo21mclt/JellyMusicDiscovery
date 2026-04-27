using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace JellyMusicDiscovery.Services;

/// <summary>
/// Returns a MediaSource pointing at the plugin's /mdiscover/stream endpoint
/// for stub Audio items. This is the hook Jellyfin calls during PlaybackInfo
/// resolution, so the response works the same for the web UI and any
/// third-party client speaking the standard API.
/// </summary>
public class DiscoveryMediaSourceProvider : IMediaSourceProvider
{
    public string Name => "Music Discovery";

    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        // Audio (track) stubs — keep the existing behavior.
        if (item is MediaBrowser.Controller.Entities.Audio.Audio)
        {
            if (!item.IsDiscoveryStub())
                return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
            var src = new MediaSourceInfo
            {
                Id = item.Id.ToString("N"),
                Path = $"/mdiscover/stream?id={item.Id:N}",
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                Name = item.Name,
                Container = "mp3",
                SupportsTranscoding = false,
                SupportsDirectStream = true,
                SupportsDirectPlay = true,
                RunTimeTicks = item.RunTimeTicks,
                MediaStreams = new List<MediaBrowser.Model.Entities.MediaStream>(),
                RequiredHttpHeaders = new Dictionary<string, string>(),
            };
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { src });
        }

        // MusicVideo stubs — same shape as the BaseItemDto MediaSource so the
        // web video player gets a consistent answer from both PlaybackInfo
        // POST and the GET-DTO path. Path-based File protocol routes web's
        // video player through /Videos/{id}/... which our stream resolver
        // 302s to the Python /video/by-id endpoint.
        if (item is MediaBrowser.Controller.Entities.MusicVideo)
        {
            // Discovery video stubs don't carry the Stub tag (we don't set it
            // when registering the MusicVideo entity), so detect by the
            // synthetic path prefix instead.
            if (string.IsNullOrEmpty(item.Path) || !item.Path.StartsWith("/mdiscover-video/"))
                return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());

            var videoStream = new MediaBrowser.Model.Entities.MediaStream
            {
                Type = MediaBrowser.Model.Entities.MediaStreamType.Video,
                Codec = "h264",
                Width = 480,
                Height = 360,
                RealFrameRate = 30f,
                Index = 0,
                IsDefault = true,
            };
            var audioStream = new MediaBrowser.Model.Entities.MediaStream
            {
                Type = MediaBrowser.Model.Entities.MediaStreamType.Audio,
                Codec = "aac",
                BitRate = 128_000,
                SampleRate = 44_100,
                Channels = 2,
                ChannelLayout = "stereo",
                Index = 1,
                IsDefault = true,
            };

            var vsrc = new MediaSourceInfo
            {
                Id = item.Id.ToString("N"),
                Path = $"/data/musicvideos/streamed/{item.Id:N}.mp4",
                Protocol = MediaProtocol.File,
                IsRemote = false,
                Type = MediaSourceType.Default,
                Name = item.Name,
                Container = "mp4",
                SupportsTranscoding = true,
                SupportsDirectStream = true,
                SupportsDirectPlay = true,
                RunTimeTicks = item.RunTimeTicks,
                Bitrate = 1_000_000,
                DefaultAudioStreamIndex = 1,
                MediaStreams = new List<MediaBrowser.Model.Entities.MediaStream> { videoStream, audioStream },
                MediaAttachments = System.Array.Empty<MediaBrowser.Model.Entities.MediaAttachment>(),
                Formats = System.Array.Empty<string>(),
                RequiredHttpHeaders = new Dictionary<string, string>(),
            };
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { vsrc });
        }

        return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        => Task.FromResult<ILiveStream>(null!);
}
