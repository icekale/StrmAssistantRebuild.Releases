using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class MergeMultiVersionTask : IScheduledTask, IConfigurableScheduledTask, IScheduledTaskWithExecuteOptions
{
    public MergeMultiVersionTask(ILibraryManager libraryManager) { }
    public string Name => Common.UiLanguage.Text("Merge Multi Versions", "合并多版本", "合併多版本");
    public string Description => Common.UiLanguage.Text("Merge matching movie and series versions within the configured scope.", "在设定范围内合并相同的电影和电视节目版本。", "在設定範圍內合併相同的電影和電視節目版本。");
    public string Category => "Strm Assistant Rebuild";
    public string Key => "MergeMultiVersionTask";
    public bool IsHidden => false;
    public bool IsEnabled => true;
    public bool IsLogged => true;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) =>
        Execute(new TaskOptions(), progress, cancellationToken);
    public async Task Execute(TaskOptions options, IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = Plugin.Instance?.MultiVersionService ?? throw new InvalidOperationException("Multi-version service not initialized.");
        await service.MergeAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}
