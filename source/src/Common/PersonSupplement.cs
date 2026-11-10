using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace StrmAssistantRebuild.Common;

/// <summary>Explicit missing-only person fields; never feed a live person to native full refresh.</summary>
public static class PersonSupplement
{
    public static bool NeedsMetadata(Person p) => !p.IsLocked &&
        (!p.IsFieldLocked(MetadataFields.Name) && string.IsNullOrEmpty(p.Name) ||
         !p.IsFieldLocked(MetadataFields.Overview) && string.IsNullOrEmpty(p.Overview) ||
         !p.PremiereDate.HasValue || !p.EndDate.HasValue || !p.ProductionYear.HasValue ||
         !p.IsFieldLocked(MetadataFields.ProductionLocations) && (p.ProductionLocations?.Length ?? 0) == 0 ||
         string.IsNullOrEmpty(p.GetProviderId(MetadataProviders.Imdb)));

    public static async Task<bool> ApplyAsync(Person person, bool metadata, bool images, IProviderManager providers,
        ILibraryManager library, LibraryOptions configuration, IDirectoryService directory, EmbyPathPolicy paths, CancellationToken token)
    {
        // The caller holds the shared item lease. Fetch DTOs, not the live item: native default refresh
        // can replace data, import user/lock settings and validate/delete existing images even with replace=false.
        Person? Current() => library.GetItemById(person.InternalId) as Person;
        var changed = false;
        if (metadata && NeedsMetadata(person))
        {
            foreach (var provider in providers.GetEnabledMetadataProviders(person, configuration).OfType<IRemoteMetadataProvider<Person, PersonLookupInfo>>())
            {
                token.ThrowIfCancellationRequested();
                var lookup = person.GetLookupInfo(configuration);
                lookup.ProviderIds = new ProviderIdDictionary(person.ProviderIds);
                var response = provider is IRemoteMetadataProviderWithOptions<Person, PersonLookupInfo> withOptions
                    ? await withOptions.GetMetadata(new RemoteMetadataFetchOptions<PersonLookupInfo> { SearchInfo = lookup, DirectoryService = directory }, token).ConfigureAwait(false)
                    : await provider.GetMetadata(lookup, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var current = Current();
                if (current == null || current.IsLocked) return changed;
                person = current;
                if (response?.HasMetadata == true && response.Item != null)
                {
                    // Commit only this narrow patch on the freshly read entity. No locks, user data, arbitrary
                    // provider keys (including Adult), or image DTOs from metadata responses are imported.
                    paths.RequireWritablePath(person.GetInternalMetadataPath());
                    var before = Snapshot(person);
                    if (MergeMissing(person, response.Item))
                    {
                        try { library.UpdateItems(new List<BaseItem> { person }, null, ItemUpdateType.MetadataDownload, false, false, null, token); }
                        catch { Restore(person, before); throw; }
                        changed = true;
                    }
                }
                if (!NeedsMetadata(person)) break;
            }
        }
        if (images && !person.IsLocked && !person.HasImage(ImageType.Primary))
        {
            var candidates = await providers.GetAvailableRemoteImages(person, configuration,
                new RemoteImageQuery { ImageType = ImageType.Primary, IncludeAllLanguages = true, IncludeDisabledProviders = false }, directory, token).ConfigureAwait(false);
            var image = candidates.FirstOrDefault(x => x.Type == ImageType.Primary && Uri.TryCreate(x.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https");
            token.ThrowIfCancellationRequested();
            var current = Current();
            if (image != null && current != null && !current.IsLocked && !current.HasImage(ImageType.Primary))
            {
                paths.RequireWritablePath(current.GetInternalMetadataPath());
                var before = current.ImageInfos;
                try
                {
                    if (configuration.DownloadImagesInAdvance)
                        await providers.SaveImage(current, configuration, image.Url, ImageType.Primary, 0, Array.Empty<long>(), directory, true, token).ConfigureAwait(false);
                    else current.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = image.Url, DateModified = DateTimeOffset.UtcNow }, 0, false);
                    token.ThrowIfCancellationRequested();
                    library.UpdateImages(current);
                    changed = true;
                }
                catch { current.ImageInfos = before; throw; }
            }
        }
        return changed;
    }

    public static bool MergeMissing(Person target, Person source)
    {
        if (target.IsLocked) return false;
        var changed = false;
        if (!target.IsFieldLocked(MetadataFields.Name) && string.IsNullOrEmpty(target.Name) && !string.IsNullOrEmpty(source.Name)) { target.Name = source.Name; changed = true; }
        if (!target.IsFieldLocked(MetadataFields.Overview) && string.IsNullOrEmpty(target.Overview) && !string.IsNullOrEmpty(source.Overview)) { target.Overview = source.Overview; changed = true; }
        if (!target.PremiereDate.HasValue && source.PremiereDate.HasValue) { target.PremiereDate = source.PremiereDate; changed = true; }
        if (!target.EndDate.HasValue && source.EndDate.HasValue) { target.EndDate = source.EndDate; changed = true; }
        if (!target.ProductionYear.HasValue && target.PremiereDate.HasValue) { target.ProductionYear = target.PremiereDate.Value.Year; changed = true; }
        if (!target.IsFieldLocked(MetadataFields.ProductionLocations) && (target.ProductionLocations?.Length ?? 0) == 0 && source.ProductionLocations?.Length > 0)
        { target.ProductionLocations = source.ProductionLocations.ToArray(); changed = true; }
        var imdb = source.GetProviderId(MetadataProviders.Imdb);
        if (string.IsNullOrEmpty(target.GetProviderId(MetadataProviders.Imdb)) && !string.IsNullOrEmpty(imdb)) { target.SetProviderId(MetadataProviders.Imdb, imdb); changed = true; }
        return changed;
    }

    // Only our small synchronous patch is rolled back on persistence failure; no whole stale DTO restore.
    static Person Snapshot(Person p) => new() { Name = p.Name, Overview = p.Overview, PremiereDate = p.PremiereDate,
        EndDate = p.EndDate, ProductionYear = p.ProductionYear, ProductionLocations = p.ProductionLocations, ProviderIds = new ProviderIdDictionary(p.ProviderIds) };
    static void Restore(Person p, Person before)
    {
        p.Name = before.Name; p.Overview = before.Overview; p.PremiereDate = before.PremiereDate;
        p.EndDate = before.EndDate; p.ProductionYear = before.ProductionYear; p.ProductionLocations = before.ProductionLocations;
        if (before.ProviderIds.TryGetValue(MetadataProviders.Imdb.ToString(), out var imdb)) p.SetProviderId(MetadataProviders.Imdb, imdb);
        else p.ProviderIds.Remove(MetadataProviders.Imdb.ToString());
    }
}
