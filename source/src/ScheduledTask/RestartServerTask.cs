using MediaBrowser.Controller;
using MediaBrowser.Model.Tasks;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class RestartServerTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly IServerApplicationHost host;
    public RestartServerTask(IServerApplicationHost host) => this.host = host;
    public string Name => Common.UiLanguage.Text("Restart Server", "重启服务器", "重新啟動伺服器");
    public string Description => Resources.RestartServerTask_Description_Automatically_restart_server_when_specified_conditions_are_met;
    public string Category => "Strm Assistant Rebuild";
    public string Key => "RestartServerTask";
    public bool IsEnabled => host.CanSelfRestart && !host.IsShuttingDown;
    public bool IsHidden => !IsEnabled;
    public bool IsLogged => true;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(0);
        MaintenanceService.RequestRestart(host, cancellationToken);
        progress.Report(100);
        return Task.CompletedTask;
    }
}
