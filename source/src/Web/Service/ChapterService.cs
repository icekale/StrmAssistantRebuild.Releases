using System.Net;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using StrmAssistantRebuild.Web.Api;

namespace StrmAssistantRebuild.Web.Service;

public sealed class ChapterService : BaseApiService
{
    private readonly IItemRepository repository;
    private readonly IFileSystem files;
    public ChapterService(ILibraryManager libraryManager, IItemRepository itemRepository, IFileSystem fileSystem)
    {
        LibraryManager = libraryManager;
        repository = itemRepository;
        files = fileSystem;
    }

    public Task<object> Post(ClearIntro request) => WebServiceSupport.Batch(nameof(ClearIntro), () =>
    {
        var token = Request?.CancellationToken ?? default;
        var item = WebServiceSupport.Item(LibraryManager, request.Id);
        if (item is not (Movie or Episode or Season or Series))
            throw WebServiceSupport.Error(HttpStatusCode.BadRequest, "ClearIntro requires a movie, episode, season, or series.");
        if (item is Movie) return new[] { item };
        if (item is Episode) return WebServiceSupport.PresentationItems(LibraryManager, item, token);
        if (string.IsNullOrWhiteSpace(item.PresentationUniqueKey))
            return WebServiceSupport.Expand(LibraryManager, item, false, token).OfType<Episode>().ToArray();
        return LibraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { nameof(Episode) }, HasPath = true,
            SeriesPresentationUniqueKey = item is Series ? item.PresentationUniqueKey : null,
            ParentWithPresentationUniqueKey = item is Season ? item.PresentationUniqueKey : null,
            GroupByPresentationUniqueKey = false
        }, token).DistinctBy(i => i.InternalId).ToArray();
    }, (item, token) =>
    {
        if (Plugin.Instance?.ChapterApi is { } markerApi) return markerApi.ClearAsync(item, token);
        var chapters = repository.GetChapters(item, token);
        chapters.RemoveAll(IsIntroOrCredits);
        token.ThrowIfCancellationRequested();
        repository.SaveChapters(item.InternalId, chapters);
        return Task.CompletedTask;
    }, Request);

    public Task<object> Post(ClearChapterImage request)
    {
        var plugin = Plugin.Instance;
        var api = plugin?.MediaInfoApi;
        if (api == null) return Task.FromResult<object>(new Emby.Web.GenericUI.Common.HttpErrorResult("Media subsystem has not been initialized.", HttpStatusCode.ServiceUnavailable));
        return WebServiceSupport.Batch(nameof(ClearChapterImage), () =>
            WebServiceSupport.MediaItems(LibraryManager, WebServiceSupport.Item(LibraryManager, request.Id), true,
                Request?.CancellationToken ?? default), async (item, token) =>
        {
            await plugin!.WorkQueue.RunSingle(item.InternalId, async ct =>
            {
                var chapters = repository.GetChapters(item, ct);
                var paths = WebServiceSupport.ChapterImageFiles((Video)item, chapters, files);
                // Copy before edits: a failed/cancelled save must not mutate repository-owned cached chapters.
                var cleared = chapters.Select(c => new ChapterInfo { Name = c.Name, StartPositionTicks = c.StartPositionTicks,
                    MarkerType = c.MarkerType, ChapterIndex = c.ChapterIndex }).ToList();
                await api.ApplyAndPersistAsync(item, inner =>
                {
                    var retired = WebServiceSupport.RetireFiles(paths, files, inner);
                    try { inner.ThrowIfCancellationRequested(); repository.SaveChapters(item.InternalId, true, cleared); }
                    catch { WebServiceSupport.RestoreRetiredFiles(retired, files); throw; }
                    return Task.CompletedTask;
                }, ct, invalidateExistingCache: true).ConfigureAwait(false);
                return true;
            }, token).ConfigureAwait(false);
        }, Request);
    }

    internal static bool IsIntroOrCredits(ChapterInfo chapter) =>
        chapter.MarkerType is MarkerType.IntroStart or MarkerType.IntroEnd or MarkerType.CreditsStart;
}
