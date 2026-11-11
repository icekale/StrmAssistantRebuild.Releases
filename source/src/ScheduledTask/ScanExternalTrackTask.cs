using MediaBrowser.Model.Tasks;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class ScanExternalTrackTask : IScheduledTask
{
    public string Name => Common.UiLanguage.Text("Scan External Tracks", "扫描外挂字幕和音轨", "掃描外掛字幕和音軌");
    public string Description => Common.UiLanguage.Text("Discover local external subtitles/audio and update Emby media streams without changing media files.", "发现本地外挂字幕和音轨并更新 Emby 媒体流，不修改媒体文件。", "發現本機外掛字幕和音軌並更新 Emby 媒體串流，不修改媒體檔案。");
    public string Category => "Strm Assistant Rebuild";
    public string Key => "ScanExternalTrackTask";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) =>
        (Plugin.Instance?.MaintenanceService ?? throw new InvalidOperationException("Maintenance service not initialized."))
        .ScanExternalTracksAsync(cancellationToken, progress);
}
