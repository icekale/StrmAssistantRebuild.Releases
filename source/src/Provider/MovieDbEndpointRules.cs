using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Provider;

/// <summary>Pure native TMDB endpoint validation; no host, transport or detour dependency.</summary>
public static class MovieDbEndpointRules
{
    internal sealed record RouteSettings(string? Api, string? Image, string Key);
    /// <summary>Pure validation, no mutation, provider calls or secret-bearing diagnostics. OFF ignores dormant values.</summary>
    public static bool ValidateOptions(MetadataEnhanceOptions options, out string detail) => Parse(options, out _, out detail);

    internal static bool Parse(MetadataEnhanceOptions options, out RouteSettings settings, out string detail)
    {
        settings = new(null, null, "");
        detail = "Valid native TMDB routing options.";
        if (!options.AltMovieDbConfig) return true;
        if (!Root(options.AltMovieDbApiUrl, "/3", out var api))
        { detail = "AltMovieDbApiUrl requires an absolute HTTP(S) root without credentials, query, fragment or ambiguous path."; return false; }
        if (!Root(options.AltMovieDbImageUrl, "/t/p", out var image))
        { detail = "AltMovieDbImageUrl requires an absolute HTTP(S) root without credentials, query, fragment or ambiguous path."; return false; }
        var key = (options.AltMovieDbApiKey ?? "").Trim();
        // Original validator is a regex whose plaintext is unknown. Bound and encode rather than invent its grammar.
        if (key.Length > 512 || key.Any(char.IsControl))
        { detail = "AltMovieDbApiKey exceeds the bounded query-value format."; return false; }
        settings = new(api, image, key);
        return true;
    }

    private static bool Root(string? value, string nativePath, out string? root)
    {
        root = null;
        value = (value ?? "").Trim().TrimEnd('/');
        if (value.Length == 0) return true;
        if (value.Any(char.IsWhiteSpace) || value.Contains('\\') || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 || uri.Port is < 1 or > 65535 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !CleanPath(value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..])) return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        // Accept either an origin/prefix or an explicitly versioned image/API root, without /3/3 or /t/p/t/p.
        if (path.EndsWith(nativePath, StringComparison.Ordinal)) path = path[..^nativePath.Length];
        root = uri.GetLeftPart(UriPartial.Authority) + path;
        return true;
    }

    internal static bool CleanPath(string text)
    {
        foreach (var part in text.Split('/'))
        {
            var decoded = Uri.UnescapeDataString(part);
            if (decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\') || decoded.Any(char.IsControl)) return false;
        }
        return true;
    }

}
