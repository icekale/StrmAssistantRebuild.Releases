using System.Globalization;
using Emby.Notifications;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace StrmAssistantRebuild.Common;

/// <summary>Opt-in notifications for newly added visible favorites or episodes of favorite series.</summary>
public sealed class FavoriteUpdateNotification : IDisposable
{
    public const string EventId=OriginalNotifications.Favorites; // M164: original event id
    private readonly ILibraryManager library;
    private readonly IUserManager users;
    private readonly IUserDataManager data;
    private readonly INotificationManager notifications;
    private readonly ILogger logger;
    private readonly DelayedCatchupQueue queue;
    private int disposed;
    private long completed, emitted, rejected;
    public Task Completion=>queue.Completion;
    public bool IsBusy=>queue.IsBusy;
    public int PendingCount=>queue.PendingCount;
    public long CompletedItems=>Interlocked.Read(ref completed);
    public long EmittedRequests=>Interlocked.Read(ref emitted);
    public long RejectedEvents=>Interlocked.Read(ref rejected);
    public FavoriteUpdateNotification(ILibraryManager library,IUserManager users,IUserDataManager data,INotificationManager notifications,ILogger logger)
    {
        this.library=library;this.users=users;this.data=data;this.notifications=notifications;this.logger=logger;
        queue=new DelayedCatchupQueue(Process,message=>logger.Warn("[FavoriteUpdate] {0}",message),256);
        queue.Configure(true);library.ItemAdded+=OnAdded;
    }
    private static bool Supported(BaseItem? item)=>item is Movie or Series or Episode;
    private void OnAdded(object? sender,ItemChangeEventArgs e)
    {
        if(Volatile.Read(ref disposed)!=0||!Supported(e.Item)||e.Item.InternalId<=0)return;
        if(!queue.Enqueue(e.Item.InternalId,TimeSpan.Zero))Interlocked.Increment(ref rejected);
    }
    private Task Process(long[] ids,CancellationToken token)
    {
        foreach(var id in ids)
        {
            token.ThrowIfCancellationRequested();
            try{SendForItem(id,token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception error){logger.Warn("[FavoriteUpdate] Item {0}: {1}",id,error.GetType().Name);}
            finally{Interlocked.Increment(ref completed);}
        }
        return Task.CompletedTask;
    }
    internal void SendForItem(long id,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(Volatile.Read(ref disposed)!=0||id<=0)return;
        var item=library.GetItemById(id);if(!Supported(item)||item.InternalId!=id)return;
        var recipients=users.GetUserList(new UserQuery{IsDisabled=false,Limit=1025,EnableTotalRecordCount=false});
        if(recipients.Length>1024){logger.Warn("[FavoriteUpdate] User budget exceeded for item {0}; event retained only in native library state.",id);return;}
        foreach(var recipient in recipients)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if(recipient?.Policy?.IsDisabled!=false||recipient.InternalId<=0||!Visible(item,recipient,token)||!Interested(item,recipient,token))continue;
                // Recheck current account and newly added item immediately before handing off to native rules.
                var user=users.GetUserById(recipient.InternalId);
                if(user?.Policy?.IsDisabled!=false||user.InternalId!=recipient.InternalId||!Visible(item,user,token))continue;
                token.ThrowIfCancellationRequested();if(Volatile.Read(ref disposed)!=0)return;
                notifications.SendNotification(new NotificationRequest{EventId=EventId,User=user,Item=item,Title=Title(CultureInfo.CurrentUICulture.Name),Description=OriginalNotifications.FavoritesDescription(item.Path,user.Name,CultureInfo.CurrentUICulture.Name),CancellationToken=token});
                Interlocked.Increment(ref emitted);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception error){logger.Warn("[FavoriteUpdate] Recipient {0}, item {1}: {2}",recipient?.InternalId,id,error.GetType().Name);}
        }
    }
    private bool Visible(BaseItem item,User user,CancellationToken token)=>library.FilterItemsToIdsForUser(new[]{item},user,null,token).Contains(item.InternalId);
    private bool Favorite(BaseItem item,User user,CancellationToken token)=>data.GetUserData(user,item,token)?.IsFavorite==true;
    private bool Interested(BaseItem item,User user,CancellationToken token)
    {
        IEnumerable<BaseItem> candidates=new[]{item};
        if(!string.IsNullOrWhiteSpace(item.PresentationUniqueKey))
        {
            var versions=library.GetItemList(new InternalItemsQuery(user){Recursive=true,PresentationUniqueKey=item.PresentationUniqueKey,IncludeItemTypes=new[]{item.GetClientTypeName()},GroupByPresentationUniqueKey=false,Limit=129,EnableTotalRecordCount=false},token);
            if(versions.Length>128){logger.Warn("[FavoriteUpdate] Version budget exceeded for item {0}.",item.InternalId);return false;}
            candidates=versions.Where(v=>v!=null&&v.InternalId>0&&v.GetClientTypeName()==item.GetClientTypeName()&&v.PresentationUniqueKey==item.PresentationUniqueKey).Append(item).DistinctBy(v=>v.InternalId);
        }
        foreach(var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            if(!Visible(candidate,user,token))continue;
            if(Favorite(candidate,user,token))return true;
            if(candidate is Episode episode&&episode.SeriesId>0)
            {
                var series=library.GetItemById(episode.SeriesId);
                if(series is Series&&series.InternalId==episode.SeriesId&&Visible(series,user,token)&&Favorite(series,user,token))return true;
            }
        }
        return false;
    }
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;library.ItemAdded-=OnAdded;queue.Dispose();}
    public static string Title(string? language)=>OriginalNotifications.Title(EventId,language);
}
