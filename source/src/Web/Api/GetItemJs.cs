using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Unauthenticated]
[Route("/{Web}/item/item.js", "GET", IsHidden = true)]
public class GetItemJs
{
    public string Web { get; set; } = null!;
}
