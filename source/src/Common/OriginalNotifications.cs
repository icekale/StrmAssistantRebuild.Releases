using System.Globalization;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.Common;

/// <summary>
/// M164: the original plugin's notification identity and text (notes/decrypted/StrmAssistant.Notification/CustomNotifications.cs
/// and senders in A----------/AA---------.cs:47280-47760, 48868-49060). Event ids and category are the original ones, so
/// notification subscriptions made for the original plugin keep working; texts come from the original resource strings.
/// </summary>
public static class OriginalNotifications
{
    public const string CategoryId = "strm.assistant";
    public const string Favorites = "favorites.update", IntroSkip = "introskip.update", Metadata = "metadata.update", Image = "image.update", DeepDelete = "deep.delete";

    public static CultureInfo Culture(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return CultureInfo.CurrentUICulture;
        try { return CultureInfo.GetCultureInfo(language); } catch (CultureNotFoundException) { return CultureInfo.CurrentUICulture; }
    }
    private static string Text(string name, CultureInfo culture) => Resources.ResourceManager.GetString(name, culture) ?? name;
    private static string Lines(string value) => value.Replace("\\n", Environment.NewLine);

    /// <summary>Original category name: the plugin title.</summary>
    public static string PluginName(string? language) => Text("PluginOptions_EditorTitle_Strm_Assistant", Culture(language));
    public static string EventName(string eventId, string? language) => Text(eventId switch
    {
        Favorites => "Notification_CatchupUpdate_EventName",
        IntroSkip => "Notification_IntroSkipUpdate_EventName",
        Metadata => "Notification_MetadataUpdate_EventName",
        Image => "Notification_ImageUpdate_EventName",
        DeepDelete => "Notification_DeepDelete_EventName",
        _ => throw new ArgumentOutOfRangeException(nameof(eventId))
    }, Culture(language));

    /// <summary>Favorites and marker notifications use the plugin title alone; the others "plugin - event".</summary>
    public static string Title(string eventId, string? language) =>
        eventId is Favorites or IntroSkip ? PluginName(language) : PluginName(language) + " - " + EventName(eventId, language);

    public static string FavoritesDescription(string? path, string? userName, string? language) =>
        string.Format(Culture(language), Lines(Text("Notification_CatchupUpdate_EventDescription", Culture(language))), path, userName);
    public static string ImageDescription(object imageType, string? language) =>
        string.Format(Culture(language), Text("Notification_ImageUpdate_EventDescription", Culture(language)), imageType);
    public static string MetadataDescription(string? name, long internalId, IEnumerable<string> fields) =>
        $"{name} ({internalId}){Environment.NewLine}{Environment.NewLine}Changed Fields:{Environment.NewLine}{string.Join(Environment.NewLine, fields)}";
    public static string DeepDeleteDescription(string? name, string? path, IEnumerable<string> mountPaths, string? language) =>
        string.Format(Culture(language), Lines(Text("Notification_DeepDelete_EventDescription", Culture(language))), name, path, string.Join(Environment.NewLine, mountPaths));

    /// <summary>Original marker time format (hh:mm:ss.fff).</summary>
    public static string Time(long ticks) => TimeSpan.FromTicks(ticks).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    public static string IntroMessage(string? series, string? season, string? language) =>
        string.Format(Culture(language), Text("Notification_IntroUpdate_Message", Culture(language)), series, season);
    public static string CreditsMessage(string? series, string? season, string? language) =>
        string.Format(Culture(language), Text("Notification_CreditsUpdate_Message", Culture(language)), series, season);
    public static string IntroDescription(string? series, string? season, long startTicks, long endTicks, string? userName, string? language) =>
        string.Format(Culture(language), Lines(Text("Notification_IntroUpdate_Description", Culture(language))), series, season, Time(startTicks), Time(endTicks), userName);
    public static string CreditsDescription(string? series, string? season, long durationTicks, string? userName, string? language) =>
        string.Format(Culture(language), Lines(Text("Notification_CreditsUpdate_Description", Culture(language))), series, season, Time(durationTicks), userName);
}
