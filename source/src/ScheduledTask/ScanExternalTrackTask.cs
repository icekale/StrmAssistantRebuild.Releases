using MediaBrowser.Model.Tasks;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class ScanExternalTrackTask : IScheduledTask
{
    public string Name => "Scan External Tracks";
    public string Description => "Discover local external subtitles/audio and update Emby media streams without changing media files.";
    public string Category => "Strm Assistant Rebuild";
    public string Key => "ScanExternalTrackTask";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) =>
        (Plugin.Instance?.MaintenanceService ?? throw new InvalidOperationException("Maintenance service not initialized."))
        .ScanExternalTracksAsync(cancellationToken, progress);
}
