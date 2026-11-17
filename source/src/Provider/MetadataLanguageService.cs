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
    public MetadataLanguageService(ILibraryManager library, IProviderManager providers, Func<IDirectoryService> createDirectory,
        Func<MetadataEnhanceOptions> options, HttpClientFactory http, ILogger logger, EmbyPathPolicy paths, string cacheRoot)
    { this.library = library; this.providers = providers; this.createDirectory = createDirectory; this.options = options; this.http = http; this.logger = logger; this.paths = paths; _ = cacheRoot; } // cacheRoot: kept for callers; the poster work folder went with M171.

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
            var (original, legacy) = item switch
            {
                Movie movie => await FetchBothAsync<Movie, MovieInfo>(movie, token).ConfigureAwait(false),
                Series series => await FetchBothAsync<Series, SeriesInfo>(series, token).ConfigureAwait(false),
                Season season => await FetchBothAsync<Season, SeasonInfo>(season, token).ConfigureAwait(false),
                Episode episode => await FetchBothAsync<Episode, EpisodeInfo>(episode, token).ConfigureAwait(false),
                BoxSet boxSet => await FetchBothAsync<BoxSet, ItemLookupInfo>(boxSet, token).ConfigureAwait(false),
                Person person => await FetchBothAsync<Person, PersonLookupInfo>(person, token).ConfigureAwait(false),
                _ => ((MetadataLanguageResult?)null, (MetadataLanguageResult?)null)
            };
            token.ThrowIfCancellationRequested();
            if (item.IsLocked) return false;
            var before = (item.Name, item.Overview, item.Tagline, item.Genres);
            try
            {
                if (original != null) changed |= ApplyText(item, original, settings.BlockNonFallbackLanguage);
                if (legacy != null)
                {
                    // TVDB keeps its own (rebuild) policy; it never re-decides a field the TMDB original rules just set.
                    if (original?.Name != null) legacy.Name = null;
                    if (original?.Overview != null) legacy.Overview = null;
                    changed |= ApplyText(item, legacy, settings.BlockNonFallbackLanguage && original == null);
                }
                var tmdb = settings.ChineseMovieDb && item is Movie or Series or Season or Episode && TmdbEnabled(item);
                if (tmdb && item is Series) changed |= MapSeriesGenres(item);
                if (changed) Persist(library, item, token);
                if (tmdb) changed |= ResetDirectorRoles(item);
            }
            catch { (item.Name, item.Overview, item.Tagline, item.Genres) = before; throw; }
        }
        // M171: PreferOriginalPoster has no post-refresh step. The original only orders native candidates (NativePosterOrdering)
        // and adds season00-poster (NativeSeasonZeroPoster); the former rebuild-only missing-Primary download was removed.
        return changed;
    }

    /// <summary>M174: native TMDB detail reader (series alternative titles, US/JP ratings); replaceable by component tests.</summary>
    internal Func<object?, string, string?, string?, CancellationToken, Task<JsonElement?>> TmdbDetails { get; set; } = NativeTmdbDetails.FetchAsync;
    private static long alternativeTitles, ratingFailures;
    /// <summary>Series alternative titles offered to the name rule (A:53162-53472).</summary>
    public static long AlternativeTitlesFound => Interlocked.Read(ref alternativeTitles);
    public static long RatingDetailFailures => Interlocked.Read(ref ratingFailures);

    /// <summary>
    /// M174: the original US/JP content-rating fallback for a Movie or Series (see <see cref="OriginalContentRatings"/>), applied
    /// after the host refresh. Runs only when the preferred country (lookup MetadataCountryCode) has a fallback file, TMDB is an
    /// enabled provider and the item has a TMDB id; the native detail for the main language is read again (normally a native cache
    /// hit). A failed read changes nothing. Returns true when OfficialRating changed and was saved.
    /// </summary>
    public async Task<bool> ApplyContentRatingAsync(BaseItem item, OriginalContentRatings ratings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (item is not (Movie or Series) || item.IsLocked || item.IsFieldLocked(MetadataFields.OfficialRating)) return false;
        var movie = item is Movie;
        var libraryOptions = library.GetLibraryOptions(item);
        ItemLookupInfo lookup = item is Movie m ? m.GetLookupInfo(libraryOptions) : ((Series)item).GetLookupInfo(libraryOptions);
        var country = lookup.MetadataCountryCode?.ToUpperInvariant();
        var tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
        if (country == null || string.IsNullOrEmpty(tmdbId) || ratings.Map(movie, country) is not { } map) return false;
        // The detail provider itself (MovieDbProvider / MovieDbSeriesProvider), not another enabled TheMovieDb provider such as images.
        var provider = providers.GetEnabledMetadataProviders(item, libraryOptions)
            .Where(p => movie ? p is IRemoteMetadataProvider<Movie, MovieInfo> : p is IRemoteMetadataProvider<Series, SeriesInfo>).FirstOrDefault(p => ProviderKind(p.Name) == "tmdb");
        if (provider == null) return false;
        JsonElement? details;
        try { details = await (TmdbDetails ?? NativeTmdbDetails.FetchAsync)(provider, movie ? "movie" : "series", tmdbId, lookup.MetadataLanguage, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Interlocked.Increment(ref ratingFailures); logger.Warn("Content rating fallback skipped for {0}: {1}", item.InternalId, ex.GetType().Name); return false; }
        token.ThrowIfCancellationRequested();
        if (details is not { } dto || item.IsLocked) return false;
        var official = item.OfficialRating?.Trim();
        var final = OriginalContentRatings.Decide(official, movie, country, NativeTmdbDetails.Ratings(dto, movie), map, ratings.Known(country), out var outcome);
        ratings.Count(outcome);
        if (string.Equals(final, item.OfficialRating, StringComparison.Ordinal)) return false;
        var previous = item.OfficialRating;
        item.OfficialRating = final;
        try { Persist(library, item, token); }
        catch { item.OfficialRating = previous; throw; }
        return true;
    }

    bool TmdbEnabled(BaseItem item) =>
        providers.GetEnabledMetadataProviders(item, library.GetLibraryOptions(item)).Any(p => ProviderKind(p.Name) == "tmdb");

    /// <summary>M170: original series ImportData genre names (52575).</summary>
    static bool MapSeriesGenres(BaseItem item)
    {
        if (item.IsFieldLocked(MetadataFields.Genres) || item.Genres == null || item.Genres.Length == 0) return false;
        var mapped = item.Genres.Select(ChineseTextRules.MapSeriesGenre).ToArray();
        if (mapped.SequenceEqual(item.Genres, StringComparer.Ordinal)) return false;
        item.Genres = mapped;
        return true;
    }

    /// <summary>M170: original People cleanup (51606): a non-actor with a Han name and role "Director" loses the English role text.</summary>
    bool ResetDirectorRoles(BaseItem item)
    {
        if (item.IsLocked || item.IsFieldLocked(MetadataFields.Cast)) return false;
        var people = library.GetItemPeople(new InternalPeopleQuery { ItemIds = new[] { item.InternalId }, EnableIds = true, EnableImages = true, EnableProviderIds = true });
        if (people == null || people.Count == 0) return false;
        var changed = false;
        foreach (var person in people)
            if (person != null && person.Type != PersonType.Actor && ChineseTextRules.Han(person.Name) && person.Role == "Director") { person.Role = string.Empty; changed = true; }
        if (changed) library.UpdatePeople(item, people, false);
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

    /// <summary>Grouped episode lookup at aired coordinates: the native main-language request first (the host refreshed the wrong
    /// coordinates), then — only with ChineseMovieDb — the original fallback languages and rules.</summary>
    public async Task<MetadataLanguageResult?> FetchEpisodeAsync(Episode item, int season, int episode, CancellationToken token)
    {
        var settings = options();
        var libraryOptions = library.GetLibraryOptions(item);
        var tmdb = providers.GetEnabledMetadataProviders(item, libraryOptions).OfType<IRemoteMetadataProvider<Episode, EpisodeInfo>>().FirstOrDefault(p => ProviderKind(p.Name) == "tmdb");
        if (tmdb == null) return null;
        return await FetchOriginalAsync(item, tmdb, lookup => { lookup.ParentIndexNumber = season; lookup.IndexNumber = episode; lookup.ProviderIds = new ProviderIdDictionary(); },
            libraryOptions, settings, token, true).ConfigureAwait(false);
    }

    async Task<(MetadataLanguageResult? Original, MetadataLanguageResult? Legacy)> FetchBothAsync<T, TLookup>(T item, CancellationToken token)
        where T : BaseItem, IHasLookupInfo<TLookup>, new() where TLookup : ItemLookupInfo, new()
    {
        var settings = options();
        var libraryOptions = library.GetLibraryOptions(item);
        var typed = providers.GetEnabledMetadataProviders(item, libraryOptions).OfType<IRemoteMetadataProvider<T, TLookup>>().ToArray();
        MetadataLanguageResult? original = null;
        // Person is not part of the original ChineseMovieDb; it keeps the rebuild's legacy pass below.
        if (settings.ChineseMovieDb && item is not Person && typed.FirstOrDefault(p => ProviderKind(p.Name) == "tmdb") is { } tmdb)
            original = await FetchOriginalAsync(item, tmdb, null, libraryOptions, settings, token, false).ConfigureAwait(false);
        var legacy = settings.ChineseTvdb || settings.ChineseMovieDb && item is Person
            ? await FetchLegacyAsync(item, typed, libraryOptions, settings, item is Person, token).ConfigureAwait(false) : null;
        return (original, legacy);
    }

    sealed class TextState { public string? Name, Overview, Tagline; }

    /// <summary>
    /// M170: the original ChineseMovieDb language loop, run after the host refresh. The host's main-language result is the first
    /// iteration (normalized and converted as the original's first ProcessMainInfo call would); then the fixed fallback order is
    /// fetched one language at a time with the original merge rules, stopping as soon as the original completeness check passes.
    /// </summary>
    async Task<MetadataLanguageResult> FetchOriginalAsync<T, TLookup>(T item, IRemoteMetadataProvider<T, TLookup> provider, Action<TLookup>? configure,
        MediaBrowser.Model.Configuration.LibraryOptions libraryOptions, MetadataEnhanceOptions settings, CancellationToken token, bool grouped)
        where T : BaseItem, IHasLookupInfo<TLookup>, new() where TLookup : ItemLookupInfo, new()
    {
        var chinese = settings.ChineseMovieDb;
        var ja = chinese && ChineseTextRules.JapaneseFallback(settings);
        var target = item.GetLookupInfo(libraryOptions).MetadataLanguage;
        var main = item is Movie or Series;
        var originalTitle = main ? item.OriginalTitle : null;
        var ruleLanguage = main ? ChineseTextRules.RuleLanguage(item.OriginalTitle) : null;
        var normalize = chinese && main;
        var taglines = chinese && main && !settings.ClearTagline;
        string Convert(string text, string? fetched) => chinese ? ChineseTextRules.Convert(text, fetched, target, settings.PreferTraditionalChinese) : text;
        var state = new TextState();
        // M174: series alternative titles (EnsureSeriesInfo postfix, A:53162-53472). The native detail is read once, only when a
        // name without Han needs one; alternative_titles and languages are the same in every language's response.
        var alternatives = chinese && item is Series;
        JsonElement? details = null;
        var detailsRead = false;
        async Task<string?> Alternative(string? name, string? language)
        {
            if (!alternatives || ChineseTextRules.Han(name) || string.IsNullOrEmpty(language) || !language.Contains('-')) return null;
            if (!detailsRead)
            {
                detailsRead = true;
                try { details = await (TmdbDetails ?? NativeTmdbDetails.FetchAsync)(provider, "series", item.GetProviderId(MetadataProviders.Tmdb), target, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.Warn("Series alternative titles skipped for {0}: {1}", item.InternalId, ex.GetType().Name); }
            }
            if (details is not { } dto) return null;
            var title = ChineseTextRules.AlternativeTitle(name, language, NativeTmdbDetails.FirstLanguage(dto), ja, NativeTmdbDetails.AlternativeTitles(dto));
            if (title != null) Interlocked.Increment(ref alternativeTitles);
            return title;
        }
        if (!grouped)
        {
            state.Name = item.Name; state.Overview = item.Overview; state.Tagline = taglines ? item.Tagline : null;
            // Rebuild adaptation: the host's main-language name stands for the first native response.
            if (await Alternative(state.Name, target).ConfigureAwait(false) is { } mainAlternative) state.Name = mainAlternative;
            if (normalize && !string.IsNullOrEmpty(state.Overview)) state.Overview = ChineseTextRules.NormalizeOverview(state.Overview);
            if (chinese && LanguageFamily(target) == "zh")
            {
                // Rebuild adaptation: stored text may come from an earlier ja-JP fallback, which the original never converts;
                // kana-bearing text is treated as that Japanese result and left alone.
                string? Main(string? text) => string.IsNullOrEmpty(text) || ChineseTextRules.Kana(text) ? text : Convert(text, target);
                state.Name = Main(state.Name); state.Overview = Main(state.Overview); state.Tagline = Main(state.Tagline);
            }
            // Rebuild adaptation: in the original the fallbacks run before the host's English fallback; an English episode name
            // left by that later step stands for "no localized name yet".
            if (item is Episode && !ChineseTextRules.Localized(state.Name, ja)) state.Name = null;
        }
        bool Complete() => item is Episode
            ? !string.IsNullOrWhiteSpace(state.Name) && !ChineseTextRules.ZhEpisodePlaceholder(state.Name) && !ChineseTextRules.JaEpisodePlaceholder(state.Name) && ChineseTextRules.Localized(state.Overview, ja)
            : ChineseTextRules.Localized(state.Name, ja) && ChineseTextRules.Localized(state.Overview, ja) && (!taglines || ChineseTextRules.Localized(state.Tagline, ja));
        var result = new MetadataLanguageResult { OriginalRules = true, JapaneseFallback = ja };
        var languages = new List<string?>();
        if (grouped) languages.Add(null); // native main-language request at the aired coordinates
        if (chinese) languages.AddRange(ChineseTextRules.FallbackOrder(settings));
        var observedEpisodeMetadata = false;
        IDirectoryService? directory = null; // One native cache per logical lookup, shared only by its fallbacks.
        foreach (var language in languages)
        {
            token.ThrowIfCancellationRequested();
            if (language != null && Complete()) break;
            try
            {
                var lookup = item.GetLookupInfo(libraryOptions);
                configure?.Invoke(lookup);
                if (language != null) SetSingleLanguage(lookup, language);
                var response = await GetMetadataAsync(provider, lookup, () => directory ??= createDirectory(), token).ConfigureAwait(false);
                if (response?.HasMetadata != true || response.Item == null) continue;
                result.ObservedResponse = true;
                if (grouped && !observedEpisodeMetadata && lookup is EpisodeInfo requested && response.Item is Episode episode)
                {
                    // Native returned-item contract check, not proof of raw response coordinates: the first successful
                    // result owns IDs/date/rating/cast; later language fallbacks must not replace that decision.
                    observedEpisodeMetadata = true;
                    CaptureAired(result, requested, episode, response.People);
                }
                if (language != null && !SameLanguage(string.IsNullOrWhiteSpace(response.ResultLanguage) ? language : response.ResultLanguage, language)) continue;
                var fetched = language ?? lookup.MetadataLanguage;
                var r = response.Item;
                var name = language != null && await Alternative(r.Name, fetched).ConfigureAwait(false) is { } alternative ? alternative : r.Name;
                var takeName = item switch
                {
                    Episode => ChineseTextRules.ReplaceEpisodeName(state.Name, name, ja),
                    Movie or Series => ChineseTextRules.ReplaceName(state.Name, name, originalTitle, ruleLanguage, ja),
                    _ => ChineseTextRules.ReplaceText(state.Name, name, null, ja)
                };
                if (takeName) state.Name = Convert(name!, fetched);
                if (ChineseTextRules.ReplaceText(state.Overview, r.Overview, ruleLanguage, ja))
                    state.Overview = Convert(normalize ? ChineseTextRules.NormalizeOverview(r.Overview!) : r.Overview!, fetched);
                if (taglines && ChineseTextRules.ReplaceText(state.Tagline, r.Tagline, ruleLanguage, ja)) state.Tagline = Convert(r.Tagline!, fetched);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.Warn("Metadata language provider tmdb/{0} skipped: {1}", language ?? "main", ex.GetType().Name); }
        }
        if (state.Name != null && state.Name != item.Name) result.Name = state.Name;
        if (state.Overview != null && state.Overview != item.Overview) result.Overview = state.Overview;
        if (taglines && state.Tagline != null && state.Tagline != item.Tagline) result.Tagline = state.Tagline;
        return result;
    }

    static void SetSingleLanguage(ItemLookupInfo lookup, string language)
    {
        lookup.MetadataLanguage = ToHostLanguage(language);
        // Do not carry the host's original multi-language preference into this explicit single-language request.
        lookup.MetadataLanguages = new[] { new MediaBrowser.Model.Globalization.CultureDto
        { Name = lookup.MetadataLanguage, TwoLetterISOLanguageNames = new[] { LanguageFamily(language) }, ThreeLetterISOLanguageNames = new[]
            { language.ToLowerInvariant() switch { "zhtw" => "zhtw", "yue" => "yue", _ => LanguageFamily(language) switch { "zh" => "zho", "ja" => "jpn", "ko" => "kor", "en" => "eng", var value => value } } } } };
    }

    static async Task<MetadataResult<T>?> GetMetadataAsync<T, TLookup>(IRemoteMetadataProvider<T, TLookup> provider, TLookup lookup, Func<IDirectoryService> directory, CancellationToken token)
        where T : BaseItem, IHasLookupInfo<TLookup>, new() where TLookup : ItemLookupInfo, new() =>
        provider is IRemoteMetadataProviderWithOptions<T, TLookup> withOptions
            ? await withOptions.GetMetadata(new RemoteMetadataFetchOptions<TLookup> { SearchInfo = lookup, DirectoryService = directory() }, token).ConfigureAwait(false)
            : await provider.GetMetadata(lookup, token).ConfigureAwait(false);

    static void CaptureAired(MetadataLanguageResult result, EpisodeInfo requested, Episode episode, List<PersonInfo>? people)
    {
        if (episode.ParentIndexNumber != requested.ParentIndexNumber || episode.IndexNumber != requested.IndexNumber) return;
        if (episode.PremiereDate is { } date && date != default(DateTimeOffset)) result.AiredPremiereDate = date;
        result.AiredTmdbId = PositiveId(episode.GetProviderId(MetadataProviders.Tmdb));
        result.AiredTvdbId = PositiveId(episode.GetProviderId(MetadataProviders.Tvdb));
        if (episode.CommunityRating is >= 0 and <= 10) result.AiredRating = episode.CommunityRating;
        result.AiredPeople = CapturePeople(people);
        var imdb = episode.GetProviderId(MetadataProviders.Imdb);
        if (imdb?.StartsWith("tt", StringComparison.Ordinal) == true && PositiveId(imdb[2..]) != null) result.AiredImdbId = imdb;
    }

    /// <summary>Rebuild pass for TVDB (ChineseTvdb) and, rebuild-only, TMDB Person text: user order, first matching script.</summary>
    async Task<MetadataLanguageResult?> FetchLegacyAsync<T, TLookup>(T item, IRemoteMetadataProvider<T, TLookup>[] typed,
        MediaBrowser.Model.Configuration.LibraryOptions libraryOptions, MetadataEnhanceOptions settings, bool includeTmdb, CancellationToken token)
        where T : BaseItem, IHasLookupInfo<TLookup>, new() where TLookup : ItemLookupInfo, new()
    {
        var useTmdb = includeTmdb && settings.ChineseMovieDb;
        var result = new MetadataLanguageResult { AllowedLanguages = ParseLanguages((useTmdb ? settings.FallbackLanguages : "") + "," + (settings.ChineseTvdb ? settings.TvdbFallbackLanguages : "")) };
        IDirectoryService? directory = null;
        // Preserve host provider ordering; only exact recognized names are selected, never reflected methods.
        foreach (var provider in typed)
        {
            var kind = ProviderKind(provider.Name);
            if (kind == "tmdb" ? !useTmdb : kind == "tvdb" ? !settings.ChineseTvdb : true) continue;
            foreach (var language in ParseLanguages(kind == "tmdb" ? settings.FallbackLanguages : settings.TvdbFallbackLanguages))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var lookup = item.GetLookupInfo(libraryOptions);
                    SetSingleLanguage(lookup, language);
                    var response = await GetMetadataAsync(provider, lookup, () => directory ??= createDirectory(), token).ConfigureAwait(false);
                    if (response?.HasMetadata != true || response.Item == null) continue;
                    result.ObservedResponse = true;
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

    // Legacy (TVDB/Person) pass: convert only confirmed Chinese text, and only with PreferTraditionalChinese.
    static string ApplyChineseTextPolicy(string text, string? resultLanguage, bool preferTraditional) =>
        preferTraditional && LanguageFamily(resultLanguage) == "zh" && ContentMatches(text, "zh")
            ? PinyinUtility.ToTraditional(PinyinUtility.ToSimplified(text)) : text;

    public static bool ApplyText(BaseItem item, MetadataLanguageResult result, bool blockNonFallback)
    {
        if (item.IsLocked) return false;
        var changed = false;
        if (result.OriginalRules)
        {
            // Values were already decided by the original rules; this only writes them, honoring field locks.
            if (result.Name != null && !item.IsFieldLocked(MetadataFields.Name) && item.Name != result.Name) { item.Name = result.Name; changed = true; }
            if (!item.IsFieldLocked(MetadataFields.Overview))
            {
                var overview = result.Overview ?? item.Overview;
                // Original BlockNonFallback runs on a real provider result only; an outage is not proof.
                if (blockNonFallback && result.ObservedResponse && ChineseTextRules.BlockOverview(overview, result.JapaneseFallback)) overview = string.Empty;
                if (overview != null && item.Overview != overview) { item.Overview = overview; changed = true; }
            }
            if (result.Tagline != null && !item.IsFieldLocked(MetadataFields.Tagline) && item.Tagline != result.Tagline) { item.Tagline = result.Tagline; changed = true; }
            return changed;
        }
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
    internal static async Task<JsonDocument?> GetTmdbAsync(HttpClientFactory http, MetadataEnhanceOptions settings, string relative, CancellationToken token, string? query = null)
    {
        if (string.IsNullOrWhiteSpace(settings.AltMovieDbApiKey)) return null;
        var endpoint = TmdbEndpoint(settings, relative);
        var url = new Uri(endpoint.AbsoluteUri + "?" + (string.IsNullOrEmpty(query) ? "" : query + "&") + "api_key=" + Uri.EscapeDataString(settings.AltMovieDbApiKey));
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
    public string? Tagline { get; set; }
    /// <summary>M170: Name/Overview/Tagline are final values decided by the original ChineseMovieDb rules.</summary>
    public bool OriginalRules { get; set; }
    public bool JapaneseFallback { get; set; }
    public bool ObservedResponse { get; set; }
    public DateTimeOffset? AiredPremiereDate { get; set; }
    public float? AiredRating { get; set; }
    public List<PersonInfo>? AiredPeople { get; set; }
    public string? AiredTmdbId { get; set; }
    public string? AiredImdbId { get; set; }
    public string? AiredTvdbId { get; set; }
    public string[] AllowedLanguages { get; set; } = Array.Empty<string>();
}
