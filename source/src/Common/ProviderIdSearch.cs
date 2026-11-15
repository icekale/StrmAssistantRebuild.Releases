using System.Text.RegularExpressions;

namespace StrmAssistantRebuild.Common;

/// <summary>
/// M168: the original provider-id search syntax (notes/decrypted AAA--------/A----------.cs:5198-5221, applied by the query
/// prefix m_f77fd35e439a3bb4512cf110): "tt…"/"nm…" (IMDb), "tmdb[id]=N", "tvdb[id]=N", "douban[id]=N", "bgm[id]=N[,N]" or
/// "bangumi=N[,N]". A match turns the search into a native provider-id lookup (AnyProviderIdEquals) restricted to Movie, BoxSet,
/// Series, Season and Person. Bare digits (the original "tmdbid" entry, only with DigitsAsTmdbId) keep the rebuild's existing
/// TMDB-id handling (NumericSearchPolicy).
/// </summary>
public static class ProviderIdSearch
{
    public static readonly string[] Types = { "Movie", "BoxSet", "Series", "Season", "Person" };
    private static readonly (string Key, Regex Pattern, int Group)[] Patterns =
    {
        ("imdb", new Regex(@"^(tt|nm)\d{7,8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), 0),
        ("tmdb", new Regex(@"^tmdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), 2),
        ("tvdb", new Regex(@"^tvdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), 2),
        ("douban", new Regex(@"^douban(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), 2),
        ("bangumi", new Regex(@"^(bgm(id)?|bangumi)=(\d+(?:,\d+)*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), 3),
    };

    public static bool TryParse(string? term, out string key, out string value)
    {
        key = value = "";
        var text = term?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 128) return false;
        foreach (var (name, pattern, group) in Patterns)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;
            key = name; value = match.Groups[group].Value;
            return value.Length > 0;
        }
        return false;
    }

    /// <summary>Original intersection: an empty type list stays empty (no type filter), otherwise only the five types remain.</summary>
    public static string[] RestrictTypes(string[] types) => types.Intersect(Types, StringComparer.Ordinal).ToArray();
}
