using Emby.Notifications;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Notification;

/// <summary>M166: the original registers both collection events unconditionally, next to favorites and intro skip.</summary>
public sealed class CollectionNotificationTypes : INotificationTypeFactory
{
    public List<NotificationTypeInfo> GetNotificationTypes(string language) => new[] { OriginalNotifications.CollectionAdded, OriginalNotifications.CollectionRemoved }
        .Select(id => new NotificationTypeInfo { Id = id, Name = OriginalNotifications.EventName(id, language), CategoryId = OriginalNotifications.CategoryId, CategoryName = OriginalNotifications.PluginName(language) })
        .ToList();
}
