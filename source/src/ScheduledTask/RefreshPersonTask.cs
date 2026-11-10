using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class RefreshPersonTask : IScheduledTask
{
    private readonly IServerConfigurationManager configuration;
    public RefreshPersonTask(ILibraryManager library, IServerConfigurationManager configuration) { this.configuration = configuration; }
    public string Name => Resources.RefreshPersonTask_Name_Refresh_Chinese_Actor;
    public string Description => Resources.RefreshPersonTask_Description_Refreshes_and_repairs_Chinese_actors;
    public string Category => "Strm Assistant Rebuild";
    public string Key => "RefreshPersonTask";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public async Task Execute(CancellationToken token, IProgress<double> progress)
    {
        token.ThrowIfCancellationRequested();
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        if (!string.Equals((configuration.Configuration.PreferredMetadataLanguage ?? "").Split('-')[0], "zh", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Resources.ServerPreferredMetadataLanguageIsNotZh);
        var service = plugin.PersonRefreshService ?? throw new InvalidOperationException("Person refresh service not initialized.");
        await service.ExecuteAsync(token, progress).ConfigureAwait(false);
    }
}
