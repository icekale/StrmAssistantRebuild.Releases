using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using StrmAssistantRebuild.Options;
using RefreshOptions = MediaBrowser.Controller.Providers.MetadataRefreshOptions;

namespace StrmAssistantRebuild.Common;

/// <summary>Public RefreshCompleted hook and scheduled work share one bounded queue and the same item lock.</summary>
public sealed class MetadataEnhancePipeline : IDisposable
{
    private readonly ILibraryManager library;
    private readonly IProviderManager providers;
    private readonly IFileSystem files;
    private readonly EmbyPathPolicy paths;
    private readonly QueueManager queue;
    private readonly Func<MetadataEnhanceOptions> options;
    private readonly Func<BaseItem, CancellationToken, Task> enhance;
    private readonly ILogger logger;
    private readonly DelayedCatchupQueue pending;
    private readonly DelayedCatchupQueue? pendingSeriesCast;
    private readonly object pendingGate = new();
    private readonly HashSet<long> pendingEnhancements = new();
    private static readonly object originGate = new();
    private static readonly Dictionary<long, int> ownOperations = new();
    // Host callbacks need not retain ExecutionContext: suppress only while this item's
    // owned operation is active, never remember success/failure as a permanent tombstone.
    internal static bool IsOwnOperation(long id) { lock (originGate) return ownOperations.ContainsKey(id); }
    internal static IDisposable SuppressRefreshEvents(long id) => new OwnedOperation(id);
    private sealed class OwnedOperation : IDisposable
    {
        private readonly long id;
        private bool disposed;
        public OwnedOperation(long id)
        {
            this.id = id;
            lock (originGate) ownOperations[id] = ownOperations.GetValueOrDefault(id) + 1;
        }
        public void Dispose()
        {
            lock (originGate)
            {
                if (disposed) return;
                disposed = true;
                if (--ownOperations[id] == 0) ownOperations.Remove(id);
            }
        }
    }
    public Task Completion => pendingSeriesCast == null ? pending.Completion : Task.WhenAll(pending.Completion, pendingSeriesCast.Completion);
    public bool IsBusy => pending.IsBusy || pendingSeriesCast?.IsBusy == true;
    public MetadataEnhancePipeline(ILibraryManager library, IProviderManager providers, IFileSystem files, EmbyPathPolicy paths, QueueManager queue,
        Func<MetadataEnhanceOptions> options, Func<BaseItem, CancellationToken, Task> enhance, ILogger logger,
        Func<Series, CancellationToken, Task>? aggregateSeriesCast = null)
    {
        this.library = library; this.providers = providers; this.files = files; this.paths = paths; this.queue = queue;
        this.options = options; this.enhance = enhance; this.logger = logger;
        pending = new DelayedCatchupQueue(HandlePending, text => logger.Warn("[StrmAssistantRebuild] {0}", text));
        pending.Configure(true);
        if (aggregateSeriesCast != null)
        {
            // Separate delayed consumer: never acquire a Series stripe/tier2 lease while a Season lease is held.
            pendingSeriesCast = new DelayedCatchupQueue(async (ids, token) =>
            {
                await queue.RunBatch(ids, async (id, ct) =>
                {
                    if (!options().EnhanceMovieDbPerson || library.GetItemById(id) is not Series series || series.IsLocked) return;
                    using var origin = SuppressRefreshEvents(id);
                    await aggregateSeriesCast(series, ct).ConfigureAwait(false);
                }, new LogProgress(logger), token, true, id => id).ConfigureAwait(false);
            }, text => logger.Warn("[StrmAssistantRebuild] Series cast: {0}", text));
            pendingSeriesCast.Configure(true);
            library.ItemUpdated += OnItemUpdated;
        }
        providers.RefreshCompleted += OnRefreshCompleted;
    }
    private bool AnyEnabled()
    {
        var o = options();
        return o.EnableDoubanAssistScraping || o.ChineseMovieDb || o.ChineseTvdb || o.PinyinSortName || o.EnhanceNfoMetadata || o.MovieDbEpisodeGroup || o.PreferOriginalPoster;
    }
    private void QueueSeriesCast(BaseItem item)
    {
        if (pendingSeriesCast == null || !options().EnhanceMovieDbPerson || item.IsLocked) return;
        var id = item switch { Series series => series.InternalId, Season { IndexNumber: > 0 } season => season.SeriesId, _ => 0 };
        if (id > 0 && !pendingSeriesCast.Enqueue(id, TimeSpan.FromMilliseconds(250)))
            logger.Warn("[StrmAssistantRebuild] Series cast queue full; series {0} needs another refresh.", id);
    }
    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            // ItemUpdated carries the committed metadata reason. RefreshCompleted has no such reason
            // and may represent an image-only/no-op refresh: do not aggregate from both notifications.
            if (e.Item is not (Series or Season) || e.Item.InternalId <= 0 || IsOwnOperation(e.Item.InternalId) ||
                (e.UpdateReason & (ItemUpdateType.MetadataImport | ItemUpdateType.MetadataDownload)) == 0) return;
            QueueSeriesCast(e.Item);
        }
        catch (Exception ex) { logger.Warn("[StrmAssistantRebuild] Series cast event degraded: {0}", ex.Message); }
    }
    private void OnRefreshCompleted(object? sender, GenericEventArgs<RefreshProgressInfo> e)
    {
        try
        {
            var item = e.Argument.Item;
            if (IsOwnOperation(item.InternalId) || !AnyEnabled() || item is not (Movie or Series or Season or Episode or Person) || item.IsLocked) return;
            lock (pendingGate)
            {
                if (IsOwnOperation(item.InternalId)) return;
                if (pending.Enqueue(item.InternalId, TimeSpan.FromMilliseconds(250))) pendingEnhancements.Add(item.InternalId);
                else logger.Warn("[StrmAssistantRebuild] Metadata enhancement queue full; item {0} can be retried by task.", item.InternalId);
            }
        }
        catch (Exception ex) { logger.Warn("[StrmAssistantRebuild] Metadata event degraded: {0}", ex.Message); }
    }
    private async Task HandlePending(long[] ids, CancellationToken token)
    {
        var items = new List<BaseItem>();
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (library.GetItemById(id) is { } item && !item.IsLocked) { items.Add(item); continue; }
            }
            catch (Exception ex) { logger.Warn("[StrmAssistantRebuild] Metadata item {0} lookup failed: {1}", id, ex.Message); }
            lock (pendingGate) pendingEnhancements.Remove(id);
        }
        await queue.RunBatch(items, (item, ct) => EnhanceCore(item, ct, queued: true), new LogProgress(logger), token, true, i => i.InternalId).ConfigureAwait(false);
    }
    private (long Id, string Name, string Role, PersonType Type)[]? ReadCastLinks(BaseItem item)
    {
        try
        {
            var people = library.GetItemPeople(item);
            if (people.Count > 20000) throw new InvalidDataException("Cast relationship snapshot exceeds 20000 rows.");
            // Snapshot only persisted association/identity/order. Person images/provider IDs are global
            // records shared by Series and Season; changing those alone must not reorder a Series cast.
            return people.Select(p => (p.Id, p.Name, p.Role, p.Type)).ToArray();
        }
        catch (Exception error)
        {
            logger.Warn("[StrmAssistantRebuild] Cast change observation skipped for {0}: {1}", item.InternalId, error.GetType().Name);
            return null; // Unknown is not an empty cast and must not prevent other enhancement work.
        }
    }
    private async Task EnhanceCore(BaseItem item, CancellationToken token, bool queued = false, bool scheduleCast = true)
    {
        using var origin = SuppressRefreshEvents(item.InternalId);
        // Checked after acquiring the shared item lock: a direct refresh/apply may have
        // superseded this queued enhancement while it waited for the same item.
        lock (pendingGate) { if (!pendingEnhancements.Remove(item.InternalId) && queued) return; }
        if (!item.IsLocked && (!queued || AnyEnabled()))
        {
            var observeCast = queued && pendingSeriesCast != null && options().EnhanceMovieDbPerson &&
                item is Series or Season { IndexNumber: > 0 };
            var beforeCast = observeCast ? ReadCastLinks(item) : null;
            try { await enhance(item, token).ConfigureAwait(false); }
            finally
            {
                // Explicit work owns one fallback. Queued work only propagates an actual committed
                // link change, including partial success before another enhancer fails; not a no-op or rollback.
                if (!token.IsCancellationRequested && (scheduleCast && !queued ||
                    beforeCast != null && ReadCastLinks(item) is { } afterCast && !beforeCast.SequenceEqual(afterCast)) && !token.IsCancellationRequested)
                    QueueSeriesCast(item);
            }
        }
    }
    public Task ApplyAsync(BaseItem item, CancellationToken token) => queue.RunSingle(item.InternalId, async ct => { await EnhanceCore(item, ct).ConfigureAwait(false); return true; }, token);
    public Task<bool> RefreshAsync(BaseItem item, CancellationToken token, bool metadata = true, bool images = true, bool fullImages = false, bool downloadImagesInAdvance = false, bool supplementOnly = false, bool exclusiveTaskMarks = false) =>
        queue.RunSingle(item.InternalId, async ct =>
        {
            if (supplementOnly)
            {
                // Re-read after acquiring the item lease; never resurrect a removed person or overwrite a newer edit.
                if (item is not Person || library.GetItemById(item.InternalId) is not Person person || person.IsLocked) return false;
                paths.RequireWritablePath(person.GetInternalMetadataPath());
                var collections = library.GetCollectionFolders(person, ct);
                var source = library.GetLibraryOptions(person, collections, ct);
                var configuration = JsonSerializer.Deserialize<MediaBrowser.Model.Configuration.LibraryOptions>(JsonSerializer.Serialize(source))!;
                configuration.SaveLocalMetadata = false; configuration.SaveLocalThumbnailSets = false; configuration.SaveSubtitlesWithMedia = false;
                if (downloadImagesInAdvance) configuration.DownloadImagesInAdvance = true;
                using var origin = SuppressRefreshEvents(person.InternalId);
                lock (pendingGate) pendingEnhancements.Remove(person.InternalId);
                return await PersonSupplement.ApplyAsync(person, metadata, images, providers, library, configuration,
                    new DirectoryService(logger, files), paths, ct).ConfigureAwait(false);
            }
            await RefreshCore(item, ct, metadata, images, fullImages, downloadImagesInAdvance, exclusiveTaskMarks).ConfigureAwait(false);
            return true;
        }, token);
    /// <summary>M160: set by the plugin to ExclusiveEligibility.EnterTaskItem (kept as a hook so this pipeline has no exclusive-mode dependency).</summary>
    public static Func<BaseItem, IDisposable?>? TaskItemMarks { get; set; }
    public Task<BatchProgress> RefreshBatchAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken token) =>
        queue.RunBatch(items, (item, ct) => RefreshCore(item, ct, true, true, false, false, exclusiveTaskMarks: true), progress, token, true, i => i.InternalId);
    private async Task RefreshCore(BaseItem item, CancellationToken token, bool metadata, bool images, bool fullImages, bool downloadImagesInAdvance = false, bool exclusiveTaskMarks = false)
    {
        if (item.IsLocked) return;
        paths.RequireWritablePath(item.GetInternalMetadataPath());
        var collections = library.GetCollectionFolders(item, token);
        var source = library.GetLibraryOptions(item, collections, token);
        var configuration = JsonSerializer.Deserialize<MediaBrowser.Model.Configuration.LibraryOptions>(JsonSerializer.Serialize(source))!;
        configuration.SaveLocalMetadata = false; configuration.SaveLocalThumbnailSets = false; configuration.SaveSubtitlesWithMedia = false;
        if (downloadImagesInAdvance) configuration.DownloadImagesInAdvance = true;
        var refresh = new RefreshOptions(new DirectoryService(logger, files))
        {
            EnableRemoteContentProbe = false, EnableThumbnailImageExtraction = false, EnableSubtitleDownloading = false,
            MetadataRefreshMode = metadata ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.ValidationOnly,
            ReplaceAllMetadata = metadata, ImageRefreshMode = images ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.ValidationOnly,
            ReplaceAllImages = fullImages
        };
        using var origin = SuppressRefreshEvents(item.InternalId);
        lock (pendingGate) pendingEnhancements.Remove(item.InternalId);
        // M160: original RefreshMetadataTask/EpisodeRefresh mark a Video item with {CatchAllBlock, IgnoreExtSubChange} for the refresh.
        using var exclusiveMarks = exclusiveTaskMarks ? TaskItemMarks?.Invoke(item) : null;
        try
        {
            await providers.RefreshSingleItem(item, refresh, collections, configuration, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (metadata || item is not Person) await EnhanceCore(item, token, scheduleCast: false).ConfigureAwait(false);
        }
        finally
        {
            // Native metadata can be committed before a later refresh stage fails. Reconcile the current
            // stored cast after releasing this lease, not a stale DTO. Image-only work never requests it.
            if (metadata && !token.IsCancellationRequested) QueueSeriesCast(item);
        }
    }
    public void Dispose()
    {
        providers.RefreshCompleted -= OnRefreshCompleted;
        if (pendingSeriesCast != null) library.ItemUpdated -= OnItemUpdated;
        lock (pendingGate) { pendingEnhancements.Clear(); pending.Dispose(); pendingSeriesCast?.Dispose(); }
    }
    private sealed class LogProgress : IProgress<double>
    {
        private readonly ILogger logger;
        public LogProgress(ILogger logger) => this.logger = logger;
        public void Report(double value) => logger.Info("[StrmAssistantRebuild] Metadata enhancement progress {0:F1}%", value);
    }
}
