using System.Text;
using System.Text.Json;
using StrmAssistantRebuild.Options;
using StrmAssistantRebuild.Provider;

namespace StrmAssistantRebuild.Common;

/// <summary>Plugin-data-only cache. Override files are source DoubanDetailResponse JSON; never overwritten.</summary>
public sealed class DoubanCache
{
    private readonly EmbyPathPolicy paths;
    private readonly string root;
    private readonly Func<DateTimeOffset> now;
    public TimeSpan MaxAge { get; }
    public int MaxBytes { get; }
    public long MaxTotalBytes { get; }
    // ponytail: one cache writer per process, bounded metadata cache; avoid a per-subject lock registry.
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private sealed record Entry(int Version, DateTimeOffset FetchedAt, DoubanSubject Subject);
    public DoubanCache(EmbyPathPolicy paths, string root, TimeSpan? maxAge = null, int maxBytes = 2 * 1024 * 1024,
        long maxTotalBytes = 64 * 1024 * 1024, Func<DateTimeOffset>? clock = null)
    {
        this.paths = paths; this.root = paths.RequireWritablePath(root); now = clock ?? (() => DateTimeOffset.UtcNow);
        MaxAge = maxAge ?? TimeSpan.FromDays(7); MaxBytes = maxBytes; MaxTotalBytes = maxTotalBytes;
        if (MaxAge <= TimeSpan.Zero || maxBytes < 1 || maxTotalBytes < maxBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
    }
    public string FilePath(string id, bool isOverride = false)
    {
        if (!DoubanSubjectParser.IsSubjectId(id)) throw new ArgumentException("Invalid Douban subject id.");
        return paths.RequireWritablePath(Path.Combine(root, "douban", isOverride ? "override" : "cache", id + ".json"));
    }
    /// <summary>M155: original plugin layout {root}/{cache|override}/{douban-movies|douban-tv}/{id}/{all|series}.json (frodo detail JSON).</summary>
    public IEnumerable<string> OriginalPaths(string id, bool isOverride)
    {
        if (!DoubanSubjectParser.IsSubjectId(id)) throw new ArgumentException("Invalid Douban subject id.");
        var branch = isOverride ? "override" : "cache";
        yield return paths.RequireWritablePath(Path.Combine(root, branch, "douban-movies", id, "all.json"));
        yield return paths.RequireWritablePath(Path.Combine(root, branch, "douban-tv", id, "series.json"));
    }
    public async Task<DoubanSubject?> ReadAsync(string id, bool isOverride, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = FilePath(id, isOverride);
        if (!File.Exists(path))
        {
            // Original files are the frodo detail JSON (same format as rebuild overrides) and never expired.
            foreach (var original in OriginalPaths(id, isOverride))
            {
                if (!File.Exists(original) || new FileInfo(original).Length > MaxBytes) continue;
                try
                {
                    await using var stream = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, true);
                    var subject = DoubanSubjectParser.ParseDetailJson(Encoding.UTF8.GetString(await ReadBoundedAsync(stream, MaxBytes, token).ConfigureAwait(false)), id).Subject;
                    if (subject != null) return subject;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
            }
            return null;
        }
        if (new FileInfo(path).Length > MaxBytes) return null;
        try
        {
            await using var file = new FileStream(paths.RequireWritablePath(path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, true);
            var data = await ReadBoundedAsync(file, MaxBytes, token).ConfigureAwait(false);
            if (isOverride) return DoubanSubjectParser.ParseDetailJson(Encoding.UTF8.GetString(data), id).Subject;
            var entry = JsonSerializer.Deserialize<Entry>(data);
            if (entry?.Version != 1 || entry.Subject == null || entry.FetchedAt > now() + TimeSpan.FromMinutes(5) || now() - entry.FetchedAt > MaxAge ||
                !DoubanSubjectParser.MatchesIdentity(entry.Subject, id, null) || entry.Subject.People == null) return null;
            return entry.Subject;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }
    public async Task WriteAsync(DoubanSubject subject, CancellationToken token)
    {
        if (!DoubanSubjectParser.MatchesIdentity(subject, subject.Id, null)) throw new InvalidDataException("Invalid subject identity.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Entry(1, now(), subject));
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Douban cache entry exceeds size limit.");
        await WriteGate.WaitAsync(token).ConfigureAwait(false);
        string? temp = null;
        try
        {
            var target = FilePath(subject.Id);
            var directory = paths.RequireWritablePath(Path.GetDirectoryName(target)!);
            Directory.CreateDirectory(directory);
            temp = paths.RequireWritablePath(target + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(paths.RequireWritablePath(temp), paths.RequireWritablePath(target), true);
            temp = null;
            var files = new DirectoryInfo(directory).GetFiles("*.json").OrderBy(f => f.LastWriteTimeUtc).ToArray();
            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (total <= MaxTotalBytes) break;
                if (file.FullName == target) continue;
                File.Delete(paths.RequireWritablePath(file.FullName)); total -= file.Length;
            }
        }
        finally
        {
            try { if (temp != null && File.Exists(temp)) File.Delete(paths.RequireWritablePath(temp)); }
            finally { WriteGate.Release(); }
        }
    }
    public async Task<DoubanLookup> ResolveAsync(string? id, string? imdb, MetadataBuildOptions.MetadataCacheOption mode,
        Func<CancellationToken, Task<DoubanLookup>> online, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (id != null && mode != MetadataBuildOptions.MetadataCacheOption.None)
        {
            var over = await ReadAsync(id, true, token).ConfigureAwait(false);
            // Detail DTO has no IMDb field. An explicitly supplied subject id still must not contradict known IMDb.
            if (over != null && DoubanSubjectParser.MatchesIdentity(over, id, imdb)) return new(over, "override-cache");
            if (mode == MetadataBuildOptions.MetadataCacheOption.PreferCacheThenOnline)
            {
                var cached = await ReadAsync(id, false, token).ConfigureAwait(false);
                if (cached != null && DoubanSubjectParser.MatchesIdentity(cached, id, imdb)) return new(cached, "cache");
            }
        }
        var result = await online(token).ConfigureAwait(false);
        if (result.Subject != null && !DoubanSubjectParser.MatchesIdentity(result.Subject, id, imdb)) return new(null, "identity-mismatch");
        if (result.Subject != null && mode is MetadataBuildOptions.MetadataCacheOption.PreferCacheThenOnline or MetadataBuildOptions.MetadataCacheOption.PreferOnlineWithCache)
        {
            try { await WriteAsync(result.Subject, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return result with { Status = "online-cache-write-failed" }; }
        }
        // PreferOnlineWithCache means ALWAYS online in the shipped resource; no stale/error fallback.
        // UseOverrideCacheOnly means override cache ahead of online, not an offline switch.
        return result;
    }
    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(maximum + 1, 8192)];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum - (int)output.Length + 1)), token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("Douban response exceeds size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return output.ToArray();
    }
}
