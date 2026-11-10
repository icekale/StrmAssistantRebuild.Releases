using Emby.Notifications;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Notification;

/// <summary>Independent rebuild event; users opt in through Emby's existing notification settings.</summary>
public sealed class MetadataNotificationTypes : INotificationTypeFactory
{
    public List<NotificationTypeInfo> GetNotificationTypes(string language)
    {
        var types=new List<NotificationTypeInfo>();
        if(MetadataEditNotification.IsEnabled) types.Add(new NotificationTypeInfo {Id=MetadataEditNotification.EventId,Name=MetadataEditNotification.Title(language),CategoryId="strmassistant.rebuild",CategoryName="Strm Assistant Rebuild"});
        if(ImageEditNotification.IsEnabled) types.Add(new NotificationTypeInfo {Id=ImageEditNotification.EventId,Name=ImageEditNotification.Title(language),CategoryId="strmassistant.rebuild",CategoryName="Strm Assistant Rebuild"});
        return types;
    }
}
