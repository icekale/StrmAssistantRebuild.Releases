using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace StrmAssistantRebuild.Common;

/// <summary>Per-operation image series + public BIF writer + discoverable metadata path + chapter registration.</summary>
public sealed class VideoThumbnailApi
{
    private readonly IImageExtractionManager images;
    private readonly IItemRepository repository;
    private readonly IMediaMountManager mounts;
    private readonly string workRoot;
    private readonly IImageProcessor? imageProcessor;
    private readonly CustomEncoderRunner? customEncoder;

    public VideoThumbnailApi(IImageExtractionManager images, IItemRepository repository, IMediaMountManager mounts, string workRoot,
        IImageProcessor? imageProcessor = null, CustomEncoderRunner? customEncoder = null)
    {
        this.images = images;
        this.repository = repository;
        this.mounts = mounts;
        this.workRoot = workRoot;
        this.imageProcessor = imageProcessor;
        this.customEncoder = customEncoder;
    }

    public async Task<string> GenerateAsync(Video item, MediaSourceInfo source, int intervalSeconds, int timeoutMs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(source);
        using var deadline = ThumbnailFiles.Deadline(timeoutMs, cancellationToken);
        var token = deadline.Token;
        var executable = customEncoder?.GetExecutable();
        if (intervalSeconds != -1) intervalSeconds = Math.Clamp(intervalSeconds, 1, 300);
        if (!item.SupportsThumbnails) throw new NotSupportedException("Host Video.SupportsThumbnails is false; its BIF endpoint would return empty data for this item.");
        var stream = ThumbnailFiles.VideoStream(source);
        var runtime = ThumbnailFiles.Runtime(item, source);
        var fileName = Path.GetFileNameWithoutExtension(item.Path);
        if (string.IsNullOrWhiteSpace(fileName)) throw new InvalidDataException("Video filename is required for host BIF discovery.");
        var metadata = ThumbnailFiles.Writable(item.GetInternalMetadataPath());
        var destination = ThumbnailFiles.Writable(Path.Combine(metadata, fileName + "-320-" + intervalSeconds.ToString(CultureInfo.InvariantCulture) + ".bif"));
        string? work = null;
        try
        {
            work = ThumbnailFiles.CreateWorkDirectory(workRoot);
            using var mount = await mounts.Mount(source.ToFileSystemMetadata(), source.Container, token).ConfigureAwait(false);
            if (intervalSeconds == -1)
                return await GenerateChapterImages(item, source, mount, stream, runtime, metadata, work, token, executable, timeoutMs).ConfigureAwait(false);
            var expectedFrames = checked((int)Math.Ceiling(runtime / (double)TimeSpan.TicksPerSecond / intervalSeconds));
            if (expectedFrames > 1_000_000) throw new InvalidDataException("Thumbnail frame count exceeds the supported BIF index limit.");
            if (executable == null)
                await images.ExtractVideoImagesOnInterval(source, mount?.MountedPathInfo.FullName ?? source.Path,
                    ThumbnailFiles.Container(mount?.MountedContainer ?? source.Container), stream, mount?.MountedProtocol ?? source.Protocol,
                    item.Video3DFormat, TimeSpan.FromSeconds(intervalSeconds), work, "img_", 320, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var frames = Directory.GetFiles(work, "img_*.jpg").OrderBy(ThumbnailFiles.FrameNumber).ToArray();
            if (executable != null || frames.Length != expectedFrames)
            {
                if (executable == null) Plugin.Instance?.Logger.Info("Thumbnail interval extraction returned {0}/{1} frames; retrying explicit host timestamps.", frames.Length, expectedFrames);
                foreach (var frame in frames) ThumbnailFiles.DeleteFile(frame);
                var times = Enumerable.Range(0, expectedFrames).Select(i => TimeSpan.FromSeconds((long)i * intervalSeconds)).ToArray();
                frames = await ExtractExplicitFrames(item, source, mount, stream, times, work, token, executable, timeoutMs).ConfigureAwait(false);
            }
            if (frames.Length != expectedFrames) throw new InvalidDataException("Image extraction did not cover the complete BIF timeline.");
            for (var i = 0; i < frames.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                ThumbnailFiles.Writable(frames[i]);
                if (ThumbnailFiles.FrameNumber(frames[i]) != i + 1) throw new InvalidDataException("Image-series frame sequence has a gap.");
                if (ThumbnailFiles.ValidateJpeg(frames[i]).Width > 320) throw new InvalidDataException("Thumbnail JPEG width exceeds the advertised 320-pixel set.");
            }
            var descriptors = frames.Select(path => new FileSystemMetadata { FullName = path, Length = new FileInfo(path).Length }).ToList();
            var estimatedLength = 64L + 8L * (frames.Length + 1) + descriptors.Sum(f => f.Length);
            if (estimatedLength > int.MaxValue) throw new InvalidDataException("BIF exceeds the host writer's signed 32-bit offset limit.");
            var staged = ThumbnailFiles.Writable(Path.Combine(work, "preview.bif"));
            await using (var output = new ThumbnailFiles.CancellableFileStream(staged, token))
                await Video.CreateBif(output, checked(intervalSeconds * 1000), descriptors).ConfigureAwait(false);
            var index = ReadIndex(staged, token);
            if (index.FrameCount != frames.Length || index.TimestampMultiplierMs != intervalSeconds * 1000)
                throw new InvalidDataException("Host BIF header disagrees with extracted images.");
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(ThumbnailFiles.Writable(metadata));
            // Same-volume atomic publish in the destination directory; retain the previous BIF on repository failure.
            var pending = ThumbnailFiles.Writable(destination + "." + Guid.NewGuid().ToString("N") + ".tmp");
            var backup = ThumbnailFiles.Writable(destination + "." + Guid.NewGuid().ToString("N") + ".bak");
            var hadPrevious = File.Exists(destination);
            var published = false;
            var registered = false;
            try
            {
                await using (var input = File.OpenRead(staged))
                await using (var output = new ThumbnailFiles.CancellableFileStream(pending, token))
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                var chapters = ReadChapters(item, token);
                token.ThrowIfCancellationRequested();
                if (hadPrevious) File.Replace(ThumbnailFiles.Writable(pending), ThumbnailFiles.Writable(destination), ThumbnailFiles.Writable(backup));
                else File.Move(ThumbnailFiles.Writable(pending), ThumbnailFiles.Writable(destination));
                published = true;
                foreach (var chapter in chapters)
                {
                    chapter.ImagePath = destination;
                    chapter.ImageDateModified = File.GetLastWriteTimeUtc(destination);
                    chapter.ImageTag = null;
                }
                repository.SaveChapters(item.InternalId, true, chapters);
                registered = true;
                await OwnedBifFiles.PublishAndRetireAsync(item.InternalId, destination, token).ConfigureAwait(false);
                var discovery = new DirectoryService(Plugin.Instance!.Logger, BaseItem.FileSystem);
                var selected = Video.GetThumbnailSetInfos(item.Path, item.Id, discovery, 320, true).FirstOrDefault();
                if (selected == null || !string.Equals(Path.GetFullPath(selected.Path), Path.GetFullPath(destination),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new IOException("A different native or unowned BIF still takes precedence; it was preserved. New set: " + destination);
                return destination;
            }
            finally
            {
                ThumbnailFiles.DeleteFile(pending);
                if (registered) ThumbnailFiles.DeleteFile(backup);
                else
                {
                    if (published) ThumbnailFiles.DeleteFile(destination);
                    if (File.Exists(backup)) File.Move(ThumbnailFiles.Writable(backup), ThumbnailFiles.Writable(destination), true);
                }
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Host thumbnail extraction exceeded its deadline.", ex);
        }
        finally { ThumbnailFiles.DeleteWorkDirectory(work); }
    }

    private List<ChapterInfo> ReadChapters(Video item, CancellationToken token)
    {
        var chapters = repository.GetChapters(item, token).Select(c => new ChapterInfo
        {
            StartPositionTicks = c.StartPositionTicks, Name = c.Name, MarkerType = c.MarkerType,
            ChapterIndex = c.ChapterIndex, ImageTag = c.ImageTag, ImagePath = c.ImagePath, ImageDateModified = c.ImageDateModified
        }).ToList();
        if (chapters.Count == 0) chapters.Add(new ChapterInfo { StartPositionTicks = 0, Name = "Chapter 1" });
        return chapters;
    }

    private async Task<string> GenerateChapterImages(Video item, MediaSourceInfo source, IMediaMount? mount,
        MediaStream stream, long runtime, string metadata, string work, CancellationToken token, string? executable, int timeoutMs)
    {
        var chapters = ReadChapters(item, token);
        // Use native slow-path's first-chapter 15s, but stay one second before EOF (half-runtime for tiny clips).
        // Unlike native quick-path's >=8s rounding, nonzero chapter positions stay exact unless bounded at EOF.
        var lastPosition = runtime - Math.Min(TimeSpan.TicksPerSecond, Math.Max(1, runtime / 2));
        var positions = chapters.Select(c => TimeSpan.FromTicks(Math.Clamp(
            c.StartPositionTicks == 0 ? 15 * TimeSpan.TicksPerSecond : c.StartPositionTicks, 0, lastPosition))).ToArray();
        var times = positions.Distinct().OrderBy(t => t).ToArray();
        var frames = await ExtractExplicitFrames(item, source, mount, stream, times, work, token, executable, timeoutMs).ConfigureAwait(false);
        if (frames.Length != times.Length) throw new InvalidDataException("Chapter image extraction did not cover all requested positions.");
        foreach (var frame in frames)
        {
            token.ThrowIfCancellationRequested();
            ThumbnailFiles.Writable(frame);
            ThumbnailFiles.ValidateJpeg(frame);
        }
        // Independent namespace per successful generation: never overwrite native or another writer's JPEGs.
        var destination = ThumbnailFiles.Writable(Path.Combine(metadata, "strmassistant-chapters-" + Guid.NewGuid().ToString("N")));
        var registered = false;
        try
        {
            Directory.CreateDirectory(destination);
            var paths = new string[frames.Length];
            for (var i = 0; i < frames.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                paths[i] = ThumbnailFiles.Writable(Path.Combine(destination, Path.GetFileName(frames[i])));
                await using var input = File.OpenRead(frames[i]);
                await using var output = new ThumbnailFiles.CancellableFileStream(paths[i], token);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
            for (var i = 0; i < chapters.Count; i++)
            {
                var path = paths[Array.IndexOf(times, positions[i])];
                chapters[i].ImagePath = path;
                chapters[i].ImageDateModified = File.GetLastWriteTimeUtc(path);
                chapters[i].ImageTag = null;
            }
            token.ThrowIfCancellationRequested();
            repository.SaveChapters(item.InternalId, true, chapters);
            registered = true; // JPEGs must survive even if post-commit retirement/discovery fails.
            await OwnedBifFiles.RetireAllOwnedAsync(item.InternalId, metadata, token).ConfigureAwait(false);
            var discovery = new DirectoryService(Plugin.Instance!.Logger, BaseItem.FileSystem);
            var selected = Video.GetThumbnailSetInfos(item.Path, item.Id, discovery, 320, true).FirstOrDefault();
            if (selected != null)
                throw new IOException("Chapter JPEGs registered, but a native or unowned BIF still takes precedence for the thumbnail/BIF endpoint; it was preserved: " + selected.Path);
            Plugin.Instance?.Logger.Info("Chapter JPEGs registered; prior JPEG files preserved. Native chapter image paths use the new JPEGs; no BIF set remains: {0}", destination);
            return destination;
        }
        finally
        {
            if (!registered) ThumbnailFiles.DeleteWorkDirectory(destination);
        }
    }

    private async Task<string[]> ExtractExplicitFrames(Video item, MediaSourceInfo source, IMediaMount? mount,
        MediaStream stream, TimeSpan[] times, string work, CancellationToken token, string? executable, int timeoutMs)
    {
        var input = mount?.MountedPathInfo.FullName ?? source.Path;
        var container = ThumbnailFiles.Container(mount?.MountedContainer ?? source.Container);
        var protocol = mount?.MountedProtocol ?? source.Protocol;
        if (executable == null)
        {
            try
            {
                var actual = await images.ExtractVideoImagesFromList2(source, input, container, stream, protocol,
                    item.Video3DFormat, times, work, "img_", 320, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var list = Directory.GetFiles(work, "img_*.jpg").OrderBy(ThumbnailFiles.FrameNumber).ToArray();
                // Host MultiImageExtractor emits a leading initialization frame and skips its timestamp in the return value.
                if (actual.Count == times.Length && list.Length == times.Length + 1 &&
                    actual.Zip(times, (a, b) => Math.Abs((a - b).TotalMilliseconds) <= 250).All(value => value))
                {
                    ThumbnailFiles.DeleteFile(list[0]);
                    for (var i = 1; i < list.Length; i++)
                        File.Move(ThumbnailFiles.Writable(list[i]), ThumbnailFiles.Writable(Path.Combine(work, "img_" + i.ToString("D5", CultureInfo.InvariantCulture) + ".jpg")));
                    return Directory.GetFiles(work, "img_*.jpg").OrderBy(ThumbnailFiles.FrameNumber).ToArray();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is NotSupportedException or IOException)
            {
                Plugin.Instance?.Logger.Warn("Host timestamp-list extraction degraded: {0}", ex.Message);
            }
            foreach (var frame in Directory.GetFiles(work, "img_*.jpg")) ThumbnailFiles.DeleteFile(frame);
            Plugin.Instance?.Logger.Info("Host timestamp list did not cover requested positions; extracting {0} individual frames.", times.Length);
        }
        ThumbnailFiles.Writable(BaseItem.ConfigurationManager.ApplicationPaths.TempDirectory);
        // ponytail: custom mode uses exact single-frame seeks and the existing host resize path; batch only after profiling.
        var result = new string[times.Length];
        for (var i = 0; i < times.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            string? extracted = null;
            try
            {
                extracted = await CustomEncoderRunner.ExtractImageAsync(executable, images, item, source, mount, stream,
                    times[i], BaseItem.ConfigurationManager.ApplicationPaths.TempDirectory, timeoutMs, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThumbnailFiles.Writable(extracted);
                var dimensions = ThumbnailFiles.ValidateJpeg(extracted);
                result[i] = ThumbnailFiles.Writable(Path.Combine(work, "img_" + (i + 1).ToString("D5", CultureInfo.InvariantCulture) + ".jpg"));
                await using var output = new ThumbnailFiles.CancellableFileStream(result[i], token);
                if (imageProcessor != null)
                {
                    ThumbnailFiles.Writable(BaseItem.ConfigurationManager.ApplicationPaths.ImageCachePath);
                    await imageProcessor.ProcessImage(new ImageProcessingOptions
                    {
                        Item = item, ItemId = item.InternalId,
                        Image = new ItemImageInfo { Path = extracted, DateModified = File.GetLastWriteTimeUtc(extracted), Type = ImageType.Primary },
                        MaxWidth = 320, Quality = 90, SupportedOutputFormats = new[] { ImageFormat.Jpg },
                        Enhancers = Array.Empty<MediaBrowser.Controller.Providers.IImageEnhancer>()
                    }, output, token).ConfigureAwait(false);
                }
                else
                {
                    if (dimensions.Width > 320) throw new NotSupportedException("Host IImageProcessor is required to resize individual thumbnail frames to 320 pixels.");
                    await using var sourceFile = File.OpenRead(extracted);
                    await sourceFile.CopyToAsync(output, token).ConfigureAwait(false);
                }
            }
            finally { ThumbnailFiles.DeleteFile(extracted); }
        }
        return result;
    }

    public sealed record BifIndex(int FrameCount, uint TimestampMultiplierMs, long[] TimestampMilliseconds, uint[] Offsets);

    /// <summary>Validate actual BIF bytes, not a filename: v0 header, complete index, monotonic timestamps, JPEG boundaries.</summary>
    public static BifIndex ReadIndex(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 72 || stream.Length > uint.MaxValue) throw new InvalidDataException("BIF size is invalid.");
        if (!reader.ReadBytes(8).SequenceEqual(new byte[] { 137, 66, 73, 70, 13, 10, 26, 10 })) throw new InvalidDataException("BIF magic is invalid.");
        if (reader.ReadUInt32() != 0) throw new InvalidDataException("Unsupported BIF version.");
        var count = reader.ReadUInt32();
        var multiplier = reader.ReadUInt32();
        if (multiplier == 0) multiplier = 1000; // BIF v0 default.
        if (count == 0 || count > 1_000_000 || 64L + 8L * (count + 1) > stream.Length) throw new InvalidDataException("BIF image index is invalid.");
        if (reader.ReadBytes(44).Any(b => b != 0)) throw new InvalidDataException("BIF reserved header bytes are nonzero.");
        var timestamps = new long[count];
        var offsets = new uint[count + 1];
        for (var i = 0; i <= count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timestamp = reader.ReadUInt32();
            offsets[i] = reader.ReadUInt32();
            if (offsets[i] > stream.Length || (i == 0 ? offsets[i] != 64L + 8L * (count + 1) : offsets[i] <= offsets[i - 1]))
                throw new InvalidDataException("BIF image offsets are invalid.");
            if (i == count)
            {
                if (timestamp != uint.MaxValue || offsets[i] != stream.Length) throw new InvalidDataException("BIF terminal index is invalid.");
            }
            else
            {
                if (timestamp == uint.MaxValue) throw new InvalidDataException("BIF timestamp collides with sentinel.");
                timestamps[i] = checked((long)timestamp * multiplier);
                if (i > 0 && timestamps[i] <= timestamps[i - 1]) throw new InvalidDataException("BIF timestamps are not increasing.");
            }
        }
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offsets[i + 1] - offsets[i] < 4) throw new InvalidDataException("BIF image payload is empty.");
            stream.Position = offsets[i];
            if (reader.ReadByte() != 255 || reader.ReadByte() != 216) throw new InvalidDataException("BIF image is not JPEG.");
            stream.Position = offsets[i + 1] - 2;
            if (reader.ReadByte() != 255 || reader.ReadByte() != 217) throw new InvalidDataException("BIF image is truncated.");
        }
        return new BifIndex((int)count, multiplier, timestamps, offsets);
    }
}
