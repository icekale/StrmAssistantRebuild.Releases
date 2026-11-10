using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Common.Net;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

/// <summary>Only the pinned native manager's future factory products; existing clients and response streams stay native-owned.</summary>
public static class NativeHttpRouting
{
    private const string Owner = "StrmAssistantRebuild.NativeHttpRouting.4.10.2";
    private const string HostName = "Emby.Server.Implementations";
    private const string HostType = HostName + ".ApplicationHost";
    private const string ManagerType = HostName + ".HttpClientManager.CoreHttpClientManager";
    private static readonly Version SupportedVersion = new(4, 10, 2, 0);
    private static readonly Guid Mvid = new("2009e43d-fa42-4e57-a902-2784a8306072");
    private static readonly object Gate = new();
    private sealed record State(object Target, MethodInfo Factory, MethodInfo Context, ProxyRouting? Proxy, bool Decompress);
    private static State? active;
    private static Harmony? patcher;
    private static MethodInfo? installed;
    private static long appliedNewHandlerCount;
    public static long AppliedNewHandlerCount => Interlocked.Read(ref appliedNewHandlerCount);
    public static bool IsEnabled => Volatile.Read(ref active) != null;
    public static bool FutureOnly => true;
    // No cache enumeration/eviction or active-response drain assertion. A controlled host restart establishes a new baseline.
    public static bool RestartPending { get; private set; }
    public static string Detail { get; private set; } = "Inactive; native clients unchanged.";
    private const string Lifetime = " FutureOnly: existing cached/explicit connection contexts and streams retain their original settings. Drain outstanding responses and restart the host for uniform settings; no clients are evicted here.";

    /// <summary>Call after NetworkService.Configure/check has normalized LastProxyCheckSucceeded. No network probe is run.</summary>
    public static bool Configure(Version hostVersion, IHttpClient nativeClient, NetworkOptions options, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            RestartPending = true; // Startup may already have created native contexts before plugin construction.
            try
            {
                Remove();
                ProxyRouting? proxy = null;
                var url = (options.ProxyServerUrl ?? "").Trim();
                if (options.EnableProxyServer && url.Length > 0)
                {
                    if (!ProxyRouting.TryCreate(url, options.ProxyMode, out var parsed))
                    { detail = Detail = "Inactive: invalid absolute HTTP(S) proxy endpoint or mode." + Lifetime; return false; }
                    if (!parsed!.TransportSupported)
                    { detail = Detail = NetworkService.HttpsProxyGap + Lifetime; return false; }
                    if (options.LastProxyCheckSucceeded) proxy = parsed;
                }
                if (proxy == null && !options.EnableAutomaticDecompression)
                { detail = Detail = "Inactive for future clients: native proxy and per-request decompression retained." + Lifetime; return true; }
                var manager = nativeClient.GetType();
                var factoryField = manager.GetField("_httpClientHandlerFactory", BindingFlags.Instance | BindingFlags.NonPublic);
                var factoryDelegate = factoryField?.GetValue(nativeClient) as Delegate;
                var context = manager.GetMethod("GetConnectionContextInternal", BindingFlags.Instance | BindingFlags.NonPublic);
                if (hostVersion != SupportedVersion || manager.FullName != ManagerType || !manager.IsSealed ||
                    factoryField is not { IsPrivate: true, IsInitOnly: true } || factoryDelegate?.GetInvocationList().Length != 1 ||
                    factoryDelegate.Target == null || factoryDelegate.Method is not MethodInfo factory || context == null ||
                    !Verify(factory, context) || !factory.DeclaringType!.IsInstanceOfType(factoryDelegate.Target) ||
                    factoryDelegate.Target.GetType().GetMethod(factory.Name, BindingFlags.Instance | BindingFlags.NonPublic)?.MethodHandle != factory.MethodHandle)
                { detail = Detail = "Inactive: native manager/factory delegate, version, file, MVID or IL contract differs." + Lifetime; return false; }
                var next = new State(factoryDelegate.Target, factory, context, proxy, options.EnableAutomaticDecompression);
                if (ForeignOwner(next))
                { detail = Detail = "Inactive: foreign native factory/context patch owner." + Lifetime; return false; }
                installed = factory;
                (patcher ??= new Harmony(Owner)).Patch(factory, postfix: new HarmonyMethod(typeof(NativeHttpRouting).GetMethod(nameof(AfterFactory), BindingFlags.Static | BindingFlags.NonPublic)!));
                Volatile.Write(ref active, next);
                detail = Detail = "Configured native factory only: " + (proxy == null ? "native/default proxy retained" : "checked proxy with shared private-subnet/whitelist routing") +
                    (next.Decompress ? "; new handlers use GZip/Deflate/Brotli." : "; native per-request decompression retained.") + Lifetime;
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref active, null);
                Detail = "Inactive: native factory setup failed (" + error.GetType().Name + ")." + Lifetime;
                try { Remove(); } catch { Detail += " Owned detour cleanup pending; callback inactive."; }
                detail = Detail;
                return false;
            }
        }
    }

    private static void AfterFactory(object __instance, HttpMessageHandler __result)
    {
        var state = Volatile.Read(ref active);
        if (state == null || !ReferenceEquals(state.Target, __instance)) return;
        try
        {
            if (ForeignOwner(state) || __result.GetType() != typeof(SocketsHttpHandler))
            { Volatile.Write(ref active, null); Detail = "Inactive: native factory result/ownership changed." + Lifetime; return; }
            // Pinned IL creates this exact handler afresh and never sends it. Never unwrap or mutate a shared/used handler.
            var handler = (SocketsHttpHandler)__result;
            if (state.Proxy != null) { handler.Proxy = state.Proxy; handler.UseProxy = true; }
            // Original v57 true installs All; false installs no decompression override (not None).
            if (state.Decompress) handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli;
            Interlocked.Increment(ref appliedNewHandlerCount);
        }
        catch (Exception error)
        {
            Volatile.Write(ref active, null);
            Detail = "Inactive: native factory application failed (" + error.GetType().Name + ")." + Lifetime;
        }
    }

    private static bool ForeignOwner(State state) => new[] { state.Factory, state.Context }
        .Any(method => Harmony.GetPatchInfo(method)?.Owners.Any(owner => owner != Owner) == true);

    internal static bool Verify(MethodInfo factory, MethodInfo context)
    {
        var assembly = factory.Module.Assembly;
        if (assembly != context.Module.Assembly || assembly.GetName().Name != HostName || assembly.GetName().Version != SupportedVersion ||
            factory.Module.ModuleVersionId != Mvid || string.IsNullOrEmpty(assembly.Location)) return false;
        using var file = File.OpenRead(assembly.Location);
        using var sha = SHA256.Create();
        if (Convert.ToHexString(sha.ComputeHash(file)) != "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F") return false;
        var fbody = factory.GetMethodBody(); var cbody = context.GetMethodBody();
        if (factory.DeclaringType?.FullName != HostType || factory.Name != "CreateHttpClientHandler" || !factory.IsFamily || !factory.IsVirtual || factory.IsStatic ||
            factory.ContainsGenericParameters || factory.ReturnType != typeof(HttpMessageHandler) || factory.GetParameters().Length != 1 ||
            factory.GetParameters()[0].ParameterType.FullName != HostName + ".HttpClientManager.HttpMessageHandlerOptions" ||
            context.DeclaringType?.FullName != ManagerType || context.Name != "GetConnectionContextInternal" || !context.IsFamily || !context.IsVirtual || context.IsStatic ||
            context.ReturnType.FullName != HostName + ".HttpClientManager.BaseHttpClientInfo" || context.GetParameters().Length != 1 ||
            context.GetParameters()[0].ParameterType != typeof(MediaBrowser.Common.Net.HttpRequestOptions) ||
            !Match(factory, 0x06000447, 148, 3, 0x1100009A, "07041283951511809101118399151180910108151180910102", "3CE87491EFC6443FB11B5F00A6FCE7C94260F04106DAC178AFEEBDDB6651EC1E") ||
            !Match(context, 0x06000E81, 418, 5, 0x110003AC, "070B12864C0E0E0E02081511809101118A81118A811286501283911D0E", "D063C75DC69C93E97FDACF8993965BDBAB05B96EAB2A6B2D46C0FB0D82F7B3A5") ||
            !fbody!.LocalVariables.Select(v => v.LocalType).SequenceEqual(new[] { typeof(SocketsHttpHandler), typeof(DecompressionMethods?), typeof(int?), typeof(bool?) }) ||
            fbody.ExceptionHandlingClauses.Count != 1 || cbody!.ExceptionHandlingClauses.Count != 0) return false;
        var eh = fbody.ExceptionHandlingClauses[0];
        return eh.Flags == ExceptionHandlingClauseOptions.Clause && eh.CatchType == typeof(NotImplementedException) &&
            eh.TryOffset == 65 && eh.TryLength == 22 && eh.HandlerOffset == 87 && eh.HandlerLength == 3;
    }

    private static bool Match(MethodInfo method, int token, int length, int stack, int signature, string sigHex, string hash)
    {
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        return method.MetadataToken == token && method.GetMethodImplementationFlags() == MethodImplAttributes.IL &&
            body is { InitLocals: true } && body.MaxStackSize == stack && body.LocalSignatureMetadataToken == signature &&
            Convert.ToHexString(method.Module.ResolveSignature(signature)) == sigHex && body.LocalVariables.All(v => !v.IsPinned) &&
            il?.Length == length && Convert.ToHexString(SHA256.HashData(il)) == hash;
    }

    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try { Remove(); Detail = "Inactive for future clients: native factory restored." + Lifetime; }
            catch { Detail = "Inactive callback; owned detour cleanup pending." + Lifetime; }
        }
    }
    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.Postfix, Owner);
        installed = null;
    }
}
