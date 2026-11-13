using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Dlna;
using System.Text.Json;

namespace StrmAssistantRebuild.Common;

/// <summary>Bounded 4.10.2 saver optimization for typed RefreshSingleItem/explicit refresh scopes.
/// Does not claim parity with the original plugin's opaque image/provider eligibility predicates.</summary>
public static class ExclusiveSaverControl
{
    private const string Owner = "StrmAssistantRebuild.ExclusiveSaver.4.10.2";
    private const string ProviderType = "Emby.Providers.Manager.ProviderManager";
    private const string NoNfo = "NoNfoSaverOptimization";
    private static readonly Version ExpectedVersion = new(4, 10, 2, 0);
    private static readonly Guid ExpectedMvid = new("29b37ec2-5f02-443c-a995-1f61c71f43c6");
    private static readonly object Gate = new();
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly List<MethodInfo> Installed = new();
    private static MethodInfo[] verifiedTargets = Array.Empty<MethodInfo>();
    private static Harmony? patcher;
    private static long generation;
    private static volatile bool enabled;
    private static bool noNfo;
    internal static volatile bool RemoteContextEnabled;
    public static bool IsEnabled => enabled;
    public static bool IsHealthy
    {
        get { lock (Gate) { try { return enabled && HealthyTypedPatches(); } catch { return false; } } }
    }
    public static string Detail { get; private set; } = "Disabled; native saver behavior unchanged.";
    private const string ExpectedSha256 = "16874EA37E2EC8152E7F2FA9F9D7FA51A284959DCE3A918694003D009B2B2A94";

    private static bool Report(bool success, string message, out string detail)
    { Detail = detail = message; return success; }

    // Actual shipped Providers PE: refresh boundary, common save entry, unchanged native eligibility.
    private static readonly Dictionary<int, (int Length, string Hash, int Stack, int Locals, int Exceptions)> Pins = new()
    {
        [0x0600023e] = (155, "AF42D79F530F87FDCC81AC817E573E1FF69DABA676AAFBEBB86439C508C2F249", 6, 0x110000bf, 0),
        [0x06000266] = (97, "09BFAA47E6A96CBB3B805771B66E29DB8306BC565F652F060A91B3EA54E0D731", 2, 0x110000d5, 0),
        [0x06000267] = (249, "2FE3C2E24A9C49C2E10E1052E8A0AA78728B9F358050133C1E4B5538DC32DA15", 7, 0x110000d6, 1)
    };

    /// <summary>Only NoNfo changes saver behavior; the independent remote-fetcher token is neutral here. Outer consumer validation stays separate.</summary>
    public static bool Configure(Version hostVersion, bool exclusiveEnabled, string? features, out string detail)
    {
        lock (Gate)
        {
            var selected = ExclusiveEligibility.Strip(features).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var supported = selected.All(x => string.Equals(x, NoNfo, StringComparison.OrdinalIgnoreCase) || string.Equals(x, "BlockRemoteFetcher", StringComparison.OrdinalIgnoreCase));
            var nextNoNfo = selected.Contains(NoNfo, StringComparer.OrdinalIgnoreCase);
            try
            {
                if (enabled && exclusiveEnabled && supported && hostVersion == ExpectedVersion && noNfo == nextNoNfo && HasExpectedPatches())
                { return Report(true, "Unchanged: healthy typed refresh saver optimization; scopes retained.", out detail); }
                enabled = false;
                Interlocked.Increment(ref generation);
                RemovePatches();
                if (!exclusiveEnabled) return Report(true, "Disabled; native saver behavior restored.", out detail);
                if (!supported) return Report(false, "Inactive: unsupported saver features: " + features, out detail);
                if (hostVersion != ExpectedVersion) return Report(false, "Inactive: requires exact host 4.10.2.0.", out detail);
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.Providers");
                if (assembly == null) return Report(false, "Inactive: Emby.Providers is not loaded.", out detail);
                // Whole PE pin includes async SaveMetadata.MoveNext, every EH/local signature and public caller.
                // No byte rewriting or assembly loading here; dynamic/in-memory hosts stay inactive.
                using (var stream = File.OpenRead(assembly.Location))
                using (var sha = SHA256.Create())
                    if (Convert.ToHexString(sha.ComputeHash(stream)) != ExpectedSha256)
                        return Report(false, "Inactive: complete Providers assembly digest differs.", out detail);
                var targets = verifiedTargets = GetTargets(assembly);
                foreach (var target in targets)
                {
                    if (!Verify(target)) return Report(false, "Inactive: signature/MVID/IL differs: " + target.Name, out detail);
                    if (Harmony.GetPatchInfo(target)?.Owners.Any() == true)
                        return Report(false, "Inactive: foreign patch owns " + target.Name, out detail);
                }
                patcher ??= new Harmony(Owner);
                Installed.Add(targets[0]);
                patcher.Patch(targets[0], prefix: Hook(nameof(RefreshPrefix)), postfix: Hook(nameof(RefreshPostfix)), finalizer: Hook(nameof(RefreshFinalizer)));
                Installed.Add(targets[1]);
                patcher.Patch(targets[1], prefix: Hook(nameof(SavePrefix)));
                noNfo = nextNoNfo;
                enabled = true;
                return Report(true, "Enabled: typed RefreshSingleItem/explicit scopes; common native SaveMetadata entry. " +
                    (noNfo ? "NoNfo skips only final no-fetcher stage." : "Default no-fetcher optimization active."), out detail);
            }
            catch (Exception error)
            {
                enabled = false;
                Interlocked.Increment(ref generation); // A failed health check also revokes captured scopes.
                detail = "Inactive: " + error.GetType().Name + ": " + error.Message;
                try { RemovePatches(); } catch { detail += "; owned cleanup pending (behavior disabled)."; }
                Detail = detail;
                return false;
            }
        }
    }

    public static void Disable()
    {
        lock (Gate)
        {
            enabled = false;
            Interlocked.Increment(ref generation);
            Detail = "Disabled; native saver behavior restored.";
            nativeRestore = null; restoreFailure = null;
            try { RemovePatches(); } catch { Detail += " Owned cleanup pending (behavior disabled)."; }
        }
    }

    /// <summary>Item identity is independent of ExclusiveMediaControl's boolean extraction ownership.</summary>
    public static IDisposable EnterOwnedItem(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Enter(item.InternalId, null);
    }

    /// <summary>Use only around a complete awaited operation with its actual typed options, not a queued future refresh.</summary>
    public static IDisposable EnterRefresh(BaseItem item, MetadataRefreshOptions refresh, LibraryOptions libraryOptions)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(libraryOptions);
        return EnterRefreshCore(item, refresh, libraryOptions, false);
    }

    private static Scope EnterRefreshCore(BaseItem item, MetadataRefreshOptions refresh, LibraryOptions libraryOptions, bool nativeOrigin)
    {
        var snapshot = new RefreshContext(item.InternalId, item.DateLastRefreshed == DateTimeOffset.MinValue,
            refresh.MetadataRefreshMode <= MetadataRefreshMode.Default && refresh.ImageRefreshMode <= MetadataRefreshMode.Default,
            libraryOptions.TypeOptions?.Any(o => o != null && o.Type == item.GetType().Name && o.MetadataFetchers?.Any() == true) == true);
        // Explicit scopes keep their existing saver-only meaning. Only the actual public native entry
        // creates this envelope; neither metadata enumeration nor EnterRefresh can manufacture readiness.
        if (nativeOrigin) { snapshot.Item = item; snapshot.Options = refresh; }
        if (nativeOrigin && RemoteContextEnabled && (item.GetType() == typeof(Movie) || item.GetType() == typeof(Episode) || item.GetType() == typeof(Audio)))
            try { snapshot.Remote = new RemoteRefresh(item, refresh, libraryOptions); }
            catch { /* An optional new consumer must never fault the existing saver/native refresh. */ }
        return Enter(0, snapshot);
    }

    private sealed record RefreshContext(long ItemId, bool IsNew, bool Ordinary, bool HasFetchers)
    { internal RemoteRefresh? Remote; internal Func<Task>? SuccessTail; internal BaseItem? Item; internal MetadataRefreshOptions? Options; internal ExclusiveFileChange.State? FileChange; }
    /// <summary>M156: the innermost live native RefreshSingleItem envelope (item, typed options), for original eligibility rules.</summary>
    internal static (BaseItem Item, MetadataRefreshOptions Options, bool IsNew, bool Ordinary, ExclusiveFileChange.State? FileChange)? CurrentNativeRefresh
    {
        get
        {
            for (var scope = LiveScope(); scope != null; scope = scope.Parent)
                if (scope.Refresh is { Item: { } item, Options: { } options } r) return (item, options, r.IsNew, r.Ordinary, r.FileChange);
            return null;
        }
    }

    private static Func<BaseItem, CancellationToken, Func<bool>, Task<bool>>? nativeRestore;
    private static Action<long, Exception>? restoreFailure;
    private static string? restoreConfigurationKey;
    /// <summary>Optional ordinary native-success consumer. Rebinding revokes old scopes, not native Tasks.</summary>
    public static void ConfigureNativeRestore(Func<BaseItem, CancellationToken, Func<bool>, Task<bool>>? restore,
        Action<long, Exception>? reportFailure, string? configurationKey = null)
    {
        lock (Gate)
        {
            if (nativeRestore != restore || restoreConfigurationKey != configurationKey) Interlocked.Increment(ref generation);
            nativeRestore = restore; restoreFailure = reportFailure; restoreConfigurationKey = configurationKey;
        }
    }

    private static void CaptureRestoreTail(Scope scope, BaseItem item, MetadataRefreshOptions options,
        LibraryOptions library, CancellationToken token)
    {
        var restore = nativeRestore;
        if (restore == null || scope.Parent != null || (item.GetType() != typeof(Movie) && item.GetType() != typeof(Episode) && item.GetType() != typeof(Audio)) ||
            scope.Refresh!.IsNew || !scope.Refresh.Ordinary || options.ReplaceAllMetadata || options.ReplaceAllImages ||
            options.OverwriteLocalMetadataProviderIds || options.SearchResult != null) return;
        var id = item.InternalId; var guid = item.Id; var path = item.Path; var parentId = item.ParentId;
        var episode = item as Episode;
        var episodeIdentity = (episode?.SeriesId, episode?.InternalAlbumId); // Raw links; zero is a valid native fallback.
        var audio = item as Audio;
        var audioAlbumId = audio?.InternalAlbumId;
        var directory = options.DirectoryService; var metadata = options.MetadataRefreshMode; var image = options.ImageRefreshMode;
        var forceSave = options.ForceSave;
        if (id <= 0 || guid == Guid.Empty || string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) ||
            item.ExtraType.HasValue || !item.IsFileProtocol) return;
        var parent = item.Parent;
        if (parent == null) return;
        bool Valid()
        {
            if (token.IsCancellationRequested || nativeRestore != restore || !enabled || !HealthyTypedPatches() ||
                !ReferenceEquals(LiveScope(), scope) || !ReferenceEquals(item.Parent, parent) || !item.IsFileProtocol) return false;
            // Recheck identity after native getters; DateLastRefreshed legitimately changes on success.
            return item.InternalId == id && item.Id == guid && item.Path == path && item.ParentId == parentId && !item.ExtraType.HasValue &&
                (episode?.SeriesId, episode?.InternalAlbumId) == episodeIdentity &&
                audio?.InternalAlbumId == audioAlbumId &&
                ReferenceEquals(options.DirectoryService, directory) && options.MetadataRefreshMode == metadata && options.ImageRefreshMode == image &&
                options.ForceSave == forceSave && !options.ReplaceAllMetadata && !options.ReplaceAllImages &&
                !options.OverwriteLocalMetadataProviderIds && options.SearchResult == null &&
                enabled && HealthyTypedPatches() && ReferenceEquals(LiveScope(), scope) && nativeRestore == restore;
        }
        if (!Valid()) return;
        var report = restoreFailure;
        scope.Refresh.SuccessTail = async () =>
        {
            token.ThrowIfCancellationRequested();
            try { if (Valid()) await restore(item, token, Valid).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); throw; }
            catch (Exception error) { try { report?.Invoke(id, error); } catch { } }
            token.ThrowIfCancellationRequested();
        };
    }

    // This consumer needs typed identity, not file-change state. No file/stream reads or unused 0 framework.
    private sealed class RemoteRefresh
    {
        internal readonly BaseItem Item;
        internal readonly Guid Guid;
        internal readonly long Id;
        private readonly long parentId;
        private readonly MediaBrowser.Model.Entities.ExtraType? extraType;
        private readonly bool wasNew;
        private BaseItem? parentWitness;
        internal readonly string Path;
        internal readonly MetadataRefreshOptions Options;
        internal readonly LibraryOptions Library;
        private readonly object? directory;
        private readonly MetadataRefreshMode metadataMode, imageMode;
        private readonly bool replaceMetadata, replaceImages, overwrite, forceSave;
        private readonly object? searchResult;
        private readonly TypeOptions[]? typeArray;
        private readonly (TypeOptions? Option, string? Type, string[]? Array, string[]? Values)[] typeSnapshot;
        internal bool Attempted, Ready;
        internal long RemoteEpoch;
        internal RemoteRefresh(BaseItem item, MetadataRefreshOptions options, LibraryOptions library)
        {
            Item = item; Guid = item.Id; Id = item.InternalId; Path = item.Path;
            parentId = item.ParentId; extraType = item.ExtraType; wasNew = item.DateLastRefreshed == DateTimeOffset.MinValue;
            Options = options; Library = library; directory = options.DirectoryService;
            metadataMode = options.MetadataRefreshMode; imageMode = options.ImageRefreshMode;
            replaceMetadata = options.ReplaceAllMetadata; replaceImages = options.ReplaceAllImages;
            overwrite = options.OverwriteLocalMetadataProviderIds; forceSave = options.ForceSave; searchResult = options.SearchResult;
            typeArray = library.TypeOptions;
            typeSnapshot = (typeArray ?? Array.Empty<TypeOptions>()).Select(o => ((TypeOptions?)o, o?.Type, o?.MetadataFetchers, o?.MetadataFetchers?.ToArray())).ToArray();
        }
        internal bool Matches(BaseItem item, LibraryOptions library, ImageRefreshOptions? options = null)
        {
            if (!ReferenceEquals(item, Item) || item.Id != Guid || item.InternalId != Id || item.Path != Path ||
                item.ParentId != parentId || item.ExtraType != extraType || (item.DateLastRefreshed == DateTimeOffset.MinValue) != wasNew ||
                !ReferenceEquals(library, Library) || (options != null && !ReferenceEquals(options, Options)) ||
                !ReferenceEquals(directory, Options.DirectoryService) || Options.MetadataRefreshMode != metadataMode ||
                Options.ImageRefreshMode != imageMode || Options.ReplaceAllMetadata != replaceMetadata ||
                Options.ReplaceAllImages != replaceImages || Options.OverwriteLocalMetadataProviderIds != overwrite ||
                Options.ForceSave != forceSave || !ReferenceEquals(Options.SearchResult, searchResult) ||
                !ReferenceEquals(typeArray, library.TypeOptions)) return false;
            for (var i = 0; i < typeSnapshot.Length; i++)
            {
                var saved = typeSnapshot[i]; var current = typeArray![i];
                if (!ReferenceEquals(current, saved.Option) || current?.Type != saved.Type ||
                    !ReferenceEquals(current?.MetadataFetchers, saved.Array) ||
                    (saved.Values != null && !saved.Values.SequenceEqual(current!.MetadataFetchers))) return false;
            }
            return true;
        }
        internal bool FullMetadataValidationOnly => metadataMode == MetadataRefreshMode.FullRefresh && imageMode == MetadataRefreshMode.ValidationOnly;
        internal bool Bounded() => Id > 0 && Guid != System.Guid.Empty && !string.IsNullOrWhiteSpace(Path) &&
            System.IO.Path.IsPathRooted(Path) && (Item.GetType() == typeof(Movie) || Item.GetType() == typeof(Episode) || Item.GetType() == typeof(Audio)) &&
            ((metadataMode <= MetadataRefreshMode.Default && imageMode <= MetadataRefreshMode.Default) || FullMetadataValidationOnly) &&
            !replaceMetadata && !replaceImages && !overwrite && searchResult == null &&
            !Item.ExtraType.HasValue && Item.IsFileProtocol && SameParent();
        private bool SameParent()
        {
            var parent = Item.Parent;
            if (parent == null) return false;
            if (parentWitness == null) parentWitness = parent; // Late native resolution, not a second entry lookup.
            return ReferenceEquals(parentWitness, parent);
        }
    }

    private static Scope? RemoteScope(BaseItem item)
    {
        Scope? refresh = null;
        var sawOwned = false;
        long ownedId = 0;
        for (var scope = LiveScope(); scope != null; scope = scope.Parent)
        {
            if (!sawOwned && scope.Refresh == null) { sawOwned = true; ownedId = scope.OwnedItemId; }
            refresh ??= scope.Refresh != null ? scope : null; // Never borrow an outer refresh over the nearest one.
        }
        return ownedId != 0 && ownedId == item.InternalId ? null : refresh;
    }

    internal static void CaptureRemoteRefresh(BaseItem item, LibraryOptions library, ImageRefreshOptions options, long remoteEpoch)
    {
        RemoteRefresh? context = null;
        try
        {
            var scope = RemoteScope(item);
            context = scope?.Refresh?.Remote;
            if (context == null) return;
            lock (context)
            {
                if (context.Attempted) { if (!context.Matches(item, library, options)) context.Ready = false; return; }
                context.Attempted = true; // An invalid first observation never recaptures a replacement identity.
                if (!enabled || !HealthyTypedPatches() || !context.Matches(item, library, options) || !context.Bounded()) return;
                // IsFileProtocol/Parent may call services. Revalidate after those callbacks, including both epochs.
                context.Ready = enabled && HealthyTypedPatches() && ReferenceEquals(scope, RemoteScope(item)) && context.Matches(item, library, options);
                context.RemoteEpoch = remoteEpoch;
            }
        }
        catch { if (context != null) { context.Attempted = true; context.Ready = false; } }
    }

    // A revoked/invalid scope is not an absent scope. This direct query never creates refresh authority.
    private static bool CanBlockUnscopedMetadata(BaseItem item)
    {
        if (!enabled || Current.Value != null || !HealthyTypedPatches() || item == null) return false;
        var epoch = Volatile.Read(ref generation);
        var type = item.GetType();
        if (type != typeof(Movie) && type != typeof(Episode) && type != typeof(Audio)) return false;
        var id = item.InternalId; var guid = item.Id; var path = item.Path;
        var parentId = item.ParentId; var extra = item.ExtraType;
        if (id <= 0 || guid == Guid.Empty || extra.HasValue || string.IsNullOrWhiteSpace(path) ||
            !System.IO.Path.IsPathRooted(path) || !item.IsFileProtocol) return false;
        var parent = item.Parent; // Prefer the native cached/lookup path; no new file or restore reads.
        if (parent == null || !ReferenceEquals(parent, item.Parent)) return false;
        // Both getters may call services. Check the actual current value and both identities afterwards.
        return enabled && HealthyTypedPatches() && epoch == Volatile.Read(ref generation) && Current.Value == null &&
            item.InternalId == id && item.Id == guid && item.Path == path && item.ParentId == parentId && item.ExtraType == extra;
    }

    internal static bool CanBlockRemoteMetadata(BaseItem item, LibraryOptions library, long remoteEpoch, bool allowFull)
    {
        RemoteRefresh? context = null;
        try
        {
            if (Current.Value == null) return CanBlockUnscopedMetadata(item);
            var scope = RemoteScope(item);
            context = scope?.Refresh?.Remote;
            if (context == null) return false;
            lock (context)
            {
                if (!context.Ready) return false;
                var valid = enabled && HealthyTypedPatches() && context.RemoteEpoch == remoteEpoch &&
                    context.Matches(item, library) && context.Bounded() && enabled && HealthyTypedPatches() &&
                    ReferenceEquals(scope, RemoteScope(item)) && context.Matches(item, library);
                if (!valid) context.Ready = false;
                // A dual-interface provider is ineligible only for this extra mode, not a revoked scope.
                return valid && (allowFull || !context.FullMetadataValidationOnly);
            }
        }
        catch { if (context != null) context.Ready = false; return false; }
    }

    private static Scope Enter(long ownedItemId, RefreshContext? refresh)
    {
        var scope = new Scope(LiveScope(), Volatile.Read(ref generation), ownedItemId, refresh);
        Current.Value = scope;
        return scope;
    }

    private static long nativeEntries, nativeContexts;
    /// <summary>M165 diagnostic: native RefreshSingleItem entries seen by the saver hook, and those that created a context.</summary>
    public static long NativeEntries => Interlocked.Read(ref nativeEntries);
    public static long NativeContexts => Interlocked.Read(ref nativeContexts);
    /// <summary>M165 diagnostic: why the current flow has no live native refresh context.</summary>
    internal static string DescribeScope()
    {
        var scope = Current.Value;
        if (scope == null) return "no scope in this flow";
        var epoch = Volatile.Read(ref generation);
        var depth = 0;
        for (var cursor = scope; cursor != null; cursor = cursor.Parent, depth++)
        {
            if (Volatile.Read(ref cursor.Disposed) != 0) return "disposed scope at depth " + depth + (cursor.Refresh?.Item != null ? " (native refresh of " + cursor.Refresh.ItemId + ")" : "");
            if (cursor.Epoch != epoch) return "stale epoch " + cursor.Epoch + " at depth " + depth + " (current " + epoch + ")";
        }
        return "live chain of " + depth + " without a native refresh";
    }
    private static Scope? LiveScope()
    {
        var scope = Current.Value;
        var epoch = Volatile.Read(ref generation);
        for (var cursor = scope; cursor != null; cursor = cursor.Parent)
            if (Volatile.Read(ref cursor.Disposed) != 0 || cursor.Epoch != epoch) return null;
        return scope;
    }

    // Opaque optional consumer state shares the existing native refresh epoch/lifetime.
    internal static object? NativeChangedState => LiveScope()?.NativeChangedState;
    internal static IDisposable? AttachNativeChangedFetch(object expected, object witness, out Action? detach)
    {
        detach = null;
        var parent = LiveScope();
        if (parent == null || parent.Parent != null || !ReferenceEquals(parent.NativeChangedState, expected)) return null;
        var scope = new Scope(parent, parent.Epoch, 0, null) { NativeChangedState = witness };
        Current.Value = scope;
        detach = scope.Detach;
        return scope;
    }

    private sealed class Scope : IDisposable
    {
        internal readonly Scope? Parent;
        internal readonly long Epoch, OwnedItemId;
        internal readonly RefreshContext? Refresh;
        internal int Disposed;
        internal object? NativeChangedState;
        internal Scope(Scope? parent, long epoch, long ownedItemId, RefreshContext? refresh)
        { Parent = parent; Epoch = epoch; OwnedItemId = ownedItemId; Refresh = refresh; }
        internal void Detach()
        {
            if (!ReferenceEquals(Current.Value, this)) return;
            var parent = Parent;
            while (parent != null && Volatile.Read(ref parent.Disposed) != 0) parent = parent.Parent;
            Current.Value = parent;
        }
        public void Dispose() { Interlocked.Exchange(ref Disposed, 1); Detach(); }
    }

    private static void RefreshPrefix(object __instance, BaseItem item, MetadataRefreshOptions options, BaseItem[] __2, LibraryOptions libraryOptions, CancellationToken __4, out Scope? __state)
    {
        __state = null;
        Interlocked.Increment(ref nativeEntries);
        if (enabled && item != null && options != null && libraryOptions != null)
        {
            Interlocked.Increment(ref nativeContexts);
            var canRestore = Current.Value == null && !ExclusiveMediaControl.IsOwnedOperation;
            var canProbe = Current.Value == null && !ExclusiveMediaControl.HasRawOwnedScope;
            __state = EnterRefreshCore(item, options, libraryOptions, true);
            try { if (canRestore && __state.Refresh != null) __state.Refresh.FileChange = ExclusiveFileChange.Begin(item, options, __state.Refresh.IsNew, __4); }
            catch (OperationCanceledException) { throw; }
            catch { /* Optional file-change handling never faults the native refresh. */ }
            try { if (canProbe) __state.NativeChangedState = NativeChangedProbeControl.Capture(__instance, item, options, __2, libraryOptions, __4); }
            catch { /* Optional probe capture leaves the global block in place. */ }
            try { if (canRestore) CaptureRestoreTail(__state, item, options, libraryOptions, __4); }
            catch { /* Optional capture/getter failure leaves the original native refresh running. */ }
        }
    }

    private static void RefreshPostfix(ref Task<ItemUpdateType> __result, Scope? __state)
    {
        if (__state == null) return;
        // Restore caller now; keep shared scope alive until the returned native Task actually finishes.
        __result = FinishRefresh(__result, __state);
        __state.Detach();
    }

    private static async Task<ItemUpdateType> FinishRefresh(Task<ItemUpdateType> task, Scope scope)
    {
        try
        {
            var result = await task.ConfigureAwait(false); // Native fault/cancellation never enters the tail.
            if (scope.Refresh?.SuccessTail is { } tail) await tail().ConfigureAwait(false);
            if (scope.Refresh is { FileChange: { } change, Item: { } changed }) await ExclusiveFileChange.Complete(changed, change, CancellationToken.None).ConfigureAwait(false);
            return result; // Deliberately awaited, unlike the original completion's discarded Task.
        }
        finally { scope.Dispose(); }
    }

    private static void RefreshFinalizer(Exception? __exception, Scope? __state)
    { if (__exception != null) __state?.Dispose(); }

    private static void SavePrefix(BaseItem item, ref ItemUpdateType updateType)
    {
        if (!enabled || item == null || (updateType & ItemUpdateType.MetadataDownload) == 0) return;
        long ownedItemId = 0;
        var sawOwned = false;
        RefreshContext? refresh = null;
        for (var scope = LiveScope(); scope != null; scope = scope.Parent)
        {
            if (!sawOwned && scope.Refresh == null) { ownedItemId = scope.OwnedItemId; sawOwned = true; }
            refresh ??= scope.Refresh;
        }
        if (ownedItemId != 0 && ownedItemId == item.InternalId)
            updateType &= ~ItemUpdateType.MetadataDownload;
        if (refresh == null || refresh.ItemId != item.InternalId) return;
        if ((!refresh.IsNew && refresh.Ordinary) || (!noNfo && !refresh.HasFetchers))
            updateType &= ~ItemUpdateType.MetadataDownload;
        // No saver.Name gate and no forced eligibility. Native IsEnabledFor/configuration/Save run unchanged.
        // Snapshot here: LibraryManager does not await SaveMetadata; later savers must retain the same flags.
    }

    private static bool HasExpectedPatches()
    {
        if (verifiedTargets.Length != 3 || Installed.Count != 2 ||
            Installed[0] != verifiedTargets[0] || Installed[1] != verifiedTargets[1]) return false;
        var refresh = Harmony.GetPatchInfo(verifiedTargets[0]);
        var save = Harmony.GetPatchInfo(verifiedTargets[1]);
        // Eligibility is pinned but intentionally unpatched; a late owner here changes saver semantics too.
        if (Harmony.GetPatchInfo(verifiedTargets[2])?.Owners.Any() == true || refresh == null || save == null) return false;
        return Exact(refresh.Prefixes, nameof(RefreshPrefix)) && Exact(refresh.Postfixes, nameof(RefreshPostfix)) &&
            Exact(refresh.Finalizers, nameof(RefreshFinalizer)) && refresh.Transpilers.Count == 0 &&
            Exact(save.Prefixes, nameof(SavePrefix)) && save.Postfixes.Count == 0 && save.Finalizers.Count == 0 && save.Transpilers.Count == 0;
    }

    private static bool HealthyTypedPatches() => HasExpectedPatches() && verifiedTargets.All(target =>
    {
        var info = Harmony.GetPatchInfo(target);
        return info == null || (info.InnerPrefixes.Count == 0 && info.InnerPostfixes.Count == 0 &&
            info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).All(p => p.priority == Priority.Normal && p.before.Length == 0 && p.after.Length == 0 && !p.debug));
    });

    private static bool Exact(IReadOnlyCollection<Patch> patches, string name) => patches.Count == 1 &&
        patches.Single().owner == Owner && patches.Single().PatchMethod ==
        typeof(ExclusiveSaverControl).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

    private static HarmonyMethod Hook(string name) => new(typeof(ExclusiveSaverControl).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!);
    private static void RemovePatches()
    {
        for (var i = Installed.Count - 1; i >= 0; i--)
        {
            patcher?.Unpatch(Installed[i], HarmonyPatchType.All, Owner);
            Installed.RemoveAt(i);
        }
    }

    internal static MethodInfo[] GetTargets(Assembly assembly)
    {
        var type = assembly.GetType(ProviderType, true)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return new[]
        {
            type.GetMethod("RefreshSingleItem", flags, null, new[] { typeof(BaseItem), typeof(MetadataRefreshOptions), typeof(BaseItem[]), typeof(LibraryOptions), typeof(CancellationToken) }, null)!,
            type.GetMethod("SaveMetadata", flags, null, new[] { typeof(BaseItem), typeof(LibraryOptions), typeof(ItemUpdateType), typeof(IMetadataSaver[]), typeof(CancellationToken) }, null)!,
            type.GetMethod("IsSaverEnabledForItem", flags, null, new[] { typeof(IMetadataSaver), typeof(BaseItem), typeof(LibraryOptions), typeof(ItemUpdateType), typeof(bool), typeof(bool) }, null)!
        };
    }

    internal static bool Verify(MethodInfo method)
    {
        if (method == null || !Pins.TryGetValue(method.MetadataToken, out var pin)) return false;
        var assembly = method.Module.Assembly.GetName();
        var parameterTypes = method.GetParameters().Select(p => p.ParameterType);
        var expectedTypes = method.MetadataToken switch
        {
            0x0600023e => new[] { typeof(BaseItem), typeof(MetadataRefreshOptions), typeof(BaseItem[]), typeof(LibraryOptions), typeof(CancellationToken) },
            0x06000266 => new[] { typeof(BaseItem), typeof(LibraryOptions), typeof(ItemUpdateType), typeof(IMetadataSaver[]), typeof(CancellationToken) },
            _ => new[] { typeof(IMetadataSaver), typeof(BaseItem), typeof(LibraryOptions), typeof(ItemUpdateType), typeof(bool), typeof(bool) }
        };
        var expectedName = method.MetadataToken == 0x0600023e ? "RefreshSingleItem" : method.MetadataToken == 0x06000266 ? "SaveMetadata" : "IsSaverEnabledForItem";
        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        return assembly.Name == "Emby.Providers" && assembly.Version == ExpectedVersion && method.Module.ModuleVersionId == ExpectedMvid &&
            method.DeclaringType?.FullName == ProviderType && !method.IsStatic && !method.IsAbstract && !method.ContainsGenericParameters &&
            method.Name == expectedName && parameterTypes.SequenceEqual(expectedTypes) &&
            method.IsPublic == (method.MetadataToken == 0x0600023e) &&
            method.ReturnType == (method.MetadataToken == 0x0600023e ? typeof(Task<ItemUpdateType>) : method.MetadataToken == 0x06000266 ? typeof(Task) : typeof(bool)) &&
            il?.Length == pin.Length && body!.MaxStackSize == pin.Stack && body.InitLocals &&
            body.LocalSignatureMetadataToken == pin.Locals && body.ExceptionHandlingClauses.Count == pin.Exceptions &&
            Convert.ToHexString(SHA256.HashData(il)) == pin.Hash;
    }
}

// Default !0 && changed eligibility mapped to the reconstruction's global gate, not feature 1/0.
// ponytail: one Movie public Task patch; no second AsyncLocal, queue, clear, or generic MoveNext detour.
internal static class NativeChangedProbeControl
{
    private const string Owner = "StrmAssistantRebuild.NativeChangedProbe.4.10.2";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly object Gate = new();
    private static readonly Harmony Patcher = new(Owner);
    private static MethodInfo? target;
    private static Binding? binding;
    internal static string Detail { get; private set; } = "Disabled: bounded native changed probe.";

    // Values, not an alternative options model. Production supplies its actual current store.
    internal static bool Configure(Version version, IProviderManager? manager, ILibraryManager? library, IFileSystem? files,
        Func<(bool Exclusive, string? Features, string? Persist)>? options, out string detail)
    {
        lock (Gate)
        {
            DisableCore(); // Every configuration save revokes this consumer's old binding, not owned scopes.
            if (target != null) return Report(false, "Inactive: owned Movie patch cleanup pending.", out detail);
            try
            {
                if (version != new Version(4, 10, 2, 0) || manager == null || library == null || files == null || options == null ||
                    !Selected(options())) return Report(false, "Inactive: default exclusive selection with None/Default required.", out detail);
                var assembly = manager.GetType().Assembly;
                if (manager.GetType().FullName != "Emby.Providers.Manager.ProviderManager" ||
                    !FilePin(assembly, "16874EA37E2EC8152E7F2FA9F9D7FA51A284959DCE3A918694003D009B2B2A94") ||
                    !FilePin(typeof(BaseItem).Assembly, "EAA6B9EDEBB21FE3AF9C272DEF8C913D9EC6B177B552E1B789FEE15CB59289DA") ||
                    !FilePin(typeof(MediaSourceInfo).Assembly, "2B7DA29239811F8C035784DF7B3F48A1668E9537784B6524DDE43B3905572617"))
                    return Report(false, "Inactive: native complete PE binding differs.", out detail);
                var type = assembly.GetType("Emby.Providers.MediaInfo.FFProbeProvider", true)!;
                var method = Target(assembly);
                if (method == null || !VerifyTarget(method) || Harmony.GetPatchInfo(method)?.Owners.Any() == true)
                    return Report(false, "Inactive: exact public Movie Fetch124 pin/ownership differs.", out detail);
                var next = new Binding(manager, library, files, options, type);
                if (!next.Registered() || !ExclusiveSaverControl.IsHealthy || !ExclusiveMediaControl.IsHealthy || !NativePathUnpatched(method))
                    return Report(false, "Inactive: registered native path or extraction/saver owner missing.", out detail);
                target = method;
                Patcher.Patch(method, prefix: Hook(nameof(FetchPrefix)), postfix: Hook(nameof(FetchPostfix)), finalizer: Hook(nameof(FetchFinalizer)));
                binding = next;
                if (!Healthy()) { DisableCore(); return Report(false, "Inactive: optional Movie owner health differs.", out detail); }
                return Report(true, "Enabled: bounded default changed physical Movie primary probe; None/Default; native media/chapter/date writes retained; no default clear or feature 0/1.", out detail);
            }
            catch (Exception e) { DisableCore(); return Report(false, "Inactive: bounded native probe " + e.GetType().Name, out detail); }
        }
    }
    internal static MethodInfo? Target(Assembly assembly) => assembly.GetType("Emby.Providers.MediaInfo.FFProbeProvider", false)?
        .GetMethod("FetchAsync", new[] { typeof(MetadataResult<Movie>), typeof(CustomMetadataProviderOptions), typeof(CancellationToken) });
    /// <summary>Exact public Movie Fetch124 pin (ownership is checked separately by Configure).</summary>
    internal static bool VerifyTarget(MethodInfo method)
    {
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        return method.MetadataToken == 0x06000124 && method.IsPublic && !method.IsStatic && !method.ContainsGenericParameters &&
            method.ReturnType == typeof(Task<ItemUpdateType>) && method.Module.ModuleVersionId == new Guid("29b37ec2-5f02-443c-a995-1f61c71f43c6") &&
            il?.Length == 27 && body!.MaxStackSize == 8 && !body.InitLocals && body.LocalSignatureMetadataToken == 0 && body.ExceptionHandlingClauses.Count == 0 &&
            Convert.ToHexString(SHA256.HashData(il)) == "93B7C8DA49BC25ABF3DDE6578EC9D5B8145CFA4D418AF1CC85D48FE3C634E9DF";
    }
    private static bool Report(bool ok, string message, out string detail) { Detail = detail = message; return ok; }
    internal static void Disable() { lock (Gate) DisableCore(); }
    private static void DisableCore()
    {
        binding = null;
        if (target != null) { try { Patcher.Unpatch(target, HarmonyPatchType.All, Owner); target = null; } catch { /* Retain the owned cleanup handle for the next attempt. */ } }
    }
    private static HarmonyMethod Hook(string name) => new(typeof(NativeChangedProbeControl).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!);
    private static bool FilePin(Assembly assembly, string hash)
    { using var stream = File.OpenRead(assembly.Location); using var sha = SHA256.Create(); return assembly.GetName().Version == new Version(4, 10, 2, 0) && Convert.ToHexString(sha.ComputeHash(stream)) == hash; }
    private static bool Selected((bool Exclusive, string? Features, string? Persist) o)
    {
        // IgnoreFileChange (0) turns the changed-file probe off, as in the original.
        if ((o.Features ?? "").Split(',', StringSplitOptions.TrimEntries).Contains("IgnoreFileChange", StringComparer.OrdinalIgnoreCase)) return false;
        var selected = ExclusiveEligibility.Strip(o.Features).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return o.Exclusive && (selected.Length == 0 || selected.Length == 1 &&
            string.Equals(selected[0], "NoPersistIntegration", StringComparison.OrdinalIgnoreCase)) && o.Persist is "None" or "Default";
    }
    private static bool Healthy()
    {
        var method = target; var b = binding;
        if (method == null || b == null || !b.Registered() || !Selected(b.Options()) || !ExclusiveSaverControl.IsHealthy || !ExclusiveMediaControl.IsHealthy) return false;
        var p = Harmony.GetPatchInfo(method);
        bool Exact(IReadOnlyCollection<Patch> patches, string name) => patches.Count == 1 && patches.Single().owner == Owner &&
            patches.Single().PatchMethod == Hook(name).method && patches.Single().priority == Priority.Normal &&
            patches.Single().before.Length == 0 && patches.Single().after.Length == 0 && !patches.Single().debug;
        return p != null && Exact(p.Prefixes, nameof(FetchPrefix)) && Exact(p.Postfixes, nameof(FetchPostfix)) && Exact(p.Finalizers, nameof(FetchFinalizer)) &&
            p.Transpilers.Count == 0 && p.InnerPrefixes.Count == 0 && p.InnerPostfixes.Count == 0 && NativePathUnpatched(method);
    }
    // The shipped native producer/getter path is trusted; equal DTOs from an arbitrary malicious
    // callback are not authenticated. Check late Harmony changes, not just initial disk bytes.
    private static bool NativePathUnpatched(MethodInfo fetch)
    {
        foreach (var m in Harmony.GetAllPatchedMethods())
        {
            if (m == fetch) continue;
            // Harmony retains method keys after unpatch; history with no active hooks is not ownership.
            var info = Harmony.GetPatchInfo(m);
            if (info == null || info.Prefixes.Count == 0 && info.Postfixes.Count == 0 && info.Transpilers.Count == 0 &&
                info.Finalizers.Count == 0 && info.InnerPrefixes.Count == 0 && info.InnerPostfixes.Count == 0) continue;
            var name = m.DeclaringType?.FullName ?? "";
            if (name.StartsWith("Emby.Providers.MediaInfo.", StringComparison.Ordinal) || name.StartsWith("Emby.Providers.Manager.MetadataService", StringComparison.Ordinal) ||
                name.StartsWith("Emby.Providers.Movies.MovieMetadataService", StringComparison.Ordinal) ||
                m.DeclaringType == typeof(BaseItem) || m.DeclaringType == typeof(Video) || m.DeclaringType == typeof(Movie) ||
                name.StartsWith("MediaBrowser.Controller.Providers.BaseMetadataResult", StringComparison.Ordinal) || name.StartsWith("MediaBrowser.Controller.Providers.MetadataResult", StringComparison.Ordinal) ||
                m.DeclaringType == typeof(MediaInfoRequest) || m.DeclaringType == typeof(MediaSourceInfo) || m.DeclaringType == typeof(CustomMetadataProviderOptions)) return false;
        }
        return true;
    }
    private sealed class Binding
    {
        internal readonly IProviderManager Manager; internal readonly ILibraryManager Library; internal readonly IFileSystem Files;
        internal readonly Func<(bool Exclusive, string? Features, string? Persist)> Options;
        internal readonly object Provider, Probe;
        private readonly FieldInfo providersField, servicesField, probeField, filesField, libraryField;
        private readonly IMetadataProvider[] providers, providerValues;
        private readonly object[] services, serviceValues;
        internal Binding(IProviderManager manager, ILibraryManager library, IFileSystem files, Func<(bool, string?, string?)> options, Type providerType)
        {
            Manager = manager; Library = library; Files = files; Options = options;
            providersField = manager.GetType().GetField("_metadataProviders", Fields)!;
            servicesField = manager.GetType().GetField("_metadataServices", Fields)!;
            providers = (IMetadataProvider[])providersField.GetValue(manager)!; providerValues = providers.ToArray();
            services = (object[])servicesField.GetValue(manager)!; serviceValues = services.ToArray();
            Provider = providers.Single(p => p.GetType() == providerType);
            probeField = providerType.GetField("_mediaProbeManager", Fields)!;
            filesField = providerType.GetField("_fileSystem", Fields)!;
            libraryField = providerType.GetField("_libraryManager", Fields)!;
            Probe = probeField.GetValue(Provider)!;
            if (Probe?.GetType().FullName != "Emby.Server.MediaEncoding.Probing.MediaProbeManager" ||
                Probe.GetType().Module.ModuleVersionId != new Guid("1a6398de-16b2-4e89-9e0c-2ed7d3859103")) throw new InvalidOperationException("Native probe DI differs");
            var movie = services.Single(s => s.GetType().FullName == "Emby.Providers.Movies.MovieMetadataService" && s.GetType().Assembly == providerType.Assembly);
            if (services.TakeWhile(s => !ReferenceEquals(s, movie)).Any(s => s.GetType().Assembly != providerType.Assembly)) throw new InvalidOperationException("Foreign service precedes Movie");
        }
        internal bool Registered() => ReferenceEquals(providersField.GetValue(Manager), providers) && Enumerable.SequenceEqual<IMetadataProvider>(providers, providerValues, ReferenceEqualityComparer.Instance) &&
            ReferenceEquals(servicesField.GetValue(Manager), services) && Enumerable.SequenceEqual<object>(services, serviceValues, ReferenceEqualityComparer.Instance) &&
            ReferenceEquals(probeField.GetValue(Provider), Probe) && ReferenceEquals(filesField.GetValue(Provider), Files) && ReferenceEquals(libraryField.GetValue(Provider), Library);
    }
    private sealed class Candidate
    {
        internal readonly Binding Binding; internal readonly Movie Item; internal readonly MetadataRefreshOptions Refresh; internal readonly LibraryOptions Library;
        internal readonly BaseItem[] Folders, FolderValues; internal readonly CancellationToken Token;
        internal readonly long Id, ParentId; internal readonly Guid Guid; internal readonly string Path, SourceId, Container;
        private readonly string sourcePrefix;
        private readonly bool externalIds;
        internal readonly DateTimeOffset Created, OldDate, Stamp; internal readonly long Length, MediaEpoch;
        internal readonly object Directory; internal readonly string RefreshKey, LibraryKey; internal readonly (bool, string?, string?) Options;
        internal int Attempted;
        internal Candidate(Binding b, Movie item, MetadataRefreshOptions refresh, BaseItem[] folders, LibraryOptions library, CancellationToken token, FileSystemMetadata file)
        {
            Binding = b; Item = item; Refresh = refresh; Folders = folders; FolderValues = folders.ToArray(); Library = library; Token = token; MediaEpoch = ExclusiveMediaControl.Epoch;
            Id = item.InternalId; Guid = item.Id; Path = item.Path; ParentId = item.ParentId; Created = item.DateCreated; OldDate = item.DateModified;
            Stamp = file.LastWriteTimeUtc; Length = file.Length; SourceId = item.GetDefaultMediaSourceId(); Directory = refresh.DirectoryService;
            Container = item.Container; sourcePrefix = BaseItem.MediaSourceIdPrefix; externalIds = item.EnableInternalIdsExternally;
            RefreshKey = OptionsKey(refresh); LibraryKey = JsonSerializer.Serialize(library); Options = b.Options();
        }
        internal bool Identity(BaseItem? item) => item?.GetType() == typeof(Movie) && item.InternalId == Id && item.Id == Guid && item.Path == Path &&
            item.ParentId == ParentId && item.DateCreated == Created && item.Parent != null && Physical((Movie)item);
        internal bool Stable()
        {
            if (!ReferenceEquals(binding, Binding) || !Healthy() || Token.IsCancellationRequested || ExclusiveMediaControl.HasRawOwnedScope || !Identity(Item) ||
                !Identity(Binding.Library.GetItemById(Id)) || Item.DateModified != OldDate && Item.DateModified != Stamp ||
                Item.GetDefaultMediaSourceId() != SourceId || Binding.Options() != Options || !Selected(Binding.Options()) ||
                !ReferenceEquals(Refresh.DirectoryService, Directory) || OptionsKey(Refresh) != RefreshKey ||
                JsonSerializer.Serialize(Library) != LibraryKey || Library.AutomaticRefreshIntervalDays > 0 || Library.PlaceholderMetadataRefreshIntervalDays > 0 ||
                !Enumerable.SequenceEqual<BaseItem>(Folders, FolderValues, ReferenceEqualityComparer.Instance)) return false;
            return SameFile(Binding.Files.GetFileInfo(Path)) && ReferenceEquals(binding, Binding) && Healthy() && SameValues();
        }
        // No service/stat/options-provider callback after this final value check.
        internal bool SameValues() => ReferenceEquals(binding, Binding) && MediaEpoch == ExclusiveMediaControl.Epoch && !Token.IsCancellationRequested && !ExclusiveMediaControl.HasRawOwnedScope &&
            Item.InternalId == Id && Item.Id == Guid && Item.Path == Path && Item.ParentId == ParentId && Item.DateCreated == Created &&
            Item.Container == Container && !Item.IsShortcut && !Item.IsVirtualItem && !Item.ExtraType.HasValue && Item.DateLastRefreshed != DateTimeOffset.MinValue &&
            BaseItem.MediaSourceIdPrefix == sourcePrefix && Item.EnableInternalIdsExternally == externalIds &&
            ReferenceEquals(Refresh.DirectoryService, Directory) && OptionsKey(Refresh) == RefreshKey && JsonSerializer.Serialize(Library) == LibraryKey &&
            Enumerable.SequenceEqual<BaseItem>(Folders, FolderValues, ReferenceEqualityComparer.Instance);
        internal bool SameFile(FileSystemMetadata? file) => file?.Exists == true && !file.IsDirectory && file.FullName == Path && file.Length == Length && file.LastWriteTimeUtc == Stamp;
    }
    private static string OptionsKey(MetadataRefreshOptions r) => JsonSerializer.Serialize(new {
        r.MetadataRefreshMode, r.ImageRefreshMode, r.ReplaceAllMetadata, r.ReplaceAllImages, r.ReplaceThumbnailImages,
        r.OverwriteLocalMetadataProviderIds, r.ForceSave, HasSearch = r.SearchResult != null, r.SearchResultApplied, r.SearchResultItemId,
        r.EnableRemoteContentProbe, r.EnableSubtitleDownloading, r.EnableThumbnailImageExtraction, r.UpdateMetadataFromRelatedItems,
        r.IsSecondPass, r.Recursive, r.IsAutomated, r.RefreshPaths });
    private static bool Physical(Movie item) => item.InternalId > 0 && item.Id != Guid.Empty && item.ParentId > 0 &&
        item.DateLastRefreshed != DateTimeOffset.MinValue && !item.ExtraType.HasValue && !item.IsShortcut && !item.IsVirtualItem && !item.IsPlaceHolder &&
        !item.IsResolvedToFolder && item.IsFileProtocol && item.PathProtocol == MediaProtocol.File &&
        !string.IsNullOrWhiteSpace(item.Path) && Path.IsPathRooted(item.Path) && !string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase) &&
        item.MediaContainer is not MediaContainers.Iso and not MediaContainers.Bluray and not MediaContainers.BlurayIso and not MediaContainers.Dvd and not MediaContainers.DvdIso;
    internal static object? Capture(object manager, BaseItem item, MetadataRefreshOptions refresh, BaseItem[] folders, LibraryOptions library, CancellationToken token)
    {
        var identity = (item.InternalId, item.Id, item.Path, item.ParentId, item.DateCreated);
        var old = item.DateModified;
        var b = binding;
        if (b == null || !ReferenceEquals(b.Manager, manager) || !Healthy() || token.IsCancellationRequested || item.GetType() != typeof(Movie) ||
            !Physical((Movie)item) || item.Parent == null || folders == null || refresh.DirectoryService == null ||
            refresh.MetadataRefreshMode > MetadataRefreshMode.Default || refresh.ImageRefreshMode > MetadataRefreshMode.Default ||
            refresh.ReplaceAllMetadata || refresh.ReplaceAllImages || refresh.ReplaceThumbnailImages || refresh.OverwriteLocalMetadataProviderIds || refresh.SearchResult != null ||
            library.AutomaticRefreshIntervalDays > 0 || library.PlaceholderMetadataRefreshIntervalDays > 0) return null;
        var file = b.Files.GetFileInfo(identity.Path);
        if ((item.InternalId, item.Id, item.Path, item.ParentId, item.DateCreated) != identity || item.DateModified != old || file?.Exists != true || file.IsDirectory || file.FullName != item.Path || file.Length < 0 || file.LastWriteTimeUtc <= old ||
            file.LastWriteTimeUtc.ToUnixTimeSeconds() == 0 || file.LastWriteTimeUtc.ToUnixTimeSeconds() <= old.ToUnixTimeSeconds()) return null;
        var c = new Candidate(b, (Movie)item, refresh, folders, library, token, file);
        return c.OldDate == old && !string.IsNullOrEmpty(c.SourceId) && c.Stable() ? c : null;
    }
    private sealed class FetchWitness
    {
        internal readonly Candidate Candidate; internal readonly MetadataResult<Movie> Result; internal readonly CustomMetadataProviderOptions Options;
        internal IDisposable? Scope; internal Action? Detach; internal int Consumed, Closed;
        internal FetchWitness(Candidate c, MetadataResult<Movie> result, CustomMetadataProviderOptions options) { Candidate = c; Result = result; Options = options; }
        internal void Close() { Interlocked.Exchange(ref Closed, 1); Scope?.Dispose(); }
    }
    private static void FetchPrefix(object __instance, MetadataResult<Movie> itemResult, CustomMetadataProviderOptions options, CancellationToken cancellationToken, out FetchWitness? __state)
    {
        __state = null;
        try
        {
            if (ExclusiveSaverControl.NativeChangedState is not Candidate c || !ReferenceEquals(__instance, c.Binding.Provider) ||
                !ReferenceEquals(itemResult?.Item, c.Item) || !ReferenceEquals(options?.MetadataRefreshOptions, c.Refresh) ||
                !ReferenceEquals(options?.LibraryOptions, c.Library) || !ReferenceEquals(options?.CollectionFolders, c.Folders) ||
                cancellationToken != c.Token || !c.Stable() || Interlocked.CompareExchange(ref c.Attempted, 1, 0) != 0) return;
            var w = new FetchWitness(c, itemResult!, options!);
            w.Scope = ExclusiveSaverControl.AttachNativeChangedFetch(c, w, out w.Detach);
            if (w.Scope != null) __state = w;
        }
        catch { /* Native Fetch still executes behind the original global block. */ }
    }
    private static void FetchPostfix(ref Task<ItemUpdateType> __result, FetchWitness? __state)
    { if (__state == null) return; __result = FinishFetch(__result, __state); __state.Detach?.Invoke(); }
    private static async Task<ItemUpdateType> FinishFetch(Task<ItemUpdateType> task, FetchWitness witness)
    { try { return await task.ConfigureAwait(false); } finally { witness.Close(); } }
    private static void FetchFinalizer(Exception? __exception, FetchWitness? __state) { if (__exception != null) __state?.Close(); }
    private static bool SameRequest(Candidate c, MediaInfoRequest? request, MediaSourceInfo? s) => s != null && ReferenceEquals(request?.MediaSource, s) &&
        request!.MediaType == DlnaProfileType.Video && request.ExtractChapters && s.Id == c.SourceId && s.ItemId == null && s.ServerId == null && s.Path == c.Path && s.Protocol == MediaProtocol.File &&
        string.IsNullOrEmpty(s.EncoderPath) && !s.EncoderProtocol.HasValue && string.IsNullOrEmpty(s.ProbePath) && !s.ProbeProtocol.HasValue &&
        !(s.RequiredHttpHeaders?.Count > 0) && !s.IsRemote && !s.HasMixedProtocols && !s.IsInfiniteStream && !s.RequiresOpening && !s.RequiresClosing && !s.RequiresLooping &&
        string.IsNullOrEmpty(s.OpenToken) && string.IsNullOrEmpty(s.LiveStreamId) && s.Controller == null && !s.WallClockStart.HasValue && !s.TrancodeLiveStartIndex.HasValue;
    internal static bool TryConsume(object probe, MediaInfoRequest request, CancellationToken token)
    {
        try
        {
            if (ExclusiveSaverControl.NativeChangedState is not FetchWitness w || Volatile.Read(ref w.Closed) != 0 || Volatile.Read(ref w.Consumed) != 0) return false;
            var c = w.Candidate; var s = request?.MediaSource;
            // Probe manager is the frozen actual FFProbe dependency, not a same-shaped receiver.
            if (!ReferenceEquals(c.Binding.Probe, probe) || token != c.Token || token.IsCancellationRequested || !SameRequest(c, request, s)) return false;
            if (!c.SameFile(c.Binding.Files.GetFileInfo(c.Path)) || !c.Stable() || !ReferenceEquals(ExclusiveSaverControl.NativeChangedState, w) || Volatile.Read(ref w.Closed) != 0) return false;
            if (c.Binding.Options() != c.Options || !c.Binding.Registered()) return false;
            if (!c.SameValues() || c.Item.DateModified != c.Stamp || !SameRequest(c, request, s) ||
                !ReferenceEquals(w.Result.Item, c.Item) || !ReferenceEquals(w.Options.MetadataRefreshOptions, c.Refresh) ||
                !ReferenceEquals(w.Options.LibraryOptions, c.Library) || !ReferenceEquals(w.Options.CollectionFolders, c.Folders)) return false;
            if (!ReferenceEquals(ExclusiveSaverControl.NativeChangedState, w) || Volatile.Read(ref w.Closed) != 0) return false;
            return Interlocked.CompareExchange(ref w.Consumed, 1, 0) == 0;
        }
        catch { return false; }
    }
}
