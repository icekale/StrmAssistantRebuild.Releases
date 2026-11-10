using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Data;

namespace StrmAssistantRebuild.Common;

/// <summary>A synchronous, exact-entity key override for a controlled save, not a global library policy.</summary>
public static class ScopedEpisodeUserDataKey
{
    private const string Owner="StrmAssistantRebuild.ScopedEpisodeKey.4.10.2";
    private static readonly object Gate=new();
    private static readonly List<MethodInfo> installed=new();
    private static Harmony? patcher;
    private sealed record Frame(Episode Item,long Id,string NativeKey,string ScopedKey);
    [ThreadStatic] private static Frame? frame;
    [ThreadStatic] private static bool nativeRead;
    private sealed record Rule(long Id,Guid Guid,long LibraryId,string NativeKey,string ScopedKey,string EpisodeKey,string SeriesKey);
    private sealed record Policy(ILibraryManager Library,IReadOnlyDictionary<long,Rule> Rules);
    private static Policy? policy;
    public static int RetainedCount=>Volatile.Read(ref policy)?.Rules.Count??0;
    internal static bool IsInactive=>Volatile.Read(ref policy)==null;
    internal static void RequireInactive()
    {
        if(!IsInactive)throw new InvalidOperationException("Retained policy conflict: initial migration requires an inactive policy.");
    }
    // Opaque identity plus detached immutable rows; the policy dictionary never escapes this type.
    internal sealed class RetainedSnapshot
    {
        private readonly Policy captured;
        internal RetainedSnapshot()
        {
            captured=Volatile.Read(ref policy)??throw new InvalidOperationException("Append requires a nonempty retained policy.");
            Rows=Array.AsReadOnly(captured.Rules.Values.Select(r=>new EpisodeLibraryScopePlan.Row(r.Id,r.Guid,r.LibraryId,r.NativeKey,r.ScopedKey,r.ScopedKey,r.EpisodeKey,r.SeriesKey,"AlreadyScoped",Array.Empty<string>())).ToArray());
        }
        internal IReadOnlyList<EpisodeLibraryScopePlan.Row> Rows{get;}
        internal int Count=>captured.Rules.Count;
        internal bool IsCurrent=>ReferenceEquals(captured,Volatile.Read(ref policy));
        internal bool Matches(EpisodeLibraryScopePlan.Row row)=>captured.Rules.TryGetValue(row.ItemId,out var rule)&&rule==FromRow(row);
        internal void RequireCurrent()
        {
            if(!IsCurrent)throw new InvalidOperationException("Retained policy conflict: the expected policy was replaced or cleared.");
            if(!Healthy(Targets()))throw new InvalidOperationException("Retained key hook ownership changed.");
        }
        internal static RetainedSnapshot Capture(ILibraryManager library)
        {
            if(!Monitor.TryEnter(Gate))throw new InvalidOperationException("Another key operation is in progress.");
            try
            {
                var current=Volatile.Read(ref policy);
                if(current==null||current.Rules.Count==0)throw new InvalidOperationException("Append requires a nonempty retained policy.");
                if(!ReferenceEquals(current.Library,library))throw new InvalidOperationException("Retained library manager differs.");
                var snapshot=new RetainedSnapshot();snapshot.RequireCurrent();return snapshot;
            }
            finally{Monitor.Exit(Gate);}
        }
    }
    public static string ReadNativeKey(BaseItem item,IDataContext? context=null)
    {
        ArgumentNullException.ThrowIfNull(item);var previous=nativeRead;nativeRead=true;
        try{return item.GetUserDataKey(context);}
        finally{nativeRead=previous;}
    }
    public static string Detail{get;private set;}="Inactive; ordinary native keys unchanged. Full library progress separation is not enabled.";
    internal static MethodInfo[] Targets()=>new[]{
        typeof(BaseItem).GetMethod(nameof(BaseItem.GetUserDataKey),new[]{typeof(IDataContext)})!,
        typeof(Episode).GetMethod("GetUserDataKeyInternal",BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly,null,new[]{typeof(IDataContext)},null)!
    };
    public static bool Probe(Version version,out string detail)
    {
        try
        {
            var targets=Targets();var assembly=typeof(BaseItem).Assembly;
            var valid=version==new Version(4,10,2,0)&&assembly.GetName().Name=="MediaBrowser.Controller"&&assembly.GetName().Version==version&&assembly.ManifestModule.ModuleVersionId==new Guid("58dae430-7295-47bb-a62f-b26bf0d89604")&&
                targets[0].DeclaringType==typeof(BaseItem)&&targets[0].IsPublic&&!targets[0].IsVirtual&&targets[1].DeclaringType==typeof(Episode)&&targets[1].IsFamily&&targets[1].IsVirtual&&!targets[1].IsFinal&&
                targets[0].Attributes==(MethodAttributes)0x86&&targets[1].Attributes==(MethodAttributes)0xC4&&
                targets.All(m=>!m.IsStatic&&!m.ContainsGenericParameters&&m.ReturnType==typeof(string)&&m.GetMethodImplementationFlags()==0)&&
                Pin(targets[0],0x06000DC1,18,8,false,0,"4934EE5055AE7580A1DA1967AB1A175A331EA881454E38A7AE6E24ECBAE4C106")&&
                Pin(targets[1],0x060012C0,145,4,true,0x110000E8,"E7EFBD400B99D43DD7F3230CD73A76D0D59B9E9B90F474AD3C7F5B158F85AC16")&&
                Convert.ToHexString(SHA256.HashData(targets[1].Module.ResolveSignature(0x110000E8)))=="D84907B5250B859510C7267009E164BA40202C17E9F074850995989F6E52D4BE";
            detail=valid?"Two native Episode key entrypoints verified; synchronous explicit-save primitive only, not migration/activation.":"Native Episode key contract differs; no replacement installed.";return valid;
        }
        catch(Exception error){detail="Native Episode key probe failed: "+error.GetType().Name;return false;}
    }
    private static bool Pin(MethodInfo method,int token,int length,int stack,bool init,int local,string hash)
    {
        var body=method.GetMethodBody();var il=body?.GetILAsByteArray();
        return method.MetadataToken==token&&body!=null&&il!=null&&il.Length==length&&body.MaxStackSize==stack&&body.InitLocals==init&&body.LocalSignatureMetadataToken==local&&body.ExceptionHandlingClauses.Count==0&&
            Convert.ToHexString(SHA256.HashData(il))==hash;
    }
    public static void Run(Version version,Episode item,string expectedNativeKey,string scopedKey,Action save,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(item);ArgumentNullException.ThrowIfNull(save);
        if(item.InternalId<=0||!item.IndexNumber.HasValue||!item.ParentIndexNumber.HasValue||string.IsNullOrWhiteSpace(item.PresentationUniqueKey)||string.IsNullOrWhiteSpace(expectedNativeKey)||
            string.IsNullOrWhiteSpace(scopedKey)||!scopedKey.StartsWith("strmassistant.rebuild.library.v1/",StringComparison.Ordinal)||scopedKey.Length<="strmassistant.rebuild.library.v1/".Length||scopedKey.Any(char.IsControl)||scopedKey==expectedNativeKey)throw new ArgumentException("A persisted episode, its expected native key and an independent scoped key are required.");
        if(!Monitor.TryEnter(Gate))throw new InvalidOperationException("Another controlled key save is in progress.");
        try
        {
            if(frame!=null)throw new InvalidOperationException("Nested scoped key saves are not supported.");
            if(Volatile.Read(ref policy)!=null)throw new InvalidOperationException("Temporary key overrides are unavailable while retained mappings are active.");
            token.ThrowIfCancellationRequested();RemoveOwned();
            if(!Probe(version,out var detail))throw new NotSupportedException(detail);
            var targets=Targets();
            if(targets.Any(m=>Harmony.GetPatchInfo(m)?.Owners.Any()==true))throw new InvalidOperationException("Native key patch ownership differs.");
            if(!string.Equals(((BaseItem)item).GetUserDataKey(null),expectedNativeKey,StringComparison.Ordinal))throw new InvalidOperationException("Native key changed before the controlled save.");
            Exception? failure=null;
            try
            {
                foreach(var method in targets){installed.Add(method);(patcher??=new Harmony(Owner)).Patch(method,postfix:new HarmonyMethod(typeof(ScopedEpisodeUserDataKey),nameof(After)));}
                frame=new(item,item.InternalId,expectedNativeKey,scopedKey);
                Detail="Explicit synchronous Episode key save in progress; other entities/threads remain native.";
                if(((BaseItem)item).GetUserDataKey(null)!=scopedKey)throw new InvalidOperationException("Native key interception was not observed.");
                token.ThrowIfCancellationRequested();save();
            }
            catch(Exception error){failure=error;throw;}
            finally
            {
                frame=null;
                try{RemoveOwned();Detail="Inactive; explicit save scope ended and owned key hooks removed.";}
                catch{Detail="Inactive; key hook cleanup pending, no thread-local replacement remains.";if(failure==null)throw;}
            }
        }
        finally{Monitor.Exit(Gate);}
    }
    public static void RetainMapped(Version version,ILibraryManager library,IUserDataManager data,IEnumerable<EpisodeLibraryScopePlan.Row> rows,long probeUserId,CancellationToken token)=>RetainMappedCore(version,library,data,rows,probeUserId,token,null,null);
    internal static void RetainMappedValidated(Version version,ILibraryManager library,IUserDataManager data,IEnumerable<EpisodeLibraryScopePlan.Row> rows,long probeUserId,CancellationToken token,Action validateJournal)
    {
        ArgumentNullException.ThrowIfNull(validateJournal);RetainMappedCore(version,library,data,rows,probeUserId,token,null,validateJournal);
    }
    internal static RetainedSnapshot AppendMapped(Version version,ILibraryManager library,IUserDataManager data,IEnumerable<EpisodeLibraryScopePlan.Row> rows,long probeUserId,CancellationToken token,RetainedSnapshot expected,Action validateJournal)
    {
        ArgumentNullException.ThrowIfNull(expected);ArgumentNullException.ThrowIfNull(validateJournal);
        return RetainMappedCore(version,library,data,rows,probeUserId,token,expected,validateJournal);
    }
    internal static RetainedSnapshot RetainInitialMapped(Version version,ILibraryManager library,IUserDataManager data,IEnumerable<EpisodeLibraryScopePlan.Row> rows,long probeUserId,CancellationToken token,Action validateJournal)
    {
        ArgumentNullException.ThrowIfNull(validateJournal);
        return RetainMappedCore(version,library,data,rows,probeUserId,token,null,validateJournal,true);
    }
    private static RetainedSnapshot RetainMappedCore(Version version,ILibraryManager library,IUserDataManager data,IEnumerable<EpisodeLibraryScopePlan.Row> rows,long probeUserId,CancellationToken token,RetainedSnapshot? expected,Action? validateJournal,bool initial=false)
    {
        ArgumentNullException.ThrowIfNull(library);ArgumentNullException.ThrowIfNull(data);ArgumentNullException.ThrowIfNull(rows);
        if(!Monitor.TryEnter(Gate))throw new InvalidOperationException("Another key operation is in progress.");
        try
        {
            if(frame!=null)throw new InvalidOperationException("A temporary key save is active.");
            expected?.RequireCurrent();if(initial)RequireInactive();
            if(!Probe(version,out var detail))throw new NotSupportedException(detail);
            var selected=new List<EpisodeLibraryScopePlan.Row>();
            foreach(var row in rows){token.ThrowIfCancellationRequested();if(selected.Count==4096)throw new InvalidOperationException("Retained mapping budget exceeded.");selected.Add(row);}
            if(selected.Count==0)throw new ArgumentException("At least one already-scoped mapping is required.");
            var rules=new Dictionary<long,Rule>();
            foreach(var row in selected)
            {
                if(row==null||row.Status!="AlreadyScoped"||row.Issues==null||row.Issues.Length!=0||row.ItemId<=0||row.ItemGuid==Guid.Empty||row.LibraryId<=0||row.StoredKey!=row.ScopedKey)throw new ArgumentException("Only verified already-scoped rows may be retained.");
                rules.Add(row.ItemId,FromRow(row));
            }
            if(expected!=null&&(rules.Count<=expected.Count||expected.Rows.Any(r=>!rules.TryGetValue(r.ItemId,out var rule)||rule!=FromRow(r))))throw new InvalidOperationException("Append publication must preserve every retained rule unchanged in a strict superset.");
            var actual=EpisodeLibraryScopePlan.Build(version,library,data,rules.Keys,probeUserId,token);
            if(actual.Length!=rules.Count||actual.Any(r=>r.Status!="AlreadyScoped"||r.Issues.Length!=0||!rules.TryGetValue(r.ItemId,out var expected)||FromRow(r)!=expected))throw new InvalidOperationException("Stored mapping no longer matches retained request.");
            token.ThrowIfCancellationRequested();expected?.RequireCurrent();if(initial)RequireInactive();var targets=Targets();
            if(Volatile.Read(ref policy)!=null)
            {
                if(!Healthy(targets))throw new InvalidOperationException("Retained key hook ownership changed.");
            }
            else
            {
                RemoveOwned();
                if(targets.Any(m=>Harmony.GetPatchInfo(m)?.Owners.Any()==true))throw new InvalidOperationException("Native key patch ownership differs.");
                try
                {
                    foreach(var method in targets){installed.Add(method);(patcher??=new Harmony(Owner)).Patch(method,postfix:new HarmonyMethod(typeof(ScopedEpisodeUserDataKey),nameof(After)));}
                    if(!Healthy(targets))throw new InvalidOperationException("Retained key installation did not match the expected hooks.");
                }
                catch{Detail="Inactive; retained-key installation failed.";try{RemoveOwned();}catch{Detail="Inactive; retained-key installation failed and hook cleanup is pending.";}throw;}
            }
            if(token.IsCancellationRequested){if(Volatile.Read(ref policy)==null)RemoveOwned();token.ThrowIfCancellationRequested();}
            // The expected-reference comparison is inside Gate and after every external read callback.
            if(validateJournal!=null)
            {
                try{if(initial)RequireInactive();validateJournal();token.ThrowIfCancellationRequested();expected?.RequireCurrent();if(initial)RequireInactive();}
                catch{if(Volatile.Read(ref policy)==null)RemoveOwned();throw;}
            }
            if(initial)
            {
                // Null is an explicit expectation, not the public retain API's unconditional replacement.
                if(Interlocked.CompareExchange(ref policy,new(library,rules),null)!=null)
                    throw new InvalidOperationException("Retained policy conflict: initial publication lost the inactive comparison.");
            }
            else Volatile.Write(ref policy,new(library,rules));
            Detail="Explicit in-memory retained Episode mappings active; ordinary compatible saves retain scoped keys. Global migration is not activated; restart recovery is managed separately.";
            return new RetainedSnapshot();
        }
        finally{Monitor.Exit(Gate);}
    }
    public static void ClearRetained()
    {
        if(!Monitor.TryEnter(Gate))throw new InvalidOperationException("Another key operation is in progress.");
        try
        {
            if(frame!=null)throw new InvalidOperationException("A temporary key save is active.");
            Volatile.Write(ref policy,null);
            try{RemoveOwned();Detail="Retained key policy cleared; database mappings were not restored by this operation.";}
            catch{Detail="Inactive; retained-key hook cleanup pending. Database mappings were not restored.";throw;}
        }
        finally{Monitor.Exit(Gate);}
    }
    internal static bool SameIdentity(EpisodeLibraryScopePlan.Row a,EpisodeLibraryScopePlan.Row b)=>FromRow(a)==FromRow(b);
    private static Rule FromRow(EpisodeLibraryScopePlan.Row row)=>new(row.ItemId,row.ItemGuid,row.LibraryId,row.NativeKey,row.ScopedKey,row.EpisodeKey,row.SeriesKey);
    private static bool Healthy(MethodInfo[] methods)
    {
        var hook=typeof(ScopedEpisodeUserDataKey).GetMethod(nameof(After),BindingFlags.Static|BindingFlags.NonPublic)!;
        return methods.All(m=>{var p=Harmony.GetPatchInfo(m);return p!=null&&p.Postfixes.Count==1&&p.Postfixes[0].owner==Owner&&p.Postfixes[0].PatchMethod==hook&&p.Postfixes[0].priority==Priority.Normal&&p.Postfixes[0].before.Length==0&&p.Postfixes[0].after.Length==0&&!p.Postfixes[0].debug&&p.Prefixes.Count==0&&p.Transpilers.Count==0&&p.Finalizers.Count==0&&p.InnerPrefixes.Count==0&&p.InnerPostfixes.Count==0;});
    }
    private static void After(BaseItem __instance,IDataContext? __0,ref string __result)
    {
        if(nativeRead)return;
        var scope=frame;
        if(scope!=null&&ReferenceEquals(scope.Item,__instance))
        {
            if(__instance.InternalId!=scope.Id)throw new InvalidOperationException("Episode identity changed during the controlled save.");
            if(__result==scope.ScopedKey)return;
            if(!string.Equals(__result,scope.NativeKey,StringComparison.Ordinal))throw new InvalidOperationException("Episode native key changed during the controlled save.");
            __result=scope.ScopedKey;return;
        }
        var retained=Volatile.Read(ref policy);
        if(retained==null||__instance is not Episode episode||!retained.Rules.TryGetValue(episode.InternalId,out var rule)||episode.Id!=rule.Guid)return;
        if(!Healthy(Targets()))throw new InvalidOperationException("Retained key hook ownership changed.");
        if(episode.PresentationUniqueKey!=rule.EpisodeKey||episode.SeriesPresentationUniqueKey!=rule.SeriesKey)throw new InvalidOperationException("Retained Episode presentation identity needs reconciliation.");
        if(__result==rule.ScopedKey)return;
        if(!string.Equals(__result,rule.NativeKey,StringComparison.Ordinal))throw new InvalidOperationException("Retained Episode native key needs reconciliation.");
        // Preserve the native transaction context; opening an independent read lease here would
        // deadlock while SaveItems owns the pool writer. No lock or new connection in this callback.
        var folders=retained.Library.GetCollectionFolders(episode,__0,CancellationToken.None);
        if(folders.Length==0||folders.Any(f=>f is not CollectionFolder||f.InternalId<=0)||!folders.Select(f=>f.InternalId).Distinct().SequenceEqual(new[]{rule.LibraryId}))throw new InvalidOperationException("Retained Episode library membership needs reconciliation.");
        if(!ReferenceEquals(retained,Volatile.Read(ref policy)))throw new InvalidOperationException("Retained Episode policy changed during key calculation.");
        __result=rule.ScopedKey;
    }
    private static void RemoveOwned()
    {
        Exception? failure=null;
        for(var i=installed.Count-1;i>=0;i--)
        {
            try{patcher!.Unpatch(installed[i],HarmonyPatchType.All,Owner);installed.RemoveAt(i);}
            catch(Exception error){failure??=error;}
        }
        if(failure!=null)throw new InvalidOperationException("Owned key hook cleanup did not complete.",failure);
    }
}
