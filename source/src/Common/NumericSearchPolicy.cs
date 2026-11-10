using System.Globalization;
using MediaBrowser.Controller.Entities;

namespace StrmAssistantRebuild.Common;

/// <summary>Conservative reconstruction, not the original encrypted regex. IDs remain strings, never ItemIds.</summary>
internal static class NumericSearchPolicy
{
    internal const int MaxIdLength = 64;
    internal const int CandidateBudget = 20_000;

    internal static string? Id(string? term)
    {
        var value = term?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > MaxIdLength || value.Any(c => c < '0' || c > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) return null;
        return value; // Exact provider value: 00123 is not 123. No normalization through numeric conversion.
    }

    internal static bool Supports(string type) => type is "Movie" or "Series" or "Episode";

    internal static string Tmdb(BaseItem item)
    {
        // A provider key is case-insensitive; neither its value nor another provider namespace is interchangeable.
        var value = item.ProviderIds?.FirstOrDefault(p => string.Equals(p.Key, "Tmdb", StringComparison.OrdinalIgnoreCase)).Value;
        return value is { Length: <= MaxIdLength } ? value : string.Empty;
    }
}
