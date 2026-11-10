using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;

namespace StrmAssistantRebuild.Common;

/// <summary>Suppresses only the pinned native collection-library post-scan task, never manual collection operations.</summary>
public static class BoxsetAutoCreationControl
{
    private const string Owner = "StrmAssistantRebuild.BoxsetAutoCreation.4.10.2";
    private const string HostName = "Emby.Server.Implementations";
    private const string TaskName = HostName + ".Collections.CollectionLibraryPostScanTask";
    private const string ManagerName = HostName + ".Collections.CollectionManager";
    private static readonly Version Version = new(4, 10, 2, 0);
    private static readonly Guid Mvid = new("2009e43d-fa42-4e57-a902-2784a8306072");
    private static readonly object Gate = new();
    private sealed record Contract(MethodInfo Run, MethodInfo Ensure);
    private static Contract? active;
    private static Harmony? patcher;
    private static MethodInfo? installed;
    private static long suppressedRuns;
    /// <summary>Lifetime witness count; compare deltas across configuration changes.</summary>
    public static long SuppressedRuns => Interlocked.Read(ref suppressedRuns);
    public static bool IsEnabled => Volatile.Read(ref active) != null;
    public static string Detail { get; private set; } = "Disabled; native automatic and manual collection operations unchanged.";

    public static bool Configure(Version hostVersion, bool noBoxsetsAutoCreation, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try
            {
                Remove();
                if (!noBoxsetsAutoCreation)
                { detail = Detail = "Disabled; native collection-library post-scan task restored."; return true; }
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == HostName);
                var run = assembly?.GetType(TaskName)?.GetMethod("Run", new[] { typeof(IProgress<double>), typeof(CancellationToken) });
                var ensure = assembly?.GetType(ManagerName)?.GetMethod("EnsureLibraryFolder", BindingFlags.Instance | BindingFlags.NonPublic);
                if (hostVersion != Version || run == null || ensure == null || !Verify(run, ensure))
                { detail = Detail = "Inactive: exact native post-scan/Ensure contract, file, MVID, IL or locals differs; native behavior retained."; return false; }
                var contract = new Contract(run, ensure);
                if (ForeignOwner(contract))
                { detail = Detail = "Inactive: foreign post-scan/Ensure patch; native behavior retained."; return false; }
                installed = run; // Retain the removal handle even if installation throws partway through.
                (patcher ??= new Harmony(Owner)).Patch(run,
                    prefix: new HarmonyMethod(typeof(BoxsetAutoCreationControl).GetMethod(nameof(BeforeRun), BindingFlags.Static | BindingFlags.NonPublic)!));
                Volatile.Write(ref active, contract);
                detail = Detail = "Enabled: collection-library automatic post-scan creation only; manual Create/Add and existing collections unchanged. User-view hiding is not implemented here.";
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref active, null);
                Detail = "Inactive: post-scan guard installation failed (" + error.GetType().Name + "); native behavior retained.";
                try { Remove(); } catch { Detail += " Owned detour cleanup pending; guard inactive."; }
                detail = Detail;
                return false;
            }
        }
    }

    private static bool BeforeRun(ref Task __result)
    {
        var contract = Volatile.Read(ref active);
        if (contract == null) return true;
        try
        {
            if (!ForeignOwner(contract))
            {
                // Native Run only ensures the library and returns CompletedTask. It does not observe cancellation.
                // Skip before the query and _libraryCreated write; no stack heuristic, metadata or membership changes.
                __result = Task.CompletedTask;
                Interlocked.Increment(ref suppressedRuns);
                return false;
            }
        }
        catch { /* Observation failure leaves the native operation intact. */ }
        Volatile.Write(ref active, null);
        Detail = "Inactive: post-scan contract ownership changed; native behavior retained.";
        return true;
    }

    private static bool ForeignOwner(Contract contract) => new[] { contract.Run, contract.Ensure }
        .Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(owner => owner != Owner) == true);

    internal static bool Verify(MethodInfo run, MethodInfo ensure)
    {
        var assembly = run.Module.Assembly;
        if (assembly != ensure.Module.Assembly || assembly.GetName().Name != HostName || assembly.GetName().Version != Version ||
            run.Module.ModuleVersionId != Mvid || ensure.Module.ModuleVersionId != Mvid || string.IsNullOrEmpty(assembly.Location)) return false;
        using var file = File.OpenRead(assembly.Location);
        using var sha = SHA256.Create();
        if (Convert.ToHexString(sha.ComputeHash(file)) != "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F") return false;
        return run.DeclaringType is { IsPublic: true, IsSealed: true } && run.DeclaringType.FullName == TaskName &&
            typeof(ILibraryPostScanTask).IsAssignableFrom(run.DeclaringType) && run.Name == "Run" && run.IsPublic && run.IsFinal && run.IsVirtual &&
            !run.IsStatic && !run.ContainsGenericParameters && run.ReturnType == typeof(Task) &&
            run.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(IProgress<double>), typeof(CancellationToken) }) &&
            ensure.DeclaringType is { IsPublic: true, IsSealed: true } && ensure.DeclaringType.FullName == ManagerName &&
            ensure.Name == "EnsureLibraryFolder" && ensure.IsAssembly && !ensure.IsStatic && !ensure.IsVirtual &&
            !ensure.ContainsGenericParameters && ensure.ReturnType == typeof(void) && ensure.GetParameters().Length == 0 &&
            Match(run, 0x0600111D, 98, 6, 0x1100026D, "070112847D", new[] { typeof(InternalItemsQuery) },
                "F061DF82BBD85D5F9FF330EBFC95CEBACDE45A2F6212287187EC32964180020C") &&
            Match(ensure, 0x06001114, 117, 5, 0x110004B1, "07021283810E", new[] { typeof(LibraryOptions), typeof(string) },
                "8EE5101E4DBF58BED9D84DAAD4B6B91CD1899CBAD39C1765DBE8197280947250");
    }

    internal static bool Match(MethodInfo method, int token, int length, int stack, int signature, string signatureHex, Type[] locals, string hash)
    {
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        return method.MetadataToken == token && method.GetMethodImplementationFlags() == MethodImplAttributes.IL &&
            body is { InitLocals: true } && body.MaxStackSize == stack && body.LocalSignatureMetadataToken == signature &&
            Convert.ToHexString(method.Module.ResolveSignature(signature)) == signatureHex &&
            body.LocalVariables.All(v => !v.IsPinned) && body.LocalVariables.Select(v => v.LocalType).SequenceEqual(locals) &&
            body.ExceptionHandlingClauses.Count == 0 && il?.Length == length && Convert.ToHexString(SHA256.HashData(il)) == hash;
    }

    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try { Remove(); Detail = "Disabled; native collection-library post-scan task restored."; }
            catch { Detail = "Disabled; owned detour cleanup pending; guard inactive."; }
        }
    }
    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.Prefix, Owner);
        installed = null;
    }
}
