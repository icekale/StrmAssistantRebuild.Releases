using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Provider;

/// <summary>Post-refresh enhancement using enabled public host providers; never changes provider internals.</summary>
public sealed class MetadataLanguageService
{
    readonly ILibraryManager library;
    readonly IProviderManager providers;
    readonly Func<IDirectoryService> createDirectory;
    readonly Func<MetadataEnhanceOptions> options;
    readonly HttpClientFactory http;
    readonly ILogger logger;
    readonly EmbyPathPolicy paths;
    readonly string cacheRoot;
    public MetadataLanguageService(ILibraryManager library, IProviderManager providers, Func<IDirectoryService> createDirectory,
        Func<MetadataEnhanceOptions> options, HttpClientFactory http, ILogger logger, EmbyPathPolicy paths, string cacheRoot)
    { this.library = library; this.providers = providers; this.createDirectory = createDirectory; this.options = options; this.http = http; this.logger = logger; this.paths = paths; this.cacheRoot = cacheRoot; }

    public async Task<bool> ApplyAsync(BaseItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (item.IsLocked) return false;
        var settings = options();
        var changed = false;
        // Grouped coordinates are not TMDB aired coordinates. EpisodeGroupService handles their lookup.
        var grouped = item is Episode ep && settings.MovieDbEpisodeGroup && EpisodeGroupService.HasGroupSource(ep.Series, settings);
        if (!grouped && (settings.ChineseMovieDb || settings.ChineseTvdb))
        {
            var result = item switch
            {
                Movie movie => await FetchAsync<Movie, MovieInfo>(movie, null, token).ConfigureAwait(false),
                Series series => await FetchAsync<Series, SeriesInfo>(series, null, token).ConfigureAwait(false),
                Season season => await FetchAsync<Season, SeasonInfo>(season, null, token).ConfigureAwait(false),
                Episode episode => await FetchAsync<Episode, EpisodeInfo>(episode, null, token).ConfigureAwait(false),
                Person person => await FetchAsync<Person, PersonLookupInfo>(person, null, token).ConfigureAwait(false),
                _ => null
            };
            if (result != null) changed = ApplyText(item, result, settings.BlockNonFallbackLanguage);
            if (changed) Persist(library, item, token);
        }
        if (settings.PreferOriginalPoster && item is Movie or Series)
        {
            try { changed |= await ApplyPosterAsync(item, settings, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.Warn("Metadata original poster failed: {0}", ex.GetType().Name); }
        }
        return changed;
    }

    /// <summary>Call after every metadata enhancer, so a later provider cannot restore a cleared tagline.</summary>
    public Task<bool> ApplyFinalPolicyAsync(BaseItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var settings = options();
        if (!settings.ClearTagline || !(settings.ChineseMovieDb || settings.ChineseTvdb) ||
            item is not (Movie or Series or Season or Episode) || item.IsLocked ||
            item.IsFieldLocked(MetadataFields.Tagline) || string.IsNullOrEmpty(item.Tagline)) return Task.FromResult(false);
        // Final cleanup is independent of fallback success; never use it to clear Overview or rewrite text.
        var previous = item.Tagline;
        item.Tagline = null;
        try { Persist(library, item, token); }
        catch { item.Tagline = previous; throw; }
        return Task.FromResult(true);
    }

    public Task<MetadataLanguageResult?> FetchEpisodeAsync(Episode item, int season, int episode, CancellationToken token) =>
        FetchAsync<Episode, EpisodeInfo>(item, lookup => { lookup.ParentIndexNumber = season; lookup.IndexNumber = episode; lookup.ProviderIds = new ProviderIdDictionary(); }, token, true);

    async Task<MetadataLanguageResult?> FetchAsync<T, TLookup>(T item, Action<TLookup>? configure, CancellationToken token, bool forceTmdb = false)
        where T : BaseItem, IHasLookupInfo<TLookup>, new() where TLookup : ItemLookupInfo, new()
    {
        var settings = options();
        var libraryOptions = library.GetLibraryOptions(item);
        var enabled = providers.GetEnabledMetadataProviders(item, libraryOptions);
        var result = new MetadataLanguageResult { AllowedLanguages = ParseLanguages((settings.ChineseMovieDb || forceTmdb ? settings.FallbackLanguages : "") + "," + (settings.ChineseTvdb && !forceTmdb ? settings.TvdbFallbackLanguages : "")) };
        var observedEpisodeMetadata = false;
        IDirectoryService? directory = null; // One native cache per logical lookup, shared only by its fallbacks.
        // Preserve host provider ordering; only exact recognized names are selected, never reflected methods.
        foreach (var provider in enabled.OfType<IRemoteMetadataProvider<T, TLookup>>())
        {
            var kind = ProviderKind(provider.Name);
            if (kind == "tmdb" ? !(settings.ChineseMovieDb || forceTmdb) : kind == "tvdb" ? !settings.ChineseTvdb || forceTmdb : true) continue;
            var languages = ParseLanguages(kind == "tmdb" ? settings.FallbackLanguages : settings.TvdbFallbackLanguages);
            if (languages.Length == 0) continue;
            foreach (var language in languages)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var lookup = item.GetLookupInfo(libraryOptions);
                    configure?.Invoke(lookup);
                    lookup.MetadataLanguage = ToHostLanguage(language);
                    // Do not carry the host's original multi-language preference into this explicit single-language request.
                    lookup.MetadataLanguages = new[] { new MediaBrowser.Model.Globalization.CultureDto
                    { Name = lookup.MetadataLanguage, TwoLetterISOLanguageNames = new[] { LanguageFamily(language) }, ThreeLetterISOLanguageNames = new[]
                        { language.ToLowerInvariant() switch { "zhtw" => "zhtw", "yue" => "yue", _ => LanguageFamily(language) switch { "zh" => "zho", "ja" => "jpn", "ko" => "kor", "en" => "eng", var value => value } } } } };
                    var response = provider is IRemoteMetadataProviderWithOptions<T, TLookup> withOptions
                        ? await withOptions.GetMetadata(new RemoteMetadataFetchOptions<TLookup> { SearchInfo = lookup, DirectoryService = directory ??= createDirectory() }, token).ConfigureAwait(false)
                        : await provider.GetMetadata(lookup, token).ConfigureAwait(false);
                    if (response?.HasMetadata != true || response.Item == null) continue;
                    result.ObservedResponse = true;
                    if (forceTmdb && !observedEpisodeMetadata && lookup is EpisodeInfo requested && response.Item is Episode episode)
                    {
                        // This is a native returned-item contract check, not proof of raw response coordinates:
                        // native ImportData can copy requested indices. First successful result owns IDs/date,
                        // including unknown fields; later language fallbacks must not replace that decision.
                        observedEpisodeMetadata = true;
                        if (episode.ParentIndexNumber == requested.ParentIndexNumber && episode.IndexNumber == requested.IndexNumber)
                        {
                            if (episode.PremiereDate is { } date && date != default(DateTimeOffset)) result.AiredPremiereDate = date;
                            result.AiredTmdbId = PositiveId(episode.GetProviderId(MetadataProviders.Tmdb));
                            result.AiredTvdbId = PositiveId(episode.GetProviderId(MetadataProviders.Tvdb));
                            if (episode.CommunityRating is >= 0 and <= 10) result.AiredRating = episode.CommunityRating;
                            result.AiredPeople = CapturePeople(response.People);
                            var imdb = episode.GetProviderId(MetadataProviders.Imdb);
                            if (imdb?.StartsWith("tt", StringComparison.Ordinal) == true && PositiveId(imdb[2..]) != null) result.AiredImdbId = imdb;
                        }
                    }
                    // Explicit provider language is authoritative; script checks also reject known silent English fallback.
                    var actual = string.IsNullOrWhiteSpace(response.ResultLanguage) ? language : response.ResultLanguage;
                    if (!SameLanguage(actual, language)) continue;
                    if (result.Name == null && ContentMatches(response.Item.Name, language))
                        result.Name = ApplyChineseTextPolicy(response.Item.Name, response.ResultLanguage, settings.PreferTraditionalChinese);
                    if (result.Overview == null && ContentMatches(response.Item.Overview, language))
                        result.Overview = ApplyChineseTextPolicy(response.Item.Overview, response.ResultLanguage, settings.PreferTraditionalChinese);
                    if (result.Name != null && result.Overview != null) return result;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.Warn("Metadata language provider {0}/{1} skipped: {2}", kind, language, ex.GetType().Name); }
            }
        }
        return result.ObservedResponse || result.Name != null || result.Overview != null ? result : null;
    }

    private static List<PersonInfo>? CapturePeople(List<PersonInfo>? people)
    {
        if (people == null || people.Count > 20000) return null;
        var copy = new List<PersonInfo>(people.Count);
        foreach (var person in people)
        {
            if (person == null || string.IsNullOrWhiteSpace(person.Name) || person.Name.Length > 1024 ||
                person.Role?.Length > 4096 || !Enum.IsDefined(typeof(PersonType), person.Type)) return null;
            var row = new PersonInfo { Name = person.Name, Role = person.Role, Type = person.Type };
            var tmdb = PositiveId(person.GetProviderId(MetadataProviders.Tmdb));
            if (tmdb != null) row.SetProviderId(MetadataProviders.Tmdb, tmdb);
            // Native PersonInfo thumbnails are URL associations, not a request to copy arbitrary local files.
            if (person.ImageUrl?.Length <= 8192 && IsHttpUrl(person.ImageUrl)) row.ImageUrl = person.ImageUrl;
            copy.Add(row);
        }
        return copy;
    }
    internal static string? PositiveId(string? value) => value is { Length: > 0 and <= 19 } &&
        value.All(c => c is >= '0' and <= '9') && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number.ToString(CultureInfo.InvariantCulture) : null;

    // Source also converts selected provider text (decompiled 93062–93211), not just fallback arrays.
    // Rebuild choice: keep every configured language in place; standard dictionary variant 0 only.
    // Unknown/implicit language is not evidence to convert Han-only Japanese or pre-existing user text.
    static string ApplyChineseTextPolicy(string text, string? resultLanguage, bool preferTraditional) =>
        preferTraditional && LanguageFamily(resultLanguage) == "zh" && ContentMatches(text, "zh")
            ? PinyinUtility.ToTraditional(PinyinUtility.ToSimplified(text)) : text;

    public static bool ApplyText(BaseItem item, MetadataLanguageResult result, bool blockNonFallback)
    {
        if (item.IsLocked) return false;
        var changed = false;
        if (!item.IsFieldLocked(MetadataFields.Name) && !string.IsNullOrWhiteSpace(result.Name) && item.Name != result.Name)
        { item.Name = result.Name; changed = true; }
        // An outage/missing provider is not proof of a disallowed overview. Clear only after a real provider response.
        if (!item.IsFieldLocked(MetadataFields.Overview) && (result.Overview != null || blockNonFallback && result.ObservedResponse &&
            !result.AllowedLanguages.Any(language => ContentMatches(item.Overview, language))))
        {
            var overview = result.Overview ?? string.Empty;
            if (item.Overview != overview) { item.Overview = overview; changed = true; }
        }
        return changed;
    }

    public static string[] ParseLanguages(string? value) => (value ?? string.Empty).Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(x => x.Length <= 16 && x.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static string ToHostLanguage(string language) => language.ToLowerInvariant() switch
    { "zho" => "zh-CN", "zhtw" => "zh-TW", "yue" => "zh-HK", "jpn" => "ja-JP", _ => language };
    public static string LanguageFamily(string? language) => ToHostLanguage(language ?? string.Empty).ToLowerInvariant().Split('-')[0] switch
    { "eng" => "en", "kor" => "ko", var value => value };
    public static bool SameLanguage(string? a, string? b) => string.Equals(ToHostLanguage(a ?? ""), ToHostLanguage(b ?? ""), StringComparison.OrdinalIgnoreCase) ||
        ((!ToHostLanguage(a ?? "").Contains('-') || !ToHostLanguage(b ?? "").Contains('-')) && LanguageFamily(a) == LanguageFamily(b));
    public static bool ContentMatches(string? text, string language)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var kana = text.Any(c => c is >= '\u3040' and <= '\u30ff' && c != '\u30fb');
        var han = text.Any(c => c is >= '\u3400' and <= '\u9fff');
        return LanguageFamily(language) switch
        {
            "zh" => han && !kana,
            "ja" => kana || han, // Kanji-only Japanese is ambiguous; provider ResultLanguage remains authoritative.
            "ko" => text.Any(c => c is >= '\uac00' and <= '\ud7af'),
            _ => true // No invented language detector for Latin-script languages.
        };
    }
    public static string ProviderKind(string? name) => (name ?? "").Replace(" ", "").ToLowerInvariant() switch
    { "themoviedb" or "themoviedb.org" or "moviedb" or "tmdb" => "tmdb", "thetvdb" or "thetvdb.com" or "tvdb" => "tvdb", _ => string.Empty };

    public static void Persist(ILibraryManager library, BaseItem item, CancellationToken token) =>
        library.UpdateItems(new List<BaseItem> { item }, null, ItemUpdateType.MetadataImport, true, false, null, token);

    public static IEnumerable<RemoteImageInfo> SortPosters(IEnumerable<RemoteImageInfo> images, string originalLanguage) =>
        OriginalPosterPolicy.Sort(images, originalLanguage);

    async Task<bool> ApplyPosterAsync(BaseItem item, MetadataEnhanceOptions settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // This is a missing-image fallback, not the source's provider/UI candidate hook. Never replace a user's poster.
        if (item is not (Movie or Series) || item.IsLocked || item.HasImage(ImageType.Primary, 0)) return false;
        var original = OriginalPosterPolicy.OriginalLanguageHint(item.OriginalTitle);
        if (original == null)
        {
            logger.Debug("Original poster {0}: {1}; no provider original_language is exposed by the public item contract.", item.InternalId,
                string.IsNullOrWhiteSpace(item.OriginalTitle) ? "no-original-title" : "unknown-original-language");
            return false;
        }
        var libraryOptions = library.GetLibraryOptions(item);
        var preferred = libraryOptions.PreferredImageLanguage;
        // Candidate lookup and its image save share this operation's native cache, never other items/runs.
        var directory = createDirectory();
        // The host owns provider enablement, credentials and its existing provider/rating/size/vote tie-breaks.
        var candidates = (await providers.GetAvailableRemoteImages(item, libraryOptions,
            new RemoteImageQuery { ImageType = ImageType.Primary, IncludeAllLanguages = true, IncludeDisabledProviders = false }, directory, token).ConfigureAwait(false)).ToArray();
        token.ThrowIfCancellationRequested();
        if (candidates.Length == 0)
        {
            // Native ProviderManager catches individual provider failures and returns empty. Do not manufacture a no-match verdict.
            logger.Warn("Original poster {0}: native-empty-or-provider-failure; inspect native GetImageInfos logs.", item.InternalId);
            return false;
        }
        var ranked = OriginalPosterPolicy.Sort(candidates, original, preferred).ToArray();
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            logger.Debug("Original poster candidate {0}: native-index={1}, provider={2}, language={3}, priority={4}, size={5}x{6}, rating={7}, votes={8}, type={9}, http={10}",
                item.InternalId, i, candidate.ProviderName, candidate.Language ?? "<unknown>", OriginalPosterPolicy.Priority(candidate, original, preferred),
                candidate.Width, candidate.Height, candidate.CommunityRating, candidate.VoteCount, candidate.Type, IsHttpUrl(candidate.Url));
        }
        var image = ranked.FirstOrDefault(candidate => OriginalPosterPolicy.Priority(candidate, original, preferred) < 4 && IsHttpUrl(candidate.Url));
        if (image == null) { logger.Debug("Original poster {0}: no-matching-candidate in {1} returned candidates.", item.InternalId, candidates.Length); return false; }
        paths.RequireWritablePath(item.GetInternalMetadataPath());
        var bytes = await ReadHttpAsync(http, new Uri(image.Url), 12 * 1024 * 1024, token).ConfigureAwait(false);
        var mime = ImageMime(bytes) ?? throw new InvalidDataException("Remote poster is not a supported raster image.");
        var work = paths.RequireWritablePath(Path.Combine(cacheRoot, "posters", "image-work"));
        Directory.CreateDirectory(work);
        var temp = paths.RequireWritablePath(Path.Combine(work, Guid.NewGuid().ToString("N") + ".tmp"));
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (item.IsLocked || item.HasImage(ImageType.Primary, 0)) return false;
            paths.RequireWritablePath(item.GetInternalMetadataPath());
            var previous = item.ImageInfos;
            try
            {
                await providers.SaveImage(item, new MediaBrowser.Model.Configuration.LibraryOptions { SaveLocalMetadata = false }, temp, mime.AsMemory(),
                    ImageType.Primary, 0, false, Array.Empty<long>(), directory, true, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                library.UpdateImages(item); // ImageSaver writes bytes/SetImage only; post-refresh owns the repository commit.
            }
            catch { item.ImageInfos = previous; throw; }
            logger.Debug("Original poster {0}: saved missing Primary via native SaveImage; language={1}, original-script-hint={2}, priority={3}",
                item.InternalId, image.Language, original, OriginalPosterPolicy.Priority(image, original, preferred));
            return true;
        }
        finally { try { if (File.Exists(temp)) File.Delete(paths.RequireWritablePath(temp)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.Warn("Poster temporary cleanup failed: {0}", ex.GetType().Name); } }
    }
    static string? ImageMime(byte[] b) => b.Length < 12 ? null :
        b[0] == 0xff && b[1] == 0xd8 && b[^2] == 0xff && b[^1] == 0xd9 ? "image/jpeg" :
        b.AsSpan(0, 8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 }) ? "image/png" :
        System.Text.Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(b, 8, 4) == "WEBP" ? "image/webp" : null;
    public static bool IsHttpUrl(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);

    internal static Uri TmdbEndpoint(MetadataEnhanceOptions settings, string relative)
    {
        var root = settings.AltMovieDbConfig ? settings.AltMovieDbApiUrl?.Trim().TrimEnd('/') : null;
        if (string.IsNullOrWhiteSpace(root)) root = "https://api.themoviedb.org";
        if (!IsHttpUrl(root) || !Uri.TryCreate(root, UriKind.Absolute, out var uri) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid TMDB API root.");
        root = uri.AbsoluteUri.TrimEnd('/');
        return new Uri(root + (root.EndsWith("/3", StringComparison.Ordinal) ? "/" : "/3/") + relative);
    }
    internal static async Task<JsonDocument?> GetTmdbAsync(HttpClientFactory http, MetadataEnhanceOptions settings, string relative, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.AltMovieDbApiKey)) return null;
        var endpoint = TmdbEndpoint(settings, relative);
        var url = new Uri(endpoint.AbsoluteUri + "?api_key=" + Uri.EscapeDataString(settings.AltMovieDbApiKey));
        var data = await ReadHttpAsync(http, url, 4 * 1024 * 1024, token).ConfigureAwait(false);
        return JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
    }
    internal static async Task<byte[]> ReadHttpAsync(HttpClientFactory http, Uri uri, int maxBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var response = await http.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Metadata response exceeds size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await ReadBoundedAsync(stream, maxBytes, token).ConfigureAwait(false);
    }
    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        { if (output.Length + count > maxBytes) throw new InvalidDataException("Metadata input exceeds size limit."); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
        return output.ToArray();
    }
}

public sealed class MetadataLanguageResult
{
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public bool ObservedResponse { get; set; }
    public DateTimeOffset? AiredPremiereDate { get; set; }
    public float? AiredRating { get; set; }
    public List<PersonInfo>? AiredPeople { get; set; }
    public string? AiredTmdbId { get; set; }
    public string? AiredImdbId { get; set; }
    public string? AiredTvdbId { get; set; }
    public string[] AllowedLanguages { get; set; } = Array.Empty<string>();
}
