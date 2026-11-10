using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Route("/{Web}/modules/shortcuts.js", "GET", IsHidden = true)]
[Unauthenticated]
public class GetShortcutJs
{
    public string Web { get; set; } = null!;
}
