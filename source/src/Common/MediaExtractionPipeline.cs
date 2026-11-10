using MediaBrowser.Controller.Entities;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

public static class MediaExtractionPipeline
{
    public static async Task Extract(Plugin plugin, BaseItem item, MediaInfoExtractOptions options, CancellationToken token)
    {
        using var owned = ExclusiveMediaControl.EnterOwnedOperation();
        using var saverOwner = ExclusiveSaverControl.EnterOwnedItem(item);
        var api = plugin.MediaInfoApi ?? throw new InvalidOperationException("Media services not initialized.");
        await api.ExtractAsync(item, token).ConfigureAwait(false);
        if (MediaImageSelection.CanAutoCapture(item, options) && item is Video video && plugin.ImageCaptureApi is { } capture)
        {
            var source = await api.ResolveMediaSourceAsync(item, token).ConfigureAwait(false);
            if (!MediaImageSelection.CanAutoCapture(item, options, source)) return;
            await capture.CaptureAsync(video, source, options.ImageCapturePosition,
                Math.Clamp((int)(options.MediaInfoExtractionTimeoutMs * options.ImageExtractionTimeoutMultiplier), 1000, 3600000), token,
                ScopeSelection.Parse(options.ImageCapturePostProcessMode).Contains("GenerateBackdrop"),
                postProcessMode: options.ImageCapturePostProcessMode, onlyWhenMissing: true).ConfigureAwait(false);
        }
    }
}
