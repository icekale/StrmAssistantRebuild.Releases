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
    public const string EventId=OriginalNotifications.IntroSkip; // M164: original event id
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
            await sessions!.SendMessageCommand(session.Id,session.Id,new MessageCommand{Header=request.Title,Text=Messages.TryGetValue(request,out var text)?text:request.Description,TimeoutMs=500},token).ConfigureAwait(false);
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
        // Original text (A----------/AA---------.cs:47280-47760): "{series} - {season} - Intro marker updated" plus times hh:mm:ss.fff and the user.
        var language=CultureInfo.CurrentUICulture.Name;var series=Safe(item.FindSeriesName,item.SeriesName);var season=Safe(item.FindSeasonName,item.ParentIndexNumber?.ToString(CultureInfo.InvariantCulture));
        var parts=new List<string>();
        if(changed.HasIntro)parts.Add(OriginalNotifications.IntroDescription(series,season,changed.IntroStartTicks!.Value,changed.IntroEndTicks!.Value,user.Name,language));
        if(changed.HasCredits)parts.Add(OriginalNotifications.CreditsDescription(series,season,item.RunTimeTicks.Value-changed.CreditsStartTicks!.Value,user.Name,language));
        var message=changed.HasIntro?OriginalNotifications.IntroMessage(series,season,language):OriginalNotifications.CreditsMessage(series,season,language);
        var request=new NotificationRequest{EventId=EventId,User=user,Item=item,Title=Title(language),Description=string.Join(Environment.NewLine+Environment.NewLine,parts),CancellationToken=token};
        Messages.AddOrUpdate(request,message);
        return request;
    }
    // On-screen message per request (the original showed "{series} - {season} - ... marker updated" in the OSD).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NotificationRequest,string> Messages=new();
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
    private static string? Safe(Func<string?> read,string? fallback){try{return read()??fallback;}catch{return fallback;}}
    private void Warn(long item,Exception error){try{logger.Warn("[MarkerUpdate] Notification unavailable for {0}: {1}",item,error.GetType().Name);}catch{}}
    public static string Title(string? language)=>OriginalNotifications.Title(EventId,language);
}
