using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Data;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace StrmAssistantRebuild.Common;

/// <summary>Direct Tag/Genre/Studio candidates witnessed by native-authorized ordinary media, not global metadata.</summary>
public static class SearchNamedVisibility
{
    private const int PageSize = 500;
    private const int MediaBudget = 20_000;
    private static readonly string[] OrdinaryTypes =
    {
        "Movie", "Series", "Season", "Episode", "Video", "Trailer", "Audio", "MusicVideo", "Book", "Game", "Photo", "PhotoAlbum"
    };

    public static bool IsNamedType(string type) => string.Equals(type, "Tag", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "Genre", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "Studio", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null means whole-query native fallback; empty is a complete proof with no visible direct association.</summary>
    public static long[]? Filter(ILibraryManager library, IItemRepository repository, InternalItemsQuery original,
        long[] candidates, CancellationToken token, IDataContext? context = null)
    {
        token.ThrowIfCancellationRequested();
        if (library is null || repository is null || original is null || candidates is null ||
            candidates.Length > MediaBudget || !Supports(original)) return null;
        var wanted = candidates.Where(id => id > 0).ToHashSet();
        if (original.ItemIds.Length != 0) wanted.IntersectWith(original.ItemIds);
        if (original.OuterItemIds.Length != 0) wanted.IntersectWith(original.OuterItemIds);
        if (wanted.Count == 0) return Array.Empty<long>();
        try
        {
            // R2069-2122/R12068-12124: native relation type and tuple.Item3 are authoritative, never the name.
            var type = original.IncludeItemTypes[0];
            var linkType = type.Equals("Genre", StringComparison.OrdinalIgnoreCase) ? ItemLinkType.Genres :
                type.Equals("Studio", StringComparison.OrdinalIgnoreCase) ? ItemLinkType.Studios : ItemLinkType.Tags;
            // Only authorization/scope crosses namespaces. Named ratings/favorites/IDs/history stay on the final query.
            // SetUser applies actual user policy; reapplying named filters to media would change their meaning.
            var media = new InternalItemsQuery(original.User)
            {
                Recursive = true, SkipAncestorNormalization = true,
                ParentIds = (long[])original.ParentIds.Clone(), AncestorIds = (long[])original.AncestorIds.Clone(),
                ExcludeParentIds = (long[])original.ExcludeParentIds.Clone(),
                ExcludeUserViewIds = (long[])original.ExcludeUserViewIds.Clone(),
                ExcludeUserViewIdStrings = (string[])original.ExcludeUserViewIdStrings.Clone(),
                IncludeItemTypes = (string[])OrdinaryTypes.Clone(), IncludeLiveTVView = false,
                CanEditItems = original.CanEditItems, AllowOwnedItems = original.AllowOwnedItems,
                EnableAutoSort = false, OrderBy = new[] { ("Id", SortOrder.Ascending) },
                GroupByPresentationUniqueKey = false, EnableGroupByMetadataKey = false, EnableTotalRecordCount = false,
                DtoOptions = new DtoOptions { Fields = new[] { ItemFields.Id }, EnableUserData = false }
            };
            var seen = new HashSet<long>();
            var visible = new List<long>();
            for (var start = 0; ; start += PageSize)
            {
                token.ThrowIfCancellationRequested();
                media.StartIndex = start; media.Limit = PageSize;
                var page = context is null ? library.GetItemList(media, token) : library.GetItemList(media, context, token);
                token.ThrowIfCancellationRequested();
                if (page is null || page.Length > PageSize || start + page.Length > MediaBudget) return null;
                if (page.Length == 0) break;
                foreach (var item in page)
                    if (item is null || item.InternalId <= 0 || !seen.Add(item.InternalId) ||
                        !OrdinaryTypes.Contains(item.GetType().Name, StringComparer.OrdinalIgnoreCase)) return null;
                var authorized = library.FilterItemsToIdsForUser(page, original.User, context, token);
                token.ThrowIfCancellationRequested();
                var pageIds = page.Select(item => item.InternalId).ToHashSet();
                if (authorized is null || authorized.Any(id => id <= 0 || !pageIds.Contains(id))) return null;
                var allowed = authorized.ToHashSet();
                foreach (var item in page)
                    if (allowed.Contains(item.InternalId) && (linkType == ItemLinkType.Genres ? item.SupportsGenres :
                        linkType == ItemLinkType.Studios ? item.SupportsStudios : item.SupportsTags)) visible.Add(item.InternalId);
                if (page.Length < PageSize) break;
            }
            var found = new HashSet<long>();
            // ponytail: public API is one media ID per read, capped at 20k; no cache or partial/budget-truncated success.
            foreach (var id in visible)
            {
                token.ThrowIfCancellationRequested();
                var links = repository.GetItemLinks(id, new List<ItemLinkType> { linkType }, context!, token);
                token.ThrowIfCancellationRequested();
                if (links is null) return null;
                foreach (var link in links)
                {
                    token.ThrowIfCancellationRequested();
                    if (link.Item1 != linkType || link.Item3 <= 0) return null;
                    if (wanted.Contains(link.Item3)) found.Add(link.Item3);
                }
            }
            token.ThrowIfCancellationRequested();
            return candidates.Where(found.Contains).Distinct().ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static bool Supports(InternalItemsQuery q) =>
        q.User is not null && q.User.InternalId > 0 && q.Recursive && !string.IsNullOrWhiteSpace(q.SearchTerm) &&
        q.IncludeItemTypes.Length == 1 && IsNamedType(q.IncludeItemTypes[0]) &&
        (q.ParentIds.Length != 0 || q.AncestorIds.Length != 0) && q.TopParentIds.Length == 0 &&
        q.ItemLinkLevel == 0 && q.AllowAutoGlobalItemLinkTypesFromIncludeItemTypes && q.AllowedGlobalItemLinkTypes.Length == 0 &&
        q.EnableInnerItemLinkJoin && !q.IsQueryingForTopLevelFolders && !q.ForceOriginalFolders && !q.ProjectToMedia &&
        !q.GroupItemsIntoItemLinkType.HasValue && !q.IsFolder.HasValue && q.MediaTypes.Length == 0 && q.FixedType is null &&
        string.IsNullOrEmpty(q.Name) && q.SimilarTo is null && !q.IsNextUpQuery && q.StartItem == 0 && q.AdjacentTo == 0 &&
        q.StartIndex.GetValueOrDefault() >= 0 && q.Limit.GetValueOrDefault() >= 0 &&
        !q.EnableGroupByMetadataKey && (q.GroupBy.Length == 0 || q.GroupBy.Length == 1 && q.GroupBy[0] == "Type") &&
        !q.GroupBySeriesId && !q.GroupBySeriesPresentationUniqueKey && !q.GroupByAlbumId &&
        !q.GroupByParentPresentationUniqueKey && !q.GroupByPrimaryImagePath && !q.GroupProgramsBySeries &&
        !q.IsDuplicate.HasValue && !q.IsDeadPerson.HasValue && !q.IsDeadItemLink.HasValue &&
        q.InnerIncludeItemTypes.Length == 0 && q.OriginalInnerIncludeItemTypes.Length == 0 && q.CollectionTypes.Length == 0 &&
        !q.IsQueryingByTags && !q.IsQueryingByGenres && !q.IsQueryingByStudios && q.CollectionIds.Length == 0 &&
        q.ListIds.Length == 0 && q.ListItemIds.Length == 0 && q.PersonIds.Length == 0 && q.AppearsInItemIds.Length == 0 &&
        q.PersonTypes.Length == 0 && !q.MaxListOrder.HasValue &&
        q.ArtistIds.Length == 0 && q.AlbumArtistIds.Length == 0 && q.ContributingArtistIds.Length == 0 &&
        q.ComposerArtistIds.Length == 0 && q.FirstAlbumArtistIds.Length == 0 && q.AllAlbumArtistIds.Length == 0 &&
        q.ExcludeArtistIds.Length == 0 && q.AlbumIds.Length == 0 && q.SeriesIds.Length == 0 && q.HasSameGenresFromItems.Length == 0 &&
        string.IsNullOrEmpty(q.SeriesPresentationUniqueKey) && string.IsNullOrEmpty(q.AlbumWithPresentationUniqueKey) &&
        string.IsNullOrEmpty(q.Path) && q.PathIsAny.Length == 0 && q.PathStartsWithAny.Length == 0 &&
        string.IsNullOrEmpty(q.PathStartsWith) && string.IsNullOrEmpty(q.PathNotStartsWith) &&
        !q.IsAiring.HasValue && !q.HasAired.HasValue && !q.MinStartDate.HasValue && !q.MaxStartDate.HasValue &&
        !q.MinEndDate.HasValue && !q.MaxEndDate.HasValue;
}
