using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Library;

namespace StrmAssistantRebuild.Common;

/// <summary>Explicit migration intent/recovery ledger; no startup, OFF or global option wiring.</summary>
public sealed class EpisodeScopeJournal
{
    private const int MaxBytes=16*1024*1024,MaxEntries=4096;
    private readonly EmbyPathPolicy paths;
    private readonly string file;
    public sealed record Entry(Guid OperationId,string Phase,EpisodeLibraryScopePlan.Row Plan);
    public sealed record LibraryBinding(long LibraryId,Guid LibraryGuid);
    private sealed record Ledger(int Format,long Revision,Entry[] Entries,
        [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] bool ResumeOnStartup=false,
        [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] LibraryBinding[]? StartupCatchupLibraries=null);
    public sealed record Observation(Guid OperationId,long ItemId,string State,string Reason);
    public sealed record Recovery(int Retained,Observation[] Observations);
    public EpisodeScopeJournal(EmbyPathPolicy paths,string filePath)
    {
        ArgumentNullException.ThrowIfNull(paths);this.paths=paths;file=paths.RequireWritablePath(filePath);
        if(!file.EndsWith(".json",StringComparison.Ordinal))throw new ArgumentException("A journal JSON path is required.");
    }
    /// <summary>Explicit opt-in for recovery of this known journal at a future server startup, not global migration.</summary>
    public void SetStartupRecovery(bool enabled,CancellationToken token)
    {
        using var held=Acquire(token);var ledger=Load();
        if(enabled&&ledger.Entries.Length==0)throw new InvalidOperationException("Startup recovery requires an existing journal intent.");
        if(ledger.ResumeOnStartup!=enabled)Save(ledger with{ResumeOnStartup=enabled},token);
    }
    public bool StartupRecoveryRequested(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!File.Exists(paths.RequireWritablePath(file))&&!File.Exists(paths.RequireWritablePath(file+".bak")))return false;
        using var held=Acquire(token);return Load().ResumeOnStartup;
    }
    /// <summary>Future startup selection only. Does not configure or disable any live catch-up generation.</summary>
    public void SetStartupCatchupLibraries(IEnumerable<LibraryBinding>? bindings,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var selected=ValidateBindings(bindings,token);
        using var held=Acquire(token);var ledger=Load();
        if(selected.Length!=0&&(!ledger.ResumeOnStartup||selected.Any(b=>!ledger.Entries.Any(e=>e.Plan.LibraryId==b.LibraryId))))
            throw new InvalidOperationException("Startup catch-up requires enabled startup recovery and an existing journal entry for every selected library.");
        if(!selected.SequenceEqual(ValidateBindings(ledger.StartupCatchupLibraries,token)))
            Save(ledger with{StartupCatchupLibraries=selected.Length==0?null:selected},token);
    }
    public IReadOnlyList<LibraryBinding> GetStartupCatchupLibraries(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!File.Exists(paths.RequireWritablePath(file))&&!File.Exists(paths.RequireWritablePath(file+".bak")))return Array.AsReadOnly(Array.Empty<LibraryBinding>());
        using var held=Acquire(token);return Array.AsReadOnly(ValidateBindings(Load().StartupCatchupLibraries,token));
    }
    /// <summary>Detached configuration baseline; entry facts inform the editor but are not a revision lock.</summary>
    public sealed class StartupSettingsSnapshot
    {
        internal StartupSettingsSnapshot(bool resume,LibraryBinding[] bindings,long[] libraryIds,int entryCount)
        { ResumeOnStartup=resume;Bindings=Array.AsReadOnly(bindings.ToArray());JournalLibraryIds=Array.AsReadOnly(libraryIds.ToArray());EntryCount=entryCount; }
        public bool ResumeOnStartup { get; }
        public IReadOnlyList<LibraryBinding> Bindings { get; }
        public IReadOnlyList<long> JournalLibraryIds { get; }
        public int EntryCount { get; }
    }
    private bool HasStoredJournal()=>File.Exists(paths.RequireWritablePath(file))||File.Exists(paths.RequireWritablePath(file+".bak"));
    private static StartupSettingsSnapshot StartupSnapshot(Ledger ledger,CancellationToken token)=>new(ledger.ResumeOnStartup,ValidateBindings(ledger.StartupCatchupLibraries,token),ledger.Entries.Select(e=>e.Plan.LibraryId).Distinct().OrderBy(id=>id).ToArray(),ledger.Entries.Length);
    public StartupSettingsSnapshot ReadStartupSettings(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!HasStoredJournal())return StartupSnapshot(new Ledger(1,0,Array.Empty<Entry>()),token);
        using var held=Acquire(token);return StartupSnapshot(Load(),token);
    }
    /// <summary>Atomic future-startup configuration only; does not invoke recovery or alter live scope.</summary>
    public StartupSettingsSnapshot SaveIfUnchanged(StartupSettingsSnapshot baseline,bool resume,IEnumerable<LibraryBinding>? bindings,CancellationToken token,Action<IReadOnlyList<LibraryBinding>>? validateBindings=null)
    {
        ArgumentNullException.ThrowIfNull(baseline);token.ThrowIfCancellationRequested();var selected=ValidateBindings(bindings,token);
        // A missing default journal stays missing. No lease/file is needed for this disabled no-op.
        if(!resume&&selected.Length==0&&!baseline.ResumeOnStartup&&baseline.Bindings.Count==0&&!HasStoredJournal())
            return StartupSnapshot(new Ledger(1,0,Array.Empty<Entry>()),token);
        using var held=Acquire(token);var ledger=Load(out var original);var current=ValidateBindings(ledger.StartupCatchupLibraries,token);
        if(ledger.ResumeOnStartup!=baseline.ResumeOnStartup||!current.SequenceEqual(baseline.Bindings))
            throw new InvalidOperationException("Startup settings changed in another editor. Close and reopen this dialog before saving.");
        var selectionChanged=!current.SequenceEqual(selected);
        if(resume&&ledger.Entries.Length==0)throw new InvalidOperationException("Startup recovery requires an existing journal intent.");
        if(selectionChanged&&selected.Length!=0&&!resume)throw new InvalidOperationException("Enable startup recovery before changing a nonempty library selection.");
        if(selected.Length!=0&&(resume||selectionChanged))
        {
            if(selected.Any(b=>!ledger.Entries.Any(e=>e.Plan.LibraryId==b.LibraryId)))throw new InvalidOperationException("Selected library is absent from the current journal intent.");
            // Native read-only identity validation under this one lease; no batch/service gates.
            if(validateBindings==null)throw new InvalidOperationException("Current library identity validation is required.");
        }
        validateBindings?.Invoke(Array.AsReadOnly(selected));token.ThrowIfCancellationRequested();
        // Manager callbacks may run external code. Preserve non-cooperating raw edits as well.
        _=Load(out var afterValidation);
        if(!original.SequenceEqual(afterValidation))throw new InvalidOperationException("Journal changed during startup settings validation. Close and reopen this dialog.");
        if(ledger.ResumeOnStartup==resume&&!selectionChanged)return StartupSnapshot(ledger,token);
        return StartupSnapshot(Save(ledger with{ResumeOnStartup=resume,StartupCatchupLibraries=selected.Length==0?null:selected},token),token);
    }
    // Checked outside catch-up/startup service gates. Recovery may promote phases but may not silently
    // replace the startup attempt's selected scope or operation identities before activation.
    internal Entry[] RequireStartupCatchupLibraries(IReadOnlyList<LibraryBinding> expected,CancellationToken token,Entry[]? intent=null)
    {
        using var held=Acquire(token);var ledger=Load();ValidateStartupIntent(ledger,expected,token,intent);return ledger.Entries;
    }
    internal void WithStartupCatchupLibraries(IReadOnlyList<LibraryBinding> expected,CancellationToken token,Entry[] intent,Action publish)
    {
        // Same cooperative journal lease spans final intent check and the short runtime publication.
        // The publish callback must not read the journal or perform external manager IO.
        using var held=Acquire(token);var ledger=Load();ValidateStartupIntent(ledger,expected,token,intent);publish();
    }
    private static void ValidateStartupIntent(Ledger ledger,IReadOnlyList<LibraryBinding> expected,CancellationToken token,Entry[]? intent)
    {
        if(!ledger.ResumeOnStartup||!ValidateBindings(ledger.StartupCatchupLibraries,token).SequenceEqual(expected))
            throw new InvalidOperationException("Startup catch-up intent changed or was disarmed; retained recovery facts are unchanged.");
        if(ledger.Entries.Length!=0&&expected.Any(b=>!ledger.Entries.Any(e=>e.Plan.LibraryId==b.LibraryId)))
            throw new InvalidOperationException("Startup catch-up library is absent from the current journal intent.");
        if(intent!=null&&(intent.Length!=ledger.Entries.Length||intent.Any(e=>!ledger.Entries.Any(now=>now.OperationId==e.OperationId&&SameIdentity(now.Plan,e.Plan)&&now.Phase==(e.Phase=="Prepared"?"Applied":e.Phase=="Restoring"?"Restored":e.Phase)))))
            throw new InvalidOperationException("Startup journal operations changed after recovery; catch-up was not activated for the old intent.");
    }
    internal static LibraryBinding[] ValidateBindings(IEnumerable<LibraryBinding>? bindings,CancellationToken token)
    {
        var selected=new List<LibraryBinding>();var ids=new HashSet<long>();
        foreach(var binding in bindings??Array.Empty<LibraryBinding>())
        {
            token.ThrowIfCancellationRequested();
            if(selected.Count==64||binding==null||binding.LibraryId<=0||binding.LibraryGuid==Guid.Empty||!ids.Add(binding.LibraryId))
                throw new InvalidDataException("Invalid, duplicate or over-budget startup library binding.");
            selected.Add(binding);
        }
        return selected.OrderBy(b=>b.LibraryId).ToArray();
    }
    public Entry[] Read(CancellationToken token)
    {
        using var held=Acquire(token);return Load().Entries;
    }
    // An initial run owns exactly its captured bytes plus its own successful saves. In particular,
    // a matching-looking foreign Prepared/Applied entry or configuration edit is never adopted.
    internal sealed class InitialState
    {
        internal byte[] Bytes;
        internal InitialState(byte[] bytes){Bytes=bytes;}
        internal void Require(byte[] current)
        {
            if(!Bytes.SequenceEqual(current))throw new InvalidOperationException("Initial journal changed outside this run; current evidence was preserved.");
        }
    }
    internal sealed class InitialFinalizationException:Exception
    {
        internal NativeEpisodeScopeTransition.Result Result { get; }
        internal InitialFinalizationException(NativeEpisodeScopeTransition.Result result,Exception error):base("Native call returned successfully but initial journal finalization failed; inspect durable intent/current mapping. "+error.Message,error){Result=result;}
    }
    internal InitialState CaptureInitial(CancellationToken token)
    {
        using var held=Acquire(token);var ledger=Load(out var original);
        if(ledger.Entries.Length!=0||ledger.ResumeOnStartup||ValidateBindings(ledger.StartupCatchupLibraries,token).Length!=0)
            throw new InvalidOperationException("Initial migration requires an empty journal with disabled startup recovery and no startup library bindings.");
        return new InitialState(original);
    }
    /// <summary>Keep the existing journal lease through final observation and retained-policy publication.</summary>
    internal T WithCurrentEntries<T>(Func<Entry[],Action,T> observe,CancellationToken token,InitialState? initial=null)
    {
        ArgumentNullException.ThrowIfNull(observe);using var held=Acquire(token);var ledger=Load(out var original);initial?.Require(original);
        return observe(ledger.Entries,()=>
        {
            // Re-read under this same lease, including byte changes by non-cooperating file writers.
            token.ThrowIfCancellationRequested();_=Load(out var current);
            if(!original.SequenceEqual(current))throw new InvalidOperationException("Journal changed during append publication validation.");
        });
    }
    public Guid Prepare(EpisodeLibraryScopePlan.Row plan,CancellationToken token)
    {
        ValidatePlan(plan);using var held=Acquire(token);var ledger=Load();
        var prepared=PrepareCore(ledger,plan);if(prepared.Changed)Save(prepared.Ledger,token);
        return prepared.Entry.OperationId;
    }
    public NativeEpisodeScopeTransition.Result Apply(Version version,ILibraryManager library,IUserDataManager data,string databasePath,EpisodeLibraryScopePlan.Row plan,CancellationToken token)=>ApplyCore(version,library,data,databasePath,plan,token,null);
    internal NativeEpisodeScopeTransition.Result ApplyGuarded(Version version,ILibraryManager library,IUserDataManager data,string databasePath,EpisodeLibraryScopePlan.Row plan,CancellationToken token,Action<Entry[]> guard,Guid? libraryGuid=null,Action? validateEpoch=null,InitialState? initial=null,Action? validateBeforeWrite=null)
    {
        ArgumentNullException.ThrowIfNull(guard);return ApplyCore(version,library,data,databasePath,plan,token,guard,libraryGuid,validateEpoch,initial,validateBeforeWrite);
    }
    private NativeEpisodeScopeTransition.Result ApplyCore(Version version,ILibraryManager library,IUserDataManager data,string databasePath,EpisodeLibraryScopePlan.Row plan,CancellationToken token,Action<Entry[]>? guard,Guid? libraryGuid=null,Action? validateEpoch=null,InitialState? initial=null,Action? validateBeforeWrite=null)
    {
        ValidatePlan(plan);using var held=Acquire(token);var ledger=Load(out var original);initial?.Require(original);guard?.Invoke(ledger.Entries);
        if(initial!=null){_=Load(out var afterGuard);initial.Require(afterGuard);}
        var prepared=PrepareCore(ledger,plan);
        var durable=prepared.Changed?Save(prepared.Ledger,token):prepared.Ledger;
        if(initial!=null)initial.Bytes=Serialize(durable);
        // Intent is durable before entering the native transaction. A thrown native call does
        // not prove absence of commit (for example lease disposal after COMMIT may fail).
        validateEpoch?.Invoke();
        void BeforeWrite()
        {
            validateBeforeWrite?.Invoke();token.ThrowIfCancellationRequested();
            _=Load(out var current);initial!.Require(current);
        }
        var result=libraryGuid.HasValue?NativeEpisodeScopeTransition.ApplySelected(version,library,data,paths,databasePath,plan,token,libraryGuid.Value,validateEpoch!,initial==null?null:BeforeWrite):NativeEpisodeScopeTransition.Apply(version,library,data,paths,databasePath,plan,token);
        try
        {
            var applied=durable with{Entries=durable.Entries.Select(e=>e.OperationId==prepared.Entry.OperationId?e with{Phase="Applied"}:e).ToArray()};
            // No post-commit cancellation check: record the observed successful commit even if the
            // request token became cancelled. IO failure leaves the Prepared record for recovery.
            if(initial!=null){_=Load(out var afterNative);initial.Require(afterNative);}
            var saved=Save(applied,CancellationToken.None);if(initial!=null)initial.Bytes=Serialize(saved);return result;
        }
        catch(Exception error)when(initial!=null){throw new InitialFinalizationException(result,error);}
    }
    public Guid PrepareRestore(long itemId,CancellationToken token)
    {
        RequireRestoreIdle();using var held=Acquire(token);var prepared=PrepareRestoreCore(Load(),itemId);
        if(prepared.Changed)Save(prepared.Ledger,token);
        return prepared.Entry.OperationId;
    }
    public NativeEpisodeScopeTransition.RestoreResult Restore(Version version,ILibraryManager library,IUserDataManager data,string databasePath,EpisodeLibraryScopePlan.Row plan,CancellationToken token)
    {
        RequireRestoreIdle();ValidatePlan(plan);using var held=Acquire(token);var ledger=Load();
        var entry=ledger.Entries.SingleOrDefault(e=>e.Plan.ItemId==plan.ItemId)??throw new InvalidOperationException("Restoration requires an existing journal entry.");
        if(!SameIdentity(entry.Plan,plan)||(entry.Phase=="Restored"&&plan.Status!="Ready"))throw new InvalidOperationException("Restoration plan conflicts with journal identity or completed restoration.");
        var prepared=PrepareRestoreCore(ledger,plan.ItemId);var durable=prepared.Changed?Save(prepared.Ledger,token):prepared.Ledger;
        var result=NativeEpisodeScopeTransition.RestoreNative(version,library,data,paths,databasePath,plan,token);
        if(entry.Phase!="Restored")Save(durable with{Entries=durable.Entries.Select(e=>e.OperationId==entry.OperationId?e with{Phase="Restored"}:e).ToArray()},CancellationToken.None);
        return result;
    }
    private static void RequireRestoreIdle()
    {
        if(ScopedEpisodeUserDataKey.RetainedCount!=0)throw new InvalidOperationException("Clear retained mappings and quiesce writers before preparing restoration.");
    }
    private static (Ledger Ledger,Entry Entry,bool Changed) PrepareRestoreCore(Ledger ledger,long itemId)
    {
        var entry=ledger.Entries.SingleOrDefault(e=>e.Plan.ItemId==itemId)??throw new InvalidOperationException("Restoration requires an existing journal entry.");
        if(entry.Phase is "Restoring" or "Restored")return(ledger,entry,false);
        var restoring=entry with{Phase="Restoring"};
        return(ledger with{Entries=ledger.Entries.Select(e=>e.OperationId==entry.OperationId?restoring:e).ToArray()},restoring,true);
    }
    public Recovery RecoverAndRetain(Version version,ILibraryManager library,IUserDataManager data,long probeUserId,CancellationToken token)
    {
        if(ScopedEpisodeUserDataKey.RetainedCount!=0)throw new InvalidOperationException("Recovery requires an empty runtime policy and a quiescent caller.");
        using var held=Acquire(token);var ledger=Load(out var original);
        void ValidateCurrent()
        {
            token.ThrowIfCancellationRequested();_=Load(out var current);
            if(!original.SequenceEqual(current))throw new InvalidOperationException("Journal changed during recovery observation; current evidence was preserved.");
        }
        if(ledger.Entries.Length==0)return new Recovery(0,Array.Empty<Observation>());
        var current=EpisodeLibraryScopePlan.Build(version,library,data,ledger.Entries.Select(e=>e.Plan.ItemId),probeUserId,token).ToDictionary(r=>r.ItemId);
        var observations=new List<Observation>();var retained=new List<EpisodeLibraryScopePlan.Row>();
        foreach(var entry in ledger.Entries)
        {
            token.ThrowIfCancellationRequested();var actual=current[entry.Plan.ItemId];
            if(!SameIdentity(entry.Plan,actual)||actual.Issues.Length!=0||actual.Status is not ("Ready" or "AlreadyScoped"))
                observations.Add(new(entry.OperationId,entry.Plan.ItemId,"Conflict","Identity, mapping or presentation no longer matches journal."));
            else if(entry.Phase is "Restoring" or "Restored")
            {
                observations.Add(actual.Status=="Ready"
                    ?new(entry.OperationId,entry.Plan.ItemId,entry.Phase=="Restoring"?"RestoredObservation":"Restored","Original native mapping observed; recovery does not change user-history rows.")
                    :new(entry.OperationId,entry.Plan.ItemId,entry.Phase=="Restoring"?"RestorePending":"Conflict",entry.Phase=="Restoring"?"Restoration intent has not been observed in the database.":"Restored entry unexpectedly has a scoped mapping."));
            }
            else if(actual.Status=="AlreadyScoped")
            {observations.Add(new(entry.OperationId,entry.Plan.ItemId,"Retain","Observed committed scoped mapping."));retained.Add(actual);}
            else observations.Add(new(entry.OperationId,entry.Plan.ItemId,entry.Phase=="Prepared"?"PendingOrReverted":"Conflict",entry.Phase=="Prepared"?"Intent alone cannot distinguish never-applied from later reverted mapping.":"Applied journal entry no longer has its scoped mapping."));
        }
        // Do not quietly publish a subset or rewrite promotions while another entry is uncertain.
        if(observations.Any(o=>o.State is not ("Retain" or "RestoredObservation" or "Restored")))return new Recovery(0,observations.ToArray());
        ValidateCurrent();
        if(ledger.Entries.Any(e=>e.Phase is "Prepared" or "Restoring"))
        {
            ledger=Save(ledger with{Entries=ledger.Entries.Select(e=>e.Phase=="Prepared"?e with{Phase="Applied"}:e.Phase=="Restoring"?e with{Phase="Restored"}:e).ToArray()},token);
            original=Serialize(ledger); // Expected bytes of our own promotion, never a fresh external baseline.
        }
        ValidateCurrent();
        if(retained.Count>0)ScopedEpisodeUserDataKey.RetainMappedValidated(version,library,data,retained,probeUserId,token,ValidateCurrent);
        return new Recovery(retained.Count,observations.ToArray());
    }
    public sealed record CycleReset(bool Reset,string? ArchivePath,long Revision);
    public CycleReset BeginNextCycle(Version version,ILibraryManager library,IUserDataManager data,long probeUserId,CancellationToken token)
    {
        RequireRestoreIdle();using var held=Acquire(token);var ledger=Load(out var original);
        if(ledger.Entries.Length==0)return new CycleReset(false,null,ledger.Revision);
        if(ledger.Entries.Any(e=>e.Phase!="Restored"))throw new InvalidOperationException("Every journal entry must be restored before beginning another cycle.");
        _=checked(ledger.Revision+1);
        var current=EpisodeLibraryScopePlan.Build(version,library,data,ledger.Entries.Select(e=>e.Plan.ItemId),probeUserId,token).ToDictionary(r=>r.ItemId);
        if(ledger.Entries.Any(e=>!current.TryGetValue(e.Plan.ItemId,out var row)||row.Status!="Ready"||row.Issues.Length!=0||!SameIdentity(e.Plan,row)))throw new InvalidOperationException("Restored journal no longer matches the native mappings; no cycle was reset.");
        RequireRestoreIdle();token.ThrowIfCancellationRequested();
        var archive=Archive(original,token);
        // The archive is a retained audit copy, never an automatic recovery fallback. Failure
        // before reset publication leaves the old main intact; retry reuses the identical archive.
        RequireRestoreIdle();var saved=Save(ledger with{Entries=Array.Empty<Entry>()},token);
        return new CycleReset(true,archive,saved.Revision);
    }
    private string Archive(byte[] original,CancellationToken token)
    {
        var directory=paths.RequireWritablePath(file+".cycles");
        var target=paths.RequireWritablePath(Path.Combine(directory,Convert.ToHexString(SHA256.HashData(original))+".json"));
        if(File.Exists(target))
        {
            if(!ReadBytes(target).SequenceEqual(original))throw new InvalidDataException("Existing cycle archive content differs; it will not be overwritten.");
            return target;
        }
        if(!Directory.Exists(directory))Directory.CreateDirectory(paths.RequireWritablePath(directory));
        // ponytail: bounded audit retention, no automatic pruning of earlier cycle evidence.
        if(Directory.EnumerateFiles(paths.RequireWritablePath(directory),"*.json").Take(256).Count()==256)throw new InvalidOperationException("Cycle archive budget reached; no earlier evidence is deleted automatically.");
        var temporary=paths.RequireWritablePath(target+"."+Guid.NewGuid().ToString("N")+".tmp");Exception? failure=null;var created=false;
        try
        {
            token.ThrowIfCancellationRequested();
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;output.Write(original);output.Flush(true);}
            token.ThrowIfCancellationRequested();File.Move(paths.RequireWritablePath(temporary),paths.RequireWritablePath(target));return target;
        }
        catch(Exception error){failure=error;throw;}
        finally
        {
            try{if(created&&File.Exists(temporary))File.Delete(paths.RequireWritablePath(temporary));}
            catch when(failure!=null){/* Preserve primary archive failure, leaving no reset claimed. */}
        }
    }
    private FileStream Acquire(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var path=paths.RequireWritablePath(file+".lock");
        var parent=Path.GetDirectoryName(path)!;
        if(!Directory.Exists(parent))Directory.CreateDirectory(paths.RequireWritablePath(parent));
        // Keep this lock inode. Deleting it after release would let two callers lock different files.
        return new FileStream(paths.RequireWritablePath(path),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    private Ledger Load()=>Load(out _);
    private Ledger Load(out byte[] original)
    {
        original=Array.Empty<byte>();var path=paths.RequireWritablePath(file);
        if(!File.Exists(path))
        {
            if(File.Exists(paths.RequireWritablePath(file+".bak")))throw new InvalidDataException("Main journal missing while backup exists; no empty replacement is inferred.");
            return new Ledger(1,0,Array.Empty<Entry>());
        }
        original=ReadBytes(path);
        using var json=JsonDocument.Parse(original);UniqueProperties(json.RootElement);
        if(json.RootElement.ValueKind!=JsonValueKind.Object||!json.RootElement.TryGetProperty("Format",out _)||!json.RootElement.TryGetProperty("Revision",out _)||!json.RootElement.TryGetProperty("Entries",out _))throw new InvalidDataException("Incomplete journal envelope.");
        var ledger=json.RootElement.Deserialize<Ledger>()??throw new InvalidDataException("Empty journal document.");
        ValidateLedger(ledger);return ledger;
    }
    private byte[] ReadBytes(string path)
    {
        using var input=new FileStream(paths.RequireWritablePath(path),FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length>MaxBytes)throw new InvalidDataException("Journal size budget exceeded.");
        using var bytes=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=input.Read(buffer,0,buffer.Length))!=0){if(bytes.Length+count>MaxBytes)throw new InvalidDataException("Journal size budget exceeded.");bytes.Write(buffer,0,count);}
        return bytes.ToArray();
    }
    private static byte[] Serialize(Ledger ledger)=>JsonSerializer.SerializeToUtf8Bytes(ledger,new JsonSerializerOptions{WriteIndented=true});
    private Ledger Save(Ledger ledger,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();ledger=ledger with{Revision=checked(ledger.Revision+1)};ValidateLedger(ledger);
        var bytes=Serialize(ledger);
        if(bytes.Length>MaxBytes)throw new InvalidDataException("Journal size budget exceeded.");
        var temporary=paths.RequireWritablePath(file+"."+Guid.NewGuid().ToString("N")+".tmp");
        Exception? failure=null;var created=false;
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;output.Write(bytes);output.Flush(true);}
            token.ThrowIfCancellationRequested();
            var destination=paths.RequireWritablePath(file);
            if(File.Exists(destination))File.Replace(paths.RequireWritablePath(temporary),destination,paths.RequireWritablePath(file+".bak"));
            else File.Move(paths.RequireWritablePath(temporary),destination);
            return ledger;
        }
        catch(Exception error){failure=error;throw;}
        finally
        {
            try{if(created&&File.Exists(temporary))File.Delete(paths.RequireWritablePath(temporary));}
            catch when(failure!=null){/* Keep the primary write failure; a temp file may remain. */}
        }
    }
    private static (Ledger Ledger,Entry Entry,bool Changed) PrepareCore(Ledger ledger,EpisodeLibraryScopePlan.Row plan)
    {
        var existing=ledger.Entries.SingleOrDefault(e=>e.Plan.ItemId==plan.ItemId);
        if(existing!=null)
        {
            if(!SameIdentity(existing.Plan,plan)||existing.Phase is "Restoring" or "Restored")throw new InvalidOperationException("Journal identity or restoration intent conflicts with requested migration.");
            return(ledger,existing,false);
        }
        if(ledger.Entries.Length==MaxEntries)throw new InvalidOperationException("Journal entry budget exceeded.");
        var entry=new Entry(Guid.NewGuid(),"Prepared",plan with{Issues=Array.Empty<string>()});
        return(ledger with{Entries=ledger.Entries.Append(entry).ToArray()},entry,true);
    }
    private static bool SameIdentity(EpisodeLibraryScopePlan.Row a,EpisodeLibraryScopePlan.Row b)=>a.ItemId==b.ItemId&&a.ItemGuid==b.ItemGuid&&a.LibraryId==b.LibraryId&&a.NativeKey==b.NativeKey&&a.ScopedKey==b.ScopedKey&&a.EpisodeKey==b.EpisodeKey&&a.SeriesKey==b.SeriesKey;
    private static void ValidateLedger(Ledger ledger)
    {
        if(ledger.Format!=1||ledger.Revision<0||ledger.Entries==null||ledger.Entries.Length>MaxEntries)throw new InvalidDataException("Unsupported or invalid journal envelope.");
        _=ValidateBindings(ledger.StartupCatchupLibraries,CancellationToken.None);
        var items=new HashSet<long>();var operations=new HashSet<Guid>();
        foreach(var entry in ledger.Entries)
        {
            if(entry==null||entry.OperationId==Guid.Empty||entry.Phase is not ("Prepared" or "Applied" or "Restoring" or "Restored")||!operations.Add(entry.OperationId))throw new InvalidDataException("Invalid or duplicate journal operation.");
            ValidatePlan(entry.Plan);if(!items.Add(entry.Plan.ItemId))throw new InvalidDataException("Duplicate journal item.");
        }
    }
    internal static void ValidatePlan(EpisodeLibraryScopePlan.Row plan)
    {
        if(plan==null||plan.ItemId<=0||plan.ItemGuid==Guid.Empty||plan.LibraryId<=0||plan.Issues==null||plan.Issues.Length!=0||plan.Status is not ("Ready" or "AlreadyScoped")||
            new[]{plan.NativeKey,plan.ScopedKey,plan.EpisodeKey,plan.SeriesKey}.Any(s=>string.IsNullOrWhiteSpace(s)||s.Any(char.IsControl))||plan.NativeKey.StartsWith(EpisodeLibraryScopePlan.KeyPrefix,StringComparison.OrdinalIgnoreCase)||
            plan.ScopedKey!=EpisodeLibraryScopePlan.KeyPrefix+plan.LibraryId.ToString(CultureInfo.InvariantCulture)+"/"+plan.NativeKey||plan.StoredKey!=(plan.Status=="Ready"?plan.NativeKey:plan.ScopedKey))throw new InvalidDataException("Invalid journal plan identity.");
    }
    private static void UniqueProperties(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in element.EnumerateObject()){if(!names.Add(property.Name))throw new InvalidDataException("Duplicate JSON property in journal.");UniqueProperties(property.Value);}
        }
        else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())UniqueProperties(child);
    }
}
