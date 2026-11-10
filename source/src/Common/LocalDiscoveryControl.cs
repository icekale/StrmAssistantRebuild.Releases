using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.System;

namespace StrmAssistantRebuild.Common;

/// <summary>Exact 4.10.2 discovery payload and authenticated local-address projection; no network configuration writes.</summary>
public static class LocalDiscoveryControl
{
    private const string Owner = "StrmAssistantRebuild.LocalDiscovery.4.10.2";
    private const string HostName = "Emby.Server.Implementations";
    private const string HostType = HostName + ".ApplicationHost";
    private const string UdpType = HostName + ".Udp.UdpServer";
    private static readonly Version SupportedVersion = new(4, 10, 2, 0);
    private static readonly Guid Mvid = new("2009e43d-fa42-4e57-a902-2784a8306072");
    private static readonly object Gate = new();
    private sealed record Contract(MethodInfo Send, MethodInfo Info, MethodInfo Move);
    private sealed record State(Contract Contract, string Url);
    private static State? active;
    private static Harmony? patcher;
    private static Contract? installed;
    public static bool IsEnabled => Volatile.Read(ref active) != null;
    public static string Detail { get; private set; } = "Disabled; native LAN discovery and system information unchanged.";

    public static bool TryNormalizeUrl(string? raw, out string normalized, out string? error) =>
        NetworkEndpointRules.TryNormalizeUrl(raw, out normalized, out error);

    public static bool Configure(Version hostVersion, string? url, ILogger? logger)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try
            {
                Remove();
                if (!TryNormalizeUrl(url, out var normalized, out var error))
                { Detail = "Inactive: " + error; return false; }
                if (normalized.Length == 0)
                { Detail = "Disabled; native LAN discovery and system information restored."; return true; }
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == HostName);
                var send = assembly?.GetType(UdpType)?.GetMethod("SendMessage", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(string), typeof(IPEndPoint), typeof(Encoding) }, null);
                var info = assembly?.GetType(HostType)?.GetMethod("GetPublicSystemInfoInternal", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(IPAddress), typeof(IPAddress), typeof(bool), typeof(bool), typeof(CancellationToken) }, null);
                var move = assembly?.GetType(HostType + "+<GetPublicSystemInfoInternal>d__447")?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic);
                if (hostVersion != SupportedVersion || send == null || info == null || move == null || !Verify(send, info, move))
                { Detail = "Inactive: discovery host version, file, MVID, method IL, locals or exception contract differs; native behavior retained."; return false; }
                var contract = new Contract(send, info, move);
                if (ForeignOwner(contract))
                { Detail = "Inactive: foreign discovery/system-info patch; native behavior retained."; return false; }
                installed = contract; // Keep both cleanup handles even if installation fails partway through.
                patcher ??= new Harmony(Owner);
                patcher.Patch(send, prefix: new HarmonyMethod(typeof(LocalDiscoveryControl), nameof(BeforeSend)));
                patcher.Patch(move, transpiler: new HarmonyMethod(typeof(LocalDiscoveryControl), nameof(ProjectLocalAddresses)));
                Volatile.Write(ref active, new State(contract, normalized));
                Detail = "Enabled: native UDP discovery Address and authenticated nonempty LocalAddresses[0] use the configured authority; remote addresses and server settings unchanged.";
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref active, null);
                Detail = "Inactive: discovery hook setup failed (" + error.GetType().Name + "); native behavior retained.";
                try { Remove(); } catch { Detail += " Owned hook cleanup pending; projection inactive."; }
                try { logger?.Warn(Detail); } catch { /* Diagnostic sinks do not change configuration semantics. */ }
                return false;
            }
        }
    }

    private static State? Current()
    {
        var state = Volatile.Read(ref active);
        if (state == null) return null;
        try { if (!ForeignOwner(state.Contract)) return state; } catch { }
        // A late foreign patch deactivates both projections together, without replacing the native operation.
        Interlocked.CompareExchange(ref active, null, state);
        Detail = "Inactive: discovery/system-info ownership changed; native behavior retained.";
        return null;
    }

    private static void BeforeSend(ref string localUrl)
    {
        var state = Current();
        if (state != null) localUrl = state.Url;
    }

    internal static string[] RewriteAddresses(string[] addresses)
    {
        var state = Current();
        if (state == null || addresses == null || addresses.Length == 0 || string.IsNullOrEmpty(addresses[0])) return addresses!;
        var result = (string[])addresses.Clone();
        result[0] = state.Url;
        return result;
    }

    private static IEnumerable<CodeInstruction> ProjectLocalAddresses(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var setter = typeof(PublicSystemInfo).GetProperty(nameof(PublicSystemInfo.LocalAddresses))!.SetMethod!;
        var matches = code.Where(c => c.Calls(setter)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Pinned LocalAddresses setter count differs.");
        var helper = typeof(LocalDiscoveryControl).GetMethod(nameof(RewriteAddresses), BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var instruction in code)
        {
            if (instruction == matches[0])
            {
                // Insert inside the original authenticated/nonempty branch, before its single setter.
                // No Task wrapper, blocking Result, result-object replacement, catch or finally changes.
                var projection = new CodeInstruction(OpCodes.Call, helper);
                projection.labels.AddRange(instruction.labels); instruction.labels.Clear();
                projection.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                yield return projection;
            }
            yield return instruction;
        }
    }

    private static bool ForeignOwner(Contract c) => new[] { c.Send, c.Info, c.Move }
        .Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(owner => owner != Owner) == true);

    internal static bool Verify(MethodInfo send, MethodInfo info, MethodInfo move)
    {
        var assembly = send.Module.Assembly;
        if (assembly != info.Module.Assembly || assembly != move.Module.Assembly || assembly.GetName().Name != HostName ||
            assembly.GetName().Version != SupportedVersion || send.Module.ModuleVersionId != Mvid || string.IsNullOrEmpty(assembly.Location)) return false;
        using var file = File.OpenRead(assembly.Location);
        using var sha = SHA256.Create();
        if (Convert.ToHexString(sha.ComputeHash(file)) != "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F") return false;
        if (send.DeclaringType is not { IsPublic: true, IsSealed: true } || send.DeclaringType.FullName != UdpType ||
            !OrdinaryPrivate(send, "SendMessage", typeof(void), typeof(string), typeof(IPEndPoint), typeof(Encoding)) ||
            info.DeclaringType is not { IsPublic: true, IsAbstract: true } || info.DeclaringType.FullName != HostType ||
            !OrdinaryPrivate(info, "GetPublicSystemInfoInternal", typeof(Task<PublicSystemInfo>), typeof(IPAddress), typeof(IPAddress), typeof(bool), typeof(bool), typeof(CancellationToken)) ||
            move.DeclaringType?.FullName != HostType + "+<GetPublicSystemInfoInternal>d__447" || !move.DeclaringType.IsValueType ||
            info.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType != move.DeclaringType ||
            !typeof(IAsyncStateMachine).IsAssignableFrom(move.DeclaringType) || move.Name != "MoveNext" || !move.IsPrivate || !move.IsFinal || !move.IsVirtual ||
            move.IsStatic || move.ContainsGenericParameters || move.ReturnType != typeof(void) || move.GetParameters().Length != 0) return false;
        return Match(send, 0x06000535, 163, 7, 0x110000DD, "070412845D1284590E1D05", 4,
                "8CE9BB1C1D992C5BD0D80209D35A4586BF2CD4F8A7F529F5366419553E143629", false) &&
            Match(info, 0x06000483, 89, 2, 0x110000B1, "07011187C8", 1,
                "C1B3B3AD7F3270CE4A3E5371EC88E301210CA26C1E5AEF78C2B40B2454AB3240", false) &&
            Match(move, 0x06001252, 365, 5, 0x110004E9, "0707081283E81283E90E15118215010E15118211010E1280C5", 7,
                "F3B3EB526A6E4300CA35AC56018BB8871008BDD5E7780A2C34F46607C7D0717B", true);
    }

    private static bool OrdinaryPrivate(MethodInfo method, string name, Type result, params Type[] parameters) =>
        method.Name == name && method.IsPrivate && !method.IsStatic && !method.IsVirtual && !method.ContainsGenericParameters &&
        method.ReturnType == result && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters);

    internal static bool Match(MethodInfo method, int token, int length, int stack, int signature, string signatureHex, int locals, string hash, bool asyncCatch)
    {
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        if (method.MetadataToken != token || method.GetMethodImplementationFlags() != MethodImplAttributes.IL ||
            body is not { InitLocals: true } || body.MaxStackSize != stack || body.LocalSignatureMetadataToken != signature ||
            Convert.ToHexString(method.Module.ResolveSignature(signature)) != signatureHex || body.LocalVariables.Count != locals ||
            body.LocalVariables.Any(v => v.IsPinned) || il?.Length != length || Convert.ToHexString(SHA256.HashData(il)) != hash) return false;
        if (!asyncCatch) return body.ExceptionHandlingClauses.Count == 0;
        if (body.ExceptionHandlingClauses.Count != 1) return false;
        var region = body.ExceptionHandlingClauses[0];
        return region.Flags == ExceptionHandlingClauseOptions.Clause && region.CatchType == typeof(Exception) &&
            region.TryOffset == 14 && region.TryLength == 291 && region.HandlerOffset == 305 && region.HandlerLength == 32;
    }

    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try { Remove(); Detail = "Disabled; native LAN discovery and system information restored."; }
            catch { Detail = "Disabled; owned hook cleanup pending; projection inactive."; }
        }
    }

    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed.Send, HarmonyPatchType.Prefix, Owner);
        patcher?.Unpatch(installed.Move, HarmonyPatchType.Transpiler, Owner);
        installed = null;
    }
}
