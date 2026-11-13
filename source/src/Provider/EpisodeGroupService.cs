using System.Globalization;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Provider;

/// <summary>Group numbering describes the files; aired numbering is used only for remote lookups.</summary>
public sealed class EpisodeGroupService
{
    public const string ProviderKey = "TmdbEpisodeGroup";
    readonly ILibraryManager library;
    readonly MetadataLanguageService languages;
    readonly Func<MetadataEnhanceOptions> options;
    readonly HttpClientFactory http;
    readonly EmbyPathPolicy paths;
    readonly string cacheRoot;
    readonly ILogger logger;
    public EpisodeGroupService(ILibraryManager library, MetadataLanguageService languages, Func<MetadataEnhanceOptions> options,
        HttpClientFactory http, EmbyPathPolicy paths, string cacheRoot, ILogger logger)
    { this.library = library; this.languages = languages; this.options = options; this.http = http; this.paths = paths; this.cacheRoot = cacheRoot; this.logger = logger; }

    public async Task<bool> ApplyAsync(BaseItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var settings = options();
        if (!settings.MovieDbEpisodeGroup || item.IsLocked || item is not (Episode or Season)) return false;
        var series = item is Episode episode ? episode.Series : ((Season)item).Series;
        if (series == null) return false;
        var doc = await LoadAsync(series, token).ConfigureAwait(false);
        if (doc == null) return false;
        MetadataLanguageResult? text = null;
        string? tmdbId = null;
        EpisodeGroupInfo? selectedGroup = null;
        if (item is Episode ep)
        {
            if (!ep.ParentIndexNumber.HasValue || !ep.IndexNumber.HasValue || ep.IndexNumberEnd > ep.IndexNumber) return false;
            var map = doc.Episodes.SingleOrDefault(x => x.GroupSeason == ep.ParentIndexNumber && x.GroupEpisode == ep.IndexNumber);
            if (map == null) return false;
            // Preserve local/group numbering. Never rewrite identity to aired numbering and remap a second time.
            text = await languages.FetchEpisodeAsync(ep, map.AiredSeason, map.AiredEpisode, token).ConfigureAwait(false);
            if (settings.EnableMovieDbEpisodeGroupExtendedInfo)
            {
                text ??= new MetadataLanguageResult();
                var fallback = MetadataLanguageService.ParseLanguages(settings.FallbackLanguages);
                if (text.Name == null && !string.IsNullOrWhiteSpace(map.Name) && fallback.Any(l => MetadataLanguageService.ContentMatches(map.Name, l))) text.Name = map.Name;
                if (text.Overview == null && !string.IsNullOrWhiteSpace(map.Overview) && fallback.Any(l => MetadataLanguageService.ContentMatches(map.Overview, l))) text.Overview = map.Overview;
            }
            if (map.TmdbId != null && text?.AiredTmdbId != null &&
                !string.Equals(map.TmdbId, text.AiredTmdbId, StringComparison.Ordinal))
                throw new InvalidDataException("Mapped episode and native result TMDB identities differ.");
            tmdbId = map.TmdbId ?? text?.AiredTmdbId;
        }
        else if (item is Season season && season.IndexNumber.HasValue)
        {
            selectedGroup = doc.Groups.SingleOrDefault(g => g.Order == season.IndexNumber);
            if (selectedGroup != null && settings.EnableMovieDbEpisodeGroupExtendedInfo)
            {
                var allowed = MetadataLanguageService.ParseLanguages(settings.FallbackLanguages);
                text = new MetadataLanguageResult
                {
                    Name = allowed.Any(l => MetadataLanguageService.ContentMatches(selectedGroup.Name, l)) ? selectedGroup.Name : null,
                    Overview = allowed.Any(l => MetadataLanguageService.ContentMatches(selectedGroup.Overview, l)) ? selectedGroup.Overview : null
                };
            }
        }
        // All asynchronous lookup/parsing finishes before touching the live entity. MetadataFields has
        // no PremiereDate/ProductionYear values on this host; the item-wide lock protects those fields.
        token.ThrowIfCancellationRequested();
        if (item.IsLocked) return false;
        var before = (item.Name, item.Overview, item.PremiereDate, item.ProductionYear, item.DateLastSaved, item.CommunityRating);
        var people = item is Episode && !item.IsFieldLocked(MetadataFields.Cast) ? text?.AiredPeople : null;
        var previousPeople = people == null ? null : library.GetItemPeople(new InternalPeopleQuery
        { ItemIds = new[] { item.InternalId }, EnableIds = true, EnableImages = true, EnableProviderIds = true });
        var peopleChanged = people != null && previousPeople != null && !previousPeople.Select(p => (p.Name,p.Role,p.Type)).SequenceEqual(people.Select(p => (p.Name,p.Role,p.Type)));
        var peopleAttempted = false;
        var ids = new[] { (Key: MetadataProviders.Tmdb.ToString(), Value: tmdbId),
            (Key: MetadataProviders.Imdb.ToString(), Value: item is Episode ? text?.AiredImdbId : null),
            (Key: MetadataProviders.Tvdb.ToString(), Value: item is Episode ? text?.AiredTvdbId : null) }
            .Where(id => id.Value != null && item.GetProviderId(id.Key) != id.Value)
            .Select(id => (id.Key, id.Value, Existed: item.ProviderIds.TryGetValue(id.Key, out var prior), Prior: prior)).ToArray();
        var changed = false;
        try
        {
            if (text != null) changed = MetadataLanguageService.ApplyText(item, text, item is Episode && settings.BlockNonFallbackLanguage);
            foreach (var id in ids) { item.SetProviderId(id.Key, id.Value); changed = true; }
            if (item is Episode && text?.AiredPremiereDate is { } airedDate &&
                (item.PremiereDate != airedDate || item.ProductionYear != airedDate.Year))
            { item.PremiereDate = airedDate; item.ProductionYear = airedDate.Year; changed = true; }
            if (item is Episode && !item.IsFieldLocked(MetadataFields.CommunityRating) &&
                text?.AiredRating is { } rating && item.CommunityRating.GetValueOrDefault() != rating)
            { item.CommunityRating = rating; changed = true; }
            if (peopleChanged)
            { peopleAttempted = true; library.UpdatePeople(item, people!, false); changed = true; }
            if (selectedGroup != null)
            {
                var date = settings.EnableMovieDbEpisodeGroupExtendedInfo ? selectedGroup.AirDate : null;
                if (item.PremiereDate != date || item.ProductionYear != date?.Year)
                { item.PremiereDate = date; item.ProductionYear = date?.Year; changed = true; }
            }
            token.ThrowIfCancellationRequested();
            if (changed) MetadataLanguageService.Persist(library, item, token);
            return changed;
        }
        catch (Exception failure)
        {
            (item.Name, item.Overview, item.PremiereDate, item.ProductionYear, item.DateLastSaved, item.CommunityRating) = before;
            foreach (var id in ids)
                if (id.Existed) item.ProviderIds[id.Key] = id.Prior;
                else item.ProviderIds.Remove(id.Key);
            if (peopleAttempted)
            {
                try { library.UpdatePeople(item, previousPeople!, false); }
                catch (Exception rollback) { throw new AggregateException("Group metadata and cast rollback failed.", failure, rollback); }
            }
            throw;
        }
    }

    public static bool HasGroupSource(Series? series, MetadataEnhanceOptions settings) =>
        series != null && HasGroupSource(series.Path, series.GetProviderId(ProviderKey), settings, series.GetProviderId(MetadataProviders.Tmdb));
    /// <summary>Same rule from a series path and group reference (e.g. a SeriesInfo lookup).</summary>
    public static bool HasGroupSource(string? seriesPath, string? reference, MetadataEnhanceOptions settings, string? seriesTmdbId = null) =>
        !string.IsNullOrWhiteSpace(reference) || OriginalOverridePath(seriesTmdbId) is { } o && File.Exists(o) ||
        settings.LocalEpisodeGroup && !string.IsNullOrWhiteSpace(seriesPath) &&
        Path.IsPathRooted(seriesPath) && new[] { "episodegroup.json", "episode-group.json" }.Any(name => File.Exists(Path.Combine(seriesPath, name)));

    /// <summary>
    /// M161: the original's override file override/tmdb-tv/{series TMDB id}/episodegroup.json in the metadata cache
    /// (notes/decrypted/A----------/A----------.cs:104206, :113990), read first when the cache mode is not None. Set by the
    /// plugin; returns the cache root or null (mode None).
    /// </summary>
    public static Func<string?>? OriginalOverrideRoot { get; set; }
    public static long OriginalOverrideReads => Interlocked.Read(ref originalOverrideReads);
    private static long originalOverrideReads;
    internal static string? OriginalOverridePath(string? seriesTmdbId)
    {
        var root = OriginalOverrideRoot?.Invoke();
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(seriesTmdbId) || seriesTmdbId.Length > 10 || !seriesTmdbId.All(c => c is >= '0' and <= '9')) return null;
        return Path.Combine(root, "override", "tmdb-tv", seriesTmdbId, "episodegroup.json");
    }

    public Task<EpisodeGroupDocument?> LoadAsync(Series series, CancellationToken token) =>
        LoadAsync(series.Path, series.GetProviderId(ProviderKey), token, series.GetProviderId(MetadataProviders.Tmdb));

    public async Task<EpisodeGroupDocument?> LoadAsync(string? seriesPath, string? reference, CancellationToken token, string? seriesTmdbId = null)
    {
        token.ThrowIfCancellationRequested();
        var settings = options();
        if (OriginalOverridePath(seriesTmdbId) is { } overridePath && File.Exists(overridePath))
        {
            try
            {
                await using var stream = File.OpenRead(paths.RequireWritablePath(overridePath));
                var document = Parse(await MetadataLanguageService.ReadBoundedAsync(stream, 4 * 1024 * 1024, token).ConfigureAwait(false));
                Interlocked.Increment(ref originalOverrideReads);
                return document;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { logger.Debug("Original episodegroup.json override ignored: {0}", ex.GetType().Name); }
        }
        if (settings.LocalEpisodeGroup && !string.IsNullOrWhiteSpace(seriesPath) && Path.IsPathRooted(seriesPath))
        {
            // Read-only media sidecar; remote cache is never written here.
            foreach (var name in new[] { "episodegroup.json", "episode-group.json" })
            {
                var sidecar = Path.Combine(seriesPath, name);
                if (!File.Exists(sidecar)) continue;
                await using var stream = File.OpenRead(sidecar);
                return Parse(await MetadataLanguageService.ReadBoundedAsync(stream, 4 * 1024 * 1024, token).ConfigureAwait(false));
            }
        }
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var external = MetadataLanguageService.IsHttpUrl(reference);
        if (!external && (reference.Length > 128 || !reference.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidDataException("Invalid episode group identifier.");
        var relative = "tv/episode_group/" + Uri.EscapeDataString(reference);
        // Namespace native ID requests by the exact public endpoint, not by credentials. Old ID-only
        // files have ambiguous origin and are left untouched rather than silently trusted or deleted.
        var identity = external ? reference : "tmdb-v2:" + MetadataLanguageService.TmdbEndpoint(settings, relative).AbsoluteUri;
        var cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var cache = paths.RequireWritablePath(Path.Combine(cacheRoot, "episode-groups", cacheKey + ".json"));
        var cacheTime = File.Exists(cache) ? File.GetLastWriteTimeUtc(cache) : DateTime.MinValue;
        var now = DateTime.UtcNow;
        if (cacheTime <= now.AddMinutes(5) && now - cacheTime < TimeSpan.FromHours(6))
        {
            try
            {
                await using var stream = File.OpenRead(cache);
                var cached = Parse(await MetadataLanguageService.ReadBoundedAsync(stream, 4 * 1024 * 1024, token).ConfigureAwait(false));
                RequireIdentity(cached, reference, external);
                return cached;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { logger.Debug("Episode group cache discarded: {0}", ex.GetType().Name); }
        }
        if (!external && string.IsNullOrWhiteSpace(settings.AltMovieDbApiKey))
        { logger.Debug("Episode group remote lookup skipped: AltMovieDbApiKey is empty; validated cache/local JSON remain available."); return null; }
        byte[] bytes;
        if (external) bytes = await MetadataLanguageService.ReadHttpAsync(http, new Uri(reference), 4 * 1024 * 1024, token).ConfigureAwait(false);
        else
        {
            using var result = await MetadataLanguageService.GetTmdbAsync(http, settings, relative, token).ConfigureAwait(false);
            if (result == null) return null;
            bytes = Encoding.UTF8.GetBytes(result.RootElement.GetRawText());
        }
        var parsed = Parse(bytes);
        RequireIdentity(parsed, reference, external);
        Directory.CreateDirectory(paths.RequireWritablePath(Path.GetDirectoryName(cache)!));
        var temp = paths.RequireWritablePath(cache + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try { await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); File.Move(temp, cache, true); }
        finally { try { if (File.Exists(temp)) File.Delete(paths.RequireWritablePath(temp)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.Warn("Episode group temporary cleanup skipped: {0}", ex.GetType().Name); } }
        return parsed;
    }

    private static void RequireIdentity(EpisodeGroupDocument document, string reference, bool external)
    {
        if (!external && !string.Equals(document.Id, reference, StringComparison.Ordinal))
            throw new InvalidDataException("Episode group response identity mismatch.");
    }
    public static EpisodeGroupDocument Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("Episode group exceeds size limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !Property(root, "groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Episode group must contain a groups array.");
        var result = new EpisodeGroupDocument { Id = Text(root, "id") };
        var coordinates = new HashSet<(int, int)>();
        var groupNumbers = new HashSet<int>();
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid episode group entry.");
            var season = Number(group, "order");
            if (season < 0 || !groupNumbers.Add(season)) throw new InvalidDataException("Duplicate or negative group order.");
            result.Groups.Add(new EpisodeGroupInfo(season, Text(group, "name"), Text(group, "overview"), AirDate(group)));
            if (!Property(group, "episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Group episodes must be an array.");
            var ordinal = 0;
            foreach (var ep in episodes.EnumerateArray())
            {
                if (result.Episodes.Count >= 20000) throw new InvalidDataException("Episode group entry count exceeds limit.");
                var raw = Property(ep, "season_number", out _);
                // TMDB/compact schema uses explicit zero-based order; blueprint schema uses array order if omitted.
                var order = Property(ep, "order", out _) ? Number(ep, "order") : raw ? throw new InvalidDataException("TMDB episode order is required.") : ordinal;
                var airedSeason = Number(ep, raw ? "season_number" : "season");
                var airedEpisode = Number(ep, raw ? "episode_number" : "episode");
                if (order < 0 || order >= int.MaxValue || airedSeason < 0 || airedEpisode < 1 || !coordinates.Add((season, order + 1))) throw new InvalidDataException("Invalid or duplicate episode coordinates.");
                var id = Text(ep, raw ? "id" : "TmdbId");
                if (id != null) id = MetadataLanguageService.PositiveId(id) ?? throw new InvalidDataException("Invalid TMDB episode id.");
                result.Episodes.Add(new EpisodeGroupMapping(season, order + 1, airedSeason, airedEpisode, id, Text(ep, "name"), Text(ep, "overview"), EpisodeAirDate(ep)));
                ordinal++;
            }
        }
        return result;
    }
    static DateTimeOffset? AirDate(JsonElement group)
    {
        if (!Property(group, "air_date", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            // Explicit reconstruction: date-only is UTC midnight, timestamp must carry an offset.
            // Never let server locale/time zone reinterpret a calendar date or infer dates from episodes.
            if (text.Length == 10 && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return date;
            if (text.Length > 10 && (text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10) &&
                value.TryGetDateTimeOffset(out var timestamp)) return timestamp;
        }
        throw new InvalidDataException("Invalid episode-group air_date; expected ISO date or offset timestamp.");
    }
    // Episode dates are optional display data: an absent or malformed value is null, never a parse failure.
    static DateTimeOffset? EpisodeAirDate(JsonElement episode)
    {
        try { return AirDate(episode); } catch (InvalidDataException) { return null; }
    }
    static bool Property(JsonElement value, string key, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) { result = property.Value; return true; }
        result = default; return false;
    }
    static int Number(JsonElement value, string key) => Property(value, key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : throw new InvalidDataException("Missing integer: " + key);
    static string? Text(JsonElement value, string key) => !Property(value, key, out var p) || p.ValueKind == JsonValueKind.Null ? null : p.ValueKind == JsonValueKind.String ? p.GetString() : p.ValueKind == JsonValueKind.Number ? p.GetRawText() : throw new InvalidDataException("Invalid scalar: " + key);
}
public sealed class EpisodeGroupDocument
{
    public string? Id { get; set; }
    public List<EpisodeGroupInfo> Groups { get; } = new();
    public List<EpisodeGroupMapping> Episodes { get; } = new();
}
public sealed record EpisodeGroupInfo(int Order, string? Name, string? Overview, DateTimeOffset? AirDate = null);
public sealed record EpisodeGroupMapping(int GroupSeason, int GroupEpisode, int AiredSeason, int AiredEpisode, string? TmdbId, string? Name, string? Overview, DateTimeOffset? AirDate = null);
