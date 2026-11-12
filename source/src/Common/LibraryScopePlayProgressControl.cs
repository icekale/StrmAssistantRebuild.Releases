using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Data;

namespace StrmAssistantRebuild.Common;

/// <summary>
/// M153: the original global LibraryScopePlayProgress (decompiled AA---------/A----------.cs:4043-4441). Postfixes on the
/// native user-data key getters prefix "{library InternalId}-" for Episodes (with season and episode numbers), Movies and
/// Series; Series.CreatePresentationUniqueKey is guarded so presentation keys stay unscoped. Keys match the original
/// byte for byte, so progress recorded by the original plugin is found again.
/// Like the original, nothing is copied, merged or moved: an item switches key the next time Emby saves it, its earlier
/// progress stays under the native key (visible again after OFF), and progress made while ON stays under the scoped key.
/// </summary>
public static class LibraryScopePlayProgressControl
{
    private const string Owner = "StrmAssistantRebuild.LibraryScopePlayProgress.4.10.2";
    private static readonly Guid Mvid = new("58dae430-7295-47bb-a62f-b26bf0d89604");
    private static readonly object Gate = new();
    private static readonly List<MethodBase> installed = new();
    private static Harmony? patcher;
    private static ILibraryManager? library;
    private static int enabled;
    [ThreadStatic] private static int presentationDepth;
    private static long scoped;
    public static bool IsEnabled => Volatile.Read(ref enabled) != 0;
    public static long ScopedKeys => Interlocked.Read(ref scoped);
    public static string Detail { get; private set; } = "Disabled; native user-data keys unchanged.";

    internal static (MethodInfo Method, int Token, int Length, string Hash)[] Targets() => new[]
    {
        (Get(typeof(Episode), "GetUserDataKeyInternal", typeof(IDataContext)), 0x060012C0, 145, "E7EFBD400B99D43DD7F3230CD73A76D0D59B9E9B90F474AD3C7F5B158F85AC16"),
        (Get(typeof(Video), "GetUserDataKeyInternal", typeof(IDataContext)), 0x0600128B, 33, "00EFFB7D4AD7AEB2FC01B0FEE808AE4B72C4DF919B60EAFF6871E7C21CDC5562"),
        (Get(typeof(Series), "GetUserDataKeyInternal", typeof(IDataContext)), 0x0600131F, 72, "18CCA621CB3945C32989BD651619FF170281A81728A00F3098037EC19F48B5AB"),
        (Get(typeof(Series), "CreatePresentationUniqueKey", typeof(BaseItem[]), typeof(MediaBrowser.Model.Configuration.LibraryOptions), typeof(IDataContext), typeof(CancellationToken)),
            0x0600131D, 105, "8C57EE31009248F0831C17C966D011354E04C73C0E83066960A99F0E8FC76436")
    };
    private static MethodInfo Get(Type type, string name, params Type[] parameters) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, parameters, null)
        ?? throw new MissingMethodException(type.Name, name);

    internal static bool Verify(out string detail)
    {
        try
        {
            var assembly = typeof(BaseItem).Assembly;
            if (assembly.GetName().Name != "MediaBrowser.Controller" || assembly.GetName().Version != new Version(4, 10, 2, 0) || assembly.ManifestModule.ModuleVersionId != Mvid)
            { detail = "MediaBrowser.Controller identity differs"; return false; }
            foreach (var (method, token, length, hash) in Targets())
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (method.MetadataToken != token || il?.Length != length || Convert.ToHexString(SHA256.HashData(il)) != hash || method.ReturnType != typeof(string) || method.IsStatic)
                { detail = method.DeclaringType!.Name + "." + method.Name + " differs"; return false; }
            }
            detail = "verified"; return true;
        }
        catch (Exception error) { detail = error.GetType().Name + ": " + error.Message; return false; }
    }

    /// <param name="explicitMappingsActive">The rebuild's explicit episode migration owns these getters while it retains rules; the two never run together.</param>
    public static bool Configure(Version hostVersion, bool enable, ILibraryManager libraryManager, bool explicitMappingsActive, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref enabled, 0);
            try
            {
                Remove();
                if (!enable) { detail = Detail = "Disabled; native user-data keys unchanged (progress recorded while ON stays under the library keys)."; return true; }
                if (hostVersion != new Version(4, 10, 2, 0)) throw new NotSupportedException("host version differs from 4.10.2.0");
                if (!Verify(out var why)) throw new NotSupportedException("host contract differs: " + why);
                if (explicitMappingsActive) throw new InvalidOperationException("explicit episode migration mappings are active; restore them first");
                var targets = Targets();
                if (targets.Any(t => Harmony.GetPatchInfo(t.Method)?.Owners.Any(o => o != Owner) == true)) throw new InvalidOperationException("foreign user-data key patch present");
                library = libraryManager;
                patcher ??= new Harmony(Owner);
                Patch(targets[0].Method, postfix: nameof(EpisodeKey));
                Patch(targets[1].Method, postfix: nameof(VideoKey));
                Patch(targets[2].Method, postfix: nameof(SeriesKey));
                Patch(targets[3].Method, prefix: nameof(EnterPresentation), finalizer: nameof(LeavePresentation));
                Volatile.Write(ref enabled, 1);
                detail = Detail = "Active; Episode/Movie/Series user-data keys prefixed with the library id (original format). Existing progress is not copied: items switch keys when Emby next saves them.";
                return true;
            }
            catch (Exception error)
            {
                detail = Detail = "Inactive: " + error.Message + "; native user-data keys retained.";
                try { Remove(); } catch { detail = Detail += " Cleanup pending."; }
                return false;
            }
        }
    }
    private static void Patch(MethodInfo target, string? prefix = null, string? postfix = null, string? finalizer = null)
    {
        HarmonyMethod? M(string? name) => name == null ? null : new HarmonyMethod(typeof(LibraryScopePlayProgressControl).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!);
        installed.Add(target);
        patcher!.Patch(target, prefix: M(prefix), postfix: M(postfix), finalizer: M(finalizer));
    }
    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref enabled, 0);
            try { Remove(); Detail = "Disabled; native user-data keys unchanged."; }
            catch { Detail = "Disabled; cleanup pending; keys pass through."; }
        }
    }
    private static void Remove()
    {
        foreach (var method in installed) patcher?.Unpatch(method, HarmonyPatchType.All, Owner);
        installed.Clear();
    }

    private static Folder? Library(BaseItem item, object[] args)
    {
        var manager = library;
        if (manager == null) return null;
        var context = args.Length > 0 ? args[0] as IDataContext : null;
        return manager.GetCollectionFolders(item, context, CancellationToken.None)?.FirstOrDefault();
    }
    private static void Scope(BaseItem item, object[] args, ref string result)
    {
        var folder = Library(item, args);
        if (folder == null) return;
        result = folder.InternalId + "-" + result;
        Interlocked.Increment(ref scoped);
    }
    private static void EpisodeKey(Episode __instance, object[] __args, ref string __result)
    {
        if (!IsEnabled || !__instance.ParentIndexNumber.HasValue || !__instance.IndexNumber.HasValue) return;
        Scope(__instance, __args, ref __result);
    }
    private static void VideoKey(Video __instance, object[] __args, ref string __result)
    {
        if (!IsEnabled || string.IsNullOrEmpty(__result) || __instance is not Movie) return;
        Scope(__instance, __args, ref __result);
    }
    private static void SeriesKey(Series __instance, object[] __args, ref string __result)
    {
        if (!IsEnabled || presentationDepth > 0) return;
        Scope(__instance, __args, ref __result);
    }
    private static void EnterPresentation() => presentationDepth++;
    private static Exception? LeavePresentation(Exception? __exception)
    {
        presentationDepth = presentationDepth <= 0 ? 0 : presentationDepth - 1;
        return __exception;
    }
}
