using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

/// <summary>Observed, cancellable index builds never run on the settings or server startup thread.</summary>
public sealed class SearchCoordinator : IDisposable
{
    private readonly ChineseSearchIndex index;
    private readonly IItemRepository repository;
    private readonly Version version;
    private readonly ILogger logger;
    private readonly DelayedCatchupQueue worker;
    private readonly object gate = new();
    private readonly SemaphoreSlim buildGate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private ModOptions latest = new();
    private long revision;
    private bool disposed;
    private bool rebuildRequired;
    private CancellationTokenSource? manual;
    private TaskCompletionSource<bool>? manualDone;
    private double progressValue;
    public string Status { get; private set; } = "Native search active.";
    public int Count => index.Count;
    public double Progress => Volatile.Read(ref progressValue);
    public bool IsReady => index.IsReady && ChineseSearchCompatibility.IsEnabled;
    public Task Completion => worker.Completion;
    public bool IsBusy { get { lock (gate) return manual != null || worker.IsBusy; } }
    public SearchCoordinator(ChineseSearchIndex index, IItemRepository repository, Version version, ILogger logger)
    {
        this.index = index; this.repository = repository; this.version = version; this.logger = logger;
        worker = new DelayedCatchupQueue(Build, text => logger.Warn("[StrmAssistantRebuild] Search build: {0}", text), 1);
    }
    public void Configure(ModOptions options)
    {
        lock (gate)
        {
            if (disposed) return;
            // OFF can be coalesced away by the delayed worker before its following ON executes.
            // Remember the invalidation so a still-ready old index never skips the next authoritative scan.
            if (!options.EnhanceChineseSearch) rebuildRequired = true;
            latest = new ModOptions { EnhanceChineseSearch = options.EnhanceChineseSearch, EnhanceChineseSearchMode = options.EnhanceChineseSearchMode, SearchScope = options.SearchScope, SearchTuningPreferences = options.SearchTuningPreferences };
            revision++;
            ChineseSearchCompatibility.Disable(); manual?.Cancel();
            worker.Configure(false); worker.Configure(true);
            Status = options.EnhanceChineseSearch ? "Building candidate index; native search remains active." : "Native search active.";
            worker.Enqueue(1, TimeSpan.Zero);
        }
    }
    private async Task Build(long[] _, CancellationToken token)
    {
        ModOptions options; long expected; bool force;
        lock (gate) { options = latest; expected = revision; force = rebuildRequired; }
        await BuildCore(options, expected, force, null, token).ConfigureAwait(false);
    }
    public async Task RebuildAsync(IProgress<double> progress, CancellationToken token)
    {
        ModOptions options; long expected;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(SearchCoordinator));
            if (!latest.EnhanceChineseSearch) throw new InvalidOperationException("Chinese search is disabled; no index rebuild was started.");
            if (manual != null) throw new InvalidOperationException("An index rebuild is already running.");
            manual = linked; manualDone = done; options = latest; expected = ++revision;
            worker.Configure(false); worker.Configure(true);
            ChineseSearchCompatibility.Disable(); Status = "Rebuilding all candidate tokens; native search remains active.";
        }
        try
        {
            await Task.Run(() => BuildCore(options, expected, true, progress, linked.Token), linked.Token).ConfigureAwait(false);
            if (!index.IsReady) throw new InvalidOperationException("Index rebuild failed; native search remains active. See the per-build log.");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            lock (gate) { if (expected == revision) Status = "Index rebuild cancelled; native search remains active."; }
            throw;
        }
        finally
        {
            lock (gate) { if (ReferenceEquals(manual, linked)) manual = null; }
            done.TrySetResult(true);
        }
    }
    public void CancelRebuild() { lock (gate) manual?.Cancel(); }
    private async Task BuildCore(ModOptions options, long expected, bool force, IProgress<double>? progress, CancellationToken token)
    {
        await buildGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            index.Configure(options, token, new BuildProgress(this, progress), force);
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (disposed || revision != expected) return;
                rebuildRequired = false;
                var active = PatchHealth.Record("ChineseSearch", ChineseSearchCompatibility.Configure(version, repository, index, options.EnhanceChineseSearch, out var detail));
                Status = detail;
                if (active) logger.Info("[StrmAssistantRebuild] {0}", detail); else logger.Warn("[StrmAssistantRebuild] {0}", detail);
            }
        }
        finally { buildGate.Release(); }
    }
    public void Dispose()
    {
        Task pending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; revision++; ChineseSearchCompatibility.Disable(); worker.Dispose(); stopping.Cancel(); manual?.Cancel();
            pending = manualDone?.Task ?? Task.CompletedTask;
        }
        // All builds use no UI synchronization context and obey cancellation; drain before disposing the index.
        try { Task.WhenAll(worker.Completion, pending).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        index.Dispose();
    }
    private sealed class BuildProgress : IProgress<double>
    {
        private readonly SearchCoordinator owner;
        private readonly IProgress<double>? consumer;
        public BuildProgress(SearchCoordinator owner, IProgress<double>? consumer) { this.owner = owner; this.consumer = consumer; }
        public void Report(double value)
        {
            Volatile.Write(ref owner.progressValue, value);
            owner.logger.Info("[StrmAssistantRebuild] Search indexing {0:F1}%", value);
            consumer?.Report(value);
        }
    }
}
