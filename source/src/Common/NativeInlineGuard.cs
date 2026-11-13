using System.Reflection;
using HarmonyLib;

namespace StrmAssistantRebuild.Common;

/// <summary>
/// M165: exclusive-mode patches are installed only when the mode is switched on, possibly long after Emby started. All of
/// their pinned 4.10.2 targets are small methods without exception handling, so .NET 8 tier-1 recompilation (with dynamic PGO
/// guarded devirtualization for interface calls) may have already inlined the original bodies into hot host callers; such
/// callers never reach the later Harmony detour until Emby restarts. Observed on the M164 host: after a few library refreshes
/// with exclusive mode off, every manual refresh bypassed the RefreshSingleItem prefix (native refresh entries=0/0), so native
/// ffprobe lost its refresh context. At plugin construction (before the host's refresh callers become hot) this marks the
/// same fingerprint-verified targets as not inlinable, without patching them, so a later detour is always reached.
/// </summary>
public static class NativeInlineGuard
{
    private static readonly object Gate = new();
    private static readonly HashSet<MethodBase> Guarded = new();
    private static Func<MethodBase, bool>? disable;
    public static int Count { get { lock (Gate) return Guarded.Count; } }
    public static string Detail { get; private set; } = "Not applied.";

    /// <summary>Exact expected target count on the pinned 4.10.2 host: saver 2, remote fetcher 2, chapter images 1, Movie ffprobe 1, extraction 8.</summary>
    public const int Expected = 14;

    public static void Apply(Version hostVersion)
    {
        lock (Gate)
        {
            try
            {
                if (hostVersion != new Version(4, 10, 2, 0)) { Detail = "Inactive: requires exact host 4.10.2.0."; return; }
                var targets = Targets(out var missing);
                if (Disabler() is null) { Detail = "Inactive: runtime cannot disable inlining."; return; }
                var failed = 0;
                foreach (var target in targets)
                {
                    if (Guarded.Contains(target)) continue;
                    if (TryDisable(target)) Guarded.Add(target); else failed++;
                }
                Detail = $"{Guarded.Count}/{Expected} exclusive-mode targets not inlinable" + (missing.Count > 0 ? "; unverified: " + string.Join(",", missing) : "") +
                    (failed > 0 ? "; failed=" + failed : "") + ".";
            }
            catch (Exception error) { Detail = "Inactive: " + error.GetType().Name + ": " + error.Message; }
        }
    }

    /// <summary>The same targets the exclusive controls patch, each with the control's own pin check.</summary>
    internal static List<MethodInfo> Targets(out List<string> missing)
    {
        var absent = missing = new List<string>();
        var result = new List<MethodInfo>();
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var providers = assemblies.SingleOrDefault(a => a.GetName().Name == "Emby.Providers");
        var encoding = assemblies.SingleOrDefault(a => a.GetName().Name == "Emby.Server.MediaEncoding");
        void Add(string name, Func<IEnumerable<MethodInfo>> source, Func<MethodInfo, bool> verify)
        {
            try
            {
                var found = source().ToArray();
                if (found.Length == 0 || found.Any(m => m == null || !verify(m))) { absent.Add(name); return; }
                result.AddRange(found);
            }
            catch (Exception) { absent.Add(name); }
        }
        if (providers == null) missing.Add("Emby.Providers");
        else
        {
            Add("saver", () => ExclusiveSaverControl.GetTargets(providers).Take(2), ExclusiveSaverControl.Verify);
            Add("remote", () => ExclusiveRemoteFetcherControl.GetTargets(providers), ExclusiveRemoteFetcherControl.Verify);
            Add("chapter", () => ExclusiveChapterImageControl.Target(providers) is { } m ? new[] { m } : Array.Empty<MethodInfo>(), ExclusiveChapterImageControl.Verify);
            Add("ffprobe", () => NativeChangedProbeControl.Target(providers) is { } m ? new[] { m } : Array.Empty<MethodInfo>(), NativeChangedProbeControl.VerifyTarget);
        }
        if (encoding == null) missing.Add("Emby.Server.MediaEncoding");
        else Add("extraction", () => ExclusiveMediaControl.GetTargets(encoding), m =>
        {
            var body = m.GetMethodBody();
            return body != null && ExclusiveMediaControl.VerifyFingerprint(m, body.GetILAsByteArray(), body.MaxStackSize, body.InitLocals,
                body.LocalSignatureMetadataToken, body.ExceptionHandlingClauses.Count);
        });
        return result;
    }

    /// <summary>The Harmony 2.4.2 build merged into this DLL carries MonoMod.Core; its platform triple sets the runtime's
    /// "not inline" method flag (the same step it takes for every detoured method). Bound by reflection because the merge
    /// internalizes the type.</summary>
    /// <summary>Marks one method not inlinable (not recorded in <see cref="Count"/>); false if the runtime cannot.</summary>
    internal static bool TryDisable(MethodBase method) => Disabler() is { } call && call(method);

    private static Func<MethodBase, bool>? Disabler()
    {
        if (disable != null) return disable;
        var type = typeof(Harmony).Assembly.GetType("MonoMod.Core.Platforms.PlatformTriple", false);
        var current = type?.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        var method = type?.GetMethod("TryDisableInlining", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(MethodBase) }, null);
        if (current == null || method == null || method.ReturnType != typeof(bool)) return null;
        return disable = target => (bool)method.Invoke(current, new object[] { target })!;
    }
}
