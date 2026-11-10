using Emby.Notifications;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Notification;

/// <summary>Playback marker events are independently discoverable; no transport is enabled automatically.</summary>
public sealed class MarkerNotificationTypes : INotificationTypeFactory
{
    public List<NotificationTypeInfo> GetNotificationTypes(string language)=>new()
    {
        new NotificationTypeInfo {Id=MarkerUpdateNotification.EventId,Name=MarkerUpdateNotification.Title(language),CategoryId="strmassistant.rebuild",CategoryName="Strm Assistant Rebuild"}
    };
}
