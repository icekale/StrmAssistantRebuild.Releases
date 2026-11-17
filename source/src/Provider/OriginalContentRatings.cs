using System.Text.Json;

namespace StrmAssistantRebuild.Provider;

/// <summary>
/// M174: the original's US/JP content-rating fallback (series ImportData postfix A:52575-53150, movie ProcessMainInfo postfix
/// A:50639-51245, first language only, metadata cache mode not None). The maps come only from user files in the original
/// override layout (loader AA:12063, path builder A:114637, reader A:114786; no built-in table):
///   {cache root}/override/tmdb-movies2/{us|jp}_content_rating_fallback.json (movies)
///   {cache root}/override/tmdb-tv/{us|jp}_content_rating_fallback.json (series)
/// Each file is a JSON object of string to string; keys are normalized (control characters, whitespace and U+200B-U+200D removed,
/// A:93646) and compared ordinally. The map is chosen by the upper-cased preferred country (US or JP; others have none).
/// Decision: current = OfficialRating trimmed (movies with none take the first certified release's rating, then ""). When the
/// preferred country has a TMDB entry and the current rating is a known parental rating of that country (Emby's US list, or the
/// JP list plus JP-UNRATED, case-insensitive; AA:12137-12153) it is kept; otherwise the mapped value (non-blank, trimmed) wins.
/// An empty rating is finally filled with the current value.
/// Rebuild differences: applied after the host refresh with the native DTO read again (NativeTmdbDetails); fields locked by the
/// user are not changed; files are re-read when they change instead of once when the patch is enabled.
/// </summary>
public sealed class OriginalContentRatings
{
    private const int MaxBytes = 1024 * 1024;
    private readonly Func<string?> root;
    private readonly Func<string, IEnumerable<string>> parentalRatings;
    private readonly object gate = new();
    private readonly Dictionary<string, (long Length, DateTime Written, Dictionary<string, string>? Map)> files = new(StringComparer.Ordinal);
    private long mapped, filled, kept, unreadable;
    public long Mapped => Interlocked.Read(ref mapped);
    public long Filled => Interlocked.Read(ref filled);
    public long Kept => Interlocked.Read(ref kept);
    public long Unreadable => Interlocked.Read(ref unreadable);

    public OriginalContentRatings(Func<string?> root, Func<string, IEnumerable<string>> parentalRatings)
    { this.root = root; this.parentalRatings = parentalRatings; }

    public string? PathFor(bool movie, string country)
    {
        var r = root();
        return string.IsNullOrWhiteSpace(r) ? null : Path.Combine(r, "override", movie ? "tmdb-movies2" : "tmdb-tv", country.ToLowerInvariant() + "_content_rating_fallback.json");
    }

    /// <summary>The map for the preferred country (US/JP only), or null when there is no readable file.</summary>
    public IReadOnlyDictionary<string, string>? Map(bool movie, string? preferredCountry)
    {
        var country = preferredCountry?.ToUpperInvariant();
        if (country is not ("US" or "JP") || PathFor(movie, country) is not { } path) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { lock (gate) files.Remove(path); return null; }
            lock (gate)
                if (files.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc) return cached.Map;
            Dictionary<string, string>? map = null;
            if (info.Length <= MaxBytes)
            {
                try
                {
                    var data = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllBytes(path));
                    if (data != null)
                    {
                        map = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var (key, value) in data) map[Normalize(key)] = value!;
                    }
                }
                catch (Exception) { map = null; } // The original logs at Debug and loads no map.
            }
            if (map == null) Interlocked.Increment(ref unreadable);
            lock (gate) files[path] = (info.Length, info.LastWriteTimeUtc, map);
            return map;
        }
        catch (Exception) { return null; }
    }

    /// <summary>A:93646: drops control characters, whitespace and zero-width spaces/joiners.</summary>
    public static string Normalize(string? text) => string.IsNullOrEmpty(text) ? string.Empty :
        string.Concat(text.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c) && c != '​' && c != '‌' && c != '‍'));

    /// <summary>Native MovieDb 2.0 <c>Country.GetRating</c>: US bare, DE as FSK-, others "{country}-{rating}".</summary>
    public static string? GetRating(string? rating, string? country)
    {
        if (string.IsNullOrEmpty(rating)) return null;
        if (string.Equals(country, "us", StringComparison.OrdinalIgnoreCase)) return rating;
        if (string.Equals(country, "de", StringComparison.OrdinalIgnoreCase)) country = "FSK";
        return country + "-" + rating;
    }

    /// <summary>Emby's parental ratings of the country (JP also JP-UNRATED), case-insensitive.</summary>
    public HashSet<string> Known(string country)
    {
        var names = (parentalRatings(country) ?? Enumerable.Empty<string>()).Where(n => n != null);
        if (country == "JP") names = names.Concat(new[] { "JP-UNRATED" });
        return names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The original decision. <paramref name="official"/> is already trimmed; returns the final OfficialRating.</summary>
    public static string? Decide(string? official, bool movie, string preferredCountry, IReadOnlyList<(string? Country, string? Rating)> entries,
        IReadOnlyDictionary<string, string> map, ISet<string> known, out string outcome)
    {
        var current = official;
        if (movie && current == null && entries.Count > 0) current = GetRating(entries[0].Rating, entries[0].Country);
        current = string.IsNullOrEmpty(current) ? string.Empty : current;
        var preferred = entries.Any(e => string.Equals(e.Country, preferredCountry, StringComparison.OrdinalIgnoreCase));
        if (preferred && known.Contains(current)) outcome = "kept";
        else if (map.TryGetValue(Normalize(current), out var value) && !string.IsNullOrWhiteSpace(value)) { outcome = "mapped"; return value.Trim(); }
        else outcome = "unmapped";
        if (string.IsNullOrEmpty(official) && !string.IsNullOrEmpty(current)) { outcome = "filled"; return current; }
        return official;
    }

    internal void Count(string outcome)
    {
        switch (outcome)
        {
            case "mapped": Interlocked.Increment(ref mapped); break;
            case "filled": Interlocked.Increment(ref filled); break;
            case "kept": Interlocked.Increment(ref kept); break;
        }
    }
}
