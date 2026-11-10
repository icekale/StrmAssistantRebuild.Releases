using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

/// <summary>Image-specific exclusions do not suppress media probing or metadata persistence.</summary>
public static class MediaImageSelection
{
    public static bool CanAutoCapture(BaseItem item, MediaInfoExtractOptions options, MediaSourceInfo? resolved = null) =>
        options.EnableImageCapture && item is Video && !item.IsLocked && !item.HasImage(ImageType.Primary) &&
        !MediaExclusionRules.Container(item, options.ImageCaptureExcludeMediaContainers, resolved, true);

    public static bool CanGenerateThumbnails(BaseItem item, MediaInfoExtractOptions options, MediaSourceInfo? resolved = null) =>
        item is Video && !string.IsNullOrWhiteSpace(item.Path) &&
        !MediaExclusionRules.Keyword(item.Path, options.VideoThumbnailExcludeKeywords) &&
        !MediaExclusionRules.Container(item, options.VideoThumbnailExcludeMediaContainers, resolved, false);

}
