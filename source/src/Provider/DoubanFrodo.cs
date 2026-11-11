using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Provider;

// M149/F2: the original channel talks to Douban's Android-app API (frodo) with the public app key and an HMAC-SHA1
// request signature. Constants and the signing recipe are the original's (strings 189002/190208/194420/194456).
public sealed record DoubanHotItem(string Id, string Type, string Title, string? OriginalTitle, string? CardSubtitle, string? Info,
    string? CoverUrl, int? CoverWidth, int? CoverHeight, string? PicUrl, float? Rating, string? Year, string? ReleaseDate)
{
    public string? ImageUrl => !string.IsNullOrWhiteSpace(CoverUrl) ? CoverUrl : string.IsNullOrWhiteSpace(PicUrl) ? null : PicUrl;
    // Original folder-image preference: a cover whose known width:height is at most 3:4.
    public bool IsPortrait => CoverWidth is > 0 && CoverHeight is > 0 && CoverWidth.Value * 4 <= CoverHeight.Value * 3;
}
public sealed record DoubanTrailerInfo(string? Id, string? Title, string VideoUrl, string? Runtime);

public static class DoubanFrodo
{
    public const string BaseUrl = "https://frodo.douban.com/api/v2";
    public const string ApiKey = "0dad551ec0f84ed02907ff5c42e8ec70";
    private const string Secret = "bf7dddc7c9cfe6f7";
    public const string UserAgent = "api-client/1 com.douban.frodo/7.22.0.beta9(231) Android/23 product/Mate 40 vendor/HUAWEI model/Mate 40 brand/HUAWEI rom/android network/wifi platform/AndroidPad";
    private const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-!.~'()*";

    /// <summary>Original: base64(HMAC-SHA1(secret, METHOD &amp; enc(unescaped path without trailing slash) [&amp; token] &amp; ts)).</summary>
    public static string Sign(string method, string absolutePath, string timestamp, string? accessToken = null)
    {
        var parts = new List<string> { method.ToUpperInvariant(), Encode(Uri.UnescapeDataString(absolutePath).TrimEnd('/')) };
        if (!string.IsNullOrEmpty(accessToken)) parts.Add(accessToken);
        parts.Add(timestamp);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(Secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(string.Join("&", parts))));
    }
    public static string Encode(string value)
    {
        var text = new StringBuilder(value.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(value))
            if (b < 0x80 && Unreserved.IndexOf((char)b) >= 0) text.Append((char)b);
            else text.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        return text.ToString();
    }
    public static Uri BuildUri(string relativePath, IEnumerable<KeyValuePair<string, string>> query, DateTimeOffset now)
    {
        var uri = new Uri(BaseUrl + relativePath);
        var ts = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var all = query.Concat(new KeyValuePair<string, string>[] { new("apikey", ApiKey), new("_sig", Sign("GET", uri.AbsolutePath, ts)), new("_ts", ts), new("os_rom", "android") });
        return new Uri(uri.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", all.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
    }

    private static JsonElement Get(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? value : default;
    private static string? Text(JsonElement obj, string key)
    {
        var value = Get(obj, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    }
    private static int? Int(JsonElement obj, string key) => Get(obj, key) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var v) ? v : null;

    public static IReadOnlyList<DoubanHotItem> ParseCollection(byte[] json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var items = Get(doc.RootElement, "subject_collection_items");
        if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Douban collection has no item list.");
        var result = new List<DoubanHotItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            var id = Text(item, "id")?.Trim();
            var title = Text(item, "title")?.Trim();
            if (!DoubanSubjectParser.IsSubjectId(id) || string.IsNullOrEmpty(title) || !seen.Add(id!)) continue;
            var type = (Text(item, "type") ?? "").Trim().ToLowerInvariant();
            if (type.Length == 0) type = "movie";
            if (type is not ("movie" or "tv")) continue;
            var rating = Get(item, "rating");
            var value = Get(rating, "value");
            float? score = value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var r) && float.IsFinite(r) && r > 0 && r <= 10 ? r : null;
            var cover = Get(item, "cover");
            result.Add(new DoubanHotItem(id!, type, title!, Blank(Text(item, "original_title")), Blank(Text(item, "card_subtitle")), Blank(Text(item, "info")),
                DoubanSubjectParser.ImageUrl(Text(cover, "url")), Int(cover, "width"), Int(cover, "height"),
                DoubanSubjectParser.ImageUrl(Text(Get(item, "pic"), "large") ?? Text(Get(item, "pic"), "normal")), score, Blank(Text(item, "year")), Blank(Text(item, "release_date"))));
        }
        return result;
    }
    public static IReadOnlyList<DoubanTrailerInfo> ParseTrailers(byte[] json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var items = Get(doc.RootElement, "trailers");
        if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Douban trailer response has no list.");
        return items.EnumerateArray().Select(t => (t, url: VideoUrl(Text(t, "video_url")))).Where(x => x.url != null)
            .Select(x => new DoubanTrailerInfo(Blank(Text(x.t, "id")), Blank(Text(x.t, "title")), x.url!, Blank(Text(x.t, "runtime")))).ToArray();
    }
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static string? VideoUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host.EndsWith(".doubanio.com", StringComparison.OrdinalIgnoreCase) ? uri.AbsoluteUri : null;

    /// <summary>card_subtitle "2026 / 韩国 / 剧情 爱情 / 导演 / 演员": the genre segment follows year and region.</summary>
    public static List<string> Genres(string? cardSubtitle)
    {
        if (string.IsNullOrWhiteSpace(cardSubtitle)) return new();
        var parts = cardSubtitle.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var index = parts.Length > 0 && parts[0].Length == 4 && parts[0].All(c => c is >= '0' and <= '9') ? 2 : 1;
        return index < parts.Length ? parts[index].Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(KnownGenres.Contains).Distinct().ToList() : new();
    }
    // Original genre vocabulary (strings 194908-195196); other tokens in that slot are not genres.
    private static readonly HashSet<string> KnownGenres = new(StringComparer.Ordinal)
    {
        "动作", "冒险", "动画", "喜剧", "犯罪", "纪录片", "剧情", "家庭", "奇幻", "历史", "恐怖", "音乐", "悬疑", "爱情", "科幻", "电视电影", "惊悚",
        "战争", "西部", "古装", "武侠", "灾难", "传记", "运动", "歌舞", "儿童", "真人秀", "同性", "情色", "短片", "黑色电影", "新闻", "肥皂剧", "脱口秀", "动作冒险"
    };
    public static DateTimeOffset? PremiereDate(string? year, string? releaseDate)
    {
        if (!int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out var y) || y < 1880 || y > 2200) return null;
        if (releaseDate == null || !DateTime.TryParseExact(y.ToString(CultureInfo.InvariantCulture) + "." + releaseDate, "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;
        return new DateTimeOffset(date, TimeSpan.Zero);
    }
    public static long? RuntimeTicks(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime)) return null;
        var parts = runtime.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return null;
        var seconds = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).Aggregate(0L, (total, part) => total * 60 + part);
        return seconds > 0 ? TimeSpan.FromSeconds(seconds).Ticks : null;
    }
}

public sealed class DoubanFrodoClient
{
    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    private readonly Func<DateTimeOffset> clock;
    public DoubanFrodoClient(HttpClientFactory factory, TimeSpan? timeout = null, Func<DateTimeOffset>? clock = null)
    { http = factory.Client; this.timeout = timeout ?? TimeSpan.FromSeconds(20); this.clock = clock ?? (() => DateTimeOffset.UtcNow); }

    public async Task<IReadOnlyList<DoubanHotItem>> GetCollectionAsync(string collection, int count, CancellationToken token) =>
        DoubanFrodo.ParseCollection(await GetAsync("/subject_collection/" + collection + "/items",
            new KeyValuePair<string, string>[] { new("start", "0"), new("count", count.ToString(CultureInfo.InvariantCulture)) }, 4 * 1024 * 1024, token).ConfigureAwait(false));
    public async Task<IReadOnlyList<DoubanTrailerInfo>> GetTrailersAsync(string type, string id, CancellationToken token)
    {
        if (type is not ("movie" or "tv") || !DoubanSubjectParser.IsSubjectId(id)) throw new ArgumentException("Invalid Douban subject.");
        return DoubanFrodo.ParseTrailers(await GetAsync("/" + type + "/" + id + "/trailers", Array.Empty<KeyValuePair<string, string>>(), 2 * 1024 * 1024, token).ConfigureAwait(false));
    }
    public async Task<(byte[] Data, string Extension)> GetImageAsync(string url, CancellationToken token)
    {
        var safe = DoubanSubjectParser.ImageUrl(url) ?? throw new InvalidDataException("Invalid Douban image origin.");
        // Original swaps .webp poster renditions for .jpg; doubanio serves both from the same path.
        if (safe.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) safe = safe[..^5] + ".jpg";
        var data = await DownloadAsync(new Uri(safe), 8 * 1024 * 1024, "image/jpeg,image/png,image/webp", token).ConfigureAwait(false);
        var extension = data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff ? ".jpg" :
            data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? ".png" :
            data.Length >= 12 && Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && Encoding.ASCII.GetString(data, 8, 4) == "WEBP" ? ".webp" : null;
        return extension == null ? throw new InvalidDataException("Invalid Douban image bytes.") : (data, extension);
    }
    private Task<byte[]> GetAsync(string path, IEnumerable<KeyValuePair<string, string>> query, int maximum, CancellationToken token) =>
        DownloadAsync(DoubanFrodo.BuildUri(path, query, clock()), maximum, "application/json", token);
    private async Task<byte[]> DownloadAsync(Uri uri, int maximum, string accept, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", DoubanFrodo.UserAgent);
        request.Headers.Referrer = new Uri("https://movie.douban.com/");
        request.Headers.Accept.ParseAdd(accept);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Douban returned HTTP " + (int)response.StatusCode + ".");
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Douban response exceeds size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return await DoubanCache.ReadBoundedAsync(stream, maximum, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Douban request timed out."); }
    }
}
