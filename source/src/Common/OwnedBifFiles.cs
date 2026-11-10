using System.Security.Cryptography;
using System.Text.Json;

namespace StrmAssistantRebuild.Common;

/// <summary>Only files published by this plugin, with unchanged bytes and timestamp, may be retired.</summary>
internal static class OwnedBifFiles
{
    private const string Suffix = ".strmassistant-owner.json";
    internal sealed record Proof(int Version, long ItemId, string FileName, long Length, long ModifiedUtcTicks, string Sha256);
    internal static async Task PublishAndRetireAsync(long itemId, string current, CancellationToken token)
    {
        if (itemId <= 0) throw new InvalidDataException("BIF ownership requires a positive item id.");
        current = ThumbnailFiles.Writable(current);
        var directory = Path.GetDirectoryName(current)!;
        var proof = await Describe(itemId, current, token).ConfigureAwait(false);
        var marker = ThumbnailFiles.Writable(current + Suffix);
        var temporary = ThumbnailFiles.Writable(marker + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(proof), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, marker, true);
        }
        finally { ThumbnailFiles.DeleteFile(temporary); }

        await RetireAsync(itemId, directory, current, token).ConfigureAwait(false);
    }

    internal static Task RetireAllOwnedAsync(long itemId, string directory, CancellationToken token) =>
        RetireAsync(itemId, directory, null, token);

    private static async Task RetireAsync(long itemId, string directory, string? current, CancellationToken token)
    {
        if (itemId <= 0) throw new InvalidDataException("BIF ownership requires a positive item id.");
        token.ThrowIfCancellationRequested();
        directory = ThumbnailFiles.Writable(directory);
        foreach (var candidate in Directory.EnumerateFiles(directory, "*.bif"))
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFullPath(candidate), current, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            var claimPath = ThumbnailFiles.Writable(candidate + Suffix);
            if (!File.Exists(claimPath)) continue; // Never adopt a legacy/native file from its name alone.
            Proof? claim;
            try
            {
                if (new FileInfo(claimPath).Length > 2048) continue;
                claim = JsonSerializer.Deserialize<Proof>(await File.ReadAllTextAsync(claimPath, token).ConfigureAwait(false));
            }
            catch (JsonException) { continue; }
            if (claim is not { Version: 1 } || claim.ItemId != itemId || claim.FileName != Path.GetFileName(candidate) ||
                claim.Sha256 is not { Length: 64 } || !claim.Sha256.All(Uri.IsHexDigit)) continue;
            var info = new FileInfo(ThumbnailFiles.Writable(candidate));
            if (info.Length != claim.Length || info.LastWriteTimeUtc.Ticks != claim.ModifiedUtcTicks ||
                !string.Equals((await Describe(itemId, candidate, token).ConfigureAwait(false)).Sha256, claim.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // The claim is stale once another writer changes the file. Preserve the media and revoke ownership.
                ThumbnailFiles.DeleteFile(claimPath);
                Plugin.Instance?.Logger.Warn("BIF retirement skipped: another writer changed {0}.", candidate);
                continue;
            }
            token.ThrowIfCancellationRequested();
            var archive = ThumbnailFiles.Writable(Path.Combine(directory, "strmassistant-bif-archive"));
            Directory.CreateDirectory(archive);
            // ponytail: retain interval-switch backups; add a separately configured retention budget if measured disk use warrants it.
            var backup = ThumbnailFiles.Writable(Path.Combine(archive, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(candidate)));
            File.Move(ThumbnailFiles.Writable(candidate), backup);
            try { File.Move(claimPath, ThumbnailFiles.Writable(backup + Suffix)); }
            catch
            {
                // A failed retirement stays reversible and must not silently lose the previous set.
                File.Move(backup, ThumbnailFiles.Writable(candidate));
                throw;
            }
        }
    }
    private static async Task<Proof> Describe(long itemId, string path, CancellationToken token)
    {
        var info = new FileInfo(ThumbnailFiles.Writable(path));
        var length = info.Length; var modified = info.LastWriteTimeUtc.Ticks;
        using var sha = SHA256.Create();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var digest = Convert.ToHexString(await sha.ComputeHashAsync(stream, token).ConfigureAwait(false));
        info.Refresh();
        if (info.Length != length || info.LastWriteTimeUtc.Ticks != modified) throw new IOException("BIF changed during ownership verification.");
        return new(1, itemId, Path.GetFileName(path), length, modified, digest);
    }
}
