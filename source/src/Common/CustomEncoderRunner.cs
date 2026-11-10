using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;

namespace StrmAssistantRebuild.Common;

/// <summary>Per-operation ffmpeg selection; never changes the host encoder or its configuration.</summary>
public sealed class CustomEncoderRunner
{
    private readonly Func<string?> getExecutable;
    public CustomEncoderRunner(Func<string?> getExecutable) => this.getExecutable = getExecutable ?? throw new ArgumentNullException(nameof(getExecutable));

    // Snapshot once for the whole capture/set, so an option edit cannot mix two encoders in one BIF.
    internal string? GetExecutable()
    {
        var value = getExecutable();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    internal static Task<string> ExtractImageAsync(string? executable, IImageExtractionManager images,
        Video item, MediaSourceInfo source, IMediaMount? mount, MediaStream stream, TimeSpan position,
        string tempRoot, int timeoutMs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (executable == null)
            return images.ExtractVideoImage(source, mount?.MountedPathInfo.FullName ?? source.Path,
                ThumbnailFiles.Container(mount?.MountedContainer ?? source.Container), mount?.MountedProtocol ?? source.Protocol,
                stream, item.Video3DFormat, position, token);
        return ExtractCustomAsync(executable, source, mount, stream, item.Video3DFormat, position, tempRoot, timeoutMs, token);
    }

    internal static async Task<string> ExtractCustomAsync(string executable, MediaSourceInfo source, IMediaMount? mount,
        MediaStream stream, Video3DFormat? format3D, TimeSpan position, string tempRoot, int timeoutMs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));
        var input = mount?.MountedPathInfo.FullName ??
            (!string.IsNullOrWhiteSpace(source.EncoderPath) ? source.EncoderPath :
             !string.IsNullOrWhiteSpace(source.ProbePath) ? source.ProbePath : source.Path);
        var protocol = mount?.MountedProtocol ?? (!string.IsNullOrWhiteSpace(source.EncoderPath) ? source.EncoderProtocol ?? source.Protocol :
            !string.IsNullOrWhiteSpace(source.ProbePath) ? source.ProbeProtocol ?? source.Protocol : source.Protocol);
        if (string.IsNullOrWhiteSpace(input) || input.IndexOf('\0') >= 0)
            throw new ArgumentException("Custom encoder requires an already resolved media path.");
        var extension = Path.GetExtension(Uri.TryCreate(input, UriKind.Absolute, out var uri) ? uri.AbsolutePath : input);
        var container = mount?.MountedContainer ?? source.Container;
        if (extension.Equals(".strm", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Custom encoder requires the resolved STRM target, not the shortcut file.");
        if (extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) || extension.Equals(".img", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(container, "dvd", StringComparison.OrdinalIgnoreCase) || string.Equals(container, "bluray", StringComparison.OrdinalIgnoreCase) ||
            protocol == MediaProtocol.File && Directory.Exists(input))
            throw new NotSupportedException("Custom encoder disc/ISO image extraction is not implemented.");
        if (format3D != null || source.Video3DFormat != null || stream.ExtendedVideoType != ExtendedVideoTypes.None ||
            stream.ExtendedVideoSubType != ExtendedVideoSubTypes.None ||
            string.Equals(stream.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stream.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stream.ColorPrimaries, "bt2020", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Custom encoder HDR/DV/3D conversion is not implemented; plain JPEG is not an equivalent conversion.");
        if (stream.Type != MediaStreamType.Video || stream.Index < 0)
            throw new InvalidDataException("Custom encoder requires a resolved video stream index.");
        var destination = ThumbnailFiles.Writable(Path.Combine(tempRoot, "custom-frame-" + Guid.NewGuid().ToString("N") + ".jpg"));
        var start = CustomProbeRunner.CreateProcessStartInfo(executable, "encoder");
        start.WorkingDirectory = ThumbnailFiles.Writable(tempRoot);
        // image2pipe keeps ffmpeg output files out of its argument surface and bounds JPEG bytes too.
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", position.TotalSeconds.ToString("0.#######", CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(arg);
        CustomProbeRunner.AddHttpHeaders(start, source, protocol);
        foreach (var arg in new[] { "-i", input, "-map", "0:" + stream.Index.ToString(CultureInfo.InvariantCulture), "-an", "-sn", "-dn",
            "-frames:v", "1", "-threads", "1", "-c:v", "mjpeg", "-q:v", "2", "-f", "image2pipe", "pipe:1" })
            start.ArgumentList.Add(arg);
        Directory.CreateDirectory(start.WorkingDirectory);
        try
        {
            var jpeg = await CustomProbeRunner.RunProcessAsync(start, timeoutMs, token, "encoder").ConfigureAwait(false);
            await using (var output = new ThumbnailFiles.CancellableFileStream(destination, token))
                await output.WriteAsync(jpeg, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ThumbnailFiles.ValidateJpeg(destination);
            return destination;
        }
        catch { ThumbnailFiles.DeleteFile(destination); throw; }
    }
}
