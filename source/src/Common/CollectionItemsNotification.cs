using System.Globalization;
using Emby.Notifications;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;

/// <summary>
/// M166: the original plugin's collection membership notifications (notes/decrypted/A----------/AA---------.cs:49070-49400,
/// handlers StrmAssistant/Plugin.cs:3162-3175). A created collection reports its initial items as "added"; items added to or
/// removed from a collection report "added"/"removed". The request carries the collection as Item, no user, the original title
/// "{plugin} - {event}" and description. The original's subscription site is not visible in the decompiled code (only the
/// unsubscription in Dispose); its notification types are registered unconditionally, so the rebuild subscribes at startup.
/// Unlike the original, a failure is logged instead of propagating into the host's collection operation.
/// </summary>
public sealed class CollectionItemsNotification : IDisposable
{
    private readonly ILibraryManager library;
    private readonly INotificationManager notifications;
    private readonly ILogger logger;
    private ICollectionManager? collections;
    private long emitted;
    public long EmittedRequests => Interlocked.Read(ref emitted);
    public bool IsAttached => Volatile.Read(ref collections) != null;

    public CollectionItemsNotification(ILibraryManager library, INotificationManager notifications, ILogger logger)
    { this.library = library; this.notifications = notifications; this.logger = logger; }

    public void Attach(ICollectionManager? manager)
    {
        if (manager == null || Interlocked.CompareExchange(ref collections, manager, null) != null) return;
        manager.CollectionCreated += OnCreated;
        manager.ItemsAddedToCollection += OnAdded;
        manager.ItemsRemovedFromCollection += OnRemoved;
    }

    public void Dispose()
    {
        var manager = Interlocked.Exchange(ref collections, null);
        if (manager == null) return;
        manager.CollectionCreated -= OnCreated;
        manager.ItemsAddedToCollection -= OnAdded;
        manager.ItemsRemovedFromCollection -= OnRemoved;
    }

    private void OnCreated(object? sender, CollectionCreatedEventArgs e) => Send(e.Collection, e.Options?.ItemIdList, true);
    private void OnAdded(object? sender, CollectionModifiedEventArgs e) => Send(e.Collection, e.ItemsChanged?.ToArray(), true);
    private void OnRemoved(object? sender, CollectionModifiedEventArgs e) => Send(e.Collection, e.ItemsChanged?.ToArray(), false);

    internal void Send(BoxSet? collection, long[]? ids, bool added)
    {
        if (collection == null || ids == null || ids.Length == 0 || Volatile.Read(ref collections) == null) return;
        try
        {
            var language = CultureInfo.CurrentUICulture.Name;
            var eventId = added ? OriginalNotifications.CollectionAdded : OriginalNotifications.CollectionRemoved;
            var items = library.GetItemList(new InternalItemsQuery { ItemIds = ids });
            notifications.SendNotification(new NotificationRequest
            {
                Title = OriginalNotifications.Title(eventId, language),
                EventId = eventId,
                Item = collection,
                Description = OriginalNotifications.CollectionDescription(collection.Name, collection.InternalId, items.Select(Line))
            });
            Interlocked.Increment(ref emitted);
        }
        catch (Exception error) { try { logger.Warn("[CollectionItemsNotification] Notification unavailable: {0}", error.GetType().Name); } catch { } }
    }

    internal static OriginalNotifications.CollectionLine Line(BaseItem item) => item switch
    {
        Season season => new(OriginalNotifications.CollectionLineKind.Season, nameof(Season), season.Name, season.Series?.Name, season.Name),
        Episode episode => new(OriginalNotifications.CollectionLineKind.Episode, nameof(Episode), episode.Name, episode.Series?.Name, episode.Season?.Name),
        _ => new(OriginalNotifications.CollectionLineKind.Other, item.GetType().Name, item.Name)
    };
}
