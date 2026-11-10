using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace StrmAssistantRebuild.Provider;

/// <summary>Pure candidate policy; calling it does not alter the host image picker or refresh pipeline.</summary>
public static class OriginalPosterPolicy
{
    /// <summary>A script hint, not provider original_language. Ambiguous Han/Latin and mixed scripts stay unknown.</summary>
    public static string? OriginalLanguageHint(string? originalTitle)
    {
        if (string.IsNullOrWhiteSpace(originalTitle)) return null;
        var kana = originalTitle.Any(c => c is >= '\u3040' and <= '\u30ff' && c != '\u30fb');
        var hangul = originalTitle.Any(c => c is >= '\uac00' and <= '\ud7a3');
        return kana == hangul ? null : kana ? "ja" : "ko";
    }

    /// <summary>Original poster title policy, not factual language detection; keep the missing-image hint above conservative.</summary>
    internal static string? TitleFallbackLanguage(string? originalTitle)
    {
        if (string.IsNullOrEmpty(originalTitle)) return null;
        var hangul = false; var han = false;
        foreach (var c in originalTitle)
        {
            if (c is >= '\u3040' and <= '\u30ff' && c != '\u30fb') return "ja";
            hangul |= c is >= '\uac00' and <= '\ud7a3';
            han |= c is >= '\u4e00' and <= '\u9fff';
        }
        return hangul ? "ko" : han ? "zh" : "en";
    }

    public static string NormalizeLanguage(string? language)
    {
        var value = (language ?? "").Trim().ToLowerInvariant();
        return value switch { "und" or "unk" or "mul" or "zxx" or "unknown" => "", "jpn" => "ja", "kor" => "ko", "eng" => "en", "zho" or "chi" => "zh", "zhtw" => "zh-tw", _ => value };
    }

    // Source: preferred-image exact/family, original exact/family, other. No independent size/provider tie-break:
    // GetAvailableRemoteImages already orders ties by provider, rating, pixel area and votes.
    public static int Priority(RemoteImageInfo image, string? originalLanguage, string? preferredImageLanguage = null)
    {
        var language = NormalizeLanguage(image.Language);
        var original = NormalizeLanguage(originalLanguage);
        var preferred = NormalizeLanguage(preferredImageLanguage);
        if (language.Length == 0) return 4;
        if (preferred.Length != 0 && language == preferred) return 0;
        if (preferred.Length != 0 && language.Split('-')[0] == preferred.Split('-')[0]) return 1;
        if (original.Length != 0 && language == original) return 2;
        if (original.Length != 0 && language.Split('-')[0] == original.Split('-')[0]) return 3;
        return 4;
    }

    public static IEnumerable<RemoteImageInfo> Sort(IEnumerable<RemoteImageInfo> images, string? originalLanguage, string? preferredImageLanguage = null) =>
        images.Where(image => image.Type == ImageType.Primary && !string.Equals(image.ProviderName, "Douban", StringComparison.OrdinalIgnoreCase))
            .OrderBy(image => Priority(image, originalLanguage, preferredImageLanguage));
}
