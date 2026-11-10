using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Route("/{Web}/components/strmassistant/strmassistant.js", "GET", IsHidden = true)]
[Unauthenticated]
public class GetStrmAssistantJs
{
    public string Web { get; set; } = null!;
    public string ResourceName { get; set; } = null!;
}
