using System.Globalization;

namespace StrmAssistantRebuild.Common;

/// <summary>Rebuild-owned UI text without a resource key: English, Simplified or Traditional by the current UI culture.
/// (Resources.Culture is never assigned by this plugin, so the request UI culture is authoritative.)</summary>
public static class UiLanguage
{
    public static string Text(string english, string simplified, string traditional)
    {
        var name = CultureInfo.CurrentUICulture.Name;
        if (!name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return english;
        return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) || name.EndsWith("TW", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("HK", StringComparison.OrdinalIgnoreCase) || name.EndsWith("MO", StringComparison.OrdinalIgnoreCase) ? traditional : simplified;
    }
    public static bool IsChinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
}
