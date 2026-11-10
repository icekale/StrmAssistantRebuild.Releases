using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using StrmAssistantRebuild.Web.Api;

namespace StrmAssistantRebuild.Common;

/// <summary>Raw base64 artwork and native audio item links, never URLs or media-side writes.</summary>
internal static class EmbeddedMediaMetadata
{
    internal const int MaxImageBytes = 8 * 1024 * 1024;
    internal const int MaxImagePixels = 40_000_000;

    internal static void Validate(BaseItem item, MediaInfoBundle bundle)
    {
        if (!string.IsNullOrEmpty(bundle.EmbeddedImage))
        {
            if (item is not Audio) throw new NotSupportedException("EmbeddedImage is supported only for audio items.");
            if (bundle.EmbeddedImage.Length > ((MaxImageBytes + 2) / 3) * 4)
                throw new InvalidDataException("EmbeddedImage exceeds 8 MiB.");
        }
        if (bundle.EmbeddedInfo is not { } info) return;
        if (info.Track < 0 || info.Disc < 0 || info.Name?.Length > 4096 || info.Overview?.Length > 1_048_576 || info.Album?.Length > 4096)
            throw new InvalidDataException("Embedded metadata contains invalid track, disc or text values.");
        foreach (var names in new[] { info.Composers, info.Artists, info.AlbumArtists, info.Genres })
            if (names != null && (names.Length > 1024 || names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 4096)))
                throw new InvalidDataException("Embedded metadata names must be nonempty and bounded.");
        // The host's SaveItems excludes artist/composer links on ExtraType items.
        if (info.Composers != null && item is Audio && item.ExtraType.HasValue)
            throw new NotSupportedException("Composers require a regular audio item.");
    }

    internal static byte[] Decode(string encoded)
    {
        if (encoded.Length > ((MaxImageBytes + 2) / 3) * 4) throw new InvalidDataException("EmbeddedImage exceeds 8 MiB.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException ex) { throw new InvalidDataException("EmbeddedImage must be raw base64 image bytes, not a URL, flag or data URI.", ex); }
        if (bytes.Length > MaxImageBytes) throw new InvalidDataException("EmbeddedImage exceeds 8 MiB.");
        _ = Extension(bytes);
        return bytes;
    }

    internal static string Extension(byte[] bytes)
    {
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[^2] == 255 && bytes[^1] == 217) return ".jpg";
        if (bytes.Length >= 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP") return ".webp";
        throw new InvalidDataException("EmbeddedImage requires a PNG, JPEG or WebP raster image.");
    }

    internal static async Task<ItemImageInfo> PrepareImageAsync(BaseItem item, byte[] bytes, IImageProcessor? processor,
        EmbyPathPolicy paths, CancellationToken token)
    {
        if (processor == null) throw new NotSupportedException("EmbeddedImage requires the host image processor.");
        var root = paths.RequireWritablePath(item.GetInternalMetadataPath());
        paths.RequireWritablePath(BaseItem.ConfigurationManager.ApplicationPaths.ImageCachePath);
        paths.RequireWritablePath(BaseItem.ConfigurationManager.ApplicationPaths.TempDirectory);
        Directory.CreateDirectory(root);
        var path = paths.RequireWritablePath(Path.Combine(root, "embedded-" + Guid.NewGuid().ToString("N") + Extension(bytes)));
        try
        {
            await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ImageSize size;
            Tuple<string, string, DateTimeOffset> decoded;
            try
            {
                size = processor.GetImageSize(path);
                if (size.Width <= 0 || size.Height <= 0 || !double.IsFinite(size.Width * size.Height) || size.Width * size.Height > MaxImagePixels)
                    throw new InvalidDataException("EmbeddedImage dimensions are invalid or exceed 40 million pixels.");
                // Quality < 90 prevents native ProcessImage's original-file fast path. A decoder failure
                // is swallowed by that API and returns the original path, which is explicitly rejected.
                // Item=null avoids OnImageSizeObtained calling UpdateImages during input validation.
                decoded = await processor.ProcessImage(new ImageProcessingOptions
                {
                    Image = new ItemImageInfo { Path = path, Type = ImageType.Primary, DateModified = File.GetLastWriteTimeUtc(path) },
                    Quality = 89, SupportedOutputFormats = new[] { ImageFormat.Png }, Enhancers = Array.Empty<IImageEnhancer>()
                }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (decoded == null || string.Equals(Path.GetFullPath(decoded.Item1), path, StringComparison.Ordinal))
                    throw new InvalidDataException("The host image decoder did not validate EmbeddedImage.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not InvalidDataException)
            { throw new InvalidDataException("The host image decoder rejected EmbeddedImage.", ex); }
            var validated = await ReadBoundedAsync(paths.RequireWritablePath(decoded.Item1), token).ConfigureAwait(false);
            if (Extension(validated) != ".png") throw new InvalidDataException("The host image decoder did not produce PNG output.");
            return new ItemImageInfo { Path = path, Type = ImageType.Primary, Width = (int)size.Width, Height = (int)size.Height, DateModified = File.GetLastWriteTimeUtc(path) };
        }
        catch { DeleteOwned(path, paths); throw; }
    }

    internal static async Task<string?> ExportImageAsync(BaseItem item, EmbyPathPolicy paths, CancellationToken token)
    {
        if (item is not Audio || item.GetImageInfo(ImageType.Primary, 0) is not { IsLocalFile: true } image || !Path.IsPathRooted(image.Path)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.GetInternalMetadataPath())) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(image.Path);
        // Only this item's registered native metadata artwork is eligible; never fetch remote URLs,
        // dereference external media-side art, or use a path supplied by an imported bundle.
        if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        paths.RequireWritablePath(path); // Same root/symlink rules apply to this bounded read.
        if (!File.Exists(path)) return null;
        var bytes = await ReadBoundedAsync(path, token).ConfigureAwait(false);
        _ = Extension(bytes);
        return Convert.ToBase64String(bytes);
    }

    internal static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (stream.Length > MaxImageBytes) throw new InvalidDataException("EmbeddedImage exceeds 8 MiB.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > MaxImageBytes) throw new InvalidDataException("EmbeddedImage exceeds 8 MiB.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static void DeleteOwned(string path, EmbyPathPolicy paths)
    {
        try { File.Delete(paths.RequireWritablePath(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Plugin.Instance?.Logger.Warn("[MediaInfo] Embedded image cleanup failed: {0}", ex.Message); }
    }
}
