using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

/// <summary>Database-only grouping through the host's public library API.</summary>
public sealed class MultiVersionService
{
    private readonly ILibraryManager library;
    private readonly QueueManager queue;
    private readonly ILogger logger;
    private readonly Func<ExperienceEnhanceOptions> options;
    private const int InspectionBudget = 10000;
    // ponytail: serialize whole version batches; the host merge API has no transaction/lock contract.
    private static readonly SemaphoreSlim batchGate = VersionMutationGate.Shared;

    public MultiVersionService(ILibraryManager libraryManager, QueueManager queueManager, ILogger logger,
        Func<ExperienceEnhanceOptions> getOptions)
    { library = libraryManager; queue = queueManager; this.logger = logger; options = getOptions; }

    public async Task<BatchProgress> MergeAsync(IProgress<double> progress, CancellationToken token)
    {
        await batchGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            var settings = options();
            if (!settings.MergeMultiVersion) { progress.Report(100); return new BatchProgress(); }
            var items = Select(new[] { nameof(Movie), nameof(Series), nameof(Episode) }, token);
            var buckets = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var description = Describe(item, settings, token);
                    if (description == null) continue;
                    foreach (var identity in IdentityKeys(description.Value))
                    {
                        var key = description.Value.Scope + "|" + description.Value.Kind + "|" + identity;
                        if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new List<long>();
                        bucket.Add(item.InternalId);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.Warn("[MultiVersion] Skipped item {0}: {1}", item.InternalId, ex.Message); }
            }
            var groups = buckets.Values.Select(g => g.Distinct().OrderBy(id => id).ToArray())
                .Where(g => g.Length > 1).DistinctBy(g => string.Join(",", g)).ToArray();
            return await queue.RunBatch(groups, (ids, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var candidates = ids.Select(id => library.GetItemById(id) ?? throw new InvalidOperationException($"Missing item {id}.")).ToArray();
                var group = candidates[0] is Series ? SeriesClosure(candidates, ct) : AlternateClosure(candidates, ct);
                if (!ValidGroup(group, settings, ct))
                {
                    logger.Warn("[MultiVersion] Skipped group {0}: identity, scope, or grouping limit mismatch.", string.Join(",", ids));
                    return Task.CompletedTask;
                }
                ValidateFolderLimits(group, settings, ct);
                ct.ThrowIfCancellationRequested();
                if (group[0] is Series) MergeSeries(group, ct);
                else if (!SamePresentation(group))
                {
                    library.MergeItems(group);
                    VerifyMerged(group, ct);
                }
                return Task.CompletedTask;
            }, progress, token, true, _ => 0).ConfigureAwait(false);
        }
        finally { batchGate.Release(); }
    }

    public async Task<BatchProgress> SplitMoviesAsync(IProgress<double> progress, CancellationToken token)
    {
        await batchGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            var movies = Select(new[] { nameof(Movie) }, token).OfType<Movie>()
                .Where(movie => { token.ThrowIfCancellationRequested(); return movie.GetAlternateVersionIds().Count > 0; })
                .GroupBy(movie => movie.PresentationUniqueKey, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            return await queue.RunBatch(movies, (movie, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var current = library.GetItemById(movie.InternalId) as Movie;
                if (current == null || current.GetAlternateVersionIds().Count == 0) return Task.CompletedTask;
                var closure = AlternateClosure(new[] { current }, ct);
                if (closure.Any(item => item is not Movie))
                    throw new InvalidOperationException("Split movie closure includes a non-movie item.");
                // Split all members explicitly: even an asymmetric pre-existing chain is fully separated.
                foreach (var member in closure)
                {
                    ct.ThrowIfCancellationRequested();
                    if (((Video)member).GetAlternateVersionIds().Count > 0) library.SplitItems(member);
                }
                var readback = closure.Select(member => library.GetItemById(member.InternalId) as Movie ??
                    throw new InvalidOperationException($"Missing split movie {member.InternalId}.")).ToArray();
                if (readback.Any(member => member.GetAlternateVersionIds().Count > 0) ||
                    readback.Select(member => member.PresentationUniqueKey).Distinct(StringComparer.Ordinal).Count() != readback.Length)
                    throw new InvalidOperationException("Host split readback still contains linked movie versions.");
                return Task.CompletedTask;
            }, progress, token, true, _ => 0).ConfigureAwait(false);
        }
        finally { batchGate.Release(); }
    }

    private BaseItem[] Select(string[] types, CancellationToken token) => library.GetItemList(new InternalItemsQuery
    {
        IncludeItemTypes = types, Recursive = true, GroupByPresentationUniqueKey = false,
        EnableTotalRecordCount = false
    }, token).DistinctBy(item => item.InternalId).ToArray();

    private BaseItem[] AlternateClosure(IEnumerable<BaseItem> seeds, CancellationToken token)
    {
        var found = new Dictionary<long, BaseItem>();
        var pending = new Queue<BaseItem>(seeds);
        var queued = pending.Select(item => item.InternalId).ToHashSet();
        if (queued.Count > InspectionBudget) throw new InvalidOperationException("Alternate closure exceeds inspection budget.");
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var item = pending.Dequeue();
            if (!found.TryAdd(item.InternalId, item)) continue;
            if (item is not Video video) throw new InvalidOperationException("Alternate version is not a video.");
            foreach (var id in video.GetAlternateVersionIds())
            {
                token.ThrowIfCancellationRequested();
                if (!queued.Add(id)) continue;
                if (queued.Count > InspectionBudget) throw new InvalidOperationException("Alternate closure exceeds inspection budget.");
                pending.Enqueue(library.GetItemById(id) ?? throw new InvalidOperationException($"Missing alternate version {id}."));
            }
        }
        return found.Values.OrderBy(item => item.InternalId).ToArray();
    }

    private BaseItem[] SeriesClosure(BaseItem[] seeds, CancellationToken token)
    {
        var members = seeds.ToDictionary(item => item.InternalId);
        if (members.Count > InspectionBudget) throw new InvalidOperationException("Series closure exceeds inspection budget.");
        foreach (var key in seeds.Select(item => item.PresentationUniqueKey).Where(key => !string.IsNullOrEmpty(key)).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var linked = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Series) }, PresentationUniqueKey = key,
                Recursive = true, GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false, Limit = InspectionBudget
            }, token);
            if (linked.Length >= InspectionBudget) throw new InvalidOperationException("Series closure reached inspection budget.");
            foreach (var member in linked)
            {
                token.ThrowIfCancellationRequested();
                members[member.InternalId] = member;
                if (members.Count > InspectionBudget) throw new InvalidOperationException("Series closure exceeds inspection budget.");
            }
        }
        return members.Values.OrderBy(item => item.InternalId).ToArray();
    }

    private void VerifyMerged(BaseItem[] group, CancellationToken token)
    {
        var expected = group.Select(item => item.InternalId).ToHashSet();
        var readback = new List<BaseItem>();
        foreach (var member in group)
        {
            token.ThrowIfCancellationRequested();
            if (library.GetItemById(member.InternalId) is not Video video ||
                !video.GetAlternateVersionIds().Append(video.InternalId).ToHashSet().SetEquals(expected))
                throw new InvalidOperationException($"Host merge readback mismatch for {member.InternalId}.");
            readback.Add(video);
        }
        if (!SamePresentation(readback.ToArray())) throw new InvalidOperationException("Host merge readback has different presentation keys.");
    }

    private bool ValidGroup(BaseItem[] group, ExperienceEnhanceOptions settings, CancellationToken token)
    {
        if (group.Length < 2) return false;
        var descriptions = new List<(string Scope, string Kind, string Imdb, string Tmdb)>();
        foreach (var item in group)
        {
            token.ThrowIfCancellationRequested();
            var description = Describe(item, settings, token);
            if (description == null) return false;
            descriptions.Add(description.Value);
        }
        var first = descriptions[0];
        if (descriptions.Any(d => d.Scope != first.Scope || d.Kind != first.Kind)) return false;
        // One shared provider is necessary; any conflicting known provider vetoes the whole group.
        if (descriptions.Where(d => d.Imdb.Length > 0).Select(d => d.Imdb).Distinct().Count() > 1 ||
            descriptions.Where(d => d.Tmdb.Length > 0).Select(d => d.Tmdb).Distinct().Count() > 1) return false;
        return (first.Imdb.Length > 0 && descriptions.All(d => d.Imdb == first.Imdb)) ||
               (first.Tmdb.Length > 0 && descriptions.All(d => d.Tmdb == first.Tmdb));
    }

    private void ValidateFolderLimits(BaseItem[] group, ExperienceEnhanceOptions settings, CancellationToken token)
    {
        // Native VideoListResolver counts all non-extra videos in one directory, not a provider bucket.
        // Series never enter that resolver; Episode is included here by the rebuild's video merge route.
        if (group[0] is not Video) return;
        var limit = Math.Clamp(settings.FolderVideoGroupingLimit, 1, 127);
        var folders = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var item in group)
        {
            token.ThrowIfCancellationRequested();
            var folder = VideoFolder(item.Path) ?? throw new InvalidOperationException("Unknown video directory for grouping limit.");
            if (!folders.TryGetValue(folder, out var ids)) folders[folder] = ids = new HashSet<long>();
            if (!item.ExtraType.HasValue) ids.Add(item.InternalId);
        }
        foreach (var pair in folders)
        {
            token.ThrowIfCancellationRequested();
            if (pair.Value.Count > limit) throw new InvalidOperationException("Per-folder video grouping limit exceeded.");
            // ponytail: bounded prefix census may veto a large subtree; use an exact-directory host query if added.
            var candidates = library.GetItemList(new InternalItemsQuery
            {
                MediaTypes = new[] { "Video" }, HasExtraType = false,
                PathStartsWithAny = new[] { pair.Key, pair.Key.Replace('/', '\\') }.Distinct().ToArray(),
                Recursive = true, GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false,
                Limit = InspectionBudget
            }, token);
            if (candidates.Length >= InspectionBudget) throw new InvalidOperationException("Folder census reached inspection budget.");
            foreach (var item in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (item is Video && !item.ExtraType.HasValue && VideoFolder(item.Path) == pair.Key)
                    pair.Value.Add(item.InternalId);
                if (pair.Value.Count > limit) throw new InvalidOperationException("Per-folder video grouping limit exceeded.");
            }
        }
    }

    private static string? VideoFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal) || path.Any(char.IsControl)) return null;
        path = path.Replace('\\', '/');
        var windows = path.Length > 2 && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) && path[1] == ':' && path[2] == '/';
        var unc = path.StartsWith("//", StringComparison.Ordinal);
        if (!windows && !path.StartsWith('/')) return null;
        var parts = path.Split('/');
        if (parts.Any(part => part is "." or "..") || path.EndsWith('/') ||
            path.Substring(unc ? 2 : windows ? 3 : 1).Contains("//", StringComparison.Ordinal) ||
            (unc && (parts.Length < 5 || parts[2].Length == 0 || parts[3].Length == 0))) return null;
        var folder = path.Substring(0, path.LastIndexOf('/') + 1);
        return windows || unc ? folder.ToUpperInvariant() : folder;
    }

    private (string Scope, string Kind, string Imdb, string Tmdb)? Describe(BaseItem item,
        ExperienceEnhanceOptions settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(item.Path)) return null;
        BaseItem identity = item;
        string kind;
        if (item is Movie) kind = nameof(Movie);
        else if (item is Series) kind = nameof(Series);
        else if (item is Episode episode)
        {
            if (episode.SeriesId <= 0 || episode.ParentIndexNumber is not >= 0 || episode.IndexNumber is not >= 0 ||
                library.GetItemById(episode.SeriesId) is not Series series) return null;
            identity = series;
            kind = $"Episode:{episode.ParentIndexNumber}:{episode.IndexNumber}:{episode.IndexNumberEnd ?? episode.IndexNumber}";
        }
        else return null;
        string scope;
        if (item is Movie)
        {
            switch (settings.MergeMoviesPreference)
            {
                case MultipleVersionsOptions.MergeMoviesScopeOption.FolderScope:
                    var path = item.Path.Replace('\\', '/');
                    var separator = path.LastIndexOf('/');
                    if (separator < 0) return null;
                    scope = "folder:" + path.Substring(0, separator);
                    break;
                case MultipleVersionsOptions.MergeMoviesScopeOption.LibraryScope:
                    scope = LibraryScope(item, token); break;
                case MultipleVersionsOptions.MergeMoviesScopeOption.GlobalScope:
                    scope = "global"; break;
                default: return null;
            }
        }
        else
        {
            switch (settings.MergeSeriesPreference)
            {
                case MultipleVersionsOptions.MergeSeriesScopeOption.LibraryScope:
                    scope = LibraryScope(item, token); break;
                case MultipleVersionsOptions.MergeSeriesScopeOption.GlobalScope:
                    scope = "global"; break;
                default: return null;
            }
        }
        if (scope.Length == 0) return null;
        return (scope, kind, Provider(identity, "Imdb"), Provider(identity, "Tmdb"));
    }

    private string LibraryScope(BaseItem item, CancellationToken token)
    {
        var ids = library.GetCollectionFolders(item, token).Select(folder => folder.InternalId)
            .Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        // Ambiguous multi-library membership must match exactly, not form a bridge across libraries.
        return ids.Length == 0 ? "" : "library:" + string.Join(",", ids);
    }

    private static string Provider(BaseItem item, string name)
    {
        var value = item.ProviderIds.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.Trim() ?? "";
        if (name == "Imdb")
            return value.Length > 2 && value.StartsWith("tt", StringComparison.OrdinalIgnoreCase) &&
                value.Skip(2).All(c => c >= '0' && c <= '9') && value.Skip(2).Any(c => c != '0') ? value.ToLowerInvariant() : "";
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture) : "";
    }

    private static IEnumerable<string> IdentityKeys((string Scope, string Kind, string Imdb, string Tmdb) item)
    {
        if (item.Imdb.Length > 0) yield return "imdb:" + item.Imdb;
        if (item.Tmdb.Length > 0) yield return "tmdb:" + item.Tmdb;
    }

    private static bool SamePresentation(BaseItem[] group) => !string.IsNullOrEmpty(group[0].PresentationUniqueKey) &&
        group.All(item => item.PresentationUniqueKey == group[0].PresentationUniqueKey);

    private void MergeSeries(BaseItem[] group, CancellationToken token)
    {
        var key = SamePresentation(group) ? group[0].PresentationUniqueKey : Guid.NewGuid().ToString("N") + "_";
        var members = group.ToDictionary(item => item.InternalId);
        foreach (var series in group)
        {
            token.ThrowIfCancellationRequested();
            foreach (var child in library.GetItemList(new InternalItemsQuery
            {
                ParentIds = new[] { series.InternalId }, IncludeItemTypes = new[] { nameof(Season), nameof(Episode) },
                Recursive = true, GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false
            }, token))
            {
                token.ThrowIfCancellationRequested();
                if (child.SeriesId == series.InternalId) members[child.InternalId] = child;
            }
        }
        var changes = members.Values.Select(item => (
            Item: item, OldKey: item.PresentationUniqueKey, OldSeriesKey: item.SeriesPresentationUniqueKey,
            // Native Season.CreatePresentationUniqueKey: series key + season number; null stays item-specific.
            Key: item is Series ? key : item is Season season ? season.IndexNumber.HasValue
                ? key + "-" + season.IndexNumber.Value.ToString("000") : season.IdString : item.PresentationUniqueKey,
            SeriesKey: item is Series ? item.SeriesPresentationUniqueKey : key,
            item.ParentId, item.SeriesId, item.InternalAlbumId)).ToArray();
        var changed = changes.Where(entry => entry.OldKey != entry.Key || entry.OldSeriesKey != entry.SeriesKey).ToArray();
        if (changed.Length == 0) return;
        token.ThrowIfCancellationRequested();
        try
        {
            foreach (var entry in changed)
            { entry.Item.PresentationUniqueKey = entry.Key; entry.Item.SeriesPresentationUniqueKey = entry.SeriesKey; }
            // 4.10.2 UpdateItems(false,false) is one repository transaction, with no sidecars/events.
            // Parent/album PUK queries JOIN ParentId/AlbumId to the saved parent; there is no child ParentPUK property.
            // Episode version keys still go through the separately validated native MergeItems path.
            library.UpdateItems(changed.Select(entry => entry.Item).ToList(), null, ItemUpdateType.MetadataEdit,
                false, false, null, token);
            var readback = ReadSeriesMembers(changes.Select(entry => entry.Item.InternalId).ToArray(), token);
            foreach (var entry in changes)
            {
                token.ThrowIfCancellationRequested();
                if (!readback.TryGetValue(entry.Item.InternalId, out var current) || current.GetType() != entry.Item.GetType() ||
                    current.PresentationUniqueKey != entry.Key || current.SeriesPresentationUniqueKey != entry.SeriesKey ||
                    current.ParentId != entry.ParentId || current.SeriesId != entry.SeriesId || current.InternalAlbumId != entry.InternalAlbumId)
                    throw new InvalidOperationException($"Host series merge readback mismatch for {entry.Item.InternalId}.");
            }
        }
        catch
        {
            foreach (var entry in changed)
            { entry.Item.PresentationUniqueKey = entry.OldKey; entry.Item.SeriesPresentationUniqueKey = entry.OldSeriesKey; }
            try
            {
                // Compensate a completed/incorrect write even when cancellation arrived after commit.
                // Re-read full items: restore only our two snapshotted fields, never stale unrelated metadata.
                var readback = ReadSeriesMembers(changed.Select(entry => entry.Item.InternalId).ToArray(), CancellationToken.None);
                var restore = new List<BaseItem>();
                foreach (var entry in changed)
                {
                    if (!readback.TryGetValue(entry.Item.InternalId, out var current))
                        throw new InvalidOperationException($"Missing series rollback member {entry.Item.InternalId}.");
                    if (current.PresentationUniqueKey == entry.OldKey && current.SeriesPresentationUniqueKey == entry.OldSeriesKey) continue;
                    current.PresentationUniqueKey = entry.OldKey;
                    current.SeriesPresentationUniqueKey = entry.OldSeriesKey;
                    restore.Add(current);
                }
                if (restore.Count > 0)
                {
                    library.UpdateItems(restore, null, ItemUpdateType.MetadataEdit, false, false, null, CancellationToken.None);
                    var restored = ReadSeriesMembers(changed.Select(entry => entry.Item.InternalId).ToArray(), CancellationToken.None);
                    if (changed.Any(entry =>
                        !restored.TryGetValue(entry.Item.InternalId, out var current) || current.PresentationUniqueKey != entry.OldKey ||
                        current.SeriesPresentationUniqueKey != entry.OldSeriesKey))
                        throw new InvalidOperationException("Host series rollback readback mismatch.");
                }
            }
            catch (Exception ex) { logger.Warn("[MultiVersion] Series rollback failed: {0}", ex.Message); }
            throw;
        }
    }

    private Dictionary<long, BaseItem> ReadSeriesMembers(long[] ids, CancellationToken token) => library.GetItemList(new InternalItemsQuery
    {
        ItemIds = ids, IncludeItemTypes = new[] { nameof(Series), nameof(Season), nameof(Episode) },
        Recursive = true, GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false
    }, token).ToDictionary(item => item.InternalId);
}
