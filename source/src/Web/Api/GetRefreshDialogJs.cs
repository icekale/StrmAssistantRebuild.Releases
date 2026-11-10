using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Unauthenticated]
[Route("/{Web}/modules/refreshdialog/refreshdialog.js", "GET", IsHidden = true)]
public class GetRefreshDialogJs
{
    public string Web { get; set; } = null!;
}
