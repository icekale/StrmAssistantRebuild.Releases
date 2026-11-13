using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using MediaBrowser.Common.Plugins;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Options;
using CacheMode = StrmAssistantRebuild.Options.MetadataBuildOptions.MetadataCacheOption;

namespace StrmAssistantRebuild.Provider;

/// <summary>Pinned typed detail DTO cache before native request creation. Native networking and stream disposal remain native.</summary>
public static class NativeMovieDbCache
{
    private const string Owner = "StrmAssistantRebuild.NativeMovieDbCache.2.0";
    private const string Hash = "759EDC7BF7A3B43A2BBDC34154471811F1931A7A7EF59DD25E9B00B4A6B5FC86";
    private static readonly Guid Mvid = new("bf190786-42b7-4923-aea6-1851feea4259");
    private const string EpisodePattern = "https://api.themoviedb.org/3/tv/{0}/season/{1}/episode/{2}?api_key={3}&append_to_response=images,external_ids,credits,videos";
    private static readonly object Gate = new();
    private static readonly List<MethodInfo> installed = new();
    private static Harmony? patcher;
    private static State? active;
    private sealed record Contract(string Kind, MethodInfo Method, MethodInfo Move, Type Dto,
        Func<JsonElement, object> Completed, Func<object, Request, object> Wrap);
    private sealed record State(Assembly Assembly, CacheMode Mode, MovieDbResponseCache Cache, Contract[] Contracts, MethodInfo[] Barriers, MethodInfo[] MetadataAliases);
    private sealed record Request(State State, Contract Contract, MovieDbResponseCache.Key Key, CancellationToken Token) { internal bool Write; }
    public static bool IsEnabled => Volatile.Read(ref active) != null;
    /// <summary>M155: reads served from the original plugin's cache/override layout.</summary>
    public static long OriginalReads => Volatile.Read(ref active)?.Cache.OriginalReads ?? 0;
    public static long PatchesApplied => Volatile.Read(ref active)?.Cache.PatchesApplied ?? 0;
    public static string Detail { get; private set; } = "Inactive; native detail retrieval retained.";

    public static bool Configure(Version hostVersion, IEnumerable<IPlugin>? plugins, MetadataEnhanceOptions options,
        EmbyPathPolicy paths, string defaultRoot, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try
            {
                Remove();
                var value = options.MetadataBuildOptions.MetadataCacheMode?.Trim();
                if (!Enum.GetNames<CacheMode>().Any(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase)) ||
                    !Enum.TryParse<CacheMode>(value, true, out var mode))
                { detail = Detail = "Inactive: unknown metadata cache mode."; return false; }
                // None returns before even validating paths, enumerating plugins or opening backing artifacts.
                if (mode == CacheMode.None) { detail = Detail = "Inactive; metadata cache None (zero cache IO)."; return true; }
                var selected = (plugins ?? Array.Empty<IPlugin>()).Where(p => p.GetType().FullName == "MovieDb.Plugin" &&
                    p.GetType().Assembly.GetName().Name == "MovieDb" && p.Name == "TheMovieDb").ToArray();
                if (hostVersion != new Version(4, 10, 2, 0) || selected.Length != 1 || !VerifyArtifact(selected[0]))
                { detail = Detail = "Inactive: pinned TheMovieDb host/plugin artifact differs."; return false; }
                var assembly = selected[0].GetType().Assembly;
                var contracts = new[]
                {
                    Bind(assembly, "movie", "MovieDb.MovieDbProvider", "MovieDb.MovieDbProvider+CompleteMovieData", new[] { typeof(string), typeof(bool), typeof(string), typeof(CancellationToken) }, 0x06000104, 80, "D4F5B2C711F8518DB62E33755466E777D64C659399B645F940C5575804925BAC"),
                    Bind(assembly, "series", "MovieDb.MovieDbSeriesProvider", "MovieDb.MovieDbSeriesProvider+SeriesRootObject", new[] { typeof(string), typeof(string), typeof(CancellationToken) }, 0x06000150, 79, "29FED7F65FA79648FAE8103DD2ED04593E0F3D24D59FBD40B41F2DBCF338BB85"),
                    Bind(assembly, "season", "MovieDb.MovieDbProviderBase", "MovieDb.MovieDbSeasonProvider+SeasonRootObject", new[] { typeof(string), typeof(int), typeof(string), typeof(CancellationToken) }, 0x0600010D, 88, "CBDE8E47633434201463E9D879B2B9AC4775EDF01A936B87516EB900273FA3EA"),
                    Bind(assembly, "episode", "MovieDb.MovieDbProviderBase", "MovieDb.MovieDbProviderBase+EpisodeRootObject", new[] { typeof(string), typeof(string), typeof(int), typeof(int), typeof(string), typeof(CancellationToken) }, 0x0600010F, 106, "A094ABE693818D30418F7E4CF0BAD4ACAF9EA0F863543751378E77F11BECB380")
                };
                var root = options.MetadataBuildOptions.MetadataCacheRootFolder?.Trim();
                var metadataCaller = MovieMetadataCaller(assembly, out var metadataAliases);
                var barriers = new[]
                {
                    MovieCaller(assembly),
                    metadataCaller,
                    MovieSearchCaller(assembly),
                    Barrier(assembly, 0x0600010E, 19, "96D19827F977544067E476CEA07981CFFA199EC897524D972BAE833808F2BEF4"),
                    Barrier(assembly, 0x060002F0, 415, "01783BB250C95D1DB52D4520AFCC984C99355992A373A5BAD8A21B2680ABB3C9"),
                    Barrier(assembly, 0x06000376, 214, "F90731A8EB864B3DD9F282AF265566B8EA66CCD71E9C5462CFBCB49204D5DB41")
                };
                var state = new State(assembly, mode, new MovieDbResponseCache(paths, string.IsNullOrWhiteSpace(root) ? defaultRoot : root), contracts, barriers, metadataAliases);
                if (Foreign(state)) { detail = Detail = "Inactive: foreign native detail contract owner."; return false; }
                foreach (var contract in contracts)
                {
                    installed.Add(contract.Method);
                    (patcher ??= new Harmony(Owner)).Patch(contract.Method,
                        prefix: new HarmonyMethod(typeof(NativeMovieDbCache).GetMethod(nameof(Before), BindingFlags.NonPublic | BindingFlags.Static)!),
                        postfix: new HarmonyMethod(typeof(NativeMovieDbCache).GetMethod(nameof(After), BindingFlags.NonPublic | BindingFlags.Static)!));
                }
                // D126 image 1E2 and D128 metadata199/search2C3 warm controls require
                // these callers, not 103. Each has raw EH and fresh no-op cancellation/fault proof.
                // 199 is bound once as Movie. Known reference-type aliases report the same
                // handle, but D128 observed the prefix only on Movie, not all generic T.
                foreach (var barrier in barriers)
                {
                    installed.Add(barrier);
                    patcher!.Patch(barrier, prefix: new HarmonyMethod(typeof(NativeMovieDbCache).GetMethod(nameof(CallerBarrier), BindingFlags.NonPublic | BindingFlags.Static)!));
                }
                Volatile.Write(ref active, state);
                detail = Detail = "Configured pinned Movie/Series/Season/Episode TMDB DTO cache; reconstructed layout, original TMDB override JSON; native transport retained.";
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref active, null);
                Detail = "Inactive: native detail cache setup failed (" + error.GetType().Name + ").";
                try { Remove(); } catch { Detail += " Owned cleanup pending."; }
                detail = Detail; return false;
            }
        }
    }
    private static Contract Bind(Assembly assembly, string kind, string typeName, string dtoName, Type[] parameters, int token, int length, string hash)
    {
        var type = assembly.GetType(typeName, true)!;
        var method = type.GetMethod("FetchMainResult", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, parameters, null)!;
        var dto = assembly.GetType(dtoName, true)!;
        var il = method?.GetMethodBody()?.GetILAsByteArray();
        var move = method?.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null || method.Module.ModuleVersionId != Mvid || method.MetadataToken != token || method.IsStatic ||
            method.GetMethodImplementationFlags() != MethodImplAttributes.IL || method.ReturnType != typeof(Task<>).MakeGenericType(dto) ||
            il?.Length != length || Convert.ToHexString(SHA256.HashData(il)) != hash || move == null)
            throw new InvalidDataException("Native detail signature or IL differs.");
        // These generic methods are ordinary cached delegates, NEVER registered as Harmony callbacks.
        var completed = typeof(NativeMovieDbCache).GetMethod(nameof(Completed), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(dto)
            .CreateDelegate<Func<JsonElement, object>>();
        var wrap = typeof(NativeMovieDbCache).GetMethod(nameof(Wrap), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(dto)
            .CreateDelegate<Func<object, Request, object>>();
        return new(kind, method, move, dto, completed, wrap);
    }
    private static bool Before(object __instance, MethodBase __originalMethod, object[] __args, ref object __result, out Request? __state)
    {
        __state = null;
        var state = Volatile.Read(ref active);
        if (state == null || __instance.GetType().Assembly != state.Assembly) return true;
        try
        {
            if (Foreign(state, true)) { Volatile.Write(ref active, null); Detail = "Inactive: native detail ownership changed."; return true; }
            var contract = state.Contracts.Single(c => c.Method == __originalMethod);
            var token = (CancellationToken)__args[^1];
            // Let native construct its own canceled Task and preserve original behavior.
            if (token.IsCancellationRequested) return true;
            var args = __args;
            MovieDbResponseCache.Key? key = contract.Kind switch
            {
                "movie" when args[1] is true => MovieDbResponseCache.Key.Create("movie", args[0] as string, args[2] as string),
                "series" => MovieDbResponseCache.Key.Create("series", args[0] as string, args[1] as string),
                "season" => MovieDbResponseCache.Key.Create("season", args[0] as string, args[2] as string, (int)args[1]),
                "episode" when args[0] as string == EpisodePattern => MovieDbResponseCache.Key.Create("episode", args[1] as string, args[4] as string, (int)args[2], (int)args[3]),
                _ => null
            };
            if (key == null) return true;
            var request = new Request(state, contract, key, token);
            try { if (key.Kind == "series") NativePosterOrdering.BeginSeriesCacheRequest(key.Id, key.Language); }
            catch (Exception) { /* Observation never changes native cache hit/miss behavior. */ }
            // Override always wins in every non-None mode. Online never reads the automatic cache.
            var json = Read(request, true, out var language);
            if (json == null && state.Mode == CacheMode.PreferCacheThenOnline) json = Read(request, false, out language);
            if (json.HasValue)
            {
                // M161: original patches/ file applied over the cached or override DTO.
                var ready = contract.Completed(state.Cache.ApplyPatch(json.Value, key, token));
                token.ThrowIfCancellationRequested();
                __result = ready;
                // Observation is separate from cache decoding: a poster bridge fault must not turn a hit into HTTP.
                try
                {
                    if (key.Kind == "series" && ReferenceEquals(Volatile.Read(ref active), state)) NativePosterOrdering.SeedSeriesCacheLanguage(key.Id, key.Language, language);
                }
                catch (Exception) { }
                return false;
            }
            request.Write = state.Mode is CacheMode.PreferCacheThenOnline or CacheMode.PreferOnlineWithCache;
            // M161: online results also get the original patch (any non-None mode); only the two caching modes write.
            if (request.Write || File.Exists(state.Cache.PatchPath(key))) __state = request;
        }
        catch (Exception) { __state = null; } // Cache/setup faults fall through to the original call, not another network client.
        return true;
    }
    private static JsonElement? Read(Request request, bool isOverride, out string? language)
    {
        language = null;
        try { return request.State.Cache.Read(request.Key, isOverride, request.Token, out language); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }
    private static void After(ref object __result, Request? __state)
    {
        if (__state == null || __result == null) return;
        try { __result = __state.Contract.Wrap(__result, __state); }
        catch (Exception) { /* Keep the exact original Task on bridge failure. */ }
    }
    private static object Completed<T>(JsonElement data) => Task.FromResult(data.Deserialize<T>() ?? throw new JsonException("Empty native detail DTO."));
    private static object Wrap<T>(object task, Request request) => Persist((Task<T>)task, request);
    private static async Task<T> Persist<T>(Task<T> original, Request request)
    {
        // This await is outside the cache catch: faults/cancellation belong to the native provider and propagate unchanged.
        var result = await original.ConfigureAwait(false);
        if (result == null || request.Token.IsCancellationRequested || !ReferenceEquals(Volatile.Read(ref active), request.State)) return result;
        if (!request.Write) return Patched(result, request);
        string? language = null;
        Func<bool>? posterActive = null;
        try
        {
            if (request.Key.Kind == "series" && result.GetType() == request.Contract.Dto &&
                request.Contract.Dto.GetProperty("id")?.GetValue(result) is int id && id == request.Key.Id)
                language = NativePosterOrdering.ReadSeriesCacheLanguage(id, request.Key.Language, out posterActive);
        }
        catch (Exception) { /* Optional observation failure still permits the ordinary native DTO cache write. */ }
        try
        {
            // If an observed origin goes stale during serialization/IO, skip this write, never publish stale language.
            await request.State.Cache.WriteAsync(request.Key, result, request.Token,
                () => ReferenceEquals(Volatile.Read(ref active), request.State) && (language == null || posterActive?.Invoke() == true), language).ConfigureAwait(false);
        }
        catch (Exception) { /* A cache fault never changes a successfully completed native DTO. */ }
        return Patched(result, request);
    }
    /// <summary>The cache keeps the native DTO; the caller receives it with the original patch applied (if any).</summary>
    private static T Patched<T>(T result, Request request)
    {
        try
        {
            if (!File.Exists(request.State.Cache.PatchPath(request.Key))) return result;
            var data = JsonSerializer.SerializeToElement(result);
            var patched = request.State.Cache.ApplyPatch(data, request.Key, request.Token);
            return patched.ValueKind == JsonValueKind.Undefined || patched.GetRawText() == data.GetRawText() ? result : patched.Deserialize<T>() ?? result;
        }
        catch (Exception) { return result; } // A patch fault never changes a successfully completed native DTO.
    }
    private static void CallerBarrier() { }
    private static MethodInfo Barrier(Assembly assembly, int token, int length, string hash)
    {
        var method = (MethodInfo)assembly.ManifestModule.ResolveMethod(token)!;
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (method.Module.ModuleVersionId != Mvid || il?.Length != length || Convert.ToHexString(SHA256.HashData(il)) != hash)
            throw new InvalidDataException("Native detail caller IL differs.");
        return method;
    }
    private static MethodInfo MovieCaller(Assembly assembly)
    {
        var method = Barrier(assembly, 0x060001E2, 208, "336C46322DFCA9A144A8FAEDA6B4008C8CF3362224867A578CBE5EDA70A53C49");
        var type = assembly.GetType("MovieDb.MovieDbImageProvider+<GetMovieInfo>d__6", true)!;
        var entry = (MethodInfo)assembly.ManifestModule.ResolveMethod(0x060000DB)!;
        var eh = method.GetMethodBody()!.ExceptionHandlingClauses;
        if (method.DeclaringType != type || type.MetadataToken != 0x0200003E || !type.IsValueType ||
            method.Name != "MoveNext" || !method.IsPrivate || method.IsStatic || method.ReturnType != typeof(void) ||
            method.GetParameters().Length != 0 || method.GetMethodImplementationFlags() != MethodImplAttributes.IL ||
            entry.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType != type ||
            eh.Count != 1 || eh[0].Flags != ExceptionHandlingClauseOptions.Clause || eh[0].CatchType != typeof(Exception) ||
            eh[0].TryOffset != 7 || eh[0].TryLength != 155 || eh[0].HandlerOffset != 162 || eh[0].HandlerLength != 25)
            throw new InvalidDataException("Native movie caller shape or EH differs.");
        return method;
    }
    private static MethodInfo MovieMetadataCaller(Assembly assembly, out MethodInfo[] aliases)
    {
        var open = assembly.GetType("MovieDb.GenericMovieDbInfo`1+<FetchMovieData>d__3", true)!;
        var movie = typeof(MediaBrowser.Controller.Entities.Movies.Movie);
        var method = open.MakeGenericType(movie).GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var raw = Barrier(assembly, 0x06000199, 374, "754C334771DA6315A12BD769EEB555A724650AA36F01615DDF05608B7849A14F");
        var entry = assembly.GetType("MovieDb.GenericMovieDbInfo`1", true)!.MakeGenericType(movie)
            .GetMethod("FetchMovieData", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (raw.DeclaringType != open || open.MetadataToken != 0x0200002D || !open.IsGenericTypeDefinition ||
            open.GetGenericArguments().Length != 1 || method.ContainsGenericParameters || entry.MetadataToken != 0x060000B6 ||
            entry.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType != open)
            throw new InvalidDataException("Native movie metadata caller binding differs.");
        SingleCatchCaller(method, 0x06000199, 7, 321, 328, 25);
        aliases = new[] { typeof(MediaBrowser.Controller.Entities.MusicVideo), typeof(MediaBrowser.Controller.Entities.Trailer), typeof(MediaBrowser.Controller.Entities.Video) }
            .Select(t => open.MakeGenericType(t).GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance)!).ToArray();
        // Public JIT preparation only: no native provider execution/network/cache IO.
        // The declaring-T handles are required by the cold D128 control; plain PrepareMethod was insufficient.
        RuntimeHelpers.PrepareMethod(method.MethodHandle, new[] { movie.TypeHandle });
        return method;
    }
    private static MethodInfo MovieSearchCaller(Assembly assembly)
    {
        var method = Barrier(assembly, 0x060002C3, 1351, "AF8D88A5E580FC62D7DA3BBE96218078F881EE137B7417B141E7565232F4F3D2");
        var type = assembly.GetType("MovieDb.MovieDbProvider+<GetMovieSearchResults>d__8", true)!;
        var entry = (MethodInfo)assembly.ManifestModule.ResolveMethod(0x06000100)!;
        if (method.DeclaringType != type || type.MetadataToken != 0x0200005C || type.ContainsGenericParameters ||
            entry.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType != type)
            throw new InvalidDataException("Native movie search caller binding differs.");
        SingleCatchCaller(method, 0x060002C3, 14, 1263, 1277, 39);
        return method;
    }
    private static void SingleCatchCaller(MethodInfo method, int token, int tryOffset, int tryLength, int handlerOffset, int handlerLength)
    {
        var eh = method.GetMethodBody()!.ExceptionHandlingClauses;
        if (method.MetadataToken != token || !method.DeclaringType!.IsValueType || method.Name != "MoveNext" ||
            !method.IsPrivate || method.IsStatic || method.ReturnType != typeof(void) || method.GetParameters().Length != 0 ||
            method.GetMethodImplementationFlags() != MethodImplAttributes.IL || eh.Count != 1 ||
            eh[0].Flags != ExceptionHandlingClauseOptions.Clause || eh[0].CatchType != typeof(Exception) ||
            eh[0].TryOffset != tryOffset || eh[0].TryLength != tryLength || eh[0].HandlerOffset != handlerOffset || eh[0].HandlerLength != handlerLength)
            throw new InvalidDataException("Native movie caller shape or EH differs.");
    }
    // These callers have no compatibility exceptions: setup requires empty metadata;
    // an active generation requires exactly our benign prefix, including all six categories.
    private static bool MovieCallerChanged(MethodInfo method, bool configured)
    {
        var p = Harmony.GetPatchInfo(method);
        if (p == null) return configured;
        if (p.Postfixes.Count + p.Transpilers.Count + p.Finalizers.Count + p.InnerPrefixes.Count + p.InnerPostfixes.Count != 0) return true;
        if (!configured) return p.Prefixes.Count != 0;
        return p.Prefixes.Count != 1 || p.Prefixes.Single() is var patch &&
            (patch.owner != Owner || patch.PatchMethod != typeof(NativeMovieDbCache).GetMethod(nameof(CallerBarrier), BindingFlags.NonPublic | BindingFlags.Static) ||
             patch.priority != Priority.Normal || patch.before.Length != 0 || patch.after.Length != 0 || patch.debug);
    }
    private static bool Foreign(State state, bool configured = false) => state.Barriers.Take(3).Any(m => MovieCallerChanged(m, configured)) ||
        state.MetadataAliases.Any(m => MovieCallerChanged(m, false)) ||
        state.Contracts.SelectMany(c => new[] { c.Method, c.Move }).Concat(state.Barriers)
        .Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(o => o != Owner) == true && !NativePosterOrdering.OwnsSeriesResponsePatch(m));
    private static bool VerifyArtifact(IPlugin plugin)
    {
        var assembly = plugin.GetType().Assembly;
        if (assembly.GetName().Name != "MovieDb" || assembly.GetName().Version != new Version(2, 0, 0, 0) || assembly.ManifestModule.ModuleVersionId != Mvid) return false;
        var path = string.IsNullOrEmpty(assembly.Location) ? plugin.AssemblyFilePath : assembly.Location;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        using var file = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(file)) == Hash;
    }
    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try { Remove(); Detail = "Inactive; native detail retrieval retained."; }
            catch { Detail = "Inactive callback; owned cache detour cleanup pending."; }
        }
    }
    private static void Remove()
    {
        foreach (var method in installed)
        {
            // Do not unpatch the generic aliases: they were never installed by this owner.
            // These callers may contain unrelated same-owner callbacks; remove only our prefix.
            if (method.MetadataToken is 0x060001E2 or 0x06000199 or 0x060002C3)
            {
                var callback = typeof(NativeMovieDbCache).GetMethod(nameof(CallerBarrier), BindingFlags.NonPublic | BindingFlags.Static)!;
                // Harmony's method-specific Unpatch spans owners: retain an ambiguous callback for retry.
                var info = Harmony.GetPatchInfo(method);
                if (info != null && info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.InnerPrefixes).Concat(info.InnerPostfixes)
                    .Any(p => p.PatchMethod == callback && p.owner != Owner))
                    throw new InvalidOperationException("Native movie caller cleanup has overlapping callback owners.");
                patcher?.Unpatch(method, callback);
            }
            else patcher?.Unpatch(method, HarmonyPatchType.All, Owner);
        }
        installed.Clear();
    }
}
