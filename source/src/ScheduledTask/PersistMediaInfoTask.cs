using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class PersistMediaInfoTask : IScheduledTask
{
    private readonly ILibraryManager library;
    public PersistMediaInfoTask(ILibraryManager libraryManager) => library = libraryManager;
    public string Name => Resources.PersistMediaInfoTask_Name_Persist_MediaInfo;
    public string Description => Resources.PersistMediaInfoTask_Description_Persists_media_info_to_json_file;
    public string Category => "Strm Assistant Rebuild";
    public string Key => "MediaInfoPersistTask";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public async Task Execute(CancellationToken token, IProgress<double> progress)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        var api = plugin.MediaInfoApi ?? throw new InvalidOperationException("Media services not initialized.");
        var items = MediaTaskSelection.Select(library, plugin.MediaStore.GetOptions(), token);
        await plugin.WorkQueue.RunBatch(items, api.ExportAsync, progress, token, true, item => item.InternalId).ConfigureAwait(false);
    }
}
