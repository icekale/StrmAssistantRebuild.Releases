using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;

/// <summary>Selected retained libraries. Runtime configuration is separate from durable future-startup intent.</summary>
public sealed class EpisodeScopeCatchup : IDisposable
{
    private sealed class Settings
    {
        internal readonly long Epoch, ProbeUserId;
        internal readonly IReadOnlyDictionary<long, Guid> Libraries;
        internal readonly CancellationToken Token;
        internal readonly DelayedCatchupQueue Queue;
        internal readonly EventHandler<ItemChangeEventArgs> Handler;
        // Changed under owner.gate only by this generation's exact Append publication fact.
        internal ScopedEpisodeUserDataKey.RetainedSnapshot Expected;
        internal Settings(EpisodeScopeCatchup owner, long epoch, IReadOnlyDictionary<long, Guid> libraries,
            long user, ScopedEpisodeUserDataKey.RetainedSnapshot expected, CancellationToken token)
        {
            Epoch = epoch; Libraries = libraries; ProbeUserId = user; Expected = expected; Token = token;
            Queue = new DelayedCatchupQueue((ids, ct) => owner.Process(this, ids, ct), owner.Log, 256);
            Handler = (sender, args) => owner.OnAdded(this, sender, args);
        }
    }
    private readonly object gate = new();
    private readonly ILibraryManager library;
    private readonly EpisodeScopeBatch batch;
    private readonly ILogger logger;
    private readonly TimeSpan delay;
    private readonly List<DelayedCatchupQueue> workers = new();
    private Settings? settings;
    private bool stopped;
    private Task? stopCompletion;
    private long epoch, queued, rejected, skipped, deferred, appendCalls, completed, incomplete, cancelled, applied;
    private string status = "Disabled", reason = "Explicit runtime configuration required.";
    private EpisodeScopeBatch.Report? lastReport;
    public Task Completion { get { lock (gate) return stopCompletion ?? Task.WhenAll(workers.Select(q => q.Completion)); } }
    public bool IsBusy { get { lock (gate) return workers.Any(q => q.IsBusy); } }
    public int PendingCount { get { lock (gate) return workers.Sum(q => q.PendingCount); } }
    public bool Enabled { get { lock (gate) return settings != null && !settings.Token.IsCancellationRequested; } }
    public string Status { get { lock (gate) return !stopped ? status : stopCompletion!.IsFaulted ? "StopFailed" : stopCompletion.IsCanceled ? "StopCancelled" : stopCompletion.IsCompletedSuccessfully ? "Stopped" : "Stopping"; } }
    public string Reason { get { lock (gate) return reason; } }
    /// <summary>Historical Append facts, including partial commits; Disable/Stop never discard this report.</summary>
    public EpisodeScopeBatch.Report? LastReport => Volatile.Read(ref lastReport);
    /// <summary>Accepted event deliveries, including duplicates coalesced in the pending queue.</summary>
    public long QueuedEvents => Interlocked.Read(ref queued);
    public long RejectedEvents => Interlocked.Read(ref rejected);
    public long SkippedItems => Interlocked.Read(ref skipped);
    public long DeferredItems => Interlocked.Read(ref deferred);
    public long AppendCalls => Interlocked.Read(ref appendCalls);
    public long CompletedBatches => Interlocked.Read(ref completed);
    public long IncompleteBatches => Interlocked.Read(ref incomplete);
    public long CancelledBatches => Interlocked.Read(ref cancelled);
    /// <summary>Successful native item results, not the count of newly published retained rules.</summary>
    public long AppliedItems => Interlocked.Read(ref applied);
    public EpisodeScopeCatchup(ILibraryManager library, EpisodeScopeBatch batch, ILogger logger, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(library); ArgumentNullException.ThrowIfNull(batch); ArgumentNullException.ThrowIfNull(logger);
        this.library = library; this.batch = batch; this.logger = logger; this.delay = delay ?? TimeSpan.FromSeconds(1);
        if (this.delay < TimeSpan.Zero || this.delay > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(delay));
    }
    /// <summary>Replaces/cancels the former runtime epoch, even if validation fails. At most 64 raw positive
    /// IDs, deduplicated existing CollectionFolders already represented in retained rules. Empty disables.
    /// The token also cancels this generation's work. No startup, CSV/global option or persistence writes.</summary>
    public void Configure(IEnumerable<long> selectedLibraryIds, long probeUserId, CancellationToken token)
        => ConfigureCore(selectedLibraryIds, probeUserId, token);
    internal long ConfigurationEpoch { get { lock (gate) return epoch; } }
    internal void RequireConfigurationEpoch(long expectedEpoch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) if (stopped || expectedEpoch != epoch) throw new OperationCanceledException("Startup catch-up configuration was superseded or stopped.");
    }
    // Before Recover there is intentionally no retained policy. Validate native CF identity only.
    internal void ValidateExpected(IReadOnlyList<EpisodeScopeJournal.LibraryBinding> bindings, EpisodeScopeBatch recoveringBatch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(batch, recoveringBatch) || !batch.UsesLibrary(library)) throw new InvalidOperationException("Startup catch-up and recovery owners differ.");
        if (batch.StopRequested) throw new ObjectDisposedException(nameof(EpisodeScopeBatch));
        foreach (var binding in EpisodeScopeJournal.ValidateBindings(bindings, token))
        {
            if (library.GetItemById(binding.LibraryId) is not CollectionFolder folder || folder.InternalId != binding.LibraryId || folder.Id != binding.LibraryGuid)
                throw new InvalidOperationException("Stored startup CollectionFolder ID/GUID no longer matches.");
            token.ThrowIfCancellationRequested();
        }
    }
    internal long ConfigureExpected(IReadOnlyList<EpisodeScopeJournal.LibraryBinding> bindings, long probeUserId, CancellationToken token,
        long expectedEpoch, IReadOnlyList<EpisodeLibraryScopePlan.Row> recoveredRows, Action<Action> withIntent)
        => ConfigureCore(bindings.Select(b => b.LibraryId), probeUserId, token, expectedEpoch, bindings, recoveredRows, withIntent);
    internal void DisableExpected(long ownedEpoch)
    {
        Settings? old;
        lock (gate)
        {
            if (stopped || epoch != ownedEpoch) return;
            ++epoch; old = Detach("Disabled", "Startup-owned generation cancelled; retained rules unchanged.");
        }
        Release(old);
    }
    private long ConfigureCore(IEnumerable<long> selectedLibraryIds, long probeUserId, CancellationToken token,
        long? expectedEpoch = null, IReadOnlyList<EpisodeScopeJournal.LibraryBinding>? bindings = null,
        IReadOnlyList<EpisodeLibraryScopePlan.Row>? recoveredRows = null, Action<Action>? withIntent = null)
    {
        long request; Settings? old; Settings? next = null;
        lock (gate)
        {
            if (stopped) throw new ObjectDisposedException(nameof(EpisodeScopeCatchup));
            if (expectedEpoch.HasValue && expectedEpoch != epoch) throw new OperationCanceledException("Startup configuration was superseded by explicit runtime intent.");
            request = ++epoch; old = Detach("Configuring", "Validating explicit runtime selection.");
        }
        Release(old);
        try
        {
            ArgumentNullException.ThrowIfNull(selectedLibraryIds); token.ThrowIfCancellationRequested();
            var raw = new List<long>();
            foreach (var id in selectedLibraryIds)
            {
                token.ThrowIfCancellationRequested();
                if (raw.Count == 64) throw new ArgumentException("Library selection raw input budget exceeded.");
                if (id <= 0) throw new ArgumentException("Positive CollectionFolder IDs required."); raw.Add(id);
            }
            var ids = raw.Distinct().ToArray();
            if (ids.Length == 0) { SetStatus(request, "Disabled", "Empty explicit selection."); return request; }
            if (probeUserId <= 0) throw new ArgumentOutOfRangeException(nameof(probeUserId));
            if (batch.StopRequested) throw new ObjectDisposedException(nameof(EpisodeScopeBatch));
            if (!batch.UsesLibrary(library)) throw new InvalidOperationException("Catch-up and batch library managers differ.");
            var retained = ScopedEpisodeUserDataKey.RetainedSnapshot.Capture(library);
            if (recoveredRows != null && (retained.Count != recoveredRows.Count || recoveredRows.Any(r => !retained.Matches(r))))
                throw new InvalidOperationException("Current retained policy differs from the completed startup recovery intent.");
            var expectedBindings = bindings == null ? null : EpisodeScopeJournal.ValidateBindings(bindings, token).ToDictionary(b => b.LibraryId, b => b.LibraryGuid);
            var retainedLibraries = retained.Rows.Select(r => r.LibraryId).ToHashSet();
            if (ids.Any(id => !retainedLibraries.Contains(id))) throw new InvalidOperationException("Selection includes a library absent from the runtime retained policy.");
            var folders = new Dictionary<long, Guid>();
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                if (library.GetItemById(id) is not CollectionFolder folder || folder.InternalId != id || folder.Id == Guid.Empty)
                    throw new InvalidOperationException("Expected an existing CollectionFolder ID and GUID.");
                if (expectedBindings != null && (!expectedBindings.TryGetValue(id, out var guid) || folder.Id != guid))
                    throw new InvalidOperationException("Stored startup CollectionFolder GUID changed after recovery.");
                folders.Add(id, folder.Id);
            }
            retained.RequireCurrent(); token.ThrowIfCancellationRequested();
            withIntent?.Invoke(() => { }); // File IO/callbacks are deliberately outside the service gate.
            lock (gate)
            {
                if (stopped || request != epoch) throw new OperationCanceledException("Configuration superseded.");
                token.ThrowIfCancellationRequested(); retained.RequireCurrent();
                // Track even a not-yet-subscribed worker: Stop and the generation budget include it.
                workers.RemoveAll(q => q.Completion.IsCompleted);
                if (workers.Count >= 4) throw new InvalidOperationException("Four catch-up generations are still draining; explicitly retry configuration after Completion.");
                next = new Settings(this, request, folders, probeUserId, retained, token); workers.Add(next.Queue);
            }
            library.ItemAdded += next.Handler;
            // A subscription accessor may reconfigure, cancel or change durable settings/CF identity.
            if (bindings != null) ValidateExpected(bindings, batch, token);
            void Publish()
            {
                lock (gate)
                {
                    if (stopped || request != epoch) throw new OperationCanceledException("Configuration superseded.");
                    token.ThrowIfCancellationRequested(); retained.RequireCurrent();
                    // One queue per generation prevents delayed IDs/captured delegates acquiring later scope.
                    settings = next; next.Queue.Configure(true);
                    status = "Enabled"; reason = "Selected retained libraries only; no automatic retry.";
                }
            }
            if (withIntent == null) Publish(); else withIntent(Publish);
            return request;
        }
        catch (Exception error)
        {
            Settings? failed = null;
            lock (gate) { if (request == epoch && !stopped) failed = Detach("Disabled", "Configuration rejected: " + error.Message); }
            Release(failed ?? next); throw;
        }
    }
    private void OnAdded(Settings captured, object? sender, ItemChangeEventArgs e)
    {
        try
        {
            lock (gate)
            {
                if (!ReferenceEquals(settings, captured) || captured.Token.IsCancellationRequested) return;
                // Capture native type and one immutable positive ID only; no manager/key/DB/file calls.
                if (e.Item is not Episode item) return;
                var id = item.InternalId; if (id <= 0) return;
                if (captured.Queue.Enqueue(id, delay)) Interlocked.Increment(ref queued);
                else { Interlocked.Increment(ref rejected); reason = "Pending capacity reached; explicit retry required."; }
            }
        }
        catch (Exception error) { Interlocked.Increment(ref rejected); Log("ItemAdded failed: " + error.Message); }
    }
    // Called inside native transactions: pure memory/token checks, no owner lock or manager IO.
    private void RequireCurrent(Settings captured, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(Volatile.Read(ref settings), captured)) throw new OperationCanceledException("Configuration epoch changed.");
    }
    private async Task Process(Settings captured, long[] ids, CancellationToken queueToken)
    {
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(queueToken, captured.Token);
            var token = lifetime.Token; RequireCurrent(captured, token);
            ScopedEpisodeUserDataKey.RetainedSnapshot retained;
            lock (gate) retained = captured.Expected;
            void CheckCurrent()
            {
                RequireCurrent(captured, token);
                try { retained.RequireCurrent(); }
                catch { Suspend(captured, "Retained policy identity/ownership changed; explicit reconfiguration required."); throw; }
            }
            CheckCurrent();
            var retainedIds = retained.Rows.Select(r => r.ItemId).ToHashSet(); var accepted = new List<long>();
            foreach (var id in ids)
            {
                CheckCurrent();
                if (retainedIds.Contains(id)) { Skip(captured, "Already retained ID."); continue; }
                var item = library.GetItemById(id); CheckCurrent();
                if (item is not Episode episode || episode.InternalId != id || episode.Id == Guid.Empty || episode.IsVirtualItem ||
                    !episode.IsFileProtocol || string.IsNullOrWhiteSpace(episode.Path) || !string.IsNullOrEmpty(episode.ExternalId))
                { Skip(captured, "Not a persisted local non-virtual Episode."); continue; }
                var folders = library.GetCollectionFolders(episode, token); CheckCurrent();
                if (folders == null || folders.Length == 0 || folders.Any(f => f is not CollectionFolder || f.InternalId <= 0 || f.Id == Guid.Empty) ||
                    folders.Select(f => (f.InternalId, f.Id)).Distinct().Count() != 1 ||
                    !captured.Libraries.TryGetValue(folders[0].InternalId, out var guid) || folders[0].Id != guid)
                { Skip(captured, "Outside or ambiguous selected CollectionFolder ID/GUID."); continue; }
                var libraryId = folders[0].InternalId;
                if (library.GetItemById(libraryId) is not CollectionFolder current || current.InternalId != libraryId || current.Id != guid)
                { Skip(captured, "Selected CollectionFolder identity changed."); continue; }
                CheckCurrent(); accepted.Add(id);
            }
            if (accepted.Count == 0) return;
            CheckCurrent(); Interlocked.Increment(ref appendCalls);
            // Existing Append owns admission, per-item quota and journal; binding stays enforced at native writes.
            var binding = new EpisodeScopeBatch.AppendBinding(retained, captured.Libraries, () => RequireCurrent(captured, token));
            var report = await batch.AppendSelectedAsync(accepted, captured.ProbeUserId, token, binding).ConfigureAwait(false);
            Volatile.Write(ref lastReport, report);
            Interlocked.Add(ref applied, report.Items.LongCount(r => r.State is "Applied" or "AlreadyScoped"));
            // Detach fences the epoch before out-of-lock Cancel runs; preserve the original report,
            // but classify this known cancellation even if Append observed OCE before its token did.
            var wasCancelled = report.Cancelled || queueToken.IsCancellationRequested || captured.Token.IsCancellationRequested || !ReferenceEquals(Volatile.Read(ref settings), captured);
            if (wasCancelled) Interlocked.Increment(ref cancelled);
            else if (report.Phase == "Completed" && report.RetainedCountAuthoritative) Interlocked.Increment(ref completed);
            else Interlocked.Increment(ref incomplete);
            if (wasCancelled || report.Phase != "Completed" || !report.RetainedCountAuthoritative) Interlocked.Add(ref deferred, accepted.Count);
            if (!report.RetainedCountAuthoritative) { Suspend(captured, report.Reconciliation); return; }
            lock (gate)
            {
                if (!ReferenceEquals(settings, captured)) return;
                if (report.PublishedRetained != null) captured.Expected = report.PublishedRetained;
                status = wasCancelled ? "Cancelled" : report.Phase == "Completed" ? "Enabled" : "Incomplete"; reason = report.Reconciliation;
            }
        }
        catch (OperationCanceledException) when (queueToken.IsCancellationRequested || captured.Token.IsCancellationRequested || !ReferenceEquals(Volatile.Read(ref settings), captured))
        {
            Interlocked.Increment(ref cancelled); Interlocked.Add(ref deferred, ids.Length);
            SetStatus(captured.Epoch, "Cancelled", "Configuration/worker cancelled; inspect LastReport, then explicitly retry unretained IDs.");
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref incomplete); Interlocked.Add(ref deferred, ids.Length);
            SetStatus(captured.Epoch, "Incomplete", error.GetType().Name + ": " + error.Message + "; explicit retry required."); Log(error.Message);
        }
    }
    private void Suspend(Settings captured, string detail)
    {
        Settings? old = null;
        lock (gate) { if (ReferenceEquals(settings, captured)) { ++epoch; old = Detach("Suspended", detail); } }
        Release(old);
    }
    private void Skip(Settings captured, string detail) { Interlocked.Increment(ref skipped); SetStatus(captured.Epoch, "Enabled", detail); }
    private void SetStatus(long request, string state, string detail) { lock (gate) { if (!stopped && request == epoch) { status = state; reason = detail; } } }
    private void Log(string message) { try { logger.Warn("[EpisodeScopeCatchup] {0}", message); } catch { /* A log sink never breaks host events/workers. */ } }
    // Called under gate; detaching never calls CancellationTokenSource.Cancel.
    private Settings? Detach(string state, string detail)
    {
        var old = settings; Volatile.Write(ref settings, null); status = state; reason = detail;
        return old;
    }
    private void Release(Settings? old)
    {
        if (old == null) return;
        try { library.ItemAdded -= old.Handler; }
        catch (Exception error) { Log("Unsubscribe: " + error.Message); }
        Cancel(old.Queue);
    }
    private void Cancel(DelayedCatchupQueue? queue)
    {
        try { queue?.Dispose(); }
        catch (Exception error)
        {
            Log("Cancel: " + error.Message);
            // Configure(false) already disables before synchronous callbacks. Finish stopping the timer
            // even if one callback threw before DelayedCatchupQueue.Dispose reached stopping.Cancel().
            try { queue?.Dispose(); } catch (Exception retry) { Log("Stop signal: " + retry.Message); }
        }
    }
    public void Disable()
    {
        Settings? old;
        lock (gate) { if (stopped) return; ++epoch; old = Detach("Disabled", "Explicitly disabled; retained rules unchanged."); }
        Release(old);
    }
    /// <summary>Cancel/unsubscribe, then await Completion BEFORE parent Batch.StopAndClearAsync.
    /// Already-entered native work may return committed facts; Stop never clears retained rules.</summary>
    public void Stop()
    {
        DelayedCatchupQueue[] pending; Settings? old;
        lock (gate)
        {
            if (stopped) return; stopped = true; ++epoch;
            old = Detach("Stopping", "Await Completion before clearing the parent batch retained policy.");
            pending = workers.ToArray(); stopCompletion = Task.WhenAll(pending.Select(q => q.Completion));
        }
        Release(old);
        foreach (var worker in pending) Cancel(worker);
    }
    public void Dispose() => Stop();
}
