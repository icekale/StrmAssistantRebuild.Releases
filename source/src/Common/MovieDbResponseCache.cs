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
    /// <summary>M163: also keep a plain-DTO copy in the original plugin's layout so a switch back to it reuses the cache.</summary>
    public bool WriteOriginalLayout { get; set; } = true;
    public long OriginalWrites => Interlocked.Read(ref originalWrites);
    private long originalWrites;
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
    /// <summary>
    /// M155: the original plugin's file for the same item (notes/decrypted/A----------/A----------.cs:114448, callers
    /// :52110/:53613/:54254/:54979): {root}/{cache|override}/{tmdb-movies2|tmdb-tv}/{tmdbId}/{all|series|season-N|season-N-episode-M}.json,
    /// a plain DTO with no language in its name. Read only when the rebuild's own file is missing.
    /// </summary>
    public string OriginalPath(Key key, bool isOverride)
    {
        var (folder, name) = key.Kind switch
        {
            "movie" => ("tmdb-movies2", "all"),
            "series" => ("tmdb-tv", "series"),
            "season" => ("tmdb-tv", "season-" + key.Season!.Value.ToString(CultureInfo.InvariantCulture)),
            _ => ("tmdb-tv", "season-" + key.Season!.Value.ToString(CultureInfo.InvariantCulture) + "-episode-" + key.Episode!.Value.ToString(CultureInfo.InvariantCulture))
        };
        return paths.RequireWritablePath(Path.Combine(root, isOverride ? "override" : "cache", folder, key.Id.ToString(CultureInfo.InvariantCulture), name + ".json"));
    }
    /// <summary>
    /// M161: the original's patch file (notes/decrypted/A----------/AA---------.cs:10536-11729, path :11683):
    /// {root}/override/{tmdb-movies2|tmdb-tv}/{tmdbId}/patches/{all|series|season-N|season-N-episode-M}.json.
    /// </summary>
    public string PatchPath(Key key)
    {
        var target = OriginalPath(key, true);
        return paths.RequireWritablePath(Path.Combine(Path.GetDirectoryName(target)!, "patches", Path.GetFileName(target)));
    }
    public long PatchesApplied => Interlocked.Read(ref patchesApplied);
    private long patchesApplied;
    /// <summary>
    /// Original patch semantics: every top-level property of the patch object whose name is a DTO property replaces that
    /// property of the fetched or cached DTO (the value is deserialized into the property type; unknown names are ignored).
    /// Applied for a non-empty request language only, after cache or network; the cache keeps the unpatched DTO. A patch
    /// that is unreadable, too large, not an object or would change the item identity is ignored.
    /// </summary>
    public JsonElement ApplyPatch(JsonElement data, Key key, CancellationToken token)
    {
        if (key.Language.Length == 0) return data;
        string path;
        try { path = PatchPath(key); } catch (IOException) { return data; }
        if (!File.Exists(path)) return data;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) return data;
            token.ThrowIfCancellationRequested();
            using var patch = JsonDocument.Parse(File.ReadAllBytes(path));
            if (patch.RootElement.ValueKind != JsonValueKind.Object || data.ValueKind != JsonValueKind.Object) return data;
            var merged = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject();
            foreach (var property in patch.RootElement.EnumerateObject())
                merged[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
            using var result = JsonDocument.Parse(merged.ToJsonString());
            if (!Matches(result.RootElement, key)) return data;
            Interlocked.Increment(ref patchesApplied);
            return result.RootElement.Clone();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return data; }
    }
    public long OriginalReads => Interlocked.Read(ref originalReads);
    private long originalReads;
    public JsonElement? Read(Key key, bool isOverride, CancellationToken token) => Read(key, isOverride, token, out _);
    internal JsonElement? Read(Key key, bool isOverride, CancellationToken token, out string? seriesOriginalLanguage)
    {
        seriesOriginalLanguage = null;
        token.ThrowIfCancellationRequested();
        var target = FilePath(key, isOverride);
        var original = false;
        if (!File.Exists(target))
        {
            // Original files never expired and are plain DTOs, like the rebuild's override files. Once the rebuild keeps its
            // own language-keyed file for this item, those (language-less) files are only a mirror for switching back: serving
            // them for a language the rebuild has not cached yet would mix languages (M163).
            target = OriginalPath(key, isOverride);
            if (!File.Exists(target) || HasOwnFileForItem(key, isOverride) || Mirrored.ContainsKey(ItemIdentity(key))) return null;
            original = true;
        }
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
        if (!isOverride && !original)
        {
            var entry = data.Deserialize<Entry>();
            if (entry?.Version != 1 || entry.Key != key || entry.FetchedAt > DateTimeOffset.UtcNow.AddMinutes(5) ||
                DateTimeOffset.UtcNow - entry.FetchedAt > MaxAge) return null;
            data = entry.Data;
        }
        if (!Matches(data, key)) return null;
        if (original) Interlocked.Increment(ref originalReads);
        // Optional scalar only: malformed poster metadata never changes native DTO cache validity.
        var name = isOverride || original ? "original_language" : "SeriesOriginalLanguage";
        var root = document.RootElement;
        if (key.Kind == "series" && root.EnumerateObject().Count(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == 1 &&
            root.TryGetProperty(name, out var language) && language.ValueKind == JsonValueKind.String && language.GetString() is { Length: >= 2 and <= 35 } text)
            seriesOriginalLanguage = text;
        return data.Clone();
    }
    // M163: mirrors this process wrote in the original layout. They are only for switching back to the original plugin, never
    // a substitute for the rebuild's own language-keyed entries (the original files have no language in their name).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Mirrored = new();
    private static string ItemIdentity(Key key) => key.Kind + ":" + key.Id.ToString(CultureInfo.InvariantCulture);
    /// <summary>Any language of this item already cached by the rebuild in its own layout.</summary>
    private bool HasOwnFileForItem(Key key, bool isOverride)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath(key, isOverride))!;
            if (!Directory.Exists(directory)) return false;
            var prefix = key.Kind == "season" || key.Kind == "episode"
                ? "series-" + key.Id.ToString(CultureInfo.InvariantCulture) + "-"
                : key.Kind + "-" + key.Id.ToString(CultureInfo.InvariantCulture) + "-";
            return Directory.EnumerateFiles(directory, prefix + "*.json").Any();
        }
        catch { return false; }
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
            if (WriteOriginalLayout) await WriteOriginalAsync(key, value, token).ConfigureAwait(false);
        }
        finally
        {
            try { if (temp != null) File.Delete(paths.RequireWritablePath(temp)); }
            finally { Writer.Release(); }
        }
    }
    /// <summary>
    /// M163: the original plugin wrote its cache as a plain DTO at {root}/cache/{tmdb-movies2|tmdb-tv}/{id}/{name}.json with no
    /// language in the name and no expiry (A----------/A----------.cs:114448). The rebuild keeps its own language-keyed file
    /// (read first) and additionally writes that plain copy, unchanged when the bytes already match. Override files are the
    /// user's and never written. A copy fault never fails the rebuild's own cache write.
    /// </summary>
    private async Task WriteOriginalAsync<T>(Key key, T value, CancellationToken token)
    {
        string? temp = null;
        try
        {
            using var bytes = new BoundedMemoryStream(MaxBytes);
            JsonSerializer.Serialize(new Utf8JsonWriter(bytes), value); bytes.Flush();
            using var document = JsonDocument.Parse(bytes.ToArray());
            if (!Matches(document.RootElement, key)) return;
            var target = OriginalPath(key, false);
            if (File.Exists(target))
            {
                var existing = new FileInfo(target);
                if (existing.Length > 0 && existing.Length < MaxBytes)
                {
                    using var current = File.OpenRead(target);
                    if (current.Length == bytes.Length)
                    {
                        var buffer = new byte[bytes.Length];
                        var read = 0;
                        while (read < buffer.Length)
                        {
                            var got = current.Read(buffer, read, buffer.Length - read);
                            if (got <= 0) break;
                            read += got;
                        }
                        if (read == buffer.Length && bytes.ToArray().AsSpan().SequenceEqual(buffer)) return;
                    }
                }
            }
            var directory = paths.RequireWritablePath(Path.GetDirectoryName(target)!);
            Directory.CreateDirectory(directory);
            temp = paths.RequireWritablePath(target + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
            {
                bytes.Position = 0; await bytes.CopyToAsync(output, 8192, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(paths.RequireWritablePath(target)))
                File.Replace(paths.RequireWritablePath(temp), paths.RequireWritablePath(target), paths.RequireWritablePath(target + ".bak"));
            else File.Move(paths.RequireWritablePath(temp), paths.RequireWritablePath(target));
            temp = null;
            Mirrored.TryAdd(ItemIdentity(key), 0);
            Interlocked.Increment(ref originalWrites);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* A compatibility copy never changes the rebuild's own cache. */ }
        finally { if (temp != null) { try { File.Delete(paths.RequireWritablePath(temp)); } catch { } } }
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
    /// <summary>
    /// M162: the original's "clear recent TMDB cache" (notes/decrypted/A----------/A----------.cs:116723-116790): on startup,
    /// when MetadataEnhanceOptions.ClearRecentTmdbCacheOnStartup is set, delete *.json under Emby's own cache folder
    /// {CachePath}/tmdb-movies2 and {CachePath}/tmdb-tv written in the last six hours, and (MovieDb 1.9.2.0 or later) every
    /// file under {CachePath}/httpclient. Missing folders are skipped; per-file faults are counted and swallowed.
    /// Rebuild difference: the flag is never set automatically (the original set it when it patched MovieDb.dll); a user or
    /// the settings file must set it, and it is cleared again after the run.
    /// </summary>
    public static (int Deleted, int Failed) ClearRecentTmdbHttpCache(EmbyPathPolicy paths, string? cachePath, Version? movieDbVersion, DateTimeOffset now, CancellationToken token)
    {
        var deleted = 0; var failed = 0;
        if (string.IsNullOrWhiteSpace(cachePath) || !Path.IsPathFullyQualified(cachePath)) return (0, 0);
        var cutoff = now - TimeSpan.FromHours(6);
        var roots = new List<(string Directory, string? Pattern)>
        {
            (Path.Combine(cachePath, "tmdb-movies2"), "*.json"),
            (Path.Combine(cachePath, "tmdb-tv"), "*.json")
        };
        if (movieDbVersion != null && movieDbVersion >= new Version(1, 9, 2, 0)) roots.Add((Path.Combine(cachePath, "httpclient"), null));
        foreach (var (directory, pattern) in roots)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(directory)) continue;
                IEnumerable<string> files = pattern == null
                    ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    : Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var target = paths.RequireWritablePath(file);
                        if (new FileInfo(target).LastWriteTimeUtc < cutoff.UtcDateTime) continue;
                        File.Delete(target); deleted++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { failed++; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { failed++; }
        }
        return (deleted, failed);
    }
}
