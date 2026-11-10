using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace StrmAssistantRebuild.Common;

public sealed record IntroDetectionItem(long ItemId, long SeasonId, string Status, TimeSpan? Start,
    TimeSpan? End, double? Confidence, string? Error, bool NativeCacheCleared, bool NativeCallStarted, bool NativeCacheClearStarted = false);
public sealed record IntroDetectionReport(long TargetId, string State, int Candidates, long WorkUnits,
    IReadOnlyList<IntroDetectionItem> Items)
{
    public string Mode => "existing-fingerprints-native-cache-refresh";
    public bool ChaptersWritten => false;
    public bool FingerprintsGenerated => false;
    public int MaxTargetEpisodes => NativeIntroDetection.MaxTargetEpisodes;
    public int MaxSeasonEpisodes => NativeIntroDetection.MaxSeasonEpisodes;
    public int MinFrames => NativeIntroDetection.MinFrames;
    public int MaxFrames => NativeIntroDetection.MaxFrames;
    public long MaxCallWorkUnits => NativeIntroDetection.MaxCallWork;
    public long MaxSeasonWorkUnits => NativeIntroDetection.MaxSeasonWork;
    public long MaxTargetWorkUnits => NativeIntroDetection.MaxTargetWork;
    public string BudgetState { get; init; } = "NotStarted";
    public long PeakCallWorkUnits { get; init; }
    public long StartedNativeWorkUnits { get; init; }
    public string WorkUnit => "directed frame pairs; input-size estimate, not elapsed time or measured CPU instructions";
}

public sealed partial class FingerprintApi
{
    /// <summary>Explicit server-only calculation from existing native .fp files, never extraction or chapter updates.
    /// Clears ONLY IntroFingerprint algorithm extradata immediately before each native call; native code may write it again.
    /// Neither IntroDetectionExtradata nor chapters are changed. This is not a read-only preview or a media rescan.
    /// Queue locks cover this plugin, not independent host writers; cache clearing and native detection are not a DB transaction.
    /// Native calls are synchronous without cancellation: cancellation is observed before/after each bounded call.
    /// Each episode has independent native memoization; this deliberately differs from shared-memo whole-season Update.
    /// Confidence is a raw native score, not a probability.
    /// Cancellation/failure carries the immutable partial report in Data["IntroDetectionReport"].</summary>
    public Task<IntroDetectionReport> CalculateIntroForTargetAsync(long targetId, IProgress<double>? progress,
        CancellationToken token) => CalculateIntroCoreAsync(targetId, progress, token, FingerprintStateReset.RequireNativeClear);

    // Private call-span capture only: the original selector and exact native inputs never cross an API boundary.
    // Kept here so calculation-only source-linked consumers need no chapter/apply dependencies.
    private sealed record IntroCalculationBatch(Season Season, Episode[] Episodes, NativeIntroDetection.Input[] Inputs, string Key);
    private sealed record IntroCalculationContext(ScopedFingerprintSelection Selection, IReadOnlyList<IntroCalculationBatch> Batches,
        Action<CancellationToken> CheckOwner, Action<CancellationToken> ValidateAuthority,
        Action<IntroCalculationBatch, CancellationToken> ValidateBatch);

    private async Task<IntroDetectionReport> CalculateIntroCoreAsync(long targetId, IProgress<double>? progress,
        CancellationToken token, Func<IItemRepository, Action<long, long>> requireClear,
        Action<IntroCalculationContext>? capture = null,
        Func<ScopedFingerprintSelection, IReadOnlyList<Episode>, string, Action<CancellationToken>, CancellationToken,
            Task<IReadOnlyList<NativeIntroDetection.Input>>>? prepare = null,
        ScopedFingerprintSelection? fixedScope = null, Action<CancellationToken>? externalAuthority = null,
        IReadOnlyList<Episode>? originalEpisodes = null, IReadOnlySet<long>? calculationTargets = null)
    {
        var receipts = new List<IntroDetectionItem>();
        var count = 0; long work = 0, peakCallWork = 0, startedWork = 0;
        var budgetState = "NotStarted";
        IntroDetectionReport Report(string state) => new(targetId, state, count, work, Array.AsReadOnly(receipts.ToArray()))
            { BudgetState = budgetState, PeakCallWorkUnits = peakCallWork, StartedNativeWorkUnits = startedWork };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, queue.StoppingToken);
        token = linked.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var owner = Plugin.Instance ?? throw new InvalidOperationException("Intro calculation requires the current plugin owner.");
            void Owner(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(Plugin.Instance, owner) || !ReferenceEquals(owner.FingerprintApi, this) || !ReferenceEquals(owner.WorkQueue, queue))
                    throw new InvalidOperationException("Intro calculation owner changed.");
            }
            Owner(token);
            if (!IsSupported || applicationHost?.ApplicationVersion != VerifiedVersion || jsonSerializer == null)
                throw new NotSupportedException("Intro calculation requires the verified native provider.");
            if (externalAuthority != null) { externalAuthority(token); Owner(token); }
            var scope = fixedScope ?? ScopedFingerprintSelection.Capture(library, targetId, owner.IntroStore.GetOptions(), token);
            if (fixedScope != null)
            {
                fixedScope.ValidateFixedTarget(library, targetId, owner.IntroStore.GetOptions(), token);
                Owner(token);
            }
            return await Task.Run(async () =>
            {
                Owner(token);
                var native = new NativeIntroDetection(manager!.GetType().Assembly);
                var repository = BaseItem.ItemRepository;
                var configuration = capture == null && prepare == null ? null : BaseItem.ConfigurationManager;
                var clear = requireClear(repository);
                var hostPaths = applicationPaths as IServerApplicationPaths ?? throw new NotSupportedException("Server metadata paths are required.");
                var root = Path.GetFullPath(hostPaths.InternalMetadataPath);
                var selected = scope.Select(owner.IntroStore.GetOptions(), token);
                var episodes = originalEpisodes?.ToArray() ?? selected.Episodes(owner.IntroStore.GetOptions(), token);
                count = episodes.Length;
                if (count > NativeIntroDetection.MaxTargetEpisodes || episodes.GroupBy(e => e.InternalAlbumId).Any(g => g.Count() > NativeIntroDetection.MaxSeasonEpisodes))
                {
                    budgetState = "Rejected";
                    throw new InvalidOperationException($"Intro calculation candidate budget exceeded ({NativeIntroDetection.MaxTargetEpisodes} per target, {NativeIntroDetection.MaxSeasonEpisodes} per season).");
                }
                progress?.Report(0); Owner(token);
                Dictionary<long, NativeIntroDetection.Input>? prepared = null;
                if (prepare != null)
                {
                    ValidateAuthority(token);
                    var ready = (await prepare(selected, Array.AsReadOnly(episodes), root, ValidateAuthority, token).ConfigureAwait(false)).ToArray();
                    ValidateAuthority(token);
                    var original = episodes.ToDictionary(e => e.InternalId);
                    if (ready.Select(i => i.Id).Distinct().Count() != ready.Length ||
                        ready.Any(i => !original.TryGetValue(i.Id, out var e) || !ReferenceEquals(e, i.Episode)))
                        throw new InvalidOperationException("Preparation returned inputs outside the original physical selection.");
                    prepared = ready.ToDictionary(i => i.Id);
                    episodes = episodes.Where(e => prepared.ContainsKey(e.InternalId)).ToArray();
                }
                count = calculationTargets?.Count ?? count;
                var batches = new List<IntroCalculationBatch>();
                foreach (var group in episodes.GroupBy(e => e.InternalAlbumId))
                {
                    Owner(token);
                    var season = library.GetItemById(group.Key) as Season ?? throw new InvalidOperationException("Physical season disappeared.");
                    var items = group.ToArray();
                    selected.GuardBatch(season, items, owner.IntroStore.GetOptions(), token);
                    var key = season.PresentationUniqueKey;
                    if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Season presentation key is missing.");
                    var minutes = library.GetLibraryOptions(season).IntroDetectionFingerprintLength;
                    if (minutes < 2 || minutes >= 30) minutes = 10;
                    var inputs = new List<NativeIntroDetection.Input>();
                    foreach (var episode in items)
                    {
                        Owner(token);
                        try
                        {
                            var input = prepared == null ? NativeIntroDetection.Input.Capture(episode, root, minutes) : prepared[episode.InternalId];
                            if (!(input.Source?.IsSupported(episode) ?? IsEpisodeSupported(episode)))
                                throw new NotSupportedException("Native episode eligibility failed.");
                            ValidateMetadata(episode, input.File);
                            if (prepared == null) input.Read(paths, token);
                            else { input.Check(episode); input.ValidateFile(paths, token); }
                            inputs.Add(input);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            if (calculationTargets != null) throw; // A prepared dependency changed; never silently drop a frozen reference.
                            receipts.Add(new(episode.InternalId, season.InternalId, "Failed", null, null, null, Error(ex), false, false));
                        }
                    }
                    var batch = new IntroCalculationBatch(season, items, inputs.ToArray(), key);
                    ValidateBatch(batch, token);
                    var estimate = NativeIntroDetection.MeasureWork(inputs.Select(i => i.Frames.Count).ToArray(),
                        calculationTargets == null ? null : inputs.Select(i => calculationTargets.Contains(i.Id)).ToArray());
                    work = checked(work + estimate.Total);
                    peakCallWork = Math.Max(peakCallWork, estimate.PeakCall);
                    try { NativeIntroDetection.RequireWork(estimate.Total, estimate.PeakCall, work); }
                    catch { budgetState = "Rejected"; throw; }
                    batches.Add(batch);
                }
                // Preflight every season/work budget before the first algorithm-cache mutation. No truncation.
                foreach (var batch in batches) ValidateBatch(batch, token);
                budgetState = "Admitted";
                foreach (var batch in batches)
                {
                    var seasonInfo = native.CreateSeason(batch.Inputs);
                    foreach (var input in batch.Inputs.Where(i => calculationTargets == null || calculationTargets.Contains(i.Id)))
                    {
                        Owner(token);
                        if (batch.Inputs.Length < 2)
                        {
                            receipts.Add(new(input.Id, batch.Season.InternalId, "Failed", null, null, null, "InsufficientFingerprints", false, false));
                            continue;
                        }
                        var index = receipts.Count;
                        receipts.Add(new(input.Id, batch.Season.InternalId, "Failed", null, null, null, "Interrupted", false, false));
                        try
                        {
                            await queue.RunSingle(input.Id, ct =>
                            {
                                ValidateBatch(batch, ct);
                                var typeId = repository.GetExtradataTypeId("IntroFingerprint");
                                ValidateBatch(batch, ct);
                                if (typeId <= 0) throw new InvalidOperationException("Native algorithm cache key was not resolved.");
                                // Incremental targets may have been marked while waiting; references never enter this loop.
                                if (calculationTargets != null)
                                {
                                    var marked = HasAnyIntro(input.Episode, ct);
                                    ValidateBatch(batch, ct); // Close chapter DTO/path callbacks before any cache mutation.
                                    if (marked)
                                    {
                                        receipts[index] = receipts[index] with { Status = "AlreadyMarked", Error = null };
                                        return Task.FromResult(true);
                                    }
                                }
                                // No failure-cache getter/clear, no chapter writes or persistence/export wrapper here.
                                receipts[index] = receipts[index] with { Error = "CacheClearInterrupted", NativeCacheClearStarted = true };
                                clear(input.Id, typeId);
                                receipts[index] = receipts[index] with { NativeCacheCleared = true };
                                ValidateBatch(batch, ct);
                                receipts[index] = receipts[index] with { NativeCallStarted = true };
                                startedWork = checked(startedWork + (long)input.Frames.Count * batch.Inputs.Where(i => i.Id != input.Id).Sum(i => (long)i.Frames.Count));
                                var raw = native.Detect(input, seasonInfo, batch.Season, new DirectoryService(logger, files), logger, applicationHost, jsonSerializer);
                                ValidateBatch(batch, ct);
                                receipts[index] = native.Adapt(raw, input.Id, batch.Season.InternalId, input.RuntimeTicks) with
                                    { NativeCacheCleared = true, NativeCallStarted = true, NativeCacheClearStarted = true };
                                return Task.FromResult(true);
                            }, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex) { receipts[index] = receipts[index] with { Status = "Failed", Start = null, End = null, Confidence = null, Error = Error(ex) }; }
                        progress?.Report(receipts.Count * 100d / count); Owner(token);
                    }
                }
                foreach (var batch in batches) ValidateBatch(batch, token);
                capture?.Invoke(new IntroCalculationContext(selected, Array.AsReadOnly(batches.ToArray()), CheckOwner, ValidateAuthority, ValidateBatch));
                progress?.Report(100); Owner(token);
                return Report(receipts.Any(i => i.Status == "Failed") ? "CompletedWithFailures" : "Completed");

                void ValidateAuthority(CancellationToken ct)
                {
                    CheckOwner(ct);
                    // External actor/policy reads stay outside the native writer and read leases. Close their
                    // callbacks with our own root/owner proof; never put them in the writer's CheckOwner.
                    if (externalAuthority != null) { externalAuthority(ct); CheckOwner(ct); }
                    fixedScope?.ValidateFixedTarget(library, targetId, owner.IntroStore.GetOptions(), ct);
                    if (Path.GetFullPath(hostPaths.InternalMetadataPath) != root ||
                        Path.GetFullPath(BaseItem.ConfigurationManager.ApplicationPaths.InternalMetadataPath) != root)
                        throw new InvalidOperationException("Fingerprint metadata authority changed.");
                    CheckOwner(ct);
                }
                void ValidateMetadata(Episode episode, string file)
                {
                    if (Path.GetFullPath(hostPaths.InternalMetadataPath) != root ||
                        Path.GetFullPath(BaseItem.ConfigurationManager.ApplicationPaths.InternalMetadataPath) != root ||
                        Path.GetFullPath(episode.GetInternalMetadataPath()) != Path.GetDirectoryName(file))
                        throw new InvalidOperationException("Fingerprint metadata authority changed.");
                    paths.RequireWritablePath(file);
                }
                void CheckOwner(CancellationToken ct)
                {
                    Owner(ct);
                    if (!ReferenceEquals(repository, BaseItem.ItemRepository) || !ReferenceEquals(configuration, BaseItem.ConfigurationManager))
                        throw new InvalidOperationException("Intro calculation native authority changed.");
                }
                void ValidateBatch(IntroCalculationBatch batch, CancellationToken ct)
                {
                    Owner(ct);
                    var currentOptions = library.GetLibraryOptions(batch.Season);
                    var currentMinutes = currentOptions.IntroDetectionFingerprintLength;
                    if (currentMinutes < 2 || currentMinutes >= 30) currentMinutes = 10;
                    var freshItems = new List<Episode>();
                    foreach (var input in batch.Inputs)
                    {
                        if (input.Duration != TimeSpan.FromMinutes(currentMinutes)) throw new InvalidOperationException("Fingerprint length setting changed.");
                        ValidateMetadata(input.Episode, input.File);
                        var fresh = library.GetItemById(input.Id) as Episode ?? throw new InvalidOperationException("Episode disappeared.");
                        if (input.Source != null && !input.Source.IsSupported(fresh)) throw new InvalidOperationException("Bound source eligibility changed.");
                        freshItems.Add(fresh);
                    }
                    // Close actor and all metadata/fresh-item callbacks with the selector's shared final-read proof.
                    if (externalAuthority != null) ValidateAuthority(ct);
                    var scopeCheck = selected.GuardBatch(batch.Season, batch.Episodes, owner.IntroStore.GetOptions(), ct);
                    foreach (var input in batch.Inputs) input.Source?.ValidateNative();
                    externalAuthority?.Invoke(ct); // Last host callback; scope/file/entity checks below are frozen or bounded IO.
                    scopeCheck(); // Close native-capability callbacks with the original retained entity witness.
                    var finalMinutes = currentOptions.IntroDetectionFingerprintLength;
                    if (finalMinutes < 2 || finalMinutes >= 30) finalMinutes = 10;
                    if (finalMinutes != currentMinutes) throw new InvalidOperationException("Fingerprint length changed during scope callbacks.");
                    for (var i = 0; i < batch.Inputs.Length; i++)
                    {
                        var input = batch.Inputs[i];
                        input.ValidateFile(paths, ct); // Bounded filesystem reads only; no host callbacks after scope proof.
                        input.Check(input.Episode); input.Check(freshItems[i]);
                    }
                    if (batch.Season.PresentationUniqueKey != batch.Key || !ReferenceEquals(repository, BaseItem.ItemRepository))
                        throw new InvalidOperationException("Intro calculation native authority changed.");
                    Owner(ct);
                }
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ex.Data["IntroDetectionReport"] = Report(token.IsCancellationRequested ? "Cancelled" : "Failed");
            throw;
        }
    }
    private static bool HasAnyIntro(Episode episode, CancellationToken token) => BaseItem.ItemRepository.GetChapters(episode, token)
        .Any(c => c.MarkerType is MediaBrowser.Model.Entities.MarkerType.IntroStart or MediaBrowser.Model.Entities.MarkerType.IntroEnd);

    private static string Error(Exception ex) => ex is InvalidDataException ? "InvalidFingerprintOrResult" :
        ex is FileNotFoundException or DirectoryNotFoundException ? "MissingFingerprint" :
        ex is NotSupportedException ? "NotSupported" : ex is IOException ? "FingerprintIO" : "CalculationFailed";
}

/// <summary>Exact 4.10.2 adapter. Evidence ev_730b05db7634029b4ec64132e3821a69ad4aca4e55cf76db491aa50c179748c5.
/// Only the two evidenced internal SeasonFingerprintInfo setters are invoked; no fields or private algorithms.</summary>
internal sealed class NativeIntroDetection
{
    internal const int MaxTargetEpisodes = 128, MaxSeasonEpisodes = 32, MinFrames = 4, MaxFrames = 16384;
    // D104 native pair measurements: 4800/9600/13900 frames, median 36.69/144.76/302.05ms on M2.
    // These directed-pair budgets bound work, not cross-host latency or peak memory. No sampling/truncation.
    internal const long MaxCallWork = 4_000_000_000, MaxSeasonWork = 128_000_000_000, MaxTargetWork = 256_000_000_000;
    private readonly Type fingerprint, seasonInfo, result, sequence;
    private readonly MethodInfo detect;
    private readonly PropertyInfo data, duration, itemId, path, modified, seasonFingerprints, seasonEpisodes, best, confidence, start, end, kind, resultId;

    internal NativeIntroDetection(Assembly assembly)
    {
        if (!FingerprintApi.VerifyAssembly(assembly)) throw new NotSupportedException("Intro calculation provider artifact differs.");
        Type T(string name) => assembly.GetType("Emby.Providers.Markers." + name, true)!;
        fingerprint = T("FingerprintInfo"); seasonInfo = T("SeasonFingerprintInfo"); result = T("SequenceDetectionResult"); sequence = T("SequenceResult");
        var sequenceType = T("SequenceType");
        detect = T("SequenceDetection").GetMethod("DetectSequences", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
            null, new[] { fingerprint, typeof(string), seasonInfo, typeof(BaseItem), typeof(IDirectoryService), typeof(ILogger), typeof(IServerApplicationHost), typeof(IJsonSerializer) }, null)
            ?? throw new NotSupportedException("Native sequence detection signature differs.");
        Pin(detect, 0x060001a2, "0e051164a58a27aa13571c34650c3c39c34995c0622a3be98c0ccf4e846287b3");
        if (detect.ReturnType != result || !detect.IsPublic || !detect.IsStatic) throw new NotSupportedException("Native sequence detection result differs.");
        Pin(fingerprint.GetConstructor(Type.EmptyTypes)!, 0x060001cd, "c48385bc610d7cfab957b71d31a03680115c91f66ada58de734d338e8db494e4");
        Pin(seasonInfo.GetConstructor(Type.EmptyTypes)!, 0x0600018d, "26ec467dbd2c13c80986d94110ffbac906a136563d1ddd947d6166928f8a57ca");
        data = P(fingerprint, "Data", typeof(List<uint>), true, 0x060001c4, "f9e89b4b130fe38fe8c82869553018fa08d9feb341efec455cd3001211c42262");
        duration = P(fingerprint, "Duration", typeof(TimeSpan), true, 0x060001c6, "1b7e2c667643df74cf3a6f20383b4e940c42366c54478fd0c061de3a8105006c");
        itemId = P(fingerprint, "ItemId", typeof(long), true, 0x060001c8, "28147c08947d6ece03dfbfc3626e946626607374628b44bc578660aabcb5ff46");
        path = P(fingerprint, "Path", typeof(string), true, 0x060001ca, "373ff0845b3c3209d7d67c0a1fcf63d1e7f503021518a52b3289d70f9a9cea34");
        modified = P(fingerprint, "VideoFileDateModifiedTicks", typeof(long), true, 0x060001cc, "53ca24841713463d72595f92eb3f84bced9cdc0d5d56bad013678423cfa7d957");
        seasonFingerprints = P(seasonInfo, "Fingerprints", typeof(List<>).MakeGenericType(fingerprint), true, 0x0600018a, "fd31ce41e2a53b05ced9f9627545e8893763386d8cfdd10d4ccc03524f366e86", true);
        seasonEpisodes = P(seasonInfo, "Episodes", typeof(Episode[]), true, 0x0600018c, "aa0de007b8010123e965eefd25d2ea0fb0a2777753010a8a15bb2efe2f92eb45", true);
        best = P(result, "BestTitleSequence", sequence, false, 0x060001be, "a372af799553901a30fd0d455b7e01d8782c26a2b511d9b19cffb845a879cf67");
        confidence = P(result, "Confidence", typeof(double), false, 0x060001c0, "2858cf71b2678d76af15afa719527e7d5a93ae10848c8ffe926c88ccf057a9d3");
        start = P(sequence, "SequenceStart", typeof(TimeSpan), false, 0x060001b4, "dc77946b9e9661f555a751aa2ccbb25f2038109e50a20d85f84c616fea1378ee");
        end = P(sequence, "SequenceEnd", typeof(TimeSpan), false, 0x060001b8, "a1e5b597b903c92af2551f0ba22935a1eb566ddf72448239ec5a4830a3c61d70");
        kind = P(sequence, "SequenceType", sequenceType, false, 0x060001b9, "03ecd3c15c51b48ab8c27d261954e5b8b94b232a164a6d6d21df0fc2ed3499f9");
        resultId = P(sequence, "ItemId", typeof(long), false, 0x060001bb, "fec5d9ec25e03e2842e81bf6a7c059bc3844a0ad43d5acf47578d1525c67c239");
        if (!sequenceType.IsEnum || Convert.ToInt32(Enum.Parse(sequenceType, "Intro")) != 0) throw new NotSupportedException("Native sequence kind differs.");
    }
    private static void Pin(MethodBase method, int token, string hash)
    {
        if (method == null || method.MetadataToken != token || method.ContainsGenericParameters ||
            method.GetMethodBody()?.GetILAsByteArray() is not { } il || !Convert.ToHexString(SHA256.HashData(il)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Native intro member pin differs.");
    }
    private static PropertyInfo P(Type type, string name, Type value, bool set, int token, string hash, bool internalMember = false)
    {
        var p = type.GetProperty(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | (internalMember ? BindingFlags.NonPublic : BindingFlags.Public));
        var accessor = set ? p?.SetMethod : p?.GetMethod;
        if (p?.PropertyType != value || p.GetIndexParameters().Length != 0 || accessor == null || accessor.IsStatic ||
            (internalMember ? !accessor.IsAssembly : !accessor.IsPublic)) throw new NotSupportedException("Native intro property differs: " + name);
        Pin(accessor, token, hash); return p;
    }
    private object Fingerprint(Input input)
    {
        var value = Activator.CreateInstance(fingerprint)!;
        data.SetValue(value, new List<uint>(input.Frames)); duration.SetValue(value, input.Duration); itemId.SetValue(value, input.Id);
        path.SetValue(value, input.File); modified.SetValue(value, input.Created.Ticks); return value;
    }
    internal object CreateSeason(IReadOnlyList<Input> inputs)
    {
        var value = Activator.CreateInstance(seasonInfo)!;
        var list = (IList)Activator.CreateInstance(seasonFingerprints.PropertyType)!;
        foreach (var input in inputs) list.Add(Fingerprint(input));
        seasonFingerprints.SetValue(value, list);
        // Native reads only ID/PUK here. Detached copies prevent mutable server entities changing native exclusion sets.
        seasonEpisodes.SetValue(value, inputs.Select(i => new Episode { InternalId = i.Id, Id = i.Guid, PresentationUniqueKey = i.Key }).ToArray());
        return value;
    }
    internal object? Detect(Input input, object season, BaseItem physicalSeason, IDirectoryService directory, ILogger logger, IServerApplicationHost host, IJsonSerializer json)
    {
        try { return detect.Invoke(null, new[] { Fingerprint(input), input.Key, season, physicalSeason, directory, logger, host, json }); }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    internal IntroDetectionItem Adapt(object? raw, long id, long seasonId, long runtimeTicks)
    {
        if (raw == null) throw new InvalidDataException("Native detection returned no result object.");
        if (raw.GetType() != result) throw new InvalidDataException("Unknown native result type.");
        var score = (double)confidence.GetValue(raw)!;
        // Raw native score: no probability normalization or unproven lower-bound threshold.
        if (!double.IsFinite(score)) throw new InvalidDataException("Invalid native confidence.");
        var match = best.GetValue(raw);
        if (match == null) return new(id, seasonId, "NoMatch", null, null, score, null, false, false);
        if (match.GetType() != sequence || (long)resultId.GetValue(match)! != id || Convert.ToInt32(kind.GetValue(match)) != 0)
            throw new InvalidDataException("Native result identity or sequence kind differs.");
        var from = (TimeSpan)start.GetValue(match)!; var to = (TimeSpan)end.GetValue(match)!;
        if (runtimeTicks <= 0 || from < TimeSpan.Zero || to <= from || to.Ticks > runtimeTicks) throw new InvalidDataException("Invalid native intro range.");
        return new(id, seasonId, "Calculated", from, to, score, null, false, false);
    }
    internal static (long Total, long PeakCall) MeasureWork(IReadOnlyList<int> counts, IReadOnlyList<bool>? targets = null)
    {
        if (counts.Count > MaxSeasonEpisodes || counts.Any(n => n < MinFrames || n > MaxFrames)) throw new InvalidDataException("Fingerprint frame/candidate budget exceeded.");
        if (targets != null && targets.Count != counts.Count) throw new ArgumentException("Target/frame shape differs.");
        long work = 0, peak = 0;
        for (var i = 0; i < counts.Count; i++)
        {
            if (targets != null && !targets[i]) continue;
            long call = 0;
            for (var j = 0; j < counts.Count; j++) if (i != j) call = checked(call + (long)counts[i] * counts[j]);
            work = checked(work + call); peak = Math.Max(peak, call);
        }
        return (work, peak);
    }
    internal static void RequireWork(long season, long call, long target)
    {
        // ponytail: native synchronous calls have no CT; bound EACH call independently of whole-job resources.
        if (call > MaxCallWork) throw new InvalidDataException($"Fingerprint native-call work budget exceeded ({MaxCallWork}).");
        if (season > MaxSeasonWork) throw new InvalidDataException($"Fingerprint season work budget exceeded ({MaxSeasonWork}).");
        if (target > MaxTargetWork) throw new InvalidOperationException($"Fingerprint target work budget exceeded ({MaxTargetWork}).");
    }
    internal static long Work(IReadOnlyList<int> counts)
    {
        var estimate = MeasureWork(counts);
        RequireWork(estimate.Total, estimate.PeakCall, estimate.Total);
        return estimate.Total;
    }
    // Only explicit preparation supplies this binding; direct calculation/collector retain native eligibility.
    // The source-specific implementation stays in IntroRebuild; old calculation-only source links need no new file.
    internal sealed record SourceBinding(string Id, Func<Episode, bool> IsSupported, Action<Episode> Check,
        Action ValidateNative, Action ValidateFiles, bool SignedQuery = false);

    internal sealed record Input(Episode Episode, long Id, Guid Guid, string MediaPath, DateTimeOffset Created,
        string Key, long RuntimeTicks, string File, TimeSpan Duration)
    {
        internal IReadOnlyList<uint> Frames { get; private set; } = Array.Empty<uint>();
        internal SourceBinding? Source { get; init; }
        internal string? FramesSourceId => Source?.Id;
        internal static Input Capture(Episode item, string root, int minutes)
        {
            if (item.InternalId <= 0 || item.Id == Guid.Empty || item.RunTimeTicks is not > 0 || string.IsNullOrWhiteSpace(item.PresentationUniqueKey))
                throw new InvalidDataException("Incomplete fingerprint episode identity.");
            var guid = item.Id.ToString("N");
            return new(item, item.InternalId, item.Id, item.Path, item.DateCreated, item.PresentationUniqueKey, item.RunTimeTicks.Value,
                Path.Combine(root, "library", guid[..2], guid, FormattableString.Invariant($"title_{minutes}_{item.DateCreated.Ticks}.fp")), TimeSpan.FromMinutes(minutes));
        }
        internal void Check(Episode current)
        {
            if (current.InternalId != Id || current.Id != Guid || current.Path != MediaPath || current.DateCreated != Created ||
                current.PresentationUniqueKey != Key || current.RunTimeTicks != RuntimeTicks) throw new InvalidOperationException("Frozen fingerprint identity changed.");
            Source?.Check(current); // Frozen memory only; also called inside the native chapter writer.
        }
        internal void Read(EmbyPathPolicy policy, CancellationToken token)
        {
            Source?.ValidateFiles();
            Frames = ReadFrames(policy, token).AsReadOnly();
            Source?.ValidateFiles();
        }
        internal void ValidateFile(EmbyPathPolicy policy, CancellationToken token)
        {
            Source?.ValidateFiles();
            if (!Frames.SequenceEqual(ReadFrames(policy, token))) throw new IOException("Fingerprint content changed since capture.");
            Source?.ValidateFiles();
        }
        private List<uint> ReadFrames(EmbyPathPolicy policy, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var file = new FileInfo(policy.RequireWritablePath(File));
            if (!file.Exists) throw new FileNotFoundException("Existing fingerprint required.");
            var bytes = file.Length; var stamp = file.LastWriteTimeUtc;
            if ((file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || bytes < MinFrames * 4 || bytes % 4 != 0 || bytes > MaxFrames * 4)
                throw new InvalidDataException("Fingerprint file budget or format invalid.");
            using var stream = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != bytes) throw new IOException("Fingerprint size changed.");
            using var reader = new BinaryReader(stream);
            var frames = new List<uint>((int)(bytes / 4));
            for (var i = 0L; i < bytes / 4; i++) { token.ThrowIfCancellationRequested(); frames.Add(reader.ReadUInt32()); }
            token.ThrowIfCancellationRequested();
            if (stream.ReadByte() != -1 || new FileInfo(policy.RequireWritablePath(File)).LastWriteTimeUtc != stamp) throw new IOException("Fingerprint changed while reading.");
            return frames;
        }
    }
}
