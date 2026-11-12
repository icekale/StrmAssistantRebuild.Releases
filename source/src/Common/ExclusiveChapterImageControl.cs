using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace StrmAssistantRebuild.Common;

/// <summary>
/// M159: the original's chapter-image hook (prefix on ThumbnailGenerator.RefreshThumbnailImages,
/// notes/decrypted/A----------/A----------.cs:71496-71563, cleanup helper :130391) under exclusive mode.
/// Inside the native RefreshSingleItem of the same item (not plugin-owned):
///  - native chapter/thumbnail extraction that this call performs passes the rebuild's low-level extraction gate, as the
///    original (which had no such gate) let native chapter extraction follow an allowed ffprobe;
///  - with CatchAllAllowChapterImage (3), when the item supports thumbnails and the library enables chapter image
///    extraction, extraction is forced and existing chapter images/thumbnail sets are replaced.
/// Rebuild difference: the original deleted existing chapter images and thumbnail sets before extraction; the rebuild sets
/// the host's own replaceExistingImages, so old files are replaced/removed by native code only as new ones are written.
/// </summary>
public static class ExclusiveChapterImageControl
{
    private const string Owner = "StrmAssistantRebuild.ExclusiveChapterImage.4.10.2";
    private const string TypeName = "Emby.Providers.MediaInfo.ThumbnailGenerator";
    private const string AssemblyHash = "16874EA37E2EC8152E7F2FA9F9D7FA51A284959DCE3A918694003D009B2B2A94";
    private static readonly object Gate = new();
    private static readonly AsyncLocal<long> Scope = new();
    private static Harmony? patcher;
    private static MethodInfo? installed;
    private static volatile bool enabled;
    private static long scoped, forced, extractionsAllowed;
    public static bool IsEnabled => enabled;
    public static long Scoped => Interlocked.Read(ref scoped);
    public static long Forced => Interlocked.Read(ref forced);
    public static long ExtractionsAllowed => Interlocked.Read(ref extractionsAllowed);
    public static string Detail { get; private set; } = "Disabled; native chapter image refresh unchanged.";

    public static bool Configure(Version hostVersion, bool exclusiveEnabled, out string detail)
    {
        lock (Gate)
        {
            enabled = false;
            try
            {
                if (!exclusiveEnabled) { Remove(); detail = Detail = "Disabled; native chapter image refresh unchanged."; return true; }
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.Providers");
                var target = Target(assembly);
                if (hostVersion != new Version(4, 10, 2, 0) || target == null || !Verify(target))
                    throw new NotSupportedException("host file, signature, MVID, IL, locals or EH differ");
                if (Harmony.GetPatchInfo(target)?.Owners.Any(o => o != Owner) == true)
                    throw new NotSupportedException("foreign chapter image patch present");
                if (installed != target)
                {
                    Remove();
                    installed = target;
                    (patcher ??= new Harmony(Owner)).Patch(target,
                        prefix: new HarmonyMethod(typeof(ExclusiveChapterImageControl).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)!),
                        postfix: new HarmonyMethod(typeof(ExclusiveChapterImageControl).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)!));
                }
                enabled = true;
                detail = Detail = "Active; native chapter extraction inside an item's own refresh passes the gate; CatchAllAllowChapterImage forces and replaces.";
                return true;
            }
            catch (Exception error)
            {
                detail = Detail = "Inactive: " + error.Message + "; native chapter extraction stays blocked in exclusive mode.";
                try { Remove(); } catch { detail = Detail += " Cleanup pending."; }
                return false;
            }
        }
    }
    public static void Disable()
    {
        lock (Gate)
        {
            enabled = false;
            try { Remove(); Detail = "Disabled; native chapter image refresh unchanged."; }
            catch { Detail = "Disabled; cleanup pending; passthrough."; }
        }
    }
    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.All, Owner);
        installed = null;
    }

    internal static MethodInfo? Target(Assembly? assembly) => assembly?.GetType(TypeName)?.GetMethod("RefreshThumbnailImages", BindingFlags.Instance | BindingFlags.Public, null,
        new[] { typeof(Video), typeof(MediaSourceInfo), typeof(MediaStream), typeof(LibraryOptions), typeof(IDirectoryService), typeof(List<ChapterInfo>), typeof(bool), typeof(bool), typeof(bool), typeof(CancellationToken) }, null);

    internal static bool Verify(MethodInfo method)
    {
        var type = method.DeclaringType; var assembly = method.Module.Assembly;
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        var names = method.GetParameters().Select(p => p.Name ?? "").ToArray();
        return type is { IsPublic: true, IsSealed: true } && type.FullName == TypeName && method.IsPublic && !method.IsStatic && !method.IsVirtual &&
            method.ReturnType == typeof(Task<bool>) && method.MetadataToken == 0x06000159 && method.GetMethodImplementationFlags() == MethodImplAttributes.IL &&
            names.SequenceEqual(new[] { "item", "mediaSource", "videoStream", "libraryOptions", "directoryService", "chapters", "extractImages", "replaceExistingImages", "saveChapters", "cancellationToken" }) &&
            assembly.GetName().Name == "Emby.Providers" && assembly.GetName().Version == new Version(4, 10, 2, 0) &&
            method.Module.ModuleVersionId == new Guid("29b37ec2-5f02-443c-a995-1f61c71f43c6") &&
            body is { InitLocals: true, MaxStackSize: 2, LocalSignatureMetadataToken: 0x11000062 } &&
            Convert.ToHexString(method.Module.ResolveSignature(body.LocalSignatureMetadataToken)) == "0701118290" &&
            body.ExceptionHandlingClauses.Count == 0 &&
            il?.Length == 142 && Convert.ToHexString(SHA256.HashData(il)) == "9C6AC5EFB1E3AFE37D13897824AB5244614FE8980BC5D1271FA6D486CA585188" &&
            !string.IsNullOrEmpty(assembly.Location) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) == AssemblyHash;
    }

    /// <summary>The scope flows into the async body (set before the state machine starts) and is restored for the caller by the postfix.</summary>
    private static void Prefix(Video item, LibraryOptions libraryOptions, ref bool extractImages, ref bool replaceExistingImages, out long __state)
    {
        __state = Scope.Value;
        if (!enabled || item == null || !ExclusiveEligibility.InNativeRefreshOf(item)) return;
        Scope.Value = item.InternalId;
        Interlocked.Increment(ref scoped);
        if (ForceFor(ExclusiveEligibility.ChapterImageSelected, item.SupportsThumbnails, libraryOptions?.EnableChapterImageExtraction == true))
        { extractImages = true; replaceExistingImages = true; Interlocked.Increment(ref forced); }
    }
    private static void Postfix(long __state) => Scope.Value = __state;
    /// <summary>Pure rule of the original prefix: feature 3, thumbnails supported, library chapter extraction enabled.</summary>
    public static bool ForceFor(bool chapterImageSelected, bool supportsThumbnails, bool libraryChapterExtraction) =>
        chapterImageSelected && supportsThumbnails && libraryChapterExtraction;

    /// <summary>Low-level gate consumer: a non-owned extraction running inside the scoped native RefreshThumbnailImages.</summary>
    internal static bool AllowExtraction()
    {
        if (!enabled || Scope.Value == 0) return false;
        Interlocked.Increment(ref extractionsAllowed);
        return true;
    }
    /// <summary>Test seam.</summary>
    internal static IDisposable EnterScopeForTest(long id) { var old = Scope.Value; Scope.Value = id; return new Restore(old); }
    private sealed class Restore(long old) : IDisposable { public void Dispose() => Scope.Value = old; }
}
