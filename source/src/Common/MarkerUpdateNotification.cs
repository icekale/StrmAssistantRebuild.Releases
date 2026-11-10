using System.Globalization;
using Emby.Notifications;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using StrmAssistantRebuild.IntroSkip;

namespace StrmAssistantRebuild.Common;

/// <summary>Optional native notification after verified playback-derived marker persistence.</summary>
public sealed class MarkerUpdateNotification
{
    public const string EventId="strmassistant.rebuild.markers.updated";
    private readonly ILibraryManager library;
    private readonly IUserManager users;
    private readonly INotificationManager notifications;
    private readonly ILogger logger;
    private readonly ISessionManager? sessions;
    private long emitted, displayed;
    public long EmittedRequests=>Interlocked.Read(ref emitted);
    public long DisplayedRequests=>Interlocked.Read(ref displayed);
    public MarkerUpdateNotification(ILibraryManager library,IUserManager users,INotificationManager notifications,ILogger logger,ISessionManager? sessions=null)
    {this.library=library;this.users=users;this.notifications=notifications;this.logger=logger;this.sessions=sessions;}
    public void Send(Episode source,long userId,IntroMarkerSet changed,CancellationToken token)
    {
        try { var request=Create(source,userId,changed,token);if(request!=null)Dispatch(request); }
        catch(OperationCanceledException) when(token.IsCancellationRequested){ }
        catch(Exception error){Warn(source.InternalId,error);}
    }
    public async Task SendAsync(Episode source,long userId,string? serverSessionId,IntroMarkerSet changed,CancellationToken token)
    {
        try
        {
            var request=Create(source,userId,changed,token);if(request==null)return;
            request.Session=FindSession(serverSessionId,userId,source.InternalId);
            // Notification subscriptions and OSD are independent destinations; one failing does not
            // suppress the other or change an already persisted marker. The worker awaits OSD.
            try { Dispatch(request); }
            catch(Exception error){Warn(source.InternalId,error);}
            token.ThrowIfCancellationRequested();
            var session=FindSession(serverSessionId,userId,source.InternalId);
            if(session?.SupportedCommands?.Contains("DisplayMessage",StringComparer.Ordinal)!=true)return;
            var user=users.GetUserById(userId);
            if(user?.InternalId!=userId||user.Policy?.IsDisabled!=false||!library.FilterItemsToIdsForUser(new[]{request.Item},user,null,token).Contains(source.InternalId))return;
            token.ThrowIfCancellationRequested();
            await sessions!.SendMessageCommand(session.Id,session.Id,new MessageCommand{Header=request.Title,Text=request.Description,TimeoutMs=500},token).ConfigureAwait(false);
            Interlocked.Increment(ref displayed);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){ }
        catch(Exception error){Warn(source.InternalId,error);}
    }
    private NotificationRequest? Create(Episode source,long userId,IntroMarkerSet changed,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(source.InternalId<=0||userId<=0)return null;
        var user=users.GetUserById(userId);
        if(user?.InternalId!=userId||user.Policy?.IsDisabled!=false)return null;
        var item=library.GetItemById(source.InternalId) as Episode;
        if(item?.InternalId!=source.InternalId||item.RunTimeTicks is not > 0)return null;
        changed.Validate(item.RunTimeTicks);
        if(!library.FilterItemsToIdsForUser(new[]{item},user,null,token).Contains(item.InternalId))return null;
        token.ThrowIfCancellationRequested();
        var parts=new List<string>();
        if(changed.HasIntro)parts.Add("Intro: "+TimeSpan.FromTicks(changed.IntroStartTicks!.Value).ToString("c",CultureInfo.InvariantCulture)+" - "+TimeSpan.FromTicks(changed.IntroEndTicks!.Value).ToString("c",CultureInfo.InvariantCulture));
        if(changed.HasCredits)parts.Add("Credits duration: "+TimeSpan.FromTicks(item.RunTimeTicks.Value-changed.CreditsStartTicks!.Value).ToString("c",CultureInfo.InvariantCulture));
        return new NotificationRequest{EventId=EventId,User=user,Item=item,Title=Title(CultureInfo.CurrentUICulture.Name),Description=string.Join(Environment.NewLine,parts),CancellationToken=token};
    }
    private void Dispatch(NotificationRequest request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();notifications.SendNotification(request);Interlocked.Increment(ref emitted);
    }
    private SessionInfo? FindSession(string? id,long user,long item)
    {
        if(string.IsNullOrEmpty(id)||sessions==null)return null;
        try
        {
            var matches=sessions.Sessions.Where(s=>s.Id==id).Take(2).ToArray();
            if(matches.Length!=1||matches[0].UserInternalId!=user)return null;
            var session=matches[0];
            // A stop report may have cleared the now-playing item. A new/different item means this
            // device moved on; never display the old operation over that playback.
            if(session.FullNowPlayingItem!=null&&session.FullNowPlayingItem.InternalId!=item)return null;
            if(session.FullNowPlayingItem==null&&!string.IsNullOrEmpty(session.NowPlayingItem?.Id)&&library.GetInternalId(session.NowPlayingItem.Id)!=item)return null;
            return session;
        }
        catch(Exception error){Warn(item,error);return null;}
    }
    private void Warn(long item,Exception error){try{logger.Warn("[MarkerUpdate] Notification unavailable for {0}: {1}",item,error.GetType().Name);}catch{}}
    public static string Title(string? language)=>language?.StartsWith("zh",StringComparison.OrdinalIgnoreCase)==true
        ? language.Contains("Hant",StringComparison.OrdinalIgnoreCase)||language.Equals("zh-TW",StringComparison.OrdinalIgnoreCase)||language.Equals("zh-HK",StringComparison.OrdinalIgnoreCase)?"片頭片尾標記已更新":"片头片尾标记已更新"
        : "Intro/credits markers updated";
}
