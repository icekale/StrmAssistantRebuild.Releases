using Emby.Notifications;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Notification;

/// <summary>Playback marker events are independently discoverable; no transport is enabled automatically.</summary>
public sealed class MarkerNotificationTypes : INotificationTypeFactory
{
    public List<NotificationTypeInfo> GetNotificationTypes(string language)=>new()
    {
        new NotificationTypeInfo {Id=MarkerUpdateNotification.EventId,Name=OriginalNotifications.EventName(MarkerUpdateNotification.EventId,language),CategoryId=OriginalNotifications.CategoryId,CategoryName=OriginalNotifications.PluginName(language)}
    };
}
