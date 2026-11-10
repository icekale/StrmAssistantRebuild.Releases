using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;

/// <summary>One non-blocking startup attempt for an explicitly opted-in existing journal. Never starts a migration.</summary>
public sealed class EpisodeScopeStartup
{
    public sealed record Result(string State,string Detail,EpisodeScopeBatch.Report? Recovery);
    private readonly EpisodeScopeJournal journal;
    private readonly EpisodeScopeBatch batch;
    private readonly Func<bool> startupReady;
    private readonly Func<long> chooseProbeUser;
    private readonly ILogger logger;
    private readonly TimeSpan readinessTimeout;
    private readonly CancellationTokenSource stopping=new();
    private readonly object gate=new();
    private readonly EpisodeScopeCatchup? catchup;
    private CancellationTokenSource? workerLifetime;
    private Task? lifetimeCleanup;
    private long ownedEpoch;
    private Task? completion;
    private Result status=new("NotStarted","Startup recovery has not been requested by the host entrypoint.",null);
    public Result Status=>Volatile.Read(ref status);
    public Task Completion=>Volatile.Read(ref completion)??Task.CompletedTask;
    public EpisodeScopeStartup(EpisodeScopeJournal journal,EpisodeScopeBatch batch,Func<bool> startupReady,Func<long> chooseProbeUser,ILogger logger,TimeSpan? readinessTimeout=null,EpisodeScopeCatchup? catchup=null)
    {
        ArgumentNullException.ThrowIfNull(journal);ArgumentNullException.ThrowIfNull(batch);ArgumentNullException.ThrowIfNull(startupReady);ArgumentNullException.ThrowIfNull(chooseProbeUser);ArgumentNullException.ThrowIfNull(logger);
        this.journal=journal;this.batch=batch;this.startupReady=startupReady;this.chooseProbeUser=chooseProbeUser;this.logger=logger;
        this.catchup=catchup;this.readinessTimeout=readinessTimeout??TimeSpan.FromSeconds(60);
        if(this.readinessTimeout<TimeSpan.Zero||this.readinessTimeout>TimeSpan.FromMinutes(5))throw new ArgumentOutOfRangeException(nameof(readinessTimeout));
    }
    public Task Start(CancellationToken token)
    {
        lock(gate)
        {
            // Run must return before Emby sets IsStartupComplete. Do not run or wait inline in it.
            if(completion==null)Volatile.Write(ref completion,Task.Run(()=>Execute(token),CancellationToken.None));
            return completion!;
        }
    }
    public void Stop()
    {
        try{stopping.Cancel();}catch(Exception error){Log(error);}
        DisableOwned();
        lock(gate)if(completion==null)Set("Cancelled","Stopped before startup recovery began.",null);
        if(Completion.IsCompleted)ReleaseLifetime();
    }
    private void ReleaseLifetime()
    {
        var lifetime=Interlocked.Exchange(ref workerLifetime,null);if(lifetime==null)return;
        // Linked CTS Dispose can wait for a source callback. Never make a reentrant Stop wait for
        // its own cancellation chain; this bounded, observed cleanup owns no worker or policy work.
        Volatile.Write(ref lifetimeCleanup,Task.Run(()=>{try{lifetime.Dispose();}catch(Exception error){Log(error);}}));
    }
    private void DisableOwned()
    {
        var owned=Interlocked.Read(ref ownedEpoch);if(owned!=0)catchup?.DisableExpected(owned);
    }
    private async Task Execute(CancellationToken caller)
    {
        var lifetime=CancellationTokenSource.CreateLinkedTokenSource(caller,stopping.Token);var token=lifetime.Token;
        Volatile.Write(ref workerLifetime,lifetime);
        // Keep the caller/Stop linkage alive after a successful startup, for this worker generation only.
        _=token.Register(DisableOwned);
        EpisodeScopeBatch.Report? recovered=null;var activated=false;
        try
        {
            token.ThrowIfCancellationRequested();
            var requested=journal.StartupRecoveryRequested(token);token.ThrowIfCancellationRequested();
            if(!requested){Set("Disabled","No explicit startup-recovery intent in the current journal.",null);return;}
            var bindings=journal.GetStartupCatchupLibraries(token);token.ThrowIfCancellationRequested();
            var expectedEpoch=catchup?.ConfigurationEpoch??0;
            if(bindings.Count!=0&&catchup==null){Set("Suspended","Stored startup catch-up selection requires the optional catch-up service; recovery was not attempted.",null);return;}
            Set("Waiting","Waiting asynchronously for the host startup-complete signal; this is not a writer-drain barrier.",null);
            using(var readiness=CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                readiness.CancelAfter(readinessTimeout);
                try{while(!startupReady())await Task.Delay(100,readiness.Token).ConfigureAwait(false);}
                catch(OperationCanceledException)when(readiness.IsCancellationRequested&&!token.IsCancellationRequested)
                {token.ThrowIfCancellationRequested();Set("Suspended","Host startup readiness timed out; no recovery was attempted.",null);return;}
            }
            token.ThrowIfCancellationRequested();
            // Disarming changes future startup intent, not already-published rules. Recheck after
            // waiting, but do not claim an atomic transaction with a concurrent settings writer.
            requested=journal.StartupRecoveryRequested(token);token.ThrowIfCancellationRequested();
            if(!requested){Set("Disabled","Startup-recovery intent was disabled while waiting.",null);return;}
            EpisodeScopeJournal.Entry[]? intent=null;
            if(bindings.Count!=0)intent=journal.RequireStartupCatchupLibraries(bindings,token);
            var probeUser=chooseProbeUser();token.ThrowIfCancellationRequested();
            _=journal.RequireStartupCatchupLibraries(bindings,token);
            if(probeUser<=0){Set("Suspended","No enabled local probe user is available for existing-journal recovery.",null);return;}
            if(bindings.Count!=0)
            {
                catchup!.RequireConfigurationEpoch(expectedEpoch,token);
                catchup.ValidateExpected(bindings,batch,token);
                // Both host callbacks and manager reads may run arbitrary code; re-read before Recover.
                intent=journal.RequireStartupCatchupLibraries(bindings,token);
                catchup.RequireConfigurationEpoch(expectedEpoch,token);
            }
            Set("Recovering","Reconciling only the existing explicitly opted-in journal.",null);
            recovered=await batch.RecoverAsync(probeUser,null,token).ConfigureAwait(false);
            if(bindings.Count==0||recovered.Cancelled||token.IsCancellationRequested||recovered.Phase!="Completed"||recovered.Retained==0)
            {
                Set(recovered.Cancelled||token.IsCancellationRequested?"Cancelled":recovered.Phase=="Completed"?"Completed":"Suspended",recovered.Reconciliation+(bindings.Count!=0&&recovered.Retained==0?" No retained records; catch-up remains disabled.":""),recovered);return;
            }
            _=journal.RequireStartupCatchupLibraries(bindings,token,intent);catchup!.RequireConfigurationEpoch(expectedEpoch,token);
            var retainedRows=intent!.Where(e=>e.Phase is "Prepared" or "Applied").Select(e=>e.Plan).ToArray();
            // This is a healthy current snapshot matching the recovered intent, not a claim that Recover
            // returned an exclusive publication handle (only Append exposes that separate fact).
            var owned=catchup.ConfigureExpected(bindings,probeUser,token,expectedEpoch,retainedRows,publish=>journal.WithStartupCatchupLibraries(bindings,token,intent!,publish));
            Interlocked.Exchange(ref ownedEpoch,owned);
            // Own ItemAdded/Append may already have extended this journal after publication.
            token.ThrowIfCancellationRequested();catchup.RequireConfigurationEpoch(owned,token);
            activated=true;Set("Completed",recovered.Reconciliation+" Startup catch-up configured for the stored library identities.",recovered);
        }
        catch(OperationCanceledException error)when(token.IsCancellationRequested||error.CancellationToken.IsCancellationRequested)
        {Set("Cancelled","Startup recovery cancelled; committed journal and retained facts require inspection.",recovered);}
        catch(Exception error){Log(error);Set("Suspended",(recovered?.Phase=="Completed"?"Recovery completed; catch-up activation suspended. ":"")+error.GetType().Name+": "+error.Message,recovered);}
        finally
        {
            if(!activated||token.IsCancellationRequested)
            {
                DisableOwned();ReleaseLifetime();
            }
        }
    }
    private void Set(string state,string detail,EpisodeScopeBatch.Report? recovery)=>Volatile.Write(ref status,new(state,detail,recovery));
    private void Log(Exception error)
    {
        try{logger.Warn("[StrmAssistantRebuild] Startup journal recovery: {0}: {1}",error.GetType().Name,error.Message);}catch{/* Observability must not suppress startup completion. */}
    }
}
