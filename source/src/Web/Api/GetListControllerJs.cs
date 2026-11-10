using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Route("/{Web}/modules/tabbedview/listcontroller.js", "GET", IsHidden = true)]
[Unauthenticated]
public class GetListControllerJs
{
    public string Web { get; set; } = null!;
}
