using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace StrmAssistantRebuild.Common;

/// <summary>Opt-in DeleteVersion subset: an unshared local STRM target, retired with its pointer by the caller.</summary>
public sealed class LocalDeepDelete
{
    private const int ReferenceBudget = 2000;
    private readonly ILibraryManager library;
    private readonly IMediaSourceManager sources;
    private readonly EmbyPathPolicy paths;
    private readonly Func<bool> enabled;
    public LocalDeepDelete(ILibraryManager library, IMediaSourceManager sources, EmbyPathPolicy paths, Func<bool> enabled)
    { this.library = library; this.sources = sources; this.paths = paths; this.enabled = enabled; }

    // Caller owns VersionMutationGate and the selected item lease. Never remove directories or guess sidecars.
    public string[] AdditionalPaths(BaseItem selected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!enabled() || selected is not Video || !selected.IsShortcut) return Array.Empty<string>();
        var pointer = paths.RequireWritablePath(selected.Path);
        var target = Resolve(selected, token);
        if (target == null || Same(target, pointer)) return Array.Empty<string>();
        target = paths.RequireWritablePath(target); // Explicitly retain the existing no-symlink/root policy.
        if (!new[] { ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".ts", ".m2ts", ".mts", ".webm", ".wmv", ".mpg", ".mpeg", ".flv", ".ogv", ".3gp", ".iso" }.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase))
            throw new IOException("Deep-delete target has an unrecognized video extension; files retained.");
        if (Directory.Exists(target)) throw new IOException("Deep-delete target is a directory, not a media file.");
        if (!File.Exists(target)) return Array.Empty<string>();
        var items = library.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { "Video" }, Recursive = true, GroupByPresentationUniqueKey = false,
            EnableTotalRecordCount = false, Limit = ReferenceBudget + 1
        }, token);
        if (items.Length > ReferenceBudget) throw new IOException("Deep-delete reference inspection budget exceeded; files retained.");
        // A shared physical file or a second STRM anywhere in the inspected library keeps the target alive.
        // This is a bounded live observation, not an OS-wide or cross-process reference transaction.
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (item.InternalId == selected.InternalId || item is not Video) continue;
            if (Same(item.Path, target)) return Array.Empty<string>();
            if (item.IsShortcut && Same(Resolve(item, token), target)) return Array.Empty<string>();
        }
        token.ThrowIfCancellationRequested();
        return new[] { paths.RequireWritablePath(target) };
    }
    private string? Resolve(BaseItem item, CancellationToken token)
    {
        var folders = library.GetCollectionFolders(item, token);
        var options = library.GetLibraryOptions(item, folders, token);
        // Real static resolver expands STRM locally; no probe, alternate versions, substitution, streams,
        // chapters, live opening or remote-source providers. Query contains no requesting user's track state.
        var resolved = sources.GetStaticMediaSources(item, false, false, false, false, folders, options, null!, null!, token);
        token.ThrowIfCancellationRequested();
        if (resolved == null || resolved.Count != 1) throw new IOException("Deep-delete target resolution is ambiguous; files retained.");
        var source = resolved[0];
        if (source.Protocol != MediaProtocol.File || source.RequiresOpening || string.IsNullOrWhiteSpace(source.Path)) return null;
        var target = source.Path;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile && uri.IsLoopback) target = uri.LocalPath;
        if (!Path.IsPathRooted(target) || target.Contains("://", StringComparison.Ordinal)) throw new IOException("Deep-delete target is not an absolute local path.");
        if (Path.GetExtension(target).Equals(".strm", StringComparison.OrdinalIgnoreCase)) throw new IOException("Deep-delete shortcut remains unresolved; files retained.");
        return Path.GetFullPath(target);
    }
    private static bool Same(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
        if (Path.IsPathRooted(left)) left = Path.GetFullPath(left);
        if (Path.IsPathRooted(right)) right = Path.GetFullPath(right);
        return string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
