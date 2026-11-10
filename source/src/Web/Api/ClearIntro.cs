using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Authenticated(Roles = "Admin")]
[Route("/Items/{Id}/ClearIntro", "POST")]
public class ClearIntro : IReturnVoid, IReturn
{
    [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string", ParameterType = "path")]
    public string Id { get; set; } = null!;
}
