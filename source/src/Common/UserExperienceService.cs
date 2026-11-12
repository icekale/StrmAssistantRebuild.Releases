using System.Threading.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

/// <summary>Public playback persistence events only; never edits library identity or shared item fields.</summary>
public sealed class UserExperienceService : IDisposable
{
    private readonly ILibraryManager library;
    private readonly IUserDataManager data;
    private readonly IUserManager users;
    private readonly ISessionManager sessions;
    private readonly ILogger logger;
    private readonly Action<PlaybackStopEventArgs>? onPlaybackStop;
    public bool IsPlaybackStopForwardingActive => onPlaybackStop != null && Volatile.Read(ref disposed) == 0;
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(128)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();
    private bool enabled;
    private int threshold = 50, generation, disposed;
    private long dropped;
    private sealed record Work(long UserId, long ItemId, long Position, bool Completed, int Generation, TaskCompletionSource<bool>? Barrier = null);
    public Task Completion { get; }
    public long DroppedEventCount => Interlocked.Read(ref dropped);

    public UserExperienceService(ILibraryManager library, IUserDataManager data, IUserManager users,
        ISessionManager sessions, ILogger logger, Action<PlaybackStopEventArgs>? onPlaybackStop = null)
    {
        this.library = library; this.data = data; this.users = users; this.sessions = sessions; this.logger = logger;
        this.onPlaybackStop = onPlaybackStop;
        Completion = Task.Run(RunAsync);
        // The persisted event proves a successful unfinished stop. For completion, use the native
        // stop result rather than an old Played flag (replays may retain that flag below threshold).
        data.UserDataSaved += OnUserDataSaved;
        sessions.PlaybackStopped += OnPlaybackStopped;
    }

    public void Configure(ExperienceEnhanceOptions options, Version hostVersion)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (gate)
        {
            if (disposed != 0) return;
            generation++;
            enabled = options.MarkPreviousEpisodesPlayed;
            threshold = Math.Clamp(options.MarkPreviousEpisodesPlayedThreshold, 5, 95);
        }
        // M153: LibraryScopePlayProgress is applied by LibraryScopePlayProgressControl (Plugin.ConfigureUserExperience).
        lock (gate)
            if (disposed == 0) UserExperienceGetterCompatibility.Configure(hostVersion, options, logger, library);
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.SaveReason != UserDataSaveReason.PlaybackFinished || e.Item is not Episode || e.User == null || e.UserData == null ||
            e.UserData.PlaybackPositionTicks <= 0) return;
        Enqueue(e.User.InternalId, e.Item.InternalId, e.UserData.PlaybackPositionTicks, false);
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        // Independent producer: snapshot/queue only, before the unrelated completed-Episode rule.
        // The callback must not perform source resolution, library queries or native refresh on this thread.
        if (IsPlaybackStopForwardingActive)
            try { onPlaybackStop!(e); }
            catch (Exception error) { logger.Warn("[StrmAssistantRebuild] Alternative stop capture: {0}", error.GetType().Name); }
        // In the verified SessionManager failed stops always have PlayedToCompletion=false.
        // The threshold branch never trusts this event, whose public contract omits Failed.
        if (!e.PlayedToCompletion || !e.PlaybackPositionTicks.HasValue || e.Item is not Episode || e.Users == null) return;
        foreach (var user in e.Users)
            if (user != null) Enqueue(user.InternalId, e.Item.InternalId, 0, true);
    }

    private void Enqueue(long userId, long itemId, long position, bool completed)
    {
        lock (gate)
        {
            if (!enabled || disposed != 0) return;
            if (!queue.Writer.TryWrite(new(userId, itemId, position, completed, generation))) Interlocked.Increment(ref dropped);
        }
    }

    internal async Task DrainAsync(CancellationToken token = default)
    {
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new(0, 0, 0, false, 0, barrier), token).ConfigureAwait(false);
        await barrier.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
            {
                if (work.Barrier != null) { work.Barrier.TrySetResult(true); continue; }
                try { Process(work, shutdown.Token); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
                catch (Exception error) { logger.Warn("[StrmAssistantRebuild] Previous episode update failed: {0}", error.Message); }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        finally
        {
            while (queue.Reader.TryRead(out var work)) work.Barrier?.TrySetCanceled();
        }
    }

    private void Process(Work work, CancellationToken token)
    {
        int selectedThreshold;
        lock (gate)
        {
            if (disposed != 0 || !enabled || work.Generation != generation) return;
            selectedThreshold = threshold;
        }
        if (work.UserId <= 0 || work.ItemId <= 0) return;
        var user = users.GetUserById(work.UserId);
        if (user?.InternalId != work.UserId || user.Policy?.IsDisabled != false || library.GetItemById(work.ItemId) is not Episode current ||
            current.InternalId != work.ItemId || current.IndexNumber is not > 1 || current.AlbumId <= 0) return;
        var folders = library.GetCollectionFolders(current, token);
        var options = library.GetLibraryOptions(current, folders);
        var min = Math.Clamp(options.MinResumePct, 0, 100);
        var max = Math.Clamp(options.MaxResumePct, min, 100);
        selectedThreshold = Math.Clamp(selectedThreshold, min, max);
        var runtime = current.GetRunTimeTicksForPlayState();
        // Native completion is authoritative. An unfinished persisted stop must exceed the
        // threshold, not merely reach it; no division by unknown/zero duration.
        if (!work.Completed &&
            !(work.Position > 0 && runtime > 0 && (double)work.Position / runtime * 100 > selectedThreshold)) return;
        if (!library.FilterItemsToIdsForUser(new[] { current }, user, null, token).Contains(current.InternalId)) return;
        var season = current.Season; // Native display-season relation is AlbumId, not physical ParentId.
        if (season == null || season.InternalId != current.AlbumId) return;
        var seasonNumber = season.IndexNumber;
        var seasonKey = season.PresentationUniqueKey;
        if (seasonNumber is not > 0 || string.IsNullOrWhiteSpace(seasonKey)) return;
        var series = season.Series;
        if (series == null || series.InternalId <= 0 || (season.SeriesId > 0 && series.InternalId != season.SeriesId)) return;
        var seriesKey = series.PresentationUniqueKey;
        if (string.IsNullOrWhiteSpace(seriesKey)) return;
        // Do not calculate replacement keys or fall back to an unscoped physical query. The native
        // method supplies Series PUK + display-season/Album PUK conditions and retains the user.
        token.ThrowIfCancellationRequested();
        var query = new InternalItemsQuery(user)
        {
            IsVirtualItem = false, HasIndexNumber = true,
            EnableTotalRecordCount = false, EnableAutoSort = false
        };
        var previous = season.GetEpisodes(query, token).Items ?? Array.Empty<BaseItem>();
        var actualSeason = query.SortParentIndexNumberOrAlbumWithPresentationUniqueKey;
        // Native helpers resolve the Series again. Never write results from a different/empty scope
        // if cached objects changed between admission and query construction.
        if (!string.Equals(query.SeriesPresentationUniqueKey, seriesKey, StringComparison.Ordinal) || actualSeason?.Item1 != seasonNumber ||
            !string.Equals(actualSeason?.Item2, seasonKey, StringComparison.Ordinal)) return;
        foreach (var item in previous.OfType<Episode>())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (item.InternalId <= 0 || !item.IndexNumber.HasValue || item.IndexNumber >= current.IndexNumber || item.InternalId == current.InternalId) continue;
                if (!library.FilterItemsToIdsForUser(new[] { item }, user, null, token).Contains(item.InternalId)) continue;
                var original = data.GetUserData(user, item, token);
                // The configured feature covers unplayed prior episodes OR any remaining progress,
                // including a replay of an already-played episode. Only played + zero is a no-op.
                if (original == null || (original.Played && original.PlaybackPositionTicks == 0)) continue;
                var updated = Copy(original);
                updated.Played = true; updated.PlaybackPositionTicks = 0;
                updated.PlaystateLastModified = DateTimeOffset.UtcNow;
                lock (gate)
                    if (disposed != 0 || !enabled || work.Generation != generation) return;
                token.ThrowIfCancellationRequested();
                // A toggle reason prevents our writes recursively producing playback-finished work.
                data.SaveUserData(user, item, updated, UserDataSaveReason.TogglePlayed, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { logger.Warn("[StrmAssistantRebuild] Previous episode {0} failed: {1}", item.InternalId, error.Message); }
        }
    }

    private static UserItemData Copy(UserItemData value) => new()
    {
        Key = value.Key, Rating = value.Rating, PlaybackPositionTicks = value.PlaybackPositionTicks,
        PlayCount = value.PlayCount, IsFavorite = value.IsFavorite, HideFromResume = value.HideFromResume,
        LastPlayedDate = value.LastPlayedDate, Played = value.Played, AudioStreamIndex = value.AudioStreamIndex,
        SubtitleStreamIndex = value.SubtitleStreamIndex, RatingLastModified = value.RatingLastModified,
        PlaystateLastModified = value.PlaystateLastModified, DateLastSearched = value.DateLastSearched
    };

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed != 0) return;
            disposed = 1; enabled = false; generation++;
            data.UserDataSaved -= OnUserDataSaved;
            sessions.PlaybackStopped -= OnPlaybackStopped;
            queue.Writer.TryComplete(); shutdown.Cancel();
        }
        UserExperienceGetterCompatibility.Disable();
    }
}
