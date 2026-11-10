using System.Net;
using Emby.Web.GenericUI.Common;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using StrmAssistantRebuild.Web.Api;

namespace StrmAssistantRebuild.Web.Service;

public sealed class MediaInfoService : BaseApiService
{
    private readonly IItemRepository repository;
    private readonly IFileSystem files;
    public MediaInfoService(ILibraryManager libraryManager, IItemRepository itemRepository, IFileSystem fileSystem)
    {
        LibraryManager = libraryManager;
        repository = itemRepository;
        files = fileSystem;
    }

    public async Task<object> Post(SyncMediaInfo request)
    {
        var api = Plugin.Instance?.MediaInfoApi;
        if (api == null) return new HttpErrorResult("Media subsystem has not been initialized.", HttpStatusCode.ServiceUnavailable);
        try
        {
            return await api.SyncAsync(request.Id, request.Path, request,
                Request?.CancellationToken ?? default).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new HttpErrorResult("Operation cancelled.", HttpStatusCode.RequestTimeout); }
        catch (MediaBrowser.Model.Net.HttpException ex) { return new HttpErrorResult(ex.Message, ex.StatusCode ?? HttpStatusCode.InternalServerError); }
        catch (ArgumentException ex) { return new HttpErrorResult(ex.Message, HttpStatusCode.BadRequest); }
        catch (System.Text.Json.JsonException ex) { return new HttpErrorResult(ex.Message, HttpStatusCode.BadRequest); }
        catch (InvalidDataException ex) { return new HttpErrorResult(ex.Message, HttpStatusCode.BadRequest); }
        catch (NotSupportedException ex) { return new HttpErrorResult(ex.Message, HttpStatusCode.NotImplemented); }
        catch (FileNotFoundException ex) { return new HttpErrorResult(ex.Message, HttpStatusCode.NotFound); }
        catch (IOException ex) { return new HttpErrorResult(ex.Message, HttpStatusCode.Conflict); }
        catch (Exception ex)
        {
            Plugin.Instance?.Logger.Error("[MediaInfo] Sync failed: {0}", ex.Message);
            return new HttpErrorResult("Media synchronization failed: " + ex.Message, HttpStatusCode.InternalServerError);
        }
    }

    public Task<object> Post(ClearMediaInfo request)
    {
        var plugin = Plugin.Instance;
        var api = plugin?.MediaInfoApi;
        if (api == null) return Task.FromResult<object>(new HttpErrorResult("Media subsystem has not been initialized.", HttpStatusCode.ServiceUnavailable));
        return WebServiceSupport.Batch(nameof(ClearMediaInfo), () =>
        WebServiceSupport.MediaItems(LibraryManager, WebServiceSupport.Item(LibraryManager, request.Id), false,
            Request?.CancellationToken ?? default), async (item, token) =>
        {
            await plugin!.WorkQueue.RunSingle(item.InternalId, ct =>
            {
                var images = item is Video video ? WebServiceSupport.ChapterImageFiles(video, repository.GetChapters(item, ct), files) : Array.Empty<string>();
                api.InvalidateJsonCache(item, ct); // Before any destructive change; Default must not revive the cleared snapshot.
                var retired = WebServiceSupport.RetireFiles(images, files, ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    ResetMediaInfo(item);
                    repository.SaveMediaStreams(item.InternalId, new List<MediaStream>(), ct);
                    if (item is Video) repository.SaveChapters(item.InternalId, true, new List<ChapterInfo>());
                    LibraryManager.UpdateItems(new List<BaseItem> { item }, null, ItemUpdateType.None, true, false, null, ct);
                }
                catch { WebServiceSupport.RestoreRetiredFiles(retired, files); throw; }
                return Task.FromResult(true);
            }, token).ConfigureAwait(false);
        }, Request);
    }

    internal static void ResetMediaInfo(BaseItem item)
    {
        item.Size = 0;
        item.RunTimeTicks = null;
        item.TotalBitrate = 0;
        if (item.IsShortcut) item.Container = null;
        item.MediaStreams = new List<MediaStream>();
        if (item is Video) { item.Width = 0; item.Height = 0; }
    }
}
