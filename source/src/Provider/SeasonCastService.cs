using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace StrmAssistantRebuild.Provider;

/// <summary>Read-only series aggregation. The caller owns a fresh series lease and any UpdatePeople call.</summary>
public sealed class SeasonCastService
{
    private readonly ILibraryManager library;
    public SeasonCastService(ILibraryManager library) => this.library = library;

    public List<PersonInfo>? GetSeriesPeople(Series series, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (series == null || series.InternalId <= 0 || series.IsLocked ||
            series.IsFieldLocked(MetadataFields.Cast) || !series.HasProviderId(MetadataProviders.Tmdb)) return null;
        // Original shipped AA uses Insert(0, seriesPeople), unlike the older OSS Add(seriesPeople).
        var lists = new List<List<PersonInfo>> { library.GetItemPeople(series) };
        var total = lists[0].Count;
        if (total > 20000) return null;
        var seasons = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { nameof(Season) },
            ParentWithPresentationUniqueKeyFromItemId = series.InternalId,
            MinIndexNumber = 1,
            Limit = 501, // ponytail: bounded milestone; oversize series stays unchanged, never partially aggregated.
            OrderBy = new[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
        }, token);
        if (seasons.Length > 500) return null;
        foreach (var season in seasons)
        {
            token.ThrowIfCancellationRequested();
            var people = library.GetItemPeople(season);
            total += people.Count;
            if (total > 20000) return null;
            lists.Add(people);
        }
        return Interleave(lists, token);
    }

    internal static List<PersonInfo> Interleave(IReadOnlyList<List<PersonInfo>> lists, CancellationToken token)
    {
        var result = new List<PersonInfo>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var count = lists.Count == 0 ? 0 : lists.Max(list => list.Count);
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            foreach (var list in lists)
                if (i < list.Count && list[i] is { } person && names.Add(person.Name)) result.Add(person);
        }
        return result;
    }
}
