using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;

namespace StrmAssistantRebuild.Common;

/// <summary>Copy one existing user's key row only if the target row is missing. No item remap or activation.</summary>
public static class NativeUserDataCopy
{
    internal const string SourceExistsSql="select count(*) from main.UserDatas d join main.UserDataKeys2 k on k.Id=d.UserDataKeyId where k.UserDataKey=? and d.userId=?";
    internal const string InsertKeySql="insert or ignore into main.UserDataKeys2(UserDataKey) select ? where exists(select 1 from main.UserDatas d join main.UserDataKeys2 k on k.Id=d.UserDataKeyId where k.UserDataKey=? and d.userId=?)";
    internal const string CopySql="insert or ignore into main.UserDatas(UserDataKeyId,userId,rating,played,playCount,isFavorite,HideFromResume,playbackPositionTicks,LastPlayedDateInt,AudioStreamIndex,SubtitleStreamIndex,RatingLastModified,PlaystateLastModified,DateLastSearchedInt) select t.Id,d.userId,d.rating,d.played,d.playCount,d.isFavorite,d.HideFromResume,d.playbackPositionTicks,d.LastPlayedDateInt,d.AudioStreamIndex,d.SubtitleStreamIndex,d.RatingLastModified,d.PlaystateLastModified,d.DateLastSearchedInt from main.UserDatas d join main.UserDataKeys2 s on s.Id=d.UserDataKeyId join main.UserDataKeys2 t on t.UserDataKey=? where s.UserDataKey=? and d.userId=?";
    public sealed record Result(bool SourceExists,bool TargetAlreadyExisted,int CopiedRows);
    [ThreadStatic] private static bool copying;

    public static Result CopyMissing(Version version,IUserDataManager data,EmbyPathPolicy paths,string databasePath,long userId,string sourceKey,string targetKey,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(data);ArgumentNullException.ThrowIfNull(paths);
        if(version!=new Version(4,10,2,0))throw new NotSupportedException("Native copy supports the verified 4.10.2 contract only.");
        if(userId<=0||string.IsNullOrWhiteSpace(sourceKey)||sourceKey.Any(char.IsControl)||string.IsNullOrWhiteSpace(targetKey)||
            !targetKey.StartsWith(EpisodeLibraryScopePlan.KeyPrefix,StringComparison.Ordinal)||targetKey.Length<=EpisodeLibraryScopePlan.KeyPrefix.Length||targetKey.Any(char.IsControl)||string.Equals(sourceKey,targetKey,StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A user, source key and distinct scoped target key are required.");
        return WithDatabase(version,data,paths,databasePath,token,true,(api,db)=>
        {
            var source=api.Scalar(db,SourceExistsSql,sourceKey,userId)>0;
            var existed=api.Scalar(db,SourceExistsSql,targetKey,userId)>0;
            var copied=0;
            if(source&&!existed)
            {
                api.Execute(db,InsertKeySql,targetKey,sourceKey,userId);token.ThrowIfCancellationRequested();
                api.Execute(db,CopySql,targetKey,sourceKey,userId);copied=(int)api.Changes.GetValue(db)!;
                if(copied!=1)throw new InvalidOperationException("Expected exactly one missing user row to be copied.");
            }
            return new Result(source,existed,copied);
        });
    }
    internal static T WithDatabase<T>(Version version,IUserDataManager data,EmbyPathPolicy paths,string databasePath,CancellationToken token,bool write,Func<Api,object,T> action,Action? beforeCommit=null)
    {
        ArgumentNullException.ThrowIfNull(data);ArgumentNullException.ThrowIfNull(paths);
        if(version!=new Version(4,10,2,0))throw new NotSupportedException("Native database supports the verified 4.10.2 contract only.");
        var expected=paths.RequireWritablePath(databasePath);
        if(Path.GetFileName(expected)!="library.db")throw new ArgumentException("The verified library database path is required.");
        token.ThrowIfCancellationRequested();
        if(copying)throw new InvalidOperationException("Nested native copy is not supported.");
        copying=true;
        try
        {
            var api=new Api(data);
            // The native pool is non-reentrant. Caller must hold no native read/write lease, and
            // no manager/repository operation may be called while this write lease is held.
            using var db=(IDisposable)Invoke(api.Create,api.Pool,!write,token)!;
            if(db.GetType()!=api.ConnectionType)throw new NotSupportedException("Native connection implementation differs.");
            object?[] fileArgs={"main",null};
            if(!(bool)InvokeArgs(api.FileName,db,fileArgs)!||fileArgs[1] is not string actual||
                !string.Equals(paths.RequireWritablePath(actual),expected,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new IOException("Native connection is not the expected library database.");
            if((write&&(bool)api.ReadOnly.GetValue(db)!)||!(bool)api.AutoCommit.GetValue(db)!)throw new InvalidOperationException("Independent native connection required.");
            token.ThrowIfCancellationRequested();
            if(!write)return action(api,db);
            api.Execute(db,"BEGIN IMMEDIATE");
            try
            {
                api.ValidateSchema(db);
                token.ThrowIfCancellationRequested();
                var result=action(api,db);
                token.ThrowIfCancellationRequested();beforeCommit?.Invoke();token.ThrowIfCancellationRequested();api.Execute(db,"COMMIT");
                // Cancellation after commit must not report that this committed copy was undone.
                return result;
            }
            catch(Exception primary)
            {
                try{api.Execute(db,"ROLLBACK");}
                catch(Exception rollback){throw new AggregateException("Native copy failed and rollback did not complete.",primary,rollback);}
                throw;
            }
        }
        finally{copying=false;}
    }
    private static object? Invoke(MethodInfo method,object? instance,params object?[] args)=>InvokeArgs(method,instance,args);
    private static object? InvokeArgs(MethodInfo method,object? instance,object?[] args)
    {
        try{return method.Invoke(instance,args);}
        catch(TargetInvocationException error)when(error.InnerException!=null){ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
    }
    internal sealed class Api
    {
        internal readonly object Pool;
        internal readonly Type ConnectionType;
        internal readonly MethodInfo Create,FileName;
        internal readonly PropertyInfo ReadOnly,AutoCommit,Changes;
        private readonly MethodInfo prepare,parameters,indexer,bindString,bindLong,getString,getLong,isNull,columnMetadata;
        private static void Pin(Type type,string name,string assembly,Version version,string mvid)
        {
            if(type.FullName!=name||type.Assembly.GetName().Name!=assembly||type.Assembly.GetName().Version!=version||type.Module.ModuleVersionId!=Guid.Parse(mvid))throw new NotSupportedException("Native database contract differs: "+name);
        }
        private static object UserRepository(IUserDataManager manager)
        {
            Pin(manager.GetType(),"Emby.Server.Implementations.Library.UserDataManager","Emby.Server.Implementations",new Version(4,10,2,0),"2009e43d-fa42-4e57-a902-2784a8306072");
            return manager.GetType().GetProperty("Repository",BindingFlags.Public|BindingFlags.Instance)!.GetValue(manager)!;
        }
        internal Api(IUserDataManager manager):this(UserRepository(manager),false) { }
        internal Api(IItemRepository repository):this(repository,true) { }
        private Api(object repo,bool itemRepository)
        {
            var impl=new Version(4,10,2,0);const string im="2009e43d-fa42-4e57-a902-2784a8306072";
            Pin(repo.GetType(),itemRepository?"Emby.Server.Implementations.Data.SqliteItemRepository":"Emby.Server.Implementations.Data.SqliteUserDataRepository","Emby.Server.Implementations",impl,im);
            Pool=repo.GetType().GetProperty("ConnectionManager",BindingFlags.Public|BindingFlags.Instance)!.GetValue(repo)!;
            Pin(Pool.GetType(),"Emby.Sqlite.PooledDatabaseConnectionManager","Emby.Sqlite",impl,"9d0a783b-ba0f-47cf-a81b-9564f68cec21");
            Create=Pool.GetType().GetMethod("CreateConnection",new[]{typeof(bool),typeof(CancellationToken)})!;
            var connection=Create.ReturnType;var pretty=connection.Assembly;
            Pin(connection,"SQLitePCL.pretty.IDatabaseConnection","SQLitePCL.pretty",new Version(1,2,2,0),"356e36ae-63f7-443b-a072-daa13c690f54");
            ConnectionType=pretty.GetType("SQLitePCL.pretty.SQLiteDatabaseConnection",true)!;
            FileName=ConnectionType.GetMethod("TryGetFileName",new[]{typeof(string),typeof(string).MakeByRefType()})!;
            ReadOnly=connection.GetProperty("IsReadOnly")!;AutoCommit=connection.GetProperty("IsAutoCommit")!;Changes=connection.GetProperty("Changes")!;
            columnMetadata=connection.GetMethod("GetTableColumnMetadata",new[]{typeof(string),typeof(string),typeof(string)})!;
            prepare=pretty.GetType("SQLitePCL.pretty.DatabaseConnection",true)!.GetMethod("PrepareStatement",new[]{connection,typeof(string)})!;
            var statement=prepare.ReturnType;parameters=statement.GetProperty("BindParameters")!.GetMethod!;
            var ordered=parameters.ReturnType;
            indexer=ordered.GetInterfaces().Append(ordered).SelectMany(t=>t.GetProperties()).First(p=>p.Name=="Item"&&p.GetIndexParameters().Select(a=>a.ParameterType).SequenceEqual(new[]{typeof(int)})).GetMethod!;
            var parameter=indexer.ReturnType;bindString=parameter.GetMethod("Bind",new[]{typeof(string)})!;bindLong=parameter.GetMethod("Bind",new[]{typeof(long)})!;
            var result=pretty.GetType("SQLitePCL.pretty.IResultSet",true)!;
            getString=result.GetMethod("GetString",new[]{typeof(int)})!;getLong=result.GetMethod("GetInt64",new[]{typeof(int)})!;isNull=result.GetMethod("IsDBNull",new[]{typeof(int)})!;
        }
        private IDisposable Prepare(object db,string sql,object[] values)
        {
            var statement=(IDisposable)Invoke(prepare,null,db,sql)!;
            try
            {
                var args=Invoke(parameters,statement)!;
                for(var i=0;i<values.Length;i++)
                {
                    var parameter=Invoke(indexer,args,i)!;
                    if(values[i] is string s)Invoke(bindString,parameter,s);
                    else if(values[i] is long n)Invoke(bindLong,parameter,n);
                    else throw new ArgumentException("Unexpected native SQL parameter type.");
                }
                return statement;
            }
            catch{statement.Dispose();throw;}
        }
        internal void Execute(object db,string sql,params object[] values){using var s=Prepare(db,sql,values);((IEnumerator)s).MoveNext();}
        internal long Scalar(object db,string sql,params object[] values)
        {
            using var s=Prepare(db,sql,values);var rows=(IEnumerator)s;
            if(!rows.MoveNext())throw new InvalidOperationException("Native scalar query returned no row.");
            return (long)Invoke(getLong,rows.Current,0)!;
        }
        internal List<string[]> Rows(object db,string sql,int columns,params object[] values)
        {
            using var s=Prepare(db,sql,values);var reader=(IEnumerator)s;var result=new List<string[]>();
            while(reader.MoveNext())result.Add(Enumerable.Range(0,columns).Select(i=>Text(reader.Current,i)).ToArray());
            return result;
        }
        internal void ValidateMapSchema(object db)
        {
            var needed=new[]{"Id","guid","type","ParentId","Path","ExternalId","AlbumId","SeriesId","IndexNumber","ParentIndexNumber","PresentationUniqueKey","SeriesPresentationUniqueKey","ProviderIds","UserDataKeyId"};
            var info=Rows(db,"PRAGMA main.table_info('MediaItems')",6).ToDictionary(r=>r[1],r=>r,StringComparer.OrdinalIgnoreCase);
            if(needed.Any(c=>!info.ContainsKey(c))||info["Id"][5]!="1"||info["guid"][2].ToUpperInvariant()!="BLOB")throw new NotSupportedException("Native MediaItems mapping schema differs.");
            var links=Rows(db,"PRAGMA main.table_info('ItemLinks2')",6).Select(r=>r[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(!new[]{"ItemId","Type","LinkedId","LinkOrder"}.All(links.Contains))throw new NotSupportedException("Native library link schema differs.");
            if(Scalar(db,"select count(*) from main.sqlite_master where type='trigger' and tbl_name COLLATE NOCASE in ('MediaItems','ItemLinks2')")!=0||Scalar(db,"select count(*) from sqlite_temp_master where type='trigger' and tbl_name COLLATE NOCASE in ('MediaItems','ItemLinks2')")!=0)throw new NotSupportedException("Unknown mapping triggers require review.");
        }
        private string Text(object row,int column)=>(bool)Invoke(isNull,row,column)!?"":(string)Invoke(getString,row,column)!;
        private long Number(object row,int column)=>(long)Invoke(getLong,row,column)!;
        internal void ValidateSchema(object db)
        {
            var expected=new[]{"UserDataKeyId","userId","rating","played","playCount","isFavorite","HideFromResume","playbackPositionTicks","LastPlayedDateInt","AudioStreamIndex","SubtitleStreamIndex","RatingLastModified","PlaystateLastModified","DateLastSearchedInt"};
            var columns=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            using(var s=Prepare(db,"PRAGMA main.table_info('UserDatas')",Array.Empty<object>())){var rows=(IEnumerator)s;while(rows.MoveNext())columns.Add(Text(rows.Current,1),Number(rows.Current,5));}
            if(columns.Count!=expected.Length||expected.Any(c=>!columns.ContainsKey(c))||columns["UserDataKeyId"]!=1||columns["userId"]!=2||columns.Count(c=>c.Value!=0)!=2)throw new NotSupportedException("Native UserDatas schema differs.");
            var keyColumns=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
            using(var s=Prepare(db,"PRAGMA main.table_info('UserDataKeys2')",Array.Empty<object>())){var rows=(IEnumerator)s;while(rows.MoveNext())keyColumns.Add(Text(rows.Current,1),Number(rows.Current,5));}
            if(keyColumns.Count!=2||!keyColumns.TryGetValue("Id",out var primary)||primary!=1||!keyColumns.TryGetValue("UserDataKey",out var keyPrimary)||keyPrimary!=0)throw new NotSupportedException("Native UserDataKeys2 schema differs.");
            var keyMetadata=Invoke(columnMetadata,db,"main","UserDataKeys2","UserDataKey")!;
            if((string?)keyMetadata.GetType().GetProperty("CollationSequence")!.GetValue(keyMetadata)!="NOCASE"||(bool?)keyMetadata.GetType().GetProperty("HasNotNullConstraint")!.GetValue(keyMetadata)!=true)throw new NotSupportedException("Native key column contract differs.");
            var unique=false;
            using(var s=Prepare(db,"PRAGMA main.index_list('UserDataKeys2')",Array.Empty<object>())){var rows=(IEnumerator)s;while(rows.MoveNext())if(Text(rows.Current,1)=="idx_unique_UserDataKeys"&&Number(rows.Current,2)==1&&Number(rows.Current,4)==0)unique=true;}
            var keys=0;
            using(var s=Prepare(db,"PRAGMA main.index_xinfo('idx_unique_UserDataKeys')",Array.Empty<object>())){var rows=(IEnumerator)s;while(rows.MoveNext())if(Number(rows.Current,5)==1){keys++;if(Text(rows.Current,2)!="UserDataKey"||Text(rows.Current,4)!="NOCASE")throw new NotSupportedException("Native key index differs.");}}
            if(!unique||keys!=1)throw new NotSupportedException("Native unique key index missing.");
            const string triggers="select count(*) from {0}.sqlite_master where type='trigger' and tbl_name COLLATE NOCASE in ('UserDatas','UserDataKeys2')";
            if(Scalar(db,string.Format(triggers,"main"))!=0||Scalar(db,"select count(*) from sqlite_temp_master where type='trigger' and tbl_name COLLATE NOCASE in ('UserDatas','UserDataKeys2')")!=0)throw new NotSupportedException("User-data copy does not adopt custom triggers.");
        }
    }
}
