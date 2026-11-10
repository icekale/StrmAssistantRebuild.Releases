using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace StrmAssistantRebuild.Common;

/// <summary>Explicit single-Episode copy-and-map transaction; not an automatic or persistent key policy.</summary>
public static class NativeEpisodeScopeTransition
{
    internal const string SnapshotSql="select cast(e.Id as text),hex(e.guid),cast(e.type as text),cast(e.IndexNumber as text),cast(e.ParentIndexNumber as text),cast(e.SeriesId as text),e.PresentationUniqueKey,e.SeriesPresentationUniqueKey,ek.UserDataKey,cast(s.Id as text),hex(s.guid),cast(s.type as text),sk.UserDataKey,s.PresentationUniqueKey,e.ProviderIds,s.ProviderIds,typeof(e.Path)||':'||hex(e.Path)||';'||typeof(e.ExternalId)||':'||hex(e.ExternalId)||';'||typeof(e.ParentId)||':'||hex(e.ParentId)||';'||typeof(e.AlbumId)||':'||hex(e.AlbumId)||';'||typeof(e.ProviderIds)||':'||hex(e.ProviderIds)||';'||typeof(s.Path)||':'||hex(s.Path)||';'||typeof(s.ExternalId)||':'||hex(s.ExternalId)||';'||typeof(s.ParentId)||':'||hex(s.ParentId)||';'||typeof(s.AlbumId)||':'||hex(s.AlbumId)||';'||typeof(s.ProviderIds)||':'||hex(s.ProviderIds) from main.MediaItems e join main.UserDataKeys2 ek on ek.Id=e.UserDataKeyId join main.MediaItems s on s.Id=e.SeriesId join main.UserDataKeys2 sk on sk.Id=s.UserDataKeyId where e.Id=? and coalesce(e.ExternalId,'')='' and coalesce(e.Path,'')!=''";
    internal const string BadChainSql="with recursive chain(Id,ParentId,Depth,Trail,Cycle) as (select Id,ParentId,0,','||Id||',',0 from main.MediaItems where Id=? union all select p.Id,p.ParentId,c.Depth+1,c.Trail||p.Id||',',instr(c.Trail,','||p.Id||',')>0 from chain c join main.MediaItems p on p.Id=c.ParentId where c.ParentId>0 and c.Depth<128 and c.Cycle=0) select count(*) from chain where Cycle=1 or ParentId<0 or (Depth=128 and ParentId>0) or (ParentId>0 and not exists(select 1 from main.MediaItems p where p.Id=chain.ParentId))";
    internal const string LibrariesSql="with recursive chain(Id,ParentId,Depth) as (select Id,ParentId,0 from main.MediaItems where Id=? union all select p.Id,p.ParentId,c.Depth+1 from chain c join main.MediaItems p on p.Id=c.ParentId where c.ParentId>0 and c.Depth<128) select distinct c.Id from main.MediaItems c join main.ItemLinks2 l on l.ItemId=c.Id and l.Type=8 where c.Type=4 and l.LinkedId in (select Id from chain) limit 4097";
    internal const string PeersSql="select Id from main.MediaItems where Type=8 and (PresentationUniqueKey=? COLLATE BINARY or SeriesPresentationUniqueKey=? COLLATE BINARY) limit 4097";
    internal const string EnsureKeySql="insert or ignore into main.UserDataKeys2(UserDataKey) values(?)";
    internal const string ExactKeySql="select count(*) from main.UserDataKeys2 where UserDataKey=? COLLATE BINARY";
    internal const string MissingUsersSql="select count(*) from main.UserDatas d join main.UserDataKeys2 s on s.Id=d.UserDataKeyId where s.UserDataKey=? COLLATE BINARY and not exists(select 1 from main.UserDatas t join main.UserDataKeys2 k on k.Id=t.UserDataKeyId where k.UserDataKey=? COLLATE BINARY and t.userId=d.userId)";
    internal const string CopyAllSql="insert or ignore into main.UserDatas(UserDataKeyId,userId,rating,played,playCount,isFavorite,HideFromResume,playbackPositionTicks,LastPlayedDateInt,AudioStreamIndex,SubtitleStreamIndex,RatingLastModified,PlaystateLastModified,DateLastSearchedInt) select t.Id,d.userId,d.rating,d.played,d.playCount,d.isFavorite,d.HideFromResume,d.playbackPositionTicks,d.LastPlayedDateInt,d.AudioStreamIndex,d.SubtitleStreamIndex,d.RatingLastModified,d.PlaystateLastModified,d.DateLastSearchedInt from main.UserDatas d join main.UserDataKeys2 s on s.Id=d.UserDataKeyId join main.UserDataKeys2 t on t.UserDataKey=? COLLATE BINARY where s.UserDataKey=? COLLATE BINARY";
    internal const string MapSql="update main.MediaItems set UserDataKeyId=(select Id from main.UserDataKeys2 where UserDataKey=? COLLATE BINARY) where Id=? and Type=8 and hex(guid)=? and UserDataKeyId=(select Id from main.UserDataKeys2 where UserDataKey=? COLLATE BINARY)";
    internal const string ReadMapSql="select k.UserDataKey from main.MediaItems e join main.UserDataKeys2 k on k.Id=e.UserDataKeyId where e.Id=?";
    public sealed record Result(bool AlreadyScoped,int CopiedUsers,int MappedItems);

    public sealed record RestoreResult(bool AlreadyNative,int MappedItems);
    public static Result Apply(Version version,ILibraryManager library,IUserDataManager data,EmbyPathPolicy paths,string databasePath,EpisodeLibraryScopePlan.Row plan,CancellationToken token)=>Execute(version,library,data,paths,databasePath,plan,token,false);
    public static RestoreResult RestoreNative(Version version,ILibraryManager library,IUserDataManager data,EmbyPathPolicy paths,string databasePath,EpisodeLibraryScopePlan.Row plan,CancellationToken token)
    {
        var result=Execute(version,library,data,paths,databasePath,plan,token,true);
        return new RestoreResult(result.AlreadyScoped,result.MappedItems);
    }
    internal static Result ApplySelected(Version version,ILibraryManager library,IUserDataManager data,EmbyPathPolicy paths,string databasePath,EpisodeLibraryScopePlan.Row plan,CancellationToken token,Guid libraryGuid,Action validateEpoch,Action? validateBeforeWrite=null)
    {
        ArgumentNullException.ThrowIfNull(validateEpoch);
        if(libraryGuid==Guid.Empty)throw new ArgumentException("Selected CollectionFolder GUID required.",nameof(libraryGuid));
        return Execute(version,library,data,paths,databasePath,plan,token,false,libraryGuid,validateEpoch,validateBeforeWrite);
    }
    private static Result Execute(Version version,ILibraryManager library,IUserDataManager data,EmbyPathPolicy paths,string databasePath,EpisodeLibraryScopePlan.Row plan,CancellationToken token,bool restore,Guid? libraryGuid=null,Action? validateEpoch=null,Action? validateBeforeWrite=null)
    {
        if(restore&&ScopedEpisodeUserDataKey.RetainedCount!=0)throw new InvalidOperationException("Clear retained mappings and quiesce writers before restoring a native key.");
        ArgumentNullException.ThrowIfNull(library);ArgumentNullException.ThrowIfNull(plan);
        if(plan.ItemId<=0||plan.ItemGuid==Guid.Empty||plan.LibraryId<=0||plan.Issues==null||plan.Issues.Length!=0||plan.Status is not ("Ready" or "AlreadyScoped")||
            string.IsNullOrWhiteSpace(plan.NativeKey)||plan.NativeKey.Any(char.IsControl)||plan.NativeKey.StartsWith(EpisodeLibraryScopePlan.KeyPrefix,StringComparison.OrdinalIgnoreCase)||
            plan.ScopedKey!=EpisodeLibraryScopePlan.KeyPrefix+plan.LibraryId.ToString(CultureInfo.InvariantCulture)+"/"+plan.NativeKey||
            plan.StoredKey!=(plan.Status=="Ready"?plan.NativeKey:plan.ScopedKey)||string.IsNullOrWhiteSpace(plan.EpisodeKey)||string.IsNullOrWhiteSpace(plan.SeriesKey)||plan.EpisodeKey.Any(char.IsControl)||plan.SeriesKey.Any(char.IsControl))throw new ArgumentException("A valid current Episode plan is required.");
        token.ThrowIfCancellationRequested();validateEpoch?.Invoke();
        var snapshot=NativeUserDataCopy.WithDatabase(version,data,paths,databasePath,token,false,(api,db)=>ReadSnapshot(api,db,plan.ItemId));
        if(library.GetItemById(plan.ItemId) is not Episode item||item.InternalId!=plan.ItemId||item.Id!=plan.ItemGuid||!item.IsFileProtocol||!string.IsNullOrEmpty(item.ExternalId)||libraryGuid.HasValue&&(item.IsVirtualItem||string.IsNullOrWhiteSpace(item.Path))||
            !item.IndexNumber.HasValue||!item.ParentIndexNumber.HasValue||item.SeriesId<=0||item.PresentationUniqueKey!=plan.EpisodeKey||item.SeriesPresentationUniqueKey!=plan.SeriesKey)throw new InvalidOperationException("Episode plan identity changed.");
        var folders=library.GetCollectionFolders(item,token);
        if(folders.Length==0||folders.Any(f=>f is not CollectionFolder||f.InternalId<=0||libraryGuid.HasValue&&f.Id!=libraryGuid.Value)||!folders.Select(f=>f.InternalId).Distinct().SequenceEqual(new[]{plan.LibraryId}))throw new InvalidOperationException("Episode library membership changed.");
        if(library.GetItemById(item.SeriesId) is not Series series||series.InternalId!=item.SeriesId||series.Id==Guid.Empty)throw new InvalidOperationException("Series identity missing.");
        if(!new[]{"Tvdb","Tmdb","Imdb"}.Any(k=>series.ProviderIds.TryGetValue(k,out var value)&&!string.IsNullOrEmpty(value)))throw new NotSupportedException("Only Series-provider Episode keys are supported by this transition.");
        var seriesKey=ScopedEpisodeUserDataKey.ReadNativeKey(series);
        var composed=seriesKey+item.ParentIndexNumber.Value.ToString("000")+item.IndexNumber.Value.ToString("000");
        if(composed!=plan.NativeKey||ScopedEpisodeUserDataKey.ReadNativeKey(item)!=plan.NativeKey||snapshot[0]!=plan.ItemId.ToString(CultureInfo.InvariantCulture)||snapshot[1]!=Convert.ToHexString(plan.ItemGuid.ToByteArray())||
            snapshot[2]!="8"||snapshot[3]!=item.IndexNumber.Value.ToString(CultureInfo.InvariantCulture)||snapshot[4]!=item.ParentIndexNumber.Value.ToString(CultureInfo.InvariantCulture)||
            snapshot[5]!=item.SeriesId.ToString(CultureInfo.InvariantCulture)||snapshot[6]!=plan.EpisodeKey||snapshot[7]!=plan.SeriesKey||snapshot[8]!=plan.StoredKey||snapshot[9]!=snapshot[5]||
            snapshot[10]!=Convert.ToHexString(series.Id.ToByteArray())||snapshot[11]!="6"||snapshot[12]!=seriesKey||snapshot[13]!=plan.SeriesKey||
            !ProvidersMatch(snapshot[14],item.ProviderIds)||!ProvidersMatch(snapshot[15],series.ProviderIds))throw new InvalidOperationException("Stored/native Episode dependency snapshot differs.");
        // Initial confirmation rechecks owner/current-user and journal bytes after every native
        // manager read, before taking the native writer. The transaction's epoch guard stays pure.
        validateBeforeWrite?.Invoke();token.ThrowIfCancellationRequested();
        return NativeUserDataCopy.WithDatabase(version,data,paths,databasePath,token,true,(api,db)=>
        {
            api.ValidateMapSchema(db);
            if(!snapshot.SequenceEqual(ReadSnapshot(api,db,plan.ItemId),StringComparer.Ordinal))throw new InvalidOperationException("Episode or Series changed after preparation.");
            RequireLibrary(api,db,plan.ItemId,plan.LibraryId);
            if(libraryGuid.HasValue)
            {
                var identity=api.Rows(db,"select hex(guid) from main.MediaItems where Id=? and Type=4",1,plan.LibraryId);
                if(identity.Count!=1||identity[0][0]!=Convert.ToHexString(libraryGuid.Value.ToByteArray()))throw new InvalidOperationException("Selected CollectionFolder database GUID changed.");
                token.ThrowIfCancellationRequested();validateEpoch!.Invoke();
            }
            var peers=api.Rows(db,PeersSql,1,plan.EpisodeKey,plan.SeriesKey);
            if(peers.Count>4096)throw new InvalidOperationException("Presentation conflict inspection budget exceeded.");
            foreach(var peer in peers){token.ThrowIfCancellationRequested();RequireLibrary(api,db,long.Parse(peer[0],CultureInfo.InvariantCulture),plan.LibraryId);}
            if(restore)
            {
                if(ScopedEpisodeUserDataKey.RetainedCount!=0)throw new InvalidOperationException("Retained mappings became active before native restoration.");
                if(plan.Status=="Ready")return new Result(true,0,0);
                if(api.Scalar(db,ExactKeySql,plan.NativeKey)!=1)throw new InvalidOperationException("Original native key is missing or differs in casing.");
                token.ThrowIfCancellationRequested();api.Execute(db,MapSql,plan.NativeKey,plan.ItemId,Convert.ToHexString(plan.ItemGuid.ToByteArray()),plan.ScopedKey);
                if((int)api.Changes.GetValue(db)! !=1||api.Rows(db,ReadMapSql,1,plan.ItemId).Single()[0]!=plan.NativeKey)throw new InvalidOperationException("Native key restoration did not switch exactly once.");
                return new Result(false,0,1); // No key creation or userdata copy/merge/deletion on restoration.
            }
            if(libraryGuid.HasValue){token.ThrowIfCancellationRequested();validateEpoch!.Invoke();}
            if(plan.Status=="AlreadyScoped")return new Result(true,0,0);
            api.Execute(db,EnsureKeySql,plan.ScopedKey);
            if(api.Scalar(db,ExactKeySql,plan.ScopedKey)!=1)throw new InvalidOperationException("Target key casing/identity differs from plan.");
            var missing=api.Scalar(db,MissingUsersSql,plan.NativeKey,plan.ScopedKey);token.ThrowIfCancellationRequested();
            validateEpoch?.Invoke();api.Execute(db,CopyAllSql,plan.ScopedKey,plan.NativeKey);var copied=(int)api.Changes.GetValue(db)!;
            if(copied!=missing)throw new InvalidOperationException("Missing-user copy count differs.");
            token.ThrowIfCancellationRequested();validateEpoch?.Invoke();api.Execute(db,MapSql,plan.ScopedKey,plan.ItemId,Convert.ToHexString(plan.ItemGuid.ToByteArray()),plan.NativeKey);
            if((int)api.Changes.GetValue(db)! !=1||api.Rows(db,ReadMapSql,1,plan.ItemId).Single()[0]!=plan.ScopedKey)throw new InvalidOperationException("Episode key mapping did not switch exactly once.");
            return new Result(false,copied,1);
        },validateBeforeWrite!=null?validateEpoch:null);
    }
    private static string[] ReadSnapshot(NativeUserDataCopy.Api api,object db,long id)
    {
        var rows=api.Rows(db,SnapshotSql,17,id);
        if(rows.Count!=1)throw new InvalidOperationException("Expected one complete stored Episode/Series snapshot.");
        return rows[0];
    }
    private static void RequireLibrary(NativeUserDataCopy.Api api,object db,long id,long expected)
    {
        if(api.Scalar(db,BadChainSql,id)!=0)throw new InvalidOperationException("Cyclic, dangling or oversized physical parent chain.");
        var memberships=api.Rows(db,LibrariesSql,1,id);
        if(memberships.Count!=1||memberships[0][0]!=expected.ToString(CultureInfo.InvariantCulture))throw new InvalidOperationException("Physical membership is ambiguous or crosses the planned library.");
    }
    private static bool ProvidersMatch(string stored,IEnumerable<KeyValuePair<string,string>> providers)
    {
        var parsed=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in stored.Split('|',StringSplitOptions.RemoveEmptyEntries)){var equals=entry.IndexOf('=');if(equals<0)return false;parsed[entry[..equals]]=entry[(equals+1)..];}
        var expected=providers.Where(p=>!string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
        return parsed.Count==expected.Count&&parsed.All(p=>expected.TryGetValue(p.Key,out var value)&&string.Equals(p.Value,value,StringComparison.Ordinal));
    }
}
