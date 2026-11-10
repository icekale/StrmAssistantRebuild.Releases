using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class TriggerMergeMultiVersionTask : ILibraryPostScanTask
{
    private readonly MergeMultiVersionTask task;
    public TriggerMergeMultiVersionTask(MergeMultiVersionTask task) => this.task = task;
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Plugin.Instance?.ExperienceStore.GetOptions().MergeMultiVersion != true)
        { progress.Report(100); return Task.CompletedTask; }
        return task.Execute(new TaskOptions(), progress, cancellationToken);
    }
}
