using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StrmAssistantRebuild.Web.Api;

namespace StrmAssistantRebuild.Common;

/// <summary>Versioned disk envelope; the HTTP contract remains List&lt;MediaInfoBundle&gt;.</summary>
public sealed class MediaInfoDocument
{
    public const int CurrentVersion = 1;
    public const long MaxDocumentBytes = 32 * 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public int Version { get; set; } = CurrentVersion;
    public string? ItemId { get; set; }
    public string? ItemPath { get; set; }
    public DateTimeOffset? SourceModifiedUtc { get; set; }
    // Only a successful extraction may attest the raw local carrier (not a resolved STRM target).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SourceFileLength { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? SourceFileModifiedUtc { get; set; }
    public DateTimeOffset WrittenUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<MediaInfoBundle> MediaSources { get; set; } = new();

    // A flat, path-hashed name is stable across servers using the same library mount.
    // It never combines user-controlled media paths with the writable JSON root.
    public static string FileName(string itemPath) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(itemPath))) + "-mediainfo.json";

    public static async Task<MediaInfoDocument> ReadAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxDocumentBytes) throw new InvalidDataException("Media JSON exceeds 32 MiB.");
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        if (json.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            throw new InvalidDataException("Media JSON must be a versioned object or legacy bundle array.");
        // The original OSS build wrote a bare array. Retain read compatibility, never write it.
        var document = json.RootElement.ValueKind == JsonValueKind.Array
            ? new MediaInfoDocument { Version = 0, MediaSources = json.RootElement.Deserialize<List<MediaInfoBundle>>(JsonOptions)! }
            : json.RootElement.Deserialize<MediaInfoDocument>(JsonOptions);
        if (document == null || (json.RootElement.ValueKind != JsonValueKind.Array &&
            (!json.RootElement.EnumerateObject().Any(p => p.Name.Equals(nameof(Version), StringComparison.OrdinalIgnoreCase)) || document.Version != CurrentVersion)))
            throw new InvalidDataException("Media JSON requires an explicit Version.");
        document.Validate();
        return document;
    }

    public void Validate()
    {
        if (Version != 0 && Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported media JSON version: {Version}.");
        if (SourceFileLength.HasValue != SourceFileModifiedUtc.HasValue || SourceFileLength < 0 ||
            (SourceFileModifiedUtc is { } modified && (modified == default || modified.Offset != TimeSpan.Zero)))
            throw new InvalidDataException("Media JSON requires a complete valid UTC carrier size/time pair.");
        ValidateBundles(MediaSources);
    }

    public static void ValidateBundles(IReadOnlyList<MediaInfoBundle>? bundles)
    {
        if (bundles == null || bundles.Count == 0 || bundles.Count > 64)
            throw new InvalidDataException("Media JSON requires 1 to 64 media sources.");
        foreach (var bundle in bundles)
        {
            var source = bundle?.MediaSourceInfo;
            if (source == null || source.RunTimeTicks is not > 0 || string.IsNullOrWhiteSpace(source.Container) ||
                source.MediaStreams == null || !source.MediaStreams.Any(s => s != null &&
                    (s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video || s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio)))
                throw new InvalidDataException("Media source requires positive runtime, container and audio/video streams.");
            if (source.MediaStreams.Count > 1024 || source.MediaStreams.Any(s => s == null || s.Index < 0) ||
                source.MediaStreams.GroupBy(s => s.Index).Any(g => g.Count() > 1))
                throw new InvalidDataException("Media stream indexes must be non-negative and unique.");
            if (source.Size < 0 || source.Bitrate < 0 || bundle!.Chapters == null ||
                bundle.Chapters.Count > 10000 || bundle.Chapters.Any(c => c == null || c.StartPositionTicks < 0))
                throw new InvalidDataException("Media size, bitrate or chapter data is invalid.");
        }
    }

    public Task WriteAsync(string path, EmbyPathPolicy paths, CancellationToken token) =>
        WriteGuardedAsync(path, paths, token, null);

    internal async Task WriteGuardedAsync(string path, EmbyPathPolicy paths, CancellationToken token, Func<bool>? commitGuard, bool overwrite = true)
    {
        Validate();
        if (Version != CurrentVersion) throw new InvalidDataException("Only the current JSON version can be written.");
        token.ThrowIfCancellationRequested();
        path = paths.RequireWritablePath(path);
        var directory = paths.RequireWritablePath(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(directory);
        var temporary = paths.RequireWritablePath(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(paths.RequireWritablePath(temporary), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, this, JsonOptions, token).ConfigureAwait(false);
                if (stream.Length > MaxDocumentBytes) throw new InvalidDataException("Media JSON exceeds 32 MiB.");
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            // Recheck after serialization/flush, before publishing a probe-attested cache.
            if (commitGuard != null && !commitGuard()) throw new IOException("Media input changed before cache publication.");
            token.ThrowIfCancellationRequested();
            // Same-directory atomic publish; create-only callers also preserve a concurrent writer's file.
            File.Move(paths.RequireWritablePath(temporary), paths.RequireWritablePath(path), overwrite);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(paths.RequireWritablePath(temporary)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Plugin.Instance?.Logger?.Warn("[MediaInfo] Temporary JSON cleanup failed: {0}", ex.Message); }
        }
    }
}
