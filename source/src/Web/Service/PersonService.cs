using System.Net;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using StrmAssistantRebuild.Web.Api;

namespace StrmAssistantRebuild.Web.Service;

public sealed class PersonService : BaseApiService
{
    public PersonService(ILibraryManager libraryManager) => LibraryManager = libraryManager;

    public object Post(DeletePerson request) => WebServiceSupport.Run(() =>
    {
        var person = WebServiceSupport.Item(LibraryManager, request.Id);
        if (person is not Person)
            throw WebServiceSupport.Error(HttpStatusCode.BadRequest, "DeletePerson requires a Person item.");
        // The source deletes the Person entity, not all credits of an arbitrary movie.
        WebServiceSupport.Writable(person.GetInternalMetadataPath());
        LibraryManager.DeleteItem(person, new DeleteOptions
        {
            DeleteFileLocation = false,
            DeleteFromExternalProvider = false
        }, false);
        WebServiceSupport.Log(nameof(DeletePerson), 1);
    });
}
