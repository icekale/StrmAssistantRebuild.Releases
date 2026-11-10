using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Data;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace StrmAssistantRebuild.Common;

/// <summary>Person candidates linked to native-authorized ordinary media; never returns a page or a total.</summary>
public static class SearchPersonVisibility
{
    private const int PageSize = 500;
    private const int MediaBudget = 20_000;
    private static readonly string[] OrdinaryTypes =
    {
        "Movie", "Series", "Season", "Episode", "Video", "Trailer", "Audio", "MusicVideo", "Book", "Game", "Photo", "PhotoAlbum"
    };

    /// <summary>Null retains native search; empty means the supported query has no visible associated candidate.</summary>
    public static long[]? Filter(ILibraryManager library, IItemRepository repository, InternalItemsQuery original,
        long[] candidates, CancellationToken token, IDataContext? context = null)
    {
        token.ThrowIfCancellationRequested();
        if (library is null || repository is null || original is null || candidates is null || !Supports(original)) return null;
        var wanted = candidates.Where(id => id > 0).ToHashSet();
        token.ThrowIfCancellationRequested();
        if (wanted.Count == 0) return Array.Empty<long>();
        try
        {
            // Parent/Ancestor IDs already include LibraryManager's search-view exclusions. Never expand them here.
            var media = new InternalItemsQuery(original.User)
            {
                Recursive = true, SkipAncestorNormalization = true,
                ParentIds = (long[])original.ParentIds.Clone(), AncestorIds = (long[])original.AncestorIds.Clone(),
                ExcludeParentIds = (long[])original.ExcludeParentIds.Clone(),
                ExcludeUserViewIds = (long[])original.ExcludeUserViewIds.Clone(),
                ExcludeUserViewIdStrings = (string[])original.ExcludeUserViewIdStrings.Clone(),
                IncludeItemTypes = (string[])OrdinaryTypes.Clone(), IncludeLiveTVView = false,
                MaxParentalRating = original.MaxParentalRating, MinParentalRating = original.MinParentalRating,
                BlockUnratedItems = original.BlockUnratedItems.ToArray(),
                IncludeInheritedTags = (string[])original.IncludeInheritedTags.Clone(),
                ExcludeInheritedTags = (string[])original.ExcludeInheritedTags.Clone(),
                ExcludeTags = (string[])original.ExcludeTags.Clone(), ExcludeTagIds = (long[])original.ExcludeTagIds.Clone(),
                AllowTagOrRating = original.AllowTagOrRating, CanEditItems = original.CanEditItems,
                AllowOwnedItems = original.AllowOwnedItems,
                EnableAutoSort = false, OrderBy = new[] { ("Id", SortOrder.Ascending) },
                GroupByPresentationUniqueKey = false, EnableGroupByMetadataKey = false, EnableTotalRecordCount = false,
                DtoOptions = new DtoOptions { Fields = new[] { ItemFields.Id }, EnableUserData = false }
            };
            var seen = new HashSet<long>();
            var visibleMedia = new List<long>();
            // ponytail: bounded offset scan, no cross-request cache; use a shared host snapshot if concurrent scans require it.
            for (var start = 0; ; start += PageSize)
            {
                token.ThrowIfCancellationRequested();
                media.StartIndex = start;
                media.Limit = PageSize;
                var page = context is null ? library.GetItemList(media, token) : library.GetItemList(media, context, token);
                token.ThrowIfCancellationRequested();
                if (page is null || page.Length > PageSize) return null;
                if (page.Length == 0) break;
                // Read one look-ahead page at exactly the budget; never return a truncated authorization set.
                if (start + page.Length > MediaBudget) return null;
                foreach (var item in page)
                {
                    token.ThrowIfCancellationRequested();
                    if (item is null || item.InternalId <= 0 || !seen.Add(item.InternalId) ||
                        !OrdinaryTypes.Contains(item.GetType().Name, StringComparer.OrdinalIgnoreCase)) return null;
                }
                // Explicit Parent/Ancestor queries alone are not an authorization proof: intersect the native user-root check.
                var authorized = library.FilterItemsToIdsForUser(page, original.User, context, token);
                token.ThrowIfCancellationRequested();
                if (authorized is null) return null;
                var pageIds = page.Select(item => item.InternalId).ToHashSet();
                if (authorized.Any(id => id <= 0 || !pageIds.Contains(id))) return null;
                var authorizedSet = authorized.ToHashSet();
                foreach (var item in page)
                    if (authorizedSet.Contains(item.InternalId) && item.SupportsPeople) visibleMedia.Add(item.InternalId);
                if (page.Length < PageSize) break;
            }
            token.ThrowIfCancellationRequested();
            if (visibleMedia.Count == 0) return Array.Empty<long>();
            var found = new HashSet<long>();
            var personTypes = (PersonType[])original.PersonTypes.Clone();
            foreach (var batch in visibleMedia.Chunk(PageSize))
            {
                token.ThrowIfCancellationRequested();
                var people = repository.GetItemPeople(new InternalPeopleQuery
                {
                    ItemIds = batch, EnableIds = true, EnableGroupByName = false,
                    EnableImages = false, EnableProviderIds = false,
                    PersonTypes = (PersonType[])personTypes.Clone(), MaxListOrder = original.MaxListOrder
                }, token);
                token.ThrowIfCancellationRequested();
                if (people is null) return null;
                var batchIds = batch.ToHashSet();
                foreach (var person in people)
                {
                    token.ThrowIfCancellationRequested();
                    // PersonInfo.ItemId is the media ID; PersonInfo.Id is the person ID.
                    if (person is null || person.Id <= 0 || !batchIds.Contains(person.ItemId)) return null;
                    if (personTypes.Length != 0 && !personTypes.Contains(person.Type)) return null;
                    if (wanted.Contains(person.Id)) found.Add(person.Id);
                }
            }
            token.ThrowIfCancellationRequested();
            return candidates.Where(found.Contains).Distinct().ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; } // A failed/incomplete visibility proof retains native search, never partial candidates.
    }

    private static bool Supports(InternalItemsQuery q) =>
        q.User is not null && q.User.InternalId > 0 && q.Recursive && !string.IsNullOrWhiteSpace(q.SearchTerm) &&
        q.IncludeItemTypes.Length == 1 && string.Equals(q.IncludeItemTypes[0], "Person", StringComparison.OrdinalIgnoreCase) &&
        (q.ParentIds.Length != 0 || q.AncestorIds.Length != 0) && q.TopParentIds.Length == 0 &&
        q.AllowGlobalPeople && !q.IsQueryingForTopLevelFolders && !q.ForceOriginalFolders && !q.ProjectToMedia &&
        !q.GroupItemsIntoItemLinkType.HasValue && !q.IsFolder.HasValue && q.MediaTypes.Length == 0 &&
        string.IsNullOrEmpty(q.Name) && q.SimilarTo is null && !q.IsNextUpQuery && q.StartItem == 0 &&
        !q.EnableGroupByMetadataKey && q.GroupBy.Length == 0 &&
        !q.GroupBySeriesId && !q.GroupBySeriesPresentationUniqueKey && !q.GroupByAlbumId &&
        !q.GroupByParentPresentationUniqueKey && !q.GroupByPrimaryImagePath && !q.GroupProgramsBySeries &&
        !q.IsDuplicate.HasValue && !q.IsDeadPerson.HasValue && !q.IsDeadItemLink.HasValue &&
        q.InnerIncludeItemTypes.Length == 0 && q.OriginalInnerIncludeItemTypes.Length == 0 && q.CollectionTypes.Length == 0 &&
        !q.IsQueryingByTags && !q.IsQueryingByGenres && !q.IsQueryingByStudios && q.CollectionIds.Length == 0 &&
        q.ListIds.Length == 0 && q.ListItemIds.Length == 0 && q.PersonIds.Length == 0 && q.AppearsInItemIds.Length == 0 &&
        q.ArtistIds.Length == 0 && q.AlbumArtistIds.Length == 0 && q.ContributingArtistIds.Length == 0 &&
        q.ComposerArtistIds.Length == 0 && q.FirstAlbumArtistIds.Length == 0 && q.AllAlbumArtistIds.Length == 0 &&
        q.ExcludeArtistIds.Length == 0 && q.AlbumIds.Length == 0 && q.SeriesIds.Length == 0 && q.HasSameGenresFromItems.Length == 0 &&
        string.IsNullOrEmpty(q.SeriesPresentationUniqueKey) && string.IsNullOrEmpty(q.AlbumWithPresentationUniqueKey) &&
        string.IsNullOrEmpty(q.Path) && q.PathIsAny.Length == 0 && q.PathStartsWithAny.Length == 0 &&
        string.IsNullOrEmpty(q.PathStartsWith) && string.IsNullOrEmpty(q.PathNotStartsWith) &&
        !q.IsAiring.HasValue && !q.HasAired.HasValue && !q.MinStartDate.HasValue && !q.MaxStartDate.HasValue &&
        !q.MinEndDate.HasValue && !q.MaxEndDate.HasValue;
}
