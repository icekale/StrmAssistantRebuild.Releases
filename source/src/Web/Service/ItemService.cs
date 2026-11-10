using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using StrmAssistantRebuild.Web.Api;

namespace StrmAssistantRebuild.Web.Service;

public sealed class ItemService : BaseApiService
{
    private readonly IProviderManager providers;
    public ItemService(ILibraryManager libraryManager, IProviderManager providerManager)
    {
        LibraryManager = libraryManager;
        providers = providerManager;
    }

    public Task<object> Post(LockItem request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null) return Task.FromResult<object>(new Emby.Web.GenericUI.Common.HttpErrorResult("Plugin has not been initialized.", System.Net.HttpStatusCode.ServiceUnavailable));
        return WebServiceSupport.Batch(nameof(LockItem), () =>
            WebServiceSupport.Expand(LibraryManager, WebServiceSupport.Item(LibraryManager, request.ItemId), true,
                Request?.CancellationToken ?? default), async (selected, token) =>
        {
            await plugin.WorkQueue.RunSingle(selected.InternalId, async ct =>
            {
                // A previous queued operation may have replaced/deleted the selected instance while we waited.
                var item = LibraryManager.GetItemById(selected.InternalId) ?? throw new System.IO.FileNotFoundException("Selected item no longer exists.");
                if (item.IsLocked == request.LockData && (request.LockData || item.LockedFields.Length == 0)) return true;
                WebServiceSupport.Writable(item.GetInternalMetadataPath());
                var options = WebServiceSupport.CopyOptions(LibraryManager.GetLibraryOptions(item));
                options.SaveLocalMetadata = false;
                var originalLock = item.IsLocked;
                var originalFields = item.LockedFields;
                ct.ThrowIfCancellationRequested();
                SetLock(item, request.LockData);
                try { LibraryManager.UpdateItems(new List<BaseItem> { item }, null, ItemUpdateType.MetadataEdit, true, false, null, ct); }
                catch { item.IsLocked = originalLock; item.LockedFields = originalFields; throw; }
                ct.ThrowIfCancellationRequested();
                // This public method has no CancellationToken overload. Keep the item lease until it settles.
                // A saver error follows the committed lock update; don't pretend the database rolled back.
                await providers.SaveMetadata(item, options, ItemUpdateType.MetadataEdit).ConfigureAwait(false);
                return true;
            }, token).ConfigureAwait(false);
        }, Request);
    }

    internal static void SetLock(BaseItem item, bool locked)
    {
        item.IsLocked = locked;
        if (!locked) item.LockedFields = Array.Empty<MetadataFields>();
    }
}
