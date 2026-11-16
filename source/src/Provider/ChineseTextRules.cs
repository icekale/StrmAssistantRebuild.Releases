using System.Net;
using System.Text.RegularExpressions;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Provider;

/// <summary>
/// M170: the original ChineseMovieDb text rules, decoded from notes/decrypted/A----------/A----------.cs.
/// Han = [一-鿿] (92834), kana = [぀-ヿ] without "・" (92843), placeholders 93827/93836,
/// fallback order 93541-93568, overview/tagline rule 49961, name rule 50050, episode name rule 50158,
/// conversion 93062/93177, BlockNonFallback 93613, overview normalization 50304 (case 26), genre map 52575.
/// Pure functions only; the post-refresh service decides which item fields are current.
/// </summary>
public static class ChineseTextRules
{
    public static readonly string[] SimplifiedOrder = { "zh-CN", "zh-SG", "zh-HK", "zh-TW", "ja-JP" };
    public static readonly string[] TraditionalOrder = { "zh-TW", "zh-HK", "zh-CN", "zh-SG", "ja-JP" };
    static readonly Regex ZhPlaceholder = new(@"^第\s*\d+\s*(集|話)$", RegexOptions.CultureInvariant);
    static readonly Regex JaPlaceholder = new(@"^(第\s*\d+\s*話|エピソード\s*\d+)$", RegexOptions.CultureInvariant);

    public static bool Han(string? text) => text != null && text.Any(c => c is >= '\u4e00' and <= '\u9fff');
    public static bool Kana(string? text) => text != null && text.Any(c => c is >= '\u3040' and <= '\u30ff' && c != '\u30fb');
    public static bool HanOrKana(string? text) => Han(text) || Kana(text);
    public static bool ZhEpisodePlaceholder(string? text) => text != null && ZhPlaceholder.IsMatch(text);
    public static bool JaEpisodePlaceholder(string? text) => text != null && JaPlaceholder.IsMatch(text);
    /// <summary>Chinese (or, with ja-JP selected, Japanese) script as the original's completeness checks use it.</summary>
    public static bool Localized(string? text, bool japaneseFallback) => japaneseFallback ? HanOrKana(text) : Han(text);

    /// <summary>The original checks whether ja-JP is among the selected fallback languages.</summary>
    public static bool JapaneseFallback(MetadataEnhanceOptions settings) => Selected(settings).Contains("ja-JP", StringComparer.OrdinalIgnoreCase);

    /// <summary>Fixed order filtered by the selection; the selection's own order is ignored, as in the original.</summary>
    public static string[] FallbackOrder(MetadataEnhanceOptions settings)
    {
        var selected = Selected(settings);
        return (settings.PreferTraditionalChinese ? TraditionalOrder : SimplifiedOrder).Where(l => selected.Contains(l, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
    static string[] Selected(MetadataEnhanceOptions settings) => MetadataLanguageService.ParseLanguages(settings.FallbackLanguages);

    static bool JapanesePath(bool japaneseFallback, string? originalLanguage) =>
        japaneseFallback && (string.IsNullOrEmpty(originalLanguage) || originalLanguage.Equals("ja", StringComparison.OrdinalIgnoreCase));

    /// <summary>Overview and tagline (49961); also season names and collection text, with no original language.</summary>
    public static bool ReplaceText(string? current, string? candidate, string? originalLanguage, bool japaneseFallback)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(current)) return true;
        return JapanesePath(japaneseFallback, originalLanguage)
            ? !HanOrKana(current) && HanOrKana(candidate)
            : !Han(current) && Han(candidate);
    }

    /// <summary>Movie and series names (50050).</summary>
    public static bool ReplaceName(string? current, string? candidate, string? originalTitle, string? originalLanguage, bool japaneseFallback)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(current)) return true;
        if (!JapanesePath(japaneseFallback, originalLanguage)) return !Han(current) && Han(candidate);
        if (HanOrKana(current)) return false;
        if (Han(candidate)) return true;
        if (!Kana(candidate)) return false;
        return string.IsNullOrEmpty(originalTitle) || string.Equals(current, originalTitle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Episode names (50158): only an empty name or a 第N集/第N話 placeholder is replaced.</summary>
    public static bool ReplaceEpisodeName(string? current, string? candidate, bool japaneseFallback)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(current)) return true;
        if (!ZhEpisodePlaceholder(current)) return false;
        if (Han(candidate) && !ZhEpisodePlaceholder(candidate)) return true;
        return japaneseFallback && Kana(candidate) && !JaEpisodePlaceholder(candidate);
    }

    /// <summary>
    /// Conversion of accepted text (93062/93177). Japanese requests are left alone. PreferTraditionalChinese off: simplified
    /// with the fetched language's region, then 妳→你. On: simplified with the fetched region, then traditional in the region of
    /// the requested (target) metadata language.
    /// </summary>
    public static string Convert(string text, string? fetchedLanguage, string? targetLanguage, bool preferTraditional)
    {
        var fetched = (fetchedLanguage ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text) || fetched is "ja-jp" or "jpn") return text;
        if (!preferTraditional) return PinyinUtility.ToSimplifiedForSearch(text, fetchedLanguage);
        return PinyinUtility.ToTraditional(PinyinUtility.ToSimplified(text, PinyinUtility.SearchRegion(fetchedLanguage)), PinyinUtility.SearchRegion(targetLanguage));
    }

    /// <summary>BlockNonFallbackLanguage (93613): clear a nonempty overview with no Han, keeping kana when ja-JP is selected.</summary>
    public static bool BlockOverview(string? overview, bool japaneseFallback) =>
        !string.IsNullOrEmpty(overview) && !Han(overview) && (!japaneseFallback || !Kana(overview));

    /// <summary>Movie/series overview normalization: HTML decode, then the literal two-character sequence \n\n becomes \n.</summary>
    public static string NormalizeOverview(string text) => WebUtility.HtmlDecode(text).Replace("\\n\\n", "\\n");

    /// <summary>Series ImportData genre names (52575), case-insensitive.</summary>
    public static string MapSeriesGenre(string genre) =>
        string.Equals(genre, "Sci-Fi & Fantasy", StringComparison.OrdinalIgnoreCase) ? "科幻奇幻" :
        string.Equals(genre, "War & Politics", StringComparison.OrdinalIgnoreCase) ? "战争政治" : genre;

    /// <summary>
    /// Rebuild adaptation: the original reads TMDB original_language (movie) or languages[0] (series). The public item exposes only
    /// OriginalTitle, so a kana title means "ja", a Hangul title "ko", other Latin/script text a non-Japanese language, and an empty or
    /// Han-only title stays unknown (the original's empty-language path). For Han-only titles the two paths decide the same way.
    /// </summary>
    public static string? RuleLanguage(string? originalTitle)
    {
        if (string.IsNullOrWhiteSpace(originalTitle)) return null;
        if (Kana(originalTitle)) return "ja";
        if (originalTitle.Any(c => c is >= '\uac00' and <= '\ud7a3')) return "ko";
        if (Han(originalTitle)) return null;
        return originalTitle.Any(char.IsLetter) ? "other" : null;
    }
}
