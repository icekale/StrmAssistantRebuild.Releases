using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

public static class MetadataRefreshPolicy
{
    public static bool ContainsChinese(string? value) => value?.Any(c => c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff') == true;
    public static HashSet<string> Selected(string? value) => (value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public static bool NeedsRefresh(BaseItem item, MetadataRefreshOptions options, DateTimeOffset now)
    {
        if (item.IsLocked) return false;
        if (item.PremiereDate.HasValue && item.PremiereDate < now.AddDays(-Math.Clamp(options.MetadataRefreshLookbackDays, 1, 36500))) return false;
        var rules = Selected(options.MetadataRefreshPreferences);
        if (rules.Count == 0) return true;
        var image = item.GetImageInfo(ImageType.Primary, 0);
        return rules.Contains("NoOverview") && string.IsNullOrWhiteSpace(item.Overview) ||
            rules.Contains("NoImage") && image == null ||
            rules.Contains("NonChineseNameOverview") && (!ContainsChinese(item.Name) || !ContainsChinese(item.Overview)) ||
            rules.Contains("DefaultEpisodeName") && item is Episode && DefaultNumberTitle(item.Name, false) ||
            rules.Contains("DefaultSeasonName") && item is Season && DefaultNumberTitle(item.Name, true) ||
            rules.Contains("ReplaceCapturedImage") && image != null && item.Width > 0 && item.Height > 0 && image.Width == item.Width && image.Height == item.Height;
    }
    public static bool DefaultNumberTitle(string? name, bool season) => !string.IsNullOrEmpty(name) && Regex.IsMatch(name,
        season ? @"^(?:Season\s*\d+|第\s*[0-9一二三四五六七八九十百]+\s*季)$" : @"^(?:Episode\s*\d+|第\s*[0-9一二三四五六七八九十百]+\s*集)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static (bool Metadata, bool Images) PersonRefresh(Person person, MetadataRefreshOptions options, DateTimeOffset now)
    {
        if (person.IsLocked) return (false, false);
        var rules = Selected(options.PersonRefreshPreferences);
        var chinese = ContainsChinese(person.Name) && ContainsChinese(person.Overview);
        var metadata = rules.Contains("FullMetadataRefresh") || rules.Contains("DefaultMetadataRefresh") && (!chinese || person.DateLastSaved < now.AddDays(-30));
        if (!rules.Contains("FullMetadataRefresh") && rules.Contains("ExcludeIfChinese") && chinese) metadata = false;
        if (person.IsFieldLocked(MetadataFields.Name) && person.IsFieldLocked(MetadataFields.Overview)) metadata = false;
        var image = person.GetImageInfo(ImageType.Primary, 0);
        var images = rules.Contains("FullImageRefresh") || rules.Contains("DefaultImageRefresh") &&
            (image == null || rules.Contains("DownloadImagesInAdvance") && !image.IsLocalFile);
        return (metadata, images);
    }
}
