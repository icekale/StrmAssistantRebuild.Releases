using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using StrmAssistantRebuild.Options;
using StrmAssistantRebuild.Provider;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class BuildDoubanCacheTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILibraryManager library;
    public BuildDoubanCacheTask(ILibraryManager libraryManager, IFileSystem fileSystem) => library = libraryManager;
    public string Name => "Build Douban Cache";
    public string Description => Resources.BuildDoubanCacheTask_Description_Build_Douban_metadata_cache_to_assist_MovieDb_scraping;
    public string Category => "Strm Assistant Rebuild";
    public string Key => "BuildDoubanCacheTask";
    public bool IsEnabled => Plugin.Instance?.MetadataStore.GetOptions() is { } settings &&
        (settings.EnableDoubanAssistScraping || settings.MetadataBuildOptions.MetadataCacheMode != MetadataBuildOptions.MetadataCacheOption.None.ToString());
    public bool IsHidden => !IsEnabled;
    public bool IsLogged => true;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        var service = plugin.DoubanMetadataService ?? throw new InvalidOperationException("Douban service not initialized.");
        if (!IsEnabled) { progress.Report(100); return; }
        var items = SelectItems(library, cancellationToken);
        // Original order: build cache (first half of progress), then rating lookback update.
        var buildProgress = new Progress<double>(value => progress.Report(Math.Min(value, 100) * 0.5));
        var result = await service.BuildCacheAsync(items, buildProgress, cancellationToken, plugin.WorkQueue).ConfigureAwait(false);
        var candidates = service.SelectRatingCandidates(plugin.MetadataStore.GetOptions(), cancellationToken);
        var ratingProgress = new Progress<double>(value => progress.Report(50 + Math.Min(value, 100) * 0.5));
        var ratings = await service.UpdateRatingsAsync(candidates, ratingProgress, cancellationToken, plugin.WorkQueue).ConfigureAwait(false);
        progress.Report(100);
        // A blocked provider is not a successful cache build. Per-item failures stay isolated in the batch.
        if (result.Failed > 0 && (result.Matched == 0 || result.Failed == items.Length)) throw new InvalidOperationException("Douban cache build has no verified subjects or every item reported a failure; see item logs.");
        if (ratings.Total > 0 && ratings.Failed == ratings.Total) throw new InvalidOperationException("Every rating lookback item reported a provider failure; see item logs.");
    }
    internal static BaseItem[] SelectItems(ILibraryManager library, CancellationToken token) => library.GetItemList(new InternalItemsQuery
    {
        IncludeItemTypes = new[] { "Movie", "Series", "Season" }, Recursive = true, IsVirtualItem = false,
        GroupByPresentationUniqueKey = false, IsLocked = false
    }, token).Where(item => DoubanSubjectParser.IsSubjectId(item.GetProviderId(DoubanExternalId.StaticName)) ||
        DoubanSubjectParser.IsImdbId(item.GetProviderId(MediaBrowser.Model.Entities.MetadataProviders.Imdb))).ToArray();
}
