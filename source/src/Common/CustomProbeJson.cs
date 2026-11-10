using System.Globalization;
using System.Text;
using System.Text.Json;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using StrmAssistantRebuild.Web.Api;

namespace StrmAssistantRebuild.Common;

/// <summary>ffprobe uses snake_case, seconds and numeric strings, not MediaSourceInfo's JSON contract.</summary>
internal static class CustomProbeJson
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static MediaInfoBundle Parse(string json, MediaSourceInfo resolvedSource, bool video)
    {
        if (resolvedSource == null) throw new ArgumentNullException(nameof(resolvedSource));
        if (json == null || Encoding.UTF8.GetByteCount(json) > CustomProbeRunner.MaxStdoutBytes)
            throw new InvalidDataException("Custom probe JSON exceeds 16 MiB.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            var format = Get(root, "format");
            var streamsJson = Get(root, "streams");
            if (root.ValueKind != JsonValueKind.Object || Get(root, "error").ValueKind != JsonValueKind.Undefined ||
                format.ValueKind != JsonValueKind.Object || streamsJson.ValueKind != JsonValueKind.Array ||
                streamsJson.GetArrayLength() > 1024)
                throw Invalid();
            // The official copy constructor preserves playback controls/protocol, but omits these IDs.
            var source = new MediaSourceInfo(resolvedSource)
            {
                ItemId = resolvedSource.ItemId, ServerId = resolvedSource.ServerId,
                RequiredHttpHeaders = new(resolvedSource.RequiredHttpHeaders ?? new(), StringComparer.OrdinalIgnoreCase),
                MediaStreams = new(), Chapters = Array.Empty<ChapterInfo>(),
                Container = Container(Text(format, "format_name"), resolvedSource.ProbePath ?? resolvedSource.Path),
                Formats = (Text(format, "format_name") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
                RunTimeTicks = Seconds(Text(format, "duration")), ContainerStartTimeTicks = Seconds(Text(format, "start_time"), true),
                Size = Long(Text(format, "size")), Bitrate = Integer(Text(format, "bit_rate")),
                DefaultAudioStreamIndex = null, DefaultSubtitleStreamIndex = null
            };
            long longest = 0;
            var indexes = new HashSet<int>();
            foreach (var input in streamsJson.EnumerateArray())
            {
                var type = Text(input, "codec_type") switch
                {
                    "video" => Flag(Get(input, "disposition"), "attached_pic") ? MediaStreamType.EmbeddedImage : MediaStreamType.Video,
                    "audio" => MediaStreamType.Audio,
                    "subtitle" => MediaStreamType.Subtitle,
                    _ => (MediaStreamType?)null
                };
                if (type == null) continue; // Data/attachment payloads are not playable tracks or images to extract.
                var index = Integer(Text(input, "index"));
                if (index == null || !indexes.Add(index.Value)) throw Invalid();
                var tags = Get(input, "tags");
                var disposition = Get(input, "disposition");
                var stream = new MediaStream
                {
                    Index = index.Value, Type = type.Value, Codec = Text(input, "codec_name"),
                    CodecTag = Text(input, "codec_tag_string"), Profile = Text(input, "profile"),
                    Language = Tag(tags, "language"), Title = Tag(tags, "title"), Comment = Tag(tags, "comment"),
                    TimeBase = Text(input, "time_base"), StreamStartTimeTicks = Seconds(Text(input, "start_time"), true),
                    BitRate = Integer(Text(input, "bit_rate")) ?? Integer(Tag(tags, "BPS")),
                    BitDepth = PositiveInteger(Text(input, "bits_per_raw_sample")) ?? PositiveInteger(Text(input, "bits_per_sample")),
                    IsDefault = Flag(disposition, "default"), IsForced = Flag(disposition, "forced"),
                    IsHearingImpaired = Flag(disposition, "hearing_impaired"),
                    IsExternal = false, Protocol = resolvedSource.ProbeProtocol ?? resolvedSource.Protocol,
                    Channels = PositiveInteger(Text(input, "channels")), SampleRate = PositiveInteger(Text(input, "sample_rate")),
                    ChannelLayout = Text(input, "channel_layout"), Width = PositiveInteger(Text(input, "width")),
                    Height = PositiveInteger(Text(input, "height")), PixelFormat = Text(input, "pix_fmt"),
                    AverageFrameRate = Rate(Text(input, "avg_frame_rate")), RealFrameRate = Rate(Text(input, "r_frame_rate")),
                    AspectRatio = Aspect(input), RefFrames = Integer(Text(input, "refs")),
                    Level = Number(Text(input, "level")), NalLengthSize = Text(input, "nal_length_size"),
                    IsInterlaced = Text(input, "field_order") is "tt" or "bb" or "tb" or "bt",
                    ColorTransfer = Text(input, "color_transfer"), ColorPrimaries = Text(input, "color_primaries"),
                    ColorSpace = Text(input, "color_space")
                };
                var sar = Ratio(Text(input, "sample_aspect_ratio"));
                stream.IsAnamorphic = sar > 0 ? sar != 1 : null;
                source.MediaStreams.Add(stream);
                if (type is MediaStreamType.Audio or MediaStreamType.Video)
                {
                    var duration = Seconds(Text(input, "duration")) ?? ScaledTicks(Text(input, "duration_ts"), Text(input, "time_base")) ??
                        ClockTicks(Tag(tags, "DURATION"));
                    if (duration > longest) longest = duration.Value;
                }
            }
            if (source.RunTimeTicks is not > 0) source.RunTimeTicks = longest > 0 ? longest : null;
            if (source.RunTimeTicks is not > 0 || string.IsNullOrWhiteSpace(source.Container) ||
                !source.MediaStreams.Any(s => s.Type == (video ? MediaStreamType.Video : MediaStreamType.Audio)))
                throw Invalid();
            if (source.Bitrate == null)
            {
                var sum = source.MediaStreams.Where(s => s.Type is MediaStreamType.Audio or MediaStreamType.Video)
                    .Sum(s => (long)s.BitRate.GetValueOrDefault());
                source.Bitrate = sum is > 0 and <= int.MaxValue ? (int)sum : null;
            }
            source.DefaultAudioStreamIndex = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)?.Index;
            source.DefaultSubtitleStreamIndex = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.IsDefault)?.Index;
            var chapters = new List<ChapterInfo>();
            var chaptersJson = Get(root, "chapters");
            if (chaptersJson.ValueKind != JsonValueKind.Undefined)
            {
                if (chaptersJson.ValueKind != JsonValueKind.Array || chaptersJson.GetArrayLength() > 10000) throw Invalid();
                foreach (var chapter in chaptersJson.EnumerateArray())
                {
                    var start = Seconds(Text(chapter, "start_time")) ?? ScaledTicks(Text(chapter, "start"), Text(chapter, "time_base"));
                    if (start == null) throw Invalid();
                    chapters.Add(new ChapterInfo
                    {
                        ChapterIndex = chapters.Count, StartPositionTicks = start.Value,
                        Name = Tag(Get(chapter, "tags"), "title") ?? $"Chapter {chapters.Count + 1}"
                    });
                }
                chapters.Sort((left, right) => left.StartPositionTicks.CompareTo(right.StartPositionTicks));
                for (var i = 0; i < chapters.Count; i++) chapters[i].ChapterIndex = i;
            }
            source.Chapters = chapters.ToArray();
            var metadata = Get(format, "tags");
            return new MediaInfoBundle
            {
                MediaSourceInfo = source, Chapters = chapters,
                EmbeddedInfo = new EmbeddedInfo
                {
                    // Bundle DTO uses legacy non-null annotations; absent tags deliberately remain absent.
                    Name = Tag(metadata, "title")!, Overview = (Tag(metadata, "description") ?? Tag(metadata, "comment"))!,
                    Album = Tag(metadata, "album")!, Track = Track(Tag(metadata, "track")), Disc = Track(Tag(metadata, "disc")),
                    Artists = Names(Tag(metadata, "artist"))!, AlbumArtists = Names(Tag(metadata, "album_artist"))!,
                    Composers = Names(Tag(metadata, "composer"))!, Genres = Names(Tag(metadata, "genre"))!
                }
            };
        }
        catch (JsonException) { throw new InvalidDataException("Custom probe returned invalid JSON."); }
        catch (OverflowException) { throw Invalid(); }
    }

    private static InvalidDataException Invalid() => new("Custom probe requires valid runtime, container, track indexes and media streams.");
    private static JsonElement Get(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var result) ? result : default;
    private static string? Text(JsonElement value, string key) => Scalar(Get(value, key));
    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "1", JsonValueKind.False => "0", _ => null
    };
    private static string? Tag(JsonElement tags, string key) => tags.ValueKind == JsonValueKind.Object
        ? tags.EnumerateObject().Where(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(p => Scalar(p.Value)).FirstOrDefault()
        : null;
    private static bool Flag(JsonElement disposition, string key) => Text(disposition, key) == "1";
    private static long? Long(string? value) => long.TryParse(value, NumberStyles.Integer, Invariant, out var number) && number >= 0 ? number : null;
    private static int? Integer(string? value) => Long(value) is { } number && number <= int.MaxValue ? (int)number : null;
    private static int? PositiveInteger(string? value) => Integer(value) is > 0 and var result ? result : null;
    private static double? Number(string? value) => double.TryParse(value, NumberStyles.Float, Invariant, out var number) && double.IsFinite(number) && number >= 0 ? number : null;
    private static decimal? Decimal(string? value) => decimal.TryParse(value, NumberStyles.Float, Invariant, out var number) ? number : null;
    private static decimal? Ratio(string? value)
    {
        var parts = value?.Split(new[] { '/', ':' });
        if (parts == null) return null;
        if (parts.Length == 1) return Decimal(parts[0]);
        return parts.Length == 2 && Decimal(parts[0]) is { } n && Decimal(parts[1]) is > 0 and var d ? n / d : null;
    }
    private static float? Rate(string? value) => Ratio(value) is > 0 and var number ? (float)number : null;
    private static long? Ticks(decimal? seconds, bool signed = false)
    {
        if (seconds == null || (!signed && seconds < 0)) return null;
        var ticks = seconds.Value * TimeSpan.TicksPerSecond;
        return ticks >= long.MinValue && ticks <= long.MaxValue ? (long)decimal.Round(ticks, 0, MidpointRounding.AwayFromZero) : null;
    }
    private static long? Seconds(string? value, bool signed = false) => Ticks(Decimal(value), signed);
    private static long? ScaledTicks(string? count, string? timeBase) => Ticks(Decimal(count) * Ratio(timeBase));
    private static long? ClockTicks(string? value)
    {
        var parts = value?.Split(':');
        return parts?.Length == 3 ? Ticks(Decimal(parts[0]) * 3600 + Decimal(parts[1]) * 60 + Decimal(parts[2])) : null;
    }
    private static int? Track(string? value) => PositiveInteger(value?.Split('/')[0]);
    private static string[]? Names(string? value) => string.IsNullOrWhiteSpace(value) ? null :
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string? Aspect(JsonElement input)
    {
        var display = Text(input, "display_aspect_ratio");
        if (Ratio(display) > 0) return display!.Replace('/', ':');
        if (PositiveInteger(Text(input, "width")) is not { } width || PositiveInteger(Text(input, "height")) is not { } height) return null;
        var sample = Text(input, "sample_aspect_ratio")?.Split(':');
        long n = width, d = height;
        if (sample?.Length == 2 && PositiveInteger(sample[0]) is { } sn && PositiveInteger(sample[1]) is { } sd) { n *= sn; d *= sd; }
        long a = n, b = d;
        while (b != 0) { var remainder = a % b; a = b; b = remainder; }
        return $"{n / a}:{d / a}";
    }
    private static string? Container(string? names, string? path)
    {
        var formats = (names ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (formats.Length == 0) return null;
        var extension = Path.GetExtension(Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.AbsolutePath : path)?.TrimStart('.').ToLowerInvariant();
        if (formats.Contains(extension, StringComparer.OrdinalIgnoreCase)) return extension;
        if (formats.Contains("matroska")) return extension == "webm" ? "webm" : "mkv";
        if (formats.Contains("mp4")) return "mp4";
        return formats[0];
    }
}
