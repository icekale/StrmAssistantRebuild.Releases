using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using StrmAssistantRebuild.Properties;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

public enum LibraryScopeKind { Media, Intro, Marker, Metadata }

/// <summary>Server maintenance and viewer selection deliberately have separate entry points.</summary>
public static class ScopeSelection
{
    // OSS-compatible reconstruction value; the encrypted Pro literal remains unknown.
    public const string Favorites = ScopeRules.Favorites;
    public const string None = ScopeRules.None;
    private static IUserManager? users;
    public static bool FavoritesAvailable => Volatile.Read(ref users) != null;
    public static void ConfigureUsers(IUserManager? value) => Volatile.Write(ref users, value);

    public static HashSet<string> Parse(string? scope) => ScopeRules.Parse(scope);
    public static void ValidateMarkerScope(string? scope) => ScopeRules.ValidateMarkerScope(scope);

    public static bool EligibleFolder(VirtualFolderInfo folder, LibraryScopeKind kind)
    {
        var type = folder.CollectionType ?? "";
        var mixed = type.Length == 0;
        var tv = type.Equals("tvshows", StringComparison.OrdinalIgnoreCase);
        if (kind == LibraryScopeKind.Marker) return (mixed || tv) && folder.LibraryOptions?.EnableMarkerDetection == true;
        if (kind is LibraryScopeKind.Intro or LibraryScopeKind.Metadata)
            return mixed || tv || type.Equals("movies", StringComparison.OrdinalIgnoreCase);
        return !new[] { "books", "photos", "games", "livetv", "playlists", "boxsets" }.Contains(type, StringComparer.OrdinalIgnoreCase);
    }

    public static bool EligibleItem(BaseItem item, LibraryScopeKind kind) =>
        !string.IsNullOrWhiteSpace(item.Path) && (kind switch
        {
            LibraryScopeKind.Media => item is Video or Audio,
            LibraryScopeKind.Marker => item is Episode,
            LibraryScopeKind.Intro => item is Movie or Episode,
            _ => item is Movie or Series or Season or Episode
        });

    public static bool MatchesFolder(VirtualFolderInfo folder, ISet<string> selected) =>
        selected.Count == 0 || selected.Contains(folder.ItemId ?? "") || selected.Contains(folder.Id ?? "") ||
        selected.Contains(folder.Guid ?? "");

    public static BaseItem[] SelectForServer(ILibraryManager library, string? scope, LibraryScopeKind kind,
        CancellationToken token, IUserManager? userManager = null)
    {
        return Select(library, scope, kind, token, null, userManager ?? Volatile.Read(ref users));
    }

    /// <summary>Never unions other users' favorites; every query, including expansions, retains this viewer.</summary>
    public static BaseItem[] SelectForViewer(ILibraryManager library, string? scope, LibraryScopeKind kind,
        User viewer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        token.ThrowIfCancellationRequested();
        return viewer.Policy.IsDisabled ? Array.Empty<BaseItem>() : Select(library, scope, kind, token, viewer, null);
    }

    private static BaseItem[] Select(ILibraryManager library, string? scope, LibraryScopeKind kind,
        CancellationToken token, User? viewer, IUserManager? userManager)
    {
        token.ThrowIfCancellationRequested();
        if (kind == LibraryScopeKind.Marker) ValidateMarkerScope(scope);
        var selected = Parse(scope);
        if (selected.Contains(None)) return Array.Empty<BaseItem>();
        var folders = library.GetVirtualFolders().Where(f => EligibleFolder(f, kind)).ToArray();
        var result = new Dictionary<long, BaseItem>();
        foreach (var folder in folders.Where(f => MatchesFolder(f, selected)))
        {
            token.ThrowIfCancellationRequested();
            var id = FolderId(library, folder);
            if (!id.HasValue) continue;
            var query = Query(viewer);
            query.ParentIds = new[] { id.Value };
            foreach (var item in library.GetItemList(query, token))
            {
                token.ThrowIfCancellationRequested();
                if (EligibleItem(item, kind)) result[item.InternalId] = item;
            }
        }
        if (selected.Contains(Favorites) && kind is LibraryScopeKind.Media or LibraryScopeKind.Marker)
        {
            var favoriteUsers = viewer != null ? new[] { viewer } :
                (userManager ?? throw new InvalidOperationException("Favorites user manager is not initialized."))
                .GetUserList(new UserQuery { IsDisabled = false }).Where(u => !u.Policy.IsDisabled).ToArray();
            var eligibleIds = folders.Select(f => FolderId(library, f)).Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
            foreach (var user in favoriteUsers)
            {
                token.ThrowIfCancellationRequested();
                var query = Query(user);
                query.IsFavorite = true;
                query.IncludeItemTypes = new[] { nameof(Movie), nameof(Episode), nameof(Series) };
                var seeds = library.GetItemList(query, token);
                foreach (var item in Visible(library, ExpandFavorites(library, seeds, user, token), user, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (EligibleItem(item, kind) && library.GetCollectionFolders(item, token).Any(f => eligibleIds.Contains(f.InternalId)))
                        result[item.InternalId] = item;
                }
            }
        }
        token.ThrowIfCancellationRequested();
        var items = result.Values.ToArray();
        return viewer == null ? items : Visible(library, items, viewer, token);
    }

    private static BaseItem[] Visible(ILibraryManager library, BaseItem[] items, User user, CancellationToken token)
    {
        // Explicit parents skip native auto-root selection in 4.10.2. Re-intersect with the
        // public user-root filter, not just query.User or a hand-written EnabledFolders check.
        var allowed = library.FilterItemsToIdsForUser(items, user, null, token).ToHashSet();
        return items.Where(i => allowed.Contains(i.InternalId)).ToArray();
    }

    // OSS expands presentation variants and the series containing a favorited episode too.
    // Unlike the OSS expansion queries, ALL these queries retain the originating user's policy.
    private static BaseItem[] ExpandFavorites(ILibraryManager library, BaseItem[] seeds, User user, CancellationToken token)
    {
        var variants = seeds.GroupBy(i => i.InternalId).ToDictionary(g => g.Key, g => g.First());
        foreach (var key in seeds.Select(i => i.PresentationUniqueKey).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var query = Query(user);
            query.PresentationUniqueKey = key;
            query.IncludeItemTypes = new[] { nameof(Movie), nameof(Episode), nameof(Series) };
            foreach (var item in library.GetItemList(query, token)) variants[item.InternalId] = item;
        }
        var series = variants.Values.OfType<Series>().Select(i => i.InternalId)
            .Concat(variants.Values.OfType<Episode>().Select(i => i.SeriesId)).Where(id => id > 0).Distinct().ToArray();
        if (series.Length > 0)
        {
            token.ThrowIfCancellationRequested();
            var query = Query(user);
            query.AncestorIds = series;
            query.IncludeItemTypes = new[] { nameof(Episode) };
            foreach (var item in library.GetItemList(query, token)) variants[item.InternalId] = item;
        }
        return variants.Values.ToArray();
    }

    /// <summary>Catch-up uses the same folder/type eligibility and a batch-scoped favorite snapshot.</summary>
    public static bool Matches(ILibraryManager library, BaseItem item, string? scope, LibraryScopeKind kind,
        CancellationToken token, ISet<long>? favoriteIds = null)
    {
        token.ThrowIfCancellationRequested();
        if (kind == LibraryScopeKind.Marker) ValidateMarkerScope(scope);
        var selected = Parse(scope);
        if (selected.Contains(None) || !EligibleItem(item, kind)) return false;
        var parents = library.GetCollectionFolders(item, token).Select(f => f.InternalId).ToHashSet();
        var folders = library.GetVirtualFolders().Where(f => EligibleFolder(f, kind) && parents.Contains(FolderId(library, f) ?? 0));
        return folders.Any(f => MatchesFolder(f, selected) || selected.Contains(Favorites) && favoriteIds?.Contains(item.InternalId) == true);
    }

    public static HashSet<long>? FavoriteIds(ILibraryManager library, string? scope, LibraryScopeKind kind, CancellationToken token) =>
        Parse(scope).Contains(Favorites) && !Parse(scope).Contains(None)
            ? SelectForServer(library, Favorites, kind, token).Select(i => i.InternalId).ToHashSet() : null;

    private static long? FolderId(ILibraryManager library, VirtualFolderInfo folder) =>
        long.TryParse(folder.ItemId, out var id) ? id : library.GetItemById(folder.ItemId)?.InternalId;

    private static InternalItemsQuery Query(User? user) => new(user!)
    { Recursive = true, GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false };
}
