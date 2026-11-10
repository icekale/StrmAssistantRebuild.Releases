using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StrmAssistantRebuild.Common;

/// <summary>Reconstructed TMDB detail layout, not a claim of original plugin disk compatibility.</summary>
public sealed class MovieDbResponseCache
{
    public sealed record Key(string Kind, int Id, int? Season, int? Episode, string Language)
    {
        public static Key? Create(string kind, string? id, string? language, int? season = null, int? episode = null)
        {
            if (id == null || id.Length > 10 || !id.All(c => c is >= '0' and <= '9') ||
                !int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1) return null;
            if (kind is not ("movie" or "series" or "season" or "episode") ||
                (kind is "season" or "episode") != season.HasValue || (kind == "episode") != episode.HasValue ||
                season is < 0 or > 10000 || episode is < 1 or > 100000) return null;
            language ??= "";
            if (language.Length > 35 || language.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')) ||
                language.StartsWith('-') || language.EndsWith('-')) return null;
            return new(kind, number, season, episode, language.ToLowerInvariant());
        }
        public string FileName => Kind + "-" + Id.ToString(CultureInfo.InvariantCulture) +
            (Season.HasValue ? "-s" + Season.Value.ToString(CultureInfo.InvariantCulture) : "") +
            (Episode.HasValue ? "-e" + Episode.Value.ToString(CultureInfo.InvariantCulture) : "") + "-" + (Language.Length == 0 ? "_default" : Language) + ".json";
    }
    private readonly EmbyPathPolicy paths;
    private readonly string root;
    public int MaxBytes { get; }
    public long MaxTotalBytes { get; }
    public TimeSpan MaxAge { get; }
    // ponytail: bounded cache and one writer per process; no unbounded per-key lock registry.
    private static readonly SemaphoreSlim Writer = new(1, 1);
    private sealed record Entry(int Version, Key Key, DateTimeOffset FetchedAt, JsonElement Data);
    public MovieDbResponseCache(EmbyPathPolicy paths, string root, int maxBytes = 2 * 1024 * 1024, long maxTotalBytes = 64 * 1024 * 1024, TimeSpan? maxAge = null)
    {
        if (!Path.IsPathFullyQualified(root)) throw new IOException("Cache root must be absolute.");
        this.paths = paths; this.root = paths.RequireWritablePath(root);
        MaxBytes = maxBytes; MaxTotalBytes = maxTotalBytes; MaxAge = maxAge ?? TimeSpan.FromDays(7);
        if (maxBytes < 1 || maxTotalBytes < maxBytes || MaxAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxBytes));
    }
    public string FilePath(Key key, bool isOverride = false)
    {
        if (Key.Create(key.Kind, key.Id.ToString(CultureInfo.InvariantCulture), key.Language, key.Season, key.Episode) != key)
            throw new IOException("Invalid cache identity.");
        return paths.RequireWritablePath(Path.Combine(root, "tmdb", isOverride ? "override" : "cache", key.FileName));
    }
    public JsonElement? Read(Key key, bool isOverride, CancellationToken token) => Read(key, isOverride, token, out _);
    internal JsonElement? Read(Key key, bool isOverride, CancellationToken token, out string? seriesOriginalLanguage)
    {
        seriesOriginalLanguage = null;
        token.ThrowIfCancellationRequested();
        var target = FilePath(key, isOverride);
        if (!File.Exists(target)) return null;
        using var file = new FileStream(paths.RequireWritablePath(target), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > MaxBytes) return null;
        using var bytes = new MemoryStream();
        var buffer = new byte[Math.Min(MaxBytes, 8192)];
        int count;
        while ((count = file.Read(buffer, 0, Math.Min(buffer.Length, MaxBytes - (int)bytes.Length + 1))) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (bytes.Length + count > MaxBytes) return null;
            bytes.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(bytes.ToArray());
        var data = document.RootElement;
        if (!isOverride)
        {
            var entry = data.Deserialize<Entry>();
            if (entry?.Version != 1 || entry.Key != key || entry.FetchedAt > DateTimeOffset.UtcNow.AddMinutes(5) ||
                DateTimeOffset.UtcNow - entry.FetchedAt > MaxAge) return null;
            data = entry.Data;
        }
        if (!Matches(data, key)) return null;
        // Optional scalar only: malformed poster metadata never changes native DTO cache validity.
        var name = isOverride ? "original_language" : "SeriesOriginalLanguage";
        var root = document.RootElement;
        if (key.Kind == "series" && root.EnumerateObject().Count(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == 1 &&
            root.TryGetProperty(name, out var language) && language.ValueKind == JsonValueKind.String && language.GetString() is { Length: >= 2 and <= 35 } text)
            seriesOriginalLanguage = text;
        return data.Clone();
    }
    public static bool Matches(JsonElement data, Key key)
    {
        if (data.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in data.EnumerateObject()) if (!names.Add(property.Name)) return false;
        static int? Number(JsonElement value, string name) => value.TryGetProperty(name, out var p) && p.TryGetInt32(out var n) ? n : null;
        var id = Number(data, "id");
        if (id is null or < 1 || (key.Kind is "movie" or "series") && id != key.Id) return false;
        if (key.Season.HasValue && Number(data, "season_number") != key.Season) return false;
        if (key.Episode.HasValue && Number(data, "episode_number") != key.Episode) return false;
        // Season/episode TMDB DTOs omit their parent series id. An optional explicit claim must agree.
        if (data.TryGetProperty("show_id", out _) && Number(data, "show_id") != key.Id ||
            data.TryGetProperty("series_id", out _) && Number(data, "series_id") != key.Id) return false;
        return true;
    }
    public Task WriteAsync<T>(Key key, T value, CancellationToken token, Func<bool>? stillActive = null) => WriteAsync(key, value, token, stillActive, null);
    internal async Task WriteAsync<T>(Key key, T value, CancellationToken token, Func<bool>? stillActive, string? seriesOriginalLanguage)
    {
        token.ThrowIfCancellationRequested();
        // Serialize to a capped stream so an already materialized native DTO never causes an unbounded cache copy.
        using var bytes = new BoundedMemoryStream(MaxBytes);
        using (var json = new Utf8JsonWriter(bytes))
        {
            json.WriteStartObject(); json.WriteNumber("Version", 1); json.WritePropertyName("Key"); JsonSerializer.Serialize(json, key);
            json.WriteString("FetchedAt", DateTimeOffset.UtcNow); json.WritePropertyName("Data"); JsonSerializer.Serialize(json, value);
            if (key.Kind == "series" && seriesOriginalLanguage != null) json.WriteString("SeriesOriginalLanguage", seriesOriginalLanguage);
            json.WriteEndObject(); json.Flush();
        }
        using var document = JsonDocument.Parse(bytes.ToArray());
        if (!Matches(document.RootElement.GetProperty("Data"), key)) return;
        await Writer.WaitAsync(token).ConfigureAwait(false);
        string? temp = null;
        try
        {
            if (stillActive?.Invoke() == false) return;
            var target = FilePath(key);
            var directory = paths.RequireWritablePath(Path.GetDirectoryName(target)!);
            Directory.CreateDirectory(directory);
            // Count every direct file (including backups/abandoned temps); never recurse or evict manual data.
            long total = 0; var entries = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 8192) throw new IOException("Cache entry count limit.");
                var checkedPath = paths.RequireWritablePath(path);
                var info = new FileInfo(checkedPath);
                if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new IOException("Unexpected cache entry.");
                total = checked(total + info.Length);
            }
            // Reserve temporary bytes before writing. Existing target becomes a single .bak on atomic replace.
            if (total > MaxTotalBytes - bytes.Length) return;
            temp = paths.RequireWritablePath(target + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
            {
                bytes.Position = 0; await bytes.CopyToAsync(output, 8192, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (stillActive?.Invoke() == false) return;
            if (File.Exists(paths.RequireWritablePath(target)))
                File.Replace(paths.RequireWritablePath(temp), paths.RequireWritablePath(target), paths.RequireWritablePath(target + ".bak"));
            else File.Move(paths.RequireWritablePath(temp), paths.RequireWritablePath(target));
            temp = null;
        }
        finally
        {
            try { if (temp != null) File.Delete(paths.RequireWritablePath(temp)); }
            finally { Writer.Release(); }
        }
    }
    private sealed class BoundedMemoryStream : MemoryStream
    {
        private readonly int maximum;
        public BoundedMemoryStream(int maximum) => this.maximum = maximum;
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        private void Check(int count) { if (Position > maximum - count) throw new IOException("Cache JSON size limit."); }
    }
}
