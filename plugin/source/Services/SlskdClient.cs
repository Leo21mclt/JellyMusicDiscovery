using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Services;

/// <summary>
/// Minimal slskd API wrapper: kick off a search, poll for responses,
/// auto-pick the best file by configured format/bitrate, enqueue the download
/// into a Jellyfin-library-resident folder so the eventual file appears in
/// the normal scan.
/// </summary>
public class SlskdClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SlskdClient> _log;

    public SlskdClient(IHttpClientFactory httpFactory, ILogger<SlskdClient> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<DownloadHandle?> SearchAndDownloadAsync(string artist, string title, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.SlskdBaseUrl) || string.IsNullOrWhiteSpace(cfg.SlskdApiKey))
        {
            _log.LogWarning("slskd not configured; skipping download.");
            return null;
        }

        using var http = MakeClient();

        // 1. Submit search.
        var searchText = $"{artist} {title}".Trim();
        var startResp = await http.PostAsJsonAsync("api/v0/searches", new
        {
            searchText,
            timeout = cfg.SlskdSearchTimeoutSeconds * 1000,
            fileLimit = 250,
            filterResponses = true,
            minimumPeerUploadSpeed = cfg.SlskdMinPeerSpeedKbps * 1024,
        }, ct).ConfigureAwait(false);

        if (!startResp.IsSuccessStatusCode)
        {
            _log.LogWarning("slskd search submit failed: {Status}", (int)startResp.StatusCode);
            return null;
        }
        var search = await startResp.Content.ReadFromJsonAsync<SlskdSearch>(JsonOpts, ct).ConfigureAwait(false);
        if (search?.Id is null) return null;

        // 2. Poll until search completes (or timeout).
        var deadline = DateTime.UtcNow.AddSeconds(cfg.SlskdSearchTimeoutSeconds + 4);
        SlskdSearch? final = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(750, ct).ConfigureAwait(false);
            var s = await http.GetFromJsonAsync<SlskdSearch>($"api/v0/searches/{search.Id}", JsonOpts, ct).ConfigureAwait(false);
            if (s?.IsComplete == true) { final = s; break; }
        }
        final ??= await http.GetFromJsonAsync<SlskdSearch>($"api/v0/searches/{search.Id}", JsonOpts, ct).ConfigureAwait(false);

        // 3. Fetch responses (full bodies).
        var responses = await http.GetFromJsonAsync<List<SlskdSearchResponse>>(
            $"api/v0/searches/{search.Id}/responses", JsonOpts, ct).ConfigureAwait(false) ?? new();

        var best = PickBest(responses, artist, title, cfg);
        if (best is null)
        {
            _log.LogInformation("slskd: no acceptable result for {Artist} - {Title}", artist, title);
            return null;
        }

        // 4. Enqueue download.
        var enqueue = await http.PostAsJsonAsync($"api/v0/transfers/downloads/{Uri.EscapeDataString(best.Username)}",
            new[]
            {
                new { filename = best.File.Filename, size = best.File.Size },
            },
            ct).ConfigureAwait(false);

        if (!enqueue.IsSuccessStatusCode)
        {
            _log.LogWarning("slskd enqueue failed: {Status} {Body}", (int)enqueue.StatusCode,
                await enqueue.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return null;
        }

        var localPath = ResolveLocalPath(best.File.Filename);
        return new DownloadHandle
        {
            Username = best.Username,
            RemoteFile = best.File.Filename,
            ExpectedSize = best.File.Size,
            LocalPath = localPath,
            ContentType = GuessContentType(best.File.Filename),
        };
    }

    public async Task<DownloadStatus?> GetDownloadStatusAsync(string username, string remoteFile, CancellationToken ct)
    {
        using var http = MakeClient();
        try
        {
            var transfers = await http.GetFromJsonAsync<List<SlskdTransfer>>(
                $"api/v0/transfers/downloads/{Uri.EscapeDataString(username)}", JsonOpts, ct).ConfigureAwait(false);
            var match = transfers?.FirstOrDefault(t => string.Equals(t.Filename, remoteFile, StringComparison.Ordinal));
            if (match is null) return null;
            return new DownloadStatus { State = match.State ?? "Unknown", BytesTransferred = match.BytesTransferred };
        }
        catch (Exception ex) { _log.LogDebug(ex, "slskd status fetch failed"); return null; }
    }

    private HttpClient MakeClient()
    {
        var cfg = Plugin.Instance!.Configuration;
        var http = _httpFactory.CreateClient();
        http.BaseAddress = new Uri(cfg.SlskdBaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.Add("X-API-Key", cfg.SlskdApiKey);
        return http;
    }

    private static SlskdSearchResponse.Hit? PickBest(List<SlskdSearchResponse> responses, string artist, string title, Configuration.PluginConfiguration cfg)
    {
        var prefs = cfg.PreferredFormats.Select(p => p.ToLowerInvariant()).ToArray();
        int FormatRank(string filename)
        {
            var ext = Path.GetExtension(filename).TrimStart('.').ToLowerInvariant();
            for (var i = 0; i < prefs.Length; i++) if (prefs[i] == ext) return i;
            return int.MaxValue;
        }

        var artistKey = artist.ToLowerInvariant();
        var titleKey = title.ToLowerInvariant();

        return responses
            .SelectMany(r => (r.Files ?? new()).Select(f => new SlskdSearchResponse.Hit
            {
                Username = r.Username ?? string.Empty,
                File = f,
                UploadSpeed = r.UploadSpeed ?? 0,
                QueueLength = r.QueueLength ?? 0,
            }))
            .Where(h => h.File.Size > 0)
            .Where(h => h.File.BitRate is null || h.File.BitRate >= cfg.MinBitrateKbps)
            .Where(h => h.File.Filename.ToLowerInvariant().Contains(titleKey))
            .OrderBy(h => FormatRank(h.File.Filename))                                // preferred format first
            .ThenByDescending(h => h.File.Filename.ToLowerInvariant().Contains(artistKey)) // artist match boost
            .ThenByDescending(h => h.UploadSpeed)
            .ThenBy(h => h.QueueLength)
            .FirstOrDefault();
    }

    private static string ResolveLocalPath(string remoteFilename)
    {
        var cfg = Plugin.Instance!.Configuration;
        var leaf = Path.GetFileName(remoteFilename.Replace('\\', '/'));
        return Path.Combine(cfg.MusicLibraryRoot, cfg.DiscoveryDownloadSubdir, leaf);
    }

    private static string GuessContentType(string filename)
    {
        return Path.GetExtension(filename).TrimStart('.').ToLowerInvariant() switch
        {
            "flac" => "audio/flac",
            "mp3" => "audio/mpeg",
            "ogg" => "audio/ogg",
            "m4a" or "aac" => "audio/mp4",
            "opus" => "audio/opus",
            "wav" => "audio/wav",
            _ => "application/octet-stream",
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // --- DTOs ----------------------------------------------------------

    public class DownloadHandle
    {
        public required string Username { get; init; }
        public required string RemoteFile { get; init; }
        public required long ExpectedSize { get; init; }
        public required string LocalPath { get; init; }
        public required string ContentType { get; init; }
    }

    public class DownloadStatus
    {
        public required string State { get; init; }
        public long BytesTransferred { get; init; }
    }

    private class SlskdSearch
    {
        public string? Id { get; set; }
        public bool? IsComplete { get; set; }
    }

    private class SlskdSearchResponse
    {
        public string? Username { get; set; }
        public List<SlskdFile>? Files { get; set; }
        [JsonPropertyName("uploadSpeed")] public int? UploadSpeed { get; set; }
        [JsonPropertyName("queueLength")] public int? QueueLength { get; set; }

        public class Hit
        {
            public required string Username { get; init; }
            public required SlskdFile File { get; init; }
            public int UploadSpeed { get; init; }
            public int QueueLength { get; init; }
        }
    }

    private class SlskdFile
    {
        public string Filename { get; set; } = string.Empty;
        public long Size { get; set; }
        public int? BitRate { get; set; }
        public int? Length { get; set; }
    }

    private class SlskdTransfer
    {
        public string? Filename { get; set; }
        public string? State { get; set; }
        public long BytesTransferred { get; set; }
    }
}
