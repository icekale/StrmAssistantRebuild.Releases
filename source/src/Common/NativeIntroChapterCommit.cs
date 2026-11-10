using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using StrmAssistantRebuild.IntroSkip;

namespace StrmAssistantRebuild.Common;

public sealed record IntroCommitReport(long ItemId, bool? Committed, int BeforeIntroCount, int RemovedRows,
    int InsertedRows, int RetainedCount, int IntroStartIndex, int IntroEndIndex, int FailureClearedRows, string Layout);

public sealed partial class ChapterApi
{
    /// <summary>One physical episode only. Retained chapter rows are never projected, rewritten or renumbered.
    /// Authority callbacks run before the native writer; only frozen memory checks run inside it.
    /// A post-commit persistence/disposal error carries Data["IntroCommitReport"] with Committed=true.</summary>
    public Task<IntroCommitReport> ApplyDetectedIntroAsync(Episode item, IntroMarkerSet markers,
        CancellationToken token, Action<BaseItem, CancellationToken>? validateAuthority = null) =>
        ApplyDetectedIntroAsync(item, markers, token, validateAuthority, null, null);

    // Internal orchestration supplies only a frozen-memory check and same-connection SQL proof.
    internal async Task<IntroCommitReport> ApplyDetectedIntroAsync(Episode item, IntroMarkerSet markers,
        CancellationToken token, Action<BaseItem, CancellationToken>? validateAuthority,
        Action<CancellationToken>? checkFrozen, Action<NativeUserDataCopy.Api, object>? validateScope,
        bool requireMissingIntro = false)
    {
        ArgumentNullException.ThrowIfNull(item); ArgumentNullException.ThrowIfNull(markers);
        token.ThrowIfCancellationRequested();
        var initial = NativeIntroChapterCommit.Identity.Capture(item, markers);
        IntroCommitReport? receipt = null;
        try
        {
            return await queue.RunSingle(initial.Id, async ct =>
            {
                var owner = Plugin.Instance ?? throw new InvalidOperationException("Intro commit requires the current plugin owner.");
                var media = owner.MediaInfoApi ?? throw new InvalidOperationException("Media persistence is not initialized.");
                var configuration = BaseItem.ConfigurationManager;
                var databasePath = owner.Paths.RequireWritablePath(Path.Combine(configuration.ApplicationPaths.DataPath, "library.db"));
                void Pure(CancellationToken writeToken)
                {
                    ct.ThrowIfCancellationRequested(); writeToken.ThrowIfCancellationRequested();
                    initial.Check(item);
                    if (!ReferenceEquals(Plugin.Instance, owner) || !ReferenceEquals(owner.ChapterApi, this) ||
                        !ReferenceEquals(owner.WorkQueue, queue) || !ReferenceEquals(owner.MediaInfoApi, media) ||
                        !ReferenceEquals(BaseItem.ItemRepository, repository) || !ReferenceEquals(BaseItem.ConfigurationManager, configuration))
                        throw new InvalidOperationException("Intro commit authority changed.");
                    checkFrozen?.Invoke(writeToken);
                }
                void Guard(CancellationToken writeToken)
                {
                    Pure(writeToken); validateAuthority?.Invoke(item, writeToken); Pure(writeToken);
                    if (owner.Paths.RequireWritablePath(Path.Combine(configuration.ApplicationPaths.DataPath, "library.db")) != databasePath)
                        throw new InvalidOperationException("Intro commit database path changed.");
                }
                Guard(ct);
                // This fast read avoids the persistence wrapper for a known skip. The writer repeats it atomically.
                if (requireMissingIntro)
                {
                    var existing = repository.GetChapters(item, ct);
                    var intro = existing.Count(c => c.MarkerType is MediaBrowser.Model.Entities.MarkerType.IntroStart or MediaBrowser.Model.Entities.MarkerType.IntroEnd);
                    Guard(ct);
                    if (intro != 0) return new IntroCommitReport(initial.Id, false, intro, 0, 0, existing.Count - intro, -1, -1, 0, "SkipConcurrentIntro");
                }
                await media.ApplyAndPersistAsync(item, writeToken =>
                {
                    Guard(writeToken);
                    receipt = NativeIntroChapterCommit.Commit(repository, owner.Paths, databasePath, initial, markers,
                        writeToken, () => Pure(writeToken), validateScope, requireMissingIntro);
                    return Task.CompletedTask;
                }, ct, invalidateExistingCache: true).ConfigureAwait(false);
                // No cancellation check here: SQLite COMMIT is the outcome boundary, not cache export.
                return receipt ?? throw new InvalidOperationException("Intro persistence did not invoke its mutation.");
            }, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (receipt != null) error.Data["IntroCommitReport"] = receipt;
            throw;
        }
    }
}

/// <summary>Bounded chapter-only transaction over the verified host's existing writer pool.</summary>
internal static class NativeIntroChapterCommit
{
    [ThreadStatic] private static bool writing;
    private const string Retained = "(MarkerType IS NOT 1 AND MarkerType IS NOT 2)";
    internal sealed record Identity(long Id, Guid Guid, string Path, long Parent, long Series, long Album,
        DateTimeOffset Created, long Runtime, int? Index, int? ParentIndex)
    {
        internal static Identity Capture(Episode item, IntroMarkerSet markers)
        {
            if (item.GetType() != typeof(Episode) || item.InternalId <= 0 || item.Id == Guid.Empty || item.RunTimeTicks is not > 0 ||
                item.IsVirtualItem || !string.IsNullOrEmpty(item.ExternalId) ||
                string.IsNullOrWhiteSpace(item.Path) || !System.IO.Path.IsPathRooted(item.Path))
                throw new ArgumentException("Intro commit requires a persisted local physical episode with a runtime and GUID.");
            if (!markers.HasIntro || markers.CreditsStartTicks.HasValue) throw new ArgumentException("Only an intro pair is accepted.");
            markers.Validate(item.RunTimeTicks);
            var snapshot = new Identity(item.InternalId, item.Id, item.Path, item.ParentId, item.SeriesId, item.InternalAlbumId,
                item.DateCreated, item.RunTimeTicks.Value, item.IndexNumber, item.ParentIndexNumber);
            if (!item.IsFileProtocol) throw new ArgumentException("Intro commit requires a local file protocol.");
            snapshot.Check(item); // A protocol manager callback must not retarget the initial capture.
            return snapshot;
        }
        internal void Check(Episode item)
        {
            // IsFileProtocol calls the host media manager; never evaluate it under a native writer lease.
            if (item.InternalId != Id || item.Id != Guid || item.Path != Path || item.ParentId != Parent ||
                item.SeriesId != Series || item.InternalAlbumId != Album || item.DateCreated != Created ||
                item.RunTimeTicks != Runtime || item.IndexNumber != Index || item.ParentIndexNumber != ParentIndex ||
                item.IsVirtualItem || !string.IsNullOrEmpty(item.ExternalId))
                throw new InvalidOperationException("Intro episode identity changed.");
        }
    }

    internal static IntroCommitReport Commit(IItemRepository repository, EmbyPathPolicy paths, string databasePath,
        Identity item, IntroMarkerSet markers, CancellationToken token, Action checkFrozen,
        Action<NativeUserDataCopy.Api, object>? validateScope = null, bool requireMissingIntro = false)
    {
        if (writing) throw new InvalidOperationException("Nested intro transactions are not supported.");
        var expected = paths.RequireWritablePath(databasePath);
        if (Path.GetFileName(expected) != "library.db") throw new ArgumentException("The native library database is required.");
        token.ThrowIfCancellationRequested(); checkFrozen();
        writing = true;
        var receipt = new IntroCommitReport(item.Id, false, 0, 0, 0, 0, -1, -1, 0, "Unchanged");
        try
        {
            // Api admits only the exact SqliteItemRepository type; hashes cover the whole reflection/SQL contract.
            var api = new NativeUserDataCopy.Api(repository);
            Pin(repository.GetType().Assembly, "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F");
            Pin(api.Pool.GetType().Assembly, "4AB4A81A995388710900EE787198A5CD4742F2BEED093D89A56EC8A1ED58D2DA");
            Pin(api.ConnectionType.Assembly, "0AF3DA72E8B1D9586C19450DEB3EAD41C510055F695E169B9B99F25B901B68C5");
            using var db = (IDisposable)Invoke(api.Create, api.Pool, new object?[] { false, token })!;
            if (db.GetType() != api.ConnectionType) throw new NotSupportedException("Native connection implementation differs.");
            object?[] file = { "main", null };
            if (!(bool)Invoke(api.FileName, db, file)! || file[1] is not string actual ||
                !string.Equals(paths.RequireWritablePath(actual), expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("Native connection is not the expected library database.");
            if ((bool)api.ReadOnly.GetValue(db)! || !(bool)api.AutoCommit.GetValue(db)!) throw new InvalidOperationException("Independent writable native connection required.");
            token.ThrowIfCancellationRequested(); checkFrozen();
            try { receipt = Transact(api, db, item, markers, token, checkFrozen, validateScope, requireMissingIntro); }
            catch (Exception error)
            {
                if (error.Data["IntroCommitReport"] is IntroCommitReport outcome) receipt = outcome;
                throw;
            }
            return receipt;
        }
        catch (Exception error)
        {
            if (error.Data["IntroCommitReport"] == null) error.Data["IntroCommitReport"] = receipt;
            throw;
        }
        finally { writing = false; }
    }

    private static IntroCommitReport Transact(NativeUserDataCopy.Api api, object db, Identity item,
        IntroMarkerSet markers, CancellationToken token, Action checkFrozen, Action<NativeUserDataCopy.Api, object>? validateScope, bool requireMissingIntro)
    {
        var receipt = new IntroCommitReport(item.Id, false, 0, 0, 0, 0, -1, -1, 0, "Unchanged");
        try
        {
            if (!markers.HasIntro || markers.CreditsStartTicks.HasValue || item.Runtime <= 0) throw new ArgumentException("Only a bounded intro pair is accepted.");
            markers.Validate(item.Runtime);
            token.ThrowIfCancellationRequested(); checkFrozen();
            var commitAttempted = false;
            try
            {
                api.Execute(db, "BEGIN IMMEDIATE");
                ValidateSchema(api, db);
                Witness(api, db, item);
                var count = api.Scalar(db, "select count(*) from main.Chapters3 where ItemId=?", item.Id);
                var kept = api.Scalar(db, "select count(*) from main.Chapters3 where ItemId=? and " + Retained, item.Id);
                if (requireMissingIntro && count != kept)
                {
                    receipt = receipt with { BeforeIntroCount = checked((int)(count - kept)), RetainedCount = checked((int)kept), Layout = "SkipConcurrentIntro" };
                    validateScope?.Invoke(api, db); checkFrozen(); token.ThrowIfCancellationRequested();
                    api.Execute(db, "ROLLBACK");
                    return receipt; // No DELETE/INSERT/failure clear. Earlier persistence invalidation may already have happened.
                }
                if (count > 10000 || kept > 9998) throw new InvalidOperationException("Chapter row budget exceeded.");
                // GetChapters assigns DTO ordinal indexes; GetChapter looks up the physical key. Exactly two holes
                // in a final dense 0..N+1 layout preserve both contracts without rewriting any retained row.
                if (api.Scalar(db, "select count(*) from main.Chapters3 where ItemId=? and " + Retained + " and (ChapterIndex<0 or ChapterIndex>?)", item.Id, kept + 1) != 0)
                    throw new NotSupportedException("Retained chapter layout does not admit two dense intro slots.");
                var slots = api.Rows(db, "with recursive slots(i) as (select 0 union all select i+1 from slots where i<?) select cast(i as text) from slots where not exists(select 1 from main.Chapters3 c where c.ItemId=? and c.ChapterIndex=i and " + Retained + ") order by i", 1, kept + 1, item.Id);
                if (slots.Count != 2) throw new NotSupportedException("Expected exactly two unoccupied chapter slots.");
                var start = int.Parse(slots[0][0], System.Globalization.CultureInfo.InvariantCulture);
                var end = int.Parse(slots[1][0], System.Globalization.CultureInfo.InvariantCulture);
                const string failureName = "IntroDetectionExtradata";
                if (api.Scalar(db, "select count(*) from main.ItemExtradataTypes where Name=? COLLATE NOCASE", failureName) > 1)
                    throw new NotSupportedException("Native intro failure type name is ambiguous.");
                receipt = receipt with { BeforeIntroCount = (int)(count - kept), RetainedCount = (int)kept, IntroStartIndex = start, IntroEndIndex = end, Layout = start == kept && end == kept + 1 ? "Append" : "ReuseHoles" };
                token.ThrowIfCancellationRequested(); checkFrozen();
                validateScope?.Invoke(api, db);
                token.ThrowIfCancellationRequested();
                api.Execute(db, "delete from main.Chapters3 where ItemId=? and MarkerType in (1,2)", item.Id);
                var removed = (int)api.Changes.GetValue(db)!;
                if (removed != count - kept) throw new InvalidOperationException("Intro deletion count changed.");
                const string insert = "insert into main.Chapters3(ItemId,ChapterIndex,StartPositionTicks,Name,ImagePath,ImageDateModified,MarkerType) values(?,?,?,?,NULL,-62135596800,?)";
                api.Execute(db, insert, item.Id, (long)start, markers.IntroStartTicks!.Value, "IntroStart#SA", 1L);
                if ((int)api.Changes.GetValue(db)! != 1) throw new InvalidOperationException("Intro start was not inserted.");
                api.Execute(db, insert, item.Id, (long)end, markers.IntroEndTicks!.Value, "IntroEnd#SA", 2L);
                if ((int)api.Changes.GetValue(db)! != 1) throw new InvalidOperationException("Intro end was not inserted.");
                api.Execute(db, "delete from main.ItemExtradata where ItemId=? and ExtradataTypeId in (select ExtradataTypeId from main.ItemExtradataTypes where Name=? COLLATE NOCASE)", item.Id, failureName);
                var cleared = (int)api.Changes.GetValue(db)!;
                var result = new IntroCommitReport(item.Id, true, removed, removed, 2, (int)kept, start, end, cleared, start == kept && end == kept + 1 ? "Append" : "ReuseHoles");
                token.ThrowIfCancellationRequested();
                validateScope?.Invoke(api, db);
                checkFrozen(); token.ThrowIfCancellationRequested();
                commitAttempted = true;
                api.Execute(db, "COMMIT");
                receipt = result;
            }
            catch (Exception primary)
            {
                // COMMIT may succeed but its statement disposal may throw. Unknown is not a rollback claim.
                if (commitAttempted) receipt = receipt with { Committed = null };
                try
                {
                    if (!(bool)api.AutoCommit.GetValue(db)!)
                    {
                        api.Execute(db, "ROLLBACK");
                        receipt = receipt with { Committed = false };
                    }
                }
                catch (Exception rollback)
                {
                    receipt = receipt with { Committed = null };
                    throw new AggregateException("Intro transaction failed and rollback did not complete.", primary, rollback);
                }
                throw;
            }
            return receipt;
        }
        catch (Exception error)
        {
            error.Data["IntroCommitReport"] = receipt;
            throw;
        }
    }

    private static void Witness(NativeUserDataCopy.Api api, object db, Identity item)
    {
        if (api.Scalar(db, "select count(*) from main.MediaItems where Id=? and typeof(guid)='blob' and hex(guid)=? and type=8 and Path=? COLLATE BINARY and coalesce(ParentId,0)=? and coalesce(SeriesId,0)=? and coalesce(AlbumId,0)=? and DateCreated=? and RunTimeTicks=? and coalesce(ExternalId,'')='' and ((IndexNumber is null and ?=0) or (IndexNumber=? and ?=1)) and ((ParentIndexNumber is null and ?=0) or (ParentIndexNumber=? and ?=1))",
            item.Id, Convert.ToHexString(item.Guid.ToByteArray()), item.Path, item.Parent, item.Series, item.Album, item.Created.ToUnixTimeSeconds(), item.Runtime,
            item.Index.HasValue ? 1L : 0L, (long)item.Index.GetValueOrDefault(), item.Index.HasValue ? 1L : 0L,
            item.ParentIndex.HasValue ? 1L : 0L, (long)item.ParentIndex.GetValueOrDefault(), item.ParentIndex.HasValue ? 1L : 0L) != 1)
            throw new InvalidOperationException("Native episode identity witness differs.");
    }
    private static void ValidateSchema(NativeUserDataCopy.Api api, object db)
    {
        void Table(string name, string[] names, string[] types, int[] keys, string nulls, string[] foreignKeys)
        {
            var rows = api.Rows(db, "PRAGMA main.table_xinfo('" + name + "')", 7);
            if (rows.Count != names.Length || rows.Where((r, i) => r[1] != names[i] || !string.Equals(r[2], types[i], StringComparison.OrdinalIgnoreCase) ||
                r[3] != nulls[i].ToString() || r[4] != "" || r[5] != keys[i].ToString(System.Globalization.CultureInfo.InvariantCulture) || r[6] != "0").Any())
                throw new NotSupportedException("Native chapter schema differs: " + name);
            var shape = api.Rows(db, "PRAGMA main.table_list('" + name + "')", 6);
            if (shape.Count != 1 || shape[0][0] != "main" || shape[0][2] != "table" || shape[0][4] != (name == "ItemExtradataTypes" ? "0" : "1") || shape[0][5] != "1")
                throw new NotSupportedException("Native strict table layout differs: " + name);
            var links = api.Rows(db, "PRAGMA main.foreign_key_list('" + name + "')", 8)
                .Select(r => string.Join("|", r.Skip(2))).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (!links.SequenceEqual(foreignKeys.OrderBy(x => x, StringComparer.Ordinal))) throw new NotSupportedException("Native foreign keys differ: " + name);
            var indexes = api.Rows(db, "PRAGMA main.index_list('" + name + "')", 5);
            var primary = indexes.Where(r => r[3] == "pk").ToArray();
            if (indexes.Count != 1 || primary.Length != (name == "ItemExtradataTypes" ? 0 : 1) || indexes.Any(r => r[2] == "1" && r[3] != "pk") ||
                (name == "ItemExtradataTypes" && (indexes[0][1] != "idx_ItemExtradataTypes_Name" || indexes[0][2] != "0" || indexes[0][3] != "c" || indexes[0][4] != "0")))
                throw new NotSupportedException("Native unique indexes differ: " + name);
            foreach (var index in primary)
            {
                if (index[2] != "1" || index[4] != "0") throw new NotSupportedException("Native primary index differs.");
                var columns = api.Rows(db, "PRAGMA main.index_xinfo('" + index[1].Replace("'", "''") + "')", 6).Where(r => r[5] == "1").ToArray();
                var expected = names.Where((_, i) => keys[i] != 0).ToArray();
                if (!columns.Select(r => r[2]).SequenceEqual(expected) || columns.Any(r => r[3] != "0" || r[4] != "BINARY")) throw new NotSupportedException("Native primary key ordering differs.");
            }
        }
        Table("Chapters3", new[] { "ItemId", "ChapterIndex", "StartPositionTicks", "Name", "ImagePath", "ImageDateModified", "MarkerType" }, new[] { "INT", "INT", "INTEGER", "TEXT", "TEXT", "INT", "INT" }, new[] { 1, 2, 0, 0, 0, 0, 0 }, "1110001", new[] { "MediaItems|ItemId|Id|NO ACTION|NO ACTION|NONE" });
        Table("ItemExtradata", new[] { "ItemId", "ExtradataTypeId", "Value" }, new[] { "INT", "INT", "TEXT" }, new[] { 1, 2, 0 }, "111", new[] { "MediaItems|ItemId|Id|NO ACTION|NO ACTION|NONE", "ItemExtradataTypes|ExtradataTypeId|ExtradataTypeId|NO ACTION|NO ACTION|NONE" });
        Table("ItemExtradataTypes", new[] { "ExtradataTypeId", "Name" }, new[] { "INTEGER", "TEXT" }, new[] { 1, 0 }, "01", Array.Empty<string>());
        var typeIndex = api.Rows(db, "PRAGMA main.index_xinfo('idx_ItemExtradataTypes_Name')", 6).Where(r => r[5] == "1").ToArray();
        if (typeIndex.Length != 1 || typeIndex[0][2] != "Name" || typeIndex[0][3] != "0" || typeIndex[0][4] != "NOCASE")
            throw new NotSupportedException("Native extradata name index differs.");
        // Pin the three bounded CREATE statements too: PRAGMA alone omits custom CHECK clauses/collations.
        var ddl = new[] {
            "create table Chapters3(ItemId INT,ChapterIndex INT NOT NULL,StartPositionTicks INTEGER NOT NULL,Name TEXT,ImagePath TEXT,ImageDateModified INT,MarkerType INT NOT NULL,PRIMARY KEY(ItemId,ChapterIndex),FOREIGN KEY(ItemId) REFERENCES MediaItems(Id)) STRICT, WITHOUT ROWID",
            "create table ItemExtradata(ItemId INT NOT NULL,ExtradataTypeId INT NOT NULL,Value TEXT NOT NULL,PRIMARY KEY(ItemId,ExtradataTypeId),FOREIGN KEY(ItemId) REFERENCES MediaItems(Id),FOREIGN KEY(ExtradataTypeId) REFERENCES ItemExtradataTypes(ExtradataTypeId)) STRICT, WITHOUT ROWID",
            "create table ItemExtradataTypes(ExtradataTypeId INTEGER PRIMARY KEY AUTOINCREMENT,Name TEXT NOT NULL COLLATE NOCASE) STRICT" };
        static string Normalize(string sql) => string.Concat(sql.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant().Replace("IFNOTEXISTS", "");
        var actualDdl = api.Rows(db, "select sql from main.sqlite_master where type='table' and name in ('Chapters3','ItemExtradata','ItemExtradataTypes')", 1).Select(r => Normalize(r[0])).OrderBy(s => s, StringComparer.Ordinal);
        if (!actualDdl.SequenceEqual(ddl.Select(Normalize).OrderBy(s => s, StringComparer.Ordinal))) throw new NotSupportedException("Native chapter CREATE contract differs.");
        var media = api.Rows(db, "PRAGMA main.table_info('MediaItems')", 6).ToDictionary(r => r[1], StringComparer.OrdinalIgnoreCase);
        foreach (var field in new[] { "Id", "guid", "type", "Path", "ParentId", "SeriesId", "AlbumId", "DateCreated", "RunTimeTicks", "ExternalId", "IndexNumber", "ParentIndexNumber" })
            if (!media.ContainsKey(field)) throw new NotSupportedException("Native identity column missing: " + field);
        if (media["Id"][5] != "1" || !media["guid"][2].Equals("BLOB", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Native identity primary key/GUID schema differs.");
        const string targets = " where type='trigger' and tbl_name COLLATE NOCASE in ('Chapters3','ItemExtradata','ItemExtradataTypes')";
        if (api.Scalar(db, "select count(*) from main.sqlite_master" + targets) != 0 || api.Scalar(db, "select count(*) from sqlite_temp_master" + targets) != 0)
            throw new NotSupportedException("Unknown chapter/extradata triggers require review.");
    }
    private static void Pin(Assembly assembly, string hash)
    {
        if (string.IsNullOrEmpty(assembly.Location)) throw new NotSupportedException("Native assembly file is missing.");
        using var file = File.OpenRead(assembly.Location);
        using var sha = SHA256.Create();
        if (Convert.ToHexString(sha.ComputeHash(file)) != hash) throw new NotSupportedException("Native chapter assembly artifact differs.");
    }
    private static object? Invoke(MethodInfo method, object instance, object?[] args)
    {
        try { return method.Invoke(instance, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
