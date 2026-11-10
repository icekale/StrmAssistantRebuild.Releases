using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Events;
using StrmAssistantRebuild.IntroSkip;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

public sealed class MediaCatchupService : IDisposable
{
    private readonly Plugin plugin;
    private readonly ILibraryManager library;
    private readonly DelayedCatchupQueue queue;
    private readonly IUserDataManager? userData;
    private readonly IProviderManager? providers;
    private readonly object gate = new();
    private readonly Dictionary<long, bool> nativeRefreshes = new();
    private readonly Dictionary<long, AlternativeStop> alternativeStops = new();
    private readonly IUserManager? alternativeUsers;
    private bool alternativeActive, alternativeRemoteRequired;
    private sealed record AlternativeIdentity(long Id, Guid Guid, string Path, string Key, long Parent, DateTimeOffset Created)
    {
        internal static AlternativeIdentity Of(BaseItem item) => new(item.InternalId, item.Id, item.Path, item.PresentationUniqueKey, item.ParentId, item.DateCreated);
    }
    private sealed record AlternativeStop(AlternativeIdentity Item, string Source, string Session, string PlaySession, long User, long Epoch);
    public bool IsAlternativeConsumerReady => !disposed && !queue.Completion.IsCompleted && alternativeUsers != null &&
        plugin.MediaInfoApi?.SupportsAlternativeRefresh == true && plugin.UserExperienceService?.IsPlaybackStopForwardingActive == true &&
        !plugin.WorkQueue.StoppingToken.IsCancellationRequested;
    public bool IsAlternativeExtractActive => alternativeActive && CurrentRemoteEntryReady(alternativeRemoteRequired) &&
        IsAlternativeConsumerReady && ExclusiveMediaControl.IsHealthy && ExclusiveSaverControl.IsHealthy;
    private bool CurrentRemoteEntryReady(bool required)
    {
        if (!required) return true;
        try
        {
            var inventory = providers?.ImageProviders;
            return inventory is { Length: > 0 } && inventory.All(provider => provider != null) &&
                ReferenceEquals(providers, BaseItem.ProviderManager) && ExclusiveRemoteFetcherControl.IsHealthy;
        }
        catch { return false; }
    }
    private string? configuration;
    private long configurationEpoch;
    private bool disposed;
    // Generated counts core stage completion or native Created=true, not reused canonical files.
    // Committed counts incremental SQL receipts only.
    // Native collector commits are not observed by this diagnostic (PartialNativeFailure rows).
    public sealed record FingerprintSeasonInfo(long SeasonId, string State, int Selected, int Generated,
        int Existing, int Skipped, int Committed, int Failed, double Progress);
    public sealed record FingerprintBatchInfo(string State, IReadOnlyList<FingerprintSeasonInfo> Seasons);
    private FingerprintBatchInfo? lastFingerprintBatch;
    /// <summary>Last fingerprint batch only: immutable counts, no paths or persistent selection inventory.</summary>
    public FingerprintBatchInfo? LastFingerprintBatch => Volatile.Read(ref lastFingerprintBatch);
    public Task Completion => queue.Completion;
    public int PendingCount => queue.PendingCount;
    public bool IsBusy => queue.IsBusy;
    public MediaCatchupService(Plugin plugin, ILibraryManager library, IUserDataManager? userData = null, IProviderManager? providerManager = null, IUserManager? userManager = null)
    {
        this.plugin = plugin;
        this.library = library;
        this.userData = userData;
        providers = providerManager;
        alternativeUsers = userManager;
        queue = new DelayedCatchupQueue(Process, text => plugin.Logger.Warn("[StrmAssistantRebuild] {0}", text));
        library.ItemAdded += OnItemAdded;
        if (userData != null) userData.UserDataSaved += OnUserDataSaved;
        if (providers != null) providers.RefreshCompleted += OnRefreshCompleted;
        Configure();
    }
    public void Configure()
    {
        var next = ReadConfiguration(out var general, out var intro);
        var media = plugin.MediaStore.GetOptions();
        var selected = ExclusiveMediaControl.TryGetAlternativeSelection(media.ExclusiveControlFeatures, out var saverFeatures);
        var requiresRemote = selected && saverFeatures.Split(',').Contains("BlockRemoteFetcher", StringComparer.Ordinal);
        var active = CurrentRemoteEntryReady(requiresRemote) && IsAlternativeConsumerReady && ExclusiveMediaControl.IsHealthy &&
            ExclusiveSaverControl.IsHealthy && media.ExclusiveExtract && selected;
        lock (gate)
        {
            if (disposed) return;
            var changed = configuration != next || alternativeActive != active;
            configuration = next; alternativeActive = active; alternativeRemoteRequired = requiresRemote;
            if (changed) { configurationEpoch++; nativeRefreshes.Clear(); alternativeStops.Clear(); }
            queue.Configure(Enabled(general, intro) || active, reset: changed);
        }
    }
    private string ReadConfiguration(out GeneralOptions general, out IntroSkipOptions intro)
    {
        general = plugin.ConfigurationStore.GetOptions().GeneralOptions;
        var media = plugin.MediaStore.GetOptions();
        intro = plugin.IntroStore.GetOptions();
        var refresh = plugin.MetadataStore.GetOptions().MetadataRefreshOptions;
        // Compare consumed settings, not UI catalogs or unrelated concurrency/display edits.
        return JsonSerializer.Serialize(new
        {
            general.CatchupMode, general.CatchupTaskScope, general.CatchupMinimumDelaySeconds,
            Media = new { media.LibraryScope, media.MediaInfoExcludeKeywords, media.MediaInfoExcludeMediaContainers,
                media.EnableImageCapture, media.ImageCapturePosition, media.ImageCapturePostProcessMode, media.ImageCaptureExcludeMediaContainers,
                media.MediaInfoExtractionTimeoutMs, media.ImageExtractionTimeoutMultiplier, media.PersistMediaInfoMode,
                media.MediaInfoJsonRootFolder, media.CustomProbePath, media.CustomEncoderPath, media.ClearEmbeddedMetadata,
                media.ExclusiveExtract, media.ExclusiveControlFeatures },
            Intro = new { intro.EnableIntroSkip, intro.UnlockIntroSkip, intro.LibraryScope, intro.MarkerEnabledLibraryScope,
                intro.FingerprintExcludeKeywords, intro.IntroSkipScrapeOptions.EnableAutomaticIntroScraping,
                intro.IntroSkipScrapeOptions.UnifiedIntroDbProviders, intro.IntroSkipScrapeOptions.TidbApiKey, intro.IntroSkipScrapeOptions.IntroDbApiKey },
            Refresh = new { refresh.MetadataRefreshLibraryScope, refresh.MetadataRefreshPreferences, refresh.MetadataRefreshLookbackDays }
        });
    }
    private static bool Enabled(GeneralOptions general, IntroSkipOptions intro) =>
        general.CatchupMode && Selected(general.CatchupTaskScope).Overlaps(Enum.GetNames<GeneralOptions.CatchupTask>()) ||
        intro.IntroSkipScrapeOptions.EnableAutomaticIntroScraping;
    private static HashSet<string> Selected(string? scope) => string.IsNullOrWhiteSpace(scope)
        ? Enum.GetNames<GeneralOptions.CatchupTask>().ToHashSet(StringComparer.OrdinalIgnoreCase)
        : scope.Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private void Enqueue(BaseItem item, bool refreshed)
    {
        if (item is not (Video or Audio or Series or Season) || string.IsNullOrWhiteSpace(item.Path) ||
            MetadataEnhancePipeline.IsOwnOperation(item.InternalId)) return;
        var general = plugin.ConfigurationStore.GetOptions().GeneralOptions;
        if (!Enabled(general, plugin.IntroStore.GetOptions())) return;
        lock (gate)
        {
            if (disposed) return;
            if (queue.Enqueue(item.InternalId, TimeSpan.FromSeconds(Math.Clamp(general.CatchupMinimumDelaySeconds, 0, 86400))))
                nativeRefreshes[item.InternalId] = refreshed || nativeRefreshes.GetValueOrDefault(item.InternalId);
            else plugin.Logger.Warn("[StrmAssistantRebuild] Catch-up queue full or disabled; item {0} remains available to later events or tasks.", item.InternalId);
        }
    }
    /// <summary>Called by the existing stop subscriber. Value capture only: no library/source/file I/O.</summary>
    public void OnPlaybackStop(PlaybackStopEventArgs e)
    {
        if (e.Item?.GetType() != typeof(Movie) || e.Item.InternalId <= 0 || string.IsNullOrEmpty(e.MediaSourceId) ||
            string.IsNullOrEmpty(e.Session?.Id) || string.IsNullOrEmpty(e.PlaySessionId) || e.Session.UserInternalId <= 0) return;
        var identity = AlternativeIdentity.Of(e.Item);
        lock (gate)
        {
            if (!alternativeActive || disposed || !ExclusiveMediaControl.IsEnabled) return;
            if (queue.Enqueue(identity.Id, TimeSpan.Zero))
                alternativeStops[identity.Id] = new(identity, e.MediaSourceId, e.Session.Id, e.PlaySessionId, e.Session.UserInternalId, configurationEpoch);
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        try { Enqueue(e.Item, false); }
        catch (Exception ex) { plugin.Logger.Warn("[StrmAssistantRebuild] ItemAdded catch-up degraded: {0}", ex.Message); }
    }
    private void OnRefreshCompleted(object? sender, GenericEventArgs<RefreshProgressInfo> e)
    {
        try { Enqueue(e.Argument.Item, true); }
        catch (Exception ex) { plugin.Logger.Warn("[StrmAssistantRebuild] RefreshCompleted catch-up degraded: {0}", ex.Message); }
    }
    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            if (e.User?.Policy.IsDisabled != false || e.UserData?.IsFavorite != true ||
                e.SaveReason is not (MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserRating or MediaBrowser.Model.Entities.UserDataSaveReason.Import) ||
                e.Item is not (Video or Series)) return;
            var general = plugin.ConfigurationStore.GetOptions().GeneralOptions;
            if (!general.CatchupMode || !Selected(general.CatchupTaskScope).Overlaps(new[] { "MediaInfo", "IntroSkip", "Fingerprint" })) return;
            // Series expansion and current favorites are read on the worker, never on the event thread.
            var item = e.Item;
            if (item is Episode episode && episode.SeriesId > 0)
            {
                lock (gate)
                {
                    if (disposed) return;
                    if (queue.Enqueue(episode.SeriesId, TimeSpan.FromSeconds(Math.Clamp(general.CatchupMinimumDelaySeconds, 0, 86400))))
                        nativeRefreshes.TryAdd(episode.SeriesId, false);
                }
            }
            else Enqueue(item, false);
        }
        catch (Exception ex) { plugin.Logger.Warn("[StrmAssistantRebuild] Favorite catch-up degraded: {0}", ex.Message); }
    }
    private async Task Process(long[] ids, CancellationToken token)
    {
        var refreshed = new HashSet<long>();
        var ordinary = new List<long>();
        var alternatives = new List<AlternativeStop>();
        lock (gate)
        {
            token.ThrowIfCancellationRequested(); // An old cancelled batch must not consume new-generation origins.
            foreach (var id in ids)
            {
                // Both origins can occupy the same queue key. Neither consumes the other's work.
                if (alternativeStops.Remove(id, out var stop)) alternatives.Add(stop);
                if (nativeRefreshes.Remove(id, out var native))
                { ordinary.Add(id); if (native) refreshed.Add(id); }
            }
        }
        foreach (var stop in alternatives)
        {
            try { await ProcessAlternative(stop, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { plugin.Logger.Warn("[StrmAssistantRebuild] Alternative seed {0}: {1}", stop.Item.Id, error.GetType().Name); }
        }
        ids = ordinary.ToArray();
        if (ids.Length == 0) return;
        var items = new Dictionary<long, BaseItem>();
        var alreadyRefreshed = new HashSet<long>();
        foreach (var id in ids)
        {
            await Attempt("lookup", id, async () =>
            {
                var item = library.GetItemById(id);
                if (item == null) return;
                var candidates = item is Movie or Series && !string.IsNullOrWhiteSpace(item.PresentationUniqueKey)
                    ? library.GetItemList(new InternalItemsQuery
                    {
                        PresentationUniqueKey = item.PresentationUniqueKey,
                        IncludeItemTypes = new[] { nameof(Movie), nameof(Series) }, GroupByPresentationUniqueKey = false
                    }, token).Append(item).DistinctBy(i => i.InternalId)
                    : new[] { item };
                foreach (var candidate in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    var expanded = candidate is Series or Season
                        ? library.GetItemList(new InternalItemsQuery
                        {
                            ParentIds = new[] { candidate.InternalId }, Recursive = true,
                            IncludeItemTypes = new[] { nameof(Episode) }, GroupByPresentationUniqueKey = false
                        }, token) : new[] { candidate };
                    foreach (var entry in expanded.Where(i => i is Video or Audio))
                    {
                        items[entry.InternalId] = entry;
                        // A series/season refresh does not establish that its episodes refreshed.
                        if (refreshed.Contains(id) && entry.InternalId == id) alreadyRefreshed.Add(entry.InternalId);
                    }
                }
                await Task.CompletedTask;
            }, token).ConfigureAwait(false);
        }
        var general = plugin.ConfigurationStore.GetOptions().GeneralOptions;
        var selected = Selected(general.CatchupTaskScope);
        var intro = plugin.IntroStore.GetOptions();
        var mediaOptions = plugin.MediaStore.GetOptions();
        if (general.CatchupMode && (selected.Contains("MediaInfo") || selected.Contains("IntroSkip")))
        {
            await Attempt("media batch", 0, async () =>
            {
                var favoriteIds = selected.Contains("MediaInfo") ? ScopeSelection.FavoriteIds(library, mediaOptions.LibraryScope, LibraryScopeKind.Media, token) : null;
                var media = items.Values.Where(i =>
                    selected.Contains("MediaInfo") && MediaTaskSelection.Matches(library, i, mediaOptions, token, favoriteIds) ||
                    selected.Contains("IntroSkip") && intro.EnableIntroSkip && i is Episode && InIntroScope(i, intro, token)).ToArray();
                await plugin.WorkQueue.RunBatch(media, async (item, ct) =>
                {
                    using var origin = MetadataEnhancePipeline.SuppressRefreshEvents(item.InternalId);
                    await MediaExtractionPipeline.Extract(plugin, item, mediaOptions, ct).ConfigureAwait(false);
                }, new CatchupProgress(plugin), token, itemKey: i => i.InternalId).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        foreach (var item in items.Values)
        {
            if (general.CatchupMode && selected.Contains("EpisodeRefresh") && !alreadyRefreshed.Contains(item.InternalId) &&
                plugin.MetadataPipeline != null && item is Episode)
                await Attempt("episode refresh", item.InternalId, async () =>
                {
                    var refresh = plugin.MetadataStore.GetOptions().MetadataRefreshOptions;
                    if (ScopeSelection.Matches(library, item, refresh.MetadataRefreshLibraryScope, LibraryScopeKind.Metadata, token) &&
                        MetadataRefreshPolicy.NeedsRefresh(item, refresh, DateTimeOffset.UtcNow))
                        await plugin.MetadataPipeline.RefreshAsync(item, token).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            await Attempt("intro", item.InternalId, async () =>
            {
                if (!InIntroScope(item, intro, token) || plugin.ChapterApi == null) return;
                if (general.CatchupMode && selected.Contains("IntroSkip") && intro.EnableIntroSkip && item is Episode episode)
                    await plugin.ChapterApi.PopulateFromSeasonAsync(episode, token).ConfigureAwait(false);
                // Extraction and native refresh can make a previously runtime-less ItemAdded ready.
                if (!intro.IntroSkipScrapeOptions.EnableAutomaticIntroScraping || plugin.IntroScrapingService == null || item.RunTimeTicks is not > 0) return;
                var existing = plugin.ChapterApi.GetMarkers(item, token);
                if (existing.HasIntro && existing.HasCredits) return;
                var markers = await plugin.IntroScrapingService.GetMarkersAsync(IntroLookupRequest.FromItem(item), intro.IntroSkipScrapeOptions, token).ConfigureAwait(false);
                if (markers != null) await plugin.ChapterApi.ApplyAsync(item, markers, false, token, "AutomaticIntroDb").ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        if (general.CatchupMode && selected.Contains("Fingerprint") && intro.UnlockIntroSkip)
            await Fingerprints(items.Values, intro, token).ConfigureAwait(false);
    }
    private static bool AlternativeMovie(BaseItem? item) => item?.GetType() == typeof(Movie) && item.InternalId > 0 && item.Id != Guid.Empty &&
        item.ParentId > 0 && !item.ExtraType.HasValue && !item.IsVirtualItem && !string.IsNullOrWhiteSpace(item.PresentationUniqueKey) &&
        !string.IsNullOrWhiteSpace(item.Path) && Path.IsPathRooted(item.Path) && Path.GetExtension(item.Path).Equals(".strm", StringComparison.OrdinalIgnoreCase);

    private void AlternativeAuthority(AlternativeStop stop, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var media = plugin.MediaStore.GetOptions();
        if (!IsAlternativeExtractActive || !media.ExclusiveExtract || !ExclusiveMediaControl.TryGetAlternativeSelection(media.ExclusiveControlFeatures, out var saverFeatures) ||
            saverFeatures.Split(',').Contains("BlockRemoteFetcher", StringComparer.Ordinal) != alternativeRemoteRequired)
            throw new OperationCanceledException("Alternative consumer is inactive.", token);
        lock (gate)
            if (disposed || stop.Epoch != configurationEpoch) throw new OperationCanceledException("Alternative configuration generation changed.", token);
    }

    private async Task ProcessAlternative(AlternativeStop stop, CancellationToken token)
    {
        AlternativeAuthority(stop, token);
        var api = plugin.MediaInfoApi!;
        var display = library.GetItemById(stop.Item.Id);
        if (!AlternativeMovie(display) || AlternativeIdentity.Of(display!) != stop.Item) return;
        var user = alternativeUsers!.GetUserById(stop.User);
        if (user?.InternalId != stop.User || user.Policy?.IsDisabled != false) return;
        bool Visible(BaseItem item) => library.FilterItemsToIdsForUser(new[] { item }, user, null, token).Contains(item.InternalId);
        if (!Visible(display!)) return;
        // Original source membership/equality; no ID prefix parsing, numeric guess, or default fallback.
        BaseItem? played = display;
        if (display!.GetDefaultMediaSourceId() != stop.Source)
        {
            var sources = display.GetMediaSources(true, false, null);
            var matches = sources.Where(source => source.Id == stop.Source).Take(2).ToArray();
            if (matches.Length != 1 || string.IsNullOrEmpty(matches[0].ItemId)) return;
            played = library.GetItemById(matches[0].ItemId);
        }
        if (!AlternativeMovie(played) || played!.PresentationUniqueKey != stop.Item.Key || !Visible(played) ||
            AlternativeIdentity.Of(display) != stop.Item || !api.CanRefreshAlternative(played, token)) return;
        var playedIdentity = AlternativeIdentity.Of(played);
        // Native helper already excludes the actual played version. The displayed item may be a candidate.
        var group = ((Movie)played).GetAlternateVersionIds().Append(played.InternalId).Distinct().OrderBy(id => id).ToArray();
        if (group.Length < 2 || group.Length > 64 || group.Any(id => id <= 0) || !group.Contains(display.InternalId)) return;
        var members = new Dictionary<long, (BaseItem Item, AlternativeIdentity Identity)>();
        foreach (var id in group)
        {
            token.ThrowIfCancellationRequested();
            var item = library.GetItemById(id);
            if (!AlternativeMovie(item) || item!.InternalId != id || item.PresentationUniqueKey != stop.Item.Key) return;
            members.Add(id, (item, AlternativeIdentity.Of(item)));
        }
        var queryIdentities = new List<(BaseItem Item, AlternativeIdentity Identity)>();
        void Pure()
        {
            if (AlternativeIdentity.Of(display) != stop.Item || AlternativeIdentity.Of(played) != playedIdentity ||
                queryIdentities.Any(pair => AlternativeIdentity.Of(pair.Item) != pair.Identity) ||
                members.Any(pair => AlternativeIdentity.Of(pair.Value.Item) != pair.Value.Identity))
                throw new InvalidOperationException("Alternative selection identity changed.");
        }
        var candidates = library.GetItemList(new InternalItemsQuery(user)
        {
            ItemIds = group.Where(id => id != playedIdentity.Id).ToArray(), HasPath = true, HasAudioStream = false,
            MediaTypes = new[] { "Video" }, IncludeItemTypes = new[] { nameof(Movie) }, GroupByPresentationUniqueKey = false,
            EnableTotalRecordCount = false, Limit = 65
        }, token);
        Pure();
        if (candidates.Length > 64) return;
        queryIdentities.AddRange(candidates.Select(item => (item, AlternativeIdentity.Of(item))));
        if (candidates.Any(item => item.InternalId == playedIdentity.Id || !members.ContainsKey(item.InternalId))) return;
        var selected = new List<BaseItem>();
        foreach (var item in candidates.DistinctBy(item => item.InternalId))
        {
            if (item.InternalId == playedIdentity.Id || !members.TryGetValue(item.InternalId, out var member) ||
                AlternativeIdentity.Of(item) != member.Identity || !Visible(item) || !api.CanRefreshAlternative(item, token)) continue;
            selected.Add(item); Pure();
        }
        Pure(); AlternativeAuthority(stop, token);
        if (selected.Count == 0) return;
        await plugin.WorkQueue.RunBatch(selected, async (selectedItem, ct) =>
        {
            try
            {
                void Revalidate(CancellationToken check)
                {
                    AlternativeAuthority(stop, check); Pure();
                    if (AlternativeIdentity.Of(selectedItem) != members[selectedItem.InternalId].Identity)
                        throw new InvalidOperationException("Alternative selected object changed.");
                    var currentUser = alternativeUsers.GetUserById(stop.User);
                    if (currentUser?.InternalId != stop.User || currentUser.Policy?.IsDisabled != false) throw new InvalidOperationException("Alternative actor changed.");
                    var current = new List<BaseItem>();
                    foreach (var id in new[] { stop.Item.Id, playedIdentity.Id, selectedItem.InternalId }.Distinct())
                    {
                        var fresh = library.GetItemById(id);
                        if (!AlternativeMovie(fresh) || AlternativeIdentity.Of(fresh!) != members[id].Identity)
                            throw new InvalidOperationException("Alternative member changed.");
                        current.Add(fresh!);
                    }
                    if (!library.FilterItemsToIdsForUser(current.ToArray(), currentUser, null, check).ToHashSet().IsSupersetOf(current.Select(i => i.InternalId)))
                        throw new InvalidOperationException("Alternative visibility changed.");
                    var actual = (Movie)current.First(i => i.InternalId == playedIdentity.Id);
                    var now = actual.GetAlternateVersionIds().Append(actual.InternalId).Distinct().OrderBy(id => id).ToArray();
                    if (!now.SequenceEqual(group)) throw new InvalidOperationException("Alternative group changed.");
                    // Preserve native SQL HasAudioStream semantics, including external audio, rather than guessing from cached fields.
                    var missing = library.GetItemList(new InternalItemsQuery(currentUser)
                    {
                        ItemIds = new[] { selectedItem.InternalId }, HasPath = true, HasAudioStream = false, MediaTypes = new[] { "Video" },
                        IncludeItemTypes = new[] { nameof(Movie) }, GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false, Limit = 2
                    }, check);
                    if (missing.Length != 1 || AlternativeIdentity.Of(missing[0]) != members[selectedItem.InternalId].Identity)
                        throw new InvalidOperationException("Alternative audio eligibility changed.");
                    Pure(); AlternativeAuthority(stop, check);
                }
                Revalidate(ct);
                using var owned = ExclusiveMediaControl.EnterOwnedOperation();
                using var origin = MetadataEnhancePipeline.SuppressRefreshEvents(selectedItem.InternalId);
                await api.RefreshAlternativeAsync(selectedItem, ct, Revalidate, () =>
                {
                    // Only the raw native inventory getter is added here; recheck pure identity/epoch/owners after it.
                    // This is finite admission, not atomic ownership of native provider/library configuration.
                    var remoteReady = CurrentRemoteEntryReady(alternativeRemoteRequired);
                    Pure();
                    lock (gate)
                        if (disposed || !alternativeActive || stop.Epoch != configurationEpoch) throw new OperationCanceledException(ct);
                    if (!remoteReady || !ExclusiveMediaControl.IsHealthy || !ExclusiveSaverControl.IsHealthy ||
                        (alternativeRemoteRequired && !ExclusiveRemoteFetcherControl.IsHealthy))
                        throw new InvalidOperationException("Alternative gate ownership changed.");
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // QueueManager logs exception messages; sanitize the boundary so native URL-bearing errors never reach it.
                throw new InvalidOperationException("Alternative item " + selectedItem.InternalId + " failed (" + error.GetType().Name + ").");
            }
        }, new CatchupProgress(plugin), token, itemKey: item => item.InternalId).ConfigureAwait(false);
    }

    private async Task Fingerprints(IEnumerable<BaseItem> items, IntroSkipOptions intro, CancellationToken token)
    {
        var rows = new List<FingerprintSeasonInfo>();
        void Publish(string state) => Volatile.Write(ref lastFingerprintBatch, new(state, Array.AsReadOnly(rows.ToArray())));
        Publish("Running");
        var failed = false;
        try
        {
            var fingerprint = plugin.FingerprintApi;
            if (fingerprint?.IsSupported != true || plugin.ChapterApi == null)
            {
                Publish("Unavailable");
                plugin.Logger.Warn("[StrmAssistantRebuild] Catch-up fingerprint unavailable: {0}", fingerprint?.SupportDetail ?? "services not initialized");
                return;
            }
            // Capture all event seeds before any favorite, scope, season or library callback. Group keys
            // are frozen too: a callback must not redirect a later seed into a different physical season.
            var seeds = items.OfType<Episode>().Select(e => (Item: e, Identity: FingerprintIdentity.Of(e))).ToArray();
            var frozen = seeds.Select(e => (Item: (BaseItem)e.Item, e.Identity)).ToList();
            var markerScope = intro.MarkerEnabledLibraryScope;
            var exclusions = intro.FingerprintExcludeKeywords;
            string? expectedConfiguration;
            long epoch;
            lock (gate) { expectedConfiguration = configuration; epoch = configurationEpoch; }
            void Pure()
            {
                token.ThrowIfCancellationRequested();
                if (frozen.Any(e => FingerprintIdentity.Of(e.Item) != e.Identity))
                    throw new InvalidOperationException("Catch-up fingerprint selection identity changed.");
            }
            void Authority(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                // Store callbacks stay outside gate and outside the core's native SQL writer lease.
                var current = ReadConfiguration(out var general, out var options);
                lock (gate)
                {
                    if (disposed || configurationEpoch != epoch || configuration != expectedConfiguration || current != expectedConfiguration)
                        throw new InvalidOperationException("Catch-up fingerprint configuration generation changed.");
                }
                if (!ReferenceEquals(plugin, Plugin.Instance) || !ReferenceEquals(fingerprint, plugin.FingerprintApi) ||
                    !general.CatchupMode || !Selected(general.CatchupTaskScope).Contains("Fingerprint") ||
                    !options.UnlockIntroSkip || options.MarkerEnabledLibraryScope != markerScope || options.FingerprintExcludeKeywords != exclusions)
                    throw new InvalidOperationException("Catch-up fingerprint authority changed.");
                Pure();
                ct.ThrowIfCancellationRequested();
            }
            // Batch-local dedup only. Capture physical roots/ancestors before scope callbacks, sibling
            // queries or core invocation; M106 later retains the exact filtered sibling array.
            var seasons = new List<(long Id, Season Item)>();
            foreach (var group in seeds.GroupBy(e => e.Identity.Album))
            {
                token.ThrowIfCancellationRequested();
                var season = library.GetItemById(group.Key) as Season;
                Pure();
                if (season == null) continue;
                if (season.InternalId != group.Key) throw new InvalidOperationException("Catch-up physical season identity changed.");
                frozen.Add((season, FingerprintIdentity.Of(season)));
                var seen = new HashSet<long> { group.Key };
                for (var id = season.ParentId; id != 0;)
                {
                    if (seen.Count > 128 || !seen.Add(id)) throw new InvalidOperationException("Catch-up physical ancestry is cyclic or unbounded.");
                    var parent = library.GetItemById(id);
                    Pure();
                    if (parent == null || parent.InternalId != id) throw new InvalidOperationException("Catch-up physical ancestor disappeared.");
                    frozen.Add((parent, FingerprintIdentity.Of(parent)));
                    id = parent.ParentId;
                }
                seasons.Add((group.Key, season));
            }
            var favorites = ScopeSelection.FavoriteIds(library, markerScope, LibraryScopeKind.Marker, token);
            var exclude = ScopeSelection.Parse(exclusions);
            bool Candidate(Episode episode) => ScopeSelection.Matches(library, episode, markerScope, LibraryScopeKind.Marker, token, favorites) &&
                !exclude.Any(k => episode.Path.Contains(k, StringComparison.OrdinalIgnoreCase));
            var selectedSeeds = seeds.Where(e => Candidate(e.Item)).ToArray();
            Pure();
            foreach (var group in seasons.Where(s => selectedSeeds.Any(e => e.Identity.Album == s.Id)))
            {
                token.ThrowIfCancellationRequested();
                var row = -1;
                var count = 0;
                IntroRebuildReport? report = null;
                try
                {
                    Pure();
                    var season = group.Item;
                    var raw = library.GetItemList(new InternalItemsQuery
                    {
                        ParentIds = new[] { group.Id }, Recursive = true,
                        IncludeItemTypes = new[] { nameof(Episode) }, GroupByPresentationUniqueKey = false
                    }, token).OfType<Episode>().Select(e => (Item: e, Identity: FingerprintIdentity.Of(e))).ToArray();
                    Pure();
                    var selected = raw.Where(e => Candidate(e.Item)).DistinctBy(e => e.Identity.Id).ToArray();
                    // A late scope/keyword callback may mutate an earlier object, even one already filtered.
                    if (raw.Any(e => FingerprintIdentity.Of(e.Item) != e.Identity))
                        throw new InvalidOperationException("Catch-up sibling selection changed during filtering.");
                    Pure();
                    var siblings = selected.Select(e => e.Item).ToArray();
                    count = siblings.Length;
                    if (siblings.Any(e => Path.GetExtension(e.Path).Equals(".strm", StringComparison.OrdinalIgnoreCase)))
                    {
                        frozen.AddRange(selected.Select(e => (Item: (BaseItem)e.Item, e.Identity)));
                        row = rows.Count;
                        rows.Add(new(group.Id, "Running", count, 0, 0, 0, 0, 0, 0)); Publish("Running");
                        Authority(token);
                        using var owned = ExclusiveMediaControl.EnterOwnedOperation();
                        // Core owns quotas, source classification, missing-only generation and intro-only SQL.
                        // Its persistence exports JSON but raises no native refresh: no suppression inventory.
                        report = await fingerprint.ProcessIntroForSelectedSeasonAsync(season, siblings, new CatchupProgress(plugin, value =>
                        {
                            if (!double.IsFinite(value)) return;
                            rows[row] = rows[row] with { Progress = Math.Max(rows[row].Progress, Math.Clamp(value, 0, 100)) };
                            Publish("Running");
                        }), token, Authority).ConfigureAwait(false);
                        rows[row] = FingerprintReceipt(group.Id, count, report, 100);
                        Authority(token);
                        failed |= report.State != "Completed" || rows[row].Failed != 0;
                        Publish("Running");
                        continue;
                    }
                    // Physical-only seasons retain the existing native eligibility/create/collector path.
                    if (!selectedSeeds.Any(e => e.Identity.Album == group.Id && fingerprint.IsEpisodeSupported(e.Item))) continue;
                    siblings = siblings.Where(fingerprint.IsEpisodeSupported).ToArray();
                    var missing = siblings.Where(e => !plugin.ChapterApi.GetMarkers(e, token).HasIntro).ToArray();
                    if (missing.Length == 0) continue;
                    var generated = 0;
                    var nativeBatch = await plugin.WorkQueue.RunBatch(missing, async (episode, ct) =>
                    {
                        using var operation = ExclusiveMediaControl.EnterOwnedOperation();
                        using var origin = MetadataEnhancePipeline.SuppressRefreshEvents(episode.InternalId);
                        var created = await fingerprint.CreateTitleFingerprint(episode, ct).ConfigureAwait(false);
                        if (created.Item2) Interlocked.Increment(ref generated);
                    }, new CatchupProgress(plugin), token, itemKey: e => e.InternalId).ConfigureAwait(false);
                    if (nativeBatch.Failed != 0)
                    {
                        failed = true; row = rows.Count;
                        rows.Add(new(group.Id, "PartialNativeFailure", siblings.Length, generated, nativeBatch.Done - generated, 0, 0, nativeBatch.Failed, 100));
                        Publish("Running");
                    }
                    if (siblings.Length < 2) continue;
                    using var nativeOwned = ExclusiveMediaControl.EnterOwnedOperation();
                    await fingerprint.UpdateIntroMarkerForSeason(season, token, allowedItems: siblings.Select(e => e.InternalId).ToHashSet()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    report ??= ex.Data["IntroRebuildReport"] as IntroRebuildReport;
                    var cancelled = token.IsCancellationRequested;
                    var state = cancelled ? "Cancelled" : "Failed";
                    var receipt = report == null ? new FingerprintSeasonInfo(group.Id, state, count, 0, 0, 0, 0, count, 0) :
                        FingerprintReceipt(group.Id, count, report, row < 0 ? 0 : rows[row].Progress) with { State = state };
                    if (row < 0) rows.Add(receipt); else rows[row] = receipt;
                    failed = true; Publish("Running");
                    if (cancelled) throw;
                    plugin.Logger.Warn("[StrmAssistantRebuild] Catch-up fingerprint season {0}: {1}; a later external event may retry.", group.Id, ex.GetType().Name);
                }
            }
            token.ThrowIfCancellationRequested();
            Publish(failed ? "CompletedWithFailures" : "Completed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Publish("Cancelled"); throw; }
        catch (Exception ex)
        {
            Publish("Failed");
            plugin.Logger.Warn("[StrmAssistantRebuild] Catch-up fingerprint selection: {0}; a later external event may retry.", ex.GetType().Name);
        }
    }
    private sealed record FingerprintIdentity(Type Type, long Id, Guid Guid, string Path, long Parent, long Series,
        long Album, int? Index, int? ParentIndex, DateTimeOffset Created, long? Runtime, string Container, bool Virtual, string External, string Key)
    {
        internal static FingerprintIdentity Of(BaseItem e) => new(e.GetType(), e.InternalId, e.Id, e.Path, e.ParentId,
            e.SeriesId, e.InternalAlbumId, e.IndexNumber, e.ParentIndexNumber, e.DateCreated, e.RunTimeTicks, e.Container,
            e.IsVirtualItem, e.ExternalId, e.PresentationUniqueKey);
    }
    private static FingerprintSeasonInfo FingerprintReceipt(long seasonId, int selected, IntroRebuildReport report, double progress)
    {
        var applied = report.ApplyReport?.Items;
        var failed = report.Preparation.Where(i => i.Error != null || i.StageCleanup?.StartsWith("Retained", StringComparison.Ordinal) == true).Select(i => i.ItemId)
            .Concat(applied?.Where(i => i.Status is "CalculationFailed" or "CommitFailed").Select(i => i.ItemId) ?? Array.Empty<long>()).Distinct().Count();
        var skipped = report.Preparation.Where(i => i.Status is "AlreadyMarked" or "ReferenceOnly").Select(i => i.ItemId)
            .Concat(applied?.Where(i => i.Status is "AlreadyMarked" or "NoMatch").Select(i => i.ItemId) ?? Array.Empty<long>()).Distinct().Count();
        return new(seasonId, report.State, selected, report.Preparation.Count(i => i.Created == true),
            report.Preparation.Count(i => i.Status is "ExistingFingerprint" or "ReferenceOnly"), skipped,
            applied?.Count(i => i.CommitReport?.Committed == true) ?? 0, failed, progress);
    }
    private async Task Attempt(string stage, long id, Func<Task> run, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await run().ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { plugin.Logger.Warn("[StrmAssistantRebuild] Catch-up {0} item {1}: {2}; a later external event may retry.", stage, id, ex.Message); }
    }
    private bool InIntroScope(BaseItem item, IntroSkipOptions options, CancellationToken token) =>
        ScopeSelection.Matches(library, item, options.LibraryScope, LibraryScopeKind.Intro, token);
    public void Dispose()
    {
        library.ItemAdded -= OnItemAdded;
        if (userData != null) userData.UserDataSaved -= OnUserDataSaved;
        if (providers != null) providers.RefreshCompleted -= OnRefreshCompleted;
        lock (gate) { disposed = true; alternativeActive = false; configurationEpoch++; nativeRefreshes.Clear(); alternativeStops.Clear(); queue.Dispose(); }
    }
    private sealed class CatchupProgress : IProgress<double>
    {
        private readonly Plugin plugin;
        private readonly Action<double>? update;
        public CatchupProgress(Plugin plugin, Action<double>? update = null) { this.plugin = plugin; this.update = update; }
        public void Report(double value)
        {
            update?.Invoke(value);
            plugin.Logger.Info("[StrmAssistantRebuild] Catch-up progress {0:F1}%", value);
        }
    }
}
