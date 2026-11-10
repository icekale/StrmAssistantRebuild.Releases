using System.Net;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using StrmAssistantRebuild.Web.Api;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Web.Service;

public sealed class LibraryService : BaseApiService
{
    private readonly IItemRepository repository;
    private readonly IFileSystem files;
    public LibraryService(ILibraryManager libraryManager, IItemRepository itemRepository, IFileSystem fileSystem)
    {
        LibraryManager = libraryManager;
        repository = itemRepository;
        files = fileSystem;
    }

    public async Task<object> Post(DeleteVersion request)
    {
        var plugin = Plugin.Instance;
        if (plugin == null) return new Emby.Web.GenericUI.Common.HttpErrorResult("Plugin has not been initialized.", HttpStatusCode.ServiceUnavailable);
        var requestToken = Request?.CancellationToken ?? default;
        try { await VersionMutationGate.Shared.WaitAsync(requestToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new Emby.Web.GenericUI.Common.HttpErrorResult("Operation cancelled.", HttpStatusCode.RequestTimeout); }
        try
        {
            return await WebServiceSupport.Batch(nameof(DeleteVersion), () =>
            {
                var item = WebServiceSupport.Item(LibraryManager, request.Id);
                ValidateVersion(item);
                return request.DeleteParent && item is Episode episode ? SeasonVersion(episode) : new[] { item };
            }, async (selected, token) =>
            {
                await plugin.WorkQueue.RunSingle(selected.InternalId, ct =>
                {
                    var item = LibraryManager.GetItemById(selected.InternalId) ?? throw new FileNotFoundException("Selected version no longer exists.");
                    ValidateVersion(item); // Never remove the last remaining version after waiting for another mutation.
                    var user = GetUserForRequest(null);
                    var collections = LibraryManager.GetCollectionFolders(item, ct);
                    if (!(user == null ? item.CanDelete() : item.CanDelete(user, collections)))
                        throw WebServiceSupport.Error(HttpStatusCode.Forbidden, "Current user may not delete this item.");
                    // Public host enumerator: don't follow STRM targets or delete directories.
                    var paths = item.GetDeletePaths(false, collections).Where(p => !p.IsDirectory)
                        .Select(p => p.FullName).Prepend(item.Path).Distinct(StringComparer.Ordinal)
                        .Select(WebServiceSupport.Writable).ToArray();
                    var deepPaths = plugin.LocalDeepDelete?.AdditionalPaths(item, ct) ?? Array.Empty<string>();
                    var notification = plugin.DeepDeleteNotifications?.Prepare(item, user, deepPaths, ct);
                    var retired = WebServiceSupport.RetireFiles(paths.Concat(deepPaths), files, ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        repository.DeleteItems(new[] { item });
                    }
                    catch { WebServiceSupport.RestoreRetiredFiles(retired, files); throw; }
                    // Notification failures are separate from the completed mutation and never trigger rollback.
                    plugin.DeepDeleteNotifications?.Complete(notification, retired, ct);
                    // Keep exact byte backups for explicit recovery; no guessed ownership or irreversible unlink.
                    return Task.FromResult(true);
                }, token).ConfigureAwait(false);
            }, Request).ConfigureAwait(false);
        }
        finally { VersionMutationGate.Shared.Release(); }
    }
    private static void ValidateVersion(BaseItem item)
    {
        if (item is not Video video || item is not (Movie or Episode) || !item.IsFileProtocol || video.GetAlternateVersionIds().Count == 0)
            throw WebServiceSupport.Error(HttpStatusCode.BadRequest, "DeleteVersion requires a file-backed movie or episode with alternate versions.");
    }

    private BaseItem[] SeasonVersion(Episode episode)
    {
        var key = VersionKey(episode.Path);
        if (episode.ParentId <= 0 || string.IsNullOrWhiteSpace(key))
            throw WebServiceSupport.Error(HttpStatusCode.Conflict, "A season version could not be identified exactly.");
        var directory = Path.GetDirectoryName(episode.Path);
        // ponytail: exact same-directory/name key instead of the source's destructive >0.92 fuzzy match.
        var targets = LibraryManager.GetItemList(new InternalItemsQuery
        {
            ParentIds = new[] { episode.ParentId }, Recursive = false,
            IncludeItemTypes = new[] { nameof(Episode) }, GroupByPresentationUniqueKey = false
        }, Request?.CancellationToken ?? default).OfType<Episode>().Where(i => i.IsFileProtocol &&
            string.Equals(Path.GetDirectoryName(i.Path), directory, StringComparison.Ordinal) &&
            string.Equals(VersionKey(i.Path), key, StringComparison.OrdinalIgnoreCase))
            .Append(episode).DistinctBy(i => i.InternalId).Cast<BaseItem>().ToArray();
        if (targets.Any(i => ((Video)i).GetAlternateVersionIds().Count == 0))
            throw WebServiceSupport.Error(HttpStatusCode.Conflict, "Every season item must retain another version.");
        return targets;
    }

    internal static string VersionKey(string path) => Regex.Replace(Path.GetFileNameWithoutExtension(path),
        @"(?i)\bS\d{1,3}[ ._-]*E\d{1,4}(?:[ ._-]*E\d{1,4})?\b", "{episode}", RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
}
