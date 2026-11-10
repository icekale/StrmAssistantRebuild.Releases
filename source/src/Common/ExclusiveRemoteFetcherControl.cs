using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;

namespace StrmAssistantRebuild.Common;

/// <summary>Bounded BlockRemoteFetcher: non-media candidates plus remote metadata in exact ordinary
/// Movie/Episode/Audio native refresh scopes witnessed at image eligibility, or with no current scope.
/// Full metadata with image ValidationOnly additionally filters remote-only (not IPreRefreshProvider) metadata.
/// Direct media enumeration filters metadata only. No file-change/restore effects.</summary>
public static class ExclusiveRemoteFetcherControl
{
    private const string Owner = "StrmAssistantRebuild.ExclusiveRemoteFetcher.4.10.2";
    private const string ProviderType = "Emby.Providers.Manager.ProviderManager";
    private const string Feature = "BlockRemoteFetcher";
    private const string Coverage = "Exact persisted parented Series/Season/MusicAlbum/BoxSet: metadata and images, including manual queries. Exact rooted file Movie/Episode/Audio with positive ID, nonempty GUID, parent and no ExtraType: remote metadata with no current refresh/owned scope, or in ordinary native RefreshSingleItem scopes witnessed at image eligibility; full metadata with image ValidationOnly also filters remote-only providers (not IPreRefreshProvider), with no Replace/Search/Overwrite. Explicit/invalid scopes, missing image entry, identity drift and other scoped refresh modes stay native. IgnoreFileChange/clear/restore remain unsupported.";
    private static readonly Version ExpectedVersion = new(4, 10, 2, 0);
    private static readonly Guid ExpectedMvid = new("29b37ec2-5f02-443c-a995-1f61c71f43c6");
    private static readonly object Gate = new();
    private static readonly List<MethodInfo> Installed = new();
    private static MethodInfo[] targets = Array.Empty<MethodInfo>();
    private static Harmony? patcher;
    private static long generation;
    private static volatile bool enabled;
    public static bool IsEnabled => enabled;
    public static bool IsHealthy { get { lock (Gate) return HealthyTypedOwner(); } }
    public static string Detail { get; private set; } = "Disabled. " + Coverage;

    public static bool Configure(Version hostVersion, bool exclusiveEnabled, string? features, out string detail)
    {
        lock (Gate)
        {
            try
            {
                var selected = (features ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // Unrelated settings saves must not dismantle a still-valid active filter.
                // Recheck actual ownership, not only our enabled flag, before reusing the pinned targets.
                if (enabled && exclusiveEnabled && hostVersion == ExpectedVersion && selected.Length != 0 &&
                    selected.All(x => string.Equals(x, Feature, StringComparison.OrdinalIgnoreCase)) && HasExpectedPatches())
                    return Report(true, "Unchanged: existing remote eligibility filters retained. ", out detail);
                enabled = false;
                ExclusiveSaverControl.RemoteContextEnabled = false;
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
                    patcher.Patch(targets[i], prefix: new HarmonyMethod(typeof(ExclusiveRemoteFetcherControl).GetMethod(i == 0 ? nameof(MetadataPrefix) : nameof(ImagePrefix), BindingFlags.NonPublic | BindingFlags.Static)!));
                }
                enabled = true;
                ExclusiveSaverControl.RemoteContextEnabled = true;
                return Report(true, "Enabled: bounded remote provider eligibility filter. ", out detail);
            }
            catch (Exception error)
            {
                enabled = false;
                ExclusiveSaverControl.RemoteContextEnabled = false;
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
            ExclusiveSaverControl.RemoteContextEnabled = false;
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

    private static bool HasExpectedPatches()
    {
        if (targets.Length != 2 || Installed.Count != 2 || !targets.SequenceEqual(Installed)) return false;
        for (var i = 0; i < targets.Length; i++)
        {
            var info = Harmony.GetPatchInfo(targets[i]);
            var expected = typeof(ExclusiveRemoteFetcherControl).GetMethod(i == 0 ? nameof(MetadataPrefix) : nameof(ImagePrefix), BindingFlags.NonPublic | BindingFlags.Static);
            if (info == null || info.Owners.Any(owner => owner != Owner) || info.Prefixes.Count != 1 ||
                info.Prefixes[0].owner != Owner || info.Prefixes[0].PatchMethod != expected ||
                info.Postfixes.Count != 0 || info.Transpilers.Count != 0 || info.Finalizers.Count != 0) return false;
        }
        return true;
    }

    private static bool ForeignOwner() => targets.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(o => o != Owner) == true);
    private static bool ShouldFilter(BaseItem item)
    {
        if (!enabled) return false;
        if (ForeignOwner())
        {
            enabled = false;
            ExclusiveSaverControl.RemoteContextEnabled = false;
            Interlocked.Increment(ref generation);
            Detail = "Inactive: eligibility ownership changed; callbacks inactive. " + Coverage;
            return false;
        }
        // Deliberate bounded subset: no URL/protocol tests, opaque refresh guesses or library option mutations.
        return item != null && (item.GetType() == typeof(Series) || item.GetType() == typeof(Season) ||
            item.GetType() == typeof(MusicAlbum) || item.GetType() == typeof(BoxSet)) &&
            item.InternalId > 0 && !item.ExtraType.HasValue && item.Parent != null;
    }

    private static bool MetadataPrefix(IMetadataProvider provider, BaseItem item, LibraryOptions libraryOptions, ref bool __result)
    {
        if (provider is not IRemoteMetadataProvider) return true;
        var block = ShouldFilter(item); // Preserve the existing non-media contract.
        if (!block && HealthyTypedOwner())
        {
            var epoch = Volatile.Read(ref generation);
            block = ExclusiveSaverControl.CanBlockRemoteMetadata(item, libraryOptions, epoch, provider is not IPreRefreshProvider) &&
                HealthyTypedOwner() && epoch == Volatile.Read(ref generation);
        }
        if (!block) return true;
        __result = false;
        return false;
    }

    private static bool ImagePrefix(IImageProvider provider, BaseItem item, LibraryOptions libraryOptions, ImageRefreshOptions refreshOptions, ref bool __result)
    {
        // First image candidate is the original typed-context boundary, even when it is a local provider.
        // This owner alone holds 257/256. No second consumer competes for those Harmony targets.
        if (HealthyTypedOwner()) ExclusiveSaverControl.CaptureRemoteRefresh(item, libraryOptions, refreshOptions, Volatile.Read(ref generation));
        if (provider is not IRemoteImageProvider || !ShouldFilter(item)) return true;
        __result = false;
        return false;
    }

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
                new[] { typeof(IImageProvider), typeof(BaseItem), typeof(LibraryOptions), typeof(ImageRefreshOptions), typeof(bool), typeof(bool) }, null)!
        };
    }

    internal static bool Verify(MethodInfo method)
    {
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

    private static bool FilePin(Assembly assembly, string name, Guid mvid, string hash)
    {
        if (assembly.GetName().Name != name || assembly.GetName().Version != ExpectedVersion || assembly.ManifestModule.ModuleVersionId != mvid || string.IsNullOrEmpty(assembly.Location)) return false;
        using var stream = File.OpenRead(assembly.Location);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)) == hash;
    }
}
