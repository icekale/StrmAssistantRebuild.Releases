using Emby.Notifications;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Notification;

/// <summary>Always discoverable, independent of media catch-up and optional UI enhancement switches.</summary>
public sealed class FavoriteNotificationTypes : INotificationTypeFactory
{
    public List<NotificationTypeInfo> GetNotificationTypes(string language) => new()
    {
        new NotificationTypeInfo { Id=FavoriteUpdateNotification.EventId,Name=OriginalNotifications.EventName(FavoriteUpdateNotification.EventId,language),CategoryId=OriginalNotifications.CategoryId,CategoryName=OriginalNotifications.PluginName(language) }
    };
}
