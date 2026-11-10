using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Provider;

/// <summary>
/// M144/F1: independent rating lookback (RatingUpdateLookbackDays) and MDBList Rotten Tomatoes source (MdbListApiKey).
/// Observed original contract (decompiled BuildDoubanCacheTask + candidate predicate):
///   candidates = Movie/Series/Season with a Douban id, PremiereDate in [UtcNow - lookback, UtcNow], newest premiere first;
///   an existing Douban cache file is required; refresh when the file is older than 8 hours for an already rated item
///   whose premiere is inside the lookback window, otherwise when it is older than 30 days.
///   MDBList: "https://api.mdblist.com/" + path + "?apikey=" + key + "&amp;format=json"; ratings[source=tomatoes].value -> CriticRating.
/// The exact MDBList path literal is encrypted in the original; "imdb/{movie|show}/{tt}" is a reconstruction from the adjacent
/// recovered literals ("/imdb/", "movie", "show") and the public MDBList API, not an observed string.
/// </summary>
public static class RatingRefreshRules
{
    public static readonly TimeSpan RecentInterval = TimeSpan.FromHours(8);
    public static readonly TimeSpan StaleInterval = TimeSpan.FromDays(30);
    public const int MaxLookbackDays = 36500;

    public static bool IsCandidateType(BaseItem item) => item is Movie or Series or Season;

    /// <summary>Lookback window start; nonpositive or absurd values are rejected rather than widened.</summary>
    public static DateTimeOffset? WindowStart(int lookbackDays, DateTimeOffset now) =>
        lookbackDays is >= 1 and <= MaxLookbackDays ? now.AddDays(-lookbackDays) : null;

    public static bool InWindow(DateTimeOffset? premiere, DateTimeOffset windowStart, DateTimeOffset now) =>
        premiere is { } date && date >= windowStart && date <= now;

    /// <summary>Original per-item predicate. cacheWriteUtc is the existing Douban cache file time; null means no cache file.</summary>
    public static bool IsDue(float? communityRating, DateTimeOffset? premiere, DateTimeOffset? cacheWriteUtc, DateTimeOffset windowStart, DateTimeOffset now)
    {
        if (cacheWriteUtc is not { } written || written.ToUnixTimeSeconds() <= 0) return false;
        var recent = communityRating.HasValue && premiere is { } date && date > windowStart;
        return written < now - (recent ? RecentInterval : StaleInterval);
    }

    public static string? MdbListPath(BaseItem item, string? imdb)
    {
        if (!DoubanSubjectParser.IsImdbId(imdb)) return null;
        return item switch { Movie => "imdb/movie/" + imdb, Series or Season => "imdb/show/" + imdb, _ => null };
    }

    /// <summary>ratings[] entry whose source is "tomatoes" (case-insensitive); value must be a finite 0..100 score.</summary>
    public static float? ParseRottenTomatoes(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("ratings", out var ratings) || ratings.ValueKind != JsonValueKind.Array) return null;
        foreach (var rating in ratings.EnumerateArray())
        {
            if (rating.ValueKind != JsonValueKind.Object || !rating.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String ||
                !string.Equals(source.GetString(), "tomatoes", StringComparison.OrdinalIgnoreCase)) continue;
            if (!rating.TryGetProperty("value", out var value)) return null;
            float score;
            if (value.ValueKind == JsonValueKind.Number) { if (!value.TryGetSingle(out score)) return null; }
            else if (value.ValueKind != JsonValueKind.String || !float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out score)) return null;
            return float.IsFinite(score) && score is >= 0 and <= 100 ? score : null;
        }
        return null;
    }
}

public sealed record MdbListLookup(float? Score, string Status);
public sealed record RatingItemResult(bool Community, bool Critic, string Status)
{
    // A provider failure for an enabled source is a failure even when the other source succeeded.
    public bool Failed => Status.Split(':').Any(part => part is "douban-http-error" or "douban-timeout" or "douban-invalid-response" or
        "douban-identity-mismatch" or "douban-cache-path-error" or "mdblist-timeout" or "mdblist-http-error" or "mdblist-invalid-response" or
        "mdblist-unauthorized" or "mdblist-rate-limited" or "invalid-cache-mode" || part.StartsWith("mdblist-http-", StringComparison.Ordinal));
}
public sealed record RatingBatchResult(int Total, int Community, int Critic, int Unchanged, int Failed);

public sealed class MdbListClient
{
    public const string BaseUrl = "https://api.mdblist.com/";
    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    private readonly int maxBytes;
    public MdbListClient(HttpClientFactory factory, TimeSpan? timeout = null, int maxBytes = 512 * 1024)
    { http = factory.Client; this.timeout = timeout ?? TimeSpan.FromSeconds(20); this.maxBytes = maxBytes; }

    public static string BuildUrl(string path, string apiKey) =>
        BaseUrl + path + "?apikey=" + Uri.EscapeDataString(apiKey) + "&format=json";

    public async Task<MdbListLookup> GetRottenTomatoesAsync(string path, string apiKey, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new(null, "no-key");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(path, apiKey.Trim()));
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode == 429) return new(null, "rate-limited");
            if ((int)response.StatusCode is 401 or 403) return new(null, "unauthorized");
            if ((int)response.StatusCode == 404) return new(null, "not-found");
            if (!response.IsSuccessStatusCode) return new(null, "http-" + (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > maxBytes) return new(null, "invalid-response");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var data = await DoubanCache.ReadBoundedAsync(stream, maxBytes, deadline.Token).ConfigureAwait(false);
            var score = RatingRefreshRules.ParseRottenTomatoes(data);
            return score == null ? new(null, "no-tomatoes") : new(score, "online");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null, "timeout"); }
        catch (HttpRequestException) { return new(null, "http-error"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { return new(null, "invalid-response"); }
    }
}
