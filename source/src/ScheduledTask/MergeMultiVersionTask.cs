using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class MergeMultiVersionTask : IScheduledTask, IConfigurableScheduledTask, IScheduledTaskWithExecuteOptions
{
    public MergeMultiVersionTask(ILibraryManager libraryManager) { }
    public string Name => "Merge Multi Versions";
    public string Description => "Merge matching movie and series versions within the configured scope.";
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
