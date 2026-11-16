using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;

namespace StrmAssistantRebuild.Common;

/// <summary>M172: BlockRemoteFetcher as the original (decompiled metadata/image CanRefresh prefixes, A:70601/A:70112):
/// every remote metadata and remote image provider is ineligible for parented items and extras. Pass-throughs: rooted items
/// (no parent, no ExtraType); remote metadata inside native GetEnabledMetadataProviders (credits, episode lists, external ids);
/// remote images for a file Video/Audio checked inside the refresh of another, new file Video/Audio (its own refresh blocks, new or not).
/// The original default exclusive provider rules (M158) apply in the same prefixes.</summary>
public static class ExclusiveRemoteFetcherControl
{
    private const string Owner = "StrmAssistantRebuild.ExclusiveRemoteFetcher.4.10.2";
    private const string ProviderType = "Emby.Providers.Manager.ProviderManager";
    private const string Feature = "BlockRemoteFetcher";
    private const string Coverage = "BlockRemoteFetcher (original rule): remote metadata and remote image providers are ineligible for every parented item or extra, including manual image queries. Native pass-throughs: rooted items (no parent, no ExtraType); remote metadata inside native GetEnabledMetadataProviders (person credits, episode lists, external ids); remote images for a file Video/Audio checked inside the refresh of another, new file Video/Audio (an item's own refresh blocks them, new or not). Original-only pass-through without a rebuild counterpart: the SyncMediaInfo native refresh (the rebuild's sync endpoint does not refresh). File-change handling is ExclusiveFileChange (M157).";
    private static readonly Version ExpectedVersion = new(4, 10, 2, 0);
    private static readonly Guid ExpectedMvid = new("29b37ec2-5f02-443c-a995-1f61c71f43c6");
    private static readonly object Gate = new();
    private static readonly List<MethodInfo> Installed = new();
    private static MethodInfo[] targets = Array.Empty<MethodInfo>();
    private static Harmony? patcher;
    private static long generation;
    private static volatile bool enabled;
    // M158: blockAll = BlockRemoteFetcher (9) selected; defaultRules = the original default exclusive provider rules.
    private static volatile bool blockAll = true, defaultRules;
    // Original A_0091 (ThreadStatic, AA:13000): depth of native GetEnabledMetadataProviders on this thread.
    [ThreadStatic] private static int window;
    public static bool IsEnabled => enabled;
    public static bool BlocksAllRemote => enabled && blockAll;
    public static bool DefaultRulesActive => enabled && defaultRules;
    public static bool IsHealthy { get { lock (Gate) return HealthyTypedOwner(); } }
    public static string Detail { get; private set; } = "Disabled. " + Coverage;

    /// <summary>
    /// M158: production entry. Installs the same pinned CanRefresh prefixes whenever exclusive mode is on and applies the
    /// original default rules (decompiled metadata/image CanRefresh prefixes): on an ordinary refresh of an old Video/Audio
    /// item remote metadata and image providers are skipped, and a screen-grab (dynamic) image provider is skipped when the
    /// item already has a Primary image unless CatchAllAllow or ReplaceAllImages (without CatchAllBlock/UnconditionalBlock).
    /// BlockRemoteFetcher (9) keeps its existing bounded behavior on top.
    /// </summary>
    public static bool ConfigureWithDefaults(Version hostVersion, bool exclusiveEnabled, string? features, out string detail)
    {
        lock (Gate)
        {
            var nine = (features ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(Feature, StringComparer.OrdinalIgnoreCase);
            if (!exclusiveEnabled) { defaultRules = false; return Configure(hostVersion, false, "", out detail); }
            var ok = Configure(hostVersion, true, Feature, out detail);
            if (!ok) { defaultRules = false; return false; }
            blockAll = nine;
            defaultRules = true;
            detail = Detail = (nine ? "Enabled: BlockRemoteFetcher plus original default provider rules. " : "Enabled: original default exclusive provider rules (ordinary refresh skips remote providers; screen grab only without Primary). ") + Coverage;
            return true;
        }
    }

    public static bool Configure(Version hostVersion, bool exclusiveEnabled, string? features, out string detail)
    {
        lock (Gate)
        {
            blockAll = true; defaultRules = false;
            try
            {
                var selected = (features ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // Unrelated settings saves must not dismantle a still-valid active filter.
                // Recheck actual ownership, not only our enabled flag, before reusing the pinned targets.
                if (enabled && exclusiveEnabled && hostVersion == ExpectedVersion && selected.Length != 0 &&
                    selected.All(x => string.Equals(x, Feature, StringComparison.OrdinalIgnoreCase)) && HasExpectedPatches())
                    return Report(true, "Unchanged: existing remote eligibility filters retained. ", out detail);
                enabled = false;
                Interlocked.Increment(ref generation);
                Remove();
                if (!exclusiveEnabled || selected.Length == 0) return Report(true, "Disabled; native candidates restored. ", out detail);
                if (selected.Any(x => !string.Equals(x, Feature, StringComparison.OrdinalIgnoreCase)))
                    return Report(false, "Inactive: unsupported feature selection. ", out detail);
                if (hostVersion != ExpectedVersion) return Report(false, "Inactive: requires exact host 4.10.2.0. ", out detail);
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.Providers");
                if (assembly == null || !FilePin(assembly, "Emby.Providers", ExpectedMvid, "16874EA37E2EC8152E7F2FA9F9D7FA51A284959DCE3A918694003D009B2B2A94") ||
                    !FilePin(typeof(BaseItem).Assembly, "MediaBrowser.Controller", new Guid("58dae430-7295-47bb-a62f-b26bf0d89604"), "EAA6B9EDEBB21FE3AF9C272DEF8C913D9EC6B177B552E1B789FEE15CB59289DA") ||
                    !FilePin(typeof(LibraryOptions).Assembly, "MediaBrowser.Model", new Guid("3d56dcbb-6440-4e93-a3ad-05e588af05c2"), "2B7DA29239811F8C035784DF7B3F48A1668E9537784B6524DDE43B3905572617"))
                    return Report(false, "Inactive: exact native assembly identity differs. ", out detail);
                targets = GetTargets(assembly);
                if (!targets.All(Verify) || ForeignOwner()) return Report(false, "Inactive: eligibility signature/body or owner differs. ", out detail);
                patcher ??= new Harmony(Owner);
                for (var i = 0; i < targets.Length; i++)
                {
                    Installed.Add(targets[i]);
                    patcher.Patch(targets[i], prefix: new HarmonyMethod(Hook(PrefixName(i))), finalizer: i == 2 ? new HarmonyMethod(Hook(nameof(LeaveWindow))) : null);
                }
                enabled = true;
                return Report(true, "Enabled: remote provider eligibility filter. ", out detail);
            }
            catch (Exception error)
            {
                enabled = false;
                Interlocked.Increment(ref generation);
                var message = "Inactive: " + error.GetType().Name + ". ";
                try { Remove(); } catch { message += "Owned cleanup pending; callbacks inactive. "; }
                return Report(false, message, out detail);
            }
        }
    }

    public static void Disable()
    {
        lock (Gate)
        {
            enabled = false;
            Interlocked.Increment(ref generation);
            Detail = "Disabled; native candidates restored. " + Coverage;
            try { Remove(); } catch { Detail += " Owned cleanup pending; callbacks inactive."; }
        }
    }

    private static bool Report(bool result, string message, out string detail)
    { detail = Detail = message + Coverage; return result; }

    private static void Remove()
    {
        for (var i = Installed.Count - 1; i >= 0; i--)
        {
            patcher?.Unpatch(Installed[i], HarmonyPatchType.All, Owner);
            Installed.RemoveAt(i);
        }
    }

    private static MethodInfo Hook(string name) => typeof(ExclusiveRemoteFetcherControl).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private static string PrefixName(int index) => index switch { 0 => nameof(MetadataPrefix), 1 => nameof(ImagePrefix), _ => nameof(EnterWindow) };

    private static bool HasExpectedPatches()
    {
        if (targets.Length != 3 || Installed.Count != 3 || !targets.SequenceEqual(Installed)) return false;
        for (var i = 0; i < targets.Length; i++)
        {
            var info = Harmony.GetPatchInfo(targets[i]);
            if (info == null || info.Owners.Any(owner => owner != Owner) || info.Prefixes.Count != 1 ||
                info.Prefixes[0].owner != Owner || info.Prefixes[0].PatchMethod != Hook(PrefixName(i)) ||
                info.Postfixes.Count != 0 || info.Transpilers.Count != 0) return false;
            if (i == 2 ? info.Finalizers.Count != 1 || info.Finalizers[0].owner != Owner || info.Finalizers[0].PatchMethod != Hook(nameof(LeaveWindow)) : info.Finalizers.Count != 0) return false;
        }
        return true;
    }

    private static bool ForeignOwner() => targets.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(o => o != Owner) == true);
    /// <summary>BlockRemoteFetcher is selected and this owner alone still holds the three exact targets.</summary>
    private static bool BlockActive()
    {
        if (!enabled || !blockAll) return false;
        if (!ForeignOwner()) return true;
        enabled = false;
        Interlocked.Increment(ref generation);
        Detail = "Inactive: eligibility ownership changed; callbacks inactive. " + Coverage;
        return false;
    }
    /// <summary>Original: an item without a parent that is not an extra keeps native eligibility (library roots, detached items).</summary>
    private static bool Rooted(BaseItem item) => item.Parent == null && !item.ExtraType.HasValue;

    // Original prefix/finalizer pair on GetEnabledMetadataProviders: always counted while installed (the native call is synchronous).
    private static void EnterWindow() => window++;
    private static void LeaveWindow() { if (window > 0) window--; else window = 0; }
    internal static bool InsideEnabledProviders => window > 0;

    private static bool MetadataPrefix(IMetadataProvider provider, BaseItem item, ref bool __result)
    {
        if (!enabled || provider is not IRemoteMetadataProvider || item == null || window > 0 || Rooted(item)) return true;
        if (defaultRules && ExclusiveEligibility.SkipRemoteOnOrdinary(item)) { __result = false; return false; }
        if (!BlockActive()) return true;
        Interlocked.Increment(ref metadataBlocked);
        __result = false;
        return false;
    }

    private static bool ImagePrefix(IImageProvider provider, BaseItem item, ImageRefreshOptions refreshOptions, ref bool __result)
    {
        if (!enabled || item == null || Rooted(item)) return true;
        if (defaultRules && (provider is IRemoteImageProvider && ExclusiveEligibility.SkipRemoteOnOrdinary(item) ||
            provider is IDynamicImageProviderWithLibraryOptions && ExclusiveEligibility.BlockDynamicImage(item)))
        { __result = false; return false; }
        if (provider is not IRemoteImageProvider || !BlockActive()) return true;
        // Original (A:70112 case 62 / IL_06ea): a context for this same item goes to global 9 and is blocked whether new or not
        // (without a context the original creates one for this item, so it blocks too). Only a typed file Video/Audio check made
        // inside the refresh of another, new file Video/Audio (e.g. its extras) keeps remote images.
        if (refreshOptions is MetadataRefreshOptions && item is Video or Audio && item.IsFileProtocol &&
            ExclusiveSaverControl.CurrentNativeRefresh is { IsNew: true, Item: Video or Audio } c && c.Item.IsFileProtocol &&
            c.Item.InternalId != item.InternalId)
            return true;
        Interlocked.Increment(ref imagesBlocked);
        __result = false;
        return false;
    }
    private static long metadataBlocked, imagesBlocked;
    public static long MetadataBlocked => Interlocked.Read(ref metadataBlocked);
    public static long ImagesBlocked => Interlocked.Read(ref imagesBlocked);

    private static bool HealthyTypedOwner()
    {
        try { return enabled && HasExpectedPatches() && targets.All(target =>
            { var p = Harmony.GetPatchInfo(target)!; return p.InnerPrefixes.Count == 0 && p.InnerPostfixes.Count == 0 &&
                p.Prefixes.All(h => h.priority == Priority.Normal && h.before.Length == 0 && h.after.Length == 0 && !h.debug); }); }
        catch { return false; }
    }

    internal static MethodInfo[] GetTargets(Assembly assembly)
    {
        var type = assembly.GetType(ProviderType, true)!;
        return new[]
        {
            type.GetMethod("CanRefresh", BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(IMetadataProvider), typeof(BaseItem), typeof(LibraryOptions), typeof(bool), typeof(bool), typeof(bool) }, null)!,
            type.GetMethod("CanRefresh", BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(IImageProvider), typeof(BaseItem), typeof(LibraryOptions), typeof(ImageRefreshOptions), typeof(bool), typeof(bool) }, null)!,
            type.GetMethod("GetEnabledMetadataProviders", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(BaseItem), typeof(LibraryOptions) }, null)!
        };
    }

    internal static bool Verify(MethodInfo method)
    {
        if (method?.Name == "GetEnabledMetadataProviders") return VerifyWindow(method);
        if (method == null || method.DeclaringType?.FullName != ProviderType || method.Name != "CanRefresh" ||
            !method.IsPrivate || method.IsAbstract || method.ContainsGenericParameters || method.ReturnType != typeof(bool) ||
            method.Module.ModuleVersionId != ExpectedMvid || method.Module.Assembly.GetName().Name != "Emby.Providers" || method.Module.Assembly.GetName().Version != ExpectedVersion) return false;
        var metadata = method.MetadataToken == 0x06000256;
        if (!metadata && method.MetadataToken != 0x06000257) return false;
        var expected = metadata
            ? new[] { typeof(IMetadataProvider), typeof(BaseItem), typeof(LibraryOptions), typeof(bool), typeof(bool), typeof(bool) }
            : new[] { typeof(IImageProvider), typeof(BaseItem), typeof(LibraryOptions), typeof(ImageRefreshOptions), typeof(bool), typeof(bool) };
        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (method.IsStatic != metadata || !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(expected) || body == null || il == null || !body.InitLocals ||
            body.MaxStackSize != (metadata ? 3 : 7) || body.LocalSignatureMetadataToken != (metadata ? 0x110000ce : 0x110000cf) ||
            il.Length != (metadata ? 166 : 158) || Convert.ToHexString(SHA256.HashData(il)) != (metadata
                ? "500682DEA9CE7F65DEAFF47DA94CD92494B5EEB9C2D942DAAA96CDA0309C8CE3"
                : "3276188FA4D8A0E59FCB18746C05D7C11C1F5DC2ECB1AD5BA6571049B08B6B02")) return false;
        if (metadata) return body.ExceptionHandlingClauses.Count == 0;
        if (body.ExceptionHandlingClauses.Count != 1) return false;
        var eh = body.ExceptionHandlingClauses[0];
        return eh.Flags == ExceptionHandlingClauseOptions.Clause && eh.TryOffset == 90 && eh.TryLength == 10 &&
            eh.HandlerOffset == 100 && eh.HandlerLength == 56 && eh.CatchType == typeof(Exception);
    }

    /// <summary>Native public GetEnabledMetadataProviders: synchronous CanRefresh filter and ToArray (no EH).</summary>
    private static bool VerifyWindow(MethodInfo method)
    {
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        return method.DeclaringType?.FullName == ProviderType && method.IsPublic && !method.IsStatic && method.MetadataToken == 0x0600024C &&
            method.ReturnType == typeof(IMetadataProvider[]) && method.Module.ModuleVersionId == ExpectedMvid && method.Module.Assembly.GetName().Version == ExpectedVersion &&
            method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(BaseItem), typeof(LibraryOptions) }) &&
            body is { InitLocals: true, MaxStackSize: 3, LocalSignatureMetadataToken: 0x110000C8 } && body.ExceptionHandlingClauses.Count == 0 &&
            il?.Length == 131 && Convert.ToHexString(SHA256.HashData(il)) == "6443EC862D36D5D43AF62D5B9BB21AF2DF4F352A7C98A962DD872326DF9D8CAF";
    }

    private static bool FilePin(Assembly assembly, string name, Guid mvid, string hash)
    {
        if (assembly.GetName().Name != name || assembly.GetName().Version != ExpectedVersion || assembly.ManifestModule.ModuleVersionId != mvid || string.IsNullOrEmpty(assembly.Location)) return false;
        using var stream = File.OpenRead(assembly.Location);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)) == hash;
    }
}
