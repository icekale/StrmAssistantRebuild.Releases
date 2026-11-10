using System.Globalization;
using System.Threading.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.IntroSkip;

/// <summary>Real Emby events -> bounded observation queue -> awaited chapter and verified-outcome callbacks.</summary>
public sealed class PlaySessionMonitor : IDisposable, IAsyncDisposable
{
    private readonly ILibraryManager library;
    private readonly IUserManager users;
    private readonly ISessionManager sessions;
    private readonly Func<IntroSkipOptions> getOptions;
    private readonly Func<BaseItem, CancellationToken, IntroMarkerSet> readMarkers;
    private readonly Func<Episode, IntroMarkerSet, bool, CancellationToken, Task> persist;
    private readonly Func<Episode, long, string?, IntroMarkerSet, CancellationToken, Task>? onUpdated;
    private readonly ILogger logger;
    private readonly Func<DateTime> clock;
    private readonly Channel<Observation> events;
    private readonly CancellationTokenSource stopping = new();
    private readonly object lifecycle = new();
    private readonly Dictionary<string, TrackedSession> tracked = new(StringComparer.Ordinal);
    private readonly Dictionary<(long Item, bool Credits), DateTime> updates = new();
    private readonly int maxSessions;
    private string[] libraryPaths = Array.Empty<string>();
    private HashSet<long> userIds = new();
    private string[] clients = Array.Empty<string>();
    private (string? Library, string? User, string? Client)? scopeKey;
    private bool allUsers, initialized, disposed;
    private long lostEpoch, observedEpoch, dropped;
    private int trackedCount;
    public Task Completion { get; }
    public int TrackedSessionCount => Volatile.Read(ref trackedCount);
    public long DroppedEventCount => Interlocked.Read(ref dropped);

    /// <summary>Call on IntroStore.FileSaved: invalidate queued histories, including an off/on between events.
    /// An already-entered persistence callback finishes under its own locks; Dispose cancels its token.</summary>
    public void ResetObservations() => Interlocked.Increment(ref lostEpoch);

    /// <param name="persist">Must acquire the shared item locks and await real chapter repository writes.
    /// CreditsStartTicks is absolute for this episode; ChapterApi projects remaining duration to siblings.</param>
    public PlaySessionMonitor(ILibraryManager library, IUserManager users, ISessionManager sessions,
        Func<IntroSkipOptions> getOptions, Func<BaseItem, CancellationToken, IntroMarkerSet> readMarkers,
        Func<Episode, IntroMarkerSet, bool, CancellationToken, Task> persist, ILogger logger,
        Func<Episode, long, string?, IntroMarkerSet, CancellationToken, Task>? onUpdated = null)
        : this(library, users, sessions, getOptions, readMarkers, persist, logger, () => DateTime.UtcNow, 2048, 1024, onUpdated) { }

    internal PlaySessionMonitor(ILibraryManager library, IUserManager users, ISessionManager sessions,
        Func<IntroSkipOptions> getOptions, Func<BaseItem, CancellationToken, IntroMarkerSet> readMarkers,
        Func<Episode, IntroMarkerSet, bool, CancellationToken, Task> persist, ILogger logger,
        Func<DateTime> clock, int queueCapacity, int maxSessions,
        Func<Episode, long, string?, IntroMarkerSet, CancellationToken, Task>? onUpdated = null)
    {
        this.library = library; this.users = users; this.sessions = sessions;
        this.getOptions = getOptions; this.readMarkers = readMarkers; this.persist = persist; this.onUpdated = onUpdated;
        this.logger = logger; this.clock = clock; this.maxSessions = Math.Max(1, maxSessions);
        events = Channel.CreateBounded<Observation>(new BoundedChannelOptions(Math.Max(1, queueCapacity))
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        Completion = Task.Run(WorkAsync);
    }

    public void Initialize()
    {
        lock (lifecycle)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PlaySessionMonitor));
            if (initialized) return;
            sessions.PlaybackStart += OnStart;
            sessions.PlaybackProgress += OnProgress;
            sessions.PlaybackStopped += OnStop;
            initialized = true;
        }
    }
    private void OnStart(object? sender, PlaybackProgressEventArgs e) => Capture(e, true, false);
    private void OnProgress(object? sender, PlaybackProgressEventArgs e) => Capture(e, false, false);
    private void OnStop(object? sender, PlaybackStopEventArgs e) => Capture(e, false, true);
    private void Capture(PlaybackProgressEventArgs e, bool start, bool stop)
    {
        try
        {
            if (stopping.IsCancellationRequested || string.IsNullOrEmpty(e.PlaySessionId)) return;
            if (!start && !stop && e.EventName != ProgressEvent.TimeUpdate && e.EventName != ProgressEvent.Pause &&
                e.EventName != ProgressEvent.Unpause && e.EventName != ProgressEvent.PlaybackRateChange) return;
            var item = e.Item as Episode;
            // Copy the relevant event fields now, especially time/position/client/user; Emby can reuse event objects.
            var observation = new Observation(e.PlaySessionId, item, item?.InternalId ?? 0, item?.Path,
                item?.IndexNumber, item?.ParentIndexNumber, item?.RunTimeTicks, e.PlaybackPositionTicks,
                e.Session?.UserInternalId ?? 0, e.ClientName, e.EventName, start, stop, clock(), Interlocked.Read(ref lostEpoch), ServerSessionId: e.Session?.Id);
            if (!events.Writer.TryWrite(observation))
            {
                Interlocked.Increment(ref dropped);
                Interlocked.Increment(ref lostEpoch);
            }
        }
        catch (Exception ex) { Warn("Capture", ex); }
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var e in events.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
            {
                if (e.Barrier != null) { e.Barrier.TrySetResult(true); continue; }
                try { await ProcessAsync(e, stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
                catch (Exception ex) { Remove(e.Id); Warn("Observation", ex); }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) { Warn("Worker", ex); }
        finally
        {
            tracked.Clear(); updates.Clear(); Volatile.Write(ref trackedCount, 0);
            events.Writer.TryComplete();
            while (events.Reader.TryRead(out var pending)) pending.Barrier?.TrySetCanceled();
        }
    }

    private async Task ProcessAsync(Observation e, CancellationToken token)
    {
        var epoch = Interlocked.Read(ref lostEpoch);
        if (epoch != observedEpoch)
        {
            // ponytail: one serial worker; on overflow abandon partial histories rather than infer a false seek.
            tracked.Clear(); updates.Clear(); scopeKey = null; Volatile.Write(ref trackedCount, 0); observedEpoch = epoch;
        }
        if (e.Epoch != epoch) return;
        Prune(e.At);
        try
        {
            if (e.Start) Remove(e.Id);
            var options = getOptions();
            if (!options.EnableIntroSkip) { tracked.Clear(); Volatile.Write(ref trackedCount, 0); return; }
            if (e.Item == null || e.ItemId <= 0 || e.Position is null or < 0 || e.Index == null || !(e.Season > 0)) return;
            if (!e.Start && !e.Stop && e.Position == 0) return;
            RefreshScope(options, e.Start);
            if (!InScope(e.Path, e.User, e.Client)) { Remove(e.Id); return; }
            if (!tracked.TryGetValue(e.Id, out var state) || state.ItemId != e.ItemId)
            {
                if (tracked.Count >= maxSessions) Remove(tracked.MinBy(pair => pair.Value.LastSeen).Key);
                // Missing Start: baseline at the first observed position; never invent an earlier zero-position jump.
                state = new TrackedSession(e.ItemId, new PlaySessionData(options, readMarkers(e.Item, token), e.Position.Value, e.At), e.At);
                tracked[e.Id] = state; Volatile.Write(ref trackedCount, tracked.Count);
            }
            if (e.At < state.LastSeen) return;
            state.LastSeen = e.At;
            if (e.Start) return;
            var update = e.Stop ? state.Data.Stop(e.Position.Value, e.Runtime) :
                state.Data.Progress(e.Kind, e.Position.Value, e.Runtime, e.At);
            if (update == null) return;
            var introAllowed = update.HasIntro && CanUpdate(e.ItemId, false, e.At);
            var creditsAllowed = update.HasCredits && CanUpdate(e.ItemId, true, e.At);
            if (!introAllowed && !creditsAllowed) return;
            update = new IntroMarkerSet(introAllowed ? update.IntroStartTicks : null,
                introAllowed ? update.IntroEndTicks : null, creditsAllowed ? update.CreditsStartTicks : null);
            update.Validate(e.Runtime);
            token.ThrowIfCancellationRequested();
            // Re-read enabled/preference immediately before side effects, not from the playback-start snapshot.
            options = getOptions();
            if (!options.EnableIntroSkip || e.Epoch != Interlocked.Read(ref lostEpoch)) return;
            if (introAllowed) updates[(e.ItemId, false)] = e.At;
            if (creditsAllowed) updates[(e.ItemId, true)] = e.At;
            var before = onUpdated == null ? null : readMarkers(e.Item, token);
            await persist(e.Item, update, PlaySessionData.HasPreference(options, IntroSkipOptions.IntroSkipPreference.ResetAndOverwrite), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var actual = readMarkers(e.Item, token); // actual repository outcome, never a guessed successful write
            state.Data.Markers = actual;
            if (onUpdated != null && before != null && getOptions().EnableIntroSkip && e.Epoch == Interlocked.Read(ref lostEpoch))
            {
                var introChanged = update.HasIntro && actual.HasIntro && actual.IntroStartTicks == update.IntroStartTicks && actual.IntroEndTicks == update.IntroEndTicks &&
                    (actual.IntroStartTicks != before.IntroStartTicks || actual.IntroEndTicks != before.IntroEndTicks);
                var creditsChanged = update.HasCredits && actual.CreditsStartTicks == update.CreditsStartTicks && actual.CreditsStartTicks != before.CreditsStartTicks;
                if (introChanged || creditsChanged)
                {
                    var changed = new IntroMarkerSet(introChanged ? actual.IntroStartTicks : null, introChanged ? actual.IntroEndTicks : null, creditsChanged ? actual.CreditsStartTicks : null);
                    try { await onUpdated(e.Item, e.User, e.ServerSessionId, changed, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) { Warn("Marker notification", error); }
                }
            }
        }
        finally { if (e.Stop) Remove(e.Id); }
    }
    private bool CanUpdate(long item, bool credits, DateTime at) =>
        !updates.TryGetValue((item, credits), out var last) || at - last >= TimeSpan.FromSeconds(10);
    private void Prune(DateTime at)
    {
        // Resource limits are reconstruction hardening, not inferred detection thresholds.
        foreach (var key in tracked.Where(pair => at - pair.Value.LastSeen > TimeSpan.FromHours(6)).Select(pair => pair.Key).ToArray()) Remove(key);
        foreach (var key in updates.Where(pair => at - pair.Value >= TimeSpan.FromSeconds(10)).Select(pair => pair.Key).ToArray()) updates.Remove(key);
        // A flood of distinct stopping sessions must not grow the per-item throttle map indefinitely either.
        while (updates.Count >= maxSessions * 2) updates.Remove(updates.MinBy(pair => pair.Value).Key);
    }
    private void Remove(string id) { tracked.Remove(id); Volatile.Write(ref trackedCount, tracked.Count); }

    private void RefreshScope(IntroSkipOptions options, bool force)
    {
        var key = (options.LibraryScope, options.UserScope, options.ClientScope);
        if (!force && scopeKey == key) return;
        var selectedLibraries = Split(options.LibraryScope);
        var paths = library.GetVirtualFolders()
            .Where(folder => folder.CollectionType == null || string.Equals(folder.CollectionType, "tvshows", StringComparison.OrdinalIgnoreCase))
            .Where(folder => selectedLibraries.Length == 0 || selectedLibraries.Contains(folder.Id, StringComparer.OrdinalIgnoreCase) ||
                selectedLibraries.Contains(folder.ItemId, StringComparer.OrdinalIgnoreCase))
            .SelectMany(folder => folder.Locations ?? Array.Empty<string>()).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();
        var selectedUsers = Split(options.UserScope);
        var allowedUsers = new HashSet<long>();
        if (selectedUsers.Length > 0)
        {
            // Canonical source format: decimal InternalId. GUID support preserves early rebuild UI selections only.
            foreach (var user in users.GetUserList(new UserQuery { IsDisabled = false }))
                if (selectedUsers.Any(value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                        ? id == user.InternalId : Guid.TryParse(value, out var guid) && guid == user.Id)) allowedUsers.Add(user.InternalId);
        }
        libraryPaths = paths; userIds = allowedUsers; allUsers = selectedUsers.Length == 0;
        clients = (options.ClientScope ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length > 0).ToArray();
        scopeKey = key;
    }
    private static string[] Split(string? value) => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private bool InScope(string? path, long user, string? client) =>
        !string.IsNullOrEmpty(path) && libraryPaths.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)) &&
        (allUsers || userIds.Contains(user)) && !string.IsNullOrEmpty(client) &&
        clients.Any(keyword => client.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    private void Warn(string source, Exception ex)
    {
        try { logger.Warn("[StrmAssistantRebuild] Playback {0}: {1}", source, ex.Message); }
        catch { /* Logging must not fault an Emby event or the observed worker. */ }
    }

    internal async Task DrainAsync()
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await events.Writer.WriteAsync(new Observation("", null, 0, null, null, null, null, null, 0, null, default, false, false, default, 0, done), stopping.Token).ConfigureAwait(false);
        await done.Task.WaitAsync(stopping.Token).ConfigureAwait(false);
    }
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed) return;
            disposed = true;
            if (initialized)
            {
                sessions.PlaybackStart -= OnStart;
                sessions.PlaybackProgress -= OnProgress;
                sessions.PlaybackStopped -= OnStop;
            }
            events.Writer.TryComplete();
            try { stopping.Cancel(); } catch (Exception ex) { Warn("Cancellation", ex); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Completion.ConfigureAwait(false);
    }
    private sealed class TrackedSession
    {
        public long ItemId { get; }
        public PlaySessionData Data { get; }
        public DateTime LastSeen { get; set; }
        public TrackedSession(long itemId, PlaySessionData data, DateTime at) { ItemId = itemId; Data = data; LastSeen = at; }
    }
    private sealed record Observation(string Id, Episode? Item, long ItemId, string? Path, int? Index, int? Season,
        long? Runtime, long? Position, long User, string? Client, ProgressEvent Kind, bool Start, bool Stop,
        DateTime At, long Epoch, TaskCompletionSource<bool>? Barrier = null, string? ServerSessionId = null);
}
