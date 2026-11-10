using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Unauthenticated]
[Route("/{Web}/components/libraryoptionseditor/libraryoptionseditor.js", "GET", IsHidden = true)]
public class GetLibraryOptionsEditorJs
{
    public string Web { get; set; } = null!;
}
