using Emby.Notifications;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Notification;

/// <summary>Independent rebuild event; users opt in through Emby's existing notification settings.</summary>
public sealed class MetadataNotificationTypes : INotificationTypeFactory
{
    public List<NotificationTypeInfo> GetNotificationTypes(string language)
    {
        var types=new List<NotificationTypeInfo>();
        if(MetadataEditNotification.IsEnabled) types.Add(new NotificationTypeInfo {Id=MetadataEditNotification.EventId,Name=OriginalNotifications.EventName(MetadataEditNotification.EventId,language),CategoryId=OriginalNotifications.CategoryId,CategoryName=OriginalNotifications.PluginName(language)});
        if(ImageEditNotification.IsEnabled) types.Add(new NotificationTypeInfo {Id=ImageEditNotification.EventId,Name=OriginalNotifications.EventName(ImageEditNotification.EventId,language),CategoryId=OriginalNotifications.CategoryId,CategoryName=OriginalNotifications.PluginName(language)});
        return types;
    }
}
