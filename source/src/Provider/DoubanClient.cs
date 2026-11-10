using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Provider;

public sealed class DoubanClient
{
    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    private readonly int maxBytes;
    public DoubanClient(HttpClientFactory factory, TimeSpan? timeout = null, int maxBytes = 2 * 1024 * 1024)
    { http = factory.Client; this.timeout = timeout ?? TimeSpan.FromSeconds(20); this.maxBytes = maxBytes; }

    public async Task<DoubanLookup> GetAsync(string? id, string? imdb, MetadataRefreshOptions options, CancellationToken token)
    {
        if (id != null && !DoubanSubjectParser.IsSubjectId(id) || imdb != null && !DoubanSubjectParser.IsImdbId(imdb)) return new(null, "invalid-identity");
        if (id == null && imdb == null) return new(null, "no-identity");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            if (id != null) return await GetSubjectAsync(id, imdb, options, deadline.Token).ConfigureAwait(false);
            // Search yields candidates only. Every candidate must independently expose the exact IMDb field.
            var url = "https://movie.douban.com/subject_search?search_text=" + Uri.EscapeDataString(imdb!) + "&cat=1002";
            var html = Encoding.UTF8.GetString(await DownloadAsync(url, options, maxBytes, deadline.Token).ConfigureAwait(false));
            var ids = SearchCandidates(html);
            if (ids.Length == 0) return new(null, "no-match");
            if (ids.Length > 5) return new(null, "ambiguous-candidates");
            DoubanSubject? matched = null;
            var incomplete = false;
            foreach (var candidate in ids)
            {
                var result = await GetSubjectAsync(candidate, imdb, options, deadline.Token).ConfigureAwait(false);
                if (result.Subject == null) { if (result.Status != "identity-mismatch") incomplete = true; continue; }
                if (result.Subject.ImdbId == null) { incomplete = true; continue; }
                if (result.Subject.ImdbId != imdb) continue;
                if (matched != null) return new(null, "ambiguous-match");
                matched = result.Subject;
            }
            if (incomplete) return new(null, "imdb-resolution-unavailable");
            return matched == null ? new(null, "no-exact-imdb-match") : new(matched, "online");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null, "timeout"); }
        catch (HttpRequestException) { return new(null, "http-error"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or JsonException or RegexMatchTimeoutException) { return new(null, "invalid-response"); }
    }
    private async Task<DoubanLookup> GetSubjectAsync(string id, string? imdb, MetadataRefreshOptions options, CancellationToken token)
    {
        var html = Encoding.UTF8.GetString(await DownloadAsync(DoubanSubjectParser.SubjectUrl(id), options, maxBytes, token).ConfigureAwait(false));
        var result = DoubanSubjectParser.ParseHtml(html, id);
        return result.Subject != null && !DoubanSubjectParser.MatchesIdentity(result.Subject, id, imdb) ? new(null, "identity-mismatch") : result;
    }
    public static string[] SearchCandidates(string html)
    {
        // Public subject_search HTML emits window.__DATA__ = {...}; no title-score matching or first-item shortcut.
        var match = Regex.Match(html, @"window\.__DATA__\s*=\s*", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        if (!match.Success) return Array.Empty<string>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(html.Substring(match.Index + match.Length)), new JsonReaderOptions { MaxDepth = 32 });
        using var doc = JsonDocument.ParseValue(ref reader);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return items.EnumerateArray().Select(item => item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? DoubanSubjectParser.SubjectIdFromUrl(url.GetString()) : null)
            .Where(id => id != null).Cast<string>().Distinct(StringComparer.Ordinal).Take(6).ToArray();
    }
    public async Task<(byte[] Data, string Mime)> GetImageAsync(string url, CancellationToken token)
    {
        if (DoubanSubjectParser.ImageUrl(url) == null) throw new InvalidDataException("Invalid Douban image origin.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        var data = await DownloadAsync(url, null, 8 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
        var mime = data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff ? "image/jpeg" :
            data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) ? "image/png" : null;
        return mime == null ? throw new InvalidDataException("Invalid Douban image bytes.") : (data, mime);
    }
    private async Task<byte[]> DownloadAsync(string url, MetadataRefreshOptions? options, int maximum, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(options == null ? "image/jpeg,image/png" : "text/html,application/xhtml+xml");
        if (options != null)
        {
            if (!string.IsNullOrWhiteSpace(options.DoubanCookie)) request.Headers.Add("Cookie", options.DoubanCookie);
            if (!string.IsNullOrWhiteSpace(options.DoubanAccessToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.DoubanAccessToken);
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var final = response.RequestMessage?.RequestUri;
        if (final != null && (final.Scheme != "https" || final.Host != new Uri(url).Host)) throw new InvalidDataException("Unexpected Douban redirect.");
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Douban response exceeds size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await DoubanCache.ReadBoundedAsync(stream, maximum, token).ConfigureAwait(false);
    }
}
