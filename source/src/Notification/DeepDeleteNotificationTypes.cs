using Emby.Notifications;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Notification;

public sealed class DeepDeleteNotificationTypes : INotificationTypeFactory
{
    public List<NotificationTypeInfo> GetNotificationTypes(string language)=>Plugin.Instance?.DeepDeleteNotifications?.IsEnabled==true
        ? new(){new NotificationTypeInfo{Id=DeepDeleteNotification.EventId,Name=OriginalNotifications.EventName(DeepDeleteNotification.EventId,language),CategoryId=OriginalNotifications.CategoryId,CategoryName=OriginalNotifications.PluginName(language)}}
        : new();
}
