using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Api;

[Route("/Items/SyncMediaInfo", "POST")]
[Authenticated(Roles = "Admin")]
public class SyncMediaInfo : List<MediaInfoBundle>, IReturn<List<MediaInfoBundle>>, IReturn
{
    [ApiMember(Name = "Id", Description = "Preferred Item Id", IsRequired = false, DataType = "string", ParameterType = "query")]
    public string Id { get; set; } = null!;
    [ApiMember(Name = "Path", Description = "Optional Item Path", IsRequired = false, DataType = "string", ParameterType = "query")]
    public string Path { get; set; } = null!;
}
