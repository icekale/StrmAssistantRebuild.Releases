using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Authenticated(Roles = "Admin")]
[Route("/Items/{ItemId}/Lock", "POST")]
public class LockItem : IReturnVoid, IReturn
{
    [ApiMember(Name = "ItemId", Description = "Item Id", IsRequired = true, DataType = "string", ParameterType = "path")]
    public string ItemId { get; set; } = null!;
    [ApiMember(Name = "LockData", Description = "Indicates if lock or unlock metadata.", IsRequired = true, DataType = "bool", ParameterType = "query")]
    public bool LockData { get; set; }
}
