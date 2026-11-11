using System.Globalization;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.Common;

/// <summary>Host snapshot for the dashboard; collected by the plugin, never by this builder.</summary>
public sealed record DashboardSnapshot(
    long WorkingSetBytes, int? MemoryLimitMB, double? RestartCountdownSeconds, bool RestartBusyPending,
    int RunningScheduledTasks, int CatchupTasks, int PendingRefreshRequests, int ActiveSessions, int PlayingSessions,
    long? SearchIndexBytes, bool SearchIndexEnabled, bool SearchIndexBusy, double SearchIndexProgress);

/// <summary>
/// M148: dashboard layout of the original (decompiled DashboardOptions + its refresh routine): a "Memory Usage" item
/// (memory icon, settings button) with five small sub-items, and a "Search Index" item with Rebuild / Optimize.
/// Command semantics are the rebuild's: scheduled tasks and catch-up items can be cancelled; the native refresh queue
/// and sessions are display-only (no public cancel API / no mass playback stop). Optimize has no equivalent for the
/// rebuild's file index and stays disabled.
/// </summary>
public static class DashboardOverview
{
    public const string ConfigureMemory = "ConfigureMemory";
    public const string CancelScheduledTasks = "CancelScheduledTasks";
    public const string CancelCatchup = "CancelCatchupTasks";
    public const string RebuildIndex = "RebuildSearchIndex";
    public const string CancelIndexRebuild = "CancelSearchRebuild";

    public static GenericItemList Build(DashboardSnapshot s)
    {
        var memory = new GenericListItem
        {
            PrimaryText = Resources.DashboardOptions_Memory_Usage,
            SecondaryText = Mb(s.WorkingSetBytes) + (s.MemoryLimitMB is { } limit ? " / " + limit.ToString(CultureInfo.InvariantCulture) + " MB" : ""),
            Status = ItemStatus.Unknown, Icon = IconNames.memory, IconMode = ItemListIconMode.LargeRegular,
            Button1 = new ButtonItem { Icon = IconNames.settings, CommandId = ConfigureMemory, IsEnabled = true, IsVisible = true },
            SubItems = new List<GenericListItem>
            {
                Sub(string.Format(Resources.Dashboard_Running_Scheduled_Tasks, s.RunningScheduledTasks), IconNames.fact_check,
                    Cancel(CancelScheduledTasks, s.RunningScheduledTasks > 0, UiLanguage.Text("Cancel all running scheduled tasks?", "取消所有正在运行的计划任务？", "取消所有正在執行的排程工作？"))),
                Sub(string.Format(Resources.Dashboard_Running_Catchup_Tasks, s.CatchupTasks), IconNames.fact_check,
                    Cancel(CancelCatchup, s.CatchupTasks > 0, UiLanguage.Text("Clear queued catch-up items and stop the running batch?", "清空排队中的追更项目并停止当前批次？", "清空排隊中的追更項目並停止目前批次？"))),
                Sub(string.Format(Resources.Dashboard_Pending_Refresh_Requests, s.PendingRefreshRequests), IconNames.fact_check, Cancel("", false, null)),
                Sub(s.PlayingSessions > 0 ? string.Format(Resources.Dashboard_Active_Sessions_Playing, s.ActiveSessions, s.PlayingSessions)
                    : string.Format(Resources.Dashboard_Active_Sessions, s.ActiveSessions), IconNames.fact_check, Cancel("", false, null))
            }
        };
        if (s.MemoryLimitMB.HasValue && (s.RestartCountdownSeconds.HasValue || s.RestartBusyPending))
            memory.SubItems.Add(Sub(s.RestartCountdownSeconds is { } seconds ? string.Format(Resources.Dashboard_Restart_In_Countdown, seconds / 60.0)
                : Resources.Dashboard_Restart_Pending, IconNames.schedule, null));
        var index = new GenericListItem
        {
            PrimaryText = Resources.DashboardOptions_Search_Index,
            SecondaryText = s.SearchIndexBusy ? Resources.Dashboard_Search_Index_Rebuilding
                : s.SearchIndexBytes is { } bytes ? Mb(bytes) : UiLanguage.Text("Not built", "尚未建立", "尚未建立"),
            HasPercentage = s.SearchIndexBusy, PercentComplete = s.SearchIndexBusy ? (int)Math.Clamp(s.SearchIndexProgress, 0, 100) : 0,
            Status = ItemStatus.Unknown, Icon = IconNames.manage_search, IconMode = ItemListIconMode.LargeRegular,
            Button1 = new ButtonItem
            {
                Caption = Resources.Dashboard_Search_Index_Rebuild, Icon = IconNames.cached, CommandId = RebuildIndex,
                ConfirmationPrompt = Resources.Dashboard_Search_Index_Rebuild_Confirm, IsVisible = true,
                IsEnabled = s.SearchIndexEnabled && !s.SearchIndexBusy
            },
            Button2 = s.SearchIndexBusy
                ? new ButtonItem { Caption = UiLanguage.Text("Cancel", "取消", "取消"), Icon = IconNames.cancel, CommandId = CancelIndexRebuild, IsEnabled = true, IsVisible = true }
                : new ButtonItem { Caption = Resources.Dashboard_Search_Index_Optimize, Icon = IconNames.build, CommandId = "", IsEnabled = false, IsVisible = true }
        };
        return new GenericItemList { memory, index };
    }

    private static GenericListItem Sub(string text, IconNames icon, ButtonItem? button) =>
        new() { PrimaryText = text, Icon = icon, IconMode = ItemListIconMode.SmallRegular, Button1 = button };

    private static ButtonItem Cancel(string command, bool enabled, string? confirm) =>
        new() { Icon = IconNames.cancel, CommandId = command, IsEnabled = enabled && command.Length > 0, IsVisible = true, ConfirmationPrompt = confirm };

    private static string Mb(long bytes) => bytes < 1024 * 1024
        ? (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB"
        : (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";
}
