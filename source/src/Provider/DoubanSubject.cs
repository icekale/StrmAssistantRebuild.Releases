using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StrmAssistantRebuild.Provider;

// Only public subject data is retained. Request cookies/tokens never enter this model.
public sealed record DoubanPerson(string Name, string? Role, string Type, string? ImageUrl);
public sealed record DoubanSubject(string Id, string? ImdbId, string? Title, string? Overview,
    float? Rating, string? ImageUrl, DoubanPerson[] People, string SourceUrl);
public sealed record DoubanLookup(DoubanSubject? Subject, string Status);
public sealed record DoubanApplyResult(bool Changed, string Status, string? SubjectId = null)
{
    // Applied fields may coexist with a failed poster/cache write; callers must not count partial success as clean success.
    public bool Failed => Status.Split(':').Any(part => part is "http-error" or "timeout" or "invalid-response" or "identity-mismatch" or
        "invalid-identity" or "cache-path-error" or "write-error" or "imdb-resolution-unavailable" or "invalid-cache-mode" or
        "poster-failed" or "poster-not-supported" or "cache-write-failed");
}
public sealed record DoubanBatchResult(int Matched, int Changed, int Skipped, int Failed);

public static class DoubanSubjectParser
{
    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;
    private static Match Match(string value, string pattern) => Regex.Match(value, pattern, Rx, TimeSpan.FromSeconds(1));
    private static MatchCollection Matches(string value, string pattern) => Regex.Matches(value, pattern, Rx, TimeSpan.FromSeconds(1));
    public static bool IsSubjectId(string? id) => id != null && Regex.IsMatch(id, "^[1-9][0-9]{0,11}$", RegexOptions.CultureInvariant);
    public static bool IsImdbId(string? id) => id != null && Regex.IsMatch(id, "^tt[0-9]{7,10}$", RegexOptions.CultureInvariant);
    public static string SubjectUrl(string id) => "https://movie.douban.com/subject/" + id + "/";
    public static string? SubjectIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(new Uri("https://movie.douban.com"), url, out var uri) ||
            uri.Scheme != "https" || uri.Host != "movie.douban.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return null;
        var m = Match(uri.AbsolutePath, "^/subject/([1-9][0-9]{0,11})/?$");
        return m.Success ? m.Groups[1].Value : null;
    }
    public static string? ImageUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
            !(uri.Host.EndsWith(".doubanio.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "img.douban.com")) return null;
        return uri.AbsoluteUri;
    }
    private static JsonElement Get(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var result) ? result : default;
    private static string? Text(JsonElement obj, string key)
    {
        var value = Get(obj, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
    }
    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", "", Rx, TimeSpan.FromSeconds(1))).Trim();
    private static float? Rating(JsonElement obj, string valueKey, string maxKey)
    {
        if (!float.TryParse(Text(obj, valueKey), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value) || value < 0) return null;
        var maximum = Text(obj, maxKey);
        var max = 10f;
        if (maximum != null && (!float.TryParse(maximum, NumberStyles.Float, CultureInfo.InvariantCulture, out max) || !float.IsFinite(max) || max <= 0)) return null;
        return value > max ? null : value * 10 / max;
    }
    private static IEnumerable<JsonElement> Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static string? Attribute(string attributes, string name)
    {
        var m = Match(attributes, "(?:^|\\s)" + Regex.Escape(name) + "\\s*=\\s*([\"'])(.*?)\\1");
        return m.Success ? WebUtility.HtmlDecode(m.Groups[2].Value) : null;
    }
    public static DoubanLookup ParseHtml(string html, string expectedId)
    {
        if (!IsSubjectId(expectedId)) return new(null, "invalid-identity");
        try
        {
            // JSON-LD identifies the subject itself, unlike related-subject links elsewhere on a page.
            var subjectNodes = new List<JsonElement>();
            foreach (Match script in Matches(html, "<script\\b([^>]*)>(.*?)</script\\s*>"))
            {
                if (!string.Equals(Attribute(script.Groups[1].Value, "type"), "application/ld+json", StringComparison.OrdinalIgnoreCase)) continue;
                using var json = JsonDocument.Parse(script.Groups[2].Value, new JsonDocumentOptions { MaxDepth = 32 });
                var nodes = json.RootElement.ValueKind == JsonValueKind.Array ? Array(json.RootElement) : new[] { json.RootElement };
                foreach (var node in nodes)
                    if (Text(node, "@type") is "Movie" or "TVSeries" or "TVSeason") subjectNodes.Add(node.Clone());
            }
            if (subjectNodes.Count != 1) return new(null, "invalid-response");
            var root = subjectNodes[0];
            if (SubjectIdFromUrl(Text(root, "url")) != expectedId) return new(null, "identity-mismatch");
            foreach (Match link in Matches(html, "<link\\b([^>]*)>"))
                if (Attribute(link.Groups[1].Value, "rel") == "canonical" && SubjectIdFromUrl(Attribute(link.Groups[1].Value, "href")) != expectedId)
                    return new(null, "identity-mismatch");
            foreach (Match meta in Matches(html, "<meta\\b([^>]*)>"))
                if (Attribute(meta.Groups[1].Value, "property") == "og:url" && SubjectIdFromUrl(Attribute(meta.Groups[1].Value, "content")) != expectedId)
                    return new(null, "identity-mismatch");
            // Read only the labelled IMDb field, never an IMDb mention in reviews or related links.
            var info = Match(html, "<div\\b[^>]*id\\s*=\\s*['\"]info['\"][^>]*>(.*?)</div\\s*>");
            var imdbMatch = Match(info.Success ? info.Groups[1].Value : "", "<span\\b[^>]*>\\s*IMDb\\s*:?\\s*</span>\\s*:?\\s*(?:<a\\b[^>]*>\\s*)?(tt[0-9]{7,10})(?![0-9])");
            var imdb = imdbMatch.Success ? imdbMatch.Groups[1].Value : null;
            var overview = Clean(Text(root, "description"));
            var summary = Match(html, "<span\\b[^>]*property\\s*=\\s*['\"]v:summary['\"][^>]*>(.*?)</span\\s*>");
            if (summary.Success) overview = Clean(summary.Groups[1].Value);
            var people = Array(Get(root, "actor")).Select(p => new DoubanPerson(Clean(Text(p, "name")) ?? "", null, "Actor", ImageUrl(Text(p, "image"))))
                .Concat(Array(Get(root, "director")).Select(p => new DoubanPerson(Clean(Text(p, "name")) ?? "", null, "Director", ImageUrl(Text(p, "image")))))
                .Where(p => p.Name.Length > 0).DistinctBy(p => (p.Name, p.Type)).Take(200).ToArray();
            foreach (Match entry in Matches(html, "<li\\b[^>]*class\\s*=\\s*['\"][^'\"]*\\bcelebrity\\b[^'\"]*['\"][^>]*>(.*?)</li\\s*>"))
            {
                var block = entry.Groups[1].Value;
                var name = Match(block, "<span\\b[^>]*class\\s*=\\s*['\"]name['\"][^>]*>(.*?)</span>");
                var role = Match(block, "<span\\b[^>]*class\\s*=\\s*['\"]role['\"][^>]*>(.*?)</span>");
                var cleanName = Clean(name.Groups[1].Value);
                var cleanRole = Clean(role.Groups[1].Value);
                var index = System.Array.FindIndex(people, p => p.Name == cleanName && p.Type == "Actor");
                // Occupation labels like 演员/导演 are not characters. Only an explicit 饰 prefix is admitted.
                if (index >= 0 && cleanRole?.StartsWith("饰 ", StringComparison.Ordinal) == true)
                    people[index] = people[index] with { Role = cleanRole.Substring(2).Trim() };
            }
            return new(new(expectedId, imdb, Clean(Text(root, "name")), overview, Rating(Get(root, "aggregateRating"), "ratingValue", "bestRating"),
                ImageUrl(Text(root, "image")), people, SubjectUrl(expectedId)), "online");
        }
        catch (Exception ex) when (ex is JsonException or RegexMatchTimeoutException or InvalidOperationException) { return new(null, "invalid-response"); }
    }
    public static DoubanLookup ParseDetailJson(string json, string? expectedId = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            var id = Text(root, "id");
            if (!IsSubjectId(id)) return new(null, "invalid-response");
            if (expectedId != null && id != expectedId) return new(null, "identity-mismatch");
            var url = Text(root, "url");
            if (url != null && SubjectIdFromUrl(url) != id) return new(null, "identity-mismatch");
            IEnumerable<DoubanPerson> People(string key, string type) => Array(Get(root, key)).Select(p =>
            {
                // roles describe occupations; they are not character names. Only character maps to Role.
                return new DoubanPerson(Clean(Text(p, "name")) ?? "", Clean(Text(p, "character")), type, ImageUrl(Text(Get(p, "avatar"), "large") ?? Text(Get(p, "avatar"), "normal")));
            }).Where(p => p.Name.Length > 0);
            return new(new(id!, null, Clean(Text(root, "title")), Clean(Text(root, "intro")), Rating(Get(root, "rating"), "value", "max"),
                ImageUrl(Text(Get(root, "pic"), "large") ?? Text(Get(root, "pic"), "normal")),
                People("actors", "Actor").Concat(People("directors", "Director")).DistinctBy(p => (p.Name, p.Type)).Take(200).ToArray(), SubjectUrl(id!)), "override-cache");
        }
        catch (Exception ex) when (ex is JsonException or RegexMatchTimeoutException or InvalidOperationException) { return new(null, "invalid-response"); }
    }
    public static bool MatchesIdentity(DoubanSubject subject, string? id, string? imdb) => IsSubjectId(subject.Id) &&
        (id == null || id == subject.Id) && (imdb == null || imdb == subject.ImdbId || id != null && subject.ImdbId == null) && SubjectIdFromUrl(subject.SourceUrl) == subject.Id;
}
