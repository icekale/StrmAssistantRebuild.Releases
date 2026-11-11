using System.Diagnostics;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace StrmAssistantRebuild.Common;

/// <summary>One observed worker and at most one owned timer; wall-clock changes never affect the window.</summary>
public sealed class MemoryMonitor : IDisposable
{
    private readonly object gate = new();
    private readonly IServerApplicationHost host;
    private readonly ISessionManager sessions;
    private readonly ITaskManager tasks;
    private readonly ILogger logger;
    private readonly Func<long> timestamp;
    private readonly Func<long> workingSet;
    private readonly Func<bool>? additionalBusy;
    private readonly long frequency;
    private readonly TimeSpan samplePeriod;
    private readonly SemaphoreSlim changed = new(0, 1);
    private readonly CancellationTokenSource stopping = new();
    private PeriodicTimer? timer;
    private int? limit;
    private long? exceededAt;
    private bool restartRequested;
    private bool disposed;
    private string status = "Disabled";
    private double? countdown;
    private bool busyPending;
    public Task Completion { get; }
    public string Status { get { lock (gate) return status; } }
    /// <summary>Dashboard snapshot: configured limit, seconds left in the idle countdown, or a restart held back because the server is busy above the limit.</summary>
    public (int? LimitMB, double? CountdownSeconds, bool BusyPending) RestartState { get { lock (gate) return (limit, countdown, busyPending); } }

    public MemoryMonitor(IServerApplicationHost host, ISessionManager sessions, ITaskManager tasks, ILogger logger, Func<bool>? additionalBusy = null)
        : this(host, sessions, tasks, logger, Stopwatch.GetTimestamp, Stopwatch.Frequency, ReadWorkingSet, TimeSpan.FromSeconds(30), additionalBusy) { }

    internal MemoryMonitor(IServerApplicationHost host, ISessionManager sessions, ITaskManager tasks, ILogger logger,
        Func<long> timestamp, long frequency, Func<long> workingSet, TimeSpan samplePeriod, Func<bool>? additionalBusy = null)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (samplePeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(samplePeriod));
        this.host = host; this.sessions = sessions; this.tasks = tasks; this.logger = logger;
        this.timestamp = timestamp; this.frequency = frequency; this.workingSet = workingSet; this.samplePeriod = samplePeriod; this.additionalBusy = additionalBusy;
        sessions.PlaybackStart += OnPlayback;
        sessions.PlaybackProgress += OnPlayback;
        tasks.TaskExecuting += OnTask;
        Completion = Task.Run(RunAsync);
    }

    private static long ReadWorkingSet() { using var process = Process.GetCurrentProcess(); return process.WorkingSet64; }

    public void Configure(int? memoryUsageLimitMB)
    {
        if (memoryUsageLimitMB is < 500) throw new ArgumentOutOfRangeException(nameof(memoryUsageLimitMB), "Memory limit must be null or at least 500 MB.");
        lock (gate)
        {
            if (disposed) return;
            if (limit == memoryUsageLimitMB) return;
            limit = memoryUsageLimitMB;
            exceededAt = null; restartRequested = false; countdown = null; busyPending = false;
            status = limit.HasValue ? "Waiting for idle memory samples" : "Disabled";
            timer?.Dispose(); // Interrupt an outstanding tick immediately, including on disable.
            if (changed.CurrentCount == 0) changed.Release();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await changed.WaitAsync(stopping.Token).ConfigureAwait(false);
                PeriodicTimer owned;
                lock (gate)
                {
                    if (disposed) return;
                    if (!limit.HasValue) continue;
                    timer = owned = new PeriodicTimer(samplePeriod);
                }
                try
                {
                    while (await owned.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
                    {
                        try { Sample(); }
                        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
                        catch (Exception ex)
                        {
                            lock (gate) { exceededAt = null; if (!disposed && limit.HasValue) status = "Sample failed: " + ex.Message; }
                            logger.Warn("[StrmAssistantRebuild] Memory monitor: {0}", ex.Message);
                        }
                    }
                }
                finally
                {
                    owned.Dispose();
                    lock (gate) { if (ReferenceEquals(timer, owned)) timer = null; }
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) { logger.Warn("[StrmAssistantRebuild] Memory worker stopped: {0}", ex.Message); }
    }

    internal void Sample()
    {
        lock (gate)
        {
            if (disposed || !limit.HasValue) return;
            if (host.IsShuttingDown || !host.CanSelfRestart)
            { exceededAt = null; status = "Host restart unavailable"; return; }
            if (sessions.Sessions.Any(s => s.NowPlayingItem != null || s.FullNowPlayingItem != null) ||
                tasks.ScheduledTasks.Any(task => task.State != TaskState.Idle) || additionalBusy?.Invoke() == true)
            { exceededAt = null; countdown = null; busyPending = workingSet() > (long)limit.Value * 1024 * 1024; status = "Busy; idle window reset"; return; }
            if (workingSet() <= (long)limit.Value * 1024 * 1024)
            { exceededAt = null; restartRequested = false; countdown = null; busyPending = false; status = "Below memory limit"; return; }
            if (restartRequested) return;
            var now = timestamp();
            if (!exceededAt.HasValue || now < exceededAt.Value) exceededAt = now;
            var elapsed = (now - exceededAt.Value) / (double)frequency;
            countdown = Math.Max(0, 900 - elapsed); busyPending = false;
            status = $"Idle above limit; restart in {Math.Max(0, Math.Ceiling(900 - elapsed))} seconds";
            if (elapsed < 900) return;
            stopping.Token.ThrowIfCancellationRequested();
            restartRequested = true;
            status = "Restart requested after 15 continuous idle minutes above limit";
            logger.Warn("[StrmAssistantRebuild] {0}", status);
            MaintenanceService.RequestRestart(host, stopping.Token);
        }
    }

    private void OnPlayback(object? sender, PlaybackProgressEventArgs e) => ResetActivity();
    private void OnTask(object? sender, GenericEventArgs<IScheduledTaskWorker> e) => ResetActivity();
    public void NotifyActivity() => ResetActivity();
    private void ResetActivity()
    {
        lock (gate) { exceededAt = null; countdown = null; if (limit.HasValue && !disposed) status = "Activity observed; idle window reset"; }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; limit = null; exceededAt = null; status = "Stopped";
            stopping.Cancel(); timer?.Dispose();
        }
        sessions.PlaybackStart -= OnPlayback;
        sessions.PlaybackProgress -= OnPlayback;
        tasks.TaskExecuting -= OnTask;
    }
}
