using System.Security;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Plugins.UI.Views.Enums;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.Options.UIBaseClasses;

/// <summary>Native GenericUI editor; the framework owns DialogOk/Cancel and returning to the parent.</summary>
public sealed class MemoryDialogView : IPluginDialogView, IPluginViewWithOptions
{
    private readonly IPluginUIView parent;
    private readonly SimpleFileStore<DashboardOptions> store;
    private bool allowOk = true, allowCancel = true;

    public MemoryDialogView(IPluginUIView parent, SimpleFileStore<DashboardOptions> store)
    {
        this.parent = parent;
        this.store = store;
        PluginId = parent.PluginId;
        User = parent.User;
        RequireAdministrator();
        ContentData = SimpleFileStore<MemoryOptions>.Copy(store.GetOptions().MemoryOptions);
    }

    public string Caption => Resources.MemoryOptions_EditorTitle;
    public string SubCaption => string.Empty;
    public string PluginId { get; }
    public IEditableObject ContentData { get; set; }
    public UserDto User { get; set; }
    public string RedirectViewUrl { get; set; } = string.Empty;
    public bool AllowOk { get => allowOk && User?.Policy?.IsAdministrator == true; set => allowOk = value; }
    public bool AllowCancel { get => allowCancel && User?.Policy?.IsAdministrator == true; set => allowCancel = value; }
    public bool ShowDialogFullScreen => false;
    public PluginViewOptions ViewOptions { get; } = new()
    {
        OKButtonCaption = Resources.SaveButtonCaption,
        DialogSize = DialogSize.Small,
        PrimaryDialogAction = DialogAction.AddOrEdit
    };
    public event EventHandler<GenericEventArgs<IPluginUIView>>? UIViewInfoChanged { add { } remove { } }

    public bool IsCommandAllowed(string command) => command == "DialogOk" && AllowOk || command == "DialogCancel" && AllowCancel;
    public Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
    {
        RequireAdministrator();
        // Null delegates to DialogViewBase: OnOk/OnCancel -> parent.OnDialogResult -> original host.
        // Returning this would swallow the native close command and leave the dialog open.
        return Task.FromResult<IPluginUIView>(null!);
    }
    public Task OnOkCommand(string itemId, string commandId, string data)
    {
        RequireAdministrator();
        var memory = ContentData as MemoryOptions ?? throw new ArgumentException("Expected MemoryOptions.");
        if (memory.MemoryUsageLimitMB is < 500)
            throw new ArgumentException("MemoryUsageLimitMB must be at least 500 MB, or left blank to disable the monitor.");
        var dashboard = store.GetOptions();
        dashboard.MemoryOptions = SimpleFileStore<MemoryOptions>.Copy(memory);
        if (!store.Save(dashboard))
            throw new IOException("Memory settings have not been saved. Storage is read-only or unavailable; retry Save or cancel.");
        // Keep the returning editor current so a subsequent PageSave cannot restore the old limit.
        if (parent.ContentData is DashboardOptions original)
            original.MemoryOptions = SimpleFileStore<MemoryOptions>.Copy(memory);
        return Task.CompletedTask;
    }
    public Task OnCancelCommand() { RequireAdministrator(); return Task.CompletedTask; }
    public Task Cancel() => Task.CompletedTask;
    public void OnDialogResult(IPluginUIView dialog, bool success, object result) { }
    private void RequireAdministrator()
    {
        if (User?.Policy?.IsAdministrator != true) throw new SecurityException("Administrator access required.");
    }
}
