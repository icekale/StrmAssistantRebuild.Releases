using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace StrmAssistantRebuild.Provider;

/// <summary>
/// Original (M171, PreferOriginalPoster group, A:66527): a postfix on the native LocalImageProvider season-folder image lookup.
/// For season 0 without a local Primary it adds "season00-poster" as Primary, first from the series folder and then from the
/// season folder, through the native private GetFiles (CancellationToken.None, as the original) and AddImage helpers.
/// Native naming ("season-specials-poster" etc.) is unchanged; other seasons and items are untouched.
/// </summary>
public static class NativeSeasonZeroPoster
{
    private const string Owner = "StrmAssistantRebuild.NativeSeasonZeroPoster.4.10.2";
    private const string TypeName = "Emby.LocalMetadata.Images.LocalImageProvider";
    internal const string AssemblyHash = "E600DE1905475D09279CCFC290B0DC2FEB395E3B28A3BFFCEA7BD01A2170AADE";
    private static readonly Guid Mvid = new("28c402ef-9e0e-4b57-a1ac-c4d39b06cd22");
    private static readonly object Gate = new();
    private static Harmony? patcher;
    private static MethodInfo? installed, getFiles, addImage;
    private static int enabled;
    private static long added;
    public static bool IsEnabled => Volatile.Read(ref enabled) != 0 && installed != null && Healthy(installed);
    public static long Added => Interlocked.Read(ref added);
    public static string Detail { get; private set; } = "Disabled; native season image lookup unchanged.";

    public static bool Configure(Version hostVersion, bool enable, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref enabled, 0);
            try
            {
                if (!enable) { Remove(); detail = Detail = "Disabled; native season image lookup unchanged."; return true; }
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.LocalMetadata");
                var type = assembly?.GetType(TypeName);
                var target = type?.GetMethod("PopulateSeasonImagesFromSeasonOrSeriesFolder", BindingFlags.Instance | BindingFlags.NonPublic);
                if (hostVersion != new Version(4, 10, 2, 0) || target == null || !Verify(target, out var files, out var add))
                    throw new NotSupportedException("host file, signature, MVID, IL, locals or EH differ");
                if (Harmony.GetPatchInfo(target)?.Owners.Any(o => o != Owner) == true)
                    throw new NotSupportedException("foreign season image patch present");
                getFiles = files; addImage = add;
                if (installed != target)
                {
                    Remove();
                    installed = target;
                    (patcher ??= new Harmony(Owner)).Patch(target, postfix: new HarmonyMethod(typeof(NativeSeasonZeroPoster).GetMethod(nameof(AfterSeasonImages), BindingFlags.Static | BindingFlags.NonPublic)!));
                }
                Volatile.Write(ref enabled, 1);
                detail = Detail = "Active; season 0 without a local Primary also takes season00-poster (series folder, then season folder); native names unchanged.";
                return true;
            }
            catch (Exception error)
            {
                detail = Detail = "Inactive: " + error.Message + "; native season image lookup retained.";
                try { Remove(); } catch { detail = Detail += " Cleanup pending."; }
                return false;
            }
        }
    }
    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref enabled, 0);
            try { Remove(); Detail = "Disabled; native season image lookup unchanged."; }
            catch { Detail = "Disabled; cleanup pending; postfix passthrough."; }
        }
    }
    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.Postfix, Owner);
        installed = null;
    }
    private static bool Healthy(MethodInfo method)
    {
        var info = Harmony.GetPatchInfo(method);
        return info != null && info.Postfixes.Count == 1 && info.Postfixes[0].owner == Owner && info.Prefixes.Count + info.Transpilers.Count + info.Finalizers.Count == 0;
    }

    // Postfix after each native call (series folder, then season folder), exactly as the original.
    private static void AfterSeasonImages(object __instance, Season __0, LibraryOptions __2, List<LocalImageInfo> __3, IDirectoryService __4)
    {
        if (Volatile.Read(ref enabled) == 0) return;
        try { Apply(__instance, __0, __2, __3, __4); }
        catch (Exception) { /* A failed lookup leaves the native image list exactly as built. */ }
    }

    internal static bool Apply(object provider, Season season, LibraryOptions libraryOptions, List<LocalImageInfo> images, IDirectoryService directoryService,
        Func<BaseItem, FileSystemMetadata[]>? files = null, Func<FileSystemMetadata[], string, bool>? add = null)
    {
        var index = season?.IndexNumber;
        if (index != 0 || images == null || images.Any(i => i.Type == ImageType.Primary)) return false;
        files ??= item => (FileSystemMetadata[])getFiles!.Invoke(provider, new object?[] { item, libraryOptions, false, directoryService, CancellationToken.None })!;
        add ??= (found, name) => (bool)addImage!.Invoke(provider, new object[] { found, images, name, ImageType.Primary })!;
        var name = "season" + index.Value.ToString("00", CultureInfo.InvariantCulture) + "-poster";
        // The original passes season.Series unchecked; a missing series only skips that folder here instead of faulting the host.
        var series = season!.Series;
        var hit = series != null && add(files(series), name) || add(files(season), name);
        if (hit) Interlocked.Increment(ref added);
        return hit;
    }

    internal static bool Verify(MethodInfo method, out MethodInfo? files, out MethodInfo? add)
    {
        files = add = null;
        var type = method.DeclaringType; var assembly = method.Module.Assembly; var module = method.Module;
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        if (type is not { IsPublic: true, IsSealed: true } || type.FullName != TypeName || !typeof(ILocalImageFileProvider).IsAssignableFrom(type) ||
            !method.IsPrivate || method.IsStatic || method.ReturnType != typeof(void) || method.MetadataToken != 0x06000024 ||
            !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Season), typeof(BaseItem), typeof(LibraryOptions), typeof(List<LocalImageInfo>), typeof(IDirectoryService), typeof(CancellationToken) }) ||
            assembly.GetName().Version != new Version(4, 10, 2, 0) || module.ModuleVersionId != Mvid || method.GetMethodImplementationFlags() != MethodImplAttributes.IL ||
            body is not { InitLocals: true, MaxStackSize: 6, LocalSignatureMetadataToken: 0x11000010 } ||
            Convert.ToHexString(module.ResolveSignature(body.LocalSignatureMetadataToken)) != "0708151180C901081D12808D0E151275010E0E08151180CD010E0E" ||
            body.ExceptionHandlingClauses.Count != 1 || il?.Length != 301 || Convert.ToHexString(SHA256.HashData(il)) != "783D782D50BC1E32E011FD7644AB0220DBD87A2CADB82309A8D79367EA249DF0")
            return false;
        var getFiles = module.ResolveMethod(0x0600001B) as MethodInfo; var addImage = module.ResolveMethod(0x06000026) as MethodInfo;
        if (getFiles == null || getFiles.DeclaringType != type || getFiles.Name != "GetFiles" || !getFiles.IsPrivate || getFiles.IsStatic || getFiles.ReturnType != typeof(FileSystemMetadata[]) ||
            !getFiles.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(BaseItem), typeof(LibraryOptions), typeof(bool), typeof(IDirectoryService), typeof(CancellationToken) }) ||
            Hash(getFiles) != (344, "F5CF5911CA5420CCE84753401219308C903CA64130FEF82F6F2319E6AFDDD589")) return false;
        if (addImage == null || addImage.DeclaringType != type || addImage.Name != "AddImage" || !addImage.IsPrivate || addImage.IsStatic || addImage.ReturnType != typeof(bool) ||
            !addImage.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(FileSystemMetadata[]), typeof(List<LocalImageInfo>), typeof(string), typeof(ImageType) }) ||
            Hash(addImage) != (13, "F5DE8CB23CF35D1F314A7F0F095E85C843FEF85425D6C09990E693740E1D3FA7")) return false;
        if (string.IsNullOrEmpty(assembly.Location) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) != AssemblyHash) return false;
        files = getFiles; add = addImage;
        return true;
    }
    private static (int, string) Hash(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
        return (il.Length, Convert.ToHexString(SHA256.HashData(il)));
    }
}
