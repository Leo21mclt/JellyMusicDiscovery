using System;
using System.Threading.Tasks;
using JellyMusicDiscovery.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Filters;

/// <summary>
/// Short-circuits image requests for stub items. When the client asks for
/// /Items/{guid}/Images/Primary on a synthetic item, we redirect the response
/// to the Deezer CDN URL we cached at search time. Real items pass through
/// untouched to Jellyfin's normal image pipeline.
/// </summary>
public class MusicImageActionFilter : IAsyncActionFilter
{
    private readonly DiscoveryItemCache _cache;
    private readonly DiscoveryPlaylistFeed _discoveryFeed;
    private readonly ILogger<MusicImageActionFilter> _log;

    public MusicImageActionFilter(
        DiscoveryItemCache cache,
        DiscoveryPlaylistFeed discoveryFeed,
        ILogger<MusicImageActionFilter> log)
    {
        _cache = cache;
        _discoveryFeed = discoveryFeed;
        _log = log;
    }

    public Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        try
        {
            // Bail fast unless this looks like a /Items/{id}/Images/... route.
            var route = ctx.RouteData.Values;
            var controller = route.TryGetValue("controller", out var c) ? c?.ToString() : null;
            if (!string.Equals(controller, "Image", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(controller, "Images", StringComparison.OrdinalIgnoreCase))
                return next();

            // Pull the item id out of any of the usual route slots.
            string? idStr = null;
            foreach (var key in new[] { "itemId", "ItemId", "id", "Id" })
                if (route.TryGetValue(key, out var v) && v is not null) { idStr = v.ToString(); break; }
            if (idStr is null && ctx.HttpContext.Request.RouteValues.TryGetValue("itemId", out var rv))
                idStr = rv?.ToString();

            if (idStr is null || !Guid.TryParse(idStr, out var itemId)) return next();

            // Discovery playlist? Different storage from the item cache —
            // its image URL lives in the playlist DTO.
            var dp = _discoveryFeed.Get(itemId);
            if (dp is not null && !string.IsNullOrEmpty(dp.ImageUrl))
            {
                ctx.Result = new RedirectResult(dp.ImageUrl);
                return Task.CompletedTask;
            }

            if (!_cache.TryGet(itemId, out var entry) || entry?.PrimaryImageUrl is null)
                return next();

            // Short-circuit with a 302 to the Deezer image. Saves the bandwidth
            // hop through Jellyfin and lets the browser cache directly.
            ctx.Result = new RedirectResult(entry.PrimaryImageUrl);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Image filter passthrough on error.");
            return next();
        }
    }
}
