using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Route("/Items/{Id}/ClearMediaInfo", "POST")]
[Authenticated(Roles = "Admin")]
public class ClearMediaInfo : IReturnVoid, IReturn
{
    [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string", ParameterType = "path")]
    public string Id { get; set; } = null!;
}
