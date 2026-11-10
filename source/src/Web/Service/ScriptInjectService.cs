using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistantRebuild.Web.Api;

namespace StrmAssistantRebuild.Web.Service;

[Unauthenticated]
public sealed class ScriptInjectService : IService, IRequiresRequest
{
    private readonly IHttpResultFactory results;
    public ScriptInjectService(IHttpResultFactory resultFactory) => results = resultFactory;
    public IRequest Request { get; set; } = null!;
    private object Asset(string path)
    {
        var manager = Plugin.Instance?.DashboardAssets;
        if (manager == null) return Error(503, "Web assets are not initialized.");
        var bytes = manager.Get(path);
        if (bytes.Length == 0) return Error(404, "Dashboard asset not available for this host.");
        return results.GetResult(Request, bytes, path.EndsWith(".html", StringComparison.Ordinal) ? "text/html; charset=utf-8" : "application/javascript; charset=utf-8",
            new Dictionary<string, string> { ["Cache-Control"] = "no-cache", ["X-Content-Type-Options"] = "nosniff" });
    }
    private object Error(int status, string text)
    {
        var response = results.GetResult(Request, (ReadOnlyMemory<byte>)System.Text.Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8");
        if (response is IHttpResult result) result.StatusCode = (System.Net.HttpStatusCode)status;
        Request.Response.StatusCode = status;
        return response;
    }
    public object Get(GetStrmAssistantJs request) => Asset("components/strmassistant/strmassistant.js");
    public object Get(GetShortcutJs request) => Asset("modules/shortcuts.js");
    public object Get(GetMediaInfoJs request) => Asset("modules/mediainfo/mediainfo.js");
    public object Get(GetRefreshDialogJs request) => Asset("modules/refreshdialog/refreshdialog.js");
    public object Get(GetListControllerJs request) => Asset("modules/tabbedview/listcontroller.js");
    public object Get(GetIndicatorJs request) => Asset("modules/indicators/indicators.js");
    public object Get(GetFilterMenuHtml request) => Asset("modules/filtermenu/filtermenu.template.html");
    public object Get(GetItemJs request) => Asset("item/item.js");
    public object Get(GetLinkedItemsJs request) => Asset("item/linkeditems.js");
    public object Get(GetListJs request) => Asset("list/list.js");
    public object Get(GetLibraryOptionsEditorJs request) => Asset("components/libraryoptionseditor/libraryoptionseditor.js");
}
