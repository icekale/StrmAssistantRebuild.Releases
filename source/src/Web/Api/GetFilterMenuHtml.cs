using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Route("/{Web}/modules/filtermenu/filtermenu.template.html", "GET", IsHidden = true)]
[Unauthenticated]
public class GetFilterMenuHtml
{
    public string Web { get; set; } = null!;
}
