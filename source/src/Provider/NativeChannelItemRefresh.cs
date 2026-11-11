using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace StrmAssistantRebuild.Provider;

/// <summary>
/// Original (M149): native ChannelManager.GetChannelItemEntity queues a full metadata refresh after every channel item
/// write. For Douban channel items (rewritten on each listing) that refresh is skipped; every other channel is unchanged.
/// </summary>
public static class NativeChannelItemRefresh
{
    private const string Owner = "StrmAssistantRebuild.NativeChannelItemRefresh.4.10.2";
    private const string TypeName = "Emby.Server.Implementations.Channels.ChannelManager";
    internal const string AssemblyHash = "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F";
    private static readonly object Gate = new();
    private static Harmony? patcher;
    private static MethodInfo? installed;
    private static int enabled;
    private static long skipped;
    public static bool IsEnabled => Volatile.Read(ref enabled) != 0;
    public static long Skipped => Interlocked.Read(ref skipped);
    public static string Detail { get; private set; } = "Disabled; native channel item refresh unchanged.";

    public static bool Configure(Version hostVersion, bool enable, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref enabled, 0);
            try
            {
                if (!enable) { Remove(); detail = Detail = "Disabled; native channel item refresh unchanged."; return true; }
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.Server.Implementations");
                var target = assembly?.GetType(TypeName)?.GetMethod("GetChannelItemEntity", BindingFlags.Instance | BindingFlags.NonPublic);
                if (hostVersion != new Version(4, 10, 2, 0) || target == null || !Verify(target))
                    throw new NotSupportedException("host file, signature, MVID, IL, locals or EH differ");
                if (Harmony.GetPatchInfo(target)?.Owners.Any(o => o != Owner) == true)
                    throw new NotSupportedException("foreign channel item patch present");
                if (installed != target)
                {
                    Remove();
                    installed = target;
                    (patcher ??= new Harmony(Owner)).Patch(target, transpiler: new HarmonyMethod(typeof(NativeChannelItemRefresh).GetMethod(nameof(Transpile), BindingFlags.Static | BindingFlags.NonPublic)!));
                }
                Volatile.Write(ref enabled, 1);
                detail = Detail = "Active; metadata refresh skipped only for Douban channel items; other channels unchanged.";
                return true;
            }
            catch (Exception error)
            {
                detail = Detail = "Inactive: " + error.Message + "; native channel item refresh retained.";
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
            try { Remove(); Detail = "Disabled; native channel item refresh unchanged."; }
            catch { Detail = "Disabled; cleanup pending; refresh passthrough."; }
        }
    }
    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.Transpiler, Owner);
        installed = null;
    }

    /// <summary>Replaces the single native QueueRefresh call; disabled or non-Douban items call the host exactly as before.</summary>
    internal static void QueueRefresh(IProviderManager providerManager, long id, MetadataRefreshOptions options, RefreshPriority priority, ChannelItemInfo info)
    {
        // Only media items carry the douban:v2: prefix; the three root folders keep native refresh (cheap, like any channel folder).
        if (IsEnabled && DoubanChannelService.IsChannelItemId(info?.Id)) { Interlocked.Increment(ref skipped); return; }
        providerManager.QueueRefresh(id, options, priority);
    }

    internal static bool Verify(MethodInfo method)
    {
        var type = method.DeclaringType; var assembly = method.Module.Assembly;
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        return type is { IsPublic: true, IsSealed: true } && type.FullName == TypeName && typeof(IChannelManager).IsAssignableFrom(type) &&
            method.IsPrivate && !method.IsStatic && !method.ContainsGenericParameters && method.Name == "GetChannelItemEntity" && method.ReturnType == typeof(long) &&
            method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(ChannelItemInfo), typeof(IChannel), typeof(bool), typeof(long), typeof(BaseItem) }) &&
            assembly.GetName().Name == "Emby.Server.Implementations" && assembly.GetName().Version == new Version(4, 10, 2, 0) &&
            method.Module.ModuleVersionId == new Guid("2009e43d-fa42-4e57-a902-2784a8306072") && method.MetadataToken == 0x0600114B &&
            method.GetMethodImplementationFlags() == MethodImplAttributes.IL &&
            body is { InitLocals: true, MaxStackSize: 5, LocalSignatureMetadataToken: 0x110004CB } &&
            Convert.ToHexString(method.Module.ResolveSignature(body.LocalSignatureMetadataToken)) ==
                "07110A12846902128AED128AE9128B5D128BDD15118091010A15118091010815118091011181E11511809101081181E1128479128BDD1285051511810101128505128505" &&
            body.LocalVariables.Count == 17 && body.LocalVariables.All(v => !v.IsPinned) &&
            body.ExceptionHandlingClauses.Count == 1 && body.ExceptionHandlingClauses[0] is
                { Flags: ExceptionHandlingClauseOptions.Finally, TryOffset: 1086, TryLength: 65, HandlerOffset: 1151, HandlerLength: 14 } &&
            il?.Length == 1436 && Convert.ToHexString(SHA256.HashData(il)) == "C61BA4AB9D81084FB17B35039952A4C113049761DD6F315C53BB41802B290E81" &&
            !string.IsNullOrEmpty(assembly.Location) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) == AssemblyHash;
    }

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        if (__originalMethod is not MethodInfo method || !Verify(method)) throw new InvalidOperationException("Native channel item pin mismatch");
        var code = instructions.ToList();
        var native = typeof(IProviderManager).GetMethod(nameof(IProviderManager.QueueRefresh), new[] { typeof(long), typeof(MetadataRefreshOptions), typeof(RefreshPriority) })!;
        var calls = code.Select((c, i) => (c, i)).Where(x => (x.c.opcode == OpCodes.Callvirt || x.c.opcode == OpCodes.Call) && Equals(x.c.operand, native)).ToArray();
        if (calls.Length != 1) throw new InvalidOperationException("Expected exactly one native QueueRefresh call");
        // Keep the call instruction (labels/blocks) and pass ChannelItemInfo (arg 1) as the extra trailing argument.
        var call = calls[0].c;
        call.opcode = OpCodes.Call;
        call.operand = typeof(NativeChannelItemRefresh).GetMethod(nameof(QueueRefresh), BindingFlags.Static | BindingFlags.NonPublic)!;
        code.Insert(calls[0].i, new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(call));
        return code;
    }
}
