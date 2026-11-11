using MediaBrowser.Model.Tasks;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class DeletePersonTask : IScheduledTask, IConfigurableScheduledTask
{
    public string Name => Common.UiLanguage.Text("Delete Persons (Debug metadata reset)", "删除人员（调试：重置人员元数据）", "刪除人員（偵錯：重設人員中繼資料）");
    public string Description => Resources.DeletePersonTask_Description_Deletes_all_persons;
    public string Category => "Strm Assistant Rebuild";
    public string Key => "DeletePersonTask";
    public bool IsEnabled => Plugin.Instance?.MaintenanceService?.CanDeletePersons == true;
    public bool IsHidden => !IsEnabled;
    public bool IsLogged => true;
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) =>
        (Plugin.Instance?.MaintenanceService ?? throw new InvalidOperationException("Maintenance service not initialized."))
        .DeletePersonsAsync(cancellationToken, progress);
}
