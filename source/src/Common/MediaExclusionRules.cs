using Emby.Media.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace StrmAssistantRebuild.Common;

/// <summary>Exact host container aliases and filename-only keywords, shared by task selection and extraction.</summary>
public static class MediaExclusionRules
{
    public static bool Excludes(BaseItem item, string? containers, string? keywords, MediaSourceInfo? resolved = null) =>
        Keyword(item.Path, keywords) || Container(item, containers, resolved, true);

    public static bool Keyword(string? path, string? keywords)
    {
        var stem = FileStem(path);
        return (keywords ?? "").Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(word => stem.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
    public static string FileStem(string? path)
    {
        path ??= "";
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) path = uri.AbsolutePath;
        return Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
    }
    public static bool Container(BaseItem item, string? setting, MediaSourceInfo? resolved, bool checkFileExtension)
    {
        var selected = (setting ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Enum.TryParse<MediaContainers>(value, true, out var parsed) && Enum.IsDefined(typeof(MediaContainers), parsed) ? (MediaContainers?)parsed : null)
            .Where(value => value.HasValue).Select(value => value!.Value).ToHashSet();
        if (selected.Count == 0) return false;
        if (Matches(item.Container) || resolved != null && Matches(resolved.Container)) return true;
        if (!checkFileExtension) return false;
        // Never treat the STRM pointer's extension as the remote video's container. The host-resolved
        // probe path, when available, can provide a target extension without opening/downloading anything.
        return !item.IsShortcut && Extension(item.Path) || resolved != null && Extension(resolved.ProbePath ?? resolved.Path);

        bool Matches(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var parsed = EnumExtensions.EnumFromStringOrAlias<MediaContainers>(value);
            return parsed.HasValue && selected.Contains(parsed.Value);
        }
        bool Extension(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) path = uri.AbsolutePath;
            var extension = Path.GetExtension(path.Replace('\\', '/')).TrimStart('.');
            return selected.Any(value => EnumExtensions.GetAliases(value)?.Any(alias => string.Equals(alias, extension, StringComparison.OrdinalIgnoreCase)) == true);
        }
    }
}
