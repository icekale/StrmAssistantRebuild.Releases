using System.Globalization;
using Emby.Notifications;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;

/// <summary>Notification for the local deep-delete subset after target retirement and repository commit.</summary>
public sealed class DeepDeleteNotification
{
    public const string EventId="strmassistant.rebuild.deepdelete.completed";
    private readonly ILibraryManager library;
    private readonly IUserManager users;
    private readonly INotificationManager notifications;
    private readonly Func<bool> enabled;
    private readonly ILogger logger;
    private long emitted;
    public long EmittedRequests=>Interlocked.Read(ref emitted);
    public bool IsEnabled { get { try{return enabled();}catch{return false;} } }
    public DeepDeleteNotification(ILibraryManager library,IUserManager users,INotificationManager notifications,Func<bool> enabled,ILogger logger)
    {this.library=library;this.users=users;this.notifications=notifications;this.enabled=enabled;this.logger=logger;}
    public sealed class Pending
    {
        internal readonly BaseItem Item;
        internal readonly long UserId;
        internal readonly long ItemId;
        internal readonly string[] Targets;
        internal readonly BaseItem[] CollectionFolders;
        internal int Consumed;
        internal Pending(BaseItem item,long user,string[] targets,BaseItem[] folders){Item=item;ItemId=item.InternalId;UserId=user;Targets=targets;CollectionFolders=folders;}
    }
    public Pending? Prepare(BaseItem item,User? actor,IEnumerable<string> targets,CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if(!enabled()||item.InternalId<=0||item is not (Movie or Episode)||!item.IsShortcut||actor?.InternalId is not > 0)return null;
            var selected=targets.Where(p=>!string.IsNullOrWhiteSpace(p)&&p!=item.Path).Distinct(StringComparer.Ordinal).ToArray();
            if(selected.Length==0)return null;
            var user=users.GetUserById(actor.InternalId);
            if(user?.InternalId!=actor.InternalId||user.Policy?.IsDisabled!=false||!library.FilterItemsToIdsForUser(new[]{item},user,null,token).Contains(item.InternalId))return null;
            var folders=library.GetCollectionFolders(item,token).ToArray();
            token.ThrowIfCancellationRequested();return new Pending(item,user.InternalId,selected,folders);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){return null;}
        catch(Exception error){Warn(error);return null;}
    }
    // The caller invokes this only after repository.DeleteItems succeeds, outside its rollback catch.
    public void Complete(Pending? pending,IReadOnlyList<(string Source,string Archive)> retired,CancellationToken token)
    {
        if(pending==null||Interlocked.Exchange(ref pending.Consumed,1)!=0)return;
        try
        {
            token.ThrowIfCancellationRequested();if(!enabled()||pending.Item.InternalId!=pending.ItemId)return;
            var count=retired.Where(p=>!string.IsNullOrEmpty(p.Archive)&&p.Source!=p.Archive&&pending.Targets.Contains(p.Source,StringComparer.Ordinal)).Select(p=>p.Source).Distinct(StringComparer.Ordinal).Count();
            if(count==0)return;
            var user=users.GetUserById(pending.UserId);
            if(user?.InternalId!=pending.UserId||user.Policy?.IsDisabled!=false)return;
            token.ThrowIfCancellationRequested();
            notifications.SendNotification(new NotificationRequest{EventId=EventId,User=user,Item=pending.Item,CollectionFolders=pending.CollectionFolders,Title=Title(CultureInfo.CurrentUICulture.Name),Description=$"Local STRM version removed; {count} target file(s) retained as recovery backups.",CancellationToken=token});
            Interlocked.Increment(ref emitted);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){ }
        catch(Exception error){Warn(error);}
    }
    private void Warn(Exception error){try{logger.Warn("[DeepDeleteNotification] Notification unavailable: {0}",error.GetType().Name);}catch{}}
    public static string Title(string? language)=>language?.StartsWith("zh",StringComparison.OrdinalIgnoreCase)==true
        ? language.Contains("Hant",StringComparison.OrdinalIgnoreCase)||language.Equals("zh-TW",StringComparison.OrdinalIgnoreCase)||language.Equals("zh-HK",StringComparison.OrdinalIgnoreCase)?"本機深度刪除已完成（保留復原備份）":"本地深度删除已完成（保留恢复备份）"
        : "Local deep-delete completed (recovery backups retained)";
}
