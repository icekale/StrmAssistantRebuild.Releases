using System.Xml;
using System.Xml.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Provider;

/// <summary>Read-only sidecar actor thumbnail import through the real host's public people persistence API.</summary>
public sealed class NfoActorService
{
    public const int MaxBytes = 2 * 1024 * 1024;
    readonly ILibraryManager library;
    readonly Func<MetadataEnhanceOptions> options;
    readonly ILogger logger;
    public NfoActorService(ILibraryManager library, Func<MetadataEnhanceOptions> options, ILogger logger)
    { this.library = library; this.options = options; this.logger = logger; }

    public async Task<bool> ApplyAsync(BaseItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!options().EnhanceNfoMetadata || item.IsFieldLocked(MetadataFields.Cast) || !item.SupportsPeople) return false;
        var path = Sidecars(item).FirstOrDefault(File.Exists);
        if (path == null) return false;
        IReadOnlyList<PersonInfo> actors;
        try
        {
            await using var stream = File.OpenRead(path);
            var bytes = await MetadataLanguageService.ReadBoundedAsync(stream, MaxBytes, token).ConfigureAwait(false);
            using var input = new MemoryStream(bytes, false);
            actors = Parse(input);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        { logger.Warn("NFO actor import skipped: {0}", ex.GetType().Name); return false; }
        var existing = library.GetItemPeople(item);
        var merged = Merge(existing, actors, out var changed);
        token.ThrowIfCancellationRequested();
        if (changed) library.UpdatePeople(item, merged, false);
        return changed;
    }

    public static IEnumerable<string> Sidecars(BaseItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Path) || !Path.IsPathRooted(item.Path)) yield break;
        if (item is Series) { yield return Path.Combine(item.Path, "tvshow.nfo"); yield break; }
        if (item is Season) { yield return Path.Combine(item.Path, "season.nfo"); yield break; }
        if (item is not (Movie or Episode)) yield break;
        yield return Path.ChangeExtension(item.Path, ".nfo");
        // movie.nfo only belongs to a dedicated movie folder, not a mixed-file directory.
        if (item is Movie && !item.IsInMixedFolder) yield return Path.Combine(Path.GetDirectoryName(item.Path)!, "movie.nfo");
    }

    public static IReadOnlyList<PersonInfo> Parse(Stream input)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false
        };
        using var reader = XmlReader.Create(input, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root == null || root.Name.Namespace != XNamespace.None || root.Name.LocalName is not ("movie" or "tvshow" or "episodedetails" or "season"))
            throw new InvalidDataException("Unsupported NFO root.");
        var result = new List<PersonInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var actor in root.Elements("actor"))
        {
            if (result.Count >= 2000) throw new InvalidDataException("NFO actor count exceeds limit.");
            var name = actor.Element("name")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || !seen.Add(name)) continue;
            var thumb = actor.Elements("thumb").Select(t => t.Value.Trim()).FirstOrDefault(MetadataLanguageService.IsHttpUrl);
            // Empty/local/file:/data: thumbnails do not erase an existing actor URL.
            if (thumb == null || thumb.Length > 8192) continue;
            result.Add(new PersonInfo { Name = name, Role = actor.Element("role")?.Value.Trim(), Type = PersonType.Actor, ImageUrl = thumb });
        }
        return result;
    }

    public static List<PersonInfo> Merge(IReadOnlyList<PersonInfo> current, IReadOnlyList<PersonInfo> actors, out bool changed)
    {
        changed = false;
        var merged = current.ToList();
        foreach (var actor in actors)
        {
            var index = merged.FindIndex(p => p.Type == PersonType.Actor && string.Equals(p.Name?.Trim(), actor.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0) { merged.Add(actor); changed = true; continue; }
            var person = merged[index];
            if (person.ImageUrl == actor.ImageUrl) continue;
            // Preserve host identity, provider IDs and roles; only import the thumbnail association.
            merged[index] = new PersonInfo { Name = person.Name, Role = person.Role, Type = person.Type, ImageUrl = actor.ImageUrl,
                ProviderIds = person.ProviderIds, ImageInfos = person.ImageInfos, ItemId = person.ItemId, Id = person.Id };
            changed = true;
        }
        return merged;
    }
}
