using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace StrmAssistantRebuild.Common;

/// <summary>Percentage screenshots through the selected encoder, persisted and registered without sidecars.</summary>
public sealed class ImageCaptureApi
{
    private readonly IImageExtractionManager images;
    private readonly IItemRepository repository;
    private readonly IMediaMountManager mounts;
    private readonly IServerApplicationPaths paths;
    private readonly string workRoot;
    private readonly CustomEncoderRunner? customEncoder;
    private readonly Func<string?>? getCropEncoder;

    public ImageCaptureApi(IImageExtractionManager images, IItemRepository repository, IMediaMountManager mounts,
        IServerApplicationPaths paths, string workRoot, CustomEncoderRunner? customEncoder = null,
        IImageProcessor? imageProcessor = null, Func<string?>? getCropEncoder = null)
    {
        this.images = images;
        this.repository = repository;
        this.mounts = mounts;
        this.paths = paths;
        this.workRoot = workRoot;
        this.customEncoder = customEncoder;
        // Emby 4.10 ImageProcessingOptions has no exact crop rectangle; Width/Height is not equivalent.
        _ = imageProcessor;
        this.getCropEncoder = getCropEncoder;
    }

    public static TimeSpan CapturePosition(long runtimeTicks, int positionPercent)
    {
        if (runtimeTicks <= 0) throw new ArgumentOutOfRangeException(nameof(runtimeTicks));
        if (positionPercent is < 10 or > 90) throw new ArgumentOutOfRangeException(nameof(positionPercent), "Position must be 10–90 percent.");
        return TimeSpan.FromTicks((long)(runtimeTicks * (decimal)positionPercent / 100));
    }

    public async Task<string> CaptureAsync(Video item, MediaSourceInfo source, int positionPercent, int timeoutMs,
        CancellationToken cancellationToken, bool generateBackdrop = false, string? postProcessMode = null,
        bool onlyWhenMissing = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (onlyWhenMissing && (item.IsLocked || item.HasImage(ImageType.Primary)))
            return item.GetImageInfo(ImageType.Primary, 0)?.Path ?? string.Empty;
        using var deadline = ThumbnailFiles.Deadline(timeoutMs, cancellationToken);
        var token = deadline.Token;
        var executable = customEncoder?.GetExecutable();
        var stream = ThumbnailFiles.VideoStream(source);
        var position = CapturePosition(ThumbnailFiles.Runtime(item, source), positionPercent);
        // ExtractVideoImage chooses a GUID.jpg under this exact host root. Validate before the host writes.
        ThumbnailFiles.Writable(paths.TempDirectory);
        var destination = ThumbnailFiles.Writable(Path.Combine(workRoot, "images", item.Id.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".jpg"));
        string? extracted = null;
        string? backdropDestination = null;
        var registered = false;
        var cropEncoderResolved = false;
        string? cropEncoder = null;
        string CropEncoder()
        {
            if (!cropEncoderResolved) { cropEncoder = executable ?? getCropEncoder?.Invoke(); cropEncoderResolved = true; }
            return !string.IsNullOrWhiteSpace(cropEncoder) ? cropEncoder :
                throw new NotSupportedException("Exact image cropping requires the custom encoder or host IMediaEncoder.EncoderPath.");
        }
        try
        {
            using var mount = await mounts.Mount(source.ToFileSystemMetadata(), source.Container, token).ConfigureAwait(false);
            extracted = await CustomEncoderRunner.ExtractImageAsync(executable, images, item, source, mount,
                stream, position, paths.TempDirectory, timeoutMs, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThumbnailFiles.Writable(extracted);
            var originalSize = ThumbnailFiles.ValidateJpeg(extracted);
            var primaryCrop = AspectCrop(item, originalSize.Width, originalSize.Height, postProcessMode);
            Directory.CreateDirectory(ThumbnailFiles.Writable(Path.GetDirectoryName(destination)!));
            await WriteImageAsync(extracted, destination, primaryCrop, CropEncoder, timeoutMs, token).ConfigureAwait(false);
            var dimensions = ThumbnailFiles.ValidateJpeg(destination);
            var backdropSize = dimensions;
            if ((generateBackdrop || HasMode(postProcessMode, "GenerateBackdrop")) &&
                (!onlyWhenMissing || !item.HasImage(ImageType.Backdrop)))
            {
                // The original backdrop helper receives the post-processed dynamic primary image.
                var backdropCrop = AspectCrop(item, dimensions.Width, dimensions.Height, null, backdrop: true);
                backdropDestination = backdropCrop == null ? destination :
                    ThumbnailFiles.Writable(Path.Combine(Path.GetDirectoryName(destination)!, Path.GetFileNameWithoutExtension(destination) + "-backdrop.jpg"));
                if (backdropDestination != destination)
                    await WriteImageAsync(destination, backdropDestination, backdropCrop, CropEncoder, timeoutMs, token).ConfigureAwait(false);
                backdropSize = ThumbnailFiles.ValidateJpeg(backdropDestination);
            }
            token.ThrowIfCancellationRequested();
            if (onlyWhenMissing && (item.IsLocked || item.HasImage(ImageType.Primary)))
                return item.GetImageInfo(ImageType.Primary, 0)?.Path ?? string.Empty;
            if (onlyWhenMissing && item.HasImage(ImageType.Backdrop))
            {
                if (backdropDestination != destination) ThumbnailFiles.DeleteFile(backdropDestination);
                backdropDestination = null;
            }
            var previous = item.ImageInfos.Select(image => new ItemImageInfo(image)).ToArray();
            try
            {
                ItemImageInfo Image(ImageType type, string file, (int Width, int Height) size) => new()
                    { Path = file, Type = type, DateModified = File.GetLastWriteTimeUtc(file), Width = size.Width, Height = size.Height };
                item.SetImage(Image(ImageType.Primary, destination, dimensions), 0, false);
                if (backdropDestination != null) item.SetImage(Image(ImageType.Backdrop, backdropDestination, backdropSize), 0, false);
                repository.SaveImages(item.InternalId, item.ImageInfos);
                registered = true;
                // Only retire this plugin's own unreferenced capture; leave host/library artwork untouched.
                var imageRoot = Path.GetDirectoryName(destination)!;
                var prefix = item.Id.ToString("N") + "-";
                foreach (var old in previous.Select(i => i.Path).Distinct(StringComparer.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(old) && string.Equals(Path.GetDirectoryName(old), imageRoot, StringComparison.Ordinal) &&
                        Path.GetFileName(old).StartsWith(prefix, StringComparison.Ordinal) &&
                        !item.ImageInfos.Any(i => string.Equals(i.Path, old, StringComparison.Ordinal))) ThumbnailFiles.DeleteFile(old);
                }
                return destination;
            }
            catch { item.ImageInfos = previous; throw; }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Image extraction exceeded its deadline.", ex);
        }
        finally
        {
            ThumbnailFiles.DeleteFile(extracted);
            if (!registered)
            {
                ThumbnailFiles.DeleteFile(destination);
                if (backdropDestination != destination) ThumbnailFiles.DeleteFile(backdropDestination);
            }
        }
    }

    private static bool HasMode(string? modes, string mode) => modes?.Split(',')
        .Any(value => value.Trim().Equals(mode, StringComparison.OrdinalIgnoreCase)) == true;

    // Original helper 115662: mode 0 is orientation-aware, mode 2 forces portrait, mode 1 is backdrop.
    // Returning a rectangle is a pure policy decision, not a claim that pixels have been changed.
    internal static (int X, int Y, int Width, int Height)? AspectCrop(Video item, int width, int height,
        string? modes, bool backdrop = false)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var ratio = (double)width / height;
        double target;
        if (backdrop) target = 16d / 9;
        else if (item is not Episode && !item.ExtraType.HasValue && HasMode(modes, "EnsureAspect2By3")) target = 2d / 3;
        else if (HasMode(modes, "EnsureAspect16By9")) target = ratio >= 1 ? 16d / 9 : 2d / 3;
        else return null;
        if (Math.Abs(ratio - target) < 0.01) return null;
        var cropWidth = ratio > target ? (int)Math.Round(height * target) : width;
        var cropHeight = ratio > target ? height : (int)Math.Round(width / target);
        if (cropWidth <= 0 || cropHeight <= 0) throw new InvalidDataException("Image is too small for the selected aspect crop.");
        return ((width - cropWidth) / 2, (int)Math.Round((height - cropHeight) * (backdrop ? 0.382 : 0.5)), cropWidth, cropHeight);
    }

    private static async Task WriteImageAsync(string input, string destination,
        (int X, int Y, int Width, int Height)? crop, Func<string> encoder, int timeoutMs, CancellationToken token)
    {
        if (crop is not { } rectangle)
        {
            await using var source = File.OpenRead(input);
            await using var output = new ThumbnailFiles.CancellableFileStream(destination, token);
            await source.CopyToAsync(output, token).ConfigureAwait(false);
            return;
        }
        var start = CustomProbeRunner.CreateProcessStartInfo(encoder(), "image crop");
        start.WorkingDirectory = ThumbnailFiles.Writable(Path.GetDirectoryName(destination)!);
        var filter = FormattableString.Invariant($"crop={rectangle.Width}:{rectangle.Height}:{rectangle.X}:{rectangle.Y}:exact=1");
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-noautorotate", "-i", ThumbnailFiles.Writable(input),
            "-map", "0:v:0", "-an", "-sn", "-dn", "-vf", filter, "-frames:v", "1", "-threads", "1", "-c:v", "mjpeg",
            "-pix_fmt", "yuvj444p", "-q:v", "2", "-f", "image2pipe", "pipe:1" }) start.ArgumentList.Add(arg);
        var jpeg = await CustomProbeRunner.RunProcessAsync(start, timeoutMs, token, "image crop").ConfigureAwait(false);
        await using (var output = new ThumbnailFiles.CancellableFileStream(destination, token))
            await output.WriteAsync(jpeg, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var size = ThumbnailFiles.ValidateJpeg(destination);
        if (size != (rectangle.Width, rectangle.Height))
            throw new InvalidDataException("Image crop encoder returned dimensions different from the requested rectangle.");
    }
}
