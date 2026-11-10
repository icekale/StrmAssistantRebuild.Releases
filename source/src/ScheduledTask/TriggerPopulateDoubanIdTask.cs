using MediaBrowser.Controller.Library;

namespace StrmAssistantRebuild.ScheduledTask;

/// <summary>The shipped contract is ILibraryPostScanTask, not another scheduled-task route.</summary>
public sealed class TriggerPopulateDoubanIdTask : ILibraryPostScanTask
{
    private readonly ILibraryManager library;
    public TriggerPopulateDoubanIdTask(ILibraryManager libraryManager) => library = libraryManager;
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin?.MetadataStore.GetOptions().EnableDoubanAssistScraping != true || plugin.DoubanMetadataService == null)
        { progress.Report(100); return; }
        await plugin.DoubanMetadataService.PopulateIdsAsync(BuildDoubanCacheTask.SelectItems(library, cancellationToken), progress,
            cancellationToken, plugin.WorkQueue).ConfigureAwait(false);
    }
}
