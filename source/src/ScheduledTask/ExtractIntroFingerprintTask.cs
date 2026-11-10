using System.Runtime.CompilerServices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.IntroSkip;
using StrmAssistantRebuild.Options;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class ExtractIntroFingerprintTask : IScheduledTask, IScheduledTaskWithExecuteOptions
{
    private readonly ILibraryManager library;
    public ExtractIntroFingerprintTask(ILibraryManager libraryManager) => library = libraryManager;
    public string Name => Resources.ExtractIntroFingerprintTask_Name_Extract_Intro_Fingerprint;
    public string Description => Resources.ExtractIntroFingerprintTask_Description_Extracts_intro_fingerprint_from_episodes;
    public string Category => "Strm Assistant Rebuild";
    public string Key => "IntroFingerprintExtractTask";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    private static int running;
    public static bool IsRunning => Volatile.Read(ref running) != 0;
    private static readonly ConditionalWeakTable<TaskOptions, BoundScope> bindings = new();
    private sealed class BoundScope
    {
        internal readonly long TargetId;
        internal readonly ScopedFingerprintSelection? Scope;
        internal readonly Func<IProgress<double>, CancellationToken, Task>? Rebuild;
        internal readonly Plugin Owner;
        private readonly ExtractIntroFingerprintTask task;
        private int claimed;
        internal BoundScope(long id, ScopedFingerprintSelection scope, Plugin owner, ExtractIntroFingerprintTask task)
        { TargetId = id; Scope = scope; Owner = owner; this.task = task; }
        internal BoundScope(long id, Func<IProgress<double>, CancellationToken, Task> rebuild, Plugin owner, ExtractIntroFingerprintTask task)
        { TargetId = id; Rebuild = rebuild; Owner = owner; this.task = task; }
        internal void Claim(ExtractIntroFingerprintTask caller)
        {
            if (Interlocked.Exchange(ref claimed, 1) != 0)
                throw new InvalidOperationException("Scoped fingerprint options were already claimed; create new options for another run.");
            if (!ReferenceEquals(caller, task)) throw new InvalidOperationException("Scoped fingerprint task owner changed.");
        }
        internal void RequireOwner(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Owner.WorkQueue.StoppingToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(Plugin.Instance, Owner)) throw new InvalidOperationException("Scoped fingerprint plugin owner changed.");
        }
    }
    public sealed record ScopeRunInfo(long TargetId, string State, IReadOnlyList<long> ItemIds);
    private ScopeRunInfo? lastScope;
    /// <summary>Last scoped invocation only; immutable, diagnostic and never persisted or accepted from a client.</summary>
    public ScopeRunInfo? LastScope => Volatile.Read(ref lastScope);
    public sealed record RebuildRunInfo(long TargetId, string State, string? Error);
    private RebuildRunInfo? lastRebuild;
    public RebuildRunInfo? LastRebuild => Volatile.Read(ref lastRebuild);
    public sealed record RunInfo(string State, string Summary);
    public sealed record SeasonRunInfo(long SeasonId, string State, int Selected, int Failed, int Skipped,
        int Generated, int Existing, int Committed);
    public sealed record IncrementalBatchInfo(string State, IReadOnlyList<SeasonRunInfo> Seasons)
    {
        public bool Parallel { get; init; }
        public int MaxConcurrentSeasons { get; init; } = 1;
        // Dispatcher activity includes waiting on shared quotas; these are NOT native/FFmpeg overlap counts.
        public int ActiveSeasons { get; init; }
        public int PeakActiveSeasons { get; init; }
        public string Summary => $"IncrementalSeasons={Seasons.Count}; Selected={Seasons.Sum(s => s.Selected)}; " +
            $"Failed={Seasons.Sum(s => s.Failed)}; Skipped={Seasons.Sum(s => s.Skipped)}; " +
            $"Generated={Seasons.Sum(s => s.Generated)}; Existing={Seasons.Sum(s => s.Existing)}; Committed={Seasons.Sum(s => s.Committed)}";
    }
    private RunInfo? lastResult;
    private IncrementalBatchInfo? lastIncrementalBatch;
    /// <summary>Ordinary task diagnostics only. Counts contain no paths, provider messages or source tokens.</summary>
    public RunInfo? LastResult => Volatile.Read(ref lastResult);
    public IncrementalBatchInfo? LastIncrementalBatch => Volatile.Read(ref lastIncrementalBatch);

    // Only the server bridge supplies this actor/root/epoch-bound M103 closure. No serialized field selects it.
    internal TaskOptions CreateRebuildOptions(long targetId, Func<IProgress<double>, CancellationToken, Task> rebuild,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(rebuild);
        token.ThrowIfCancellationRequested();
        if (targetId <= 0) throw new ArgumentOutOfRangeException(nameof(targetId));
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        plugin.WorkQueue.StoppingToken.ThrowIfCancellationRequested();
        var options = new TaskOptions();
        bindings.Add(options, new BoundScope(targetId, rebuild, plugin, this));
        return options;
    }

    /// <summary>Server-only: pass this exact native object to ITaskManager. Serialization/cloning loses the binding;
    /// an unbound native TaskOptions remains an ordinary full task. No public DTO fields encode scope.</summary>
    public TaskOptions CreateScopedOptions(long targetId, CancellationToken token)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        var scope = ScopedFingerprintSelection.Capture(library, targetId, plugin.IntroStore.GetOptions(), token);
        var options = new TaskOptions();
        plugin.WorkQueue.StoppingToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(Plugin.Instance, plugin)) throw new InvalidOperationException("Scoped fingerprint plugin owner changed during capture.");
        bindings.Add(options, new BoundScope(targetId, scope, plugin, this));
        return options;
    }

    public Task Execute(TaskOptions options, IProgress<double> progress, CancellationToken token) =>
        ExecuteCore(options != null && bindings.TryGetValue(options, out var bound) ? bound : null, token, progress);
    public Task Execute(CancellationToken token, IProgress<double> progress) => ExecuteCore(null, token, progress);

    private async Task ExecuteCore(BoundScope? bound, CancellationToken token, IProgress<double> progress)
    {
        // Claim first, even when admission, cancellation or validation fails. Keep the CWT marker forever.
        bound?.Claim(this);
        var owner = bound?.Owner ?? Plugin.Instance;
        using var stopping = owner == null ? null : CancellationTokenSource.CreateLinkedTokenSource(token, owner.WorkQueue.StoppingToken);
        token = stopping?.Token ?? token;
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("Fingerprint task is already running.");
        var selectedIds = Array.AsReadOnly(Array.Empty<long>());
        if (bound?.Rebuild != null) Volatile.Write(ref lastRebuild, new(bound.TargetId, "Running", null));
        else if (bound != null) Volatile.Write(ref lastScope, new(bound.TargetId, "Running", selectedIds));
        else
        {
            Volatile.Write(ref lastIncrementalBatch, null);
            Volatile.Write(ref lastResult, new("Running", "Scraped=0; IncrementalSeasons=0"));
        }
        var imported = 0;
        void Result(string state)
        {
            if (bound != null) return;
            if (LastIncrementalBatch is { } batch)
                Volatile.Write(ref lastIncrementalBatch, batch with { State = state });
            Volatile.Write(ref lastResult, new(state, $"Scraped={imported}; " +
                (LastIncrementalBatch?.Summary ?? "IncrementalSeasons=0")));
        }
        try
        {
            token.ThrowIfCancellationRequested();
            bound?.RequireOwner(token);
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
            if (bound?.Rebuild != null)
            {
                await bound.Rebuild(progress, token).ConfigureAwait(false);
                bound.RequireOwner(token);
                Volatile.Write(ref lastRebuild, new(bound.TargetId, "Completed", null));
                return; // Never enter native fingerprint-only or global scraping selection.
            }
            var chapters = plugin.ChapterApi ?? throw new InvalidOperationException("Chapter service not initialized.");
            var options = plugin.IntroStore.GetOptions();
            ScopeSelection.ValidateMarkerScope(options.MarkerEnabledLibraryScope);
            var scope = bound?.Scope?.Select(options, token);
            var scopedEpisodes = scope?.Episodes(options, token);
            if (bound != null)
            {
                selectedIds = Array.AsReadOnly(scopedEpisodes!.Select(e => e.InternalId).ToArray());
                Volatile.Write(ref lastScope, new(bound.TargetId, "Selected", selectedIds));
            }
            Action<BaseItem, CancellationToken>? guard = scope == null ? null : (item, ct) =>
            {
                bound!.RequireOwner(ct);
                scope.Guard(item, plugin.IntroStore.GetOptions(), ct);
                bound.RequireOwner(ct);
            };
            Action<Season, IReadOnlyList<Episode>, CancellationToken>? batchGuard = scope == null ? null : (season, items, ct) =>
            {
                bound!.RequireOwner(ct);
                scope.GuardBatch(season, items, plugin.IntroStore.GetOptions(), ct);
                bound.RequireOwner(ct);
            };
            if (scope == null)
            {
                var scraper = plugin.IntroScrapingService ?? throw new InvalidOperationException("Intro scraping service not initialized.");
                var all = Select(options, false, token);
                // HTTP runs under master quota without an item lock. Apply owns the tier2+item lock:
                // nesting the same item lock or the same tier would deadlock with a quota of one.
                await plugin.WorkQueue.RunBatch(all, async (item, ct) =>
                {
                    var existing = chapters.GetMarkers(item, ct);
                    if (existing.HasIntro && existing.HasCredits) return;
                    var markers = await scraper.GetMarkersAsync(IntroLookupRequest.FromItem(item), options.IntroSkipScrapeOptions, ct).ConfigureAwait(false);
                    if (markers == null) return;
                    await chapters.ApplyAsync(item, markers, false, ct, "IntroDb").ConfigureAwait(false);
                    Interlocked.Increment(ref imported);
                }, new ScaledProgress(progress, 0, 40), token).ConfigureAwait(false);
            }

            if (options.UnlockIntroSkip)
            {
                var fingerprint = plugin.FingerprintApi;
                if (fingerprint?.IsSupported != true)
                {
                    if (scope != null && scopedEpisodes!.Length != 0)
                        throw new NotSupportedException(fingerprint?.SupportDetail ?? "Official fingerprint service not initialized.");
                    plugin.Logger.Warn("[StrmAssistantRebuild] {0}", fingerprint?.SupportDetail ?? "NotSupported: official fingerprint service not initialized; scraping remains available.");
                }
                else
                {
                    var markerScope = options.MarkerEnabledLibraryScope;
                    var exclusions = options.FingerprintExcludeKeywords;
                    var exclude = ScopeSelection.Parse(exclusions);
                    var selected = scope == null ? Select(options, true, token).OfType<Episode>().ToArray() :
                        scopedEpisodes!.Where(e => !exclude.Any(k => e.Path.Contains(k, StringComparison.OrdinalIgnoreCase))).ToArray();
                    if (bound == null && selected.Any(IsStrm))
                    {
                        // As-of task-stage scheduling only; live shared queue limits may change independently.
                        var parallel = options.ParallelMarkerComputation;
                        await RunSelectedSeasons(plugin, fingerprint, chapters, selected, markerScope, exclusions, parallel, progress, token).ConfigureAwait(false);
                    }
                    else
                    {
                        var allowedItems = selected.Select(e => e.InternalId).ToHashSet();
                        var episodes = selected.Where(e => !chapters.GetMarkers(e, token).HasIntro).ToArray();
                        var eligible = new List<Episode>();
                        foreach (var episode in episodes)
                        {
                            token.ThrowIfCancellationRequested();
                            guard?.Invoke(episode, token);
                            if (fingerprint.IsEpisodeSupported(episode)) eligible.Add(episode);
                            else plugin.Logger.Info("[StrmAssistantRebuild] NotSupported: native fingerprint eligibility failed for item {0}; no marker synthesized.", episode.InternalId);
                        }
                        if (eligible.Count == 0) plugin.Logger.Warn("[StrmAssistantRebuild] No eligible native fingerprint episodes; no fingerprint or detected marker was produced.");
                        var batch = await plugin.WorkQueue.RunBatch(eligible, async (episode, ct) =>
                        {
                            using var owned = ExclusiveMediaControl.EnterOwnedOperation();
                            await fingerprint.CreateTitleFingerprint(episode, ct, guard).ConfigureAwait(false);
                        }, new ScaledProgress(progress, scope == null ? 40 : 0, scope == null ? 40 : 80), token, itemKey: e => e.InternalId).ConfigureAwait(false);
                        if (scope != null && batch.Failed != 0) throw new InvalidOperationException("Scoped fingerprint extraction had failed items; selection is not a successful completion.");
                        var seasons = eligible.Select(e => e.Season).Where(s => s != null).DistinctBy(s => s.InternalId).ToArray();
                        var completed = 0;
                        var progressGate = new object();
                        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
                        await Parallel.ForEachAsync(seasons, new ParallelOptions
                        {
                            CancellationToken = token,
                            MaxDegreeOfParallelism = options.ParallelMarkerComputation ? Math.Clamp(plugin.ConfigurationStore.GetOptions().GeneralOptions.Tier2MaxConcurrentCount, 1, 20) : 1
                        }, async (season, ct) =>
                        {
                            try
                            {
                                using var owned = ExclusiveMediaControl.EnterOwnedOperation();
                                await fingerprint.UpdateIntroMarkerForSeason(season, ct, allowedItems: allowedItems,
                                    scopedEpisodes: scope == null ? null : selected.Where(e => e.InternalAlbumId == season.InternalId).ToArray(),
                                    scopeGuard: guard, scopeBatchGuard: batchGuard).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception ex)
                            {
                                failures.Enqueue(ex);
                                plugin.Logger.Warn("[StrmAssistantRebuild] Fingerprint season {0}: {1}", season.InternalId, ex.GetBaseException().Message);
                            }
                            lock (progressGate) progress.Report(80 + ++completed * 20d / seasons.Length);
                        }).ConfigureAwait(false);
                        if (!failures.IsEmpty) throw new AggregateException("Fingerprint season computation or persistence failed; see per-season log.", failures);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            plugin.Logger.Info("[StrmAssistantRebuild] Intro scraping applied {0} provider result(s); fingerprint execution is not a claim of a detected marker.", imported);
            if (scope != null) scope.Episodes(plugin.IntroStore.GetOptions(), token);
            progress.Report(100);
            token.ThrowIfCancellationRequested();
            bound?.RequireOwner(token);
            if (bound != null) Volatile.Write(ref lastScope, new(bound.TargetId, "Completed", selectedIds));
            Result("Completed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (bound?.Rebuild != null) Volatile.Write(ref lastRebuild, new(bound.TargetId, "Cancelled", "Cancelled"));
            else if (bound != null) Volatile.Write(ref lastScope, new(bound.TargetId, "Cancelled", selectedIds));
            Result("Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            if (bound?.Rebuild != null) Volatile.Write(ref lastRebuild, new(bound.TargetId, "Failed", ex.GetType().Name));
            else if (bound != null) Volatile.Write(ref lastScope, new(bound.TargetId, "Failed", selectedIds));
            Result("Failed");
            throw;
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private static (Type Type, long Id, Guid Guid, string Path, long Parent, long Series, long Album, int? Index, int? ParentIndex,
        DateTimeOffset Created, long? Runtime, string Container, bool Virtual, string External) ItemIdentity(BaseItem e) =>
        (e.GetType(), e.InternalId, e.Id, e.Path, e.ParentId, e.SeriesId, e.InternalAlbumId, e.IndexNumber, e.ParentIndexNumber,
            e.DateCreated, e.RunTimeTicks, e.Container, e.IsVirtualItem, e.ExternalId);

    private static bool IsStrm(Episode episode) => Path.GetExtension(episode.Path).Equals(".strm", StringComparison.OrdinalIgnoreCase);

    private async Task RunSelectedSeasons(Plugin plugin, FingerprintApi fingerprint, ChapterApi chapters,
        Episode[] selected, string markerScope, string exclusions, bool parallel, IProgress<double> progress, CancellationToken token)
    {
        // Freeze all identities before any season callback can mutate a later season's original objects.
        var identities = selected.Select(e => (Episode: e, Identity: ItemIdentity(e))).ToArray();
        // Freeze the original server selection AFTER scraping/exclusions. Never re-select a season's children.
        var groups = selected.GroupBy(e => e.InternalAlbumId)
            .Select(g => (Id: g.Key, Episodes: g.ToArray(), AllowedIds: g.Select(e => e.InternalId).ToHashSet())).ToArray();
        var incremental = groups.Where(g => g.Episodes.Any(IsStrm)).Select(g => g.Id).ToHashSet();
        var receipts = groups.Select(g => incremental.Contains(g.Id) ?
            new SeasonRunInfo(g.Id, "Pending", g.Episodes.Length, 0, 0, 0, 0, 0) : null).ToArray();
        // Read host configuration only after freezing identities/groups; Authority catches callback-induced drift.
        var degree = parallel ? Math.Clamp(plugin.ConfigurationStore.GetOptions().GeneralOptions.Tier2MaxConcurrentCount, 1, 20) : 1;
        var percentages = new double[groups.Length];
        var gate = new object();
        var active = 0;
        var peak = 0;
        // Called only under gate. Copy in original group order; published records/arrays never mutate.
        void Publish() => Volatile.Write(ref lastIncrementalBatch,
            new("Running", Array.AsReadOnly(receipts.OfType<SeasonRunInfo>().ToArray()))
            { Parallel = parallel, MaxConcurrentSeasons = degree, ActiveSeasons = active, PeakActiveSeasons = peak });
        void Authority(CancellationToken authorityToken)
        {
            authorityToken.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            var current = plugin.IntroStore.GetOptions();
            if (!ReferenceEquals(plugin, Plugin.Instance) || !ReferenceEquals(fingerprint, plugin.FingerprintApi) ||
                !current.UnlockIntroSkip || current.MarkerEnabledLibraryScope != markerScope || current.FingerprintExcludeKeywords != exclusions ||
                identities.Any(i => ItemIdentity(i.Episode) != i.Identity))
                throw new InvalidOperationException("Ordinary fingerprint task selection authority changed.");
        }
        lock (gate) Publish();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        // Core owns generation/detection/writer leases. Wrapping a whole season in RunSingle would deadlock at quota 1.
        await Parallel.ForEachAsync(Enumerable.Range(0, groups.Length), new ParallelOptions
        { CancellationToken = token, MaxDegreeOfParallelism = degree }, async (index, seasonToken) =>
        {
            var group = groups[index];
            var isIncremental = incremental.Contains(group.Id);
            IntroRebuildReport? report = null;
            var seasonProgress = new SeriesProgress(value =>
            {
                if (!double.IsFinite(value)) return;
                lock (gate)
                {
                    percentages[index] = Math.Max(percentages[index], Math.Clamp(value, 0, 100));
                    progress.Report(40 + percentages.Average() * .6);
                }
            });
            lock (gate)
            {
                peak = Math.Max(peak, ++active);
                if (isIncremental) receipts[index] = receipts[index]! with { State = "Running" };
                Publish();
            }
            try
            {
                Authority(seasonToken);
                var season = library.GetItemById(group.Id) as Season ??
                    throw new InvalidOperationException("Selected physical season is missing.");
                using var owned = ExclusiveMediaControl.EnterOwnedOperation();
                if (isIncremental)
                {
                    // Including marked references and physical peers is essential. This season NEVER enters native Update.
                    report = await fingerprint.ProcessIntroForSelectedSeasonAsync(season, group.Episodes, seasonProgress, seasonToken, Authority).ConfigureAwait(false);
                    lock (gate) receipts[index] = IncrementalReceipt(group.Id, group.Episodes.Length, report);
                    if (report.State != "Completed" || receipts[index]!.Failed != 0)
                        throw new InvalidOperationException("Incremental season completed with failed items.");
                }
                else
                {
                    var seasonIdentity = ItemIdentity(season);
                    void NativeAuthority(CancellationToken ct)
                    {
                        Authority(ct);
                        if (ItemIdentity(season) != seasonIdentity)
                            throw new InvalidOperationException("Selected native season identity changed.");
                    }
                    var eligible = group.Episodes.Where(e => !chapters.GetMarkers(e, seasonToken).HasIntro && fingerprint.IsEpisodeSupported(e)).ToArray();
                    var batch = await plugin.WorkQueue.RunBatch(eligible, async (episode, ct) =>
                    {
                        using var operation = ExclusiveMediaControl.EnterOwnedOperation();
                        await fingerprint.CreateTitleFingerprint(episode, ct, (_, t) => NativeAuthority(t)).ConfigureAwait(false);
                    }, new ScaledProgress(seasonProgress, 0, 200d / 3), seasonToken, itemKey: e => e.InternalId).ConfigureAwait(false);
                    if (batch.Failed != 0) throw new InvalidOperationException("Native season fingerprint extraction had failed items.");
                    if (eligible.Length != 0)
                        await fingerprint.UpdateIntroMarkerForSeason(season, seasonToken, new ScaledProgress(seasonProgress, 200d / 3, 100d / 3),
                            allowedItems: group.AllowedIds, scopedEpisodes: group.Episodes,
                            scopeGuard: (_, t) => NativeAuthority(t), scopeBatchGuard: (_, _, t) => NativeAuthority(t)).ConfigureAwait(false);
                }
                seasonProgress.Report(100);
                Authority(seasonToken);
            }
            catch (OperationCanceledException ex) when (seasonToken.IsCancellationRequested)
            {
                report ??= ex.Data["IntroRebuildReport"] as IntroRebuildReport;
                lock (gate)
                    if (isIncremental) receipts[index] = (report == null ? receipts[index]! :
                        IncrementalReceipt(group.Id, group.Episodes.Length, report)) with { State = "Cancelled" };
                throw;
            }
            catch (Exception ex)
            {
                report ??= ex.Data["IntroRebuildReport"] as IntroRebuildReport;
                lock (gate)
                {
                    percentages[index] = 100; // Count settled failure without re-invoking a possibly throwing observer.
                    if (isIncremental) receipts[index] = report == null ? receipts[index]! with { State = "Failed", Failed = group.Episodes.Length } :
                        IncrementalReceipt(group.Id, group.Episodes.Length, report) with { State = "Failed" };
                }
                failures.Enqueue(ex);
                plugin.Logger.Warn("[StrmAssistantRebuild] Fingerprint season {0} failed: {1}", group.Id, ex.GetType().Name);
            }
            finally { lock (gate) { active--; Publish(); } }
        }).ConfigureAwait(false);
        // Only ExecuteCore settles the batch, after every started season has drained (including cancellation).
        if (!failures.IsEmpty) throw new AggregateException("Fingerprint seasons completed with failures; see per-season diagnostics.", failures);
    }

    private static SeasonRunInfo IncrementalReceipt(long seasonId, int selected, IntroRebuildReport report)
    {
        var preparation = report.Preparation;
        var applied = report.ApplyReport?.Items;
        var failed = preparation.Where(i => i.Error != null || i.StageCleanup?.StartsWith("Retained", StringComparison.Ordinal) == true).Select(i => i.ItemId)
            .Concat(applied?.Where(i => i.Status is "CalculationFailed" or "CommitFailed").Select(i => i.ItemId) ?? Array.Empty<long>()).Distinct().Count();
        var skipped = preparation.Where(i => i.Status is "AlreadyMarked" or "ReferenceOnly").Select(i => i.ItemId)
            .Concat(applied?.Where(i => i.Status is "AlreadyMarked" or "NoMatch").Select(i => i.ItemId) ?? Array.Empty<long>()).Distinct().Count();
        // Generated is native Task completion, not publication or a detected intro. Committed requires the DB receipt.
        return new(seasonId, report.State, selected, failed, skipped,
            preparation.Count(i => i.Created == true), preparation.Count(i => i.Status is "ExistingFingerprint" or "ReferenceOnly"),
            applied?.Count(i => i.CommitReport?.Committed == true) ?? 0);
    }

    private BaseItem[] Select(IntroSkipOptions options, bool fingerprint, CancellationToken token)
    {
        var exclude = ScopeSelection.Parse(options.FingerprintExcludeKeywords);
        var items = ScopeSelection.SelectForServer(library,
            fingerprint ? options.MarkerEnabledLibraryScope : options.LibraryScope,
            fingerprint ? LibraryScopeKind.Marker : LibraryScopeKind.Intro, token);
        return items.Where(item => !fingerprint || !exclude.Any(k => item.Path.Contains(k, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
    private sealed class SeriesProgress : IProgress<double>
    {
        private readonly Action<double> report;
        public SeriesProgress(Action<double> report) => this.report = report;
        public void Report(double value) => report(value);
    }
    private sealed class ScaledProgress : IProgress<double>
    {
        private readonly IProgress<double> progress;
        private readonly double start, weight;
        public ScaledProgress(IProgress<double> progress, double start, double weight) { this.progress = progress; this.start = start; this.weight = weight; }
        public void Report(double value) => progress.Report(start + value * weight / 100);
    }
}
