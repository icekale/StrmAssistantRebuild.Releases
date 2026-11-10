using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Route("/{Web}/list/list.js", "GET", IsHidden = true)]
[Unauthenticated]
public class GetListJs
{
    public string Web { get; set; } = null!;
}
