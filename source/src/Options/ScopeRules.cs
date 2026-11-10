using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.Options;

/// <summary>Shared UI/consumer rules for OSS-compatible scope tokens; no host state or persistence.</summary>
public static class ScopeRules
{
    public const string Favorites = "-1";
    public const string None = "__none__";
    public static HashSet<string> Parse(string? scope) => (scope ?? "").Split(new[] { ',', ';' },
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public static void ValidateMarkerScope(string? scope)
    {
        var selected = Parse(scope);
        if (selected.Contains(Favorites) && selected.Count != 1)
            throw new ArgumentException(Resources.InvalidMarkerEnabledLibraryScope, nameof(scope));
    }
}
