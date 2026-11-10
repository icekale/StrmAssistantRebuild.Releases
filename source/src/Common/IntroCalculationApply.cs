using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using StrmAssistantRebuild.IntroSkip;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

public sealed record IntroApplyItem(long ItemId, string Status, IntroCommitReport? CommitReport, string? Error);
public sealed record IntroApplyReport(long TargetId, string State, IntroDetectionReport? CalculationReport,
    IReadOnlyList<IntroApplyItem> Items);

public sealed partial class FingerprintApi
{
    /// <summary>Server-only, recomputes existing fingerprints and applies only Calculated ranges to the original
    /// physical selection. No client result/plan, extraction, reset, NoMatch clearing or whole-batch transaction.
    /// Cancellation/global authority failures throw with Data["IntroApplyReport"]; committed receipts survive.
    /// Configuration is checked after queue/persistence waits, not atomically locked with the native database.</summary>
    public Task<IntroApplyReport> CalculateAndApplyIntroForTargetAsync(long targetId, IProgress<double>? progress,
        CancellationToken token) => CalculateAndApplyIntroCoreAsync(targetId, progress, token, null);

    private Task<IntroApplyReport> CalculateAndApplyIntroCoreAsync(long targetId, IProgress<double>? progress,
        CancellationToken token,
        Func<ScopedFingerprintSelection, IReadOnlyList<Episode>, string, Action<CancellationToken>, CancellationToken,
            Task<IReadOnlyList<NativeIntroDetection.Input>>>? prepare,
        ScopedFingerprintSelection? fixedScope = null, Action<CancellationToken>? externalAuthority = null,
        IReadOnlyList<Episode>? originalEpisodes = null, IReadOnlySet<long>? calculationTargets = null) => Task.Run(async () =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, queue.StoppingToken);
        var ct = linked.Token;
        IntroDetectionReport? calculation = null;
        IntroCalculationContext? context = null;
        var items = new Dictionary<long, IntroApplyItem>();
        var lastProgress = 0d;
        void Progress(double value)
        {
            lastProgress = Math.Max(lastProgress, Math.Clamp(value, 0, 100));
            progress?.Report(lastProgress);
        }
        IntroApplyReport Report(string state) => new(targetId, state, calculation, Array.AsReadOnly(
            (calculation?.Items ?? Array.Empty<IntroDetectionItem>()).Select(i => items.GetValueOrDefault(i.ItemId) ??
                new IntroApplyItem(i.ItemId, i.Status == "AlreadyMarked" ? "AlreadyMarked" : i.Status == "NoMatch" ? "NoMatch" : i.Status == "Failed" ? "CalculationFailed" :
                    state == "Cancelled" ? "Cancelled" : "CommitFailed", null,
                    i.Status is "NoMatch" or "AlreadyMarked" ? null : i.Status == "Failed" ? i.Error : state == "Cancelled" ? "Cancelled" : "NotApplied")).ToArray()));
        try
        {
            ct.ThrowIfCancellationRequested();
            var owner = Plugin.Instance ?? throw new InvalidOperationException("Intro apply requires the current plugin owner.");
            var chapters = owner.ChapterApi ?? throw new InvalidOperationException("Intro apply requires chapter persistence.");
            var media = owner.MediaInfoApi ?? throw new InvalidOperationException("Intro apply requires media persistence.");
            var applyStart = prepare == null ? 60d : 70d;
            var prepared = prepare == null;
            calculation = await CalculateIntroCoreAsync(targetId, new IntroApplyProgress(v =>
                Progress(prepare == null ? Math.Min(59, v * .59) : prepared ? 40 + Math.Min(29, v * .29) : 0)), ct,
                FingerprintStateReset.RequireNativeClear, value => context = value, prepare == null ? null : async (selected, episodes, root, guard, t) =>
                {
                    var ready = await prepare(selected, episodes, root, guard, t).ConfigureAwait(false);
                    prepared = true;
                    return ready;
                }, fixedScope, externalAuthority, originalEpisodes, calculationTargets).ConfigureAwait(false);
            var captured = context ?? throw new InvalidOperationException("Intro calculation did not capture its physical inputs.");
            Progress(applyStart); // One explicit capture/apply boundary: legacy 60, rebuild 70.
            void Owner(CancellationToken writeToken)
            {
                ct.ThrowIfCancellationRequested(); writeToken.ThrowIfCancellationRequested();
                try
                {
                    captured.CheckOwner(writeToken);
                    if (!ReferenceEquals(owner, Plugin.Instance) || !ReferenceEquals(owner.ChapterApi, chapters) ||
                        !ReferenceEquals(owner.MediaInfoApi, media)) throw new InvalidOperationException("Intro apply owner changed.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { throw new IntroApplyAuthorityChangedException(ex); }
            }
            void Authority(CancellationToken writeToken)
            {
                Owner(writeToken);
                try
                {
                    captured.ValidateAuthority(writeToken);
                    captured.Selection.ValidateApplyRoot(owner.IntroStore.GetOptions(), writeToken);
                    Owner(writeToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (IntroApplyAuthorityChangedException) { throw; }
                catch (Exception ex) { throw new IntroApplyAuthorityChangedException(ex); }
            }
            var done = 0;
            foreach (var result in calculation.Items)
            {
                Authority(ct);
                if (result.Status == "Calculated")
                {
                    try
                    {
                        var batch = captured.Batches.Single(b => b.Season.InternalId == result.SeasonId);
                        var input = batch.Inputs.Single(i => i.Id == result.ItemId);
                        Action scopeCheck = () => { };
                        Episode[] fresh = Array.Empty<Episode>();
                        void Pure(CancellationToken writeToken)
                        {
                            Owner(writeToken); scopeCheck();
                            for (var n = 0; n < batch.Inputs.Length; n++)
                            {
                                batch.Inputs[n].Check(batch.Inputs[n].Episode);
                                if (fresh.Length != 0) batch.Inputs[n].Check(fresh[n]);
                            }
                            if (batch.Season.PresentationUniqueKey != batch.Key)
                                throw new InvalidOperationException("Frozen comparison season changed.");
                        }
                        void Guard(BaseItem _, CancellationToken writeToken)
                        {
                            Authority(writeToken);
                            captured.ValidateBatch(batch, writeToken);
                            fresh = batch.Inputs.Select(i => library.GetItemById(i.Id) as Episode ??
                                throw new InvalidOperationException("Comparison episode disappeared.")).ToArray();
                            scopeCheck = captured.Selection.CaptureApplyCheck(batch.Season, batch.Episodes,
                                owner.IntroStore.GetOptions(), batch.Inputs.Select(i => i.Duration).First(), writeToken);
                            // Finish ALL external callbacks before the final bounded content/identity proof.
                            foreach (var compared in batch.Inputs) compared.Source?.ValidateNative();
                            externalAuthority?.Invoke(writeToken); // Last host callback; close with frozen witnesses below.
                            foreach (var compared in batch.Inputs) compared.ValidateFile(paths, writeToken);
                            Pure(writeToken);
                        }
                        Guard(input.Episode, ct);
                        var receipt = await chapters.ApplyDetectedIntroAsync(input.Episode,
                            new IntroMarkerSet(result.Start!.Value.Ticks, result.End!.Value.Ticks), ct, Guard, Pure,
                            (api, db) => captured.Selection.ValidateApplyDatabase(api, db, batch.Season, batch.Key, batch.Inputs),
                            requireMissingIntro: calculationTargets != null).ConfigureAwait(false);
                        if (input.Source != null)
                        {
                            // Observe local STRM drift after the committed write/export without erasing its receipt.
                            try { Guard(input.Episode, ct); }
                            catch (Exception error) { error.Data["IntroCommitReport"] = receipt; throw; }
                        }
                        items[result.ItemId] = new(result.ItemId, receipt.Layout == "SkipConcurrentIntro" ? "AlreadyMarked" : "Applied", receipt, null);
                    }
                    catch (Exception ex)
                    {
                        var receipt = ex.Data["IntroCommitReport"] as IntroCommitReport;
                        var cancelled = ct.IsCancellationRequested || ex is OperationCanceledException;
                        var authorityChanged = ex is IntroApplyAuthorityChangedException || ex is AggregateException aggregate &&
                            aggregate.Flatten().InnerExceptions.Any(e => e is IntroApplyAuthorityChangedException);
                        items[result.ItemId] = new(result.ItemId, cancelled && receipt?.Committed != true ? "Cancelled" : "CommitFailed",
                            receipt, cancelled ? "Cancelled" : authorityChanged ? "AuthorityChanged" : "CommitFailed");
                        // An uncertain COMMIT/rollback outcome is not an ordinary isolated item failure.
                        if (cancelled || authorityChanged || receipt is { Committed: null }) throw;
                        // An ordinary layout/input/export failure is isolated; changed root/owner aborts the remainder.
                        Authority(ct);
                    }
                }
                Progress(applyStart + ++done * (100 - applyStart) / Math.Max(1, calculation.Items.Count));
                Owner(ct);
            }
            Progress(100); Owner(ct);
            return Report(calculation.Items.Any(i => i.Status == "Failed") || items.Values.Any(i => i.Status == "CommitFailed")
                ? "CompletedWithFailures" : "Completed");
        }
        catch (Exception ex)
        {
            calculation ??= ex.Data["IntroDetectionReport"] as IntroDetectionReport;
            ex.Data["IntroApplyReport"] = Report(ct.IsCancellationRequested || ex is OperationCanceledException ? "Cancelled" : "Failed");
            throw;
        }
    });

    // Synchronous forwarding, not Progress<T>: no captured UI context, delayed notifications or out-of-order phases.
    private sealed class IntroApplyProgress : IProgress<double>
    {
        private readonly Action<double> report;
        internal IntroApplyProgress(Action<double> report) => this.report = report;
        public void Report(double value) => report(value);
    }
}

internal sealed class IntroApplyAuthorityChangedException : InvalidOperationException
{
    internal IntroApplyAuthorityChangedException(Exception? inner = null) : base("Frozen intro apply authority changed.", inner) { }
}

internal sealed partial class ScopedFingerprintSelection
{
    internal void ValidateApplyRoot(IntroSkipOptions options, CancellationToken token)
    {
        try { ValidateRoot(options, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new IntroApplyAuthorityChangedException(ex); }
    }

    internal Action CaptureApplyCheck(Season season, Episode[] episodes, IntroSkipOptions options,
        TimeSpan duration, CancellationToken token)
    {
        var rootReads = new Reads(batch: true);
        var reads = new Reads(batch: true);
        var rootOptions = library.GetLibraryOptions(Read(library, root.Id, token, rootReads));
        var ownerOptions = library.GetLibraryOptions(Read(library, owner.Id, token, rootReads));
        var episodeOptions = episodes.Select(e => library.GetLibraryOptions(e)).ToArray();
        var seasonOptions = library.GetLibraryOptions(season);
        try { ValidateRoot(options, token, rootReads); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new IntroApplyAuthorityChangedException(ex); }
        Guard(season, options, token, reads);
        foreach (var episode in episodes) Guard(episode, options, token, reads);
        void Check()
        {
            try
            {
                rootReads.Check(final: true);
                ValidateMarkerAcceptance(options);
                if (!rootOptions.EnableMarkerDetection || !ownerOptions.EnableMarkerDetection ||
                    !options.UnlockIntroSkip || ownerOptions.ContentType is { Length: > 0 } type && !type.Equals("tvshows", StringComparison.OrdinalIgnoreCase))
                    throw new IntroApplyAuthorityChangedException();
            }
            catch (IntroApplyAuthorityChangedException) { throw; }
            catch (Exception ex) { throw new IntroApplyAuthorityChangedException(ex); }
            reads.Check(final: true);
            var minutes = seasonOptions.IntroDetectionFingerprintLength;
            if (minutes < 2 || minutes >= 30) minutes = 10;
            if (!seasonOptions.EnableMarkerDetection || episodeOptions.Any(o => !o.EnableMarkerDetection) || duration != TimeSpan.FromMinutes(minutes))
                throw new InvalidOperationException("Frozen comparison settings changed.");
        }
        Check(); token.ThrowIfCancellationRequested();
        // Only retained entities/options are examined under the writer. No manager, store lock, files or callbacks.
        return Check;
    }

    // Same bounded physical-chain template as NativeEpisodeScopeTransition, deliberately local to this partial:
    // old calculation-only source-linked consumers do not acquire the unrelated userdata transition dependency.
    private const string ApplyBadChainSql = "with recursive chain(Id,ParentId,Depth,Trail,Cycle) as (select Id,ParentId,0,','||Id||',',0 from main.MediaItems where Id=? union all select p.Id,p.ParentId,c.Depth+1,c.Trail||p.Id||',',instr(c.Trail,','||p.Id||',')>0 from chain c join main.MediaItems p on p.Id=c.ParentId where c.ParentId>0 and c.Depth<128 and c.Cycle=0) select count(*) from chain where Cycle=1 or ParentId<0 or (Depth=128 and ParentId>0) or (ParentId>0 and not exists(select 1 from main.MediaItems p where p.Id=chain.ParentId))";
    private const string ApplyChainSql = "with recursive chain(Id,ParentId,Depth) as (select Id,ParentId,0 from main.MediaItems where Id=? union all select p.Id,p.ParentId,c.Depth+1 from chain c join main.MediaItems p on p.Id=c.ParentId where c.ParentId>0 and c.Depth<128) ";

    internal void ValidateApplyDatabase(NativeUserDataCopy.Api api, object db, Season season, string seasonKey,
        IReadOnlyList<NativeIntroDetection.Input> inputs)
    {
        ValidateApplyTables(api, db);
        try
        {
            Chain(root, rootParents);
            if (api.Scalar(db, "select count(*) from main.MediaItems where Id=? and Type=4 and typeof(guid)='blob' and hex(guid)=?", owner.Id, Convert.ToHexString(owner.Guid.ToByteArray())) != 1)
                throw new IntroApplyAuthorityChangedException();
            Membership(root.Id);
        }
        catch (IntroApplyAuthorityChangedException) { throw; }
        catch (Exception ex) { throw new IntroApplyAuthorityChangedException(ex); }
        foreach (var input in inputs)
        {
            if (!candidates.TryGetValue(input.Id, out var expected)) throw new InvalidOperationException("Unselected comparison input.");
            Membership(input.Id);
            Chain(expected.Item, expected.Parents);
            if (!expected.Parents.Contains(root)) throw new IntroApplyAuthorityChangedException();
            if (api.Scalar(db, "select count(*) from main.MediaItems where Id=? and PresentationUniqueKey=? COLLATE BINARY and RunTimeTicks=? and DateCreated=? and coalesce(SeriesId,0)=? and coalesce(AlbumId,0)=? and coalesce(ExternalId,'')=''", input.Id, input.Key, input.RuntimeTicks, input.Created.ToUnixTimeSeconds(), expected.Item.Series, expected.Item.Album) != 1)
                throw new InvalidOperationException("Native comparison input changed.");
        }
        if (api.Scalar(db, "select count(*) from main.MediaItems where Id=? and Type=7 and PresentationUniqueKey=? COLLATE BINARY", season.InternalId, seasonKey) != 1)
            throw new InvalidOperationException("Native comparison season changed.");

        void Membership(long id)
        {
            // Match native unique CF ownership: one CF may legitimately link multiple physical ancestors.
            // CF path may be virtual/null; ID/GUID/type and actual Type8 membership are the authority.
            var memberships = api.Rows(db, ApplyChainSql + "select distinct cast(c.Id as text) from main.MediaItems c join main.ItemLinks2 l on l.ItemId=c.Id and l.Type=8 where c.Type=4 and l.LinkedId in (select Id from chain) limit 2", 1, id);
            if (memberships.Count != 1 || memberships[0][0] != owner.Id.ToString(CultureInfo.InvariantCulture))
                throw new IntroApplyAuthorityChangedException();
        }
        void Chain(Identity item, Identity[] parents)
        {
            if (api.Scalar(db, ApplyBadChainSql, item.Id) != 0) throw new InvalidOperationException("Invalid physical parent chain.");
            var rows = api.Rows(db, ApplyChainSql + "select cast(m.Id as text),typeof(m.guid),hex(m.guid),cast(coalesce(m.ParentId,0) as text),cast(m.Type as text),m.Path,cast(coalesce(m.SeriesId,0) as text),cast(coalesce(m.AlbumId,0) as text),case when m.IndexNumber is null then 'null' else 'value:'||m.IndexNumber end,case when m.ParentIndexNumber is null then 'null' else 'value:'||m.ParentIndexNumber end,cast(m.DateCreated as text),coalesce(m.ExternalId,'') from chain c join main.MediaItems m on m.Id=c.Id order by c.Depth", 12, item.Id);
            var expected = new[] { item }.Concat(parents).ToArray();
            if (rows.Count != expected.Length) throw new InvalidOperationException("Physical parent chain length changed.");
            for (var n = 0; n < expected.Length; n++)
            {
                var e = expected[n]; var r = rows[n];
                var type = e.Type == typeof(Episode) ? "8" : e.Type == typeof(Season) ? "7" : e.Type == typeof(Series) ? "6" : e.Type == typeof(CollectionFolder) ? "4" : null;
                if (r[0] != e.Id.ToString(CultureInfo.InvariantCulture) || r[1] != "blob" || r[2] != Convert.ToHexString(e.Guid.ToByteArray()) ||
                    r[3] != e.Parent.ToString(CultureInfo.InvariantCulture) || type != null && r[4] != type ||
                    type is "6" or "7" or "8" && (r[5] != e.Path || r[6] != e.Series.ToString(CultureInfo.InvariantCulture) ||
                        r[7] != e.Album.ToString(CultureInfo.InvariantCulture) || r[8] != (e.Index.HasValue ? "value:" + e.Index.Value.ToString(CultureInfo.InvariantCulture) : "null") ||
                        r[9] != (e.ParentIndex.HasValue ? "value:" + e.ParentIndex.Value.ToString(CultureInfo.InvariantCulture) : "null") ||
                        r[10] != e.Created.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) || r[11] != (e.External ?? "")))
                    throw new InvalidOperationException("Native frozen physical identity changed.");
            }
        }
    }

    private static void ValidateApplyTables(NativeUserDataCopy.Api api, object db)
    {
        foreach (var name in new[] { "MediaItems", "ItemLinks2" })
        {
            var shape = api.Rows(db, "PRAGMA main.table_list('" + name + "')", 6);
            if (shape.Count != 1 || shape[0][0] != "main" || shape[0][2] != "table" ||
                api.Scalar(db, "select count(*) from main.sqlite_master where type='table' and name=? COLLATE BINARY", name) != 1)
                throw new IntroApplyAuthorityChangedException();
            var columns = api.Rows(db, "PRAGMA main.table_xinfo('" + name + "')", 7).ToDictionary(r => r[1], StringComparer.OrdinalIgnoreCase);
            var required = name == "ItemLinks2" ? new[] { "ItemId", "LinkedId", "Type", "LinkOrder" } :
                new[] { "Id", "guid", "Type", "ParentId", "Path", "SeriesId", "AlbumId", "DateCreated", "RunTimeTicks", "ExternalId", "PresentationUniqueKey", "IndexNumber", "ParentIndexNumber" };
            foreach (var field in required)
            {
                if (!columns.TryGetValue(field, out var column) || column[6] != "0") throw new IntroApplyAuthorityChangedException();
                var type = column[2].ToUpperInvariant();
                if (field == "guid" ? type != "BLOB" : field is "Path" or "ExternalId" or "PresentationUniqueKey" ? type != "TEXT" : type is not ("INT" or "INTEGER"))
                    throw new IntroApplyAuthorityChangedException();
            }
            if (name == "MediaItems" && (columns["Id"][5] != "1" || columns.Values.Count(r => r[5] != "0") != 1) ||
                name == "ItemLinks2" && (columns["ItemId"][5] != "1" || columns["Type"][5] != "2" || columns["LinkedId"][5] != "3" ||
                    columns.Values.Count(r => r[5] != "0") != 3))
                throw new IntroApplyAuthorityChangedException();
        }
        const string triggers = " where type='trigger' and tbl_name COLLATE NOCASE in ('MediaItems','ItemLinks2')";
        if (api.Scalar(db, "select count(*) from main.sqlite_master" + triggers) != 0 ||
            api.Scalar(db, "select count(*) from sqlite_temp_master" + triggers) != 0)
            throw new IntroApplyAuthorityChangedException();
    }
}
