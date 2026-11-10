using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class ExtractMediaInfoTask : IScheduledTask
{
    private readonly ILibraryManager library;
    public ExtractMediaInfoTask(ILibraryManager libraryManager) => library = libraryManager;
    public string Name => Resources.ExtractMediaInfoTask_Name_Extract_MediaInfo;
    public string Description => Resources.ExtractMediaInfoTask_Description_Extracts_media_info_from_videos_and_audios;
    public string Category => "Strm Assistant Rebuild";
    // OSS key retained; target encrypted key remains an explicitly recorded evidence gap.
    public string Key => "MediaInfoExtractTask";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public async Task Execute(CancellationToken token, IProgress<double> progress)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        var api = plugin.MediaInfoApi ?? throw new InvalidOperationException("Media services not initialized.");
        var options = plugin.MediaStore.GetOptions();
        var items = MediaTaskSelection.Select(library, options, token);
        await plugin.WorkQueue.RunBatch(items, (item, ct) => MediaExtractionPipeline.Extract(plugin, item, options, ct),
            progress, token, itemKey: item => item.InternalId).ConfigureAwait(false);
    }
}
