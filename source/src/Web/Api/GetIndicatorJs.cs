using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Route("/{Web}/modules/indicators/indicators.js", "GET", IsHidden = true)]
[Unauthenticated]
public class GetIndicatorJs
{
    public string Web { get; set; } = null!;
}
