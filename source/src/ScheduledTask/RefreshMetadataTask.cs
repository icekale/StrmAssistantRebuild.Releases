using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class RefreshMetadataTask : IScheduledTask
{
    private readonly ILibraryManager library;
    public RefreshMetadataTask(ILibraryManager library) => this.library = library;
    public string Name => Common.UiLanguage.Text("Refresh Metadata", "刷新元数据", "重新整理中繼資料");
    public string Description => Resources.EpisodeRefreshTask_Description_Refresh_metadata_for_episodes_missing_overview;
    public string Category => "Strm Assistant Rebuild";
    public string Key => "MetadataRefreshTask";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public async Task Execute(CancellationToken token, IProgress<double> progress)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        var pipeline = plugin.MetadataPipeline ?? throw new InvalidOperationException("Metadata service not initialized.");
        var options = plugin.MetadataStore.GetOptions().MetadataRefreshOptions;
        var selected = MetadataRefreshPolicy.Selected(options.MetadataRefreshLibraryScope);
        var folders = library.GetVirtualFolders().Where(f => selected.Count == 0 || selected.Contains(f.ItemId) || selected.Contains(f.Id)).ToArray();
        var items = new Dictionary<long, BaseItem>();
        foreach (var folder in folders)
        {
            token.ThrowIfCancellationRequested();
            var root = library.GetItemById(folder.ItemId);
            if (root == null) continue;
            foreach (var item in library.GetItemList(new InternalItemsQuery { ParentIds = new[] { root.InternalId }, IncludeItemTypes = new[] { "Movie", "Series", "Season", "Episode" }, Recursive = true, IsLocked = false, GroupByPresentationUniqueKey = false }, token))
                if (MetadataRefreshPolicy.NeedsRefresh(item, options, DateTimeOffset.UtcNow)) items[item.InternalId] = item;
        }
        await pipeline.RefreshBatchAsync(items.Values.ToArray(), progress, token).ConfigureAwait(false);
    }
}
