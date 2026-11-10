using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Route("/{Web}/item/linkeditems.js", "GET", IsHidden = true)]
[Unauthenticated]
public class GetLinkedItemsJs
{
    public string Web { get; set; } = null!;
}
