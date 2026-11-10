using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Emit;
using System.Text.Json;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.TV;
using HttpRequestOptions = MediaBrowser.Common.Net.HttpRequestOptions;
using System.Reflection.PortableExecutable;
using MediaBrowser.Common.Plugins;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace StrmAssistantRebuild.Provider;

/// <summary>Expands pinned native image queries; filters and orders admitted results without request replay or image writes.</summary>
public static class NativePosterOrdering
{
    private const string Owner = "StrmAssistantRebuild.NativePosterOrdering.4.10.2";
    private const string Coverage = "Queries include all languages. Matching queries in a pinned non-nested synchronous refresh scope clear their provider-name filter, including synchronous manual callbacks. Outside-scope, ambiguous and post-kickoff queries retain it. Prior query mutations are not rolled back. Native type/library/disabled-provider admission stays unchanged. Successful healthy identity-stable queries for the current five item kinds remove Douban candidates (OrdinalIgnoreCase, no trim) and globally order all remaining native-admitted image types; the native query type/provider/disabled filters and provider calls remain unchanged, including explicit Douban-only queries. TMDB original language takes precedence over TVDB Movie/Series typed originalLanguage, then current Movie/Series OriginalTitle policy (kana > Hangul > BMP Han > nonempty default en). Exact Season/Episode with a positive unchanged SeriesId use the late native associated Series OriginalTitle. Zero SeriesId follows at most 64 late native GetParent edges, respecting the native cached parent, then re-resolves the first Series through the same library (up to 65 library lookups including re-resolution). Negative SeriesId, absent/invalid/cyclic/over-budget relations and same-ID different-Guid re-resolution retain native results including Douban. Only the additional title walk is bounded: the unchanged native entry preferred-language helper may traverse ancestors without a bound. Parent/path identity and native parent-cache transitions are finite late observations, not atomic or cross-await parent identity guarantees. Exact BoxSet uses one late native LibraryManager.GetItemList query with CollectionIds=[InternalId] and Limit=10, leaving all other native defaults unchanged. Up to ten returned member OriginalTitle values are joined with '|'; its own title/name never substitutes. Members and library reference are observed late and rechecked after title/locale getters, not an atomic database-membership snapshot. The synchronous library query may perform IO; no origin HTTP request is added. A selected Chinese origin uses the late library PreferredMetadataLanguage when its raw hyphen-delimited family is zh and its lower-case value passes the existing language validator; this only changes the local origin. Preferred image language remains the native entry snapshot. Backdrop and Episode raw null/empty-language eligible slots share preferred-exact priority. Entry null/Backdrop queries in the exact verified TMDB Movie/Series image operation expand GetBackdrops to unchanged source image references in native rating/count order. Only newly allocated native Backdrop results for those exact references receive nonempty non-xx raw source language with a nonempty path; existing null/empty/xx language remains native. Thumb candidates, DTOs and caches stay untouched; no dictionary-language rewriting. Other item/provider/helper calls retain native admission. Admission is query-entry scoped; later native query-type filtering stays native. OFF/identity changes before admission prevent expansion, but already admitted candidates are not rolled back. Title labels are heuristics, not captured provider language or cache data. TMDB Series uses existing buffered HTTP JSON (up to 2 MiB) or validated detail-cache language. Ordinary absent language and empty title/member list still apply preferred-only ordering and raw-empty Backdrop/Episode priority. Failed identity/state/parent/library observations retain the complete native result, including Douban. No request replay or image writes.";
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private enum Kind { Movie, Series, Season, Episode, BoxSet }
    private static readonly object Gate = new();
    private static readonly AsyncLocal<Query?> Current = new();
    private static readonly AsyncLocal<TmdbOperation?> CurrentSeries = new();
    private static readonly AsyncLocal<TmdbOperation?> CurrentMovie = new();
    private static readonly AsyncLocal<TvdbOperation?> CurrentTvdb = new();
    [ThreadStatic] private static RefreshFrame? CurrentRefresh;
    private sealed class RefreshFrame
    {
        public RefreshFrame? Previous;
        public RefreshFrame Root = null!;
        public State State = null!;
        public object Native = null!;
        public BaseItem Item = null!;
        public LibraryOptions Library = null!;
        public ImageRefreshOptions Options = null!;
        public IDirectoryService Directory = null!;
        public CancellationToken Token;
        public long ItemId;
        public Guid ItemGuid;
        public string? TmdbId, TvdbId;
        public int Thread;
        public bool Ready, Ambiguous, Closed;
    }
    private static State? active;
    private static Harmony? patcher;
    private static MethodInfo[] installed = Array.Empty<MethodInfo>();
    private static long generation;
    private sealed record State(IProviderManager Manager, MethodInfo QueryMethod, MethodInfo? MovieMethod, MethodInfo? MovieImages, IImageProvider? MovieProvider, MethodInfo? IdGetter, MethodInfo? LanguageGetter, MethodInfo? CaptureMethod, MethodInfo[] Barriers, MethodInfo? SeriesMove, MethodInfo? SeriesImages, IImageProvider? SeriesProvider, Type? OffsetType, TvdbBinding? TvdbMovie, TvdbBinding? TvdbSeries, MethodInfo? RefreshEntry, MethodInfo? RefreshMove, FieldInfo? RefreshManager, BackdropBinding? Backdrops, long Generation);
    private sealed record BackdropBinding(MethodInfo Helper, MethodInfo MovieConvert, MethodInfo? SeriesConvert, FieldInfo List, FieldInfo File, FieldInfo Iso, FieldInfo Average, FieldInfo Votes, FieldInfo MovieOwner, FieldInfo? SeriesOwner);
    // Exactly two native TVDB adapters; this is not an extensible provider registry.
    private sealed record TvdbBinding(Kind Kind, IImageProvider Provider, MethodInfo Images, MethodInfo Move, MethodInfo Detail, MethodInfo IdGetter, MethodInfo LanguageGetter, MethodInfo InstanceGetter, MethodInfo ConvertLanguage);
    private sealed class TvdbOperation
    {
        public Query Query = null!;
        public TvdbBinding Binding = null!;
        public RemoteImageFetchOptions Options = null!;
        public BaseItem Item = null!;
        public string Language = "";
        public int DetailRequests;
        public bool Closed;
    }
    private sealed class TvdbFrame
    {
        public TvdbOperation? Previous, Scope;
        public bool Detached;
    }
    private sealed class TmdbOperation
    {
        public Query Query = null!;
        public string Language = "", RawLanguage = "";
        public HashSet<object>? Backdrops;
        public int DetailRequests, BackdropCalls;
        public bool Closed;
    }
    private sealed class TmdbFrame
    {
        public TmdbOperation? Previous, Scope;
        public bool Detached;
    }
    // ponytail: one ID/language association per in-flight native query, not a persistent metadata cache.
    private sealed class Query
    {
        public long Generation, ItemId, SeriesId, ParentId;
        public BaseItem Item = null!;
        public LibraryOptions Library = null!;
        public Guid ItemGuid;
        public Type ItemType = null!;
        public string? RawTmdbId, RawTvdbId;
        public RemoteImageFetchOptions? MovieOptions, SeriesOptions, TvdbOptions;
        public TmdbOperation? MovieOperation;
        public Kind Kind;
        public int TmdbId, MovieOperations, SeriesOperations, TvdbId, TvdbOperations;
        public string Preferred = "", Language = "", TvdbLanguage = "";
        public bool Closed, AllowBackdropExpansion;
    }
    // Observed only after query success; no additional entry Series title/identity lookup or cross-await parent-generation claim.
    private sealed record ParentIdentity(Series Item, long ItemId, Guid ItemGuid, string? TmdbId, string? TvdbId)
    {
        public bool Matches() => Item.GetType() == typeof(Series) && Item.InternalId == ItemId && Item.Id == ItemGuid &&
            Item.GetProviderId(MetadataProviders.Tmdb) == TmdbId && Item.GetProviderId(MetadataProviders.Tvdb) == TvdbId;
    }
    // Late finite observations only; native GetParent may populate its existing non-Folder cache.
    private readonly record struct AncestorIdentity(BaseItem Item, long ItemId, Guid ItemGuid, long ParentId, long SeriesId, string? TmdbId, string? TvdbId, BaseItem? CachedParent)
    {
        public static AncestorIdentity Capture(BaseItem item) => new(item, item.InternalId, item.Id, item.ParentId, item.SeriesId,
            item.GetProviderId(MetadataProviders.Tmdb), item.GetProviderId(MetadataProviders.Tvdb), item.GetCachedParent());
        public bool Matches() => Item.InternalId == ItemId && Item.Id == ItemGuid && Item.ParentId == ParentId && Item.SeriesId == SeriesId &&
            Item.GetProviderId(MetadataProviders.Tmdb) == TmdbId && Item.GetProviderId(MetadataProviders.Tvdb) == TvdbId && ReferenceEquals(Item.GetCachedParent(), CachedParent);
    }
    // At most ten late observed references; no membership requery or cross-await member-generation claim.
    private readonly record struct MemberIdentity(BaseItem Item, long ItemId, Guid ItemGuid, string? TmdbId, string? TvdbId)
    {
        public bool Matches(BaseItem current) => ReferenceEquals(Item, current) && Item.InternalId == ItemId && Item.Id == ItemGuid &&
            Item.GetProviderId(MetadataProviders.Tmdb) == TmdbId && Item.GetProviderId(MetadataProviders.Tvdb) == TvdbId;
    }
    private sealed class Frame
    {
        public Query? Previous, Scope;
        public bool Detached;
    }
    public static bool IsEnabled => ReadState() != null;
    public static bool IsApiQueryEntryEnabled => ReadState()?.Barriers.Any(m => m.MetadataToken == 0x06000937 && m.Module.ModuleVersionId == Guid.Parse("0873fdaf-f3f8-45f4-8d9c-b647b1edf372")) == true;
    public static bool IsRefreshProviderExpansionEnabled => ReadState()?.RefreshEntry != null;
    public static bool IsBackdropExpansionEnabled => ReadState()?.Backdrops != null;
    public static bool IsSeriesEnabled => ReadState()?.SeriesMove != null;
    public static bool IsTvdbMovieEnabled => ReadState()?.TvdbMovie != null;
    public static bool IsTvdbSeriesEnabled => ReadState()?.TvdbSeries != null;
    public static string Detail { get; private set; } = "Inactive. " + Coverage;

    public static bool Configure(Version hostVersion, IProviderManager manager, bool enabled, out string detail, IEnumerable<IPlugin>? plugins = null)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try
            {
                Remove();
                if (!enabled) { detail = Detail = "Inactive. " + Coverage; return true; }
                var type = manager.GetType();
                var providers = type.GetProperty("ImageProviders")?.GetValue(manager) as IImageProvider[];
                var movies = providers?.Where(p => p.GetType().FullName == "MovieDb.MovieDbImageProvider").ToArray();
                var native = movies?.Length == 1 ? movies[0].GetType() : null;
                var query = type.GetMethod("GetAvailableRemoteImages", new[] { typeof(BaseItem), typeof(LibraryOptions), typeof(RemoteImageQuery), typeof(IDirectoryService), typeof(CancellationToken) });
                var movie = native?.GetMethod("GetMovieInfo", BindingFlags.Instance | BindingFlags.NonPublic);
                if (hostVersion != new Version(4, 10, 2, 0) || type.FullName != "Emby.Providers.Manager.ProviderManager" || !type.IsSealed || query == null || !VerifyHost(query))
                { detail = Detail = "Inactive: native host file, MVID or method contract differs. " + Coverage; return false; }
                // A missing or incompatible adapter never revokes another independently verified one.
                try { if (movie != null && !Verify(query, movie, plugins)) movie = null; } catch { movie = null; }
                var tvdbMovie = BindTvdb(providers, Kind.Movie, plugins);
                var tvdbSeries = BindTvdb(providers, Kind.Series, plugins);
                if (movie == null && tvdbMovie == null && tvdbSeries == null)
                { detail = Detail = "Inactive: no verified native poster adapter. " + Coverage; return false; }
                var resultType = movie?.ReturnType.GetGenericArguments()[0];
                var apiCaller = BindApiCaller(out var apiDetail);
                var barriers = new[] { (MethodInfo)query.Module.ResolveMethod(0x06000243)! }.Concat(movie == null ? Array.Empty<MethodInfo>() : new[] { (MethodInfo)movie.Module.ResolveMethod(0x060001E0)! }).Concat(apiCaller == null ? Array.Empty<MethodInfo>() : new[] { apiCaller }).ToArray();
                var series = providers?.Where(p => p.GetType().FullName == "MovieDb.MovieDbSeriesImageProvider").ToArray();
                // A missing/ambiguous Series registration does not revoke the already verified Movie branch.
                var seriesMove = movie != null && series?.Length == 1 && series[0].GetType().Assembly == native!.Assembly && VerifySeries(series[0].GetType())
                    ? (MethodInfo)movie.Module.ResolveMethod(0x0600041B)! : null;
                var refresh = BindRefresh(query);
                var backdrops = movie == null ? null : BindBackdrops(movie, seriesMove != null);
                var next = new State(manager, query, movie, movie == null ? null : (MethodInfo)movie.Module.ResolveMethod(0x060000D8)!, movie == null ? null : movies![0], resultType?.GetProperty("id")!.GetMethod, resultType?.GetProperty("original_language")!.GetMethod, resultType == null ? null : typeof(NativePosterOrdering).GetMethod(nameof(Capture), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(resultType), barriers, seriesMove, seriesMove == null ? null : (MethodInfo)movie!.Module.ResolveMethod(0x0600013F)!, seriesMove == null ? null : series![0], BindOffsetType(), tvdbMovie, tvdbSeries, refresh.Entry, refresh.Move, refresh.Manager, backdrops, ++generation);
                if (Occupied(next)) { detail = Detail = "Inactive: candidate hook target already occupied. " + Coverage; return false; }
                installed = Targets(next).ToArray();
                patcher ??= new Harmony(Owner);
                patcher.Patch(query, prefix: Hook(nameof(BeforeQuery)), postfix: Hook(nameof(AfterQuery)), finalizer: Hook(nameof(FinishQuery)));
                if (next.RefreshEntry != null) patcher.Patch(next.RefreshEntry, prefix: Hook(nameof(BeforeRefresh)), finalizer: Hook(nameof(FinishRefresh)));
                if (movie != null)
                {
                    patcher.Patch(movie, postfix: Hook(nameof(AfterMovie)));
                    // The canonical native MoveNext entry owns each image operation, including pre-warmed D8 dispatch.
                }
                foreach (var tvdb in TvdbBindings(next))
                {
                    patcher.Patch(tvdb.Move, transpiler: Hook(nameof(ObserveTvdbDetail)));
                    patcher.Patch(tvdb.Images, prefix: Hook(nameof(BeforeTvdbImages)), postfix: Hook(nameof(AfterTvdbImages)), finalizer: Hook(nameof(FinishTvdbImages)));
                }
                if (seriesMove != null)
                {
                    patcher.Patch(seriesMove, transpiler: Hook(nameof(ObserveSeriesResponse)));
                    patcher.Patch(next.SeriesImages!, prefix: Hook(nameof(BeforeSeriesImages)), postfix: Hook(nameof(AfterSeriesImages)), finalizer: Hook(nameof(FinishSeriesImages)));
                }
                if (backdrops != null)
                {
                    patcher.Patch(backdrops.Helper, prefix: Hook(nameof(BeforeBackdrops)));
                    patcher.Patch(backdrops.MovieConvert, postfix: Hook(nameof(AfterBackdrop)));
                    if (backdrops.SeriesConvert != null) patcher.Patch(backdrops.SeriesConvert, postfix: Hook(nameof(AfterBackdrop)));
                }
                // Recompile pinned callers that optimized JIT may already have inlined before the option was enabled.
                foreach (var caller in barriers)
                    if (caller.Module == movie?.Module) patcher.Patch(caller, prefix: Hook(nameof(BeforeMovieMove)), transpiler: Hook(nameof(ObserveMovieCompletion)), finalizer: Hook(nameof(FinishMovieImages)));
                    else patcher.Patch(caller, prefix: Hook(nameof(CallerBarrier)));
                Volatile.Write(ref active, next);
                detail = Detail = $"Active adapters: TMDB Movie={movie != null}, Series={seriesMove != null}; TVDB Movie={tvdbMovie != null}, Series={tvdbSeries != null}; refresh query={next.RefreshEntry != null}; API late entry={apiCaller != null} ({apiDetail}). " + Coverage;
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref active, null);
                Detail = "Inactive: candidate hook setup failed (" + error.GetType().Name + "). " + Coverage;
                try { Remove(); } catch { Detail += " Owned cleanup pending; callback inactive."; }
                detail = Detail;
                return false;
            }
        }
    }

    private static HarmonyMethod Hook(string name) => new(typeof(NativePosterOrdering).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!);
    private static void CallerBarrier() { }
    private static IEnumerable<TvdbBinding> TvdbBindings(State state) => new[] { state.TvdbMovie, state.TvdbSeries }.Where(b => b != null).Cast<TvdbBinding>();
    private static IEnumerable<MethodInfo> Targets(State state) => new[] { state.QueryMethod }.Concat(state.MovieMethod == null ? Array.Empty<MethodInfo>() : new[] { state.MovieMethod }).Concat(state.Barriers).Concat(state.SeriesMove == null ? Array.Empty<MethodInfo>() : new[] { state.SeriesMove, state.SeriesImages! }).Concat(TvdbBindings(state).SelectMany(b => new[] { b.Images, b.Move })).Concat(state.RefreshEntry == null ? Array.Empty<MethodInfo>() : new[] { state.RefreshEntry }).Concat(BackdropTargets(state));
    private static IEnumerable<MethodInfo> BackdropTargets(State state) => state.Backdrops == null ? Array.Empty<MethodInfo>() : new[] { state.Backdrops.Helper, state.Backdrops.MovieConvert }.Concat(state.Backdrops.SeriesConvert == null ? Array.Empty<MethodInfo>() : new[] { state.Backdrops.SeriesConvert });
    private static bool Occupied(State state) => Targets(state).Any(m => Harmony.GetPatchInfo(m)?.Owners.Any() == true) || state.MovieImages != null && !Unpatched(state.MovieImages);
    // Cache compatibility is limited to this exact transpiler, not an owner-name whitelist.
    internal static bool OwnsSeriesResponsePatch(MethodInfo method)
    {
        return method.MetadataToken == 0x0600041B && method.Module.ModuleVersionId == Guid.Parse("bf190786-42b7-4923-aea6-1851feea4259") && ExactTranspiler(method, nameof(ObserveSeriesResponse));
    }
    private static bool ExactTranspiler(MethodInfo method, string callback) => ExactPatches(method, transpiler: callback);
    private static bool ExactPatches(MethodInfo method, string? prefix = null, string? postfix = null, string? transpiler = null, string? finalizer = null)
    {
        var p = Harmony.GetPatchInfo(method);
        bool Exact(IReadOnlyCollection<Patch> patches, string? name) => name == null ? patches.Count == 0 : patches.Count == 1 && patches.Single() is var patch && patch.owner == Owner &&
            patch.PatchMethod == typeof(NativePosterOrdering).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic) && patch.priority == Priority.Normal && patch.before.Length == 0 && patch.after.Length == 0 && !patch.debug;
        return p != null && Exact(p.Prefixes, prefix) && Exact(p.Postfixes, postfix) && Exact(p.Transpilers, transpiler) && Exact(p.Finalizers, finalizer) && p.InnerPrefixes.Count + p.InnerPostfixes.Count == 0;
    }
    private static bool Healthy(State state) => ExactPatches(state.QueryMethod, nameof(BeforeQuery), nameof(AfterQuery), finalizer: nameof(FinishQuery)) &&
        (state.MovieMethod == null || ExactPatches(state.MovieMethod, postfix: nameof(AfterMovie)) && Unpatched(state.MovieImages!)) && state.Barriers.All(m => m.Module == state.MovieMethod?.Module ? ExactPatches(m, prefix: nameof(BeforeMovieMove), transpiler: nameof(ObserveMovieCompletion), finalizer: nameof(FinishMovieImages)) : ExactPatches(m, prefix: nameof(CallerBarrier))) &&
        (state.SeriesMove == null || ExactTranspiler(state.SeriesMove, nameof(ObserveSeriesResponse)) && ExactPatches(state.SeriesImages!, nameof(BeforeSeriesImages), nameof(AfterSeriesImages), finalizer: nameof(FinishSeriesImages))) &&
        TvdbBindings(state).All(b => ExactTranspiler(b.Move, nameof(ObserveTvdbDetail)) && ExactPatches(b.Images, nameof(BeforeTvdbImages), nameof(AfterTvdbImages), finalizer: nameof(FinishTvdbImages))) &&
        (state.RefreshEntry == null || ExactPatches(state.RefreshEntry, prefix: nameof(BeforeRefresh), finalizer: nameof(FinishRefresh)) && Unpatched(state.RefreshMove!)) &&
        (state.Backdrops == null || ExactPatches(state.Backdrops.Helper, prefix: nameof(BeforeBackdrops)) && ExactPatches(state.Backdrops.MovieConvert, postfix: nameof(AfterBackdrop)) &&
            (state.Backdrops.SeriesConvert == null || ExactPatches(state.Backdrops.SeriesConvert, postfix: nameof(AfterBackdrop))));
    private static bool Unpatched(MethodInfo method)
    {
        var p = Harmony.GetPatchInfo(method);
        return p == null || p.Prefixes.Count + p.Postfixes.Count + p.Transpilers.Count + p.Finalizers.Count + p.InnerPrefixes.Count + p.InnerPostfixes.Count == 0;
    }
    private static State? ReadState()
    {
        var state = Volatile.Read(ref active);
        if (state != null && !Healthy(state))
        {
            Interlocked.CompareExchange(ref active, null, state);
            Detail = "Inactive: candidate hook ownership changed. " + Coverage;
            return null;
        }
        return state;
    }
    // ponytail: bound synchronous entry scope only; opaque original nested-counter parity remains unknown.
    private static void BeforeRefresh(object __instance, BaseMetadataResult __0, LibraryOptions __1, IRemoteImageProvider __2, ImageRefreshOptions __3, CancellationToken __9, out RefreshFrame? __state)
    {
        __state = null;
        var previous = CurrentRefresh;
        if (previous != null) previous.Root.Ambiguous = true;
        try
        {
            var state = ReadState();
            if (state?.RefreshEntry == null || __instance.GetType() != state.RefreshEntry.DeclaringType ||
                !ReferenceEquals(state.RefreshManager!.GetValue(__instance), state.Manager)) return;
            var frame = new RefreshFrame { Previous = previous, State = state, Native = __instance, Library = __1, Options = __3, Token = __9, Thread = Environment.CurrentManagedThreadId };
            frame.Root = previous?.Root ?? frame;
            __state = CurrentRefresh = frame; // BaseItem is virtual: publish a non-ready guard before invoking it.
            frame.Item = __0.BaseItem;
            frame.ItemId = frame.Item.InternalId; frame.ItemGuid = frame.Item.Id;
            frame.TmdbId = frame.Item.GetProviderId(MetadataProviders.Tmdb); frame.TvdbId = frame.Item.GetProviderId(MetadataProviders.Tvdb);
            frame.Directory = __3.DirectoryService;
            frame.Ready = __1 != null && __2 != null && frame.Directory != null && !frame.Root.Ambiguous && ReferenceEquals(ReadState(), state);
        }
        catch (Exception) { /* Failed observation keeps the native provider filter. */ }
    }
    private static Exception? FinishRefresh(Exception? __exception, RefreshFrame? __state)
    {
        if (__state != null && !__state.Closed)
        {
            __state.Closed = true;
            if (ReferenceEquals(CurrentRefresh, __state)) CurrentRefresh = __state.Previous;
            __state.Previous = null;
        }
        return __exception; // Synchronous kickoff exit, not completion of the returned Task.
    }
    private static bool MatchRefresh(State state, object manager, BaseItem item, LibraryOptions library, IDirectoryService directory, CancellationToken token)
    {
        var frame = CurrentRefresh;
        return frame != null && frame.Ready && !frame.Closed && !frame.Root.Ambiguous && ReferenceEquals(frame.State, state) &&
            frame.Thread == Environment.CurrentManagedThreadId && ReferenceEquals(manager, state.Manager) && ReferenceEquals(item, frame.Item) &&
            ReferenceEquals(library, frame.Library) && ReferenceEquals(directory, frame.Directory) && token == frame.Token &&
            ReferenceEquals(frame.Options.DirectoryService, directory) && ReferenceEquals(state.RefreshManager!.GetValue(frame.Native), manager) &&
            item.InternalId == frame.ItemId && item.Id == frame.ItemGuid && item.GetProviderId(MetadataProviders.Tmdb) == frame.TmdbId && item.GetProviderId(MetadataProviders.Tvdb) == frame.TvdbId;
    }
    private static int TmdbId(BaseItem item) => ProviderId(item, MetadataProviders.Tmdb);
    private static int TvdbId(BaseItem item) => ProviderId(item, MetadataProviders.Tvdb);
    private static int ProviderId(BaseItem item, MetadataProviders provider) => int.TryParse(item.GetProviderId(provider), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0;
    private static bool SameItem(Query query, BaseItem? item)
    {
        try
        {
            return ReferenceEquals(query.Item, item) && item!.InternalId == query.ItemId && item.Id == query.ItemGuid && item.GetType() == query.ItemType &&
                item.GetProviderId(MetadataProviders.Tmdb) == query.RawTmdbId && item.GetProviderId(MetadataProviders.Tvdb) == query.RawTvdbId &&
                (query.Kind is not (Kind.Season or Kind.Episode) || item.SeriesId == query.SeriesId && item.ParentId == query.ParentId);
        }
        catch (Exception) { return false; }
    }
    private static bool QueryIdentity(Query query) => SameItem(query, query.Item) && (query.MovieOptions == null || SameItem(query, query.MovieOptions.Item)) &&
        (query.SeriesOptions == null || SameItem(query, query.SeriesOptions.Item)) && (query.TvdbOptions == null || SameItem(query, query.TvdbOptions.Item));
    private static void BeforeQuery(object __instance, BaseItem __0, LibraryOptions __1, RemoteImageQuery __2, IDirectoryService __3, CancellationToken __4, out Frame __state)
    {
        __state = new Frame { Previous = Current.Value };
        Current.Value = null; // Nested/unsupported queries must not inherit their caller's association.
        try
        {
            var state = ReadState();
            if (state == null || !ReferenceEquals(state.Manager, __instance) || __2 == null) return;
            __2.IncludeAllLanguages = true; // Entry-time native query mutation is independent of typed ordering scope; OFF does not roll it back.
            if (MatchRefresh(state, __instance, __0, __1, __3, __4)) __2.ProviderName = null;
            if (__0.InternalId <= 0) return;
            var kind = __0 is Movie ? Kind.Movie : __0.GetType() == typeof(Series) ? Kind.Series : __0.GetType() == typeof(Season) ? Kind.Season : __0.GetType() == typeof(Episode) ? Kind.Episode : __0.GetType() == typeof(BoxSet) ? Kind.BoxSet : (Kind?)null;
            var id = TmdbId(__0); var tvdbId = TvdbId(__0);
            if (kind == null) return;
            var child = kind is Kind.Season or Kind.Episode;
            var seriesId = child ? __0.SeriesId : 0; var parentId = child ? __0.ParentId : 0;
            if (child && seriesId < 0) return; // Keep native entry preference semantics, including its potentially unbounded ancestor walk.
            var query = new Query { Generation = state.Generation, AllowBackdropExpansion = __2.ImageType is null or ImageType.Backdrop, Kind = kind.Value, Item = __0, Library = __1, ItemId = __0.InternalId, ItemGuid = __0.Id, ItemType = __0.GetType(), RawTmdbId = __0.GetProviderId(MetadataProviders.Tmdb), RawTvdbId = __0.GetProviderId(MetadataProviders.Tvdb), TmdbId = id, TvdbId = tvdbId, SeriesId = seriesId, ParentId = parentId, Preferred = __0.GetPreferredImageLanguage(__1) ?? "" };
            if (SameItem(query, __0) && TmdbId(__0) == id && TvdbId(__0) == tvdbId) __state.Scope = Current.Value = query;
        }
        catch (Exception) { /* Identity observation never changes native query control flow. */ }
    }
    private static void Detach(Frame? frame)
    {
        if (frame == null || frame.Detached) return;
        Current.Value = frame.Previous;
        frame.Previous = null;
        frame.Detached = true;
    }
    private static void AfterQuery(ref Task<IEnumerable<RemoteImageInfo>> __result, Frame __state)
    {
        Detach(__state);
        if (__state.Scope != null) __result = OrderAfter(__result, __state.Scope);
    }
    private static Exception? FinishQuery(Exception? __exception, Frame? __state)
    {
        Detach(__state);
        if (__exception != null && __state?.Scope != null) __state.Scope.Closed = true;
        return __exception;
    }
    private static TmdbOperation? BackdropOperation(State state, object provider)
    {
        var query = Current.Value;
        if (state.Backdrops == null || query == null || query.Closed || !query.AllowBackdropExpansion || query.Generation != state.Generation || query.TmdbId <= 0 || !QueryIdentity(query)) return null;
        var operation = query.Kind == Kind.Movie && query.MovieOperations == 1 && ReferenceEquals(provider, state.MovieProvider) ? CurrentMovie.Value :
            query.Kind == Kind.Series && query.SeriesOperations == 1 && ReferenceEquals(provider, state.SeriesProvider) ? CurrentSeries.Value : null;
        return operation is { Closed: false } && ReferenceEquals(operation.Query, query) ? operation : null;
    }
    private static bool BeforeBackdrops(object __instance, object? __0, ref object __result)
    {
        try
        {
            var state = ReadState(); var binding = state?.Backdrops;
            var operation = state == null ? null : BackdropOperation(state, __instance);
            if (binding == null || operation == null || __0?.GetType() != binding.List.DeclaringType) return true;
            if (++operation.BackdropCalls != 1) { operation.Backdrops = null; return true; }
            if (binding.List.GetValue(__0) is not System.Collections.IList list) return true;
            var entries = list.Cast<object>().ToArray();
            // Preserve native null-entry failure and unknown/subclass behavior rather than repairing malformed DTOs.
            if (entries.Any(i => i?.GetType() != binding.Iso.DeclaringType)) return true;
            var sorted = entries.OrderByDescending(i => (double)binding.Average.GetValue(i)!).ThenByDescending(i => (int)binding.Votes.GetValue(i)!).ToArray();
            var result = Array.CreateInstance(binding.Iso.DeclaringType!, sorted.Length);
            for (var i = 0; i < sorted.Length; i++) result.SetValue(sorted[i], i);
            if (!ReferenceEquals(ReadState(), state) || !ReferenceEquals(BackdropOperation(state!, __instance), operation)) return true;
            // Query-operation-local source identity only; ISO/path/cache fields and native Thumb candidates never change.
            operation.Backdrops = new HashSet<object>(entries, ReferenceEqualityComparer.Instance);
            __result = result;
            return false;
        }
        catch (Exception) { return true; } // Failed observation leaves the original native helper and its faults intact.
    }
    private static void AfterBackdrop(object __instance, object __0, RemoteImageInfo __result, MethodBase __originalMethod)
    {
        try
        {
            var state = ReadState(); var binding = state?.Backdrops;
            if (binding == null || __result == null || __result.Type != ImageType.Backdrop || __result.ProviderName != "TheMovieDb" || __result.Language != null) return;
            var owner = __originalMethod == binding.MovieConvert ? binding.MovieOwner : __originalMethod == binding.SeriesConvert ? binding.SeriesOwner : null;
            if (owner == null || __instance.GetType() != owner.DeclaringType) return;
            var provider = owner.GetValue(__instance); var operation = provider == null ? null : BackdropOperation(state!, provider);
            if (operation?.Backdrops?.Contains(__0) != true || operation.BackdropCalls != 1) return;
            var language = (string?)binding.Iso.GetValue(__0); var path = (string?)binding.File.GetValue(__0);
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(language) || string.Equals(language, "xx", StringComparison.OrdinalIgnoreCase)) return;
            if (ReferenceEquals(ReadState(), state) && ReferenceEquals(BackdropOperation(state!, provider!), operation) && operation.Backdrops?.Contains(__0) == true)
                __result.Language = language; // Only this newly allocated native converter result receives its exact source label.
        }
        catch (Exception) { /* Failed source observation leaves the native converter result intact. */ }
    }
    private static void BeforeMovieMove(object __instance, out TmdbFrame? __state)
    {
        __state = null;
        try
        {
            var type = __instance.GetType();
            if ((int)type.GetField("<>1__state")!.GetValue(__instance)! == -1)
                BeforeMovieImages(type.GetField("<>4__this")!.GetValue(__instance)!, (RemoteImageFetchOptions)type.GetField("options")!.GetValue(__instance)!, out __state);
        }
        catch (Exception) { /* Native state observation never changes MoveNext. */ }
    }
    private static void CompleteMovieMove(IEnumerable<RemoteImageInfo>? images)
    {
        var operation = CurrentMovie.Value;
        if (operation == null) return;
        try
        {
            var query = operation.Query; var state = ReadState();
            if (images != null && !operation.Closed && !query.Closed && ReferenceEquals(Current.Value, query) && ReferenceEquals(query.MovieOperation, operation) && operation.DetailRequests == 1 && query.MovieOperations == 1 && state?.MovieImages != null && query.Generation == state.Generation && QueryIdentity(query)) query.Language = operation.Language;
        }
        catch (Exception) { /* Success publication preserves native SetResult. */ }
        finally { operation.Closed = true; operation.Language = ""; operation.Backdrops = null; }
    }
    private static void FaultMovieMove(Exception error) => CompleteMovieMove(null);
    private static IEnumerable<CodeInstruction> ObserveMovieCompletion(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var blocks = code.SelectMany((c, i) => c.blocks.Select(b => (i, b.blockType, b.catchType))).ToArray();
        if (!blocks.SequenceEqual(new[] { (6, ExceptionBlockType.BeginExceptionBlock, typeof(object)), (251, ExceptionBlockType.BeginCatchBlock, typeof(Exception)), (274, ExceptionBlockType.EndExceptionBlock, typeof(object)) }))
            throw new InvalidOperationException("Unexpected Movie completion EH");
        if (__originalMethod is not MethodInfo original || original.MetadataToken != 0x060001E0 || original.Module.ModuleVersionId != Guid.Parse("bf190786-42b7-4923-aea6-1851feea4259") || Fingerprint(original) != "0F5386B530DD99E5169D972D5EF038CDE1E25409437395067B8842B6A7E40FA0" || code.Count != 298)
            throw new InvalidOperationException("Unexpected Movie MoveNext contract");
        var inserts = code.Select((c,i)=>(c,i)).Where(x => x.c.opcode == OpCodes.Call && x.c.operand is MethodInfo m && m.DeclaringType == typeof(System.Runtime.CompilerServices.AsyncTaskMethodBuilder<IEnumerable<RemoteImageInfo>>) && (m.Name == "SetResult" || m.Name == "SetException")).ToArray();
        if (inserts.Length != 2 || inserts[0].i != 273 || inserts[1].i != 296 || ((MethodInfo)code[273].operand).Name != "SetException" || ((MethodInfo)code[296].operand).Name != "SetResult" || inserts.Any(x => x.c.blocks.Count != 0 || x.c.labels.Count != 0 || ((MethodInfo)x.c.operand).ReturnType != typeof(void)) || !((MethodInfo)code[273].operand).GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Exception) }) || !((MethodInfo)code[296].operand).GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(IEnumerable<RemoteImageInfo>) })) throw new InvalidOperationException("Unexpected Movie completion anchors");
        foreach (var (c,i) in inserts.AsEnumerable().Reverse())
        {
            var dup = new CodeInstruction(OpCodes.Dup);
            code.InsertRange(i,new[] { dup, new CodeInstruction(OpCodes.Call, typeof(NativePosterOrdering).GetMethod(((MethodInfo)c.operand).Name == "SetResult" ? nameof(CompleteMovieMove) : nameof(FaultMovieMove), BindingFlags.Static | BindingFlags.NonPublic)!) });
        }
        return code;
    }
    private static void BeforeMovieImages(object __instance, RemoteImageFetchOptions __0, out TmdbFrame __state)
    {
        __state = new TmdbFrame { Previous = CurrentMovie.Value };
        CurrentMovie.Value = null;
        try
        {
            var state = ReadState(); var query = Current.Value;
            if (query == null || query.Kind != Kind.Movie || query.Closed || query.TmdbId <= 0 || state?.MovieImages == null || query.Generation != state.Generation ||
                !ReferenceEquals(__instance, state.MovieProvider) || !SameItem(query, __0?.Item)) return;
            if (Interlocked.Increment(ref query.MovieOperations) != 1) { query.Language = ""; return; }
            query.MovieOptions = __0;
            __state.Scope = CurrentMovie.Value = query.MovieOperation = new TmdbOperation { Query = query };
        }
        catch (Exception) { /* Observation setup must not change the native image Task. */ }
    }
    private static void DetachMovie(TmdbFrame? frame)
    {
        if (frame == null || frame.Detached) return;
        CurrentMovie.Value = frame.Previous; frame.Previous = null; frame.Detached = true;
    }
    private static Exception? FinishMovieImages(Exception? __exception, TmdbFrame? __state)
    {
        DetachMovie(__state);
        if (__exception != null && __state?.Scope != null) { __state.Scope.Closed = true; __state.Scope.Backdrops = null; }
        return __exception;
    }
    private static void AfterMovie(object __instance, BaseItem __0, ref object __result)
    {
        try
        {
            var operation = CurrentMovie.Value; var scope = Current.Value; var state = ReadState();
            if (operation == null || operation.Closed || !ReferenceEquals(operation.Query, scope) || scope == null || scope.MovieOperations != 1 || scope.Closed || state?.MovieMethod == null ||
                scope.Generation != state.Generation || !ReferenceEquals(__instance, state.MovieProvider) || !SameItem(scope, __0)) return;
            if (Interlocked.Increment(ref operation.DetailRequests) != 1) { operation.Language = ""; return; }
            // Non-generic registered callback survives Harmony owner-list rebuilds; the inner Task stays exactly typed.
            __result = state.CaptureMethod!.Invoke(null, new[] { __result, operation, state })!;
        }
        catch (Exception) { /* Observation setup never changes native result/fault/cancellation. */ }
    }
    private static async Task<T> Capture<T>(Task<T> task, TmdbOperation operation, State state)
    {
        var result = await task.ConfigureAwait(false);
        try
        {
            var scope = operation.Query;
            if (result != null && !operation.Closed && !scope.Closed && operation.DetailRequests == 1 && scope.MovieOperations == 1 && QueryIdentity(scope) && ReferenceEquals(ReadState(), state))
            {
                var id = (int)state.IdGetter!.Invoke(result, null)!;
                var language = OriginalPosterPolicy.NormalizeLanguage((string?)state.LanguageGetter!.Invoke(result, null));
                if (id == scope.TmdbId && ValidLanguage(language)) operation.Language = language;
            }
        }
        catch (Exception) { /* Observation failure preserves the original successful native Task. */ }
        return result;
    }
    private static bool ValidLanguage(string value) => value.Length is >= 2 and <= 35 && value != "xx" &&
        value.Split('-') is var parts && parts[0].Length is 2 or 3 && parts[0].All(c => c is >= 'a' and <= 'z') &&
        parts.Skip(1).All(p => p.Length is >= 1 and <= 8 && p.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9'));
    private static string SeriesLanguage(string? value)
    {
        var language = OriginalPosterPolicy.NormalizeLanguage(value);
        return ValidLanguage(language) && CultureInfo.GetCultureInfo(language, predefinedOnly: true).Name.Length != 0 ? language : "";
    }
    private static TmdbOperation? SeriesCacheOperation(int id, string context)
    {
        var operation = CurrentSeries.Value; var query = Current.Value; var state = ReadState();
        // Pinned FetchImages calls EnsureSeriesInfo(id, null): metadata-language variants are not image operations.
        return context.Length == 0 && operation != null && !operation.Closed && ReferenceEquals(operation.Query, query) &&
            query is { Kind: Kind.Series, Closed: false, SeriesOperations: 1 } && QueryIdentity(query) && query.TmdbId == id && state?.SeriesMove != null && query.Generation == state.Generation ? operation : null;
    }
    internal static void BeginSeriesCacheRequest(int id, string context)
    {
        var operation = SeriesCacheOperation(id, "");
        // Same-ID metadata work in this image operation is ambiguous even when its requested language differs.
        if (operation != null && Interlocked.Add(ref operation.DetailRequests, context.Length == 0 ? 1 : 2) != 1) operation.Language = operation.RawLanguage = "";
    }
    internal static void SeedSeriesCacheLanguage(int id, string context, string? value)
    {
        var operation = SeriesCacheOperation(id, context);
        if (operation?.DetailRequests == 1) operation.Language = SeriesLanguage(value); // Pending only; never a raw source for persistence.
    }
    internal static string? ReadSeriesCacheLanguage(int id, string context, out Func<bool>? stillActive)
    {
        stillActive = null;
        var operation = SeriesCacheOperation(id, context);
        if (operation?.DetailRequests != 1 || operation.RawLanguage.Length == 0) return null;
        var language = SeriesLanguage(operation.RawLanguage);
        if (language.Length == 0) return null;
        stillActive = () => ReferenceEquals(SeriesCacheOperation(id, context), operation) && operation.DetailRequests == 1 && operation.RawLanguage == language;
        return language;
    }
    private static bool AncestorsMatch(Query scope, State state, ILibraryManager? library, List<AncestorIdentity> path) =>
        path.All(node => node.Matches()) && QueryIdentity(scope) && ReferenceEquals(ReadState(), state) && ReferenceEquals(library, BaseItem.LibraryManager);
    private static Series? AncestorSeries(Query scope, State state, ILibraryManager library, List<AncestorIdentity> path)
    {
        var root = AncestorIdentity.Capture(scope.Item);
        path.Add(root);
        var references = new HashSet<BaseItem>(ReferenceEqualityComparer.Instance) { root.Item };
        var ids = new HashSet<long> { root.ItemId };
        // ponytail: at most 64 native parent edges plus one Series re-resolution; no bound on the existing entry preference helper.
        for (var depth = 0; depth < 64; depth++)
        {
            if (!AncestorsMatch(scope, state, library, path)) return null;
            var source = path[^1];
            var next = source.Item.GetParent(null); // Native IDataContext=null, synchronous; underlying library calls may perform IO.
            if (next == null) return null;
            var node = AncestorIdentity.Capture(next);
            if (source.CachedParent != null ? !ReferenceEquals(next, source.CachedParent) : node.ItemId != source.ParentId) return null;
            // Account only for the cache transition performed by native SetCachedParent; never refresh old identity scalars.
            path[^1] = source with { CachedParent = source.CachedParent ?? (next is Folder ? null : next) };
            path.Add(node);
            if (node.ItemId <= 0 || !references.Add(next) || !ids.Add(node.ItemId) || !AncestorsMatch(scope, state, library, path)) return null;
            if (next is not Series) continue;
            // Stop at the first Series, as native FindParent<Series> does; never skip a rejected Series to find another.
            var resolved = library.GetItemById(node.ItemId, null);
            if (resolved?.GetType() != typeof(Series)) return null;
            var final = AncestorIdentity.Capture(resolved);
            path.Add(final); // A different reference with the same ID/Guid is legal, but both observations must remain stable.
            // Deliberately stricter than native: same-ID Guid replacement aborts rather than using a different title identity.
            return final.ItemId == node.ItemId && final.ItemGuid == node.ItemGuid && AncestorsMatch(scope, state, library, path) ? (Series)resolved : null;
        }
        return null; // Depth 64 was inspected; depth 65 is never queried, not a search for a different ancestor.
    }
    private static async Task<IEnumerable<RemoteImageInfo>> OrderAfter(Task<IEnumerable<RemoteImageInfo>> task, Query scope)
    {
        try
        {
            var images = await task.ConfigureAwait(false);
            if (images == null) return images!;
            var state = ReadState();
            if (state == null || state.Generation != scope.Generation || !QueryIdentity(scope)) return images;
            // Preserve existing TMDB authority regardless of TVDB completion order or disagreement.
            var language = (scope.Kind == Kind.Movie && scope.MovieOperations == 1 || scope.Kind == Kind.Series && scope.SeriesOperations == 1) ? scope.Language : "";
            if (language.Length == 0 && scope.Kind is Kind.Movie or Kind.Series && scope.TvdbOperations == 1) language = scope.TvdbLanguage;
            ParentIdentity? parent = null;
            ILibraryManager? library = null;
            BaseItem[]? items = null;
            MemberIdentity[]? members = null;
            List<AncestorIdentity>? ancestors = null;
            bool AncestorPathMatches() => ancestors == null || AncestorsMatch(scope, state, library, ancestors);
            bool MembersMatch()
            {
                if (members == null) return true;
                if (!ReferenceEquals(library, BaseItem.LibraryManager)) return false;
                for (var i = 0; i < members.Length; i++) if (!members[i].Matches(items![i])) return false;
                return true;
            }
            if (language.Length == 0)
            {
                // Latest title is a local heuristic, never captured provider language or cache data.
                try
                {
                    BaseItem item = scope.Item;
                    if (scope.Kind is Kind.Season or Kind.Episode && scope.SeriesId == 0)
                    {
                        library = BaseItem.LibraryManager;
                        if (library == null) return images;
                        ancestors = new List<AncestorIdentity>();
                        var series = AncestorSeries(scope, state, library, ancestors);
                        if (series == null) return images;
                        item = series;
                    }
                    else if (scope.Kind is Kind.Season or Kind.Episode)
                    {
                        // Exactly one native Series getter; positive frozen SeriesId avoids its ancestor fallback.
                        var series = scope.Kind == Kind.Season ? ((Season)item).Series : ((Episode)item).Series;
                        if (series?.GetType() != typeof(Series) || series.InternalId != scope.SeriesId) return images;
                        parent = new ParentIdentity(series, series.InternalId, series.Id, series.GetProviderId(MetadataProviders.Tmdb), series.GetProviderId(MetadataProviders.Tvdb));
                        if (!ReferenceEquals(ReadState(), state) || !QueryIdentity(scope)) return images;
                        item = series;
                    }
                    if (scope.Kind == Kind.BoxSet)
                    {
                        library = BaseItem.LibraryManager; // Existing native service; intentionally observed after await, not globally pinned at entry.
                        if (library == null) return images;
                        items = library.GetItemList(new InternalItemsQuery { CollectionIds = new[] { scope.ItemId }, Limit = 10 });
                        if (items == null || items.Length > 10 || !ReferenceEquals(library, BaseItem.LibraryManager) || !ReferenceEquals(ReadState(), state) || !QueryIdentity(scope)) return images;
                        members = new MemberIdentity[items.Length];
                        for (var i = 0; i < items.Length; i++)
                        {
                            var member = items[i];
                            if (member == null || member.InternalId <= 0) return images;
                            members[i] = new MemberIdentity(member, member.InternalId, member.Id, member.GetProviderId(MetadataProviders.Tmdb), member.GetProviderId(MetadataProviders.Tvdb));
                        }
                        // Original 06001A7F: preserve order, null/empty titles and separator-only nonempty default-en semantics.
                        language = OriginalPosterPolicy.TitleFallbackLanguage(string.Join("|", members.Select(m => m.Item.OriginalTitle))) ?? "";
                    }
                    else language = OriginalPosterPolicy.TitleFallbackLanguage(item.OriginalTitle) ?? "";
                    if (parent != null && !parent.Matches() || !AncestorPathMatches() || !MembersMatch()) return images;
                }
                catch (Exception) { return images; } // Failed parent/title observation preserves native success.
                if (!ReferenceEquals(ReadState(), state) || !QueryIdentity(scope)) return images;
            }
            try
            {
                if (string.Equals(language.Split('-')[0], "zh", StringComparison.OrdinalIgnoreCase))
                {
                    // Original 1067 reads the library scalar after origin selection, not the item/ancestor locale.
                    var metadata = scope.Library.PreferredMetadataLanguage;
                    if (!string.IsNullOrEmpty(metadata) && string.Equals(metadata.Split('-')[0], "zh", StringComparison.OrdinalIgnoreCase))
                    {
                        var corrected = metadata.ToLowerInvariant(); // No trim, alias conversion or metadata/cache write.
                        if (ValidLanguage(corrected)) language = corrected; // Retain this reconstruction's strict language domain.
                    }
                }
                // The late getter can run callbacks after title/parent validation and after typed capture.
                if (parent != null && !parent.Matches() || !AncestorPathMatches() || !MembersMatch() || !ReferenceEquals(ReadState(), state) || !QueryIdentity(scope)) return images;
            }
            catch (Exception) { return images; }
            return OrderSlots(images, language, scope.Preferred, scope.Kind == Kind.Episode);
        }
        finally { scope.Closed = true; scope.Language = scope.TvdbLanguage = ""; if (scope.MovieOperation is { } operation) { operation.Closed = true; operation.Language = ""; operation.Backdrops = null; scope.MovieOperation = null; } }
    }
    internal static IEnumerable<RemoteImageInfo> OrderSlots(IEnumerable<RemoteImageInfo> images, string original, string preferred, bool episode = false)
    {
        if (!string.IsNullOrEmpty(original) && !ValidLanguage(OriginalPosterPolicy.NormalizeLanguage(original))) return images;
        var array = images.ToArray();
        // All admitted image types share a stable global order; only Douban candidates are removed.
        return array.Where(i => !string.Equals(i.ProviderName, "Douban", StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => (i.Type == ImageType.Backdrop || episode) && string.IsNullOrEmpty(i.Language) ? 0 : OriginalPosterPolicy.Priority(i, original, preferred)).ToArray();
    }

    private static IEnumerable<CodeInstruction> ObserveSeriesResponse(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var response = (MethodInfo)__originalMethod.Module.ResolveMethod(0x0600011A)!;
        var matches = code.Where(i => i.Calls(response)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Native Series response call count differs.");
        var id = __originalMethod.Module.ResolveField(0x0400033D)!;
        var provider = __originalMethod.Module.ResolveField(0x0400033F)!;
        var capture = typeof(NativePosterOrdering).GetMethod(nameof(CaptureSeriesResponse), BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var instruction in code)
        {
            yield return instruction; // Keep the original call's labels and EH boundaries intact.
            if (instruction != matches[0]) continue;
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, id);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, provider);
            yield return new CodeInstruction(OpCodes.Call, capture);
        }
    }
    private static void BeforeSeriesImages(object __instance, RemoteImageFetchOptions __0, out TmdbFrame __state)
    {
        __state = new TmdbFrame { Previous = CurrentSeries.Value };
        CurrentSeries.Value = null;
        try
        {
            var state = ReadState(); var query = Current.Value; var item = __0?.Item;
            if (query == null || query.Kind != Kind.Series || query.Closed || state == null || query.Generation != state.Generation || !ReferenceEquals(__instance, state.SeriesProvider) ||
                item?.GetType() != typeof(Series) || !SameItem(query, item) || TmdbId(item) != query.TmdbId) return;
            // Multiple/native nested image invocations are ambiguous: no last-writer-wins query association.
            if (Interlocked.Increment(ref query.SeriesOperations) != 1) { query.Language = ""; return; }
            query.SeriesOptions = __0;
            __state.Scope = CurrentSeries.Value = new TmdbOperation { Query = query };
        }
        catch (Exception) { /* Observation setup must not change the native image Task. */ }
    }
    private static void DetachSeries(TmdbFrame? frame)
    {
        if (frame == null || frame.Detached) return;
        CurrentSeries.Value = frame.Previous; frame.Previous = null; frame.Detached = true;
    }
    private static void AfterSeriesImages(RemoteImageFetchOptions __0, ref Task<IEnumerable<RemoteImageInfo>> __result, TmdbFrame __state)
    {
        DetachSeries(__state);
        if (__state.Scope != null) __result = CompleteSeriesImages(__result, __state.Scope, __0);
    }
    private static Exception? FinishSeriesImages(Exception? __exception, TmdbFrame? __state)
    {
        DetachSeries(__state);
        if (__exception != null && __state?.Scope != null) { __state.Scope.Closed = true; __state.Scope.Backdrops = null; }
        return __exception;
    }
    private static async Task<IEnumerable<RemoteImageInfo>> CompleteSeriesImages(Task<IEnumerable<RemoteImageInfo>> task, TmdbOperation operation, RemoteImageFetchOptions options)
    {
        try
        {
            var images = await task.ConfigureAwait(false); // Only the exact native image-provider success publishes its language.
            try
            {
                var query = operation.Query; var state = ReadState(); var item = options?.Item;
                if (images != null && !operation.Closed && operation.DetailRequests <= 1 && !query.Closed && query.SeriesOperations == 1 && state?.SeriesMove != null && query.Generation == state.Generation &&
                    item?.GetType() == typeof(Series) && QueryIdentity(query) && SameItem(query, item) && TmdbId(item) == query.TmdbId) query.Language = operation.Language;
            }
            catch (Exception) { /* Observation must not fault an otherwise successful native Task. */ }
            return images!;
        }
        finally { operation.Closed = true; operation.Language = operation.RawLanguage = ""; operation.Backdrops = null; }
    }
    private static Task<HttpResponseInfo> CaptureSeriesResponse(Task<HttpResponseInfo> task, string id, object provider)
    {
        try
        {
            var scope = Current.Value; var operation = CurrentSeries.Value; var state = ReadState();
            // This call site constructs only /3/tv/{id}; the registered image invocation owns the association, not its numeric ID alone.
            if (scope == null || operation == null || operation.Closed || !ReferenceEquals(operation.Query, scope) || scope.SeriesOperations != 1 || scope.Kind != Kind.Series || scope.Closed || state?.SeriesMove == null || scope.Generation != state.Generation ||
                provider.GetType().FullName != "MovieDb.MovieDbSeriesProvider" || provider.GetType().Assembly != state.MovieMethod!.Module.Assembly ||
                !int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var requested) || requested != scope.TmdbId) return task;
            return CaptureBufferedResponse(task, operation, state);
        }
        catch (Exception) { return task; }
    }
    private static async Task<HttpResponseInfo> CaptureBufferedResponse(Task<HttpResponseInfo> task, TmdbOperation operation, State state)
    {
        var response = await task.ConfigureAwait(false); // Native result object/fault/token/disposal remain native.
        var scope = operation.Query;
        try
        {
            if (response == null || !QueryIdentity(scope) || scope.Closed || operation.Closed || operation.DetailRequests > 1 || scope.SeriesOperations != 1 || !ReferenceEquals(ReadState(), state)) return response!;
            var bytes = BufferedBytes(response.Content, state.OffsetType);
            if (bytes == null) return response;
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count(p => p.NameEquals("id")) != 1 || root.EnumerateObject().Count(p => p.NameEquals("original_language")) != 1 ||
                !root.GetProperty("id").TryGetInt32(out var id) || id != scope.TmdbId || root.GetProperty("original_language").ValueKind != JsonValueKind.String) return response;
            var language = SeriesLanguage(root.GetProperty("original_language").GetString());
            if (language.Length != 0 && !scope.Closed && !operation.Closed && operation.DetailRequests <= 1 && scope.SeriesOperations == 1 && ReferenceEquals(ReadState(), state)) operation.Language = operation.RawLanguage = language;
        }
        catch (Exception) { /* Observation failure changes neither the response nor its original stream cursor. */ }
        return response;
    }
    private static byte[]? BufferedBytes(Stream stream, Type? offsetType)
    {
        if (stream.GetType() == typeof(MemoryStream))
        {
            var memory = (MemoryStream)stream;
            return memory.CanRead && memory.Position == 0 && memory.Length is > 0 and <= MaxResponseBytes ? memory.ToArray() : null;
        }
        if (offsetType == null || stream.GetType() != offsetType || !stream.CanRead || !stream.CanSeek || stream.Position != 0 || stream.Length is <= 0 or > MaxResponseBytes) return null;
        var file = offsetType.GetField("_baseStream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stream) as FileStream;
        var offset = (long)offsetType.GetField("_offset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stream)!;
        if (file?.GetType() != typeof(FileStream) || !file.CanRead || file.CanWrite || !file.CanSeek || offset < 0 || file.Position != offset) return null;
        var length = checked((int)stream.Length);
        if (checked(offset + length) > file.Length) return null;
        var bytes = new byte[length];
        // Existing native HTTP-cache handle only: bounded extra disk reads, no path open, seek, replacement stream or write.
        for (var read = 0; read < length;)
        {
            var count = RandomAccess.Read(file.SafeFileHandle, bytes.AsSpan(read), checked(offset + read));
            if (count == 0) return null;
            read += count;
        }
        return file.Position == offset && stream.Position == 0 ? bytes : null;
    }
    private static Type? BindOffsetType()
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "Emby.Server.Implementations").ToArray();
            if (assemblies.Length != 1 || !FilePin(assemblies[0], "Emby.Server.Implementations", "4.10.2.0", "2009e43d-fa42-4e57-a902-2784a8306072", "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F")) return null;
            var type = assemblies[0].GetType("Emby.Server.Implementations.HttpClientManager.OffsetStream")!;
            var stream = type.GetField("_baseStream", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var offset = type.GetField("_offset", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return type.MetadataToken == 0x02000195 && stream.MetadataToken == 0x04000681 && stream.FieldType == typeof(Stream) && stream.IsInitOnly &&
                offset.MetadataToken == 0x04000682 && offset.FieldType == typeof(long) && offset.IsInitOnly && Methods(assemblies[0], new[] {
                (0x06000E9D, "36AD138AE0BD2FD009A430B7209D42080089B55E377F6271718655C4FD2D1F18"),
                (0x06000E9F, "BE95D5903E6F6C7A0DA0782E7E9F7A30D9FCE34AF3B48CE6F404C85991CCC39B"),
                (0x06000EA0, "B9301AEB5ACEBF0087075BCEBB111CD4A50AAA4717323B97F316D770BD7C88C8"),
                (0x06000EA1, "F788BE633260906DE5BA1E798582061331B4927D367BB574EBF2D5E2EB53D53D"),
                (0x06000EB0, "F34B8BA34A936092D801FC879F4FADA68DBD75F4206CA352FCE496D95AAFFE13") }) ? type : null;
        }
        catch (Exception) { return null; } // Optional native cache-stream support never disables the Movie/MemoryStream paths.
    }
    private static BackdropBinding? BindBackdrops(MethodInfo movie, bool series)
    {
        try
        {
            var module = movie.Module;
            var helper = (MethodInfo)module.ResolveMethod(0x06000120)!;
            var movieConvert = (MethodInfo)module.ResolveMethod(0x060001DF)!;
            var seriesConvert = series ? (MethodInfo)module.ResolveMethod(0x06000375)! : null;
            var list = module.ResolveField(0x0400000C)!; var file = module.ResolveField(0x04000041)!; var iso = module.ResolveField(0x04000043)!;
            var average = module.ResolveField(0x04000044)!; var votes = module.ResolveField(0x04000045)!;
            var movieOwner = module.ResolveField(0x04000112)!; var seriesOwner = series ? module.ResolveField(0x040002D6)! : null;
            var image = iso.DeclaringType!;
            bool Field(FieldInfo f, Type owner, string name, Type type) => f.DeclaringType == owner && f.Name == name && f.FieldType == type && !f.IsStatic;
            bool Converter(MethodInfo method, FieldInfo owner, Type provider) => method.DeclaringType?.FullName == provider.FullName + "+<>c__DisplayClass3_0" &&
                method.Name == "<GetImages>b__3" && !method.IsStatic && method.ReturnType == typeof(RemoteImageInfo) && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { image }) &&
                method.GetMethodBody()!.ExceptionHandlingClauses.Count == 0 && Field(owner, method.DeclaringType, "<>4__this", provider);
            if (helper.DeclaringType?.FullName != "MovieDb.MovieDbProviderBase" || helper.Name != "GetBackdrops" || helper.IsStatic || !helper.IsFamily || helper.IsVirtual ||
                helper.ReturnType != typeof(IEnumerable<>).MakeGenericType(image) || !helper.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { list.DeclaringType! }) || helper.GetMethodBody()!.ExceptionHandlingClauses.Count != 0 ||
                image.FullName != "MovieDb.TmdbImage" || list.DeclaringType?.FullName != "MovieDb.TmdbImages" || !Field(list, list.DeclaringType, "<backdrops>k__BackingField", typeof(List<>).MakeGenericType(image)) ||
                !Field(file, image, "<file_path>k__BackingField", typeof(string)) || !Field(iso, image, "<iso_639_1>k__BackingField", typeof(string)) ||
                !Field(average, image, "<vote_average>k__BackingField", typeof(double)) || !Field(votes, image, "<vote_count>k__BackingField", typeof(int)) ||
                !Converter(movieConvert, movieOwner, movie.DeclaringType!) || series && !Converter(seriesConvert!, seriesOwner!, module.Assembly.GetType("MovieDb.MovieDbSeriesImageProvider")!) ||
                !Methods(module.Assembly, new[] { (0x06000120, "D344EA27365C96122C0BC33B0E0C7DDE502D8AB2F4186E432E43956B4FE9FB9D"), (0x060001DF, "399AC2EA8561D2B7B93016E43FF9691B3EC82B2C73D6951C37FBA6AF704F4D2E") }) ||
                series && Fingerprint(seriesConvert!) != "457DEF8D5146117F7CA6D9381C7AC8579784D75D35B86430061A931125BBDA39") return null;
            return new BackdropBinding(helper, movieConvert, seriesConvert, list, file, iso, average, votes, movieOwner, seriesOwner);
        }
        catch (Exception) { return null; }
    }
    private static bool VerifySeries(Type native)
    {
        var fetch = native.GetMethod("FetchImages", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        var module = native.Module;
        var move = (MethodInfo)module.ResolveMethod(0x0600041B)!;
        return native.FullName == "MovieDb.MovieDbSeriesImageProvider" && fetch?.MetadataToken == 0x06000141 &&
            fetch.ReturnType.IsGenericType && fetch.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) && fetch.ReturnType.GenericTypeArguments[0].FullName == "MovieDb.TmdbImages" &&
            move.DeclaringType?.FullName == "MovieDb.MovieDbSeriesProvider+<FetchMainResult>d__17" && module.ResolveField(0x0400033D) is { Name: "id", FieldType: var idType } && idType == typeof(string) &&
            module.ResolveField(0x0400033F)!.FieldType.FullName == "MovieDb.MovieDbSeriesProvider" &&
            module.ResolveField(0x040002DF) is { Name: "options", FieldType: var optionsType } && optionsType == typeof(RemoteImageFetchOptions) && Methods(native.Assembly, new[] {
            (0x0600011A, "82F46E967822690A46E584D8A543C4F977A26AF122145E952A0F2B6894B47257"),
            (0x0600013F, "417DE7C45FDD2FE039ECF36B25145B4C55203C6C2CB7C912C1F4714F23BB4327"),
            (0x06000140, "94F578CCF01A4F32E989ECB41E1249DEFA49F81A4CA8055A01FFFB741685E20E"),
            (0x06000141, "D39B0291E2555217457ABB82DF312D27CEC11225C132FFF66C9CB096378FEC2B"),
            (0x06000150, "D792A100EEA9891A5CA48C2A15582B3FA97E617F8478D0EE7C9CAD73350A98EC"),
            (0x06000151, "A0C95ED23F4FE0EC1C6E0219EB2485F765163A93661E4AF9229F665393446F2F"),
            (0x060003D0, "85B2ED27E73D7A7AFCF7330FAA80FA0D125C81CE22E1C1BDE05FD75F36B3AA5B"),
            (0x060003F4, "BA4004903BB79B02672822F32B5597D7E1BE23686A7EBAA29FA15932197807C9"),
            (0x060002F2, "776B14CB99F400F2C616F8B7C3926C9D9F42C57B650809CBAA597F00F8E2A492"),
            (0x06000376, "3DF52FC1B6B7F9466A41C06379BC0DBF49C597D3AB34737936AC2F6FC9A4B770"),
            (0x06000378, "05006FF301128B22E82FB90FA6767CE1FBD84F11FA0EDDD34DAF4477198F4A4A"),
            (0x0600041B, "EA3921394AE8AD35D98FA4A8065DFEBF7E4EB48EA3A683B93E0EB0EAF273B41D") });
    }

    private static IEnumerable<CodeInstruction> ObserveTvdbDetail(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var movie = __originalMethod.MetadataToken == 0x060003F0;
        var detail = (MethodInfo)__originalMethod.Module.ResolveMethod(movie ? 0x06000369 : 0x0600039E)!;
        var options = __originalMethod.Module.ResolveField(movie ? 0x04000254 : 0x040002E8)!;
        var provider = __originalMethod.Module.ResolveField(movie ? 0x04000256 : 0x040002EA)!;
        var capture = typeof(NativePosterOrdering).GetMethod(nameof(CaptureTvdbDetail), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(detail.ReturnType.GenericTypeArguments[0]);
        var code = instructions.ToList();
        if (code.Count(i => i.Calls(detail)) != 1) throw new InvalidOperationException("Native TVDB detail call count differs.");
        foreach (var instruction in code)
        {
            yield return instruction;
            if (!instruction.Calls(detail)) continue;
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, options);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, provider);
            yield return new CodeInstruction(OpCodes.Call, capture);
        }
    }
    private static void BeforeTvdbImages(object __instance, RemoteImageFetchOptions __0, out TvdbFrame __state)
    {
        __state = new TvdbFrame { Previous = CurrentTvdb.Value };
        CurrentTvdb.Value = null;
        try
        {
            var state = ReadState(); var query = Current.Value; var item = __0?.Item;
            var binding = state == null ? null : TvdbBindings(state).SingleOrDefault(b => ReferenceEquals(b.Provider, __instance));
            if (binding == null || query == null || query.Closed || query.Generation != state!.Generation || query.Kind != binding.Kind || query.TvdbId <= 0 || item == null ||
                !SameItem(query, item) || TvdbId(item) != query.TvdbId || !(binding.Kind == Kind.Movie ? item is Movie : item.GetType() == typeof(Series))) return;
            if (Interlocked.Increment(ref query.TvdbOperations) != 1) { query.TvdbLanguage = ""; return; }
            query.TvdbOptions = __0;
            __state.Scope = CurrentTvdb.Value = new TvdbOperation { Query = query, Binding = binding, Item = item, Options = __0! };
        }
        catch (Exception) { /* Observation setup must not change the native image Task. */ }
    }
    private static void DetachTvdb(TvdbFrame? frame)
    {
        if (frame == null || frame.Detached) return;
        CurrentTvdb.Value = frame.Previous; frame.Previous = null; frame.Detached = true;
    }
    private static void AfterTvdbImages(ref Task<IEnumerable<RemoteImageInfo>> __result, TvdbFrame __state)
    {
        DetachTvdb(__state);
        if (__state.Scope != null) __result = CompleteTvdbImages(__result, __state.Scope);
    }
    private static Exception? FinishTvdbImages(Exception? __exception, TvdbFrame? __state)
    {
        DetachTvdb(__state);
        if (__exception != null && __state?.Scope != null) __state.Scope.Closed = true;
        return __exception;
    }
    private static bool TvdbLive(TvdbOperation operation, State? state) => state != null && !operation.Closed && !operation.Query.Closed && operation.Query.Generation == state.Generation &&
        operation.Query.TvdbOperations == 1 && QueryIdentity(operation.Query) && SameItem(operation.Query, operation.Options.Item) && ReferenceEquals(operation.Options.Item, operation.Item) &&
        ReferenceEquals(operation.Binding, operation.Query.Kind == Kind.Movie ? state.TvdbMovie : state.TvdbSeries);
    private static Task<T> CaptureTvdbDetail<T>(Task<T> task, RemoteImageFetchOptions options, object provider)
    {
        try
        {
            var operation = CurrentTvdb.Value; var state = ReadState();
            if (operation == null || !TvdbLive(operation, state) || !ReferenceEquals(operation.Query, Current.Value) || !ReferenceEquals(operation.Options, options) ||
                !ReferenceEquals(operation.Binding.Provider, provider) || operation.Binding.Detail.ReturnType != typeof(Task<T>)) return task;
            if (Interlocked.Increment(ref operation.DetailRequests) != 1) { operation.Language = ""; return task; }
            return ReadTvdbDetail(task, operation, state!);
        }
        catch (Exception) { return task; }
    }
    private static async Task<T> ReadTvdbDetail<T>(Task<T> task, TvdbOperation operation, State state)
    {
        var result = await task.ConfigureAwait(false); // Preserve native DTO, fault and cancellation exactly.
        try
        {
            if (result == null || operation.DetailRequests != 1 || !TvdbLive(operation, ReadState()) || !ReferenceEquals(ReadState(), state)) return result!;
            var binding = operation.Binding;
            if ((int)binding.IdGetter.Invoke(result, null)! != operation.Query.TvdbId) return result;
            var raw = (string?)binding.LanguageGetter.Invoke(result, null);
            if (string.IsNullOrEmpty(raw) || raw.Length > 35 || !ValidLanguage(OriginalPosterPolicy.NormalizeLanguage(raw))) return result;
            var entry = binding.InstanceGetter.Invoke(null, null);
            if (entry != null) operation.Language = SeriesLanguage((string?)binding.ConvertLanguage.Invoke(entry, new object[] { raw }));
        }
        catch (Exception) { /* Converter/observer failure never faults the native Task. */ }
        return result;
    }
    private static async Task<IEnumerable<RemoteImageInfo>> CompleteTvdbImages(Task<IEnumerable<RemoteImageInfo>> task, TvdbOperation operation)
    {
        try
        {
            var images = await task.ConfigureAwait(false);
            try
            {
                if (images != null && operation.DetailRequests == 1 && TvdbLive(operation, ReadState())) operation.Query.TvdbLanguage = operation.Language;
            }
            catch (Exception) { /* Only an unchanged successful image operation may publish the fallback. */ }
            return images!;
        }
        finally { operation.Closed = true; operation.Language = ""; }
    }

    private static TvdbBinding? BindTvdb(IImageProvider[]? providers, Kind kind, IEnumerable<IPlugin>? plugins)
    {
        try
        {
            var name = kind == Kind.Movie ? "Movie" : "Series";
            var matches = providers?.Where(p => p.GetType().FullName == "Tvdb.Tvdb" + name + "ImageProvider").ToArray();
            if (matches?.Length != 1) return null;
            var provider = matches[0]; var type = provider.GetType(); var assembly = type.Assembly; var module = type.Module;
            if (!FilePin(assembly, "Tvdb", "1.6.7.0", "7245186e-d86d-4385-8dfb-3582ab8e1c2a", "FE796EC38E8C6A634C111A103E60A93570ED9625236E11A3E4BC274C2889498C", plugins)) return null;
            var movie = kind == Kind.Movie;
            var images = type.GetMethod("GetImages", new[] { typeof(RemoteImageFetchOptions), typeof(CancellationToken) });
            var move = (MethodInfo)module.ResolveMethod(movie ? 0x060003F0 : 0x0600041B)!;
            var detail = (MethodInfo)module.ResolveMethod(movie ? 0x06000369 : 0x0600039E)!;
            var dto = assembly.GetType("Tvdb." + name + "Data")!;
            if (images?.MetadataToken != (movie ? 0x0600035D : 0x06000393) || images.ReturnType != typeof(Task<IEnumerable<RemoteImageInfo>>) ||
                move.DeclaringType?.FullName != type.FullName + "+<GetImages>d__3" || detail.ReturnType != typeof(Task<>).MakeGenericType(dto) ||
                dto.GetProperty("id")?.PropertyType != typeof(int) || dto.GetProperty("originalLanguage")?.PropertyType != typeof(string) ||
                module.ResolveField(movie ? 0x04000254 : 0x040002E8) is not { Name: "options", FieldType: var optionsType } || optionsType != typeof(RemoteImageFetchOptions) ||
                module.ResolveField(movie ? 0x04000256 : 0x040002EA)!.FieldType != type) return null;
            // Constructor, caller, DTO, cache-hit and conversion contracts are all bound to this artifact.
            bool Ctor(int token, string hash) => module.ResolveMethod(token) is ConstructorInfo ctor && Convert.ToHexString(SHA256.HashData(ctor.GetMethodBody()!.GetILAsByteArray()!)) == hash;
            if (!Ctor(movie ? 0x0600035A : 0x06000390, "7FF4C67CFBDCDC0058E0FA5C5BC99CA3A7BEF224A6828782563DB04544D97644") ||
                !Ctor(0x06000109, "BE4DC349A7EACCDA4DBF94B1AF16055803B60D170C02F616073ACCC311BEC0FE") ||
                !Ctor(movie ? 0x06000361 : 0x06000398, movie ? "233AEDA89B9F484B96651034B4FCC6F3FB0F6B4C593C26FA9C24871358B3928C" : "8DB49EF7BE07E34F3FB7ADF0C0EFD1F7B2AFA9FCA7ECFE6BB69CE99AFD01D40A") ||
                !Methods(assembly, new[] {
                (0x06000107, "DB4AF2771A72E846D87E05849765A39B5A27A43C05CB27E97152C08D5050D1BE"),
                (0x06000112, "B53228A9E74ECAC2CA99E0924716EB9AD6CEE4B1B691EB6CFEBAB69647C73703"),
                (0x0600030C, "41FB585E2354BA2EAB6432B3684EB899098121E67B86298284BBD6F2B391C780") }) || !Methods(assembly, movie ? new[] {
                (0x060001F9, "C7FA75DD2AC81E50064368C2EBDF47099DB8846351299707CFF386E67DB3F02A"),
                (0x06000221, "63B09ADFF93BD8BB0D4A0FEA109EA8ACECCEFACF9F83B7511D173E6146862F40"),
                (0x0600035B, "57421F06B818DBE14F7EBC3341F84D33BD00CED2A241374F7554F79D8EF2E5CC"),
                (0x0600035C, "759C2FBC10A32EB5F25C52CF77331B3E0FCDD044F6BDD195D301C806C1C808A9"),
                (0x0600035D, "DB3B85B136B7151CB33221FD351519BD7557913D8F9E08F8A5AA786C8F66E734"),
                (0x0600035E, "083922762E896DA09FB27C3AA9B80BC85B81702D17641EBAED02A9C806AB9268"),
                (0x0600035F, "DB96CEEC9D82788F67D9628C8CA24AB595DAF7E52A6B724A6A07106F4656D26C"),
                (0x06000369, "5B22F2FE03BBF134B1975A09E7DA944A2FEDD4B66B6B0B5B75A8A02597348336"),
                (0x0600036A, "55251F4CEBA301D62374CECD94C7DBC076BA403295FBDEC1BAAEE40024B60E45"),
                (0x0600036B, "30CAFF7B9A3F5AE098D7C2BD1134DFDBB6075529F9B1B1E06243F11DCD747A9D"),
                (0x060003F0, "3CDA35FC17C7562E0C07B7FF3BF502F52B13E864CE0DA8660B03526C0B1B9052"),
                (0x060003F6, "9E3C249FFCF5546E767A96AF2F9D926364328B40407C21BFF314BB44F5EE9D6A"),
                (0x060003F8, "7846080CAB1A0DEB2E8298C90E234FFF5BEB083DDF466237BBCF1E6AA0FDACC3") } : new[] {
                (0x060002BE, "AAD4F591D78A85D97E614D4DE00CA6A17CE3CA970F0E204ED4A2C39ADC64BD93"),
                (0x060002D8, "D9D040F194DADB9AC1DEAC403BFA6AF895366A0732D18904AA3B25A282C3BD32"),
                (0x06000391, "31F953C4440A396CC02959B2C6D6258084AE17B232368127E84F5B13F193A763"),
                (0x06000392, "E48D932377F92E076FF98E62462D15FFF2E64B6FAA4D21A283CB1D4E7CC8A156"),
                (0x06000393, "0A4B02924FB8EBF9E4921818131534E238719F1E744FDE15128FDC108643BCA2"),
                (0x06000394, "A44219834E3FD270253930AFF88D4F701460DC4D86564DADFEB91A17D1565CEF"),
                (0x06000396, "D9510F15986C0C9966DC0372DC8F05C9103F09EE39C83106F6D791C073679A81"),
                (0x0600039E, "3CCAE4AA22CD88C1501986693559ECF235913A4CAFCB5CF4C0FD0D04A8C96D00"),
                (0x0600039F, "149E55F21D51A17835B72871535A456AC88660A01E532DBCE4248A12E508F59F"),
                (0x060003A2, "B8E299915C8380AFFEF04281EEDA0600DF1A46DE1F8D7AE98B5A33EFED8C5B9E"),
                (0x0600041B, "8A4F1EA78384E2FF2ADE68FD9F7353136E145B9D10CD714A0BDCAAFFA4759348"),
                (0x06000425, "8D8E3FEC30B63CA6F460B3D16D1374F705C0206D580E756922ECA2A9128F5309"),
                (0x06000427, "FB2DD4600F5E27DD9EC5F5611F7D58DC3763D5DE315880BAC52F012E3B089788") })) return null;
            return new TvdbBinding(kind, provider, images, move, detail, dto.GetProperty("id")!.GetMethod!, dto.GetProperty("originalLanguage")!.GetMethod!, (MethodInfo)module.ResolveMethod(0x06000107)!, (MethodInfo)module.ResolveMethod(0x06000112)!);
        }
        catch (Exception) { return null; }
    }

    private static (MethodInfo? Entry, MethodInfo? Move, FieldInfo? Manager) BindRefresh(MethodInfo query)
    {
        try
        {
            var module = query.Module;
            var type = module.Assembly.GetType("Emby.Providers.Manager.ItemImageProvider")!;
            var entry = (MethodInfo)module.ResolveMethod(0x060001F1)!;
            var move = (MethodInfo)module.ResolveMethod(0x06000401)!;
            var manager = module.ResolveField(0x040000D0)!;
            var result = module.Assembly.GetType("Emby.Providers.Manager.RefreshResult")!;
            if (!type.IsSealed || type.MetadataToken != 0x0200004C || entry.DeclaringType != type || entry.Name != "RefreshFromProvider" || !entry.IsPrivate || entry.IsStatic || entry.ReturnType != typeof(Task) ||
                !entry.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(BaseMetadataResult), typeof(LibraryOptions), typeof(IRemoteImageProvider), typeof(ImageRefreshOptions), typeof(bool), typeof(TypeOptions), typeof(int), typeof(List<ImageType>), result, typeof(CancellationToken) }) ||
                manager.DeclaringType != type || manager.Name != "_providerManager" || manager.FieldType != typeof(IProviderManager) || !manager.IsPrivate || !manager.IsInitOnly || manager.IsStatic ||
                move.DeclaringType?.DeclaringType != type || move.Name != "MoveNext" || move.ReturnType != typeof(void) || move.GetParameters().Length != 0 ||
                entry.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType != move.DeclaringType ||
                !Methods(module.Assembly, new[] { (0x060001F1, "214CA348C8FA3410516100FAED9554C68BD7FF0462D64D58515B1D308969E51D"), (0x06000401, "CCE564AD9755CD63AE6742AA28C7D62F14EA8C6235229E9E8D0402F23FDC04CF") })) return default;
            return Unpatched(move) ? (entry, move, manager) : default; // Read-only integrity guard; never install or remove a MoveNext patch.
        }
        catch (Exception) { return default; } // Optional callsite proof never disables existing language/typed-ordering adapters.
    }
    // No assembly load or load event: this optional outer caller exists only when the host has already loaded the pinned API.
    private static MethodInfo? BindApiCaller(out string detail)
    {
        detail = "not loaded";
        try
        {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "Emby.Api").ToArray();
        if (assemblies.Length == 0) return null;
        if (assemblies.Length != 1 || !FilePin(assemblies[0], "Emby.Api", "4.10.2.0", "0873fdaf-f3f8-45f4-8d9c-b647b1edf372", "2D5F2B4CB92DE964B478A3C50787515AEFB750C0041BC019E6BCCDFDF231AD4C"))
            throw new InvalidOperationException("Loaded native API contract differs");
        var module = assemblies[0].ManifestModule;
        var entry = (MethodInfo)module.ResolveMethod(0x0600076A)!;
        var move = (MethodInfo)module.ResolveMethod(0x06000937)!;
        var query = module.GetType("Emby.Api.Images.GetRemoteImages")!;
        if (entry.DeclaringType?.FullName != "Emby.Api.Images.RemoteImageService" || !entry.DeclaringType.IsSealed || !entry.IsPublic || entry.IsStatic || entry.Name != "Get" || entry.ReturnType != typeof(Task<object>) || !entry.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { query }) ||
            entry.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType != move.DeclaringType || move.DeclaringType?.MetadataToken != 0x020001F8 || !move.IsPrivate || !move.IsFinal || !move.IsVirtual || move.IsStatic || move.Name != "MoveNext" || move.ReturnType != typeof(void) || move.GetParameters().Length != 0 ||
            Fingerprint(move) != "5A5694D2D67A3BC58A696CCBEF9AAB2CAEA96C8EDB6E6407B67F973FDAC44E60")
            throw new InvalidOperationException("Loaded native API caller differs");
        detail = "verified loaded caller";
        return move;
        }
        catch (Exception) { detail = "loaded API contract unverified"; return null; }
    }
    private static bool VerifyHost(MethodInfo query)
    {
        var host = query.Module.Assembly;
        if (!FilePin(host, "Emby.Providers", "4.10.2.0", "29b37ec2-5f02-443c-a995-1f61c71f43c6", "16874EA37E2EC8152E7F2FA9F9D7FA51A284959DCE3A918694003D009B2B2A94") ||
            !FilePin(typeof(BaseItem).Assembly, "MediaBrowser.Controller", "4.10.2.0", "58dae430-7295-47bb-a62f-b26bf0d89604", "EAA6B9EDEBB21FE3AF9C272DEF8C913D9EC6B177B552E1B789FEE15CB59289DA") ||
            !FilePin(typeof(RemoteImageInfo).Assembly, "MediaBrowser.Model", "4.10.2.0", "3d56dcbb-6440-4e93-a3ad-05e588af05c2", "2B7DA29239811F8C035784DF7B3F48A1668E9537784B6524DDE43B3905572617") ||
            !FilePin(typeof(MediaBrowser.Common.Net.IHttpClient).Assembly, "MediaBrowser.Common", "4.10.2.0", "65f49672-52c6-4686-883f-8950136ddf8a", "9FEAAB42F6A5CD75831952D9CB73F7CB0176394F8C02DF64EF54E6B4CFBF7817")) return false;
        if (query.DeclaringType?.FullName != "Emby.Providers.Manager.ProviderManager" || query.MetadataToken != 0x06000247 || !query.IsPublic || query.IsStatic ||
            query.ReturnType != typeof(Task<IEnumerable<RemoteImageInfo>>) || !query.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(BaseItem), typeof(LibraryOptions), typeof(RemoteImageQuery), typeof(IDirectoryService), typeof(CancellationToken) }) ) return false;
        return Methods(host, new[] {
            (0x06000243, "B0B07ADD37336BE014D9CB8DB79B0AF739941E1E05F2D74F8037AA6A6FD9D6AE"),
            (0x06000247, "FFF05BD5B608109FB67AE61F5423CE5EFFD90D95AF372DA7F39C95FB9FC49CB6"),
            (0x060004A9, "8BDF425B75B88599AA9338B29CDD1C1096EC8CC7869265A5C75A098573AD5FE7"),
            (0x06000248, "B384A925163664A9FA1A57379D13E89D85D5B472D86D4ECC7438ACDD433C0F0A"),
            (0x060004AD, "C554507A92AE0533898CC2E369BDF88DE57E63A6A7D670B3F364C8A5B312FE42"),
            (0x060001F1, "214CA348C8FA3410516100FAED9554C68BD7FF0462D64D58515B1D308969E51D"),
            (0x06000401, "CCE564AD9755CD63AE6742AA28C7D62F14EA8C6235229E9E8D0402F23FDC04CF") });
    }

    internal static bool Verify(MethodInfo query, MethodInfo movie, IEnumerable<IPlugin>? plugins = null)
    {
        var provider = movie.Module.Assembly;
        if (!VerifyHost(query) || !FilePin(provider, "MovieDb", "2.0.0.0", "bf190786-42b7-4923-aea6-1851feea4259", "759EDC7BF7A3B43A2BBDC34154471811F1931A7A7EF59DD25E9B00B4A6B5FC86", plugins)) return false;
        if (movie.DeclaringType?.FullName != "MovieDb.MovieDbImageProvider" || movie.MetadataToken != 0x060000DB || !movie.IsPrivate || movie.IsStatic ||
            !movie.ReturnType.IsGenericType || movie.ReturnType.GetGenericTypeDefinition() != typeof(Task<>) || movie.ReturnType.GenericTypeArguments[0].FullName != "MovieDb.MovieDbProvider+CompleteMovieData") return false;
        var imageMove = (MethodInfo)provider.ManifestModule.ResolveMethod(0x060001E0)!;
        var imageEntry = (MethodInfo)provider.ManifestModule.ResolveMethod(0x060000D8)!;
        var imageType = imageMove.DeclaringType!;
        bool Field(int token, string name, Type type) => provider.ManifestModule.ResolveField(token) is { } field && field.DeclaringType == imageType && field.Name == name && field.FieldType == type && field.IsPublic && !field.IsStatic;
        if (!imageType.IsValueType || imageType.MetadataToken != 0x0200003D || imageType.DeclaringType != movie.DeclaringType || imageMove.Name != "MoveNext" || !imageMove.IsPrivate || !imageMove.IsFinal || !imageMove.IsVirtual || imageMove.ReturnType != typeof(void) || imageMove.GetParameters().Length != 0 ||
            imageEntry.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType != imageType ||
            !Field(0x04000113, "<>1__state", typeof(int)) || !Field(0x04000115, "<>4__this", movie.DeclaringType!) || !Field(0x04000116, "options", typeof(RemoteImageFetchOptions))) return false;
        return Methods(provider, new[] {
            (0x060000D8, "D383CB11E28FEEF3A450B3D88BAB3CC5B51A26B26BCA6A1511F907662BF4C55A"),
            (0x060001E0, "0F5386B530DD99E5169D972D5EF038CDE1E25409437395067B8842B6A7E40FA0"),
            (0x060000DB, "AC68B683A8410FAB6F639C59D5212124A5A09D5D077687CB4C72F72BBC827E7B"),
            (0x060001E2, "16A2C0ED59236CFC9AE2B642FB6D918204D3C9648FBC16968CC2D029FB3541BC"),
            (0x06000103, "81061EFAF3B05AA7D7F11DE23BA4FE67232F6A6C6EEB6261309E732A375135A3"),
            (0x06000104, "5D8908229CCAE9D249879C58FDB538F778A0B098177EFF66171F6FE3BE75525E"),
            (0x060002C1, "263AC86765C3EBA08410C80F40A5C155AA63EDB3585EC92CE21469353DF9B5BC"),
            (0x06000287, "F8DCF63FB98B7D8D616478FAEEBAE6CE0A25971FF284B1D6A93B13F3314432B0"),
            (0x0600028B, "30D97258E5633D4106041CB730DB7907D015E76A12C6D621EF2B1CC7068172AD") });
    }
    private static bool FilePin(Assembly assembly, string name, string version, string mvid, string hash, IEnumerable<IPlugin>? plugins = null)
    {
        if (assembly.GetName().Name != name || assembly.GetName().Version?.ToString() != version || assembly.ManifestModule.ModuleVersionId != Guid.Parse(mvid)) return false;
        var path = assembly.Location;
        if (string.IsNullOrEmpty(path))
        {
            var matches = plugins?.Where(p => p.GetType().Assembly == assembly && p.GetType().FullName == name + ".Plugin").ToArray();
            if (matches?.Length != 1) return false;
            path = matches[0].AssemblyFilePath;
        }
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        using var file = File.OpenRead(path);
        using (var pe = new PEReader(file, PEStreamOptions.LeaveOpen))
        {
            if (!pe.HasMetadata) return false;
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) return false;
            var definition = metadata.GetAssemblyDefinition();
            if (metadata.GetString(definition.Name) != name || definition.Version.ToString() != version || metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != Guid.Parse(mvid)) return false;
        }
        file.Position = 0;
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(file)) == hash;
    }
    private static bool Methods(Assembly assembly, (int Token, string Hash)[] methods) => methods.All(p => Fingerprint((MethodInfo)assembly.ManifestModule.ResolveMethod(p.Token)!) == p.Hash);
    internal static string Fingerprint(MethodInfo method)
    {
        var body = method.GetMethodBody()!;
        var locals = body.LocalSignatureMetadataToken == 0 ? "" : Convert.ToHexString(method.Module.ResolveSignature(body.LocalSignatureMetadataToken));
        var eh = string.Join(";", body.ExceptionHandlingClauses.Select(e => $"{(int)e.Flags},{e.TryOffset},{e.TryLength},{e.HandlerOffset},{e.HandlerLength},{(e.Flags == ExceptionHandlingClauseOptions.Clause ? e.CatchType!.FullName : e.Flags == ExceptionHandlingClauseOptions.Filter ? e.FilterOffset.ToString(CultureInfo.InvariantCulture) : "")}"));
        var value = $"{method.MetadataToken:X8}|{method.Attributes}|{method.GetMethodImplementationFlags()}|{Convert.ToHexString(body.GetILAsByteArray()!)}|{body.MaxStackSize}|{body.InitLocals}|{body.LocalSignatureMetadataToken:X8}|{locals}|{eh}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try { Remove(); Detail = "Inactive. " + Coverage; }
            catch { Detail = "Inactive callback; owned cleanup pending. " + Coverage; }
        }
    }
    private static void Remove()
    {
        foreach (var method in installed) patcher?.Unpatch(method, HarmonyPatchType.All, Owner);
        installed = Array.Empty<MethodInfo>();
    }
}
