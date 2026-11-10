using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;

/// <summary>Changes only the native per-folder count threshold; all naming/grouping remains host code.</summary>
public static class NativeFolderGrouping
{
    private const string Owner = "StrmAssistantRebuild.NativeFolderGrouping.4.10.2";
    private const string Resolver = "Emby.Naming.Video.VideoListResolver";
    internal const string AssemblyHash = "3F1AD12275ACB779E419DA735832D8E34BF07F6E238A40DEDB4220292524D111";
    private static readonly object Gate = new();
    private static readonly Version SupportedVersion = new(4, 10, 2, 0);
    private static Harmony? patcher;
    private static MethodInfo? installed;
    private static int configuredLimit = 8;
    public static bool IsSupported { get; private set; }
    public static string Detail { get; private set; } = "Disabled; native folder limit 8 unchanged.";

    // Original Experience consumer gates on limit != 8, NOT MergeMultiVersion.
    public static bool Configure(Version hostVersion, int limit, ILogger logger)
    {
        lock (Gate)
        {
            IsSupported = false;
            try
            {
                if (hostVersion != SupportedVersion)
                    throw new NotSupportedException("host version differs from 4.10.2.0");
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.Naming")
                    ?? Assembly.Load(new AssemblyName("Emby.Naming"));
                var target = assembly.GetType(Resolver)?.GetMethod("GetVideosGroupedByVersion", BindingFlags.Instance | BindingFlags.NonPublic);
                if (target == null || !VerifyTarget(target))
                    throw new NotSupportedException("assembly bytes, signature, MVID, token, IL, locals or EH differ");
                if (Harmony.GetPatchInfo(target)?.Owners.Any(o => o != Owner) == true)
                    throw new NotSupportedException("foreign resolver patch present");
                var effective = Math.Clamp(limit, 1, 127);
                if (effective == 8)
                {
                    Volatile.Write(ref configuredLimit, 8);
                    Remove();
                    Detail = "Supported, OFF; owned transpiler removed, native folder limit 8 restored.";
                }
                else
                {
                    if (installed != target)
                    {
                        Remove();
                        installed = target; // Keep cleanup handle even if installation fails partway through.
                        (patcher ??= new Harmony(Owner)).Patch(target, transpiler: new HarmonyMethod(
                            typeof(NativeFolderGrouping).GetMethod(nameof(Transpile), BindingFlags.Static | BindingFlags.NonPublic)!));
                    }
                    Volatile.Write(ref configuredLimit, effective);
                    Detail = $"Active; exact native folder count threshold 8 -> {effective}; naming/extras/stacks/year/prefix/order unchanged.";
                }
                IsSupported = true;
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref configuredLimit, 8);
                Detail = "Inactive: " + error.GetType().Name + ": " + error.Message + "; threshold remains native 8.";
                try { Remove(); } catch { Detail += " Owned detour cleanup pending; dynamic threshold is 8."; }
                return false;
            }
            finally
            {
                // Logging must not turn a completed OFF/rollback into a configuration exception.
                try { logger?.Info("[StrmAssistantRebuild] NativeFolderGrouping: {0}", Detail); } catch { }
            }
        }
    }

    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref configuredLimit, 8);
            IsSupported = false;
            try { Remove(); Detail = "Disabled; owned transpiler removed, native folder limit 8 restored."; }
            catch { Detail = "Disabled; owned detour cleanup pending; dynamic threshold is 8."; }
        }
    }

    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.Transpiler, Owner);
        installed = null;
    }

    private static int ReadLimit() => Volatile.Read(ref configuredLimit);

    internal static bool VerifyTarget(MethodInfo method)
    {
        var type = method.DeclaringType; var assembly = method.Module.Assembly;
        var video = assembly.GetType("Emby.Naming.Video.VideoInfo");
        var resolver = assembly.GetType("Emby.Naming.Video.VideoResolver");
        if (video == null || resolver == null) return false;
        var list = typeof(List<>).MakeGenericType(video);
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        return type is { IsPublic: true, IsSealed: true } && type.FullName == Resolver && method.IsPrivate && !method.IsStatic &&
            !method.ContainsGenericParameters && method.Name == "GetVideosGroupedByVersion" && method.ReturnType == list &&
            method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { list, resolver, typeof(bool), typeof(bool) }) &&
            assembly.GetName().Name == "Emby.Naming" && assembly.GetName().Version == SupportedVersion &&
            method.Module.ModuleVersionId == new Guid("6fb2d67b-ed33-46c8-8325-f935b2958294") && method.MetadataToken == 0x06000066 &&
            body is { InitLocals: true, MaxStackSize: 5, LocalSignatureMetadataToken: 0x11000010 } &&
            il?.Length == 456 && Convert.ToHexString(SHA256.HashData(il)) == "026C34F342C34A94EC87F432AFB224438D4C512F175CC2DC6A94D6C22A19601D" &&
            Convert.ToHexString(method.Module.ResolveSignature(body.LocalSignatureMetadataToken)) == "070A0E021511610112481248120C15114901081D124812480E15125D011248" &&
            body.LocalVariables.Count == 10 && body.LocalVariables.All(v => !v.IsPinned) &&
            body.LocalVariables.Select(v => v.LocalType).SequenceEqual(new[] { typeof(string), typeof(bool), list.GetNestedType("Enumerator")!.MakeGenericType(video),
                video, assembly.GetType("Emby.Naming.Video.CleanDateTimeResult")!, typeof(int?), video.MakeArrayType(), video, typeof(string), list }) &&
            body.ExceptionHandlingClauses.Count == 1 && body.ExceptionHandlingClauses[0] is
                { Flags: ExceptionHandlingClauseOptions.Finally, TryOffset: 76, TryLength: 50, HandlerOffset: 126, HandlerLength: 14 } &&
            !string.IsNullOrEmpty(assembly.Location) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) == AssemblyHash;
    }

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        if (__originalMethod is not MethodInfo method || !VerifyTarget(method))
            throw new InvalidOperationException("Native folder resolver pin mismatch");
        var code = instructions.ToList();
        var countGetter = method.GetParameters()[0].ParameterType.GetProperty("Count")!.GetMethod!;
        var anchors = Enumerable.Range(2, Math.Max(0, code.Count - 3)).Where(i =>
            code[i - 2].opcode == OpCodes.Ldarg_1 && code[i - 1].opcode == OpCodes.Callvirt && Equals(code[i - 1].operand, countGetter) &&
            code[i].opcode == OpCodes.Ldc_I4_8 && code[i + 1].opcode == OpCodes.Bgt && code[i + 1].operand is Label).ToArray();
        if (anchors.Length != 1 || code.Count(c => c.opcode == OpCodes.Ldc_I4_8) != 1)
            throw new InvalidOperationException("Expected exactly one videos.Count / ldc.i4.8 / bgt anchor");
        // Retain the same instruction's labels/EH markers; no prefix, result replacement, sorting or reimplementation.
        var constant = code[anchors[0]];
        constant.opcode = OpCodes.Call;
        constant.operand = typeof(NativeFolderGrouping).GetMethod(nameof(ReadLimit), BindingFlags.Static | BindingFlags.NonPublic)!;
        return code;
    }
}
