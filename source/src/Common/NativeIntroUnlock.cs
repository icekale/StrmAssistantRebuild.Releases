using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace StrmAssistantRebuild.Common;

/// <summary>
/// M169: the original UnlockIntroSkip class (notes/decrypted/A----------/AA---------.cs:14960-15552) for Emby's own intro
/// detection (the "Detect Episode Intros" task and library scans), active while UnlockIntroSkip is on:
///  - prefix on AudioFingerprintManager.IsIntroDetectionSupported(Episode, LibraryOptions): a .strm episode is remembered in
///    the current flow (m_c0234241430b7f2407285172) and BaseItem.IsShortcut reports false for exactly that episode, so the
///    native eligibility check accepts it;
///  - postfix on MarkerScheduledTask.CreateQueryForEpisodeIntroDetection (m_d53afbb9c7f58efdf26918c2): MarkerEnabledLibraryScope
///    "-1" restricts to seasons of favorites; other scopes restrict to the selected libraries' paths and exclude seasons matched
///    by FingerprintExcludeKeywords; specials (season 0) are always skipped;
///  - postfix on SequenceDetection.DetectSequences(season variant) and prefix on AudioFingerprintManager.OnFailedToFindIntro
///    (m_3b589feb13637ce2f48cb421 / m_44967b553dd4b00d9f9be0a9): a failed detection deletes the intro markers but is logged as
///    a native detection failure (which stops retries) only when the detection confidence was zero.
/// Rebuild differences: exclusion keywords use the rebuild's path matching (as its own fingerprint task); the original's two
/// IL rewrites of UpdateSequencesForSeason and the item extradata clearing are not reproduced.
/// </summary>
public static class NativeIntroUnlock
{
    private const string Owner = "StrmAssistantRebuild.NativeIntroUnlock.4.10.2";
    private const string ProvidersHash = "16874EA37E2EC8152E7F2FA9F9D7FA51A284959DCE3A918694003D009B2B2A94";
    private const string ControllerHash = "EAA6B9EDEBB21FE3AF9C272DEF8C913D9EC6B177B552E1B789FEE15CB59289DA";
    private static readonly Guid ProvidersMvid = new("29b37ec2-5f02-443c-a995-1f61c71f43c6");
    private static readonly Guid ControllerMvid = new("58dae430-7295-47bb-a62f-b26bf0d89604");
    private static readonly object Gate = new();
    private static readonly AsyncLocal<long> AllowedShortcut = new();
    private static readonly AsyncLocal<bool> ZeroConfidence = new();
    private static readonly List<MethodBase> Installed = new();
    private static Harmony? patcher;
    private static PropertyInfo? confidence;
    private static Func<IntroSkipOptionsView>? options;
    private static ILibraryManager? library;
    private static volatile bool enabled;
    private static long unlocked, scoped, failuresKept, failuresLogged;
    public static bool IsEnabled => enabled;
    public static string Detail { get; private set; } = "Disabled; native intro detection unchanged.";
    public static string Counters => $"STRM episodes unlocked={Interlocked.Read(ref unlocked)}; scoped queries={Interlocked.Read(ref scoped)}; " +
        $"failures kept for retry={Interlocked.Read(ref failuresKept)}; zero-confidence failures logged={Interlocked.Read(ref failuresLogged)}";

    /// <summary>The settings the query postfix needs, read at call time.</summary>
    public sealed record IntroSkipOptionsView(string? MarkerEnabledLibraryScope, string? FingerprintExcludeKeywords);

    internal sealed record Target(string Assembly, int Token, int Length, string Hash, string Name);
    internal static readonly Target[] Targets =
    {
        new("Emby.Providers", 0x06000188, 146, "A693BAD7FDC62A1FD66AAFCFFCB41EF233277554F0F0EEAEF8FA76E354C5C1D0", "IsIntroDetectionSupported"),
        new("MediaBrowser.Controller", 0x06000CDC, 12, "21942918F7172EB5EB3212B8FE193DA6EA3D04154276D4F348ED493DEB81AFE2", "get_IsShortcut"),
        new("Emby.Providers", 0x06000198, 146, "184656ADB90F28C2DAF9DCEBE9250EA8E8BBE44C7792834A5603C2254A817DAD", "CreateQueryForEpisodeIntroDetection"),
        new("Emby.Providers", 0x060001A2, 727, "0E051164A58A27AA13571C34650C3C39C34995C0622A3BE98C0CCF4E846287B3", "DetectSequences"),
        new("Emby.Providers", 0x0600017E, 61, "96922D1B9EDCD6F7D17576A1BA8654DC6B0517BE6A17676BAEBB2F7150C5FDDB", "OnFailedToFindIntro"),
    };
    private static readonly string[] Hooks = { nameof(SupportPrefix), nameof(ShortcutPrefix), nameof(QueryPostfix), nameof(DetectPostfix), nameof(FailedPrefix) };

    public static bool Configure(Version hostVersion, bool unlock, ILibraryManager? libraryManager, Func<IntroSkipOptionsView>? settings, out string detail)
    {
        lock (Gate)
        {
            enabled = false;
            try
            {
                if (!unlock) { Remove(); detail = Detail = "Disabled; native intro detection unchanged."; return true; }
                if (hostVersion != new Version(4, 10, 2, 0) || libraryManager == null || settings == null)
                    throw new NotSupportedException("requires exact host 4.10.2.0 and the library manager");
                var methods = Resolve();
                if (methods.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(o => o != Owner) == true))
                    throw new NotSupportedException("foreign patch on a native intro detection method");
                library = libraryManager; options = settings;
                if (Installed.Count != methods.Length || !Installed.SequenceEqual(methods))
                {
                    Remove();
                    patcher ??= new Harmony(Owner);
                    for (var i = 0; i < methods.Length; i++)
                    {
                        Installed.Add(methods[i]);
                        var hook = new HarmonyMethod(typeof(NativeIntroUnlock).GetMethod(Hooks[i], BindingFlags.Static | BindingFlags.NonPublic)!);
                        if (Hooks[i].EndsWith("Postfix", StringComparison.Ordinal)) patcher.Patch(methods[i], postfix: hook); else patcher.Patch(methods[i], prefix: hook);
                    }
                }
                enabled = true;
                detail = Detail = "Enabled: native intro detection accepts .strm episodes (IsIntroDetectionSupported/IsShortcut), scoped native query, zero-confidence failure logging.";
                return true;
            }
            catch (Exception error)
            {
                try { Remove(); } catch { /* keep the detail below */ }
                detail = Detail = "Inactive: " + error.GetType().Name + ": " + error.Message;
                return false;
            }
        }
    }

    public static void Disable() { lock (Gate) { enabled = false; try { Remove(); } catch { } Detail = "Disabled; native intro detection unchanged."; } }

    private static void Remove()
    {
        for (var i = Installed.Count - 1; i >= 0; i--) { patcher?.Unpatch(Installed[i], HarmonyPatchType.All, Owner); Installed.RemoveAt(i); }
    }

    /// <summary>The five pinned targets (file digest, MVID, token, IL length and digest).</summary>
    internal static MethodBase[] Resolve()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var result = new MethodBase[Targets.Length];
        for (var i = 0; i < Targets.Length; i++)
        {
            var t = Targets[i];
            var assembly = assemblies.SingleOrDefault(a => a.GetName().Name == t.Assembly) ?? throw new NotSupportedException(t.Assembly + " not loaded");
            var mvid = t.Assembly == "Emby.Providers" ? ProvidersMvid : ControllerMvid;
            var hash = t.Assembly == "Emby.Providers" ? ProvidersHash : ControllerHash;
            if (assembly.ManifestModule.ModuleVersionId != mvid || string.IsNullOrEmpty(assembly.Location) ||
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) != hash)
                throw new NotSupportedException(t.Assembly + " file differs");
            var method = assembly.ManifestModule.ResolveMethod(t.Token) ?? throw new NotSupportedException(t.Name + " missing");
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (method.Name != t.Name || il?.Length != t.Length || Convert.ToHexString(SHA256.HashData(il)) != t.Hash)
                throw new NotSupportedException(t.Name + " IL differs");
            result[i] = method;
        }
        if (result[3] is MethodInfo detect) confidence = detect.ReturnType.GetProperty("Confidence") ?? throw new NotSupportedException("Confidence missing");
        return result;
    }

    // Original m_c0234241430b7f2407285172: remember the .strm episode being checked in this flow.
    private static void SupportPrefix(Episode item)
    {
        if (!enabled || item == null || !item.IsShortcut) return;
        AllowedShortcut.Value = item.InternalId;
        Interlocked.Increment(ref unlocked);
    }

    // Original A2:13240: IsShortcut is false for exactly that episode.
    private static bool ShortcutPrefix(BaseItem __instance, ref bool __result)
    {
        var id = AllowedShortcut.Value;
        if (id == 0 || __instance.InternalId != id) return true;
        __result = false;
        return false;
    }

    // Original m_d53afbb9c7f58efdf26918c2.
    private static void QueryPostfix(LibraryOptions libraryOptions, InternalItemsQuery __result)
    {
        if (!enabled || __result == null || options?.Invoke() is not { } settings || library is not { } manager) return;
        try
        {
            Interlocked.Increment(ref scoped);
            Apply(__result, settings, manager, CancellationToken.None);
        }
        catch (Exception error) { Plugin.Instance?.Logger?.Warn("[StrmAssistantRebuild] Native intro query scope skipped: {0}", error.GetType().Name); }
    }

    public static void Apply(InternalItemsQuery query, IntroSkipOptionsView settings, ILibraryManager manager, CancellationToken token)
    {
        var selected = ScopeSelection.Parse(settings.MarkerEnabledLibraryScope);
        if (selected.Contains(ScopeSelection.Favorites))
        {
            var seasons = ScopeSelection.SelectForServer(manager, ScopeSelection.Favorites, LibraryScopeKind.Marker, token)
                .OfType<Episode>().Select(e => e.ParentId).Where(id => id > 0).Distinct().ToArray();
            query.ParentIds = seasons.Length == 0 ? new long[] { -1 } : seasons;
        }
        else
        {
            if (selected.Count > 0)
            {
                var paths = manager.GetVirtualFolders().Where(f => ScopeSelection.EligibleFolder(f, LibraryScopeKind.Marker) && ScopeSelection.MatchesFolder(f, selected))
                    .SelectMany(f => f.Locations ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToArray();
                if (paths.Length > 0) query.PathStartsWithAny = paths;
            }
            var keywords = ScopeSelection.Parse(settings.FingerprintExcludeKeywords);
            if (keywords.Count > 0)
            {
                var excluded = manager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Season) }, Recursive = true, EnableTotalRecordCount = false }, token)
                    .Where(s => keywords.Any(k => (s.Path ?? "").Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                                   ((s as Season)?.Series?.Path ?? "").Contains(k, StringComparison.OrdinalIgnoreCase)))
                    .Select(s => s.InternalId).ToArray();
                if (excluded.Length > 0) query.ExcludeParentIds = excluded;
            }
        }
        query.MinParentIndexNumber = 1;
    }

    // Original m_3b589feb13637ce2f48cb421.
    private static void DetectPostfix(object __result)
    {
        if (!enabled || __result == null || confidence == null) return;
        try { if (Convert.ToDouble(confidence.GetValue(__result)) == 0.0) ZeroConfidence.Value = true; } catch { }
    }

    // Original m_44967b553dd4b00d9f9be0a9: log the native failure (stops retries) only after a zero-confidence detection.
    private static bool FailedPrefix(Episode episode)
    {
        if (!enabled || episode == null) return true;
        if (ZeroConfidence.Value) { ZeroConfidence.Value = false; Interlocked.Increment(ref failuresLogged); return true; }
        BaseItem.ItemRepository.DeleteChapters(episode.InternalId, new[] { MarkerType.IntroStart, MarkerType.IntroEnd });
        Interlocked.Increment(ref failuresKept);
        return false;
    }
}
