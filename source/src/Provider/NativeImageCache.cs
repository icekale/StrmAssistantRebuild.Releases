using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Options;
using CacheMode = StrmAssistantRebuild.Options.MetadataBuildOptions.MetadataCacheOption;

namespace StrmAssistantRebuild.Provider;

/// <summary>
/// M150: Original-image part of the metadata cache. A pinned prefix on native
/// ItemImageProvider.RefreshFromProvider (remote overload) restores cached TheMovieDb images for Movie/Series/Season/BoxSet
/// before the provider runs, and skips the remote fetch when every required type is satisfied. Required = provider-supported
/// enabled types; optional = MetadataCacheOptionalImageTypes plus, for Movie/Series/BoxSet, Thumb/Banner/Disc/Art (original
/// constants). Images that native downloads in the same pass are copied into the cache afterwards. Disk layout is the
/// rebuild's own ({root}/tmdb/images/{kind}-{id}[-sN]/{type}.{ext}); the original layout strings are not recoverable.
/// </summary>
public static class NativeImageCache
{
    private const string Owner = "StrmAssistantRebuild.NativeImageCache.4.10.2";
    private const string TypeName = "Emby.Providers.Manager.ItemImageProvider";
    internal const string AssemblyHash = "16874EA37E2EC8152E7F2FA9F9D7FA51A284959DCE3A918694003D009B2B2A94";
    public const string ProviderName = "TheMovieDb";
    public const int MaxFileBytes = 20 * 1024 * 1024;
    public const long DefaultMaxTotalBytes = 2L * 1024 * 1024 * 1024;
    // Original iteration order and the four extra types tried for Movie/Series/BoxSet (AA---------.cs:12124-12150).
    public static readonly ImageType[] Order = { ImageType.Primary, ImageType.Logo, ImageType.Backdrop, ImageType.Thumb, ImageType.Banner, ImageType.Disc, ImageType.Art };
    public static readonly ImageType[] Extras = { ImageType.Thumb, ImageType.Banner, ImageType.Disc, ImageType.Art };
    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    { [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp", [".gif"] = "image/gif", [".svg"] = "image/svg+xml" }; // TheMovieDb logos are often SVG
    private static readonly object Gate = new();
    private static Harmony? patcher;
    private static MethodInfo? installed;
    private static State? active;
    private static long restored, skipped, written;
    private sealed class State
    {
        public CacheMode Mode; public string Root = ""; public EmbyPathPolicy Paths = null!;
        public HashSet<ImageType> Optional = new(); public long MaxTotalBytes; public long TotalBytes;
        public bool Reads => Mode == CacheMode.PreferCacheThenOnline;
        public bool Writes => Mode is CacheMode.PreferCacheThenOnline or CacheMode.PreferOnlineWithCache;
    }
    public sealed record WriteState(BaseItem Item, string Folder, ImageType[] Before, ImageType[] Restored, ImageType[] Candidates);
    public static bool IsEnabled => Volatile.Read(ref active) != null;
    public static long Restored => Interlocked.Read(ref restored);
    public static long SkippedRemote => Interlocked.Read(ref skipped);
    public static long Written => Interlocked.Read(ref written);
    public static string Detail { get; private set; } = "Inactive; native image refresh unchanged.";

    public static HashSet<ImageType> ParseOptional(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => Enum.TryParse<ImageType>(t, true, out var type) && Enum.IsDefined(type) ? type : (ImageType?)null)
            .Where(t => t.HasValue).Select(t => t!.Value).ToHashSet();

    public static bool Configure(Version hostVersion, MetadataEnhanceOptions options, EmbyPathPolicy paths, string defaultRoot, out string detail, long maxTotalBytes = DefaultMaxTotalBytes)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try
            {
                Remove();
                var build = options.MetadataBuildOptions;
                if (!MetadataCacheRules.TryMode(build.MetadataCacheMode, out var mode)) { detail = Detail = "Inactive: unknown metadata cache mode."; return false; }
                if (mode == CacheMode.None) { detail = Detail = "Inactive; metadata cache None (no image cache IO)."; return true; }
                if (mode == CacheMode.UseOverrideCacheOnly) { detail = Detail = "Inactive; UseOverrideCacheOnly covers override JSON only, no image cache IO."; return true; }
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.Providers");
                var target = assembly?.GetType(TypeName)?.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .SingleOrDefault(m => m.Name == "RefreshFromProvider" && m.GetParameters().Length == 10 && m.GetParameters()[2].ParameterType == typeof(IRemoteImageProvider));
                if (hostVersion != new Version(4, 10, 2, 0) || target == null || !Verify(target))
                    throw new NotSupportedException("host file, signature, MVID, IL or locals differ");
                if (Harmony.GetPatchInfo(target)?.Owners.Any(o => o != Owner) == true) throw new NotSupportedException("foreign image refresh patch present");
                var root = build.MetadataCacheRootFolder?.Trim();
                var folder = Path.Combine(string.IsNullOrWhiteSpace(root) ? defaultRoot : root, "tmdb", "images");
                if (!Path.IsPathFullyQualified(folder)) throw new IOException("Cache root must be absolute.");
                var state = new State { Mode = mode, Root = Path.GetFullPath(folder), Paths = paths, Optional = ParseOptional(build.MetadataCacheOptionalImageTypes), MaxTotalBytes = maxTotalBytes };
                state.TotalBytes = Directory.Exists(state.Root) ? new DirectoryInfo(state.Root).EnumerateFiles("*", SearchOption.AllDirectories).Take(500_000).Sum(f => f.Length) : 0;
                installed = target;
                (patcher ??= new Harmony(Owner)).Patch(target,
                    prefix: new HarmonyMethod(typeof(NativeImageCache).GetMethod(nameof(Before), BindingFlags.Static | BindingFlags.NonPublic)!),
                    postfix: new HarmonyMethod(typeof(NativeImageCache).GetMethod(nameof(After), BindingFlags.Static | BindingFlags.NonPublic)!));
                Volatile.Write(ref active, state);
                detail = Detail = "Active; " + mode + ": TheMovieDb Movie/Series/Season/BoxSet images " + (state.Reads ? "restored from and " : "") + "written to the cache; optional types [" +
                    string.Join(",", state.Optional.OrderBy(t => t)) + "]; budget " + (maxTotalBytes >> 20) + " MiB, used " + (state.TotalBytes >> 20) + " MiB.";
                return true;
            }
            catch (Exception error)
            {
                detail = Detail = "Inactive: " + error.Message + "; native image refresh retained.";
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
            try { Remove(); Detail = "Disabled; native image refresh unchanged."; }
            catch { Detail = "Disabled; cleanup pending; native passthrough."; }
        }
    }
    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.All, Owner);
        installed = null;
    }

    /// <summary>Cache folder name for an admitted item, or null (other kinds, no TMDB id).</summary>
    public static string? FolderName(BaseItem item)
    {
        static string? Id(BaseItem? owner) => owner?.GetProviderId(MetadataProviders.Tmdb) is { Length: > 0 and <= 10 } id && id.All(c => c is >= '0' and <= '9') && id[0] != '0' ? id : null;
        return item switch
        {
            Movie => Id(item) is { } id ? "movie-" + id : null,
            Series => Id(item) is { } id ? "series-" + id : null,
            BoxSet => Id(item) is { } id ? "boxset-" + id : null,
            Season season when season.IndexNumber is >= 0 and <= 10000 => SeasonSeries(season) is { } sid ? "season-" + sid + "-s" + season.IndexNumber.Value : null,
            _ => null
        };
        string? SeasonSeries(Season season) { try { return Id(season.Series); } catch { return null; } }
    }
    internal static string? Find(string folder, ImageType type)
    {
        if (!Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder, type.ToString().ToLowerInvariant() + ".*")
            .Where(f => Mime.ContainsKey(Path.GetExtension(f)) && new FileInfo(f) is { Length: > 0 and <= MaxFileBytes })
            .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
    }
    private static void MarkImageUpdate(object result)
    {
        var property = result.GetType().GetProperty("UpdateType");
        if (property?.GetValue(result) is ItemUpdateType current) property.SetValue(result, current | ItemUpdateType.ImageUpdate);
    }

    private static bool Before(BaseMetadataResult itemResult, LibraryOptions libraryOptions, IRemoteImageProvider provider, ImageRefreshOptions refreshOptions,
        bool replaceAllImages, TypeOptions savedOptions, List<ImageType> downloadedImages, object result, CancellationToken cancellationToken,
        IProviderManager ____providerManager, ref Task __result, out WriteState? __state)
    {
        __state = null;
        var state = Volatile.Read(ref active);
        var item = itemResult?.BaseItem;
        if (state == null || item == null || provider == null || savedOptions == null || downloadedImages == null ||
            !string.Equals(provider.Name, ProviderName, StringComparison.OrdinalIgnoreCase) || cancellationToken.IsCancellationRequested) return true;
        try
        {
            if (installed == null || Harmony.GetPatchInfo(installed)?.Owners.Any(o => o != Owner) == true)
            { Volatile.Write(ref active, null); Detail = "Inactive: image refresh ownership changed; native passthrough."; return true; }
            var name = FolderName(item);
            if (name == null || !item.SupportsRemoteImageDownloading) return true;
            var folder = Path.Combine(state.Root, name);
            var extras = item is Movie or Series or BoxSet;
            var wanted = provider.GetSupportedImages(item).Concat(extras ? Extras : Array.Empty<ImageType>())
                .Where(t => savedOptions.IsEnabled(t) && savedOptions.GetLimit(t) > 0).ToHashSet();
            var before = downloadedImages.ToArray();
            var satisfied = new HashSet<ImageType>();
            var restoredNow = new List<ImageType>();
            if (state.Reads)
            {
                foreach (var type in Order.Where(wanted.Contains))
                {
                    if (downloadedImages.Contains(type) || !replaceAllImages && item.HasImage(type)) { satisfied.Add(type); continue; }
                    var file = Find(folder, type);
                    if (file == null) continue;
                    // Same as the original: a synchronous restore inside the prefix (Task.Run(...).Result).
                    var ok = Task.Run(async () =>
                    {
                        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                        await ____providerManager.SaveImage(item, libraryOptions, stream, Mime[Path.GetExtension(file)].AsMemory(), type, null, Array.Empty<long>(),
                            refreshOptions.DirectoryService, false, cancellationToken).ConfigureAwait(false);
                        return true;
                    }).GetAwaiter().GetResult();
                    if (!ok) continue;
                    downloadedImages.Add(type);
                    MarkImageUpdate(result);
                    satisfied.Add(type); restoredNow.Add(type);
                    Interlocked.Increment(ref restored);
                }
            }
            if (state.Writes) __state = new WriteState(item, folder, before, restoredNow.ToArray(), Order.Where(wanted.Contains).ToArray());
            var optional = new HashSet<ImageType>(state.Optional);
            if (extras) optional.UnionWith(Extras);
            if (state.Reads && wanted.Where(t => !optional.Contains(t)).All(satisfied.Contains) && restoredNow.Count > 0)
            {
                // Every required type is now present: the remote image list is not requested at all.
                Interlocked.Increment(ref skipped);
                __result = Task.CompletedTask;
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Detail = "Active; last cache restore failed (" + error.GetType().Name + "); native refresh continued.";
            return true;
        }
    }

    private static void After(ref Task __result, WriteState? __state, List<ImageType> downloadedImages)
    {
        if (__state == null || __result == null) return;
        __result = WriteAfter(__result, __state, downloadedImages);
    }
    private static async Task WriteAfter(Task native, WriteState write, List<ImageType> downloadedImages)
    {
        await native.ConfigureAwait(false);
        var state = Volatile.Read(ref active);
        if (state == null) return;
        try
        {
            ImageType[] fresh;
            lock (downloadedImages) fresh = downloadedImages.Skip(write.Before.Length).Distinct().ToArray();
            foreach (var type in write.Candidates.Where(t => fresh.Contains(t) && !write.Restored.Contains(t)))
            {
                var image = write.Item.GetImageInfo(type, 0);
                if (image == null || !image.IsLocalFile || !File.Exists(image.Path)) continue;
                var info = new FileInfo(image.Path);
                var extension = Path.GetExtension(info.Name).ToLowerInvariant();
                if (!Mime.ContainsKey(extension) || info.Length <= 0 || info.Length > MaxFileBytes) continue;
                if (Interlocked.Add(ref state.TotalBytes, info.Length) > state.MaxTotalBytes)
                { Interlocked.Add(ref state.TotalBytes, -info.Length); Detail = "Active; cache budget reached, new images not written."; continue; }
                Directory.CreateDirectory(state.Paths.RequireWritablePath(write.Folder));
                foreach (var old in Directory.EnumerateFiles(write.Folder, type.ToString().ToLowerInvariant() + ".*").ToArray())
                { var length = new FileInfo(old).Length; File.Delete(old); Interlocked.Add(ref state.TotalBytes, -length); }
                var target = state.Paths.RequireWritablePath(Path.Combine(write.Folder, type.ToString().ToLowerInvariant() + extension));
                var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(info.FullName, temp, true);
                File.Move(temp, target, true);
                Interlocked.Increment(ref written);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Detail = "Active; last cache write failed (" + error.GetType().Name + "); images remain on the item.";
        }
    }

    internal static bool Verify(MethodInfo method)
    {
        var type = method.DeclaringType; var assembly = method.Module.Assembly;
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        var parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
        return type is { IsPublic: true, IsSealed: true } && type.FullName == TypeName && method.IsPrivate && !method.IsStatic && method.ReturnType == typeof(Task) &&
            parameters.Length == 10 && parameters[0] == typeof(BaseMetadataResult) && parameters[1] == typeof(LibraryOptions) && parameters[2] == typeof(IRemoteImageProvider) &&
            parameters[3] == typeof(ImageRefreshOptions) && parameters[4] == typeof(bool) && parameters[5] == typeof(TypeOptions) && parameters[6] == typeof(int) &&
            parameters[7] == typeof(List<ImageType>) && parameters[8].FullName == "Emby.Providers.Manager.RefreshResult" && parameters[9] == typeof(CancellationToken) &&
            assembly.GetName().Name == "Emby.Providers" && assembly.GetName().Version == new Version(4, 10, 2, 0) &&
            method.Module.ModuleVersionId == new Guid("29b37ec2-5f02-443c-a995-1f61c71f43c6") && method.MetadataToken == 0x060001F1 &&
            method.GetMethodImplementationFlags() == MethodImplAttributes.IL &&
            body is { InitLocals: true, MaxStackSize: 2, LocalSignatureMetadataToken: 0x1100009E } && body.ExceptionHandlingClauses.Count == 0 &&
            Convert.ToHexString(method.Module.ResolveSignature(body.LocalSignatureMetadataToken)) == "070111830C" && body.LocalVariables.Count == 1 &&
            il?.Length == 142 && Convert.ToHexString(SHA256.HashData(il)) == "4B86BF98E64BF0581630A6D98225B2298190B49C9D5484D85DDDDBB4DFBF6127" &&
            !string.IsNullOrEmpty(assembly.Location) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) == AssemblyHash;
    }
}
