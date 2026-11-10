using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Data;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace StrmAssistantRebuild.Common;

/// <summary>Complete native-filtered branches, then one stable Id order. Not native FTS Rank.</summary>
public static class MixedSearchQuery
{
    private const int CandidateBudget = 20_000;
    private static readonly HashSet<string> MediaTypes = new(new[]
    {
        "Movie", "Series", "Season", "Episode", "Video", "Trailer", "Audio", "MusicVideo", "Book", "Game", "Photo", "PhotoAlbum"
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly string[] DefaultExclusions = { "Folder", "Channel", "Season", "CollectionFolder", "UserView", "LiveTVSeries" };
    private static readonly string[] LiveExclusions = { "LiveTvProgram", "LiveTvChannel" };

    /// <summary>
    /// Caller must enter its reentry guard first. False means whole-query native fallback; cancellation propagates.
    /// Type hints partition candidates, never authorize them. All output rows have passed their complete native branch.
    /// </summary>
    public static bool TryExecute(IItemRepository repository, ILibraryManager library, InternalItemsQuery query,
        long[] candidates, Func<long, string?> candidateType, IDataContext? context, CancellationToken token,
        out BaseItem[] allFiltered, bool numericSearch = false)
    {
        allFiltered = Array.Empty<BaseItem>();
        token.ThrowIfCancellationRequested();
        if (repository is null || library is null || query is null || candidates is null || candidateType is null ||
            candidates.Length > CandidateBudget || !Supports(query, numericSearch)) return false;
        try
        {
            if (query.IncludeItemTypes.Any(SearchNamedVisibility.IsNamedType))
                return TryExecuteExplicitNamed(repository, library, query, candidates, candidateType, context, token, out allFiltered);
            var isDefault = query.IncludeItemTypes.Length == 0;
            // R5363-5375: this normalized ordinary scope excludes TV even when the manager is enabled.
            // Do not infer effective LiveTV exclusion merely from this search's candidate types.
            if (isDefault && !(query.ItemLinkLevel == 0 && (query.ParentIds.Length != 0 ||
                query.AncestorIds.Length != 0 && !query.AncestorIds.Contains(repository.LiveTvViewId))) &&
                !LiveExclusions.All(type => query.ExcludeItemTypes.Contains(type, StringComparer.OrdinalIgnoreCase))) return false;

            var mediaIds = new List<long>();
            var personIds = new List<long>();
            var types = new Dictionary<long, string>();
            foreach (var id in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (id <= 0 || types.ContainsKey(id)) continue;
                var type = candidateType(id);
                // Named direct search is separate: default named joins depend on SearchTerm and two-level links.
                // A Tag/Genre/Studio candidate still keeps this WHOLE mixed query native, not a partial media result.
                if (type is null || !MediaTypes.Contains(type) && !IsPerson(type)) return false;
                types.Add(id, type);
                if (IsPerson(type)) personIds.Add(id); else mediaIds.Add(id);
            }
            // Preserve M9's separate ordinary-media route. This API owns queries that actually have Person candidates.
            if (personIds.Count == 0) return false;

            var presentationGrouping = UsesPresentationGrouping(query);
            var mediaGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var personScope = ChineseSearchCompatibility.CloneQuery(query);
            personScope.IncludeItemTypes = new[] { "Person" };
            // R6530: inert after TV exclusion; P0's helper rejects this flag without consulting effective types.
            personScope.GroupProgramsBySeries = false;
            personScope.GroupByPresentationUniqueKey = false; // Witness enumerates media, not final Person groups.
            var visiblePeople = SearchPersonVisibility.Filter(library, repository, personScope, personIds.ToArray(), token, context);
            token.ThrowIfCancellationRequested();
            if (visiblePeople is null) return false;

            var rows = new Dictionary<long, BaseItem>();
            // ponytail: bounded candidates, two complete native reads; no per-branch page and no authorization cache.
            foreach (var personBranch in new[] { false, true })
            {
                var branchIds = personBranch ? visiblePeople : mediaIds.ToArray();
                var branch = ChineseSearchCompatibility.PrepareQuery(query, branchIds);
                if (isDefault)
                    branch.ExcludeItemTypes = branch.ExcludeItemTypes.Concat(DefaultExclusions).Concat(LiveExclusions)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (personBranch) branch.IncludeItemTypes = new[] { "Person" };
                // Keep the original ItemIds, normalized ancestors and every entity/user filter. Only OuterItemIds intersects.
                branch.StartIndex = 0; branch.Limit = null; branch.EnableTotalRecordCount = false;
                branch.GroupByPresentationUniqueKey = presentationGrouping; // Freeze original mixed grouping, including on Person.
                branch.EnableAutoSort = false; branch.OrderBy = new[] { ("Id", SortOrder.Ascending) };
                branch.DtoOptions.Fields = branch.DtoOptions.Fields.Append(ItemFields.Id).Append(ItemFields.AlternateMediaSources).Distinct().ToArray(); // R3024: field 69 includes PUK, even for Person.
                if (branch.OuterItemIds.Length == 1 && branch.OuterItemIds[0] == -1) continue;
                var allowed = branch.OuterItemIds.ToHashSet();
                var filtered = repository.GetItemList(branch, context!, token);
                token.ThrowIfCancellationRequested();
                if (filtered is null || filtered.Length > CandidateBudget) return false;
                foreach (var item in filtered)
                {
                    token.ThrowIfCancellationRequested();
                    if (item is null || item.InternalId <= 0 || !allowed.Contains(item.InternalId) ||
                        !types.TryGetValue(item.InternalId, out var expected) ||
                        !string.Equals(item.GetType().Name, expected, StringComparison.OrdinalIgnoreCase) ||
                        IsPerson(item.GetType().Name) != personBranch) return false;
                    if (presentationGrouping)
                    {
                        // R6570 is plain PUK, not COALESCE(PUK, Id). Null/empty collide conservatively, too.
                        var key = item.PresentationUniqueKey ?? string.Empty;
                        if (personBranch && mediaGroupKeys.Contains(key)) return false;
                        if (!personBranch) mediaGroupKeys.Add(key);
                    }
                    rows.TryAdd(item.InternalId, item); // Native ID grouping / duplicate join rows converge by ID, never by name.
                }
            }
            token.ThrowIfCancellationRequested();
            allFiltered = rows.Values.OrderBy(item => item.InternalId).ToArray();
            token.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException) { allFiltered = Array.Empty<BaseItem>(); throw; }
        catch { allFiltered = Array.Empty<BaseItem>(); return false; } // No first-branch/partial success escapes.
    }

    // Each final branch uses the original effective PUK mode; ambiguous cross-family groups keep the whole query native.
    private static bool TryExecuteExplicitNamed(IItemRepository repository, ILibraryManager library, InternalItemsQuery query,
        long[] candidates, Func<long, string?> candidateType, IDataContext? context, CancellationToken token, out BaseItem[] allFiltered)
    {
        allFiltered = Array.Empty<BaseItem>();
        var requested = query.IncludeItemTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var presentationGrouping = UsesPresentationGrouping(query);
        var types = new Dictionary<long, string>();
        foreach (var id in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (id <= 0 || types.ContainsKey(id)) continue;
            var type = candidateType(id);
            if (type is null || !MediaTypes.Contains(type) && !IsPerson(type) && !SearchNamedVisibility.IsNamedType(type)) return false;
            types.Add(id, type); // Global candidate index: validate all hints, but never admit an unrequested known family.
        }
        var branches = new List<InternalItemsQuery>();
        foreach (var type in requested)
        {
            token.ThrowIfCancellationRequested();
            var ids = types.Where(pair => string.Equals(pair.Value, type, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray();
            if (IsPerson(type) || SearchNamedVisibility.IsNamedType(type))
            {
                var scope = ChineseSearchCompatibility.CloneQuery(query);
                scope.IncludeItemTypes = new[] { type };
                scope.GroupProgramsBySeries = false;
                scope.GroupByPresentationUniqueKey = false;
                // Call even for no candidates: an empty unsupported/scope-failed branch must still keep the whole query native.
                var visible = IsPerson(type) ? SearchPersonVisibility.Filter(library, repository, scope, ids, token, context) :
                    SearchNamedVisibility.Filter(library, repository, scope, ids, token, context);
                token.ThrowIfCancellationRequested();
                if (visible is null) return false;
                ids = visible;
            }
            var branch = ChineseSearchCompatibility.PrepareQuery(query, ids);
            branch.IncludeItemTypes = new[] { type };
            branch.StartIndex = 0; branch.Limit = null; branch.EnableTotalRecordCount = false;
            branch.EnableAutoSort = false; branch.OrderBy = new[] { ("Id", SortOrder.Ascending) };
            branch.GroupByPresentationUniqueKey = presentationGrouping;
            branch.DtoOptions.Fields = branch.DtoOptions.Fields.Append(ItemFields.Id).Append(ItemFields.AlternateMediaSources).Distinct().ToArray();
            branches.Add(branch);
        }
        var rows = new Dictionary<long, BaseItem>();
        var groupOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var branch in branches)
        {
            token.ThrowIfCancellationRequested();
            if (branch.OuterItemIds.Length == 1 && branch.OuterItemIds[0] == -1) continue;
            var allowed = branch.OuterItemIds.ToHashSet();
            var filtered = repository.GetItemList(branch, context!, token);
            token.ThrowIfCancellationRequested();
            if (filtered is null || filtered.Length > CandidateBudget) return false;
            foreach (var item in filtered)
            {
                token.ThrowIfCancellationRequested();
                if (item is null || item.InternalId <= 0 || !allowed.Contains(item.InternalId) ||
                    !types.TryGetValue(item.InternalId, out var expected) ||
                    !string.Equals(item.GetType().Name, expected, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(item.GetType().Name, branch.IncludeItemTypes[0], StringComparison.OrdinalIgnoreCase)) return false;
                if (presentationGrouping)
                {
                    // Native GROUP BY uses PUK, not COALESCE(PUK,Id); null/empty and case variants may collide.
                    var key = item.PresentationUniqueKey ?? string.Empty;
                    if (groupOwners.TryGetValue(key, out var owner) && !string.Equals(owner, branch.IncludeItemTypes[0], StringComparison.OrdinalIgnoreCase)) return false;
                    groupOwners[key] = branch.IncludeItemTypes[0];
                }
                rows.TryAdd(item.InternalId, item);
            }
        }
        token.ThrowIfCancellationRequested();
        allFiltered = rows.Values.OrderBy(item => item.InternalId).ToArray();
        token.ThrowIfCancellationRequested();
        return true;
    }

    public static BaseItem[] Page(InternalItemsQuery query, BaseItem[] allFiltered) =>
        allFiltered.Skip(query.StartIndex.GetValueOrDefault()).Take(query.Limit ?? int.MaxValue).ToArray();

    /// <summary>R7765-7767, R7937-7939, R7966-7975, R8009: preserve short-term and missing-window totals.</summary>
    public static int TotalCount(InternalItemsQuery query, BaseItem[] allFiltered)
    {
        if (!query.Limit.HasValue && query.StartIndex.GetValueOrDefault() == 0) return allFiltered.Length;
        return query.EnableTotalRecordCount && query.SearchTerm.Trim().Length >= 4 &&
               query.StartIndex.GetValueOrDefault() < allFiltered.Length ? allFiltered.Length : 0;
    }

    /// <summary>Menu from all fully filtered rows, not just TopResults' page. Stable enhanced type-name order.</summary>
    public static string[] ItemTypes(BaseItem[] allFiltered) => allFiltered.Select(item => item.GetType().Name)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(type => type, StringComparer.Ordinal).ToArray();

    private static bool IsPerson(string type) => string.Equals(type, "Person", StringComparison.OrdinalIgnoreCase);

    // R11023-11067. P0 rejects alternate type-shaping contexts (media, folder, person/tag/genre/studio IDs).
    private static bool UsesPresentationGrouping(InternalItemsQuery q)
    {
        if (q.GroupByPresentationUniqueKey.HasValue) return q.GroupByPresentationUniqueKey.Value;
        if (!string.IsNullOrEmpty(q.PresentationUniqueKey)) return false;
        var types = new[] { "Episode", "Video", "Movie", "MusicVideo", "Series", "Season" };
        return types.Any(type => (q.IncludeItemTypes.Length == 0 || q.IncludeItemTypes.Contains(type, StringComparer.OrdinalIgnoreCase)) &&
            !q.ExcludeItemTypes.Contains(type, StringComparer.OrdinalIgnoreCase) &&
            !(q.IncludeItemTypes.Length == 0 && DefaultExclusions.Contains(type, StringComparer.OrdinalIgnoreCase)));
    }

    private static bool Supports(InternalItemsQuery q, bool numericSearch)
    {
        if (q.User is null || q.User.InternalId <= 0 || !q.Recursive || string.IsNullOrWhiteSpace(q.SearchTerm) ||
            !string.IsNullOrEmpty(q.Name) || q.StartIndex < 0 || q.Limit < 0 || q.StartItem != 0 || q.AdjacentTo != 0 ||
            q.ProjectToMedia || q.GroupItemsIntoItemLinkType.HasValue || q.EnableGroupByMetadataKey || q.IsNextUpQuery ||
            q.GroupBy.Length != 0 || q.GroupBySeriesId || q.GroupBySeriesPresentationUniqueKey || q.GroupByAlbumId ||
            q.GroupByParentPresentationUniqueKey || q.GroupByPrimaryImagePath || q.IsDuplicate.HasValue ||
            q.FixedType is not null || q.ItemLinkLevel != 0 || q.PersonTypes.Length != 0 || q.MaxListOrder.HasValue ||
            q.RecursiveTaggedItems && (q.IsQueryingByTags || q.IsQueryingByGenres || q.IsQueryingByStudios || q.CollectionIds.Length != 0)) return false;
        // Complex entity / role contexts are additionally rejected by the existing P0 witness helper, before native branches.
        if (q.IncludeItemTypes.Any(SearchNamedVisibility.IsNamedType))
        {
            // A single-type branch fully removed by Exclude can normalize to [] and lose native Type WHERE.
            // Keep intersecting exclusions on the unsplit query, including conservative Person exemptions.
            if (q.IncludeItemTypes.Any(type => q.ExcludeItemTypes.Contains(type, StringComparer.OrdinalIgnoreCase))) return false;
            if (q.IncludeItemTypes.Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2 ||
                q.IncludeItemTypes.Any(type => !IsPerson(type) && !MediaTypes.Contains(type) && !SearchNamedVisibility.IsNamedType(type))) return false;
        }
        else if (q.IncludeItemTypes.Length != 0 && (q.IncludeItemTypes.Length < 2 || !q.IncludeItemTypes.Any(IsPerson) ||
            q.IncludeItemTypes.Any(type => !IsPerson(type) && !MediaTypes.Contains(type)))) return false;
        var terms = q.SearchTerm.Trim().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return false;
        if (!numericSearch && terms.Length == 1 && terms[0].Length == 1)
        {
            var ch = terms[0][0];
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || char.IsPunctuation(ch) || char.IsSeparator(ch)) return false;
        }
        return true;
    }
}
