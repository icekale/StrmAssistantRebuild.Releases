using MediaBrowser.Model.Tasks;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class UpdatePluginTask : IScheduledTask, IScheduledTaskWithExecuteOptions, IConfigurableScheduledTask
{
    public string Key => "UpdatePluginTask";
    public string Name => Resources.UpdatePluginTask_Name_Update_Plugin;
    public string Description => Resources.UpdatePluginTask_Description_Updates_plugin_to_the_latest_version;
    public string Category => "Strm Assistant Rebuild";
    public bool IsHidden => !IsEnabled;
    public bool IsEnabled => true;
    public bool IsLogged => true;
    // The original default trigger is not recoverable (yield enumerator not decompiled). Installing a DLL is never
    // unattended by default here; Emby users may add a trigger explicitly. Neither overload restarts the server.
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) => Execute(new TaskOptions(), progress, cancellationToken);
    public async Task Execute(TaskOptions options, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var updater = Plugin.Instance?.PluginUpdateService ?? throw new InvalidOperationException("Rebuild update service is not initialized.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options?.MaxRuntimeTicks is long ticks)
        {
            if (ticks <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxRuntimeTicks must be positive.");
            timeout.CancelAfter(TimeSpan.FromTicks(ticks));
        }
        var result = await updater.UpdateAsync(progress, timeout.Token).ConfigureAwait(false);
        if (result.State is "unavailable" or "failed") throw new InvalidOperationException(result.Detail);
        // HasManualInteraction is not consent to restart. Neither execution overload calls Restart().
    }
}
