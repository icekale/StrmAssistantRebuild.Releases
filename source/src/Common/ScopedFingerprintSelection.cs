using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

/// <summary>Physical, server-only scope. Never expands presentation keys or enumerates other libraries.</summary>
internal sealed partial class ScopedFingerprintSelection
{
    internal const int MaxCandidates = 4096;
    private readonly ILibraryManager library;
    private readonly Identity root, owner;
    private readonly Identity[] rootParents;
    private readonly Dictionary<long, Candidate> candidates;
    private sealed record Identity(long Id, Guid Guid, Type Type, string Path, long Parent, long Series,
        long Album, int? Index, int? ParentIndex, DateTimeOffset Created, bool Virtual, string External)
    {
        internal static Identity Of(BaseItem item) => new(item.InternalId, item.Id, item.GetType(), item.Path,
            item.ParentId, item.SeriesId, item.InternalAlbumId, item.IndexNumber, item.ParentIndexNumber, item.DateCreated,
            item.IsVirtualItem, item.ExternalId);
    }
    private sealed record Candidate(Identity Item, Identity[] Parents);
    private sealed class Reads
    {
        private readonly List<(BaseItem Item, Identity Expected)> values = new();
        private readonly bool batch;
        internal Reads(bool batch = false) => this.batch = batch;
        internal void Add(BaseItem item) => values.Add((item, Identity.Of(item)));
        internal void Check(bool final = false)
        {
            if (batch && !final) return; // One linear final pass, not an O(n²) pass per episode.
            if (values.Any(v => Identity.Of(v.Item) != v.Expected))
                throw Invalid("Observed item changed during native read callbacks.");
        }
    }

    private readonly bool selectedOnly;
    private readonly string? capturedFavorites;
    private ScopedFingerprintSelection(ILibraryManager library, Identity root, Identity owner,
        Identity[] parents, Dictionary<long, Candidate>? candidates = null, bool selectedOnly = false, string? capturedFavorites = null)
    { this.library = library; this.root = root; this.owner = owner; rootParents = parents; this.candidates = candidates ?? new();
        this.selectedOnly = selectedOnly; this.capturedFavorites = capturedFavorites; }

    // Only the ordinary server task supplies this already-selected array. No query, PUK/favorite expansion or clone.
    internal static ScopedFingerprintSelection CaptureSelectedSeason(ILibraryManager library, Season season,
        IReadOnlyList<Episode> selection, IntroSkipOptions options, CancellationToken token)
    {
        var original = selection.ToArray();
        var identities = original.Select(Identity.Of).ToArray();
        var seasonIdentity = Identity.Of(season);
        if (original.Length == 0 || original.Length > MaxCandidates ||
            identities.Select(i => i.Id).Distinct().Count() != original.Length ||
            identities.Any(i => i.Type != typeof(Episode) || i.Album != seasonIdentity.Id))
            throw Invalid("Expected a bounded, distinct original selection from one physical season.");
        ScopeSelection.ValidateMarkerScope(options.MarkerEnabledLibraryScope);
        var favorites = ScopeSelection.Parse(options.MarkerEnabledLibraryScope).SetEquals(new[] { ScopeSelection.Favorites })
            ? options.MarkerEnabledLibraryScope : null;
        RequireLocal(season);
        var folders = library.GetCollectionFolders(season, token);
        if (folders.Length != 1 || folders[0] is not CollectionFolder) throw Invalid("Expected one physical library owner.");
        var folder = Read(library, folders[0].InternalId, token);
        if (folder is not CollectionFolder || folder.Id != folders[0].Id) throw Invalid("Library identity differs.");
        var scope = new ScopedFingerprintSelection(library, seasonIdentity, Identity.Of(folder), Parents(library, season, token),
            selectedOnly: true, capturedFavorites: favorites);
        scope.ValidateRoot(options, token);
        for (var n = 0; n < original.Length; n++)
        {
            var parents = scope.ValidateEpisode(original[n], options, token);
            scope.candidates.Add(identities[n].Id, new(identities[n], parents));
        }
        scope.GuardBatch(season, original, options, token);
        if (Identity.Of(season) != seasonIdentity || original.Where((e, n) => Identity.Of(e) != identities[n]).Any())
            throw Invalid("Original task selection changed during capture.");
        return scope;
    }

    private void ValidateMarkerAcceptance(IntroSkipOptions options)
    {
        ScopeSelection.ValidateMarkerScope(options.MarkerEnabledLibraryScope);
        var marker = ScopeSelection.Parse(options.MarkerEnabledLibraryScope);
        if (capturedFavorites != null && selectedOnly)
        {
            if (options.MarkerEnabledLibraryScope == capturedFavorites) return;
            throw Invalid("Captured favorites scope changed.");
        }
        if (marker.Contains(ScopeSelection.None) || marker.Count != 0 && !marker.Any(value =>
            value == owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) || Guid.TryParse(value, out var id) && id == owner.Guid))
            throw Invalid("Target library is outside the configured marker scope.");
    }

    internal static ScopedFingerprintSelection Capture(ILibraryManager library, long targetId,
        IntroSkipOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(library);
        token.ThrowIfCancellationRequested();
        var item = Read(library, targetId, token);
        if (item is not (Series or Season) || item is Season && item.IndexNumber is not >= 1)
            throw Invalid("Target must be an actual Series or a numbered Season (index >= 1).");
        RequireLocal(item);
        var folders = library.GetCollectionFolders(item, token);
        token.ThrowIfCancellationRequested();
        if (folders.Length != 1 || folders[0] is not CollectionFolder)
            throw Invalid("Target must have exactly one CollectionFolder owner.");
        var folder = Read(library, folders[0].InternalId, token);
        if (folder is not CollectionFolder || folder.Id != folders[0].Id)
            throw Invalid("Target library identity is invalid.");
        var scope = new ScopedFingerprintSelection(library, Identity.Of(item), Identity.Of(folder), Parents(library, item, token));
        scope.ValidateRoot(options, token);
        return scope;
    }

    // Metadata may add children, but may not silently rebind this root/owner/ancestor snapshot.
    // DateCreated and index fields remain strict identities: even legitimate refresh changes fail explicitly.
    internal void ValidateFixedTarget(ILibraryManager expectedLibrary, long targetId, IntroSkipOptions options,
        CancellationToken token)
    {
        if (!ReferenceEquals(library, expectedLibrary) || root.Id != targetId)
            throw Invalid("Fixed target or library manager differs from the captured scope.");
        ValidateRoot(options, token);
    }

    internal ScopedFingerprintSelection Select(IntroSkipOptions options, CancellationToken token)
    {
        ValidateRoot(options, token);
        if (selectedOnly) return this;
        var query = new InternalItemsQuery { ParentIds = new[] { root.Id }, Recursive = true,
            IncludeItemTypes = new[] { nameof(Episode) }, GroupByPresentationUniqueKey = false,
            EnableTotalRecordCount = false, MinParentIndexNumber = 1, Limit = MaxCandidates + 1 };
        // Count raw rows before de-duplication. A sentinel is failure, never truncated success.
        var ids = library.GetInternalItemIds(query, token);
        token.ThrowIfCancellationRequested();
        if (ids.Length > MaxCandidates) throw Invalid("Scoped fingerprint candidate budget exceeded (4096 raw IDs).");
        // Native ParentIds may normalize to physical AncestorIds; no PUK/group/favorite expansion is permitted.
        if (query.Limit != MaxCandidates + 1 || query.MinParentIndexNumber != 1 || !query.Recursive || query.User != null || query.IsFavorite != null ||
            query.EnableTotalRecordCount || !query.IncludeItemTypes.SequenceEqual(new[] { nameof(Episode) }) ||
            query.GroupByPresentationUniqueKey != false || query.EnableGroupByMetadataKey || query.GroupBy.Length != 0 ||
            query.GroupBySeriesId || query.GroupBySeriesPresentationUniqueKey || query.GroupByAlbumId ||
            query.GroupByParentPresentationUniqueKey || query.GroupByPrimaryImagePath || query.GroupProgramsBySeries ||
            query.IsNextUpQuery || query.GroupItemsIntoItemLinkType.HasValue || query.ProjectToMedia)
            throw Invalid("Scoped fingerprint query contract changed.");
        var selected = new Dictionary<long, Candidate>();
        foreach (var id in ids.ToArray().Distinct())
        {
            token.ThrowIfCancellationRequested();
            var item = Read(library, id, token) as Episode ?? throw Invalid("Candidate is not an Episode.");
            var parents = ValidateEpisode(item, options, token);
            selected.Add(id, new(Identity.Of(item), parents));
        }
        ValidateRoot(options, token);
        return new(library, root, owner, rootParents, selected);
    }

    internal Episode[] Episodes(IntroSkipOptions options, CancellationToken token)
    {
        ValidateRoot(options, token);
        return candidates.Keys.Select(id => FreshEpisode(id, options, token)).ToArray();
    }

    internal void Guard(BaseItem item, IntroSkipOptions options, CancellationToken token) => Guard(item, options, token, new Reads());

    internal Action GuardBatch(Season season, IReadOnlyList<Episode> episodes, IntroSkipOptions options, CancellationToken token)
    {
        var reads = new Reads(batch: true);
        Guard(season, options, token, reads);
        foreach (var episode in episodes) Guard(episode, options, token, reads);
        // The last episode's host read can mutate an earlier passed object. No more native reads after this check.
        reads.Check(final: true);
        token.ThrowIfCancellationRequested();
        return () => { token.ThrowIfCancellationRequested(); reads.Check(final: true); };
    }

    private void Guard(BaseItem item, IntroSkipOptions options, CancellationToken token, Reads reads)
    {
        reads.Add(item);
        ValidateRoot(options, token, reads);
        if (item is Episode)
        {
            var fresh = FreshEpisode(item.InternalId, options, token, reads);
            if (Identity.Of(item) != Identity.Of(fresh)) throw Invalid("Passed episode identity changed.");
        }
        else if (item is Season)
        {
            var expected = candidates.Values.SelectMany(c => c.Parents).FirstOrDefault(p => p.Type == typeof(Season) && p.Id == item.InternalId)
                ?? throw Invalid("Season is not part of the selected physical scope.");
            var fresh = Read(library, expected.Id, token, reads);
            if (Identity.Of(fresh) != expected || Identity.Of(item) != expected) throw Invalid("Selected season identity changed.");
            RequireOwner(fresh, token, reads);
        }
        else throw Invalid("Only selected episodes and their physical seasons may be written.");
        token.ThrowIfCancellationRequested();
        reads.Check();
    }

    private Episode FreshEpisode(long id, IntroSkipOptions options, CancellationToken token, Reads? reads = null)
    {
        reads ??= new Reads();
        if (!candidates.TryGetValue(id, out var expected)) throw Invalid("Episode is not in the frozen selected ID set.");
        var item = Read(library, id, token, reads) as Episode ?? throw Invalid("Selected episode disappeared.");
        if (Identity.Of(item) != expected.Item) throw Invalid("Selected episode identity changed.");
        if (!ValidateEpisode(item, options, token, reads).SequenceEqual(expected.Parents)) throw Invalid("Selected episode physical ancestry changed.");
        if (Identity.Of(item) != expected.Item) throw Invalid("Selected episode changed during native reads.");
        reads.Check();
        return item;
    }

    private Identity[] ValidateEpisode(Episode item, IntroSkipOptions options, CancellationToken token, Reads? reads = null)
    {
        reads ??= new Reads();
        reads.Add(item);
        RequireLocal(item);
        RequireOwner(item, token, reads);
        if (!Within(item.Path, root.Path)) throw Invalid("Episode path is outside the selected target.");
        var parents = Parents(library, item, token, reads);
        // AlbumId otherwise falls back to tagged/PUK lookup. Require the actual persisted relation instead.
        var series = parents.SingleOrDefault(p => p.Type == typeof(Series) && p.Id == item.SeriesId);
        var season = parents.SingleOrDefault(p => p.Type == typeof(Season) && p.Id == item.InternalAlbumId);
        if (!parents.Contains(root) || series == null || season == null || season.Index is not >= 1 ||
            season.Series != series.Id || item.ParentIndexNumber != season.Index ||
            (root.Type == typeof(Series) ? item.SeriesId != root.Id : item.InternalAlbumId != root.Id))
            throw Invalid("Episode SeriesId/AlbumId does not match the physical target ancestry.");
        if (!Within(season.Path, series.Path) || !Within(item.Path, season.Path))
            throw Invalid("Episode or season path disagrees with its physical ancestry.");
        var actualSeason = Read(library, season.Id, token, reads);
        RequireLocal(actualSeason);
        RequireOwner(actualSeason, token, reads);
        if (!library.GetLibraryOptions(item).EnableMarkerDetection)
            throw Invalid("Episode marker detection is disabled.");
        token.ThrowIfCancellationRequested();
        reads.Check();
        return parents;
    }

    private void ValidateRoot(IntroSkipOptions options, CancellationToken token, Reads? reads = null)
    {
        token.ThrowIfCancellationRequested();
        reads ??= new Reads();
        ScopeSelection.ValidateMarkerScope(options.MarkerEnabledLibraryScope);
        if (!options.UnlockIntroSkip) throw Invalid("Built-in fingerprint extraction must already be enabled.");
        var current = Read(library, root.Id, token, reads);
        RequireLocal(current);
        if (Identity.Of(current) != root || !Parents(library, current, token, reads).SequenceEqual(rootParents))
            throw Invalid("Bound target identity or physical ancestry changed.");
        if (current is Season && (current.IndexNumber is not >= 1 || !rootParents.Any(p => p.Type == typeof(Series) && p.Id == current.SeriesId)))
            throw Invalid("Target season has no matching physical Series parent.");
        RequireOwner(current, token, reads);
        var folder = Read(library, owner.Id, token, reads);
        if (Identity.Of(folder) != owner || !library.GetLibraryOptions(current).EnableMarkerDetection)
            throw Invalid("Bound library identity or marker enablement changed.");
        // Ordinary server selection may retain its exact favorites snapshot; public target capture stays strict.
        ValidateMarkerAcceptance(options);
        var libraryOptions = library.GetLibraryOptions(folder);
        var type = libraryOptions.ContentType ?? "";
        if (!libraryOptions.EnableMarkerDetection || type.Length != 0 && !type.Equals("tvshows", StringComparison.OrdinalIgnoreCase))
            throw Invalid("Target library marker detection is disabled.");
        token.ThrowIfCancellationRequested();
        if (Identity.Of(current) != root || Identity.Of(folder) != owner)
            throw Invalid("Target or library changed during native reads.");
        reads.Check();
    }

    private void RequireOwner(BaseItem item, CancellationToken token, Reads? reads = null)
    {
        var folders = library.GetCollectionFolders(item, token);
        token.ThrowIfCancellationRequested();
        if (folders.Length != 1 || folders[0] is not CollectionFolder ||
            folders[0].InternalId != owner.Id || folders[0].Id != owner.Guid)
            throw Invalid("Item library owner changed or is ambiguous.");
        reads?.Add(folders[0]);
    }
    private static BaseItem Read(ILibraryManager library, long id, CancellationToken token, Reads? reads = null)
    {
        token.ThrowIfCancellationRequested();
        var item = id > 0 ? library.GetItemById(id) : null;
        token.ThrowIfCancellationRequested();
        if (item == null || item.InternalId != id || item.Id == Guid.Empty) throw Invalid("Missing or invalid persisted item identity.");
        reads?.Add(item);
        return item;
    }
    private static Identity[] Parents(ILibraryManager library, BaseItem item, CancellationToken token, Reads? reads = null)
    {
        var result = new List<Identity>();
        var seen = new HashSet<long> { item.InternalId };
        // Do not use GetParents(): native cached parents can hide a move and its iterator is unbounded.
        for (var id = item.ParentId; id != 0;)
        {
            if (result.Count == 128 || !seen.Add(id)) throw Invalid("Physical ancestry is cyclic or exceeds 128 parents.");
            var parent = Read(library, id, token, reads);
            result.Add(Identity.Of(parent));
            id = parent.ParentId;
        }
        return result.ToArray();
    }
    private static void RequireLocal(BaseItem item)
    {
        if (item.IsVirtualItem || !item.IsFileProtocol || !string.IsNullOrEmpty(item.ExternalId) ||
            string.IsNullOrWhiteSpace(item.Path) || !Path.IsPathRooted(item.Path))
            throw Invalid("Expected a persisted local, non-virtual physical item.");
    }
    private static bool Within(string path, string directory) => !string.IsNullOrWhiteSpace(directory) &&
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static InvalidOperationException Invalid(string reason) => new("Scoped fingerprint: " + reason);
}
