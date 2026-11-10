using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace StrmAssistantRebuild.Common;

/// <summary>Bounded server-maintenance discovery for explicit libraries, not a viewer filter or a database snapshot.</summary>
public static class EpisodeLibrarySelection
{
    public const int MaxLibraries=64,MaxCandidates=4096;
    public sealed record LibraryResult(long LibraryId,string State,int Candidates,int Accepted,string Detail);
    public sealed record Selection(bool Complete,IReadOnlyList<long> ItemIds,IReadOnlyList<LibraryResult> Libraries,int ProgressErrors);
    public static Selection Select(Version version,ILibraryManager library,IEnumerable<long> libraryIds,CancellationToken token,IProgress<double>? progress=null)
    {
        ArgumentNullException.ThrowIfNull(library);ArgumentNullException.ThrowIfNull(libraryIds);token.ThrowIfCancellationRequested();
        if(!ScopedEpisodeUserDataKey.Probe(version,out var detail))throw new NotSupportedException(detail);
        var input=new List<long>();
        foreach(var id in libraryIds){token.ThrowIfCancellationRequested();if(input.Count==MaxLibraries)throw new ArgumentException("Library selection input budget exceeded.");input.Add(id);}
        var selected=input.Distinct().ToArray();var reports=new List<LibraryResult>();var accepted=new List<long>();var unique=new HashSet<long>();
        var rawCount=0;var exhausted=false;var observerErrors=0;
        void Progress(double value){try{progress?.Report(value);}catch{observerErrors++;}token.ThrowIfCancellationRequested();}
        Progress(0);
        foreach(var libraryId in selected)
        {
            token.ThrowIfCancellationRequested();var candidates=0;var valid=0;
            try
            {
                if(exhausted){reports.Add(new(libraryId,"NotInspected",0,0,"Global candidate budget already exceeded."));continue;}
                if(libraryId<=0||library.GetItemById(libraryId) is not CollectionFolder folder||folder.InternalId!=libraryId||folder.Id==Guid.Empty)
                {reports.Add(new(libraryId,"InvalidLibrary",0,0,"Expected a persisted CollectionFolder identity."));continue;}
                var guid=folder.Id;var limit=MaxCandidates-rawCount+1;
                var query=new InternalItemsQuery{ParentIds=new[]{libraryId},Recursive=true,IncludeItemTypes=new[]{nameof(Episode)},GroupByPresentationUniqueKey=false,EnableTotalRecordCount=false,IsVirtualItem=false,Limit=limit};
                var ids=library.GetInternalItemIds(query,token);token.ThrowIfCancellationRequested();
                candidates=ids.Length;
                if(candidates>MaxCandidates-rawCount)
                {exhausted=true;reports.Add(new(libraryId,"BudgetExceeded",candidates,0,"Candidate budget exceeded; no truncated executable selection is returned."));continue;}
                rawCount+=candidates;
                // ParentIds legitimately normalize into physical AncestorIds in the native manager.
                // Preserve the type/group/budget contract without rejecting that native transformation.
                if(query.Limit!=limit||query.User!=null||query.IsFavorite!=null||query.EnableTotalRecordCount||query.IsVirtualItem!=false||!query.Recursive||!query.IncludeItemTypes.SequenceEqual(new[]{nameof(Episode)})||query.GroupByPresentationUniqueKey!=false||query.EnableGroupByMetadataKey||query.GroupBy.Length!=0||query.GroupBySeriesId||query.GroupBySeriesPresentationUniqueKey||query.GroupByAlbumId||query.GroupByParentPresentationUniqueKey||query.GroupByPrimaryImagePath||query.GroupProgramsBySeries||query.IsNextUpQuery||query.GroupItemsIntoItemLinkType.HasValue||query.ProjectToMedia)
                    throw new InvalidOperationException("Native candidate query contract changed.");
                foreach(var id in ids.ToArray().Distinct())
                {
                    token.ThrowIfCancellationRequested();
                    if(id<=0||library.GetItemById(id) is not Episode item||item.InternalId!=id||item.Id==Guid.Empty||item.IsVirtualItem||!item.IsFileProtocol||string.IsNullOrWhiteSpace(item.Path)||!string.IsNullOrEmpty(item.ExternalId))throw new InvalidOperationException("Candidate is not a persisted local non-virtual Episode.");
                    var folders=library.GetCollectionFolders(item,token);
                    if(folders.Length==0||folders.Any(f=>f is not CollectionFolder||f.InternalId<=0||f.Id!=guid)||!folders.Select(f=>f.InternalId).Distinct().SequenceEqual(new[]{libraryId}))throw new InvalidOperationException("Candidate is outside or ambiguously belongs to the selected library.");
                    valid++;if(unique.Add(id))accepted.Add(id);
                }
                if(library.GetItemById(libraryId) is not CollectionFolder current||current.InternalId!=libraryId||current.Id!=guid)throw new InvalidOperationException("Library identity changed during selection.");
                reports.Add(new(libraryId,"Ready",candidates,valid,"Observed Episode candidates passed identity and membership checks; later writes must revalidate."));
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
            catch(Exception error){reports.Add(new(libraryId,"ReadFailed",candidates,valid,error.GetType().Name+": "+error.Message));}
            finally{Progress(selected.Length==0?100:100d*reports.Count/selected.Length);}
        }
        Progress(100);var complete=reports.All(r=>r.State=="Ready");
        return new Selection(complete,Array.AsReadOnly(complete?accepted.ToArray():Array.Empty<long>()),Array.AsReadOnly(reports.ToArray()),observerErrors);
    }
}
