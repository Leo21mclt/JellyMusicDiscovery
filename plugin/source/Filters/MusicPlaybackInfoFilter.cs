using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JellyMusicDiscovery.Filters;

/// <summary>
/// Currently a passthrough; kept registered for future use (e.g., logging
/// playback intents on stubs). Actual MediaSource hijacking happens via
/// <see cref="Services.DiscoveryMediaSourceProvider"/>, which is the cleaner
/// Jellyfin-native injection point.
/// </summary>
public class MusicPlaybackInfoFilter : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next) => next();
}
