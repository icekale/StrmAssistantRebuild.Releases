using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace StrmAssistantRebuild.Common;

/// <summary>Resolve the captured identity, not the current request actor, just before optional handoff.</summary>
internal static class NotificationActor
{
    internal static User? Current(IUserManager? users,User captured,long id,bool requireAdmin)
    {
        if(id<=0)return null;
        var current=users==null?captured:users.GetUserById(id);
        return current?.InternalId==id&&current.Policy?.IsDisabled==false&&(!requireAdmin||current.Policy.IsAdministrator)?current:null;
    }
}
