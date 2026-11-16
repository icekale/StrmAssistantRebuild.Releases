using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Library;

namespace StrmAssistantRebuild.Common;

/// <summary>
/// M172: the original exclusive mode keeps library structure edits from starting a library scan (decompiled A:71454 prefix
/// that sets the request's RefreshLibrary to false, and the A:71460 transpiler that drops the QueueLibraryScan call from
/// LibraryManager.AddBackMediaPathToAllCollectionFolders). On 4.10.2 only Post(AddVirtualFolder) and Post(AddMediaPath) read
/// RefreshLibrary; the original's RemoveVirtualFolder/RemoveMediaPath prefixes have no effect there and are not installed.
/// Instead of deleting the call, the rebuild replaces it with a guarded call that still queues the native scan whenever the
/// control is not active, so a failed cleanup never leaves the host without the scan.
/// Active only while ExclusiveExtract is on; every target is pinned by assembly, MVID, token, IL, locals and EH.
/// </summary>
public static class ExclusiveLibraryScanControl
{
    private const string Owner = "StrmAssistantRebuild.ExclusiveLibraryScan.4.10.2";
    private const string ServiceType = "Emby.Api.Library.LibraryStructureService";
    private const string ManagerType = "Emby.Server.Implementations.Library.LibraryManager";
    internal const string ApiHash = "2D5F2B4CB92DE964B478A3C50787515AEFB750C0041BC019E6BCCDFDF231AD4C";
    internal const string ServerHash = "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F";
    private static readonly Guid ApiMvid = new("0873fdaf-f3f8-45f4-8d9c-b647b1edf372");
    private static readonly Guid ServerMvid = new("2009e43d-fa42-4e57-a902-2784a8306072");
    private static readonly Version ExpectedVersion = new(4, 10, 2, 0);
    private static readonly object Gate = new();
    private static Harmony? patcher;
    private static MethodInfo[] installed = Array.Empty<MethodInfo>();
    private static (Type Type, PropertyInfo Property)[] requests = Array.Empty<(Type, PropertyInfo)>();
    private static int enabled;
    private static long requestsChanged, scansSkipped;
    public static bool IsEnabled => Volatile.Read(ref enabled) != 0 && installed.Length == 3 && installed.All(Healthy);
    public static long RequestsChanged => Interlocked.Read(ref requestsChanged);
    public static long ScansSkipped => Interlocked.Read(ref scansSkipped);
    public static string Detail { get; private set; } = "Disabled; native library structure scans unchanged.";

    public static bool Configure(Version hostVersion, bool exclusiveEnabled, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref enabled, 0);
            try
            {
                if (!exclusiveEnabled) { Remove(); detail = Detail = "Disabled; native library structure scans unchanged."; return true; }
                if (hostVersion != ExpectedVersion) throw new NotSupportedException("requires exact host 4.10.2.0");
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var api = assemblies.SingleOrDefault(a => a.GetName().Name == "Emby.Api");
                var server = assemblies.SingleOrDefault(a => a.GetName().Name == "Emby.Server.Implementations");
                if (api == null || server == null) throw new NotSupportedException("Emby.Api or Emby.Server.Implementations is not loaded");
                var targets = Targets(api, server);
                if (!Verify(targets, out var found)) throw new NotSupportedException("host file, signature, MVID, IL, locals or EH differ");
                if (targets.Any(t => Harmony.GetPatchInfo(t)?.Owners.Any(o => o != Owner) == true)) throw new NotSupportedException("foreign library structure patch present");
                requests = found;
                if (!installed.SequenceEqual(targets) || !installed.All(Healthy))
                {
                    Remove();
                    patcher ??= new Harmony(Owner);
                    var list = new List<MethodInfo>();
                    try
                    {
                        foreach (var target in targets.Take(2)) { list.Add(target); patcher.Patch(target, prefix: new HarmonyMethod(Hook(nameof(NoRefresh)))); }
                        list.Add(targets[2]); patcher.Patch(targets[2], transpiler: new HarmonyMethod(Hook(nameof(GuardScan))));
                    }
                    finally { installed = list.ToArray(); }
                }
                Volatile.Write(ref enabled, 1);
                detail = Detail = "Active: adding a library or a media path does not start a library scan; removing a path does not queue one (original exclusive mode). Native RemoveVirtualFolder/RemoveMediaPath do not read RefreshLibrary on 4.10.2.";
                return true;
            }
            catch (Exception error)
            {
                detail = Detail = "Inactive: " + error.Message + "; native library structure scans retained.";
                try { Remove(); } catch { detail = Detail += " Cleanup pending; guarded calls pass through."; }
                return false;
            }
        }
    }

    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref enabled, 0);
            try { Remove(); Detail = "Disabled; native library structure scans unchanged."; }
            catch { Detail = "Disabled; cleanup pending; guarded calls pass through."; }
        }
    }

    private static void Remove()
    {
        var list = installed.ToList();
        for (var i = list.Count - 1; i >= 0; i--) { patcher?.Unpatch(list[i], HarmonyPatchType.All, Owner); list.RemoveAt(i); installed = list.ToArray(); }
        installed = Array.Empty<MethodInfo>();
    }

    private static MethodInfo Hook(string name) => typeof(ExclusiveLibraryScanControl).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static bool Healthy(MethodInfo method)
    {
        var info = Harmony.GetPatchInfo(method);
        if (info == null || info.Owners.Any(o => o != Owner) || info.Postfixes.Count + info.Finalizers.Count != 0) return false;
        return method.Name == "AddBackMediaPathToAllCollectionFolders"
            ? info.Prefixes.Count == 0 && info.Transpilers.Count == 1 && info.Transpilers[0].PatchMethod == Hook(nameof(GuardScan))
            : info.Transpilers.Count == 0 && info.Prefixes.Count == 1 && info.Prefixes[0].PatchMethod == Hook(nameof(NoRefresh));
    }

    /// <summary>[Post(AddVirtualFolder), Post(AddMediaPath), LibraryManager.AddBackMediaPathToAllCollectionFolders].</summary>
    internal static MethodInfo[] Targets(Assembly api, Assembly server)
    {
        var service = api.GetType(ServiceType, true)!;
        MethodInfo Post(string request) => service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == "Post" && m.GetParameters() is { Length: 1 } p && p[0].ParameterType.FullName == "Emby.Api.Library." + request);
        var manager = server.GetType(ManagerType, true)!;
        return new[]
        {
            Post("AddVirtualFolder"), Post("AddMediaPath"),
            manager.GetMethod("AddBackMediaPathToAllCollectionFolders", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null)!
        };
    }

    internal static bool Verify(MethodInfo[] targets, out (Type, PropertyInfo)[] found)
    {
        found = Array.Empty<(Type, PropertyInfo)>();
        if (targets.Length != 3 || targets.Any(t => t == null)) return false;
        if (!Pin(targets[0], ApiMvid, 0x060006C7, 240, "A2C226A3EDABE25078603D2E1F01A09E4F9F266EDB34DA30DD65AAB0C4D99A22", 6, 0x110000F1, "070312829912711245") ||
            !Pin(targets[1], ApiMvid, 0x060006CA, 78, "D52CE20AF569CD34931C4BFE34F7ACA80B0CBB9300040713F88629CB3C0BDCC0", 6, 0x110000F2, "070112848D") ||
            !Pin(targets[2], ServerMvid, 0x06000AB0, 220, "322B08B64E079F14F2F9BC770348ACE447E5227955CB3BA58FBEA6152B4F5AD6", 7, 0x11000291,
                "070A021287D91D12837D0812837D1283811D128729081287291283B5")) return false;
        if (!targets[0].IsPublic || !targets[1].IsPublic || !targets[2].IsPrivate || targets.Any(t => t.IsStatic || t.ReturnType != typeof(void))) return false;
        if (QueueScan(targets[2]) is not { } queue || queue.MetadataToken != 0x06000A19 || queue.DeclaringType != targets[2].DeclaringType ||
            !typeof(ILibraryManager).IsAssignableFrom(queue.DeclaringType) || queue.GetParameters().Length != 0) return false;
        var list = new List<(Type, PropertyInfo)>();
        foreach (var target in targets.Take(2))
        {
            var type = target.GetParameters()[0].ParameterType;
            var property = type.GetProperty("RefreshLibrary", BindingFlags.Public | BindingFlags.Instance);
            if (property == null || property.PropertyType != typeof(bool) || property.SetMethod is not { IsPublic: true } || !type.IsClass) return false;
            list.Add((type, property));
        }
        if (!FileHash(targets[0].Module.Assembly, ApiHash) || !FileHash(targets[2].Module.Assembly, ServerHash)) return false;
        found = list.ToArray();
        return true;
    }

    private static bool Pin(MethodInfo method, Guid mvid, int token, int length, string hash, int stack, int locals, string localSignature)
    {
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        return method.Module.ModuleVersionId == mvid && method.Module.Assembly.GetName().Version == ExpectedVersion && method.MetadataToken == token &&
            method.GetMethodImplementationFlags() == MethodImplAttributes.IL && body is { InitLocals: true } && body.MaxStackSize == stack &&
            body.LocalSignatureMetadataToken == locals && Convert.ToHexString(method.Module.ResolveSignature(locals)) == localSignature &&
            body.ExceptionHandlingClauses.Count == 0 && il?.Length == length && Convert.ToHexString(SHA256.HashData(il)) == hash;
    }

    private static bool FileHash(Assembly assembly, string hash)
    {
        if (string.IsNullOrEmpty(assembly.Location)) return false;
        using var stream = File.OpenRead(assembly.Location);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)) == hash;
    }

    /// <summary>The native method's single call to the private-host QueueLibraryScan (IL tail: ldarg.0; call 0x06000A19; ret).</summary>
    private static MethodInfo? QueueScan(MethodInfo method)
    {
        var il = method.GetMethodBody()!.GetILAsByteArray()!;
        // Pinned IL ends with: ldloc.0; brfalse.s +6; ldarg.0; call <token>; ret.
        if (il.Length < 10 || il[^10] != 0x06 || il[^9] != 0x2C || il[^8] != 0x06 || il[^7] != 0x02 || il[^6] != 0x28 || il[^1] != 0x2A) return null;
        return method.Module.ResolveMethod(BitConverter.ToInt32(il, il.Length - 5)) as MethodInfo;
    }

    private static void NoRefresh(object __0)
    {
        if (Volatile.Read(ref enabled) == 0 || __0 == null) return;
        var type = __0.GetType();
        foreach (var (requestType, property) in requests)
        {
            if (requestType != type) continue;
            try { if ((bool)property.GetValue(__0)!) { property.SetValue(__0, false); Interlocked.Increment(ref requestsChanged); } }
            catch (Exception) { /* The native request keeps its own value. */ }
            return;
        }
    }

    private static IEnumerable<CodeInstruction> GuardScan(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var list = instructions.ToList();
        var queue = QueueScan((MethodInfo)original) ?? throw new InvalidOperationException("QueueLibraryScan call not found");
        var calls = list.Where(i => i.Calls(queue)).ToList();
        if (calls.Count != 1) throw new InvalidOperationException("expected exactly one QueueLibraryScan call");
        calls[0].opcode = OpCodes.Call;
        calls[0].operand = Hook(nameof(QueueUnlessExclusive));
        return list;
    }

    private static void QueueUnlessExclusive(ILibraryManager manager)
    {
        if (Volatile.Read(ref enabled) != 0) { Interlocked.Increment(ref scansSkipped); return; }
        manager.QueueLibraryScan();
    }
}
