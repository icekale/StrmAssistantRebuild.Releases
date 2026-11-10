using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StrmAssistantRebuild.Common;

/// <summary>Shared validation and bounded, plugin-owned thumbnail files; no process or global patches.</summary>
internal static class ThumbnailFiles
{
    internal static string Writable(string path) => (Plugin.Instance ?? throw new InvalidOperationException("Plugin is not initialized.")).Paths.RequireWritablePath(path);

    internal static string CreateWorkDirectory(string root)
    {
        var directory = Writable(Path.Combine(root, "thumbnail-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static MediaStream VideoStream(MediaSourceInfo source)
    {
        if (string.IsNullOrWhiteSpace(source.Path)) throw new ArgumentException("Resolved media source path is required.", nameof(source));
        // Emby exposes embedded artwork as EmbeddedImage, not Video. Never use the embedded-image overload.
        return source.MediaStreams?.Where(s => s.Type == MediaStreamType.Video).OrderByDescending(s => s.IsDefault).FirstOrDefault()
            ?? throw new InvalidDataException("Media source has no video stream; probe it before image extraction.");
    }

    internal static long Runtime(Video item, MediaSourceInfo source)
    {
        var ticks = source.RunTimeTicks ?? item.RunTimeTicks;
        if (ticks is null or <= 0) throw new InvalidDataException("A positive media runtime is required.");
        return ticks.Value;
    }

    internal static MediaContainers? Container(string? value) => Enum.TryParse<MediaContainers>(value, true, out var result) ? result : null;

    internal static CancellationTokenSource Deadline(int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        return deadline;
    }

    internal static void DeleteFile(string? path)
    {
        if (path is null) return;
        try { File.Delete(Writable(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Plugin.Instance?.Logger.Warn("Thumbnail temporary file cleanup: {0}", ex.Message); }
    }

    internal static void DeleteWorkDirectory(string? path)
    {
        if (path is null || !Directory.Exists(path)) return;
        try
        {
            // Check every entry rather than deleting recursively through an unexpected symlink.
            Writable(path);
            foreach (var file in Directory.EnumerateFiles(path)) DeleteFile(file);
            Directory.Delete(Writable(path), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Plugin.Instance?.Logger.Warn("Thumbnail temporary directory cleanup: {0}", ex.Message); }
    }

    internal static (int Width, int Height) ValidateJpeg(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 4 || stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8)
            throw new InvalidDataException("Image extractor returned an empty or non-JPEG output.");
        stream.Seek(-2, SeekOrigin.End);
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd9) throw new InvalidDataException("JPEG output is truncated.");
        stream.Position = 2;
        int Word() { var high = stream.ReadByte(); var low = stream.ReadByte(); if (low < 0 || high < 0) throw new InvalidDataException("JPEG header is truncated."); return (high << 8) | low; }
        while (stream.Position < stream.Length - 2)
        {
            if (stream.ReadByte() != 255) throw new InvalidDataException("JPEG segment marker is invalid.");
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 255);
            if (marker is 0xda or 0xd9 or < 0) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            var length = Word();
            if (length < 2 || stream.Position + length - 2 > stream.Length) throw new InvalidDataException("JPEG segment is truncated.");
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8) throw new InvalidDataException("JPEG frame header is invalid.");
                stream.ReadByte();
                var height = Word(); var width = Word();
                if (width == 0 || height == 0) throw new InvalidDataException("JPEG dimensions are invalid.");
                return (width, height);
            }
            stream.Position += length - 2;
        }
        throw new InvalidDataException("JPEG frame dimensions are missing.");
    }

    internal static int FrameNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith("img_", StringComparison.Ordinal) || !int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException("Unexpected image-series output name.");
        return value;
    }

    // Video.CreateBif has no token parameter. Its writes (including JPEG CopyToAsync) pass through this stream.
    internal sealed class CancellableFileStream : FileStream
    {
        private readonly CancellationToken token;
        internal CancellableFileStream(string path, CancellationToken token)
            : base(Writable(path), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous) => this.token = token;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            token.ThrowIfCancellationRequested();
            return base.WriteAsync(buffer, offset, count, token);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            token.ThrowIfCancellationRequested();
            return base.WriteAsync(buffer, token);
        }
    }
}
