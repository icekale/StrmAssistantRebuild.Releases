using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Authenticated(Roles = "Admin")]
[Route("/Items/{Id}/ClearChapterImage", "POST")]
public class ClearChapterImage : IReturnVoid, IReturn
{
    [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string", ParameterType = "path")]
    public string Id { get; set; } = null!;
}
