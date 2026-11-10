using System.Text;
using ToolGood.Words;

namespace StrmAssistantRebuild.Common;

/// <summary>Shared dictionary-backed transliteration; sorting and search normalization are separate policies.</summary>
public static class PinyinUtility
{
    /// <summary>
    /// Source-compatible sort title: one initial per pinyin syllable; other text is preserved.
    /// Does not trim, change Latin case, simplify Chinese, or guess whether a title is a person's name.
    /// </summary>
    public static string GenerateSortName(string? text, bool isPersonName = false) =>
        Initials(GetFullPinyinTokens(text, isPersonName));

    /// <summary>
    /// Toneless, phrase-aware syllables in input order, including literal Latin/digit/punctuation tokens.
    /// Name mode uses the package's surname dictionary. Valid surrogate pairs remain a single token.
    /// </summary>
    public static string[] GetFullPinyinTokens(string? text, bool isPersonName = false)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        IEnumerable<string> source = isPersonName
            ? WordsHelper.GetPinyinListForName(text, false)
            : WordsHelper.GetPinyinList(text, false);
        var tokens = new List<string>();
        foreach (var token in source)
        {
            // ToolGood returns unmapped supplementary characters as two UTF-16 tokens.
            if (token.Length == 1 && char.IsLowSurrogate(token[0]) && tokens.Count > 0 &&
                tokens[^1].Length == 1 && char.IsHighSurrogate(tokens[^1][0]))
                tokens[^1] += token;
            else if (token.Length > 0)
                tokens.Add(token);
        }
        return tokens.ToArray();
    }

    public static string ToSimplified(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : WordsHelper.ToSimplifiedChinese(text, 0);

    public static string ToTraditional(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : WordsHelper.ToTraditionalChinese(text, 0);

    /// <summary>
    /// Whole-title search variants, not a word-segmentation index: original/simplified/traditional,
    /// full pinyin and initials, plus punctuation/whitespace-free forms. Ordinal lowercase, deduplicated.
    /// Unlike sort titles, search simplifies before phrase/surname lookup (重慶 -> ChongQing).
    /// </summary>
    public static string[] GetSearchTokens(string? text, bool isPersonName = false) =>
        GetSearchTokens(text, isPersonName, includeChineseVariants: true);

    // Search-index policy only. Keep the public/default helper's token order and dictionary behavior unchanged.
    internal static string[] GetSearchTokens(string? text, bool isPersonName, bool includeChineseVariants)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var original = text.Trim();
        var simplified = ToSimplified(original);
        var syllables = GetFullPinyinTokens(simplified, isPersonName);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var literals = includeChineseVariants ? new[] { original, simplified, ToTraditional(simplified) } : new[] { original };
        foreach (var value in literals.Concat(new[] { string.Concat(syllables), Initials(syllables) }))
        {
            var lower = value.ToLowerInvariant();
            var compact = new StringBuilder(lower.Length);
            foreach (var rune in lower.EnumerateRunes())
                if (Rune.IsLetterOrDigit(rune)) compact.Append(rune.ToString());
            if (compact.Length == 0) continue;
            if (seen.Add(lower)) result.Add(lower);
            if (seen.Add(compact.ToString())) result.Add(compact.ToString());
        }
        return result.ToArray();
    }

    private static string Initials(IEnumerable<string> tokens)
    {
        var result = new StringBuilder();
        foreach (var token in tokens)
        {
            if (token[0] > '\u0080') result.Append(token);
            else result.Append(token[0]);
        }
        return result.ToString();
    }
}
