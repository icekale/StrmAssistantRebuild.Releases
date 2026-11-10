using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace StrmAssistantRebuild.Common;

/// <summary>A bounded read-only observation for a future migration. Never installs hooks or writes state.</summary>
public static class EpisodeLibraryScopePlan
{
    public const string KeyPrefix="strmassistant.rebuild.library.v1/";
    public sealed record Row(long ItemId,Guid ItemGuid,long LibraryId,string NativeKey,string StoredKey,string ScopedKey,string EpisodeKey,string SeriesKey,string Status,string[] Issues);
    public static Row[] Build(Version version,ILibraryManager library,IUserDataManager data,IEnumerable<long> itemIds,long probeUserId,CancellationToken token,IProgress<double>? progress=null)
    {
        ArgumentNullException.ThrowIfNull(library);ArgumentNullException.ThrowIfNull(data);ArgumentNullException.ThrowIfNull(itemIds);
        if(probeUserId<=0)throw new ArgumentOutOfRangeException(nameof(probeUserId));
        if(!ScopedEpisodeUserDataKey.Probe(version,out var detail))throw new NotSupportedException(detail);
        var selected=new List<long>();
        foreach(var id in itemIds){token.ThrowIfCancellationRequested();if(selected.Count==4096)throw new InvalidOperationException("Episode planning input budget exceeded.");selected.Add(id);}
        var ids=selected.Distinct().ToArray();var rows=new List<Row>(ids.Length);progress?.Report(0);
        foreach(var id in ids)
        {
            token.ThrowIfCancellationRequested();
            var row=new Row(id,Guid.Empty,0,"","","","","","Blocked",Array.Empty<string>());
            try
            {
                if(id<=0){rows.Add(row with{Issues=new[]{"invalid-item-id"}});continue;}
                if(library.GetItemById(id) is not Episode item||item.InternalId!=id)
                {rows.Add(row with{Issues=new[]{"missing-or-mismatched-episode"}});continue;}
                row=row with{ItemGuid=item.Id,EpisodeKey=item.PresentationUniqueKey??"",SeriesKey=item.SeriesPresentationUniqueKey??""};
                var folders=library.GetCollectionFolders(item,token);
                if(folders.Length==0||folders.Any(f=>f is not CollectionFolder||f.InternalId<=0))
                {rows.Add(row with{Issues=new[]{"unknown-library-membership"}});continue;}
                var libraries=folders.Select(f=>f.InternalId).Distinct().ToArray();
                if(libraries.Length!=1){rows.Add(row with{Issues=new[]{"ambiguous-library-membership"}});continue;}
                row=row with{LibraryId=libraries[0]};
                // Retain known library/PUK evidence even for an incomplete row so it can still
                // block a conflicting candidate elsewhere in this selected observation.
                if(item.Id==Guid.Empty||!item.IndexNumber.HasValue||!item.ParentIndexNumber.HasValue||string.IsNullOrWhiteSpace(row.EpisodeKey)||string.IsNullOrWhiteSpace(row.SeriesKey))
                {rows.Add(row with{Issues=new[]{"incomplete-episode-identity"}});continue;}
                var native=ScopedEpisodeUserDataKey.ReadNativeKey(item);
                if(string.IsNullOrWhiteSpace(native)||native.StartsWith(KeyPrefix,StringComparison.OrdinalIgnoreCase)||native.Any(char.IsControl))
                {rows.Add(row with{Issues=new[]{"invalid-or-already-overridden-native-key"}});continue;}
                var stored=data.GetUserData(probeUserId,id,new[]{UserDataFields.UserDataKey},token)?.Key??"";
                row=row with{NativeKey=native,StoredKey=stored,ScopedKey=KeyPrefix+row.LibraryId.ToString(CultureInfo.InvariantCulture)+"/"+native};
                var status=string.Equals(stored,native,StringComparison.Ordinal)?"Ready":string.Equals(stored,row.ScopedKey,StringComparison.Ordinal)?"AlreadyScoped":"Blocked";
                rows.Add(row with{Status=status,Issues=status=="Blocked"?new[]{"persisted-key-mismatch"}:Array.Empty<string>()});
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception error){rows.Add(row with{Issues=new[]{"read-failed:"+error.GetType().Name}});}
            finally{progress?.Report(ids.Length==0?90:90d*rows.Count/ids.Length);}
        }
        // Collisions are checked only inside the supplied observation set. This conservative text
        // comparison is a warning boundary, not a promise to emulate every SQL collation.
        var episodeConflicts=rows.Where(r=>r.LibraryId>0&&!string.IsNullOrWhiteSpace(r.EpisodeKey)).GroupBy(r=>r.EpisodeKey,StringComparer.OrdinalIgnoreCase).Where(g=>g.Select(r=>r.LibraryId).Distinct().Count()>1).SelectMany(g=>g.Select(r=>r.ItemId)).ToHashSet();
        var seriesConflicts=rows.Where(r=>r.LibraryId>0&&!string.IsNullOrWhiteSpace(r.SeriesKey)).GroupBy(r=>r.SeriesKey,StringComparer.OrdinalIgnoreCase).Where(g=>g.Select(r=>r.LibraryId).Distinct().Count()>1).SelectMany(g=>g.Select(r=>r.ItemId)).ToHashSet();
        token.ThrowIfCancellationRequested();
        for(var i=0;i<rows.Count;i++)
        {
            var row=rows[i];var issues=row.Issues.ToList();
            if(episodeConflicts.Contains(row.ItemId))issues.Add("cross-library-episode-presentation");
            if(seriesConflicts.Contains(row.ItemId))issues.Add("cross-library-series-presentation");
            if(issues.Count>0)rows[i]=row with{Status="Blocked",Issues=issues.ToArray()};
        }
        progress?.Report(100);return rows.ToArray();
    }
}
