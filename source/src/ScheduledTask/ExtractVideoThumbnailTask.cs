using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.ScheduledTask;

public sealed class ExtractVideoThumbnailTask : IScheduledTask
{
    private readonly ILibraryManager library;
    public ExtractVideoThumbnailTask(ILibraryManager libraryManager) => library = libraryManager;
    public string Name => Common.UiLanguage.Text("Extract Video Thumbnail", "提取视频缩略图", "擷取影片縮圖");
    public string Description => Resources.ExtractVideoThumbnailTask_Description_Extract_video_thumbnail_preview;
    public string Category => "Strm Assistant Rebuild";
    public string Key => "VideoThumbnailExtractTask";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    public async Task Execute(CancellationToken token, IProgress<double> progress)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        var api = plugin.MediaInfoApi ?? throw new InvalidOperationException("Media services not initialized.");
        var thumbnail = plugin.VideoThumbnailApi ?? throw new InvalidOperationException("Image services not initialized.");
        var options = plugin.MediaStore.GetOptions();
        var items = MediaTaskSelection.Select(library, options, token, true).Where(item => MediaImageSelection.CanGenerateThumbnails(item, options)).ToArray();
        await plugin.WorkQueue.RunBatch(items, async (item, ct) =>
        {
            using var owned = ExclusiveMediaControl.EnterOwnedOperation();
            await api.ExtractAsync(item, ct).ConfigureAwait(false);
            var source = await api.ResolveMediaSourceAsync(item, ct).ConfigureAwait(false);
            if (!MediaImageSelection.CanGenerateThumbnails(item, options, source)) return;
            // Preserve the host's -1 chapter-only sentinel; the API clamps all other values to 1..300.
            var interval = library.GetLibraryOptions(item).ThumbnailImagesIntervalSeconds;
            await thumbnail.GenerateAsync((Video)item, source, interval,
                Math.Clamp((int)(options.MediaInfoExtractionTimeoutMs * options.ImageExtractionTimeoutMultiplier), 1000, 3600000), ct).ConfigureAwait(false);
        }, progress, token, itemKey: item => item.InternalId).ConfigureAwait(false);
    }
}
