using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Data;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace StrmAssistantRebuild.Common;

/// <summary>Fingerprint-locked repository boundary, AFTER LibraryManager normalizes user/parent/search exclusions.</summary>
public static class ChineseSearchCompatibility
{
    private const string Owner = "StrmAssistantRebuild.ChineseSearch.4.10.2";
    private const string RepositoryType = "Emby.Server.Implementations.Data.SqliteItemRepository";
    private static readonly Guid ExpectedMvid = new("2009e43d-fa42-4e57-a902-2784a8306072");
    private static readonly object Sync = new();
    private static readonly AsyncLocal<int> Depth = new();
    private static readonly PropertyInfo[] QueryProperties = typeof(InternalItemsQuery).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0)
        .OrderBy(p => p.Name is "Parent" or "Ancestors" ? 0 : 1).ToArray();
    private static readonly HashSet<string> SupportedTypes = new(new[] { "Movie", "Series", "Season", "Episode", "BoxSet", "Video", "Trailer", "Audio", "MusicVideo", "Book", "Game", "Photo", "PhotoAlbum" }, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MixedMediaTypes = new(new[] { "Movie", "Series", "Episode", "Video", "Trailer", "Audio", "MusicVideo", "Book", "Game", "Photo" }, StringComparer.OrdinalIgnoreCase);
    private static readonly string[] DefaultSearchExclusions = { "Folder", "Channel", "Season", "CollectionFolder", "UserView", "LiveTVSeries" };
    private static readonly List<MethodInfo> installed = new();
    private static Harmony? patcher;
    private static Registration? active;
    public static bool IsEnabled => Volatile.Read(ref active) != null;

    /// <summary>Checks platform signatures, not index readiness or enabled state; no detours or queries.</summary>
    public static bool Probe(Version hostVersion, IItemRepository? repository, out string detail) =>
        Probe(hostVersion, repository?.GetType(), out detail);

    /// <summary>Metadata-only overload permits offline inspection without triggering repository static initialization.</summary>
    public static bool Probe(Version hostVersion, Type? repositoryType, out string detail)
    {
        try
        {
            if (hostVersion != new Version(4, 10, 2, 0)) { detail = "Host version differs from validated 4.10.2.0."; return false; }
            if (repositoryType == null) { detail = "Repository service missing."; return false; }
            var methods = SearchMethods(repositoryType);
            if (methods.Any(m => m == null || !VerifyMethod(m))) { detail = "Repository public signature/MVID/IL differs."; return false; }
            if (methods.Any(m => Harmony.GetPatchInfo(m!)?.Owners.Any(owner => owner != Owner) == true))
            { detail = "Foreign patch owns repository search."; return false; }
            detail = "Repository item/list/type signatures verified; conditional media-only mixed queries and explicit supported types, not full search semantics.";
            return true;
        }
        catch (Exception ex) { detail = "Search probe failed: " + ex.GetType().Name + ": " + ex.Message; return false; }
    }

    public static bool Configure(Version hostVersion, IItemRepository repository, ChineseSearchIndex index, bool enabled, out string detail)
    {
        lock (Sync)
        {
            Volatile.Write(ref active, null);
            try
            {
                RemovePatches();
                if (!enabled) { detail = "Disabled; native repository search restored."; return true; }
                if (hostVersion != new Version(4, 10, 2, 0)) { detail = "Host version differs from validated 4.10.2.0."; return false; }
                if (!index.IsReady) { detail = "Candidate index is not ready; native search retained."; return false; }
                var type = repository.GetType();
                var methods = SearchMethods(type);
                if (methods.Any(m => m is null || !VerifyMethod(m))) { detail = "Repository public signature/IL fingerprint differs; native search retained."; return false; }
                if (methods.Any(m => Harmony.GetPatchInfo(m!)?.Owners.Any() == true)) { detail = "Repository already patched; foreign patches retained."; return false; }
                var finalizer = new HarmonyMethod(typeof(ChineseSearchCompatibility).GetMethod(nameof(After), BindingFlags.Static | BindingFlags.NonPublic)!);
                foreach (var method in methods)
                {
                    installed.Add(method!);
                    var name = method!.Name == "GetItems" ? nameof(BeforeItems) : method.Name == "GetItemTypes" ? nameof(BeforeTypes) : nameof(BeforeList);
                    var prefix = new HarmonyMethod(typeof(ChineseSearchCompatibility).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!);
                    (patcher ??= new Harmony(Owner)).Patch(method!, prefix: prefix, finalizer: finalizer);
                }
                Volatile.Write(ref active, new Registration(repository, index));
                detail = "Enabled: repository item/list/type queries; candidate intersection, native access filters, conditional media-only mixed coverage; enhanced stable order is not native FTS Rank.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Chinese search compatibility inactive: " + error.GetType().Name + ": " + error.Message;
                try { RemovePatches(); } catch (Exception cleanup) { detail += "; cleanup pending: " + cleanup.Message; }
                return false;
            }
        }
    }
    public static void Disable()
    {
        lock (Sync) { Volatile.Write(ref active, null); try { RemovePatches(); } catch { } }
    }
    private static void RemovePatches()
    {
        foreach (var method in installed.ToArray())
        {
            patcher?.Unpatch(method, HarmonyPatchType.Prefix, Owner);
            patcher?.Unpatch(method, HarmonyPatchType.Finalizer, Owner);
            installed.Remove(method);
        }
    }
    private static MethodInfo?[] SearchMethods(Type type) => new[] { "GetItemList", "GetItems", "GetItemTypes" }
        .Select(name => type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null,
            name == "GetItemTypes" ? new[] { typeof(InternalItemsQuery), typeof(CancellationToken) } : new[] { typeof(InternalItemsQuery), typeof(IDataContext), typeof(CancellationToken) }, null)).ToArray();

    internal static bool VerifyMethod(MethodInfo method)
    {
        var assembly = method.DeclaringType?.Assembly.GetName();
        if (method.DeclaringType?.FullName != RepositoryType || assembly?.Name != "Emby.Server.Implementations" || assembly.Version != new Version(4, 10, 2, 0) ||
            method.Module.ModuleVersionId != ExpectedMvid || !method.IsPublic || method.IsStatic ||
            !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(method.Name == "GetItemTypes" ? new[] { typeof(InternalItemsQuery), typeof(CancellationToken) } : new[] { typeof(InternalItemsQuery), typeof(IDataContext), typeof(CancellationToken) })) return false;
        var body = method.GetMethodBody();
        if (body is null || !body.InitLocals || body.MaxStackSize != (method.Name == "GetItemTypes" ? 8 : 4) || body.ExceptionHandlingClauses.Count != 2) return false;
        var il = body.GetILAsByteArray()!;
        var hash = Convert.ToHexString(SHA256.HashData(il));
        return method.Name switch
        {
            "GetItemTypes" => method.ReturnType == typeof(string[]) && method.MetadataToken == 0x0600105C && il.Length == 166 &&
                body.LocalSignatureMetadataToken == 0x1100045F && hash == "EA7F946FFE2AF34AEC865902F7E95E48BAC2411E8C11A1CB99614B0392E113D8",
            "GetItemList" => method.ReturnType == typeof(BaseItem[]) && method.MetadataToken == 0x06001049 && il.Length == 95 &&
                body.LocalSignatureMetadataToken == 0x11000451 && hash == "4760EC88308D5DCEDE3DAB910A32916AA29E36D05AEED359DD5007AA9B6473C4",
            "GetItems" => method.ReturnType == typeof(QueryResult<BaseItem>) && method.MetadataToken == 0x0600104D && il.Length == 88 &&
                body.LocalSignatureMetadataToken == 0x11000454 && hash == "C93A9CF05CB10D456BD3D3C9B98C67B137CD0C78128DD8416F1413B600CDA3CB",
            _ => false
        };
    }

    private static bool TryMixed(object repository, InternalItemsQuery query, object[] args, out BaseItem[] rows)
    {
        rows = Array.Empty<BaseItem>();
        var registration = Volatile.Read(ref active);
        if (registration == null || !ReferenceEquals(registration.Repository, repository) || Depth.Value != 0 || query == null || !CommonContext(query, registration.Index.IsNumericSearch(query.SearchTerm)) ||
            query.IncludeItemTypes.Length == 1) return false;
        var token = (CancellationToken)args[^1];
        var candidates = registration.Index.Candidates(query.SearchTerm, query.MatchAnyWord, token);
        if (candidates == null) return false;
        var previous = Depth.Value; Depth.Value++;
        try
        {
            var typeGroups = query.GroupBy.Length == 1 && query.GroupBy[0] == "Type";
            var selection = query;
            if (typeGroups)
            {
                // Actual /ItemTypes runs TagService -> folder.GetItems with a Type group, not repository.GetItemTypes.
                selection = CloneQuery(query); selection.GroupBy = Array.Empty<string>(); selection.GroupByPresentationUniqueKey = false;
            }
            if (!MixedSearchQuery.TryExecute(registration.Repository, registration.Index.Library, selection, candidates,
                registration.Index.CandidateType, args.Length == 3 ? (IDataContext?)args[1] : null, token, out rows, registration.Index.IsNumericSearch(query.SearchTerm))) return false;
            if (typeGroups) rows = rows.GroupBy(item => item.GetType().Name, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            return true;
        }
        finally { Depth.Value = previous; }
    }
    private static bool BeforeItems(object __instance, ref InternalItemsQuery __0, object[] __args, ref QueryResult<BaseItem> __result, out Invocation? __state)
    {
        __state = null;
        if (SuppressHistory(__instance, __0))
        { ((CancellationToken)__args[^1]).ThrowIfCancellationRequested(); __result = new QueryResult<BaseItem> { Items = Array.Empty<BaseItem>(), TotalRecordCount = 0 }; return false; }
        if (TryMixed(__instance, __0, __args, out var rows))
        { __result = new QueryResult<BaseItem> { Items = MixedSearchQuery.Page(__0, rows), TotalRecordCount = MixedSearchQuery.TotalCount(__0, rows) }; return false; }
        Before(__instance, ref __0, __args, out __state); return true;
    }
    private static bool BeforeList(object __instance, ref InternalItemsQuery __0, object[] __args, ref BaseItem[] __result, out Invocation? __state)
    {
        __state = null;
        if (SuppressHistory(__instance, __0))
        { ((CancellationToken)__args[^1]).ThrowIfCancellationRequested(); __result = Array.Empty<BaseItem>(); return false; }
        if (TryMixed(__instance, __0, __args, out var rows)) { __result = MixedSearchQuery.Page(__0, rows); return false; }
        Before(__instance, ref __0, __args, out __state); return true;
    }
    private static bool BeforeTypes(object __instance, ref InternalItemsQuery __0, object[] __args, ref string[] __result, out Invocation? __state)
    {
        __state = null;
        var typesQuery = CloneQuery(__0); typesQuery.GroupByPresentationUniqueKey = false;
        if (TryMixed(__instance, typesQuery, __args, out var rows)) { __result = MixedSearchQuery.ItemTypes(rows); return false; }
        Before(__instance, ref __0, __args, out __state); return true;
    }

    private static bool SuppressHistory(object repository, InternalItemsQuery query)
    {
        var registration = Volatile.Read(ref active);
        return Depth.Value == 0 && registration != null && ReferenceEquals(registration.Repository, repository) &&
            query != null && query.WasSearched == true && string.IsNullOrWhiteSpace(query.SearchTerm) && registration.Index.SuppressSearchSuggestions;
    }

    private static void Before(object __instance, ref InternalItemsQuery __0, object[] __args, out Invocation? __state)
    {
        __state = null;
        var registration = Volatile.Read(ref active);
        if (registration is null || !ReferenceEquals(__instance, registration.Repository) || __0 == null) return;
        if (Depth.Value == 0 && CommonContext(__0, registration.Index.IsNumericSearch(__0.SearchTerm)) && __0.IncludeItemTypes.Length == 1 &&
            (SearchNamedVisibility.IsNamedType(__0.IncludeItemTypes[0]) || __0.IncludeItemTypes[0].Equals("Person", StringComparison.OrdinalIgnoreCase)))
        {
            var previous = Depth.Value; var applied = false; Depth.Value++;
            var token = (CancellationToken)__args[^1];
            try
            {
                var candidates = registration.Index.Candidates(__0.SearchTerm, __0.MatchAnyWord, token);
                if (candidates == null) return;
                var context = __args.Length == 3 ? (IDataContext?)__args[1] : null;
                var visible = SearchNamedVisibility.IsNamedType(__0.IncludeItemTypes[0])
                    ? SearchNamedVisibility.Filter(registration.Index.Library, registration.Repository, __0, candidates, token, context)
                    : SearchPersonVisibility.Filter(registration.Index.Library, registration.Repository, __0, candidates, token, context);
                if (visible == null) return;
                var clone = PrepareQuery(__0, visible);
                __state = new Invocation(__0, previous); __0 = clone; __args[0] = clone; applied = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { Plugin.Instance?.Logger.Warn("[StrmAssistantRebuild] Metadata visibility search retained native handling: {0}", error.GetType().Name); }
            finally { if (!applied) Depth.Value = previous; }
            return;
        }
        Enter(registration.Index, ref __0, out __state, (CancellationToken)__args[^1]);
        // Harmony copies __args back after a prefix; keep its slot in sync with the ref replacement.
        __args[0] = __0;
    }
    private static void Enter(ChineseSearchIndex index, ref InternalItemsQuery query, out Invocation? state, CancellationToken token = default)
    {
        state = null;
        if (Depth.Value != 0 || query is null || !CommonContext(query, index.IsNumericSearch(query.SearchTerm))) return;
        var numeric = index.IsNumericSearch(query.SearchTerm);
        var candidates = index.Candidates(query.SearchTerm, query.MatchAnyWord, token);
        if (candidates is null) return;
        var mixed = query.IncludeItemTypes.Length == 0;
        if (mixed ? query.User == null || !numeric && candidates.Length == 0 || !index.CandidatesHaveOnlyTypes(candidates, MixedMediaTypes) : !(query.IncludeItemTypes.All(SupportedTypes.Contains) && CommonContext(query, numeric))) return;
        var clone = PrepareQuery(query, candidates);
        if (mixed)
        {
            // Conditional route: never turn a candidate Person/LiveTV/named item into an ancestor-exempt global hit.
            // Unsupported candidate families keep the complete original query native instead of disappearing.
            clone.IncludeItemTypes = MixedMediaTypes.ToArray();
            clone.ExcludeItemTypes = clone.ExcludeItemTypes.Concat(DefaultSearchExclusions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        state = new Invocation(query, Depth.Value);
        Depth.Value++;
        query = clone;
    }
    private static void After(ref InternalItemsQuery __0, Invocation? __state)
    {
        if (__state is null) return;
        __0 = __state.Original;
        Depth.Value = __state.PreviousDepth; // Harmony finalizer runs for success, cancellation and exceptions.
    }

    // Explicit Tag/Genre/Studio use an ordinary-media witness; default/mixed named, LiveTV and projection
    // retain native handling. Their complete SearchTerm-specific SQL context is not established.
    private static bool CommonContext(InternalItemsQuery query, bool numericSearch = false)
    {
        if (string.IsNullOrWhiteSpace(query.SearchTerm) || !string.IsNullOrEmpty(query.Name) || query.ProjectToMedia || query.GroupItemsIntoItemLinkType.HasValue ||
            query.RecursiveTaggedItems && (query.IsQueryingByGenres || query.IsQueryingByTags || query.IsQueryingByStudios || query.CollectionIds.Length != 0)) return false;
        var terms = query.SearchTerm.Trim().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (!numericSearch && terms.Length == 1 && terms[0].Length == 1)
        {
            var ch = terms[0][0];
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || char.IsPunctuation(ch) || char.IsSeparator(ch)) return false;
        }
        return true;
    }
    internal static bool CanEnhance(InternalItemsQuery query) => CommonContext(query) && query.IncludeItemTypes.Length > 0 && query.IncludeItemTypes.All(SupportedTypes.Contains);

    internal static InternalItemsQuery PrepareQuery(InternalItemsQuery original, long[] candidates)
    {
        var clone = CloneQuery(original);
        var matched = candidates.Where(id => id > 0).ToHashSet();
        if (original.ItemIds.Length != 0) matched.IntersectWith(original.ItemIds);
        if (original.OuterItemIds.Length != 0) matched.IntersectWith(original.OuterItemIds);
        // ItemIds is NOT overwritten: its presence switches off native extra-type filtering and changes projection.
        // OuterItemIds is the native additional AND Id IN clause, leaving those decisions unchanged.
        var order = original.OuterItemIds.Length > 0 ? original.OuterItemIds : original.ItemIds.Length > 0 ? original.ItemIds : candidates;
        clone.OuterItemIds = matched.Count == 0 ? new long[] { -1 } : order.Where(matched.Contains).Distinct().ToArray();
        clone.SearchTerm = null;
        clone.UseExistsForSubQueries = true;
        if (original.SearchTerm.Trim().Length < 4) clone.EnableTotalRecordCount = false;
        if (clone.OrderBy.Length == 0) clone.OrderBy = new[] { ("SortName", SortOrder.Ascending), ("Id", SortOrder.Ascending) };
        return clone;
    }
    internal static InternalItemsQuery CloneQuery(InternalItemsQuery source)
    {
        var clone = new InternalItemsQuery();
        foreach (var property in QueryProperties)
        {
            var value = property.GetValue(source);
            // Parent/Ancestors setters run first; normalized ParentIds/AncestorIds are copied afterwards.
            property.SetValue(clone, value is Array array ? array.Clone() : value);
        }
        clone.AnyProviderIdEquals = source.AnyProviderIdEquals.ToList();
        clone.AnySeriesProviderIdEquals = source.AnySeriesProviderIdEquals.ToList();
        clone.ExcludeProviderIds = new ProviderIdDictionary(source.ExcludeProviderIds);
        clone.WithImportedCollectionMatchingProviderId = new ProviderIdDictionary(source.WithImportedCollectionMatchingProviderId);
        var dto = new MediaBrowser.Controller.Dto.DtoOptions();
        foreach (var property in typeof(MediaBrowser.Controller.Dto.DtoOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(p => p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0))
        {
            var value = property.GetValue(source.DtoOptions);
            property.SetValue(dto, value is Array array ? array.Clone() : value);
        }
        clone.DtoOptions = dto;
        return clone;
    }
    private sealed record Registration(IItemRepository Repository, ChineseSearchIndex Index);
    private sealed record Invocation(InternalItemsQuery Original, int PreviousDepth);
}
