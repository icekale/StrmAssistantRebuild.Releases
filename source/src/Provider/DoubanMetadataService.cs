using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Options;
using CacheMode = StrmAssistantRebuild.Options.MetadataBuildOptions.MetadataCacheOption;

namespace StrmAssistantRebuild.Provider;

public sealed class DoubanMetadataService
{
    private readonly ILibraryManager library;
    private readonly IProviderManager provider;
    private readonly DoubanClient client;
    private readonly EmbyPathPolicy paths;
    private readonly string defaultCacheRoot;
    private readonly Func<MetadataEnhanceOptions> options;
    private readonly ILogger logger;
    private readonly IFileSystem? files;
    private readonly HttpClientFactory http;
    public DoubanMetadataService(ILibraryManager library, IProviderManager provider, HttpClientFactory http,
        EmbyPathPolicy paths, string defaultCacheRoot, Func<MetadataEnhanceOptions> getOptions, ILogger logger, IFileSystem? fileSystem = null)
    {
        this.library = library; this.provider = provider; client = new DoubanClient(http); this.http = http; this.paths = paths;
        this.defaultCacheRoot = paths.RequireWritablePath(defaultCacheRoot); options = getOptions; this.logger = logger; files = fileSystem;
    }
    // Caller owns the item lock. No refresh/re-entry and no nested QueueManager.RunSingle here.
    public Task<DoubanApplyResult> ApplyAsync(BaseItem item, CancellationToken token) => ProcessAsync(item, options(), false, false, token);
    public Task<DoubanBatchResult> BuildCacheAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken token, QueueManager? queue = null)
        => BatchAsync(items, progress, token, false, queue);
    public Task<DoubanBatchResult> PopulateIdsAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken token, QueueManager? queue = null)
        => BatchAsync(items, progress, token, true, queue);

    private async Task<DoubanBatchResult> BatchAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken token, bool idsOnly, QueueManager? queue)
    {
        var matched = 0; var changed = 0; var skipped = 0; var failed = 0; var done = 0;
        var snapshot = options();
        progress.Report(0);
        // Sequential dispatch deliberately bounds provider load; the shared queue coordinates other tasks.
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = queue == null ? await ProcessAsync(item, snapshot, !idsOnly, idsOnly, token).ConfigureAwait(false) :
                    await queue.RunSingle(item.InternalId, ct => ProcessAsync(item, snapshot, !idsOnly, idsOnly, ct), token).ConfigureAwait(false);
                if (result.SubjectId != null) matched++;
                if (result.Changed) changed++;
                if (result.Failed) failed++;
                else if (!result.Changed) skipped++;
                logger.Info("[StrmAssistantRebuild] Douban item {0}: {1}", item.InternalId, result.Status);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { failed++; logger.Warn("[StrmAssistantRebuild] Douban item {0}: failed", item.InternalId); }
            progress.Report(++done * 100d / items.Count);
        }
        progress.Report(100);
        logger.Info("[StrmAssistantRebuild] Douban matched {0}, changed {1}, skipped {2}, failed {3}", matched, changed, skipped, failed);
        return new(matched, changed, skipped, failed);
    }
    private async Task<DoubanApplyResult> ProcessAsync(BaseItem item, MetadataEnhanceOptions settings, bool build, bool idsOnly, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!settings.EnableDoubanAssistScraping && !build) return new(false, "disabled");
        if (item is not (Movie or Series or Season or Trailer)) return new(false, "unsupported");
        if (item.IsLocked) return new(false, "locked");
        var id = item.GetProviderId(DoubanExternalId.StaticName);
        var imdb = item.GetProviderId(MetadataProviders.Imdb);
        id = string.IsNullOrWhiteSpace(id) ? null : id; imdb = string.IsNullOrWhiteSpace(imdb) ? null : imdb;
        if (idsOnly && id != null) return new(false, "already-identified");
        if (id == null && imdb == null) return new(false, "no-identity");
        if (id != null && !DoubanSubjectParser.IsSubjectId(id) || imdb != null && !DoubanSubjectParser.IsImdbId(imdb)) return new(false, "invalid-identity");
        if (!Enum.TryParse<CacheMode>(settings.MetadataBuildOptions.MetadataCacheMode, true, out var mode) || !Enum.IsDefined(typeof(CacheMode), mode))
            return new(false, "invalid-cache-mode");
        DoubanLookup lookup;
        try
        {
            if (mode == CacheMode.None) lookup = await client.GetAsync(id, imdb, settings.MetadataRefreshOptions, token).ConfigureAwait(false);
            else
            {
                var root = string.IsNullOrWhiteSpace(settings.MetadataBuildOptions.MetadataCacheRootFolder) ? defaultCacheRoot : settings.MetadataBuildOptions.MetadataCacheRootFolder;
                var cache = new DoubanCache(paths, root);
                lookup = await cache.ResolveAsync(id, imdb, mode, ct => client.GetAsync(id, imdb, settings.MetadataRefreshOptions, ct), token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new(false, "cache-path-error"); }
        var subject = lookup.Subject;
        if (subject == null) return new(false, lookup.Status);
        if (!DoubanSubjectParser.MatchesIdentity(subject, id, imdb)) return new(false, "identity-mismatch");
        token.ThrowIfCancellationRequested();
        if (item.IsLocked) return new(false, "locked");
        var changed = false;
        var scope = DoubanScope.Parse(settings.DoubanAssistScrapeScope);
        bool Can(MetadataFields field) => !(item.LockedFields ?? Array.Empty<MetadataFields>()).Contains(field);
        var oldName = item.Name; var oldOverview = item.Overview; var oldRating = item.CommunityRating;
        var oldPeople = library.GetItemPeople(item);
        List<PersonInfo>? people = null;
        try
        {
            if (id == null) { item.SetProviderId(DoubanExternalId.StaticName, subject.Id); changed = true; }
            if (!idsOnly && settings.EnableDoubanAssistScraping)
            {
                if (scope.Contains("NameOverview"))
                {
                    if (Can(MetadataFields.Name) && !string.IsNullOrWhiteSpace(subject.Title) && item.Name != subject.Title) { item.Name = subject.Title; changed = true; }
                    if (Can(MetadataFields.Overview) && !string.IsNullOrWhiteSpace(subject.Overview) && item.Overview != subject.Overview) { item.Overview = subject.Overview; changed = true; }
                }
                if (scope.Contains("Rating") && Can(MetadataFields.CommunityRating) && subject.Rating is >= 0 and <= 10 && item.CommunityRating != subject.Rating)
                { item.CommunityRating = subject.Rating; changed = true; }
                if (scope.Contains("CastCharacter") && Can(MetadataFields.Cast) && subject.People.Length > 0)
                {
                    people = MergePeople(oldPeople, subject.People);
                    if (!SamePeople(oldPeople, people)) { library.UpdatePeople(item, people, true); changed = true; }
                }
            }
            token.ThrowIfCancellationRequested();
            if (changed) library.UpdateItems(new List<BaseItem> { item }, null, ItemUpdateType.MetadataEdit, true, false, null, token);
        }
        catch
        {
            item.Name = oldName; item.Overview = oldOverview; item.CommunityRating = oldRating;
            if (id == null) item.ProviderIds.Remove(DoubanExternalId.StaticName);
            if (people != null) library.UpdatePeople(item, oldPeople, false); // Existing item: an empty previous cast must clear the committed replacement.
            throw;
        }
        var status = changed ? "applied" : "unchanged";
        if (!idsOnly && settings.EnableDoubanAssistScraping && scope.Contains("Poster") && !item.HasImage(ImageType.Primary, 0) && subject.ImageUrl != null)
        {
            try { if (await SavePosterAsync(item, subject.ImageUrl, token).ConfigureAwait(false)) { changed = true; status = "applied"; } else status += files == null ? ":poster-not-supported" : ":poster-skipped"; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { status += ":poster-failed"; }
        }
        if (lookup.Status == "online-cache-write-failed") status += ":cache-write-failed";
        if (build && mode == CacheMode.None) status += ":cache-disabled";
        return new(changed, status, subject.Id);
    }

    // ---- M144/F1: independent rating lookback + MDBList Rotten Tomatoes ----
    private MdbListClient? mdbList;
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    internal MdbListClient MdbList { get => mdbList ??= new MdbListClient(http); set => mdbList = value; }

    private string CacheRoot(MetadataEnhanceOptions settings) =>
        string.IsNullOrWhiteSpace(settings.MetadataBuildOptions.MetadataCacheRootFolder) ? defaultCacheRoot : settings.MetadataBuildOptions.MetadataCacheRootFolder;

    /// <summary>Original candidate query: Movie/Series/Season with Douban id and premiere inside the lookback window, newest first, then the per-item cache-age predicate.</summary>
    public BaseItem[] SelectRatingCandidates(MetadataEnhanceOptions settings, CancellationToken token)
    {
        var now = Clock();
        if (RatingRefreshRules.WindowStart(settings.MetadataRefreshOptions.RatingUpdateLookbackDays, now) is not { } start) return Array.Empty<BaseItem>();
        if (!Enum.TryParse<CacheMode>(settings.MetadataBuildOptions.MetadataCacheMode, true, out var mode) || mode == CacheMode.None) return Array.Empty<BaseItem>();
        DoubanCache cache;
        try { cache = new DoubanCache(paths, CacheRoot(settings)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return Array.Empty<BaseItem>(); }
        return library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { "Movie", "Series", "Season" }, Recursive = true, IsVirtualItem = false,
            HasAnyProviderId = new[] { DoubanExternalId.StaticName }, MinPremiereDate = start, MaxPremiereDate = now,
            OrderBy = new[] { (ItemSortBy.PremiereDate, SortOrder.Descending) }
        }, token).Where(item => RatingRefreshRules.IsCandidateType(item) && DoubanSubjectParser.IsSubjectId(item.GetProviderId(DoubanExternalId.StaticName)) &&
            RatingRefreshRules.InWindow(item.PremiereDate, start, now) &&
            RatingRefreshRules.IsDue(item.CommunityRating, item.PremiereDate, CacheWriteTime(cache, item.GetProviderId(DoubanExternalId.StaticName)!), start, now)).ToArray();
    }

    private static DateTimeOffset? CacheWriteTime(DoubanCache cache, string id)
    {
        try { var path = cache.FilePath(id); return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public async Task<RatingBatchResult> UpdateRatingsAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress, CancellationToken token, QueueManager? queue = null)
    {
        var snapshot = options();
        int community = 0, critic = 0, unchanged = 0, failed = 0, done = 0;
        progress.Report(0);
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = queue == null ? await UpdateRatingAsync(item, snapshot, token).ConfigureAwait(false) :
                    await queue.RunSingle(item.InternalId, ct => UpdateRatingAsync(item, snapshot, ct), token).ConfigureAwait(false);
                if (result.Community) community++;
                if (result.Critic) critic++;
                if (result.Failed) failed++;
                else if (!result.Community && !result.Critic) unchanged++;
                logger.Info("[StrmAssistantRebuild] Rating item {0}: {1}", item.InternalId, result.Status);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { failed++; logger.Warn("[StrmAssistantRebuild] Rating item {0}: failed", item.InternalId); }
            progress.Report(++done * 100d / items.Count);
        }
        progress.Report(100);
        logger.Info("[StrmAssistantRebuild] Rating refresh: community {0}, critic {1}, unchanged {2}, failed {3}", community, critic, unchanged, failed);
        return new(items.Count, community, critic, unchanged, failed);
    }

    /// <summary>Caller owns the item lock. Douban rating is fetched online (cache refreshed); MDBList is optional and never fabricates a score.</summary>
    internal async Task<RatingItemResult> UpdateRatingAsync(BaseItem item, MetadataEnhanceOptions settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!RatingRefreshRules.IsCandidateType(item)) return new(false, false, "unsupported");
        if (item.IsLocked) return new(false, false, "locked");
        var id = item.GetProviderId(DoubanExternalId.StaticName);
        if (!DoubanSubjectParser.IsSubjectId(id)) return new(false, false, "invalid-identity");
        var imdb = item.GetProviderId(MetadataProviders.Imdb);
        imdb = DoubanSubjectParser.IsImdbId(imdb) ? imdb : null;
        bool Can(MetadataFields field) => !(item.LockedFields ?? Array.Empty<MetadataFields>()).Contains(field);
        var parts = new List<string>();
        float? douban = null, tomatoes = null;
        if (!Enum.TryParse<CacheMode>(settings.MetadataBuildOptions.MetadataCacheMode, true, out var mode) || !Enum.IsDefined(typeof(CacheMode), mode))
            return new(false, false, "invalid-cache-mode");
        var rating = DoubanScope.Parse(settings.DoubanAssistScrapeScope).Contains("Rating") && settings.EnableDoubanAssistScraping;
        if (rating && Can(MetadataFields.CommunityRating))
        {
            DoubanLookup lookup;
            try
            {
                var cache = new DoubanCache(paths, CacheRoot(settings));
                // Rating refresh is due precisely because the cache is old: always online, then refresh the cache entry.
                lookup = await cache.ResolveAsync(id, imdb, mode == CacheMode.None ? CacheMode.None : CacheMode.PreferOnlineWithCache,
                    ct => client.GetAsync(id, imdb, settings.MetadataRefreshOptions, ct), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { lookup = new(null, "cache-path-error"); }
            if (lookup.Subject is { } subject && DoubanSubjectParser.MatchesIdentity(subject, id, imdb)) { douban = subject.Rating; parts.Add(douban == null ? "douban-no-rating" : "douban"); }
            else parts.Add("douban-" + lookup.Status);
        }
        else parts.Add(rating ? "community-locked" : "douban-rating-off");
        var key = settings.MetadataRefreshOptions.MdbListApiKey;
        if (!string.IsNullOrWhiteSpace(key) && Can(MetadataFields.CriticRating))
        {
            var path = RatingRefreshRules.MdbListPath(item, imdb);
            if (path == null) parts.Add("mdblist-no-imdb");
            else
            {
                var result = await MdbList.GetRottenTomatoesAsync(path, key, token).ConfigureAwait(false);
                tomatoes = result.Score; parts.Add("mdblist-" + result.Status);
            }
        }
        token.ThrowIfCancellationRequested();
        if (item.IsLocked) return new(false, false, "locked");
        var oldCommunity = item.CommunityRating; var oldCritic = item.CriticRating;
        var community = douban is >= 0 and <= 10 && Can(MetadataFields.CommunityRating) && item.CommunityRating != douban;
        var critic = tomatoes is >= 0 and <= 100 && Can(MetadataFields.CriticRating) && item.CriticRating != tomatoes;
        if (community) item.CommunityRating = douban;
        if (critic) item.CriticRating = tomatoes;
        if (community || critic)
        {
            try { library.UpdateItems(new List<BaseItem> { item }, null, ItemUpdateType.MetadataEdit, true, false, null, token); }
            catch { item.CommunityRating = oldCommunity; item.CriticRating = oldCritic; throw; }
        }
        return new(community, critic, string.Join(":", parts));
    }
    public static List<PersonInfo> MergePeople(List<PersonInfo> existing, IEnumerable<DoubanPerson> incoming)
    {
        var result = existing.Select(p => new PersonInfo { Name = p.Name, Role = p.Role, Type = p.Type, ImageUrl = p.ImageUrl, ProviderIds = p.ProviderIds, Id = p.Id, ImageInfos = p.ImageInfos }).ToList();
        foreach (var person in incoming)
        {
            if (string.IsNullOrWhiteSpace(person.Name) || !Enum.TryParse<PersonType>(person.Type, out var type)) continue;
            var found = result.FirstOrDefault(p => p.Name == person.Name && p.Type == type);
            if (found == null) { found = new PersonInfo { Name = person.Name, Type = type }; result.Add(found); }
            if (!string.IsNullOrWhiteSpace(person.Role)) found.Role = person.Role;
            if (DoubanSubjectParser.ImageUrl(person.ImageUrl) is { } image) found.ImageUrl = image;
        }
        return result;
    }
    private static bool SamePeople(List<PersonInfo> a, List<PersonInfo> b) => a.Count == b.Count && a.Zip(b).All(p => p.First.Name == p.Second.Name && p.First.Role == p.Second.Role && p.First.Type == p.Second.Type && p.First.ImageUrl == p.Second.ImageUrl);
    private async Task<bool> SavePosterAsync(BaseItem item, string url, CancellationToken token)
    {
        if (files == null) return false;
        // The host's explicit saveLocallyWithMedia:false overload writes only internal metadata.
        paths.RequireWritablePath(item.GetInternalMetadataPath());
        var image = await client.GetImageAsync(url, token).ConfigureAwait(false);
        var directory = paths.RequireWritablePath(Path.Combine(defaultCacheRoot, "douban", "image-work"));
        Directory.CreateDirectory(directory);
        var temp = paths.RequireWritablePath(Path.Combine(directory, Guid.NewGuid().ToString("N") + (image.Mime == "image/png" ? ".png" : ".jpg")));
        try
        {
            await File.WriteAllBytesAsync(temp, image.Data, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (item.IsLocked || item.HasImage(ImageType.Primary, 0)) return false;
            var previous = item.ImageInfos.ToArray();
            try
            {
                await provider.SaveImage(item, new LibraryOptions { SaveLocalMetadata = false }, paths.RequireWritablePath(temp), image.Mime.AsMemory(),
                    ImageType.Primary, 0, false, Array.Empty<long>(), new DirectoryService(logger, files), true, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!item.HasImage(ImageType.Primary, 0)) throw new InvalidDataException("Native image save returned without a Primary image.");
                library.UpdateImages(item); // Native ImageSaver changes in-memory ImageInfos; this commits the repository row.
                return true;
            }
            catch { item.ImageInfos = previous; throw; }
        }
        finally { if (File.Exists(temp)) File.Delete(paths.RequireWritablePath(temp)); }
    }
}
