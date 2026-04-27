using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JellyMusicDiscovery.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.ScheduledTasks;

public class StubGcTask : IScheduledTask
{
    private readonly DiscoveryManager _discovery;
    private readonly ILogger<StubGcTask> _log;

    public StubGcTask(DiscoveryManager discovery, ILogger<StubGcTask> log)
    {
        _discovery = discovery;
        _log = log;
    }

    public string Name => "Music Discovery: GC unrequested stubs";
    public string Key => "MusicDiscoveryStubGc";
    public string Description => "Removes stub items created by Music Discovery search that the user never requested.";
    public string Category => "Music Discovery";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var removed = _discovery.GcOldStubs();
        _log.LogInformation("Stub GC removed {N} items.", removed);
        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks,
        },
    };
}
