using System.Net;
using System.Text.Json;
using Emby.Web.GenericUI.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistantRebuild.Web.Service;

internal static class WebServiceSupport
{
    internal static readonly ExtraType[] Extras = Enum.GetValues<ExtraType>().Except(new[]
        { ExtraType.Sample, ExtraType.ThemeSong, ExtraType.ThemeVideo, ExtraType.Trailer }).ToArray();

    internal static HttpException Error(HttpStatusCode status, string message) => new(message) { StatusCode = status };
    internal static object Run(Action action) => RunAsync(() => { action(); return Task.CompletedTask; }).GetAwaiter().GetResult();
    internal static async Task<object> RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return new HttpErrorResult(string.Empty, HttpStatusCode.NoContent);
        }
        catch (HttpException ex) { return new HttpErrorResult(ex.Message, ex.StatusCode ?? HttpStatusCode.InternalServerError); }
        catch (ArgumentException ex) { return new HttpErrorResult(ex.Message, HttpStatusCode.BadRequest); }
        catch (IOException ex) { return new HttpErrorResult(ex.Message, HttpStatusCode.Conflict); }
    }

    internal static async Task<object> Batch(string operation, Func<BaseItem[]> select,
        Func<BaseItem, CancellationToken, Task> action, IRequest? request)
    {
        var token = request?.CancellationToken ?? default;
        BaseItem[] items;
        try { token.ThrowIfCancellationRequested(); items = select(); }
        catch (OperationCanceledException) { return new HttpErrorResult("Operation cancelled.", HttpStatusCode.RequestTimeout); }
        catch (HttpException ex) { return new HttpErrorResult(ex.Message, ex.StatusCode ?? HttpStatusCode.InternalServerError); }
        catch (ArgumentException ex) { return new HttpErrorResult(ex.Message, HttpStatusCode.BadRequest); }
        catch (IOException ex) { return new HttpErrorResult(ex.Message, HttpStatusCode.Conflict); }
        catch (Exception ex)
        {
            Plugin.Instance?.Logger?.Error("[StrmAssistantRebuild] {0} selection failed: {1}", operation, ex.Message);
            return new HttpErrorResult("Item selection failed: " + ex.Message, HttpStatusCode.InternalServerError);
        }
        var progress = new WebBatchResult { Total = items.Length };
        foreach (var item in items)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                await action(item, token).ConfigureAwait(false);
                progress.Done++;
            }
            catch (OperationCanceledException) { progress.Cancelled = true; break; }
            catch (Exception ex)
            {
                progress.Failed++;
                progress.Errors.Add(new WebItemError { ItemId = item.InternalId.ToString(), Message = ex.Message });
                Plugin.Instance?.Logger?.Warn("[StrmAssistantRebuild] {0} item {1}: {2}", operation, item.InternalId, ex.Message);
            }
        }
        Log(operation, progress.Done);
        if (progress.Total == 1 && progress.Failed == 0 && !progress.Cancelled)
            return new HttpErrorResult(string.Empty, HttpStatusCode.NoContent);
        return progress;
    }

    internal static BaseItem Item(ILibraryManager library, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !(Guid.TryParse(id, out _) || (long.TryParse(id, out var n) && n > 0)))
            throw Error(HttpStatusCode.BadRequest, "A valid item Id is required.");
        return library.GetItemById(id) ?? throw Error(HttpStatusCode.NotFound, "Item was not found.");
    }

    internal static BaseItem[] PresentationItems(ILibraryManager library, BaseItem item, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // An empty presentation key must never turn a single-item operation into an all-library query.
        if (string.IsNullOrWhiteSpace(item.PresentationUniqueKey)) return new[] { item };
        return library.GetItemList(new InternalItemsQuery
        {
            PresentationUniqueKey = item.PresentationUniqueKey,
            HasExtraType = item.ExtraType.HasValue,
            GroupByPresentationUniqueKey = false
        }, token).Append(item).DistinctBy(i => i.InternalId).ToArray();
    }

    internal static BaseItem[] Expand(ILibraryManager library, BaseItem item, bool includeFolders, CancellationToken token = default)
    {
        return PresentationItems(library, item, token).SelectMany(i =>
        {
            token.ThrowIfCancellationRequested();
            if (i is Folder)
            {
                var children = library.GetItemList(new InternalItemsQuery
                {
                    ParentIds = new[] { i.InternalId }, Recursive = true, GroupByPresentationUniqueKey = false
                }, token);
                return includeFolders ? children.Append(i) : children;
            }
            return i is Video && !item.ExtraType.HasValue ? i.GetExtras(Extras, new MediaBrowser.Controller.Dto.DtoOptions(), token).Prepend(i) : new[] { i };
        }).DistinctBy(i => i.InternalId).ToArray();
    }

    internal static BaseItem[] MediaItems(ILibraryManager library, BaseItem item, bool videoOnly, CancellationToken token = default)
    {
        var result = Expand(library, item, false, token).Where(i => i.IsFileProtocol &&
            (i is Video || (!videoOnly && i is MediaBrowser.Controller.Entities.Audio.Audio))).ToArray();
        if (result.Length == 0) throw Error(HttpStatusCode.BadRequest, "No file-backed media items match this operation.");
        return result;
    }

    internal static LibraryOptions CopyOptions(LibraryOptions source) =>
        JsonSerializer.Deserialize<LibraryOptions>(JsonSerializer.Serialize(source))!;

    internal static string Writable(string path) => (Plugin.Instance ??
        throw Error(HttpStatusCode.ServiceUnavailable, "Plugin has not been initialized.")).Paths.RequireWritablePath(path);

    internal static string[] ChapterImageFiles(Video video, List<ChapterInfo> chapters, IFileSystem files)
    {
        var directory = new DirectoryService(Plugin.Instance!.Logger, files);
        // Only host-reported files: no globbing beside media, directory deletion, or following STRM targets.
        return chapters.Select(c => c.ImagePath).Where(p => !string.IsNullOrWhiteSpace(p))
            .Concat(Video.GetMetadataFolderThumbnailSetInfos(video.Path, video.Id, directory).Select(t => t.Path))
            .Concat(Video.GetLocalThumbnailSetInfos(video.Path, video.Id, false, directory).Select(t => t.Path))
            .Distinct(StringComparer.Ordinal).Select(Writable).ToArray();
    }

    // Explicit clear retires only the exact host-reported paths. Keep original bytes for recovery;
    // no filename-based ownership adoption, recursive deletion, or overwrite of a concurrent writer.
    internal static List<(string Source, string Archive)> RetireFiles(IEnumerable<string> paths, IFileSystem files, CancellationToken token)
    {
        var selected = paths.Distinct(StringComparer.Ordinal).Select(Writable).ToArray();
        foreach (var path in selected)
        {
            token.ThrowIfCancellationRequested();
            if (files.DirectoryExists(path)) throw new IOException("Expected a chapter image file, not a directory.");
        }
        var moved = new List<(string Source, string Archive)>();
        try
        {
            foreach (var path in selected)
            {
                token.ThrowIfCancellationRequested();
                if (!files.FileExists(Writable(path))) continue;
                var archive = Writable(path + ".strmassistant-cleared-" + Guid.NewGuid().ToString("N") + ".bak");
                files.MoveFile(path, archive); moved.Add((path, archive));
            }
            token.ThrowIfCancellationRequested();
            return moved;
        }
        catch { RestoreRetiredFiles(moved, files); throw; }
    }
    internal static void RestoreRetiredFiles(IReadOnlyList<(string Source, string Archive)> moved, IFileSystem files)
    {
        // Cleanup must run even when the caller token is cancelled. Never clobber a newly created source.
        foreach (var pair in moved.Reverse())
        {
            try
            {
                if (!files.FileExists(Writable(pair.Archive))) continue;
                if (files.FileExists(Writable(pair.Source))) throw new IOException("Original path was recreated; retained clear backup.");
                files.MoveFile(pair.Archive, pair.Source);
            }
            catch (Exception error) { Plugin.Instance?.Logger?.Warn("[StrmAssistantRebuild] Clear rollback retained {0}: {1}", pair.Archive, error.Message); }
        }
    }

    internal static void DeleteFiles(IEnumerable<string> paths, IFileSystem files, CancellationToken token = default)
    {
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            Writable(path); // Recheck immediately before each host file operation.
            if (files.DirectoryExists(path)) throw new IOException("Expected a file, not a directory.");
            if (files.FileExists(path)) files.DeleteFile(path, true);
        }
    }

    internal static void Log(string operation, int count) => Plugin.Instance?.Logger?.Info(
        "[StrmAssistantRebuild] {0}: {1} item(s) processed", operation, count);
}

public sealed class WebBatchResult
{
    public int Total { get; set; }
    public int Done { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }
    public List<WebItemError> Errors { get; set; } = new();
}

public sealed class WebItemError
{
    public string ItemId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
