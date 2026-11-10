using Emby.Notifications;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Notification;

/// <summary>Always discoverable, independent of media catch-up and optional UI enhancement switches.</summary>
public sealed class FavoriteNotificationTypes : INotificationTypeFactory
{
    public List<NotificationTypeInfo> GetNotificationTypes(string language) => new()
    {
        new NotificationTypeInfo { Id=FavoriteUpdateNotification.EventId,Name=FavoriteUpdateNotification.Title(language),CategoryId="strmassistant.rebuild",CategoryName="Strm Assistant Rebuild" }
    };
}
