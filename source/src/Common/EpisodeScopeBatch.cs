using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;

/// <summary>Explicit, bounded per-item orchestration. It does not drain native writers or wire the global option.</summary>
public sealed class EpisodeScopeBatch
{
    public enum Operation{Apply,Restore,Recover,Append}
    public sealed record ItemResult(long ItemId,string State,string Detail,int CopiedUsers,int MappedItems);
    public sealed record Report(Operation Operation,string Phase,bool Cancelled,int Retained,IReadOnlyList<ItemResult> Items,string Reconciliation,int ProgressErrors)
    {
        public EpisodeLibrarySelection.Selection? LibrarySelection { get; init; }
        /// <summary>False on an observed external policy conflict; Retained then describes the last known expected policy.</summary>
        public bool RetainedCountAuthoritative { get; init; }=true;
        // Exact publication fact for the runtime catch-up owner; never inferred by recapturing a later policy.
        internal ScopedEpisodeUserDataKey.RetainedSnapshot? PublishedRetained { get; init; }
    }
    internal sealed record AppendBinding(ScopedEpisodeUserDataKey.RetainedSnapshot Retained,IReadOnlyDictionary<long,Guid> Libraries,Action ValidateEpoch)
    {
        internal Guid Require(EpisodeLibraryScopePlan.Row row)
        {
            ValidateEpoch();Retained.RequireCurrent();
            if(!Libraries.TryGetValue(row.LibraryId,out var guid)||guid==Guid.Empty)throw new InvalidOperationException("Episode left the explicitly selected catch-up libraries.");
            return guid;
        }
    }
    private sealed record InitialBinding(IReadOnlyList<EpisodeLibraryScopePlan.Row> Rows,IReadOnlyDictionary<long,Guid> Libraries,Action? ValidateAuthority,Action? ValidateCommit);
    private sealed class SelectionProgress:IProgress<double>
    {
        private readonly Action<double> report;public SelectionProgress(Action<double> report){this.report=report;}public void Report(double value)=>report(value);
    }
    private static readonly SemaphoreSlim admission=new(1,1);
    private readonly Version version;
    private readonly ILibraryManager library;
    private readonly IUserDataManager data;
    private readonly string database;
    private readonly EpisodeScopeJournal journal;
    private readonly QueueManager queue;
    private readonly ILogger logger;
    private Report? last;
    private readonly CancellationTokenSource stopping=new();
    private readonly object stopGate=new();
    private Task? stopTask;
    private int stopRequested;
    internal bool UsesLibrary(ILibraryManager manager)=>ReferenceEquals(library,manager);
    public Report? LastReport=>Volatile.Read(ref last);
    public bool StopRequested=>Volatile.Read(ref stopRequested)!=0;
    public Task ShutdownCompletion=>Volatile.Read(ref stopTask)??Task.CompletedTask;
    public string ShutdownState
    {
        get
        {
            if(!StopRequested)return "Accepting";
            var task=Volatile.Read(ref stopTask);
            return task?.IsFaulted==true?"Failed":task?.IsCompletedSuccessfully==true?"Stopped":"Stopping";
        }
    }
    /// <summary>Close this instance, cancel its work, then clear the global retained policy after admitted batches exit.
    /// A callback may initiate/store this task, but must not await or synchronously wait for its own batch to exit.
    /// Cancelling the waiter never cancels the shared cleanup; a failed cleanup can be retried.
    /// No database remap or native-writer drain is performed.</summary>
    public Task StopAndClearAsync(CancellationToken waitToken=default)
    {
        TaskCompletionSource<bool>? completion=null;Task task;
        lock(stopGate)
        {
            if(stopTask==null||stopTask.IsFaulted)
            {
                Interlocked.Exchange(ref stopRequested,1);
                completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref stopTask,completion.Task);
            }
            task=stopTask!;
        }
        if(completion!=null)
        {
            Exception? signalFailure=null;
            try{stopping.Cancel();}catch(Exception error){signalFailure=error;}
            _=DrainAndClear(completion,signalFailure);
        }
        return waitToken.CanBeCanceled?task.WaitAsync(waitToken):task;
    }
    private static async Task DrainAndClear(TaskCompletionSource<bool> completion,Exception? signalFailure)
    {
        try
        {
            await admission.WaitAsync().ConfigureAwait(false);
            try{ScopedEpisodeUserDataKey.ClearRetained();}
            finally{admission.Release();}
            if(signalFailure!=null)completion.TrySetException(signalFailure);else completion.TrySetResult(true);
        }
        catch(Exception error)
        {
            completion.TrySetException(signalFailure==null?error:new AggregateException("Stop signalling and retained cleanup failed.",signalFailure,error));
        }
    }
    public EpisodeScopeBatch(Version version,ILibraryManager library,IUserDataManager data,string databasePath,EpisodeScopeJournal journal,QueueManager queue,ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(version);ArgumentNullException.ThrowIfNull(library);ArgumentNullException.ThrowIfNull(data);ArgumentNullException.ThrowIfNull(journal);ArgumentNullException.ThrowIfNull(queue);ArgumentNullException.ThrowIfNull(logger);
        if(string.IsNullOrWhiteSpace(databasePath))throw new ArgumentException("A native database path is required.",nameof(databasePath));
        this.version=version;this.library=library;this.data=data;database=databasePath;this.journal=journal;this.queue=queue;this.logger=logger;
    }
    /// <summary>Reconcile the complete durable journal under the same admission and shutdown boundary as batches.
    /// This may promote observed journal phases and retain keys, but never copies userdata or remaps items.</summary>
    public Task<Report> RecoverAsync(long probeUserId,IProgress<double>? progress,CancellationToken token)=>RunAsync(Operation.Recover,Array.Empty<long>(),probeUserId,progress,token);
    /// <summary>Explicitly append new Episode IDs without disabling the existing retained rules. No ItemAdded wiring.</summary>
    public Task<Report> AppendAsync(IEnumerable<long> itemIds,long probeUserId,IProgress<double>? progress,CancellationToken token)=>RunAsync(Operation.Append,itemIds,probeUserId,progress,token);
    /// <summary>Confirm exactly one server-held Ready preview against an empty, disabled journal cycle.
    /// Authority validation may read managers outside native writes; commit validation must be a pure
    /// cancellation/closed/epoch check and must never perform manager, journal or database IO.</summary>
    public Task<Report> ApplyInitialAsync(IEnumerable<EpisodeLibraryScopePlan.Row> previewRows,IEnumerable<EpisodeScopeJournal.LibraryBinding> libraries,long probeUserId,IProgress<double>? progress,CancellationToken token,Action? validateAuthority=null,Action? validateCommit=null)
    {
        ArgumentNullException.ThrowIfNull(previewRows);ArgumentNullException.ThrowIfNull(libraries);
        if(probeUserId<=0)throw new ArgumentOutOfRangeException(nameof(probeUserId));
        if(StopRequested)throw new ObjectDisposedException(nameof(EpisodeScopeBatch));
        token.ThrowIfCancellationRequested();queue.StoppingToken.ThrowIfCancellationRequested();
        var rows=new List<EpisodeLibraryScopePlan.Row>();var ids=new HashSet<long>();
        foreach(var row in previewRows)
        {
            token.ThrowIfCancellationRequested();if(rows.Count==4096)throw new ArgumentException("Initial preview raw row budget exceeded.");
            EpisodeScopeJournal.ValidatePlan(row);
            if(row.Status!="Ready"||!ids.Add(row.ItemId))throw new ArgumentException("Initial preview must contain unique Ready episodes only.");
            rows.Add(row with{Issues=row.Issues.ToArray()});
        }
        var selected=EpisodeScopeJournal.ValidateBindings(libraries,token);
        if(rows.Count==0||selected.Length==0||selected.Select(b=>b.LibraryGuid).Distinct().Count()!=selected.Length||rows.Any(r=>!selected.Any(b=>b.LibraryId==r.LibraryId)))
            throw new ArgumentException("A nonempty preview and unique selected library IDs/Guids covering every preview row are required.");
        var initial=new InitialBinding(Array.AsReadOnly(rows.ToArray()),new System.Collections.ObjectModel.ReadOnlyDictionary<long,Guid>(selected.ToDictionary(b=>b.LibraryId,b=>b.LibraryGuid)),validateAuthority,validateCommit);
        return ExecuteAsync(Operation.Apply,rows.Select(r=>r.ItemId),false,probeUserId,progress,token,initial:initial);
    }
    public Task<Report> RunAsync(Operation operation,IEnumerable<long> itemIds,long probeUserId,IProgress<double>? progress,CancellationToken token)=>ExecuteAsync(operation,itemIds,false,probeUserId,progress,token);
    public Task<Report> RunLibrariesAsync(Operation operation,IEnumerable<long> libraryIds,long probeUserId,IProgress<double>? progress,CancellationToken token)=>ExecuteAsync(operation,libraryIds,true,probeUserId,progress,token);
    internal Task<Report> AppendSelectedAsync(IEnumerable<long> itemIds,long probeUserId,CancellationToken token,AppendBinding binding)=>ExecuteAsync(Operation.Append,itemIds,false,probeUserId,null,token,binding);
    private async Task<Report> ExecuteAsync(Operation operation,IEnumerable<long> itemIds,bool byLibrary,long probeUserId,IProgress<double>? progress,CancellationToken token,AppendBinding? binding=null,InitialBinding? initial=null)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if(!Enum.IsDefined(typeof(Operation),operation)||byLibrary&&operation is Operation.Recover or Operation.Append)throw new ArgumentOutOfRangeException(nameof(operation));
        if(probeUserId<=0)throw new ArgumentOutOfRangeException(nameof(probeUserId));
        if(StopRequested)throw new ObjectDisposedException(nameof(EpisodeScopeBatch));
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token,queue.StoppingToken,stopping.Token);token=lifetime.Token;
        token.ThrowIfCancellationRequested();
        var selected=new List<long>();
        foreach(var id in itemIds){token.ThrowIfCancellationRequested();if(selected.Count==(byLibrary?EpisodeLibrarySelection.MaxLibraries:4096))throw new ArgumentException("Batch input budget exceeded.");selected.Add(id);}
        var ids=selected.Distinct().ToArray();
        if(operation==Operation.Recover&&ids.Length!=0)throw new ArgumentException("Recovery observes the complete journal and does not accept an item selection.",nameof(itemIds));
        if(!await admission.WaitAsync(0,token).ConfigureAwait(false))throw new InvalidOperationException("Another Episode scope batch is running.");
        try
        {
            if(StopRequested)throw new ObjectDisposedException(nameof(EpisodeScopeBatch));
            token.ThrowIfCancellationRequested();
            var expected=operation==Operation.Append?ScopedEpisodeUserDataKey.RetainedSnapshot.Capture(library):null;
            // Capture verifies this batch's manager; a selected caller still owns the exact expected reference.
            if(binding!=null){binding.ValidateEpoch();binding.Retained.RequireCurrent();expected=binding.Retained;}
            ScopedEpisodeUserDataKey.RetainedSnapshot? published=null;
            if(expected==null&&ScopedEpisodeUserDataKey.RetainedCount!=0)throw new InvalidOperationException("Clear retained mappings and quiesce writers before starting a scope batch.");
            if(expected!=null)
            {
                if(expected.Rows.Any(r=>ids.Contains(r.ItemId)))throw new ArgumentException("Append accepts only IDs not already retained.",nameof(itemIds));
                if(expected.Count+ids.Length>4096)throw new ArgumentException("Append retained plus requested input budget exceeded.",nameof(itemIds));
            }
            EpisodeScopeJournal.InitialState? initialJournal=null;
            var initialRows=initial?.Rows.ToDictionary(r=>r.ItemId);
            EpisodeScopeJournal.Entry[] initialEntries=Array.Empty<EpisodeScopeJournal.Entry>();
            HashSet<long>? appendIds=null;
            EpisodeLibrarySelection.Selection? librarySelection=null;
            var results=byLibrary?Array.Empty<ItemResult>():ids.Select(id=>new ItemResult(id,"Pending","Not attempted.",0,0)).ToArray();
            var observerErrors=0;var retained=expected?.Count??0;var reconciliation="Not attempted.";var cancelled=false;var lastPercentage=0d;var initialPolicyConflict=false;
            Report PolicyFacts(Report report)
            {
                if(initial!=null&&!(expected?.IsCurrent??ScopedEpisodeUserDataKey.IsInactive))initialPolicyConflict=true;
                if(!initialPolicyConflict&&(expected==null?(initial==null||ScopedEpisodeUserDataKey.IsInactive):expected.IsCurrent))return report;
                if(!reconciliation.StartsWith("Retained policy conflict:",StringComparison.Ordinal))reconciliation="Retained policy conflict: expected policy was replaced or cleared; Retained is last-known, not authoritative. No cleanup or overwrite of the conflicting external policy was attempted. "+reconciliation;
                return report with{Phase=report.Cancelled?"Cancelled":"Conflict",RetainedCountAuthoritative=false,Reconciliation=reconciliation};
            }
            void Log(Exception error){try{logger.Warn("[StrmAssistantRebuild] Episode scope batch: {0}: {1}",error.GetType().Name,error.Message);}catch{/* A log sink must not turn committed work into a reported rollback. */}}
            Report Publish(string phase,double percentage)
            {
                if(phase=="Cancelled")percentage=lastPercentage;else lastPercentage=Math.Max(lastPercentage,percentage);
                var report=PolicyFacts(new Report(operation,phase,cancelled,retained,Array.AsReadOnly(results.ToArray()),reconciliation,observerErrors){LibrarySelection=librarySelection,PublishedRetained=published});
                Volatile.Write(ref last,report);
                try{progress?.Report(percentage);}
                catch(Exception error){observerErrors++;Log(error);}
                cancelled|=token.IsCancellationRequested;
                report=PolicyFacts(report with{Phase=cancelled?"Cancelled":phase,Cancelled=cancelled,ProgressErrors=observerErrors});
                Volatile.Write(ref last,report);return report;
            }
            void RequireInitialInactive()
            {
                if(!ScopedEpisodeUserDataKey.IsInactive)initialPolicyConflict=true;
                ScopedEpisodeUserDataKey.RequireInactive();
            }
            void ValidateInitialCommit()
            {
                token.ThrowIfCancellationRequested();RequireInitialInactive();
                initial!.ValidateCommit?.Invoke();token.ThrowIfCancellationRequested();RequireInitialInactive();
            }
            void ValidateInitialLibraries()
            {
                ValidateInitialCommit();initial!.ValidateAuthority?.Invoke();ValidateInitialCommit();
                foreach(var selectedLibrary in initial.Libraries)
                {
                    if(library.GetItemById(selectedLibrary.Key) is not MediaBrowser.Controller.Entities.CollectionFolder folder||folder.InternalId!=selectedLibrary.Key||folder.Id!=selectedLibrary.Value)
                        throw new InvalidOperationException("Initial preview CollectionFolder identity changed.");
                }
                initial.ValidateAuthority?.Invoke();ValidateInitialCommit();
            }
            EpisodeLibraryScopePlan.Row[] InitialPlan(EpisodeScopeJournal.Entry[] entries,bool final)
            {
                ValidateInitialLibraries();var byId=entries.ToDictionary(e=>e.Plan.ItemId);
                if(entries.Any(e=>!initialRows!.TryGetValue(e.Plan.ItemId,out var preview)||!ScopedEpisodeUserDataKey.SameIdentity(preview,e.Plan)||e.Plan.Status!="Ready"||e.Plan.StoredKey!=preview.StoredKey||e.Phase is not ("Prepared" or "Applied"))||final&&(entries.Length!=ids.Length||entries.Any(e=>e.Phase!="Applied")))
                    throw new InvalidOperationException("Initial journal no longer contains exactly this run's confirmed preview operations.");
                // ponytail: replan the bounded joint preview per row; optimize only with an equally strong
                // transaction-bound dependency snapshot. Never expand this input with fresh discovery.
                var joint=EpisodeLibraryScopePlan.Build(version,library,data,ids,probeUserId,token);
                var actual=joint.ToDictionary(r=>r.ItemId);
                if(joint.Length!=initial!.Rows.Count||initial.Rows.Any(r=>!actual.TryGetValue(r.ItemId,out var now)||!ScopedEpisodeUserDataKey.SameIdentity(r,now)||now.Issues.Length!=0||now.Status!=(byId.TryGetValue(r.ItemId,out var entry)&&entry.Phase=="Applied"?"AlreadyScoped":"Ready")||now.StoredKey!=(now.Status=="Ready"?r.StoredKey:r.ScopedKey)))
                    throw new InvalidOperationException("Current joint plan differs from the confirmed initial preview; no replacement plan is adopted.");
                ValidateInitialLibraries();return joint;
            }
            void ValidateAppendJournal(EpisodeScopeJournal.Entry[] entries,bool final)
            {
                binding?.ValidateEpoch();expected!.RequireCurrent();
                var byId=entries.ToDictionary(e=>e.Plan.ItemId);
                if(entries.Any(e=>!appendIds!.Contains(e.Plan.ItemId)))throw new InvalidOperationException("Unplanned journal identity appeared during append; old retained policy preserved.");
                if(expected.Rows.Any(r=>!byId.TryGetValue(r.ItemId,out var e)||e.Phase!="Applied"||!expected.Matches(e.Plan)))throw new InvalidOperationException("Journal disagrees with retained identities or Applied phases.");
                if(entries.Any(e=>e.Phase!="Applied"&&(final||e.Phase!="Prepared"||!ids.Contains(e.Plan.ItemId))))throw new InvalidOperationException("Unmanaged pending or reverse journal entry; old retained policy preserved.");
                if(initialEntries.Any(e=>!byId.TryGetValue(e.Plan.ItemId,out var now)||e.OperationId!=now.OperationId||!ScopedEpisodeUserDataKey.SameIdentity(e.Plan,now.Plan)||expected.Matches(e.Plan)&&(e.Phase!=now.Phase||e.Plan.Status!=now.Plan.Status||e.Plan.StoredKey!=now.Plan.StoredKey)))throw new InvalidOperationException("Existing journal entries changed during append.");
            }
            void ValidateSelectedLibraries(IEnumerable<EpisodeLibraryScopePlan.Row> rows)
            {
                if(binding==null)return;
                foreach(var row in rows.Where(r=>!expected!.Matches(r)))
                {
                    var guid=binding.Require(row);
                    if(library.GetItemById(row.LibraryId) is not MediaBrowser.Controller.Entities.CollectionFolder folder||folder.InternalId!=row.LibraryId||folder.Id!=guid)
                        throw new InvalidOperationException("Selected catch-up CollectionFolder identity changed.");
                }
                binding.ValidateEpoch();
            }
            EpisodeLibraryScopePlan.Row[] AppendPlan(EpisodeScopeJournal.Entry[] entries,bool final)
            {
                ValidateAppendJournal(entries,final);
                var jointIds=entries.Select(e=>e.Plan.ItemId).Concat(ids).Distinct().ToArray();
                if(jointIds.Length>4096)throw new InvalidOperationException("Append journal/request union budget exceeded.");
                var joint=EpisodeLibraryScopePlan.Build(version,library,data,jointIds,probeUserId,token);
                ValidateSelectedLibraries(joint);
                var actual=joint.ToDictionary(r=>r.ItemId);
                if(entries.Any(e=>!ScopedEpisodeUserDataKey.SameIdentity(e.Plan,actual[e.Plan.ItemId])||actual[e.Plan.ItemId].Issues.Length!=0||(e.Phase=="Applied"&&actual[e.Plan.ItemId].Status!="AlreadyScoped")||actual[e.Plan.ItemId].Status is not ("Ready" or "AlreadyScoped")))throw new InvalidOperationException("Current joint plan disagrees with journal identity, mapping or presentation.");
                if(final&&(entries.Length!=joint.Length||joint.Any(r=>r.Status!="AlreadyScoped"||r.Issues.Length!=0)))throw new InvalidOperationException("Unresolved new Episode mapping; old retained policy preserved.");
                expected!.RequireCurrent();return joint;
            }
            Report Reconcile()
            {
                try
                {
                    if(initial!=null)
                    {
                        if(results.Any(r=>r.State!="Applied"))throw new InvalidOperationException("Initial migration has unconfirmed requested items; explicit recovery is required.");
                        journal.WithCurrentEntries((entries,validateCurrent)=>
                        {
                            var joint=InitialPlan(entries,true);validateCurrent();
                            expected=ScopedEpisodeUserDataKey.RetainInitialMapped(version,library,data,joint,probeUserId,token,()=>{ValidateInitialLibraries();validateCurrent();});
                            published=expected;retained=expected.Count;return true;
                        },token,initialJournal);
                        reconciliation="All confirmed preview records are Applied and jointly AlreadyScoped; initial inactive policy published. Startup settings remain disabled and unchanged.";
                        return Publish("Completed",100);
                    }
                    if(expected!=null)
                    {
                        // One journal lease spans final joint reads and the Scope Gate comparison/publication.
                        // No nested Read/Acquire and no gap for a cooperating journal writer to add an intent.
                        journal.WithCurrentEntries((entries,validateCurrent)=>
                        {
                            var joint=AppendPlan(entries,true);
                            if(joint.Length>expected.Count)
                            {
                                expected=ScopedEpisodeUserDataKey.AppendMapped(version,library,data,joint,probeUserId,token,expected,()=>{ValidateSelectedLibraries(joint);binding?.ValidateEpoch();validateCurrent();});
                                published=expected;retained=expected.Count;
                            }
                            return true;
                        },token);
                        reconciliation="All journal entries are Applied and jointly AlreadyScoped; every old retained rule and journal entry is unchanged.";
                        return Publish(results.Any(r=>r.State is "Blocked" or "CallFailed")?"Partial":"Completed",100);
                    }
                    var recovery=journal.RecoverAndRetain(version,library,data,probeUserId,token);
                    retained=recovery.Retained;
                    if(operation==Operation.Recover)results=recovery.Observations.Select(o=>new ItemResult(o.ItemId,o.State,o.Reason,0,0)).ToArray();
                    reconciliation=string.Join(";",recovery.Observations.Select(o=>o.ItemId+":"+o.State));
                    if(recovery.Observations.Any(o=>o.State is not ("Retain" or "RestoredObservation" or "Restored")))return Publish("Suspended",100);
                }
                catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
                catch(Exception error){Log(error);reconciliation=error.GetType().Name+": "+error.Message;return Publish("Suspended",100);}
                return Publish(results.Any(r=>r.State is "Blocked" or "CallFailed")?"Partial":"Completed",100);
            }
            try
            {
                if(initial!=null){ValidateInitialCommit();initialJournal=journal.CaptureInitial(token);}
                Publish("Planning",0);token.ThrowIfCancellationRequested();
                if(operation==Operation.Recover)
                {
                    Publish("Reconciling",10);token.ThrowIfCancellationRequested();
                    if(!ScopedEpisodeUserDataKey.Probe(version,out var detail)){reconciliation=detail;return Publish("Suspended",100);}
                    return Reconcile();
                }
                if(byLibrary)
                {
                    librarySelection=EpisodeLibrarySelection.Select(version,library,ids,token,new SelectionProgress(value=>Publish("SelectingLibraries",value/10)));
                    if(!librarySelection.Complete){reconciliation="Library selection was incomplete; no item mutation or journal reconciliation was attempted.";return Publish("Suspended",100);}
                    ids=librarySelection.ItemIds.ToArray();results=ids.Select(id=>new ItemResult(id,"Pending","Not attempted.",0,0)).ToArray();
                }
                if(ids.Length==0){reconciliation="Empty selection; existing journal and runtime policy were not touched.";return Publish("Completed",100);}
                EpisodeLibraryScopePlan.Row[] plan;
                if(initial!=null)
                {
                    journal.WithCurrentEntries((entries,validateCurrent)=>{InitialPlan(entries,false);validateCurrent();return true;},token,initialJournal);
                    plan=initial.Rows.ToArray();
                }
                else if(expected!=null)
                {
                    initialEntries=journal.Read(token);
                    appendIds=initialEntries.Select(e=>e.Plan.ItemId).Concat(ids).ToHashSet();
                    var joint=AppendPlan(initialEntries,false).ToDictionary(r=>r.ItemId);
                    plan=ids.Select(id=>joint[id]).ToArray();
                }
                else plan=EpisodeLibraryScopePlan.Build(version,library,data,ids,probeUserId,token);
                Publish("Executing",10);
                for(var i=0;i<plan.Length;i++)
                {
                    if(token.IsCancellationRequested){cancelled=true;break;}
                    expected?.RequireCurrent();
                    var row=plan[i];
                    if(row.Status is not ("Ready" or "AlreadyScoped")||row.Issues.Length!=0)
                        results[i]=new(row.ItemId,"Blocked",string.Join(";",row.Issues),0,0);
                    else
                    {
                        var entered=false;
                        try
                        {
                            // One item/quota lease only. Never call this batch from inside a lease
                            // on this QueueManager: its striped item locks are not reentrant.
                            results[i]=await queue.RunSingle(row.ItemId,ct=>
                            {
                                entered=true;
                                if(operation is Operation.Apply or Operation.Append)
                                {
                                    // Recheck after the queue wait, inside the SAME journal lease as Prepare/native Apply.
                                    var applied=initial!=null
                                        ?journal.ApplyGuarded(version,library,data,database,row,ct,entries=>{InitialPlan(entries,false);},initial.Libraries[row.LibraryId],ValidateInitialCommit,initialJournal,ValidateInitialLibraries)
                                        :expected==null?journal.Apply(version,library,data,database,row,ct):journal.ApplyGuarded(version,library,data,database,row,ct,entries=>{ValidateAppendJournal(entries,false);binding?.Require(row);},binding?.Require(row),binding?.ValidateEpoch);
                                    return Task.FromResult(new ItemResult(row.ItemId,applied.AlreadyScoped?"AlreadyScoped":"Applied","Native call and journal finalization returned successfully.",applied.CopiedUsers,applied.MappedItems));
                                }
                                var restored=journal.Restore(version,library,data,database,row,ct);
                                return Task.FromResult(new ItemResult(row.ItemId,restored.AlreadyNative?"AlreadyNative":"Restored","Native call and journal finalization returned successfully; history was not merged.",0,restored.MappedItems));
                            },token,secondTier:true).ConfigureAwait(false);
                        }
                        catch(OperationCanceledException error)when(token.IsCancellationRequested||error.CancellationToken.IsCancellationRequested)
                        {cancelled=true;results[i]=new(row.ItemId,initial==null?"CancelledOrUnconfirmed":entered?"CallFailed":"Pending","Cancellation observed; a previously committed transaction is not assumed rolled back.",0,0);}
                        catch(EpisodeScopeJournal.InitialFinalizationException error)
                        {Log(error);results[i]=new(row.ItemId,"CallFailed",error.Message,error.Result.CopiedUsers,error.Result.MappedItems);}
                        catch(Exception error)
                        {Log(error);results[i]=new(row.ItemId,"CallFailed",error.GetType().Name+": "+error.Message+"; inspect journal/current mapping before assuming rollback.",0,0);}
                    }
                    Publish("Executing",10+80d*(i+1)/plan.Length);
                    if(cancelled||initial!=null&&results[i].State!="Applied")break;
                }
                if(cancelled||token.IsCancellationRequested)
                {cancelled=true;reconciliation=expected==null?"Deferred after cancellation; committed items and durable intents remain. Explicit recovery is required.":"Deferred after cancellation; committed items and durable intents remain. Old retained policy preserved; explicitly retry unretained IDs with Append.";return Publish("Cancelled",90);}
                Publish("Reconciling",90);token.ThrowIfCancellationRequested();
                return Reconcile();
            }
            catch(OperationCanceledException error)when(token.IsCancellationRequested||error.CancellationToken.IsCancellationRequested)
            {cancelled=true;reconciliation="Cancelled; reconciliation/publication was not completed. Inspect journal/current mapping.";return Publish("Cancelled",90);}
            catch(Exception error)
            {Log(error);reconciliation=error.GetType().Name+": "+error.Message;return Publish(expected==null?"Failed":"Suspended",100);}
        }
        finally{admission.Release();}
    }
}
