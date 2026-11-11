using System.Diagnostics;

namespace StrmAssistantRebuild.Common;

/// <summary>One observed worker; duplicate IDs coalesce and no Task is allocated for each delay.</summary>
public sealed class DelayedCatchupQueue : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<long, long> pending = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly Func<long[], CancellationToken, Task> process;
    private readonly Action<string> report;
    private readonly int capacity;
    private CancellationTokenSource? current;
    private bool enabled;
    public Task Completion { get; }
    public int PendingCount { get { lock (gate) return pending.Count; } }
    public bool IsBusy { get { lock (gate) return current != null || pending.Count != 0; } }
    /// <summary>Queued items plus the running batch, for the dashboard.</summary>
    public int ActiveCount { get { lock (gate) return pending.Count + (current != null ? 1 : 0); } }
    /// <summary>Dashboard cancel: drop queued items and cancel the running batch; the queue stays enabled for new events.</summary>
    public int Clear()
    {
        CancellationTokenSource? cancel;
        int count;
        lock (gate) { count = pending.Count + (current != null ? 1 : 0); pending.Clear(); cancel = current; }
        try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
        return count;
    }

    public DelayedCatchupQueue(Func<long[], CancellationToken, Task> process, Action<string> report, int capacity = 10000)
    {
        this.process = process;
        this.report = report;
        this.capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
        Completion = Worker();
    }
    public void Configure(bool enable, bool reset = false)
    {
        CancellationTokenSource? cancel;
        lock (gate)
        {
            if (enabled == enable && !reset) return;
            enabled = enable;
            if (enable && !reset) return;
            pending.Clear();
            cancel = current;
        }
        try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
    }
    public bool Enqueue(long id, TimeSpan delay)
    {
        if (id <= 0 || delay < TimeSpan.Zero || delay > TimeSpan.FromDays(1)) return false;
        lock (gate)
        {
            if (!enabled || stopping.IsCancellationRequested) return false;
            if (!pending.ContainsKey(id) && pending.Count >= capacity) return false;
            pending[id] = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
            return true;
        }
    }
    private async Task Worker()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
            {
                long[] batch;
                CancellationTokenSource batchToken;
                lock (gate)
                {
                    if (!enabled || pending.Count == 0) continue;
                    var now = Stopwatch.GetTimestamp();
                    batch = pending.Where(p => p.Value <= now).Select(p => p.Key).Take(100).ToArray();
                    if (batch.Length == 0) continue;
                    foreach (var id in batch) pending.Remove(id);
                    batchToken = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    current = batchToken;
                }
                try { await process(batch, batchToken.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (batchToken.IsCancellationRequested) { }
                catch (Exception ex) { report("Catch-up batch failed: " + ex.Message); }
                finally
                {
                    lock (gate) { if (ReferenceEquals(current, batchToken)) current = null; }
                    batchToken.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) { report("Catch-up worker stopped: " + ex.Message); }
    }
    public void Dispose()
    {
        Configure(false);
        stopping.Cancel();
    }
}
