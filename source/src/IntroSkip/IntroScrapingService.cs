using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.IntroSkip;

public sealed record IntroLookupRequest(string? TmdbId, string? ImdbId, bool IsMovie, int? SeasonNumber, int? EpisodeNumber, long? RunTimeTicks)
{
    public static IntroLookupRequest FromItem(BaseItem item)
    {
        var source = item is Episode episode ? episode.Series : item;
        return new(source?.GetProviderId(MetadataProviders.Tmdb), source?.GetProviderId(MetadataProviders.Imdb),
            item is Movie, item.ParentIndexNumber, item.IndexNumber, item.RunTimeTicks);
    }
}

/// <summary>Documented read-only provider APIs. HTTP ownership always remains with Common.HttpClientFactory.</summary>
public sealed class IntroScrapingService
{
    private readonly HttpClient http;
    private readonly ILogger logger;
    private readonly TimeSpan timeout;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 32 };
    public IntroScrapingService(HttpClientFactory factory, ILogger logger, TimeSpan? providerTimeout = null)
    {
        http = factory.Client;
        this.logger = logger;
        timeout = providerTimeout ?? TimeSpan.FromSeconds(15);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(providerTimeout));
    }

    public async Task<IntroMarkerSet?> GetMarkersAsync(IntroLookupRequest request, IntroSkipScrapeOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!request.IsMovie && (request.SeasonNumber is not >= 0 || request.EpisodeNumber is not > 0)) return null;
        if (request.RunTimeTicks is <= 0) return null;
        long? start = null, end = null, credits = null;
        var order = string.IsNullOrWhiteSpace(options.UnifiedIntroDbProviders) ? "Tidb,IntroDb" : options.UnifiedIntroDbProviders;
        foreach (var provider in order.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            try
            {
                IntroMarkerSet? result = null;
                if (provider.Equals("Tidb", StringComparison.OrdinalIgnoreCase)) result = await GetTidb(request, options.TidbApiKey, deadline.Token).ConfigureAwait(false);
                else if (provider.Equals("IntroDb", StringComparison.OrdinalIgnoreCase)) result = await GetIntroDb(request, deadline.Token).ConfigureAwait(false);
                else logger.Warn("[StrmAssistantRebuild] Unknown intro provider: {0}", provider);
                token.ThrowIfCancellationRequested();
                if (result == null) continue;
                if (!end.HasValue && result.HasIntro) { start = result.IntroStartTicks; end = result.IntroEndTicks; }
                credits ??= result.CreditsStartTicks;
                if (end.HasValue && credits.HasValue) break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Do not log exception messages: transports may embed request headers or API keys.
                logger.Warn("[StrmAssistantRebuild] Intro provider {0} failed ({1}); trying next provider.", provider, ex.GetType().Name);
            }
        }
        token.ThrowIfCancellationRequested();
        if (!end.HasValue && !credits.HasValue) return null;
        var markers = new IntroMarkerSet(start, end, credits);
        markers.Validate(request.RunTimeTicks);
        return markers;
    }

    private async Task<IntroMarkerSet?> GetTidb(IntroLookupRequest request, string? apiKey, CancellationToken token)
    {
        var id = PositiveNumber(request.TmdbId) ? "tmdb_id=" + request.TmdbId : ValidImdb(request.ImdbId) ? "imdb_id=" + request.ImdbId : null;
        if (id == null) return null;
        // Current official v4 contract, not a recovered string from the original plugin:
        // https://theintrodb.org/openapi.yaml ; https://theintrodb.github.io/theintrodb-npm/migration-v3-to-v4.html
        var url = "https://api.theintrodb.org/v4/media?" + id + EpisodeQuery(request);
        if (request.RunTimeTicks is > 0) url += "&duration_ms=" + (request.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture);
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        var result = await Read<TidbResponse>(message, token).ConfigureAwait(false);
        if (result == null || result.error != null || result.type != (request.IsMovie ? "movie" : "tv") ||
            !request.IsMovie && (result.season != request.SeasonNumber || result.episode != request.EpisodeNumber)) return null;
        var intro = result.intro?.Where(s => s != null && ValidIntro(s.start_ms ?? 0, s.end_ms, request.RunTimeTicks))
            .OrderByDescending(s => s.end_ms!.Value - s.start_ms.GetValueOrDefault()).FirstOrDefault();
        // Original plugin selects the first valid credits start, not the longest credits segment.
        var outro = result.credits?.FirstOrDefault(s => s != null && ValidCredits(s.start_ms, request.RunTimeTicks));
        return Markers(intro?.start_ms ?? (intro != null ? 0 : null), intro?.end_ms, outro?.start_ms);
    }

    private async Task<IntroMarkerSet?> GetIntroDb(IntroLookupRequest request, CancellationToken token)
    {
        if (!ValidImdb(request.ImdbId)) return null;
        // https://introdb.app/docs/api: GET reads are anonymous. X-API-Key authenticates POST /submit only.
        // IntroDbApiKey is deliberately not sent to an undocumented GET header (original header remains encrypted).
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://api.introdb.app/segments?imdb_id=" + request.ImdbId +
            (request.IsMovie ? "&is_movie=true" : EpisodeQuery(request)));
        var result = await Read<IntroDbResponse>(message, token).ConfigureAwait(false);
        if (result == null || result.error != null || result.imdb_id != request.ImdbId || result.is_movie != request.IsMovie ||
            !request.IsMovie && (result.season != request.SeasonNumber || result.episode != request.EpisodeNumber)) return null;
        var intro = !result.no_intro && result.intro != null && ValidIntro(result.intro.start_ms, result.intro.end_ms, request.RunTimeTicks) ? result.intro : null;
        var outro = !result.no_outro && result.outro != null && ValidCredits(result.outro.start_ms, request.RunTimeTicks) ? result.outro : null;
        return Markers(intro?.start_ms, intro?.end_ms, outro?.start_ms);
    }

    private async Task<T?> Read<T>(HttpRequestMessage request, CancellationToken token)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return default;
        response.EnsureSuccessStatusCode();
        const int maxBytes = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes) throw new IOException("Intro response exceeds 1 MiB.");
        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await body.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (bounded.Length + count > maxBytes) throw new IOException("Intro response exceeds 1 MiB.");
            bounded.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return JsonSerializer.Deserialize<T>(bounded.GetBuffer().AsSpan(0, (int)bounded.Length), JsonOptions);
    }
    private static string EpisodeQuery(IntroLookupRequest request) => request.IsMovie ? "" :
        "&season=" + request.SeasonNumber!.Value.ToString(CultureInfo.InvariantCulture) + "&episode=" + request.EpisodeNumber!.Value.ToString(CultureInfo.InvariantCulture);
    private static bool PositiveNumber(string? value) => value != null && value.All(c => c is >= '0' and <= '9') && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
    private static bool ValidImdb(string? value) => value != null && Regex.IsMatch(value, @"\Att[0-9]{1,12}\z", RegexOptions.CultureInvariant);
    private static bool ValidTicks(long? ms, long? runtime) => ms is >= 0 && ms <= long.MaxValue / TimeSpan.TicksPerMillisecond &&
        (!runtime.HasValue || ms * TimeSpan.TicksPerMillisecond < runtime.Value);
    private static bool ValidIntro(long? start, long? end, long? runtime) => ValidTicks(start, runtime) && ValidTicks(end, runtime) && end > start;
    private static bool ValidCredits(long? start, long? runtime) => start > 0 && ValidTicks(start, runtime);
    private static IntroMarkerSet? Markers(long? start, long? end, long? credits) =>
        end.HasValue || credits.HasValue ? new(start * TimeSpan.TicksPerMillisecond, end * TimeSpan.TicksPerMillisecond, credits * TimeSpan.TicksPerMillisecond) : null;
}

// Nullable numeric fields distinguish absent data from genuine zero. Property names match wire JSON.
public sealed class IntroSegmentDto
{
    public long? start_ms { get; set; }
    public long? end_ms { get; set; }
}
public sealed class TidbResponse
{
    public string? type { get; set; }
    public int? season { get; set; }
    public int? episode { get; set; }
    public IntroSegmentDto[]? intro { get; set; }
    public IntroSegmentDto[]? credits { get; set; }
    public string? error { get; set; }
}
public sealed class IntroDbResponse
{
    public string? imdb_id { get; set; }
    public bool? is_movie { get; set; }
    public int? season { get; set; }
    public int? episode { get; set; }
    public IntroSegmentDto? intro { get; set; }
    public IntroSegmentDto? outro { get; set; }
    public bool no_intro { get; set; }
    public bool no_outro { get; set; }
    public string? error { get; set; }
}
