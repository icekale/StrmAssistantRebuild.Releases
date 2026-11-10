using System.Globalization;
using System.Reflection;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using StrmAssistantRebuild.Options;
using Display = StrmAssistantRebuild.Options.MultipleVersionsOptions.MultiVersionDisplayOption;
using Sort = StrmAssistantRebuild.Options.MultipleVersionsOptions.MultiVersionSortOption;

namespace StrmAssistantRebuild.Common;

/// <summary>Response-only names/order. Never re-query a library or alter a shared media source.</summary>
public static class MultiVersionProjection
{
    internal sealed record Settings(Display Display, Sort Sort);
    // The native copy constructor omits ItemId, ServerId, default stream indexes and more.
    // MemberwiseClone preserves every field (including future fields); nested values are read-only here.
    private static readonly Func<object, object> Copy = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<object, object>>();
    internal static T Clone<T>(T value) where T : class => (T)Copy(value);

    internal static Settings? Capture(ExperienceEnhanceOptions? options)
    {
        if (options?.BeautifyMultiVersion != true) return null;
        var display = Enum.IsDefined(typeof(Display), options.MultiVersionDisplayPreference) ? options.MultiVersionDisplayPreference : Display.SystemDefault;
        var sort = Enum.IsDefined(typeof(Sort), options.MultiVersionReorderPreference) ? options.MultiVersionReorderPreference : Sort.SystemDefault;
        return display == Display.SystemDefault && sort == Sort.SystemDefault ? null : new(display, sort);
    }

    internal static MediaSourceInfo[] Project(IEnumerable<MediaSourceInfo> sources, Settings settings)
    {
        var copies = sources.Select(source =>
        {
            if (source == null) return source!;
            var copy = Clone(source);
            var name = settings.Display switch
            {
                Display.FileNameWithoutExtension => FileName(source.Path),
                Display.MediaInfo => MediaInfo(source, false),
                Display.MediaInfoExtended => MediaInfo(source, true),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(name)) copy.Name = name;
            return copy;
        }).ToArray();
        // LINQ ordering is stable. Known quality requires BOTH source bitrate and video width,
        // matching decompiled 47506–47558, 47652–47693 and 47802–47835; unknowns trail in original order.
        var known = copies.Where(KnownQuality);
        return settings.Sort switch
        {
            Sort.HighToLowQuality => known.OrderByDescending(s => Video(s)!.Width).ThenByDescending(s => s.Bitrate).Concat(copies.Where(s => !KnownQuality(s))).ToArray(),
            Sort.LowToHighQuality => known.OrderBy(s => Video(s)!.Width).ThenBy(s => s.Bitrate).Concat(copies.Where(s => !KnownQuality(s))).ToArray(),
            Sort.PathAscending => copies.OrderBy(s => s?.Path, StringComparer.Ordinal).ToArray(),
            Sort.PathDescending => copies.OrderByDescending(s => s?.Path, StringComparer.Ordinal).ToArray(),
            _ => copies
        };
    }

    private static MediaStream? Video(MediaSourceInfo? source) => source?.MediaStreams?.FirstOrDefault(s => s?.Type == MediaStreamType.Video);
    private static bool KnownQuality(MediaSourceInfo? source) => Video(source)?.Width.HasValue == true && source!.Bitrate.HasValue;

    internal static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // Use the authorized native response's path, not a second library/source enumeration.
        // Strip URI query/fragment BEFORE choosing basename; a token may contain '/' or '.'.
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
            path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        else if (path.Contains("://", StringComparison.Ordinal))
            path = path.Split('?', '#')[0];
        path = path.Replace('\\', '/');
        var name = path[(path.LastIndexOf('/') + 1)..];
        // Decode only after stripping URL components; encoded token delimiters stay excluded too.
        name = Uri.UnescapeDataString(name);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static string? MediaInfo(MediaSourceInfo source, bool extended)
    {
        // Display uses the largest valid video; source-quality ordering keeps its separate native contract.
        // MaxBy is stable for equal areas and the long multiplication avoids 32-bit overflow.
        var video = source.MediaStreams?.Where(s => s?.Type == MediaStreamType.Video && s.Width is > 0 && s.Height is > 0)
            .MaxBy(s => (long)s.Width!.Value * s.Height!.Value);
        if (video == null) return null; // No valid video: preserve the native Name rather than inventing partial metadata.
        var audio = source.MediaStreams?.FirstOrDefault(s => s?.Type == MediaStreamType.Audio && s.Index == source.DefaultAudioStreamIndex)
            ?? source.MediaStreams?.FirstOrDefault(s => s?.Type == MediaStreamType.Audio);
        var parts = new List<string>();
        parts.Add($"{video.Width}x{video.Height}");
        if (!string.IsNullOrWhiteSpace(video.Codec)) parts.Add(video.Codec.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(audio?.Codec)) parts.Add(audio.Codec.ToUpperInvariant() + (audio.Channels is > 0 ? " " + audio.Channels + "ch" : ""));
        if (source.Bitrate is > 0) parts.Add((source.Bitrate.Value / 1000000d).ToString("0.##", CultureInfo.InvariantCulture) + " Mbps");
        if (extended)
        {
            if (!string.IsNullOrWhiteSpace(source.Container)) parts.Add(source.Container.ToUpperInvariant());
            if (video?.BitDepth is > 0) parts.Add(video.BitDepth + "bit");
            if (video?.RealFrameRate is > 0) parts.Add(video.RealFrameRate.Value.ToString("0.###", CultureInfo.InvariantCulture) + " fps");
        }
        if (video != null && (extended || video.ExtendedVideoType != ExtendedVideoTypes.None) &&
            Enum.IsDefined(typeof(ExtendedVideoTypes), video.ExtendedVideoType))
        {
            var range = video.GenerateVideoRange(true);
            if (!string.IsNullOrWhiteSpace(range)) parts.Add(range);
            if (extended && video.ExtendedVideoType == ExtendedVideoTypes.DolbyVision &&
                Enum.IsDefined(typeof(ExtendedVideoSubTypes), video.ExtendedVideoSubType) &&
                video.ExtendedVideoSubType.ToString().StartsWith("DoviProfile", StringComparison.Ordinal))
                parts.Add(video.ExtendedVideoSubTypeDescription);
        }
        if (extended)
        {
            if (video != null && source.Video3DFormat.HasValue && Enum.IsDefined(typeof(Video3DFormat), source.Video3DFormat.Value))
                parts.Add("3D " + source.Video3DFormat.Value);
            if (!string.IsNullOrWhiteSpace(audio?.Language)) parts.Add(audio.Language);
            if (source.Size is > 0) parts.Add((source.Size.Value / 1073741824d).ToString("0.##", CultureInfo.InvariantCulture) + " GiB");
        }
        // Deliberate reconstruction choice: original obfuscated formatting literals are unknown.
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
