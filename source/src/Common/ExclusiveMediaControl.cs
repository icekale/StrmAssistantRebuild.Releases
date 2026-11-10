using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.MediaInfo;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

/// <summary>Global extraction gate on eight validated public host methods; never changes media state.</summary>
public static class ExclusiveMediaControl
{
    private const string Owner = "StrmAssistantRebuild.ExclusiveMedia.4.10.2";
    private const string HostAssembly = "Emby.Server.MediaEncoding";
    private const string ProbeType = "Emby.Server.MediaEncoding.Probing.MediaProbeManager";
    private const string ImageType = "Emby.Server.MediaEncoding.ImageExtraction.ImageExtractionManager";
    private static readonly Version ExpectedVersion = new(4, 10, 2, 0);
    private static readonly Guid ExpectedMvid = new("1a6398de-16b2-4e89-9e0c-2ed7d3859103");
    private static readonly object Sync = new();
    private static readonly AsyncLocal<OwnedScope?> Current = new();
    private static readonly List<MethodInfo> Installed = new();
    private static MethodInfo[] verifiedTargets = Array.Empty<MethodInfo>();
    private static Harmony? patcher;
    private static long generation;
    // Internal for the isolated prefix-execution harness; never a host field or a public bypass API.
    internal static volatile bool enabled;
    public static bool IsEnabled => enabled;
    public static bool IsHealthy
    {
        get { lock (Sync) { try { return enabled && HasExpectedPatches() && verifiedTargets.All(target =>
            { var p = Harmony.GetPatchInfo(target)!; return p.InnerPrefixes.Count == 0 && p.InnerPostfixes.Count == 0 &&
                p.Prefixes.All(h => h.priority == Priority.Normal && h.before.Length == 0 && h.after.Length == 0 && !h.debug); }); } catch { return false; } } }
    }

    // Name, arity, IL size/hash, stack, local signature: copied from the actual 4.10.2 Linux ARM64 host.
    private static readonly Dictionary<int, (string Name, int Arity, int Length, string Hash, int Stack, int Locals)> Fingerprints = new()
    {
        [0x06000227] = ("GetMediaInfo", 2, 223, "51AB8928F7E3674868466BBD8967A3055C6A23DF5E4F99E93E261E16B0822B98", 8, 0x1100009C),
        [0x06000252] = ("ExtractAudioImage", 4, 42, "D5792F6146CB4347CFDE087BAB22D2E2DF396CD6106721E05A4DC64658A4336B", 11, 0x110000B9),
        [0x06000253] = ("ExtractAudioImage", 5, 35, "7C331AEC5DB806AC8BB64DA4873343A5A6F86955C823CE1FFAD52F3FEA175BED", 11, 0x110000BA),
        [0x06000254] = ("ExtractVideoImage", 8, 30, "FC6116715750F65F781AB34DD1D6E7A3132D8E965077A38AB5BD525DD7FBF767", 11, 0x1100003D),
        [0x06000255] = ("ExtractVideoImage", 7, 37, "55EF014C5909B8651D7DF6F92BB1AFA54D8983E49E04F498736E6D80470BE5D3", 11, 0x110000BA),
        [0x06000259] = ("ExtractVideoImagesOnInterval", 11, 112, "909183D609F8E0208014E7AA6C9DB768B0ED9A495EFCFB3BDDDFD225395AE6A3", 15, 0x110000BD),
        [0x0600025A] = ("ExtractVideoImagesFromList", 11, 151, "D1F75CDFB1A0CC0B1732B53A4BF9BB25A71AB378DB539AEBE72C67DB75D2D942", 2, 0x110000BE),
        [0x0600025B] = ("ExtractVideoImagesFromList2", 11, 65, "7ADF24006A05CE2F5C56AC75039848BCFFF3B1D6C2A942BC26D7D30F0C7812F5", 14, 0x110000BF)
    };

    /// <summary>Read-only default-gate platform check; neither changes generation nor installs a detour.</summary>
    public static bool Probe(Version hostVersion, out string detail)
    {
        try
        {
            if (hostVersion != ExpectedVersion) { detail = "Host version differs from validated 4.10.2.0."; return false; }
            var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == HostAssembly);
            if (assembly == null) { detail = "Host media encoding assembly is not loaded."; return false; }
            foreach (var target in GetTargets(assembly))
            {
                var body = target.GetMethodBody();
                if (body == null || !VerifyFingerprint(target, body.GetILAsByteArray(), body.MaxStackSize, body.InitLocals,
                    body.LocalSignatureMetadataToken, body.ExceptionHandlingClauses.Count))
                { detail = "Public extraction signature/MVID/IL differs: " + target.Name; return false; }
                if (Harmony.GetPatchInfo(target)?.Owners.Any(owner => owner != Owner) == true)
                { detail = "Foreign patch owns extraction: " + target.Name; return false; }
            }
            detail = "Eight public entrypoints verified; default exclusive gate only, no detour installed.";
            return true;
        }
        catch (Exception ex) { detail = "Exclusive probe failed: " + ex.GetType().Name + ": " + ex.Message; return false; }
    }

    public static bool Configure(Version hostVersion, MediaInfoExtractOptions options, out string detail) =>
        ConfigureWithAlternativeConsumer(hostVersion, options, out detail, false);

    public static bool ConfigureWithAlternativeConsumer(Version hostVersion, MediaInfoExtractOptions options, out string detail, bool alternativeConsumerReady)
    {
        lock (Sync)
        {
            if (options?.ExclusiveExtract == true && TryGetAlternativeSelection(options.ExclusiveControlFeatures, out _) && !alternativeConsumerReady)
            { Disable(); detail = "Exclusive gate inactive: ExtractAlternative stop producer/public refresh consumer is not active."; return false; }
            // A selected saver opt-out is supported only with its independently verified consumer.
            if (options?.ExclusiveExtract == true && (options.ExclusiveControlFeatures ?? "").Split(',', StringSplitOptions.TrimEntries).Contains("NoNfoSaverOptimization", StringComparer.OrdinalIgnoreCase) && !ExclusiveSaverControl.IsEnabled)
            { Disable(); detail = "Exclusive gate inactive: NoNfoSaverOptimization consumer is not active; native behavior retained."; return false; }
            if (options?.ExclusiveExtract == true && (options.ExclusiveControlFeatures ?? "").Split(',', StringSplitOptions.TrimEntries).Contains("BlockRemoteFetcher", StringComparer.OrdinalIgnoreCase) && !ExclusiveRemoteFetcherControl.IsEnabled)
            { Disable(); detail = "Exclusive gate inactive: BlockRemoteFetcher consumer is not active; native behavior retained."; return false; }
            try
            {
                // Preserve owned operations only while all eight installed entrypoints remain exactly healthy.
                if (enabled && hostVersion == ExpectedVersion && options?.ExclusiveExtract == true &&
                    ValidateFeatures(options.ExclusiveControlFeatures, out _) && HasExpectedPatches())
                { detail = "Enabled: unchanged healthy extraction gate; existing owned scopes retained."; return true; }
                enabled = false;
                Interlocked.Increment(ref generation); // Revoke captured scopes; in-flight host Tasks are not canceled.
                RemovePatches();
                ArgumentNullException.ThrowIfNull(options);
                if (!options.ExclusiveExtract) { detail = "Disabled; native media extraction restored."; return true; }
                if (!ValidateFeatures(options.ExclusiveControlFeatures, out detail)) return false;
                if (hostVersion != ExpectedVersion) { detail = "Exclusive gate inactive: host version differs from validated 4.10.2.0."; return false; }
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == HostAssembly);
                if (assembly is null) { detail = "Exclusive gate inactive: host media encoding assembly is not loaded."; return false; }
                var targets = verifiedTargets = GetTargets(assembly);
                foreach (var target in targets)
                {
                    var body = target.GetMethodBody();
                    if (body is null || !VerifyFingerprint(target, body.GetILAsByteArray(), body.MaxStackSize, body.InitLocals,
                            body.LocalSignatureMetadataToken, body.ExceptionHandlingClauses.Count))
                    { detail = "Exclusive gate inactive: public method fingerprint differs: " + target; return false; }
                    if (Harmony.GetPatchInfo(target)?.Owners.Any() == true)
                    { detail = "Exclusive gate inactive: another patch owns " + target.Name; return false; }
                }
                patcher ??= new Harmony(Owner);
                foreach (var target in targets)
                {
                    Installed.Add(target); // Keep cleanup handle even if installing this detour throws.
                    patcher.Patch(target, prefix: new HarmonyMethod(PrefixFor(target.ReturnType)));
                }
                enabled = true;
                detail = "Enabled: 8 fingerprint-locked public probe/image methods; owned async scopes only. ExclusiveControlFeatures: default extraction gate, with independently verified typed saver opt-out when selected.";
                return true;
            }
            catch (Exception error)
            {
                enabled = false;
                Interlocked.Increment(ref generation); // Health-check exceptions must revoke scopes too.
                detail = "Exclusive gate inactive: " + error.GetType().Name + ": " + error.Message;
                try { RemovePatches(); } catch (Exception cleanup) { detail += "; detour cleanup pending: " + cleanup.Message; }
                return false;
            }
        }
    }

    public static void Disable()
    {
        lock (Sync)
        {
            enabled = false; // Functional rollback precedes detour removal, including cleanup failures.
            Interlocked.Increment(ref generation);
            try { RemovePatches(); } catch { }
        }
    }

    /// <summary>Use around the complete awaited plugin operation. Never wrap native background scheduling.</summary>
    public static IDisposable EnterOwnedOperation()
    {
        var epoch = Volatile.Read(ref generation);
        var scope = new OwnedScope(IsOwnedOperation ? Current.Value : null, epoch);
        Current.Value = scope;
        return scope;
    }

    internal static long Epoch => Volatile.Read(ref generation);
    internal static bool HasRawOwnedScope => Current.Value != null;

    internal static bool IsOwnedOperation
    {
        get
        {
            var scope = Current.Value;
            if (scope is null) return false;
            var epoch = Volatile.Read(ref generation);
            for (; scope is not null; scope = scope.Parent)
                if (Volatile.Read(ref scope.Disposed) != 0 || scope.Generation != epoch) return false;
            return true;
        }
    }

    private sealed class OwnedScope : IDisposable
    {
        internal readonly OwnedScope? Parent;
        internal readonly long Generation;
        internal int Disposed;
        internal OwnedScope(OwnedScope? parent, long epoch) { Parent = parent; Generation = epoch; }
        public void Dispose()
        {
            Interlocked.Exchange(ref Disposed, 1); // Revoke inherited ExecutionContext copies, not just this flow's value.
            if (!ReferenceEquals(Current.Value, this)) return;
            var parent = Parent;
            while (parent is not null && Volatile.Read(ref parent.Disposed) != 0) parent = parent.Parent;
            Current.Value = parent;
        }
    }

    private static bool HasExpectedPatches()
    {
        if (verifiedTargets.Length != 8 || Installed.Count != 8 || !verifiedTargets.SequenceEqual(Installed)) return false;
        foreach (var target in verifiedTargets)
        {
            var info = Harmony.GetPatchInfo(target);
            if (info == null || info.Prefixes.Count != 1 || info.Prefixes[0].owner != Owner ||
                info.Prefixes[0].PatchMethod != PrefixFor(target.ReturnType) || info.Postfixes.Count != 0 ||
                info.Transpilers.Count != 0 || info.Finalizers.Count != 0) return false;
        }
        return true;
    }

    private static void RemovePatches()
    {
        for (var i = Installed.Count - 1; i >= 0; i--)
        {
            patcher?.Unpatch(Installed[i], HarmonyPatchType.All, Owner);
            Installed.RemoveAt(i); // Retain pending entries when cleanup throws; never unpatch other owners.
        }
    }

    internal static bool IsAlternativeOnly(string? features)
    {
        var selected = (features ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return selected.Length == 1 && string.Equals(selected[0], "ExtractAlternative", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryGetAlternativeSelection(string? features, out string saverFeatures)
    {
        saverFeatures = "";
        var selected = (features ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var noNfo = selected.Count(name => string.Equals(name, "NoNfoSaverOptimization", StringComparison.OrdinalIgnoreCase));
        var remote = selected.Count(name => string.Equals(name, "BlockRemoteFetcher", StringComparison.OrdinalIgnoreCase));
        if (selected.Length is < 1 or > 3 ||
            selected.Count(name => string.Equals(name, "ExtractAlternative", StringComparison.OrdinalIgnoreCase)) != 1 ||
            noNfo > 1 || remote > 1 || selected.Length != 1 + noNfo + remote)
            return false;
        saverFeatures = noNfo == 1 ? (remote == 1 ? "NoNfoSaverOptimization,BlockRemoteFetcher" : "NoNfoSaverOptimization")
            : remote == 1 ? "BlockRemoteFetcher" : "";
        return true;
    }

    internal static bool TryGetNoPersistIntegrationSelection(string? features, out string saverFeatures)
    {
        saverFeatures = "";
        var selected = (features ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (selected.Count(name => string.Equals(name, "NoPersistIntegration", StringComparison.OrdinalIgnoreCase)) != 1 ||
            !selected.All(name => string.Equals(name, "NoPersistIntegration", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "NoNfoSaverOptimization", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "BlockRemoteFetcher", StringComparison.OrdinalIgnoreCase))) return false;
        // Neutral only after complete validation; preserve the existing 7/9 order and duplicate semantics.
        saverFeatures = string.Join(',', selected.Where(name => !string.Equals(name, "NoPersistIntegration", StringComparison.OrdinalIgnoreCase)));
        return true;
    }

    internal static bool ValidateFeatures(string? features, out string detail)
    {
        if (TryGetAlternativeSelection(features, out _))
        { detail = "ExtractAlternative with optional NoNfoSaverOptimization/BlockRemoteFetcher requires an active stop producer and bounded native public-refresh consumer; remote combinations also require a healthy remote owner and nonempty native image-provider inventory."; return true; }
        if (TryGetNoPersistIntegrationSelection(features, out _))
        { detail = "NoPersistIntegration disables automatic persistence/restore integration only; explicit media APIs and extraction gates remain active. Optional 7/9 consumers must be independently active; combinations with ExtractAlternative remain unsupported."; return true; }
        var selected = (features ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (selected.All(name => string.Equals(name, "NoNfoSaverOptimization", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "BlockRemoteFetcher", StringComparison.OrdinalIgnoreCase)))
        { detail = selected.Length == 0 ? "Default exclusive gate." : "Selected bounded saver/Series-Season-MusicAlbum-BoxSet remote-fetcher consumers must be independently active."; return true; }
        var reasons = selected.Distinct(StringComparer.Ordinal).Select(name => name + ": " + (name switch
        {
            "CatchAllAllow" => "not implemented; needs item/refresh/persistence eligibility, not unconditional pass-through",
            "CatchAllAllowChapterImage" => "not implemented; also needs native chapter eligibility and extraction context",
            "CatchAllBlock" => "not implemented; retains original conditional item/persistence exceptions",
            "UnconditionalBlock" => "not implemented; distinct provider-pre-refresh branch, not an alias for CatchAllBlock",
            "BlockRemoteFetcher" => "implemented only for independently verified parented Series/Season/MusicAlbum/BoxSet provider eligibility", 
            "ExtractOnFileChange" => "not implemented; requires observed file-change refresh context and queue integration",
            "IgnoreFileChange" => "not implemented; requires file-change refresh context",
            "IgnoreExtSubChange" => "not implemented; requires external-subtitle change context",
            "NoPersistIntegration" => "bounded automatic native-success restore opt-out, not manual APIs; supported exactly once with optional NoNfoSaverOptimization/BlockRemoteFetcher only",
            "NoNfoSaverOptimization" => "implemented only with the independently verified typed-refresh saver consumer",
            "ExtractAlternative" => "supported with optional NoNfoSaverOptimization/BlockRemoteFetcher and an active bounded Movie/local-STRM stop consumer; remote combinations additionally require healthy remote ownership and native image-provider inventory",
            _ => "unknown option"
        }));
        detail = "Exclusive gate inactive; native behavior retained. Unsupported ExclusiveControlFeatures: " + string.Join("; ", reasons);
        return false;
    }

    internal static MethodInfo[] GetTargets(Assembly assembly)
    {
        var targets = new List<MethodInfo>();
        foreach (var pair in new[] { (typeof(IMediaProbeManager), ProbeType), (typeof(IImageExtractionManager), ImageType) })
        {
            var type = assembly.GetType(pair.Item2, true)!;
            var map = type.GetInterfaceMap(pair.Item1);
            for (var i = 0; i < map.TargetMethods.Length; i++)
            {
                var target = map.TargetMethods[i];
                var contract = map.InterfaceMethods[i];
                if (target.ReturnType != contract.ReturnType || target.Name != contract.Name ||
                    !target.GetParameters().Select(p => p.ParameterType).SequenceEqual(contract.GetParameters().Select(p => p.ParameterType)))
                    throw new NotSupportedException("Host public interface implementation differs.");
                targets.Add(target);
            }
        }
        if (targets.Count != Fingerprints.Count || targets.Select(m => m.MetadataToken).Distinct().Count() != Fingerprints.Count)
            throw new NotSupportedException("Host extraction entrypoint count differs.");
        return targets.ToArray();
    }

    // Shared predicate permits independent PEReader verification without bootstrapping host services.
    internal static bool VerifyFingerprint(MethodInfo method, byte[]? il, int stack, bool initLocals, int locals, int exceptions)
    {
        if (!Fingerprints.TryGetValue(method.MetadataToken, out var expected)) return false;
        var assembly = method.DeclaringType?.Assembly.GetName();
        var parameters = method.GetParameters();
        return assembly?.Name == HostAssembly && assembly.Version == ExpectedVersion && method.Module.ModuleVersionId == ExpectedMvid &&
            method.DeclaringType?.FullName == (method.MetadataToken == 0x06000227 ? ProbeType : ImageType) &&
            method.IsPublic && !method.IsStatic && !method.IsAbstract && method.Name == expected.Name && parameters.Length == expected.Arity &&
            parameters[^1].ParameterType == typeof(CancellationToken) && parameters[^1].Name == "cancellationToken" &&
            il?.Length == expected.Length && stack == expected.Stack && initLocals && locals == expected.Locals && exceptions == 0 &&
            Convert.ToHexString(SHA256.HashData(il)) == expected.Hash;
    }

    internal static MethodInfo PrefixFor(Type result) => typeof(ExclusiveMediaControl).GetMethod(
        result == typeof(Task<MediaInfo>) ? nameof(ProbePrefix) : result == typeof(Task<string>) ? nameof(ImagePrefix) :
        result == typeof(Task<List<TimeSpan>>) ? nameof(ListPrefix) : result == typeof(Task) ? nameof(SeriesPrefix) :
        throw new NotSupportedException("Unexpected host extraction return contract."), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool ShouldBlock => enabled && !IsOwnedOperation;
    private static Exception Blocked() => new InvalidOperationException("Exclusive media extraction blocked a non-owned operation.");
    private static Task<T> BlockedTask<T>(CancellationToken token) => token.IsCancellationRequested ? Task.FromCanceled<T>(token) : Task.FromException<T>(Blocked());
    private static bool ProbePrefix(object __instance, MediaInfoRequest request, CancellationToken cancellationToken, ref Task<MediaInfo> __result)
    {
        if (!ShouldBlock) return true;
        if (NativeChangedProbeControl.TryConsume(__instance, request, cancellationToken)) return true;
        __result = BlockedTask<MediaInfo>(cancellationToken); return false;
    }
    private static bool ImagePrefix(CancellationToken cancellationToken, ref Task<string> __result)
    { if (!ShouldBlock) return true; __result = BlockedTask<string>(cancellationToken); return false; }
    private static bool ListPrefix(CancellationToken cancellationToken, ref Task<List<TimeSpan>> __result)
    { if (!ShouldBlock) return true; __result = BlockedTask<List<TimeSpan>>(cancellationToken); return false; }
    private static bool SeriesPrefix(CancellationToken cancellationToken, ref Task __result)
    { if (!ShouldBlock) return true; __result = cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.FromException(Blocked()); return false; }
}
