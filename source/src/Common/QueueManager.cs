using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;

/// <summary>One shared quota per tier, including simultaneous scheduled and catch-up batches.</summary>
public sealed class QueueManager : IDisposable
{
    private readonly ConcurrencyGate master = new(1);
    private readonly ConcurrencyGate tier2 = new(1);
    private readonly CancellationTokenSource stopping = new();
    private readonly ILogger logger;
    private readonly SemaphoreSlim[] itemGates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private volatile int cooldownSeconds;
    private int activeOperations;
    public bool IsBusy => Volatile.Read(ref activeOperations) != 0;
    internal CancellationToken StoppingToken => stopping.Token;
    public event Action? WorkStarted;
    public QueueManager(ILogger logger) => this.logger = logger;
    public void Configure(int masterLimit, int tier2Limit, int cooldown)
    {
        master.Configure(Math.Clamp(masterLimit, 1, 20));
        tier2.Configure(Math.Clamp(tier2Limit, 1, 20));
        cooldownSeconds = Math.Clamp(cooldown, 0, 60);
    }
    public async Task<BatchProgress> RunBatch<T>(IReadOnlyList<T> items, Func<T, CancellationToken, Task> action,
        IProgress<double> progress, CancellationToken token, bool secondTier = false, Func<T, long>? itemKey = null)
    {
        using var activity = new Activity(this);
        try { WorkStarted?.Invoke(); } catch (Exception ex) { logger.Warn("[StrmAssistantRebuild] Work observer failed: {0}", ex.Message); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        var result = new BatchProgress { Total = items.Count };
        var quota = secondTier ? tier2 : master;
        var progressGate = new object();
        progress.Report(0);
        try
        {
            // At most twenty worker continuations, not one Task allocation per library item.
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 20, CancellationToken = linked.Token }, async (item, ct) =>
            {
                // ponytail: 128 striped item locks bound memory; unrelated colliding IDs serialize.
                var itemGate = itemKey == null ? null : itemGates[(int)((ulong)itemKey(item) % (uint)itemGates.Length)];
                if (itemGate != null) await itemGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var lease = await quota.Enter(ct).ConfigureAwait(false);
                    await action(item, ct).ConfigureAwait(false);
                    lock (progressGate) result.Done++;
                    if (!secondTier && quota.Limit == 1 && cooldownSeconds > 0)
                        await Task.Delay(TimeSpan.FromSeconds(cooldownSeconds), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lock (progressGate) result.Failed++;
                    logger.Warn("[StrmAssistantRebuild] Batch item failed: {0}", ex.Message);
                }
                finally
                {
                    itemGate?.Release();
                    lock (progressGate) progress.Report(result.Total == 0 ? 100 : (result.Done + result.Failed) * 100d / result.Total);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { result.Cancelled = true; throw; }
        progress.Report(100);
        logger.Info("[StrmAssistantRebuild] Batch complete: {0}/{1}, failed {2}", result.Done, result.Total, result.Failed);
        if (result.Total > 0 && result.Done == 0 && result.Failed > 0)
            throw new InvalidOperationException($"All {result.Failed} items failed; see the per-item log.");
        return result;
    }
    public async Task<T> RunSingle<T>(long itemId, Func<CancellationToken, Task<T>> action, CancellationToken token, bool secondTier = true)
    {
        using var activity = new Activity(this);
        try { WorkStarted?.Invoke(); } catch (Exception ex) { logger.Warn("[StrmAssistantRebuild] Work observer failed: {0}", ex.Message); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        var itemGate = itemGates[(int)((ulong)itemId % (uint)itemGates.Length)];
        await itemGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            using var lease = await (secondTier ? tier2 : master).Enter(linked.Token).ConfigureAwait(false);
            return await action(linked.Token).ConfigureAwait(false);
        }
        finally { itemGate.Release(); }
    }
    /// <summary>Optional recovery skips a busy item/quota, including callers already holding our lease.</summary>
    public async Task<bool> TryRunSingle(long itemId, Func<CancellationToken, Task<bool>> action, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        linked.Token.ThrowIfCancellationRequested();
        var itemGate = itemGates[(int)((ulong)itemId % (uint)itemGates.Length)];
        if (!itemGate.Wait(0, linked.Token)) return false;
        try
        {
            using var lease = tier2.TryEnter();
            if (lease == null) return false;
            using var activity = new Activity(this);
            try { WorkStarted?.Invoke(); } catch (Exception ex) { logger.Warn("[StrmAssistantRebuild] Work observer failed: {0}", ex.GetType().Name); }
            linked.Token.ThrowIfCancellationRequested();
            return await action(linked.Token).ConfigureAwait(false);
        }
        finally { itemGate.Release(); }
    }
    public void Dispose() => stopping.Cancel();

    private sealed class Activity : IDisposable
    {
        private QueueManager? owner;
        public Activity(QueueManager owner) { this.owner = owner; Interlocked.Increment(ref owner.activeOperations); }
        public void Dispose() { var value = Interlocked.Exchange(ref owner, null); if (value != null) Interlocked.Decrement(ref value.activeOperations); }
    }
    private sealed class ConcurrencyGate
    {
        private readonly object gate = new();
        private TaskCompletionSource<bool> changed = Signal();
        private int active;
        public int Limit { get; private set; }
        public ConcurrencyGate(int limit) => Limit = limit;
        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Configure(int limit) { lock (gate) { Limit = limit; Pulse(); } }
        public IDisposable? TryEnter()
        {
            lock (gate)
            {
                if (active >= Limit) return null;
                active++; return new Lease(this);
            }
        }
        public async Task<IDisposable> Enter(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Task wait;
                lock (gate)
                {
                    if (active < Limit) { active++; return new Lease(this); }
                    wait = changed.Task;
                }
                await wait.WaitAsync(token).ConfigureAwait(false);
            }
        }
        private void Pulse() { var prior = changed; changed = Signal(); prior.TrySetResult(true); }
        private void Exit() { lock (gate) { active--; Pulse(); } }
        private sealed class Lease : IDisposable
        {
            private ConcurrencyGate? owner;
            public Lease(ConcurrencyGate owner) => this.owner = owner;
            public void Dispose() => Interlocked.Exchange(ref owner, null)?.Exit();
        }
    }
}
public sealed class BatchProgress
{
    public int Total { get; init; }
    public int Done { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }
}
