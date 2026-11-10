using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using StrmAssistantRebuild.IntroSkip;

namespace StrmAssistantRebuild.Common;

public sealed partial class ChapterApi
{
    public const string MarkerSuffix = "#SA";
    private readonly ILibraryManager library;
    private readonly IItemRepository repository;
    private readonly QueueManager queue;
    private readonly ILogger logger;
    private readonly Func<BaseItem, CancellationToken, Task>? persist;
    private readonly Func<BaseItem, Func<CancellationToken, Task>, CancellationToken, Task>? persistMutation;
    public ChapterApi(ILibraryManager library, IItemRepository repository, QueueManager queue, ILogger logger,
        Func<BaseItem, CancellationToken, Task>? persist = null,
        Func<BaseItem, Func<CancellationToken, Task>, CancellationToken, Task>? persistMutation = null)
    { this.library = library; this.repository = repository; this.queue = queue; this.logger = logger; this.persist = persist; this.persistMutation = persistMutation; }

    public IntroMarkerSet GetMarkers(BaseItem item, CancellationToken token = default)
    {
        var chapters = repository.GetChapters(item, token);
        return new(chapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart)?.StartPositionTicks,
            chapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd)?.StartPositionTicks,
            chapters.FirstOrDefault(c => c.MarkerType == MarkerType.CreditsStart)?.StartPositionTicks);
    }
    public Task ApplyAsync(BaseItem item, IntroMarkerSet markers, bool overwrite, CancellationToken token, string origin = "Playback") =>
        queue.RunSingle(item.InternalId, async ct =>
        {
            markers.Validate(item.RunTimeTicks);
            var existing = repository.GetChapters(item, ct);
            var merged = Merge(existing, markers, overwrite);
            ct.ThrowIfCancellationRequested();
            await CommitAsync(item, merged, ct).ConfigureAwait(false);
            logger.Info("[StrmAssistantRebuild] {0} markers saved for item {1}.", origin, item.InternalId);
            return true;
        }, token);

    public static List<ChapterInfo> Merge(IEnumerable<ChapterInfo> original, IntroMarkerSet markers, bool overwrite)
    {
        var result = original.Select(Clone).ToList();
        bool CanReplace(params MarkerType[] types) => overwrite || !result.Any(c => types.Contains(c.MarkerType) && !IsOwned(c));
        if (markers.HasIntro && CanReplace(MarkerType.IntroStart, MarkerType.IntroEnd))
        {
            result.RemoveAll(c => c.MarkerType is MarkerType.IntroStart or MarkerType.IntroEnd);
            result.Add(Marker(MarkerType.IntroStart, markers.IntroStartTicks!.Value));
            result.Add(Marker(MarkerType.IntroEnd, markers.IntroEndTicks!.Value));
        }
        if (markers.HasCredits && CanReplace(MarkerType.CreditsStart))
        {
            result.RemoveAll(c => c.MarkerType == MarkerType.CreditsStart);
            result.Add(Marker(MarkerType.CreditsStart, markers.CreditsStartTicks!.Value));
        }
        return result.OrderBy(c => c.StartPositionTicks).ToList();
    }
    private static ChapterInfo Marker(MarkerType type, long ticks) => new() { MarkerType = type, StartPositionTicks = ticks, Name = type + MarkerSuffix };
    public static bool IsOwned(ChapterInfo marker) => marker.Name?.EndsWith(MarkerSuffix, StringComparison.Ordinal) == true;
    private static ChapterInfo Clone(ChapterInfo c) => new()
    {
        Name = c.Name, StartPositionTicks = c.StartPositionTicks, MarkerType = c.MarkerType,
        ChapterIndex = c.ChapterIndex, ImagePath = c.ImagePath, ImageTag = c.ImageTag, ImageDateModified = c.ImageDateModified
    };
    public Task ClearAsync(BaseItem item, CancellationToken token) => queue.RunSingle(item.InternalId, async ct =>
    {
        var chapters = repository.GetChapters(item, ct).Where(c => c.MarkerType is not (MarkerType.IntroStart or MarkerType.IntroEnd or MarkerType.CreditsStart)).Select(Clone).ToList();
        ct.ThrowIfCancellationRequested();
        await CommitAsync(item, chapters, ct).ConfigureAwait(false);
        return true;
    }, token);

    /// <summary>Explicit intro replacement only; persistence wrappers may still invalidate/export cache on a no-op.</summary>
    public async Task ClearIntroAsync(BaseItem item, CancellationToken token, Action<BaseItem, CancellationToken>? validateAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        token.ThrowIfCancellationRequested();
        var itemId = item.InternalId;
        if (itemId <= 0) throw new ArgumentException("Intro clearing requires a persisted item ID.", nameof(item));
        await queue.RunSingle(itemId, async ct =>
        {
            void Validate(CancellationToken writeToken)
            {
                ct.ThrowIfCancellationRequested();
                writeToken.ThrowIfCancellationRequested();
                if (item.InternalId != itemId) throw new InvalidOperationException("Chapter item identity changed.");
                validateAuthority?.Invoke(item, writeToken);
                if (item.InternalId != itemId) throw new InvalidOperationException("Chapter item identity changed.");
                ct.ThrowIfCancellationRequested();
                writeToken.ThrowIfCancellationRequested();
            }
            Validate(ct);
            Task Save(CancellationToken writeToken)
            {
                Validate(writeToken);
                // Read inside the persistence gate, not before it: credits may have changed while waiting.
                var current = repository.GetChapters(item, writeToken);
                Validate(writeToken); // Also catches a repository read that mutates this same item instance.
                var hasIntro = current.Any(c => c.MarkerType is MarkerType.IntroStart or MarkerType.IntroEnd);
                ct.ThrowIfCancellationRequested();
                writeToken.ThrowIfCancellationRequested();
                if (item.InternalId != itemId) throw new InvalidOperationException("Chapter item identity changed.");
                // Native full-list SaveChapters renumbers rows and resets intro failure data. Delete only these two types.
                if (hasIntro) repository.DeleteChapters(itemId, new[] { MarkerType.IntroStart, MarkerType.IntroEnd });
                return Task.CompletedTask;
            }
            if (persistMutation != null) await persistMutation(item, Save, ct).ConfigureAwait(false);
            else { await Save(ct).ConfigureAwait(false); if (persist != null) await persist(item, ct).ConfigureAwait(false); }
            return true;
        }, token).ConfigureAwait(false);
    }

    private async Task CommitAsync(BaseItem item, List<ChapterInfo> chapters, CancellationToken token)
    {
        Task Save(CancellationToken ct) { ct.ThrowIfCancellationRequested(); repository.SaveChapters(item.InternalId, chapters); return Task.CompletedTask; }
        if (persistMutation != null) await persistMutation(item, Save, token).ConfigureAwait(false);
        else { await Save(token).ConfigureAwait(false); if (persist != null) await persist(item, token).ConfigureAwait(false); }
    }

    public async Task ApplyBehaviorAsync(Episode source, IntroMarkerSet markers, bool overwrite, CancellationToken token)
    {
        markers.Validate(source.RunTimeTicks);
        var episodes = source.ParentId > 0 && source.IndexNumber.HasValue
            ? library.GetItemList(new InternalItemsQuery { ParentIds = new[] { source.ParentId }, IncludeItemTypes = new[] { nameof(Episode) }, Recursive = false, GroupByPresentationUniqueKey = false }, token).OfType<Episode>().ToArray()
            : new[] { source };
        var creditsDuration = source.RunTimeTicks - markers.CreditsStartTicks;
        foreach (var episode in episodes.Append(source).DistinctBy(e => e.InternalId))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (episode.RunTimeTicks is not > 0) continue;
                var current = repository.GetChapters(episode, token);
                var sameIndex = episode.InternalId == source.InternalId || source.IndexNumber.HasValue && episode.IndexNumber == source.IndexNumber;
                var earlier = !sameIndex && episode.IndexNumber < source.IndexNumber;
                var introAllowed = markers.HasIntro && (sameIndex || earlier &&
                    (!current.Any(c => c.MarkerType == MarkerType.IntroStart) || !current.Any(c => c.MarkerType == MarkerType.IntroEnd)) ||
                    !earlier && !sameIndex && (overwrite || !current.Any(c => c.MarkerType is MarkerType.IntroStart or MarkerType.IntroEnd && !IsOwned(c))));
                var creditsAllowed = markers.HasCredits && (sameIndex || earlier && !current.Any(c => c.MarkerType == MarkerType.CreditsStart) ||
                    !earlier && !sameIndex && (overwrite || !current.Any(c => c.MarkerType == MarkerType.CreditsStart && !IsOwned(c))));
                var projected = new IntroMarkerSet(introAllowed ? markers.IntroStartTicks : null, introAllowed ? markers.IntroEndTicks : null,
                    creditsAllowed && creditsDuration is > 0 ? episode.RunTimeTicks - creditsDuration : null);
                if (!projected.HasIntro && !projected.HasCredits) continue;
                // Eligibility above preserves earlier/native markers. Current episode follows the source's explicit reset semantics.
                await ApplyAsync(episode, projected, true, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.Warn("[StrmAssistantRebuild] Marker propagation item {0} failed: {1}", episode.InternalId, ex.Message); }
        }
    }
    public async Task PopulateFromSeasonAsync(Episode target, CancellationToken token)
    {
        if (target.ParentId <= 0 || target.RunTimeTicks is not > 0) return;
        var siblings = library.GetItemList(new InternalItemsQuery { ParentIds = new[] { target.ParentId }, IncludeItemTypes = new[] { nameof(Episode) }, Recursive = false, GroupByPresentationUniqueKey = false }, token)
            .OfType<Episode>().Where(e => e.InternalId != target.InternalId).OrderByDescending(e => e.IndexNumber);
        long? start = null, end = null, credits = null;
        foreach (var sibling in siblings)
        {
            token.ThrowIfCancellationRequested();
            var chapters = repository.GetChapters(sibling, token).Where(IsOwned).ToArray();
            var first = chapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart);
            var last = chapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd);
            if (!end.HasValue && first != null && last != null) { start = first.StartPositionTicks; end = last.StartPositionTicks; }
            var outro = chapters.FirstOrDefault(c => c.MarkerType == MarkerType.CreditsStart);
            if (!credits.HasValue && outro != null && sibling.RunTimeTicks > outro.StartPositionTicks)
                credits = target.RunTimeTicks - (sibling.RunTimeTicks - outro.StartPositionTicks);
            if (end.HasValue && credits.HasValue) break;
        }
        if (end.HasValue || credits.HasValue) await ApplyAsync(target, new(start, end, credits), false, token, "Catchup").ConfigureAwait(false);
    }
    public async Task ClearFollowingAsync(Episode source, CancellationToken token)
    {
        var items = source.ParentId > 0 ? library.GetItemList(new InternalItemsQuery { ParentIds = new[] { source.ParentId }, IncludeItemTypes = new[] { nameof(Episode) }, Recursive = false, GroupByPresentationUniqueKey = false }, token).OfType<Episode>() : new[] { source };
        foreach (var episode in items.Where(e => !source.IndexNumber.HasValue || e.IndexNumber >= source.IndexNumber))
        {
            token.ThrowIfCancellationRequested();
            try { await ClearAsync(episode, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.Warn("[StrmAssistantRebuild] Clear markers item {0} failed: {1}", episode.InternalId, ex.Message); }
        }
    }
}
