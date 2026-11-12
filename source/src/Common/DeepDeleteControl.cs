using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace StrmAssistantRebuild.Common;

/// <summary>
/// M154: original EnableDeepDelete native path (decompiled AA---------/A----------.cs:5270-5470, executor
/// A----------/AA---------.cs:38181-38734, classifier :45047-45412). When Emby deletes an item with "delete files",
/// the files that its .strm files (or file symlinks) point to are deleted as well, with their same-name sidecars, and
/// folders left empty are removed.
/// Deliberate safety differences from the original, all documented in M154:
/// - acts only when the native delete succeeded (the original also ran after a failed delete);
/// - a target still referenced by another library item is kept;
/// - targets must be absolute local files outside Emby's own program/data folders and outside library locations;
/// - relative symlink targets resolve against the link's folder (the original used the process working directory);
/// - sidecars must be the target name followed by a separator (the original used a bare prefix: "Foo" matched "Foo 2.nfo");
/// - the empty-folder walk never removes mount points, library locations or their ancestors, Emby folders, or folders
///   at depth 2 or less (the original walked up to the filesystem root).
/// </summary>
public static class DeepDeleteControl
{
    private const string Owner = "StrmAssistantRebuild.DeepDelete.4.10.2";
    internal const string AssemblyHash = "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F";
    public const int ReferenceBudget = 20000;
    // Original sidecar list (A----------/A----------.cs:20423-20437 with .json) plus BaseItem.SupportedImageExtensions.
    private static readonly string[] SidecarExtensions = { ".nfo", ".xml", ".srt", ".vtt", ".sub", ".idx", ".txt", ".edl", ".bif", ".smi", ".ttml", ".ass", ".json" };
    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".ts", ".m2ts", ".mts", ".webm", ".wmv", ".mpg", ".mpeg", ".flv", ".ogv", ".3gp", ".iso", ".rmvb", ".rm", ".strm" };
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim Worker = new(1, 1);
    private static Harmony? patcher;
    private static MethodInfo? installed;
    private static Context? active;
    private static long targetsDeleted, sidecarsDeleted, foldersRemoved, kept, plans;
    public static bool IsEnabled => Volatile.Read(ref active) != null;
    public static long TargetsDeleted => Interlocked.Read(ref targetsDeleted);
    public static long SidecarsDeleted => Interlocked.Read(ref sidecarsDeleted);
    public static long FoldersRemoved => Interlocked.Read(ref foldersRemoved);
    public static long TargetsKept => Interlocked.Read(ref kept);
    public static long Plans => Interlocked.Read(ref plans);
    public static string Detail { get; private set; } = "Disabled; native delete unchanged.";
    /// <summary>Test hook: completes once queued deletions have finished.</summary>
    public static Task Idle => Worker.WaitAsync().ContinueWith(_ => Worker.Release());

    internal sealed record Context(ILibraryManager Library, IMediaSourceManager Sources, IFileSystem Files, ILogger Logger, string[] EmbyRoots, Func<IReadOnlyCollection<string>> Mounts);
    public sealed record Plan(long[] DeletedIds, string[] Targets);

    public static bool Configure(Version hostVersion, bool enable, ILibraryManager library, IMediaSourceManager sources, IFileSystem files, ILogger logger,
        IEnumerable<string> embyRoots, out string detail, Func<IReadOnlyCollection<string>>? mounts = null)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try
            {
                Remove();
                if (!enable) { detail = Detail = "Disabled; native delete unchanged."; return true; }
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.Server.Implementations");
                var target = assembly?.GetType("Emby.Server.Implementations.Library.LibraryManager")?.GetMethod("DeleteItem",
                    new[] { typeof(BaseItem), typeof(DeleteOptions), typeof(BaseItem), typeof(bool) });
                if (hostVersion != new Version(4, 10, 2, 0) || target == null || !Verify(target)) throw new NotSupportedException("host file, signature, MVID, IL or locals differ");
                if (Harmony.GetPatchInfo(target)?.Owners.Any(o => o != Owner) == true) throw new NotSupportedException("foreign DeleteItem patch present");
                installed = target;
                (patcher ??= new Harmony(Owner)).Patch(target,
                    prefix: new HarmonyMethod(typeof(DeepDeleteControl).GetMethod(nameof(Before), BindingFlags.Static | BindingFlags.NonPublic)!),
                    finalizer: new HarmonyMethod(typeof(DeepDeleteControl).GetMethod(nameof(After), BindingFlags.Static | BindingFlags.NonPublic)!));
                Volatile.Write(ref active, new Context(library, sources, files, logger,
                    embyRoots.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Full).Distinct().ToArray(), mounts ?? ReadMounts));
                detail = Detail = "Active; Emby deletes with \"delete files\" also delete the files their .strm/symlinks point to, same-name sidecars and emptied folders (safety limits apply).";
                return true;
            }
            catch (Exception error)
            {
                detail = Detail = "Inactive: " + error.Message + "; native delete unchanged.";
                try { Remove(); } catch { detail = Detail += " Cleanup pending."; }
                return false;
            }
        }
    }
    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try { Remove(); Detail = "Disabled; native delete unchanged."; } catch { Detail = "Disabled; cleanup pending."; }
        }
    }
    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.All, Owner);
        installed = null;
    }

    private static void Before(BaseItem item, DeleteOptions options, out Plan? __state)
    {
        __state = null;
        var context = Volatile.Read(ref active);
        if (context == null || item == null || options?.DeleteFileLocation != true || item is BoxSet or Playlist) return;
        try { __state = BuildPlan(context, item, options.CollectionFolders); if (__state != null) Interlocked.Increment(ref plans); }
        catch (Exception error) { Log(context, "DeepDelete - plan skipped for {0}: {1}", item.Name, error.Message); }
    }
    private static Exception? After(Exception? __exception, Plan? __state)
    {
        var context = Volatile.Read(ref active);
        if (__exception == null && __state != null && context != null)
            _ = Task.Run(() => ExecuteAsync(context, __state));
        return __exception;
    }

    internal static Plan? BuildPlan(Context context, BaseItem item, BaseItem[]? folders)
    {
        folders ??= context.Library.GetCollectionFolders(item);
        var deletePaths = item.GetDeletePaths(true, folders).Select(p => Full(p.FullName)).ToArray();
        var scope = item is Folder folder
            ? new[] { item }.Concat(folder.GetItemList(new InternalItemsQuery { Recursive = true, QueryName = "DeepDelete" })).ToArray()
            : new[] { item };
        var targets = new List<string>();
        foreach (var entry in scope.Where(i => i is Video && !string.IsNullOrEmpty(i.Path)))
        {
            var path = Full(entry.Path);
            if (deletePaths.Length > 0 && !deletePaths.Any(d => Same(d, path) || Under(path, d))) continue;
            var target = TargetOf(context, entry, path, folders);
            if (target != null) targets.Add(target);
        }
        var distinct = targets.Distinct(Comparer).ToArray();
        return distinct.Length == 0 ? null : new Plan(scope.Select(i => i.InternalId).Where(i => i > 0).ToArray(), distinct);
    }
    private static string? TargetOf(Context context, BaseItem item, string path, BaseItem[] folders)
    {
        if (path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            var options = context.Library.GetLibraryOptions(item, folders, CancellationToken.None);
            var source = context.Sources.GetStaticMediaSources(item, false, false, false, false, folders, options, null!, null!, CancellationToken.None)?.FirstOrDefault();
            if (source == null || source.Protocol != MediaProtocol.File || string.IsNullOrWhiteSpace(source.Path)) return null;
            var target = source.Path;
            if (target.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)) return null;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile) target = uri.LocalPath;
            if (!Path.IsPathRooted(target) || target.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)) return null;
            return Full(target);
        }
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget == null) return null;
        // Original: one level of FileInfo.LinkTarget; a relative target is resolved against the link's own folder here.
        var link = info.LinkTarget;
        var resolved = Path.IsPathRooted(link) ? link : Path.Combine(info.DirectoryName!, link);
        return Full(resolved);
    }

    internal static async Task ExecuteAsync(Context context, Plan plan)
    {
        await Worker.WaitAsync().ConfigureAwait(false);
        try
        {
            var dirty = new HashSet<string>(Comparer);
            foreach (var target in plan.Targets)
            {
                var reason = Refuse(context, target, plan);
                if (reason != null) { Interlocked.Increment(ref kept); Log(context, "DeepDelete - Kept {0}: {1}", target, reason); continue; }
                if (!File.Exists(target)) continue;
                var directory = Path.GetDirectoryName(target)!;
                foreach (var sidecar in Sidecars(target))
                    if (Delete(context, sidecar)) Interlocked.Increment(ref sidecarsDeleted);
                if (Delete(context, target)) Interlocked.Increment(ref targetsDeleted);
                dirty.Add(directory);
            }
            if (dirty.Count == 0) return;
            await Task.Delay(500).ConfigureAwait(false);
            var mounts = context.Mounts().Select(Full).ToArray();
            var libraries = context.Library.GetVirtualFolders().SelectMany(f => f.Locations ?? Array.Empty<string>()).Where(l => !string.IsNullOrWhiteSpace(l) && Path.IsPathRooted(l)).Select(Full).ToArray();
            var protectedFolders = context.EmbyRoots.Concat(libraries).ToArray();
            var queue = new Queue<string>(dirty);
            while (queue.Count > 0)
            {
                var directory = queue.Dequeue();
                if (!Removable(directory, mounts, protectedFolders) || !Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) continue;
                try
                {
                    context.Logger.Info("DeepDelete - Attempting to delete empty folder: {0}", directory);
                    context.Files.DeleteDirectory(directory, false, false);
                    Interlocked.Increment(ref foldersRemoved);
                    var parent = Path.GetDirectoryName(directory);
                    if (!string.IsNullOrEmpty(parent)) queue.Enqueue(parent);
                }
                catch (Exception error) { Log(context, "DeepDelete - Failed to delete folder: {0} {1}", directory, error.Message); }
            }
        }
        catch (Exception error) { Log(context, "DeepDelete - aborted: {0}", error.Message, ""); }
        finally { Worker.Release(); }
    }
    private static string? Refuse(Context context, string target, Plan plan)
    {
        if (Directory.Exists(target)) return "target is a directory";
        if (!VideoExtensions.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase)) return "not a recognized media file";
        if (context.EmbyRoots.Any(r => Same(r, target) || Under(target, r))) return "inside Emby's own program/data folders";
        // Another item still pointing at the same target keeps it (the original deleted shared targets too).
        var items = context.Library.GetItemList(new InternalItemsQuery { MediaTypes = new[] { "Video" }, Recursive = true, GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false, Limit = ReferenceBudget + 1 });
        if (items.Length > ReferenceBudget) return "reference check budget exceeded";
        foreach (var item in items)
        {
            if (plan.DeletedIds.Contains(item.InternalId) || string.IsNullOrEmpty(item.Path)) continue;
            var path = Full(item.Path);
            if (Same(path, target)) return "still a library item";
            if (item is Video && (path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).LinkTarget != null))
            {
                try { if (Same(TargetOf(context, item, path, context.Library.GetCollectionFolders(item)), target)) return "still referenced by " + item.Name; }
                catch { return "reference of " + item.Name + " could not be resolved"; }
            }
        }
        return null;
    }
    /// <summary>Same folder, original extension list, name = target name exactly or followed by '.', '-' or '_' (a space starts a different title, e.g. "Movie 2").</summary>
    internal static IEnumerable<string> Sidecars(string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var images = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".tbn", ".bmp" };
        var others = Directory.EnumerateFiles(directory).Where(f => !Same(f, target) && VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension).ToArray();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (Same(file, target)) continue;
            var extension = Path.GetExtension(file);
            if (!SidecarExtensions.Concat(images).Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            if (!(name.Equals(stem, StringComparison.OrdinalIgnoreCase) || name.Length > stem.Length && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && ".-_".Contains(name[stem.Length]))) continue;
            // Another media file whose name also prefixes this sidecar (and is at least as specific) owns it.
            if (others.Any(o => o!.Length >= stem.Length && name.StartsWith(o, StringComparison.OrdinalIgnoreCase))) continue;
            yield return file;
        }
    }
    internal static bool Removable(string directory, string[] mounts, string[] protectedFolders)
    {
        var full = Full(directory);
        var depth = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth <= 2) return false;
        if (mounts.Any(m => Same(m, full) || Under(m, full))) return false;
        if (protectedFolders.Any(r => Same(r, full) || Under(r, full) || Under(full, r))) return false;
        return true;
    }
    private static bool Delete(Context context, string path)
    {
        try
        {
            context.Logger.Info("DeepDelete - Attempting to delete file: {0}", path);
            context.Files.DeleteFile(path, true);
            return true;
        }
        catch (Exception error) { Log(context, "DeepDelete - Failed to delete file: {0} {1}", path, error.Message); return false; }
    }
    private static IReadOnlyCollection<string> ReadMounts()
    {
        try
        {
            return File.ReadLines("/proc/self/mountinfo").Select(l => l.Split(' ')).Where(p => p.Length > 4)
                .Select(p => p[4].Replace("\\040", " ")).Where(p => p.StartsWith('/')).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
    private static void Log(Context context, string format, params object[] args) { try { context.Logger.Warn(format, args); } catch { } }
    private static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string? a, string? b) => a != null && b != null && Comparer.Equals(Full(a), Full(b));
    private static bool Under(string path, string root)
    {
        var r = Full(root) + Path.DirectorySeparatorChar;
        return Full(path).StartsWith(r, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    internal static bool Verify(MethodInfo method)
    {
        var assembly = method.Module.Assembly; var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        return method.DeclaringType?.FullName == "Emby.Server.Implementations.Library.LibraryManager" && method.IsPublic && !method.IsStatic &&
            method.ReturnType == typeof(void) && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(BaseItem), typeof(DeleteOptions), typeof(BaseItem), typeof(bool) }) &&
            assembly.GetName().Name == "Emby.Server.Implementations" && assembly.GetName().Version == new Version(4, 10, 2, 0) &&
            method.Module.ModuleVersionId == new Guid("2009e43d-fa42-4e57-a902-2784a8306072") && method.MetadataToken == 0x060009FB &&
            body is { InitLocals: true, MaxStackSize: 7, LocalSignatureMetadataToken: 0x11000251 } && body.LocalVariables.Count == 12 && body.ExceptionHandlingClauses.Count == 7 &&
            il?.Length == 748 && Convert.ToHexString(SHA256.HashData(il)) == "CE7D882958FBCEE4F7B515E85A86FEF9B67CFCD15BFC479E0E8D5500E2EE6892" &&
            !string.IsNullOrEmpty(assembly.Location) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) == AssemblyHash;
    }
}
