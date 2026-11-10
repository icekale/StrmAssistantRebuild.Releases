using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

/// <summary>One exact-token scope for backend fields and Web asset selection.</summary>
public static class DoubanScope
{
    public static HashSet<string> Parse(string? value) => string.IsNullOrEmpty(value)
        ? Enum.GetNames<MetadataEnhanceOptions.DoubanAssistScrapeOption>().ToHashSet(StringComparer.OrdinalIgnoreCase)
        : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
