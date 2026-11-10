using System.Net;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using StrmAssistantRebuild.Web.Api;

namespace StrmAssistantRebuild.Web.Service;

public sealed class LibraryStructureService : BaseApiService
{
    public LibraryStructureService(ILibraryManager libraryManager) => LibraryManager = libraryManager;

    public object Post(CopyVirtualFolder request) => WebServiceSupport.Run(() =>
    {
        var source = WebServiceSupport.Item(LibraryManager, request.Id);
        if (source is not CollectionFolder)
            throw WebServiceSupport.Error(HttpStatusCode.BadRequest, "Copy requires a virtual library folder.");
        var options = WebServiceSupport.CopyOptions(LibraryManager.GetLibraryOptions(source));
        // Both 3.0.0.57 and the OSS service deliberately copy settings WITHOUT media paths.
        options.PathInfos = Array.Empty<MediaPathInfo>();
        var names = LibraryManager.GetVirtualFolders().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var start = Random.Shared.Next(100, 999);
        var name = Enumerable.Range(0, 899).Select(i => source.Name + " #" + (100 + (start - 100 + i) % 899))
            .FirstOrDefault(n => !names.Contains(n));
        if (name == null) throw WebServiceSupport.Error(HttpStatusCode.Conflict, "All copy suffixes are already in use.");
        // Host owns the destination configuration path; no filesystem copying or media duplication.
        LibraryManager.AddVirtualFolder(name, options, false);
        WebServiceSupport.Log(nameof(CopyVirtualFolder), 1);
    });
}
