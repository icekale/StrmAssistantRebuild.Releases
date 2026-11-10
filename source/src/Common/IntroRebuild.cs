using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace StrmAssistantRebuild.Common;

public sealed record IntroPreparationItem(long ItemId, string Status, FingerprintResetReport? ResetReport,
    string? GeneratedPath, bool? Created, string? Error, bool GenerationStarted = false,
    string? ExpectedPath = null, bool? OutputExistsAfterError = null, string? StagedPath = null,
    bool Staged = false, bool Published = false, int? Frames = null, string? StageCleanup = null)
{
    // Created=true now means the output-path native Task completed, not the old reuse-aware tuple flag.
    public bool ReusedConcurrent => Created == false;
}
public sealed record IntroRebuildReport(long TargetId, string State, IReadOnlyList<IntroPreparationItem> Preparation,
    IntroApplyReport? ApplyReport)
{
    public string Mode { get; init; } = "Rebuild";
    public string BudgetState { get; init; } = "NotStarted";
    public long? PredictedWorkUnits { get; init; }
    public long? PredictedPeakCallWorkUnits { get; init; }
    public long? ActualInputWorkUnits => ApplyReport?.CalculationReport?.WorkUnits;
    public string WorkUnit => "directed frame pairs from complete staged/actual inputs; estimate, not elapsed time";
    public int MinFrames => NativeIntroDetection.MinFrames;
    public int MaxFrames => NativeIntroDetection.MaxFrames;
    public int MaxSeasonEpisodes => NativeIntroDetection.MaxSeasonEpisodes;
    public int MaxTargetEpisodes => NativeIntroDetection.MaxTargetEpisodes;
    public long MaxCallWorkUnits => NativeIntroDetection.MaxCallWork;
    public long MaxSeasonWorkUnits => NativeIntroDetection.MaxSeasonWork;
    public long MaxTargetWorkUnits => NativeIntroDetection.MaxTargetWork;
}

public sealed partial class FingerprintApi
{
    /// <summary>Server-only staged rebuild on the ORIGINAL physical selection. Generate all eligible inputs
    /// non-destructively, validate complete bytes and every call/season/target budget BEFORE any Reset.
    /// Stage failures preserve that item's old state; groups without two valid stages remain untouched.
    /// Global budget rejection preserves every old fingerprint, failure/cache and chapter. No frame-rate guess.
    /// Progress: staging 0..29, exactly one admitted boundary 30, Reset/publish 30..40, calculation 40..69,
    /// exactly one capture/apply boundary 70, application 70..100. M103 intentionally changes M102 preparation order.
    /// Only successful per-item intro transactions replace chapters; this is not preclear/full-Replace parity.
    /// Created=true records completion of the native output-path Task; GeneratedPath is its supplied stage path,
    /// ExpectedPath is canonical, Published records the later no-overwrite move. Owned stages are cleaned up;
    /// Reset backups and published files remain. Native Create observes its own cancellation boundaries.
    /// Exceptions carry Data["IntroRebuildReport"] with immutable preparation, budget and committed receipts.</summary>
    public Task<IntroRebuildReport> RebuildIntroForTargetAsync(long targetId, IProgress<double>? progress,
        CancellationToken token) => RebuildIntroCoreAsync(targetId, progress, token, null, null);

    // HTTP refresh retains only the pre-metadata root/owner chain; Select still runs once after metadata.
    // Authority is as-of each phase guard, not atomic with native DB/user-policy changes.
    internal Task<IntroRebuildReport> RebuildIntroForRefreshAsync(long targetId, IProgress<double>? progress,
        CancellationToken token, ScopedFingerprintSelection fixedScope, Action<CancellationToken> externalAuthority)
    {
        ArgumentNullException.ThrowIfNull(fixedScope);
        ArgumentNullException.ThrowIfNull(externalAuthority);
        return RebuildIntroCoreAsync(targetId, progress, token, fixedScope, externalAuthority);
    }

    /// <summary>Ordinary task only: exact original season selection, reuse valid canonical files, generate missing only.
    /// Existing intro episodes supply read-only fingerprints; no Reset or chapter replacement in this mode.</summary>
    internal Task<IntroRebuildReport> ProcessIntroForSelectedSeasonAsync(Season season, IReadOnlyList<Episode> selection,
        IProgress<double>? progress, CancellationToken token, Action<CancellationToken>? taskAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(season); ArgumentNullException.ThrowIfNull(selection);
        return RebuildIntroCoreAsync(season.InternalId, progress, token, null, taskAuthority, season, selection.ToArray());
    }

    private async Task<IntroRebuildReport> RebuildIntroCoreAsync(long targetId, IProgress<double>? progress,
        CancellationToken token, ScopedFingerprintSelection? fixedScope, Action<CancellationToken>? externalAuthority,
        Season? incrementalSeason = null, IReadOnlyList<Episode>? originalEpisodes = null)
    {
        var incremental = incrementalSeason != null;
        var targets = incremental ? new HashSet<long>() : null;
        var preparation = new List<IntroPreparationItem>();
        IntroApplyReport? apply = null;
        var budgetState = "NotStarted";
        long? predictedWork = null, predictedPeak = null;
        var ownedStages = new Dictionary<string, (byte[] Bytes, long Length, DateTime Modified, DateTime Created)>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, queue.StoppingToken);
        token = linked.Token;
        var last = 0d;
        void Progress(double value) { last = Math.Max(last, Math.Clamp(value, 0, 100)); progress?.Report(last); }
        IntroRebuildReport Report(string state) => new(targetId, state, Array.AsReadOnly(preparation.ToArray()), apply)
            { Mode = incremental ? "Incremental" : "Rebuild", BudgetState = budgetState, PredictedWorkUnits = predictedWork, PredictedPeakCallWorkUnits = predictedPeak };
        try
        {
            if (incremental)
            {
                token.ThrowIfCancellationRequested();
                externalAuthority?.Invoke(token); token.ThrowIfCancellationRequested();
                if (originalEpisodes!.Count > NativeIntroDetection.MaxSeasonEpisodes)
                { budgetState = "Rejected"; throw new InvalidOperationException("Selected season exceeds the 32-episode budget."); }
                if (originalEpisodes.Count == 0) { Progress(100); token.ThrowIfCancellationRequested(); return Report("Completed"); }
                fixedScope = ScopedFingerprintSelection.CaptureSelectedSeason(library, incrementalSeason!, originalEpisodes,
                    (Plugin.Instance ?? throw new InvalidOperationException("Intro owner missing.")).IntroStore.GetOptions(), token);
            }
            apply = await CalculateAndApplyIntroCoreAsync(targetId, new IntroApplyProgress(Progress), token,
                Prepare, fixedScope, externalAuthority, originalEpisodes, targets).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            CleanupStages();
            return Report(apply.State == "CompletedWithFailures" || preparation.Any(i => i.Status is not ("Prepared" or "ExistingFingerprint" or "ReferenceOnly" or "AlreadyMarked") || i.StageCleanup?.StartsWith("Retained", StringComparison.Ordinal) == true)
                ? "CompletedWithFailures" : "Completed");
        }
        catch (Exception ex)
        {
            apply ??= ex.Data["IntroApplyReport"] as IntroApplyReport;
            if (apply?.CalculationReport?.BudgetState == "Rejected") budgetState = "Rejected";
            CleanupStages();
            ex.Data["IntroRebuildReport"] = Report(token.IsCancellationRequested || ex is OperationCanceledException ? "Cancelled" : "Failed");
            throw;
        }
        (byte[] Bytes, long Length, DateTime Modified, DateTime Created) StageSnapshot(string stage)
        {
            var info = new FileInfo(paths.RequireWritablePath(stage));
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Stage is not an existing regular output.");
            var length = info.Length; var modified = info.LastWriteTimeUtc; var created = info.CreationTimeUtc;
            if (length > NativeIntroDetection.MaxFrames * 4L) throw new InvalidDataException("Stage snapshot exceeds bounded read.");
            var bytes = Array.Empty<byte>();
            // Stat zero-byte reservations without opening: a substituted FIFO must not block this guard/cleanup.
            if (length != 0)
            {
                using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != length) throw new IOException("Stage size changed before read.");
                using var reader = new BinaryReader(stream);
                bytes = reader.ReadBytes((int)length);
                if (bytes.Length != length || stream.ReadByte() != -1) throw new IOException("Stage changed during bounded read.");
            }
            var final = new FileInfo(paths.RequireWritablePath(stage));
            if (!final.Exists || final.Length != length || final.LastWriteTimeUtc != modified || final.CreationTimeUtc != created)
                throw new IOException("Stage metadata changed during snapshot.");
            return (bytes, length, modified, created);
        }
        bool StageMatches(string stage, (byte[] Bytes, long Length, DateTime Modified, DateTime Created) expected)
        {
            var actual = StageSnapshot(stage);
            return actual.Length == expected.Length && actual.Modified == expected.Modified && actual.Created == expected.Created &&
                actual.Bytes.SequenceEqual(expected.Bytes);
        }
        void CleanupStages()
        {
            // Only a matching reserved/returned snapshot may be deleted. Non-cooperating namespace races are
            // not an inode/exclusivity guarantee; changed, linked, oversized and unknown native outputs remain.
            foreach (var entry in ownedStages)
            {
                var index = preparation.FindIndex(i => i.StagedPath == entry.Key);
                var outcome = "RetainedChangedOrUnknown";
                try
                {
                    paths.RequireWritablePath(entry.Key);
                    if (!File.Exists(entry.Key)) outcome = preparation[index].Published ? "Published" : "Absent";
                    else if (StageMatches(entry.Key, entry.Value))
                    { File.Delete(paths.RequireWritablePath(entry.Key)); outcome = "Removed"; }
                    if (outcome is "Removed" or "Published" or "Absent")
                        Directory.Delete(paths.RequireWritablePath(Path.GetDirectoryName(entry.Key)!));
                }
                catch { outcome = "RetainedIOOrUnknown"; }
                if (index >= 0) preparation[index] = preparation[index] with { StageCleanup = outcome };
            }
        }

        async Task<IReadOnlyList<NativeIntroDetection.Input>> Prepare(ScopedFingerprintSelection selected,
            IReadOnlyList<Episode> episodes, string root, Action<CancellationToken> authority, CancellationToken ct)
        {
            var owner = Plugin.Instance ?? throw new InvalidOperationException("Intro rebuild owner missing.");
            var configuration = BaseItem.ConfigurationManager;
            var repository = BaseItem.ItemRepository;
            var databasePath = paths.RequireWritablePath(Path.Combine(configuration.ApplicationPaths.DataPath, "library.db"));
            var api = new NativeUserDataCopy.Api(repository);
            var pending = new Dictionary<long, (NativeIntroDetection.Input Input, Season Season, string Key)>();
            var ready = new List<NativeIntroDetection.Input>();
            foreach (var episode in episodes) preparation.Add(new(episode.InternalId, "Pending", null, null, null, null));
            if (incremental)
            {
                foreach (var episode in episodes) if (!HasAnyIntro(episode, ct)) targets!.Add(episode.InternalId);
                selected.GuardBatch(incrementalSeason!, episodes, owner.IntroStore.GetOptions(), ct);
                Authority(ct);
                if (targets!.Count == 0)
                {
                    for (var n = 0; n < preparation.Count; n++) preparation[n] = preparation[n] with { Status = "AlreadyMarked" };
                    budgetState = "Admitted"; predictedWork = predictedPeak = 0; Progress(40);
                    return Array.Empty<NativeIntroDetection.Input>();
                }
            }
            budgetState = "Staging";
            var staged = new Dictionary<long, NativeIntroDetection.Input>();
            // Capture every candidate before generating. Actual bytes, never minutes-to-frames guesses, admit work.
            for (var n = 0; n < episodes.Count; n++)
            {
                var episode = episodes[n];
                Authority(ct);
                try
                {
                    if (incremental) selected.Guard(episode, owner.IntroStore.GetOptions(), ct);
                    var source = IsStrm(episode) ? CaptureStrmSource(episode, ct) : null;
                    if (episode.IsVirtualItem || source == null && !IsEpisodeSupported(episode) || !File.Exists(episode.Path))
                        throw new NotSupportedException("Native episode eligibility failed.");
                    var season = library.GetItemById(episode.InternalAlbumId) as Season ?? throw new IntroApplyAuthorityChangedException();
                    var input = NativeIntroDetection.Input.Capture(episode, root, Minutes(episode)) with { Source = source };
                    pending.Add(input.Id, (input, season, season.PresentationUniqueKey));
                    preparation[n] = preparation[n] with { ExpectedPath = input.File };
                    Guard(input, ct);
                    if (incremental)
                    {
                        if (File.Exists(input.File) || Directory.Exists(input.File))
                        {
                            input.Read(paths, ct); Guard(input, ct); input.ValidateFile(paths, ct);
                            staged.Add(input.Id, input);
                            preparation[n] = preparation[n] with { Status = targets!.Contains(input.Id) ? "ExistingFingerprint" : "ReferenceOnly", Frames = input.Frames.Count };
                        }
                        else if (!targets!.Contains(input.Id))
                            preparation[n] = preparation[n] with { Status = "AlreadyMarked" };
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (IntroApplyAuthorityChangedException) { throw; }
                catch (Exception ex)
                {
                    pending.Remove(episode.InternalId);
                    preparation[n] = preparation[n] with { Status = incremental && preparation[n].ExpectedPath != null ? "ExistingFingerprintFailed" : "Ineligible", Error = PreparationError(ex) };
                    Authority(ct);
                }
            }
            foreach (var candidate in pending.Values) Guard(candidate.Input, ct);
            for (var n = 0; n < episodes.Count; n++)
            {
                var index = n;
                if (pending.TryGetValue(episodes[n].InternalId, out var candidate) && (!incremental || targets!.Contains(candidate.Input.Id) && !staged.ContainsKey(candidate.Input.Id)))
                {
                    var input = candidate.Input;
                    string? stage = null;
                    try
                    {
                        await queue.RunSingle(input.Id, async writeToken =>
                        {
                            Guard(input, writeToken);
                            if (incremental)
                            {
                                var marked = HasAnyIntro(input.Episode, writeToken);
                                Guard(input, writeToken);
                                if (marked)
                                {
                                    targets!.Remove(input.Id);
                                    preparation[index] = preparation[index] with { Status = "AlreadyMarked" };
                                    return true;
                                }
                            }
                            var options = library.GetLibraryOptions(input.Episode);
                            RequireWritePaths(input.Episode, options);
                            Guard(input, writeToken);
                            var directory = paths.RequireWritablePath(Path.Combine(Path.GetDirectoryName(input.File)!,
                                ".intro-stage-" + Guid.NewGuid().ToString("N")));
                            if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Stage directory already exists.");
                            Directory.CreateDirectory(directory);
                            stage = paths.RequireWritablePath(Path.Combine(directory, Path.GetFileName(input.File) + ".tmp"));
                            using (new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                            ownedStages.Add(stage, StageSnapshot(stage));
                            preparation[index] = preparation[index] with { Status = "Staging", StagedPath = stage };
                            Guard(input, writeToken);
                            var retainedMinutes = options.IntroDetectionFingerprintLength;
                            if (retainedMinutes < 2 || retainedMinutes >= 30) retainedMinutes = 10;
                            if (input.Duration != TimeSpan.FromMinutes(retainedMinutes) || !StageMatches(stage, ownedStages[stage]))
                                throw new IOException("Reserved stage or native length changed before creation.");
                            // The signed branch repeats this proof after its metadata sink callback.
                            preparation[index] = preparation[index] with { Status = "Generating", GenerationStarted = true };
                            await CreateStagedAsync(input, options, stage, writeToken, () => {
                                Guard(input, writeToken);
                                var currentMinutes = options.IntroDetectionFingerprintLength;
                                if (currentMinutes < 2 || currentMinutes >= 30) currentMinutes = 10;
                                if (input.Duration != TimeSpan.FromMinutes(currentMinutes) || !StageMatches(stage, ownedStages[stage]))
                                    throw new IOException("Reserved stage or native length changed before signed creation.");
                            }, () => {
                                preparation[index] = preparation[index] with { GeneratedPath = stage, Created = true };
                                ownedStages[stage] = StageSnapshot(stage); // Freeze native bytes BEFORE the signed completion log callback.
                            }).ConfigureAwait(false);
                            if (input.Source?.SignedQuery != true)
                            {
                                preparation[index] = preparation[index] with { GeneratedPath = stage, Created = true };
                                ownedStages[stage] = StageSnapshot(stage); // Plain/local retains its original post-native sequence.
                            }
                            Guard(input, writeToken); // Record Task completion before observing cancellation/authority drift.
                            if (!StageMatches(stage, ownedStages[stage])) throw new IOException("Returned stage changed before capture.");
                            var output = input with { File = stage };
                            output.Read(paths, writeToken);
                            Guard(input, writeToken); output.ValidateFile(paths, writeToken);
                            staged.Add(input.Id, output);
                            preparation[index] = preparation[index] with { Status = "Staged", Staged = true, Frames = output.Frames.Count };
                            return true;
                        }, ct, secondTier: false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        bool? exists = null;
                        if (stage != null) try { exists = File.Exists(paths.RequireWritablePath(stage)); } catch { }
                        var completed = input.Source?.SignedQuery == true && ex.Data["NativeSignedGenerationCompleted"] is true;
                        preparation[index] = preparation[index] with { Status = "StageFailed", OutputExistsAfterError = exists,
                            Created = completed ? true : preparation[index].Created,
                            GeneratedPath = completed ? stage : preparation[index].GeneratedPath,
                            Error = ct.IsCancellationRequested || ex is OperationCanceledException ? "Cancelled" :
                                IsAuthority(ex) ? "AuthorityChanged" : PreparationError(ex) };
                        if (ct.IsCancellationRequested || ex is OperationCanceledException || IsAuthority(ex)) throw;
                        Guard(input, ct); // Local extraction/format failures retain old state, other valid pairs may proceed.
                    }
                }
                Progress((n + 1) * 29d / Math.Max(1, episodes.Count)); Authority(ct);
            }
            // A lone valid stage has no comparison peer: preserve its old state rather than Reset for no result.
            var paired = staged.Values.GroupBy(i => pending[i.Id].Season.InternalId)
                .Where(g => g.Count() >= 2).SelectMany(g => g).Select(i => i.Id).ToHashSet();
            foreach (var input in staged.Values.Where(i => !paired.Contains(i.Id) && (!incremental || targets!.Contains(i.Id))))
            {
                var index = preparation.FindIndex(i => i.ItemId == input.Id);
                preparation[index] = preparation[index] with { Status = "InsufficientFingerprints", Error = "InsufficientFingerprints" };
            }
            foreach (var input in staged.Values) { Guard(pending[input.Id].Input, ct); input.ValidateFile(paths, ct); }
            try
            {
                var estimates = staged.Values.Where(i => paired.Contains(i.Id)).GroupBy(i => pending[i.Id].Season.InternalId)
                    .Select(g => NativeIntroDetection.MeasureWork(g.Select(i => i.Frames.Count).ToArray(),
                        incremental ? g.Select(i => targets!.Contains(i.Id)).ToArray() : null)).ToArray();
                predictedWork = estimates.Sum(e => e.Total); predictedPeak = estimates.Select(e => e.PeakCall).DefaultIfEmpty(0).Max();
                foreach (var estimate in estimates) NativeIntroDetection.RequireWork(estimate.Total, estimate.PeakCall, predictedWork.Value);
                budgetState = "Admitted";
            }
            catch { budgetState = "Rejected"; throw; }
            Progress(30); Authority(ct); // Explicit once-only admission witness; all old files/cache/chapters remain intact.
            foreach (var input in staged.Values) Guard(pending[input.Id].Input, ct);
            foreach (var input in staged.Values) input.ValidateFile(paths, ct);
            for (var n = 0; n < episodes.Count; n++)
            {
                var index = n;
                if (!paired.Contains(episodes[n].InternalId))
                { Progress(30 + (n + 1) * 10d / Math.Max(1, episodes.Count)); Authority(ct); continue; }
                var candidate = pending[episodes[n].InternalId];
                var input = candidate.Input;
                var output = staged[input.Id];
                if (incremental && output.File == input.File)
                {
                    Guard(input, ct); output.ValidateFile(paths, ct); ready.Add(output);
                    Progress(30 + (n + 1) * 10d / Math.Max(1, episodes.Count)); Authority(ct); continue;
                }
                try
                {
                    Guard(input, ct); output.ValidateFile(paths, ct);
                    if (!incremental)
                    {
                        preparation[index] = preparation[index] with { Status = "Resetting" };
                        // Reset belongs exclusively to the explicit rebuild; ordinary tasks never enter it.
                        var reset = await ResetEpisodeFingerprintStateAsync(input.Episode, ct, (_, t) =>
                        { Guard(input, t); output.ValidateFile(paths, t); }).ConfigureAwait(false);
                        preparation[index] = preparation[index] with { Status = "Reset", ResetReport = reset };
                    }
                    Progress(30 + (n + .5) * 10d / Math.Max(1, episodes.Count));
                    Guard(input, ct);
                    await queue.RunSingle(input.Id, writeToken =>
                    {
                        Guard(input, writeToken); output.ValidateFile(paths, writeToken);
                        paths.RequireWritablePath(input.File);
                        if (!StageMatches(output.File, ownedStages[output.File])) throw new IOException("Admitted stage ownership proof changed before publish.");
                        // No host callback after proof; move without overwrite preserves a concurrent canonical writer.
                        File.Move(paths.RequireWritablePath(output.File), input.File);
                        preparation[index] = preparation[index] with { Published = true };
                        var published = output with { File = input.File }; // Retain the admitted frame snapshot, never rebind.
                        Guard(input, writeToken); published.ValidateFile(paths, writeToken);
                        preparation[index] = preparation[index] with { Status = "Prepared", Error = null };
                        ready.Add(published);
                        return Task.FromResult(true);
                    }, ct, secondTier: false).ConfigureAwait(false);
                    Guard(input, ct);
                }
                catch (Exception ex)
                {
                    var item = preparation[index];
                    var reset = ex.Data["FingerprintResetReport"] as FingerprintResetReport ?? item.ResetReport;
                    bool? outputExists = null;
                    // An existence observation, NOT ownership/validity proof; may belong to another writer.
                    // No host callbacks or unbounded reads on error, and never mask the original exception.
                    if (item.GenerationStarted)
                        try { outputExists = File.Exists(paths.RequireWritablePath(input.File)); } catch { }
                    preparation[index] = item with { ResetReport = reset, OutputExistsAfterError = outputExists,
                        Status = incremental ? "PublishFailed" : item.ResetReport == null ? "ResetFailed" : "PublishFailed",
                        Error = ct.IsCancellationRequested || ex is OperationCanceledException ? "Cancelled" :
                            IsAuthority(ex) ? "AuthorityChanged" :
                            item.Created == false ? "NewFingerprintNotConfirmed" : PreparationError(ex) };
                    if (ct.IsCancellationRequested || ex is OperationCanceledException || IsAuthority(ex) ||
                        reset is { FailureBefore: not null, FailureAfter: null, FailureCleared: false }) throw;
                    // Local IO/native/export failures are isolated; any changed frozen authority aborts instead.
                    Guard(input, ct);
                }
                Progress(30 + (n + 1) * 10d / Math.Max(1, episodes.Count));
                Authority(ct);
            }
            if (episodes.Count == 0) Progress(40);
            foreach (var input in ready) { Guard(input, ct); input.ValidateFile(paths, ct); }
            Authority(ct);
            return Array.AsReadOnly(ready.ToArray());

            static object? NativeRead(MethodInfo method, object target, object?[] args)
            {
                try { return method.Invoke(target, args); }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            }
            int Minutes(BaseItem item)
            {
                var value = library.GetLibraryOptions(item).IntroDetectionFingerprintLength;
                return value < 2 || value >= 30 ? 10 : value;
            }
            void Authority(CancellationToken t)
            {
                try
                {
                    ct.ThrowIfCancellationRequested(); t.ThrowIfCancellationRequested(); authority(t);
                    if (!ReferenceEquals(owner, Plugin.Instance)) throw new IntroApplyAuthorityChangedException();
                    selected.ValidateApplyRoot(owner.IntroStore.GetOptions(), t);
                    if (paths.RequireWritablePath(Path.Combine(BaseItem.ConfigurationManager.ApplicationPaths.DataPath, "library.db")) != databasePath)
                        throw new IntroApplyAuthorityChangedException();
                    authority(t);
                }
                catch (OperationCanceledException) { throw; }
                catch (IntroApplyAuthorityChangedException) { throw; }
                catch (Exception ex) { throw new IntroApplyAuthorityChangedException(ex); }
            }
            void Guard(NativeIntroDetection.Input input, CancellationToken t)
            {
                try
                {
                    Authority(t);
                    var candidate = pending[input.Id];
                    var fresh = library.GetItemById(input.Id) as Episode ?? throw new IntroApplyAuthorityChangedException();
                    var episodeOptions = library.GetLibraryOptions(fresh);
                    if (!(input.Source?.IsSupported(fresh) ?? IsEpisodeSupported(fresh)) || !File.Exists(fresh.Path) ||
                        Path.GetFullPath(fresh.GetInternalMetadataPath()) != Path.GetDirectoryName(input.File))
                        throw new IntroApplyAuthorityChangedException();
                    var scopeCheck = selected.CaptureApplyCheck(candidate.Season, new[] { input.Episode, fresh },
                        owner.IntroStore.GetOptions(), input.Duration, t);
                    Authority(t);
                    void Pure()
                    {
                        ct.ThrowIfCancellationRequested(); t.ThrowIfCancellationRequested(); scopeCheck();
                        input.Check(input.Episode); input.Check(fresh);
                        var minutes = episodeOptions.IntroDetectionFingerprintLength;
                        if (minutes < 2 || minutes >= 30) minutes = 10;
                        if (input.Duration != TimeSpan.FromMinutes(minutes) || candidate.Season.PresentationUniqueKey != candidate.Key ||
                            !ReferenceEquals(owner, Plugin.Instance) || !ReferenceEquals(owner.FingerprintApi, this) ||
                            !ReferenceEquals(repository, BaseItem.ItemRepository) || !ReferenceEquals(configuration, BaseItem.ConfigurationManager))
                            throw new IntroApplyAuthorityChangedException();
                    }
                    input.Source?.ValidateNative();
                    externalAuthority?.Invoke(t); // Last host callback before the frozen source/scope proof and SQL lease.
                    Pure();
                    input.Source?.ValidateFiles();
                    // Read-only lease is closed before M98 native clear and native extraction. No host callback
                    // while leased; use the original selector's M101 SQL witness, not a recaptured root.
                    using (var db = (IDisposable)NativeRead(api.Create, api.Pool, new object[] { true, t })!)
                    {
                        object?[] file = { "main", null };
                        if (db.GetType() != api.ConnectionType || !(bool)NativeRead(api.FileName, db, file)! || file[1] is not string actual ||
                            !string.Equals(Path.GetFullPath(actual), databasePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                            throw new IntroApplyAuthorityChangedException();
                        selected.ValidateApplyDatabase(api, db, candidate.Season, candidate.Key, new[] { input });
                    }
                    // No host callbacks after SQL proof; file checks are outside the closed read lease.
                    Pure();
                    input.Source?.ValidateFiles();
                }
                catch (OperationCanceledException) { throw; }
                catch (IntroApplyAuthorityChangedException) { throw; }
                catch (Exception ex) { throw new IntroApplyAuthorityChangedException(ex); }
            }
        }
    }
    private async Task CreateStagedAsync(NativeIntroDetection.Input input, LibraryOptions options, string stage, CancellationToken token,
        Action validateFrozen, Action captureCompleted)
    {
        if (input.Source?.SignedQuery != true)
        {
            await ((Task)Invoke(createStaged ?? throw new NotSupportedException("Native staged creation is unavailable."),
                input.Episode, options, stage, token)!).ConfigureAwait(false);
            return;
        }
        var run = Guid.NewGuid();
        var providerLog = new SignedFingerprintLogger(logger, input.Id, run, false);
        var nativeCompleted = false;
        try
        {
            if (!CanProtectSignedInput()) throw new NotSupportedException();
            providerLog.Record(SignedFingerprintEvent.ProviderBegin);
            if (providerLog.Failed) throw new InvalidOperationException();
            var runnerLog = new SignedFingerprintLogger(logger, input.Id, run, true);
            var forwarding = new SignedFingerprintFfmpegManager(ffmpegManager, signedRunnerConstructor!, runnerLog, applicationHost);
            // Per operation only: same original dependencies and episode, no global host manager replacement.
            var protectedManager = nativeAudioConstructor!.Invoke(new object[] {
                files, providerLog, applicationPaths, forwarding, mediaEncoder, mediaMounts, jsonSerializer, applicationHost });
            validateFrozen(); // Metadata logging can invoke a host sink; reprove source/stage after that callback.
            await ((Task)createStaged!.Invoke(protectedManager, new object[] { input.Episode, options, stage, token })!).ConfigureAwait(false);
            nativeCompleted = true;
            captureCompleted();
            providerLog.Record(SignedFingerprintEvent.ProviderComplete);
            if (providerLog.Failed || runnerLog.Failed) throw new InvalidOperationException();
        }
        catch (Exception ex)
        {
            // Neither the native provider's interpolated URL exception nor sink exceptions escape generation.
            var error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            providerLog.Record(SignedFingerprintEvent.Failure, LogSeverity.Error);
            Exception opaque = error is OperationCanceledException || token.IsCancellationRequested
                ? new OperationCanceledException("Signed fingerprint generation cancelled.", token)
                : IsAuthority(error) ? new IntroApplyAuthorityChangedException()
                : error is NotSupportedException ? new NotSupportedException("Signed fingerprint generation unavailable.")
                : error is InvalidDataException ? new InvalidDataException("Signed fingerprint output is invalid.")
                : error is IOException ? new IOException("Signed fingerprint generation IO failed.")
                : new InvalidOperationException("Signed fingerprint generation failed.");
            if (nativeCompleted) opaque.Data["NativeSignedGenerationCompleted"] = true;
            throw opaque;
        }
    }

    // Shared explicit/incremental preparation only. Global/native Collector eligibility remains unchanged.
    internal bool IsEpisodeSupportedForRebuild(Episode episode, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!IsStrm(episode)) return IsEpisodeSupported(episode);
            _ = CaptureStrmSource(episode, token);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }
    private static bool IsStrm(Episode episode) => Path.GetExtension(episode.Path).Equals(".strm", StringComparison.OrdinalIgnoreCase);

    private bool StrmNativeEligible(Episode episode)
    {
        // Exact pinned 4.10.2 IsIntroDetectionSupported conditions except IsShortcut, plus our physical scope.
        // Do not clone: IsCompleteMedia must inspect the ORIGINAL recording state. No audio-stream rule exists.
        if (!IsSupported || ffmpegManager?.FfmpegCapabilities?.SupportsChromaprint != true ||
            !IsStrm(episode) || episode.IsVirtualItem || !episode.IsFileProtocol || episode.IsPlaceHolder ||
            !episode.RunTimeTicks.HasValue || !episode.IsCompleteMedia) return false;
        var options = library.GetLibraryOptions(episode);
        var minutes = options.IntroDetectionFingerprintLength;
        if (minutes < 2 || minutes >= 30) minutes = 10;
        return options.EnableMarkerDetection && episode.RunTimeTicks.Value >= TimeSpan.FromMinutes(minutes).Ticks;
    }

    private NativeIntroDetection.SourceBinding CaptureStrmSource(Episode episode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var original = StrmEpisodeIdentity(episode);
        var sources = BaseItem.MediaSourceManager;
        void CheckOriginal(Episode current)
        {
            if (StrmEpisodeIdentity(current) != original || !ReferenceEquals(sources, BaseItem.MediaSourceManager))
                throw new InvalidOperationException("Frozen STRM episode identity changed.");
        }
        if (episode.GetType() != typeof(Episode) || !StrmNativeEligible(episode))
            throw new NotSupportedException("STRM native eligibility failed.");
        CheckOriginal(episode);
        var wrapper = LocalStrmFile.Capture(original.Path, wrapper: true);
        var capabilities = ffmpegManager.FfmpegCapabilities;
        var folders = library.GetCollectionFolders(episode, token);
        CheckOriginal(episode);
        var options = library.GetLibraryOptions(episode, folders, token);
        CheckOriginal(episode);
        if (LocalStrmFile.Capture(wrapper.Path, wrapper: true) != wrapper)
            throw new IOException("STRM wrapper changed before native resolution.");
        // Same official static call as LocalDeepDelete: no alternates, substitution, streams, chapters,
        // playback providers, user-track state, probe or live opening. Native StrmMounter owns parsing.
        var resolved = sources.GetStaticMediaSources(episode, false, false, false, false, folders, options, null!, null!, token);
        token.ThrowIfCancellationRequested();
        CheckOriginal(episode);
        if (LocalStrmFile.Capture(wrapper.Path, wrapper: true) != wrapper)
            throw new IOException("STRM wrapper changed during native resolution.");
        if (resolved == null || resolved.Count != 1 || resolved[0].ItemId != original.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new NotSupportedException("Expected one primary STRM source.");
        var source = resolved[0];
#pragma warning disable CS0612 // STRM preparation respects both advertised source flags; not a native Create rule.
        if (!source.SupportsTranscoding || !source.SupportsProbing)
            throw new NotSupportedException("STRM source does not advertise offline processing support.");
#pragma warning restore CS0612
        if (source.RequiresOpening || source.IsInfiniteStream || !string.IsNullOrEmpty(source.EncoderPath) ||
            !string.IsNullOrEmpty(source.OpenToken) || !string.IsNullOrEmpty(source.LiveStreamId) || source.RequiredHttpHeaders?.Count > 0 ||
            source.Type == MediaSourceType.Placeholder || string.IsNullOrWhiteSpace(source.Id))
            throw new NotSupportedException("STRM source requires headers, a live input or an alternate encoder.");
        var remote = source.Protocol == MediaProtocol.Http;
        if ((!remote && (source.Protocol != MediaProtocol.File || source.IsRemote)) ||
            source.EncoderProtocol.HasValue && source.EncoderProtocol != source.Protocol ||
            source.ProbeProtocol.HasValue && source.ProbeProtocol != source.Protocol)
            throw new NotSupportedException("STRM source protocol is unsupported or inconsistent.");
        var remoteUri = remote ? StrmHttpUri(source.Path) : null;
        var signedQuery = remoteUri?.Query.Length > 0;
        if (signedQuery && !CanProtectSignedInput())
            throw new NotSupportedException("Signed STRM diagnostic protection is unavailable.");
        var targetPath = remoteUri != null ? remoteUri.AbsolutePath : LocalStrmFile.LocalPath(source.Path);
        // Native Create mounts the ORIGINAL wrapper; it ignores alternate ProbePath/EncoderPath and headers.
        if (!string.IsNullOrEmpty(source.ProbePath) && (remote
            ? !string.Equals(source.ProbePath, source.Path, StringComparison.Ordinal)
            : !string.Equals(LocalStrmFile.LocalPath(source.ProbePath), targetPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new NotSupportedException("STRM probe source differs from its native mounted source.");
        var extension = Path.GetExtension(targetPath);
        if (!new[] { ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".ts", ".m2ts", ".mts", ".webm", ".wmv", ".mpg", ".mpeg", ".flv", ".ogv", ".3gp" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase) ||
            new[] { "iso", "dvd", "bluray", "blu-ray", "bdmv", "disc" }.Contains(source.Container, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException("STRM target is not an ordinary media input.");
        // ponytail: native HTTP owns range/redirect/timeout/logging. Bind the initial RAW URL, not remote bytes,
        // ETag or final URL; no HEAD/download/client layer. Local media retains its existing path/stat proof.
        var target = remote ? null : LocalStrmFile.Capture(targetPath, wrapper: false);
        var targetIdentity = target == null ? source.Path : FormattableString.Invariant($"{target.Path}\n{target.Length}/{target.Modified.Ticks}/{target.Created.Ticks}");
        var descriptor = SourceKey(source);
        var bindingId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor + "\n" +
            FormattableString.Invariant($"{wrapper.Path}\n{wrapper.Length}/{wrapper.Modified.Ticks}/{wrapper.Created.Ticks}/{wrapper.WrapperHash}\n{targetIdentity}"))));
        void Check(Episode current)
        {
            // No manager callbacks / filesystem operations: this also runs inside the chapter writer.
            CheckOriginal(current);
            if (!capabilities.SupportsChromaprint || SourceKey(source) != descriptor)
                throw new InvalidOperationException("Frozen STRM source identity changed.");
        }
        void ValidateFiles()
        {
            token.ThrowIfCancellationRequested();
            if (LocalStrmFile.Capture(wrapper.Path, wrapper: true) != wrapper ||
                target != null && LocalStrmFile.Capture(target.Path, wrapper: false) != target)
                throw new IOException("Bound STRM wrapper or local target changed.");
            token.ThrowIfCancellationRequested();
        }
        void ValidateNative()
        {
            if (!ReferenceEquals(capabilities, ffmpegManager.FfmpegCapabilities) || !capabilities.SupportsChromaprint)
                throw new InvalidOperationException("STRM native capabilities were replaced or disabled.");
        }
        bool Supported(Episode current)
        {
            var eligible = StrmNativeEligible(current);
            ValidateNative(); Check(current);
            return eligible;
        }
        Check(episode); ValidateFiles();
        if (!Supported(episode)) throw new NotSupportedException("STRM eligibility changed during resolution.");
        Check(episode); ValidateFiles();
        return new(bindingId, Supported, Check, ValidateNative, ValidateFiles, signedQuery);
    }
    private static Uri StrmHttpUri(string path)
    {
        // Validate the exact string native will quote, never a normalized replacement. GetInputArgument splits
        // raw '|' into concat inputs; quotes/backslashes/whitespace may change CLI interpretation or logging.
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 ||
            path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\"' or '\'' or '\\' or '|' or '\u200c' or '\u200d') ||
            !Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString() ||
            uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new NotSupportedException("STRM HTTP input requires a headerless HTTP(S) media URL without userinfo or fragment.");
        return uri;
    }

    private static (long InternalId, Guid Id, string Path, string Container, bool Virtual, string External, bool Shortcut,
        long? Runtime, long Parent, long Series, long Album, DateTimeOffset Created, int? Index, int? ParentIndex, string Key) StrmEpisodeIdentity(Episode item) =>
        (item.InternalId, item.Id, item.Path, item.Container, item.IsVirtualItem, item.ExternalId, item.IsShortcut,
            item.RunTimeTicks, item.ParentId, item.SeriesId, item.InternalAlbumId, item.DateCreated, item.IndexNumber, item.ParentIndexNumber, item.PresentationUniqueKey);

    private static string SourceKey(MediaSourceInfo source)
    {
        var text = new[] { source.Id, source.ItemId, source.Path, source.ProbePath, source.EncoderPath, source.Container, source.OpenToken, source.LiveStreamId };
        if (text.Any(s => s?.Length > 4096 || s?.Any(char.IsControl) == true)) throw new NotSupportedException("STRM source identity exceeds the path/format budget.");
#pragma warning disable CS0612 // Bind the same advertised legacy flag checked during capture.
        return string.Join("\n", text) + $"\n{source.Protocol}/{source.ProbeProtocol}/{source.EncoderProtocol}/{source.Type}/{source.RequiresOpening}/{source.IsRemote}/{source.IsInfiniteStream}/{source.RequiredHttpHeaders?.Count ?? 0}/{source.SupportsTranscoding}/{source.SupportsProbing}";
#pragma warning restore CS0612
    }

    // Path/stat identity, NOT inode/global-writer atomicity. Only the small wrapper is hashed, never the video.
    private sealed record LocalStrmFile(string Path, long Length, DateTime Modified, DateTime Created, string? WrapperHash)
    {
        internal static string LocalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl))
                throw new NotSupportedException("Invalid local STRM source path.");
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile || uri.IsUnc || !uri.IsLoopback || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                    throw new NotSupportedException("STRM input must be a local file path.");
                path = uri.LocalPath;
            }
            if (!System.IO.Path.IsPathFullyQualified(path) || path.Contains("://", StringComparison.Ordinal) || path.StartsWith("\\\\", StringComparison.Ordinal))
                throw new NotSupportedException("STRM input must be a fully qualified local path.");
            return System.IO.Path.GetFullPath(path);
        }
        internal static LocalStrmFile Capture(string path, bool wrapper)
        {
            path = LocalPath(path);
            var root = System.IO.Path.GetPathRoot(path)!;
            var parts = path.Substring(root.Length).Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 128) throw new NotSupportedException("STRM source path depth exceeds 128.");
            foreach (var part in parts)
            {
                root = System.IO.Path.Combine(root, part);
                if (new FileInfo(root).LinkTarget != null || new DirectoryInfo(root).LinkTarget != null ||
                    (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("STRM source path traverses a link.");
            }
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || file.Length <= 0 || wrapper && file.Length > 16384)
                throw new NotSupportedException("STRM input is not a bounded wrapper or regular media file.");
            var value = new LocalStrmFile(path, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc, null);
            if (wrapper)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != value.Length) throw new IOException("STRM wrapper changed before bounded read.");
                using var reader = new BinaryReader(stream);
                var bytes = reader.ReadBytes((int)value.Length);
                if (bytes.Length != value.Length || stream.ReadByte() != -1) throw new IOException("STRM wrapper changed during bounded read.");
                value = value with { WrapperHash = Convert.ToHexString(SHA256.HashData(bytes)) };
            }
            var final = new FileInfo(path);
            if (!final.Exists || final.Length != value.Length || final.LastWriteTimeUtc != value.Modified || final.CreationTimeUtc != value.Created ||
                (final.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("STRM input changed during snapshot.");
            return value;
        }
    }

    private static bool IsAuthority(Exception ex) => ex is IntroApplyAuthorityChangedException || ex is AggregateException a &&
        a.Flatten().InnerExceptions.Any(e => e is IntroApplyAuthorityChangedException);
    private static string PreparationError(Exception ex) => ex is InvalidDataException ? "InvalidGeneratedFingerprint" :
        ex is NotSupportedException ? "NotSupported" : ex is IOException ? "FingerprintIO" : "PreparationFailed";
}
