using System.ComponentModel;
using System.Security;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using IEditableObject = MediaBrowser.Model.GenericEdit.IEditableObject;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Plugins.UI.Views.Enums;

namespace StrmAssistantRebuild.Options.UIBaseClasses;

/// <summary>Read-only native detail view; no configuration fields, files, or actions are written.</summary>
public sealed class RuntimeDiagnosticsDialog : IPluginDialogView, IPluginViewWithOptions
{
    public RuntimeDiagnosticsDialog(IPluginUIView parent, string caption, string details, string closeCaption)
    {
        PluginId = parent.PluginId; User = parent.User; Caption = caption;
        RequireAdmin();
        ContentData = new RuntimeDiagnosticsContent { Report = new StatusItem { Caption = caption, StatusText = details, Status = ItemStatus.None } };
        ViewOptions.OKButtonCaption = closeCaption;
    }
    public string Caption { get; }
    public string SubCaption => "";
    public string PluginId { get; }
    public IEditableObject ContentData { get; set; }
    public UserDto User { get; set; }
    public string RedirectViewUrl { get; set; } = "";
    public bool AllowOk { get => User?.Policy?.IsAdministrator == true; set { } }
    public bool AllowCancel { get => User?.Policy?.IsAdministrator == true; set { } }
    public bool ShowDialogFullScreen => false;
    public PluginViewOptions ViewOptions { get; } = new() { DialogSize = DialogSize.Small, PrimaryDialogAction = DialogAction.AddOrEdit };
    public event EventHandler<GenericEventArgs<IPluginUIView>>? UIViewInfoChanged { add { } remove { } }
    public bool IsCommandAllowed(string command) => User?.Policy?.IsAdministrator == true && command is "DialogOk" or "DialogCancel";
    public Task<IPluginUIView> RunCommand(string itemId, string commandId, string data) { RequireAdmin(); return Task.FromResult<IPluginUIView>(null!); }
    public Task OnOkCommand(string itemId, string commandId, string data) { RequireAdmin(); return Task.CompletedTask; }
    public Task OnCancelCommand() { RequireAdmin(); return Task.CompletedTask; }
    public Task Cancel() => Task.CompletedTask;
    public void OnDialogResult(IPluginUIView dialog, bool success, object result) { }
    private void RequireAdmin() { if (User?.Policy?.IsAdministrator != true) throw new SecurityException("Administrator access required."); }
}
public sealed class RuntimeDiagnosticsContent : CultureAwareOptionsBase
{
    [Browsable(false)] public override string EditorTitle => "";
    [DisplayName("")] public StatusItem Report { get; set; } = new();
}
