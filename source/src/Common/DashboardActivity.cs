using System.Security;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Tasks;

namespace StrmAssistantRebuild.Common;

/// <summary>Read public host snapshots; commands are short-lived references to the observed run, never raw client IDs.</summary>
public sealed class DashboardActivity : IDisposable
{
    private readonly ITaskManager tasks;
    private readonly ISessionManager sessions;
    private readonly ILogger logger;
    private readonly object gate = new();
    private readonly Dictionary<string, Ticket> tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<IScheduledTaskWorker, long> generations = new(ReferenceEqualityComparer.Instance);
    private long generation;
    private bool disposed;
    private const string Prefix = "Activity:";
    private sealed record Ticket(object Target, long Generation, PlayerStateInfo? PlayState, BaseItemDto? Playing, DateTimeOffset Expires);

    public DashboardActivity(ITaskManager tasks, ISessionManager sessions, ILogger logger)
    {
        this.tasks = tasks; this.sessions = sessions; this.logger = logger;
        tasks.TaskExecuting += OnExecuting;
        tasks.TaskCompleted += OnCompleted;
    }
    private void OnExecuting(object? sender, GenericEventArgs<IScheduledTaskWorker> e)
    {
        lock (gate) { if (!disposed) generations[e.Argument] = ++generation; }
    }
    private void OnCompleted(object? sender, TaskCompletionEventArgs e)
    {
        // Generation is advanced by the next TaskExecuting. State/reference are checked again on every command.
        lock (gate) Prune();
    }
    public static bool IsActivityCommand(string command) => command.StartsWith(Prefix, StringComparison.Ordinal);
    public void Append(GenericItemList rows, Func<string, string, string, string> text)
    {
        lock (gate)
        {
            if (disposed) return;
            Prune();
            rows.Add(new() { PrimaryText = text("Activity", "活动", "活動"),
                SecondaryText = text("Snapshot; refresh to read current host state.", "当前快照；点击刷新读取宿主最新状态。", "目前快照；按重新整理讀取宿主最新狀態。"),
                Button1 = new(text("Refresh activity", "刷新活动", "重新整理活動")) { CommandId = "RefreshActivity", IsVisible = true, IsEnabled = true } });
            try
            {
                var active = tasks.ScheduledTasks.Where(t => t.State is TaskState.Running or TaskState.Cancelling).ToArray();
                foreach (var task in active.Take(64))
                {
                    if (!generations.TryGetValue(task, out var epoch)) generations[task] = epoch = ++generation;
                    var value = task.CurrentProgress;
                    var cancel = task.State == TaskState.Running;
                    rows.Add(new() { PrimaryText = task.Name, SecondaryText = task.Category + " · " + task.State,
                        HasPercentage = value.HasValue && double.IsFinite(value.Value), PercentComplete = value.HasValue && double.IsFinite(value.Value) ? (int)Math.Clamp(value.Value,0,100) : 0,
                        Button1 = new(text("Cancel task", "取消任务", "取消工作")) { CommandId = cancel ? Issue(new(task,epoch,null,null,DateTimeOffset.UtcNow.AddMinutes(5))) : "", IsVisible = true, IsEnabled = cancel } });
                }
                if (active.Length == 0) rows.Add(new() { PrimaryText = text("No active tasks", "没有运行中的任务", "沒有執行中的工作") });
                if (active.Length > 64) rows.Add(new() { PrimaryText = text("Additional tasks", "其他活动任务", "其他活動工作"), SecondaryText = (active.Length - 64).ToString() });
            }
            catch (Exception error) { rows.Add(new() { PrimaryText = text("Task snapshot unavailable", "任务快照读取失败", "工作快照讀取失敗"), SecondaryText = error.GetType().Name }); }
            try
            {
                var active = sessions.Sessions.Where(s => s.NowPlayingItem != null || s.FullNowPlayingItem != null || s.IsActive && s.LastActivityDate >= DateTimeOffset.UtcNow.AddMinutes(-15)).ToArray();
                foreach (var session in active.Take(64))
                {
                    var playing = session.NowPlayingItem;
                    var state = session.PlayState;
                    var running = playing != null || session.FullNowPlayingItem != null;
                    var control = running && session.SupportsRemoteControl;
                    var description = running ? (state.IsPaused ? text("Paused", "已暂停", "已暫停") : text("Playing", "播放中", "播放中")) + " · " + (playing?.Name ?? session.FullNowPlayingItem?.Name) : text("Connected", "已连接", "已連線");
                    rows.Add(new() { PrimaryText = string.Join(" · ", new[] { session.UserName, session.Client, session.DeviceName }.Where(s => !string.IsNullOrWhiteSpace(s))),
                        SecondaryText = description,
                        Button1 = running ? new(text("Stop playback", "停止播放", "停止播放")) { CommandId = control ? Issue(new(session,0,state,playing,DateTimeOffset.UtcNow.AddMinutes(5))) : "", IsEnabled = control, IsVisible = true,
                            ConfirmationPrompt = text("Send a stop request to this playback session?", "向此播放会话发送停止请求？", "向此播放工作階段傳送停止請求？") } : null });
                }
                if (active.Length == 0) rows.Add(new() { PrimaryText = text("No active sessions", "没有活动会话", "沒有活動工作階段") });
                if (active.Length > 64) rows.Add(new() { PrimaryText = text("Additional sessions", "其他活动会话", "其他活動工作階段"), SecondaryText = (active.Length - 64).ToString() });
            }
            catch (Exception error) { rows.Add(new() { PrimaryText = text("Session snapshot unavailable", "会话快照读取失败", "工作階段快照讀取失敗"), SecondaryText = error.GetType().Name }); }
        }
    }
    /// <summary>M148 dashboard counts: running scheduled tasks, active sessions (playing or active in 15 minutes) and playing sessions.</summary>
    public (int RunningTasks, int Sessions, int Playing) Counts()
    {
        var running = 0; var active = 0; var playing = 0;
        try { running = tasks.ScheduledTasks.Count(t => t.State == TaskState.Running); } catch (Exception error) { logger.Debug("[StrmAssistantRebuild] Task count: {0}", error.GetType().Name); }
        try
        {
            foreach (var s in sessions.Sessions)
            {
                var isPlaying = s.NowPlayingItem != null || s.FullNowPlayingItem != null;
                if (isPlaying) playing++;
                if (isPlaying || s.IsActive && s.LastActivityDate >= DateTimeOffset.UtcNow.AddMinutes(-15)) active++;
            }
        }
        catch (Exception error) { logger.Debug("[StrmAssistantRebuild] Session count: {0}", error.GetType().Name); }
        return (running, active, playing);
    }
    /// <summary>M148 dashboard: administrator cancels every currently running scheduled task (native ITaskManager.Cancel).</summary>
    public int CancelRunningTasks(UserDto user)
    {
        if (user?.Policy?.IsAdministrator != true) throw new SecurityException("Administrator access required.");
        var running = tasks.ScheduledTasks.Where(t => t.State == TaskState.Running).ToArray();
        foreach (var task in running) tasks.Cancel(task);
        if (running.Length > 0) logger.Info("[StrmAssistantRebuild] Administrator cancelled {0} running scheduled task(s) from the dashboard.", running.Length);
        return running.Length;
    }
    public async Task<string> RunAsync(string command, UserDto user, CancellationToken token)
    {
        if (user?.Policy?.IsAdministrator != true) throw new SecurityException("Administrator access required.");
        token.ThrowIfCancellationRequested();
        Ticket ticket;
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(DashboardActivity));
            Prune();
            if (!IsActivityCommand(command) || !tickets.Remove(command, out ticket!)) throw new InvalidOperationException("Activity action expired; refresh the view.");
            if (ticket.Target is IScheduledTaskWorker worker)
            {
                if (!tasks.ScheduledTasks.Any(current => ReferenceEquals(current,worker)) || worker.State != TaskState.Running ||
                    !generations.TryGetValue(worker,out var epoch) || epoch != ticket.Generation)
                    throw new InvalidOperationException("The observed task run has changed; refresh the view.");
                token.ThrowIfCancellationRequested(); tasks.Cancel(worker);
                return "Task cancellation requested; refresh to confirm the settled state.";
            }
        }
        var session = (SessionInfo)ticket.Target;
        if (!sessions.Sessions.Any(current => ReferenceEquals(current,session)) || !ReferenceEquals(session.PlayState,ticket.PlayState) ||
            !ReferenceEquals(session.NowPlayingItem,ticket.Playing) || !session.SupportsRemoteControl || session.NowPlayingItem == null && session.FullNowPlayingItem == null)
            throw new InvalidOperationException("The observed playback session has changed; refresh the view.");
        token.ThrowIfCancellationRequested();
        // This sends the host's native control message. It does not fake PlaybackStopped, logout, or terminate a process.
        await sessions.SendPlaystateCommand(null!,session.Id,new PlaystateRequest { Command = PlaystateCommand.Stop, ControllingUserId = user.Id },token).ConfigureAwait(false);
        logger.Info("[StrmAssistantRebuild] Administrator requested native playback stop.");
        return "Playback stop requested; client acknowledgement is not implied.";
    }
    private string Issue(Ticket ticket)
    {
        // Bound memory even if a client refreshes rapidly or never sends the buttons back.
        while (tickets.Count >= 256) tickets.Remove(tickets.MinBy(pair => pair.Value.Expires).Key);
        var key = Prefix + Guid.NewGuid().ToString("N"); tickets[key] = ticket; return key;
    }
    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in tickets.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) tickets.Remove(key);
        foreach (var task in generations.Keys.Where(t => t.State == TaskState.Idle).ToArray()) generations.Remove(task);
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; tickets.Clear(); generations.Clear(); }
        tasks.TaskExecuting -= OnExecuting; tasks.TaskCompleted -= OnCompleted;
    }
}
