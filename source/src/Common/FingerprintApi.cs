using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace StrmAssistantRebuild.Common;

/// <summary>Official Emby fingerprint operations, never a synthetic fingerprint or an entitlement patch.</summary>
public sealed partial class FingerprintApi
{
    private readonly ILibraryManager library;
    private readonly IFileSystem files;
    private readonly ILogger logger;
    private readonly QueueManager queue;
    private readonly Func<BaseItem, Func<CancellationToken, Task>, CancellationToken, Task>? persistMutation;
    private readonly Func<BaseItem, Func<CancellationToken, Task>, CancellationToken, Task>? resetPersistMutation;
    private readonly IApplicationPaths applicationPaths;
    private readonly IServerApplicationHost applicationHost;
    private readonly IJsonSerializer jsonSerializer;
    private readonly IFfmpegManager ffmpegManager;
    private readonly EmbyPathPolicy paths;
    private readonly string tempDirectory;
    private readonly object? manager;
    private readonly MethodInfo? create, createStaged, collect, update, supports;
    private readonly ConstructorInfo? nativeAudioConstructor, signedRunnerConstructor;
    private readonly IMediaEncoder mediaEncoder;
    private readonly IMediaMountManager mediaMounts;

    /// <summary>Separate from plain/local provider support. No native work, source resolution or logging.</summary>
    public bool CanProtectSignedInput() => IsSupported && nativeAudioConstructor != null && signedRunnerConstructor != null;

    public bool IsSupported => manager != null;
    public string SupportDetail { get; }
    internal const string VerifiedAssemblySha256 = "16874EA37E2EC8152E7F2FA9F9D7FA51A284959DCE3A918694003D009B2B2A94";
    internal static readonly Version VerifiedVersion = new(4, 10, 2, 0);

    public FingerprintApi(ILibraryManager libraryManager, IFileSystem fileSystem, IApplicationPaths paths,
        IFfmpegManager ffmpegManager, IMediaEncoder encoder, IMediaMountManager mounts,
        IJsonSerializer json, IServerApplicationHost host, ILogger logger, QueueManager queue,
        Func<BaseItem, Func<CancellationToken, Task>, CancellationToken, Task>? persistMutation = null,
        Func<BaseItem, Func<CancellationToken, Task>, CancellationToken, Task>? resetPersistMutation = null)
    {
        library = libraryManager; files = fileSystem; this.logger = logger; this.queue = queue; this.persistMutation = persistMutation;
        applicationPaths = paths; this.resetPersistMutation = resetPersistMutation;
        applicationHost = host; jsonSerializer = json; this.ffmpegManager = ffmpegManager;
        mediaEncoder = encoder; mediaMounts = mounts;
        this.paths = new EmbyPathPolicy(paths?.ProgramDataPath ?? "", paths?.ProgramSystemPath ?? "");
        tempDirectory = paths?.TempDirectory ?? "";
        try
        {
            ArgumentNullException.ThrowIfNull(paths);
            var assembly = Assembly.Load("Emby.Providers");
            var type = assembly.GetType("Emby.Providers.Markers.AudioFingerprintManager", true)!;
            var seasonInfo = assembly.GetType("Emby.Providers.Markers.SeasonFingerprintInfo", true)!;
            // Public members first. 4.10.2 hides the declaring type, not these methods.
            // No arbitrary-name search, NonPublic invocation, private field access, or original plugin load.
            var constructor = type.GetConstructor(new[] { typeof(IFileSystem), typeof(ILogger), typeof(IApplicationPaths),
                typeof(IFfmpegManager), typeof(IMediaEncoder), typeof(IMediaMountManager), typeof(IJsonSerializer), typeof(IServerApplicationHost) })
                ?? throw new MissingMethodException(type.FullName, ".ctor");
            create = Require(type, "CreateTitleFingerprint", typeof(Task<Tuple<string, bool>>), typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken));
            createStaged = Require(type, "CreateTitleFingerprint", typeof(Task), typeof(Episode), typeof(LibraryOptions), typeof(string), typeof(CancellationToken));
            collect = Require(type, "GetAllFingerprintFilesForSeason", typeof(Task<>).MakeGenericType(seasonInfo), typeof(Season), typeof(Episode[]), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken));
            update = Require(type, "UpdateSequencesForSeason", typeof(void), typeof(Season), seasonInfo, typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken));
            supports = Require(type, "IsIntroDetectionSupported", typeof(bool), typeof(Episode), typeof(LibraryOptions));
            if (host.ApplicationVersion != VerifiedVersion || !VerifyAssembly(assembly))
                throw new NotSupportedException("Fingerprint compatibility is verified only for the recorded Emby 4.10.2.0 assembly.");
            if (!VerifyMethod(constructor, "4A8A91E255AFDA0A106F7CC3E69A9194C8E3DF41004BC1E2CE87EA344E7E1CC5") ||
                !VerifyMethod(create, "2F614F77F253781CC4F375076E98CE4A20BA3A26CF7C12F9EEA7198A3F65326B") ||
                !VerifyMethod(createStaged, "7D59BCF20E907DA49CB8D47FCE6D48DED479E9C20E595DAE3B1E6DEB38524578") ||
                !VerifyMethod(collect, "EB0772B0E6D6A448189201E3515A20B88EAC1BFC6D2D8512FFF1EFEE869282D4") ||
                !VerifyMethod(update, "9AE49FCEC8669546AB999CB92BEC1CC24A0B2886A12F19F1AD75380A40C2F64C") ||
                !VerifyMethod(supports, "A693BAD7FDC62A1FD66AAFCFFCB41EF233277554F0F0EEAEF8FA76E354C5C1D0"))
                throw new NotSupportedException("Official fingerprint method IL does not match the verified 4.10.2.0 signatures.");
            manager = constructor.Invoke(new object[] { files, logger, paths, ffmpegManager, encoder, mounts, json, host });
            nativeAudioConstructor = constructor;
            signedRunnerConstructor = SignedFingerprintFfmpegManager.ProbeConstructor();
            SupportDetail = "Verified Emby 4.10.2.0 public methods on internal AudioFingerprintManager; native eligibility and 10-minute timeout retained. Native direct/collector methods exclude STRM; plugin rebuild and incremental paths separately validate local or restricted headerless HTTP(S) STRM sources. Remote content and final redirect destinations are not pinned.";
        }
        catch (Exception ex)
        {
            manager = null;
            SupportDetail = "NotSupported: official fingerprint provider unavailable: " + ex.GetBaseException().Message;
            logger.Warn("[StrmAssistantRebuild] {0}", SupportDetail);
        }
    }

    /// <summary>Existing constructor validation outcome only: does not load/construct a manager or invoke native operations.</summary>
    public bool Probe(out string detail)
    {
        detail = SupportDetail;
        return IsSupported;
    }

    private static MethodInfo Require(Type type, string name, Type result, params Type[] parameters)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, parameters, null);
        return method?.ReturnType == result && !method.ContainsGenericParameters ? method : throw new MissingMethodException(type.FullName, name);
    }
    internal static bool VerifyAssembly(Assembly assembly)
    {
        if (assembly.GetName().Name != "Emby.Providers" || assembly.GetName().Version != VerifiedVersion ||
            assembly.ManifestModule.ModuleVersionId != new Guid("29b37ec2-5f02-443c-a995-1f61c71f43c6") || string.IsNullOrEmpty(assembly.Location)) return false;
        using var stream = File.OpenRead(assembly.Location);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream)) == VerifiedAssemblySha256;
    }
    internal static bool VerifyMethod(MethodBase method, string expectedHash) =>
        method.IsPublic && method.GetMethodBody()?.GetILAsByteArray() is { } il && Convert.ToHexString(SHA256.HashData(il)) == expectedHash;
    private object? Invoke(MethodInfo method, params object[] args)
    {
        try { return method.Invoke(manager, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    public bool IsEpisodeSupported(Episode episode)
    {
        if (!IsSupported) return false;
        var options = library.GetLibraryOptions(episode);
        return options.EnableMarkerDetection && (bool)Invoke(supports!, episode, options)!;
    }
    public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode episode, CancellationToken token,
        Action<BaseItem, CancellationToken>? scopeGuard = null)
    {
        token.ThrowIfCancellationRequested();
        scopeGuard?.Invoke(episode, token);
        if (!IsSupported) throw new NotSupportedException(SupportDetail);
        if (!IsEpisodeSupported(episode)) throw new NotSupportedException("Official fingerprint eligibility failed: requires enabled marker detection, Chromaprint, complete local non-shortcut episode and sufficient runtime.");
        var options = library.GetLibraryOptions(episode);
        RequireWritePaths(episode, options);
        scopeGuard?.Invoke(episode, token);
        token.ThrowIfCancellationRequested();
        return (Task<Tuple<string, bool>>)Invoke(create!, episode, options, new DirectoryService(logger, files), token)!;
    }

    /// <summary>Opt-in reset only: retire top-level native .fp files and clear native failure state, never chapters.
    /// Requires the reset-specific cache invalidation/export wrapper. Native IntroFingerprint algorithm cache and other extradata are retained.
    /// Exceptions retain their type and carry a partial FingerprintResetReport in Data["FingerprintResetReport"].</summary>
    public Task<FingerprintResetReport> ResetEpisodeFingerprintStateAsync(Episode episode, CancellationToken token,
        Action<BaseItem, CancellationToken>? guard = null) =>
        FingerprintStateReset.RunAsync(episode, token, guard, paths, applicationPaths as IServerApplicationPaths,
            queue, resetPersistMutation, logger, repository =>
            {
                if (!IsSupported) throw new NotSupportedException(SupportDetail);
                return FingerprintStateReset.RequireNativeClear(repository);
            });

    private void RequireWritePaths(Episode episode, LibraryOptions? options = null)
    {
        // Native CreateFingerprint writes only internal metadata + host TempDirectory, never media sidecars.
        var minutes = (options ?? library.GetLibraryOptions(episode)).IntroDetectionFingerprintLength;
        if (minutes < 2 || minutes >= 30) minutes = 10;
        var filename = FormattableString.Invariant($"title_{minutes}_{episode.DateCreated.Ticks}.fp");
        paths.RequireWritablePath(Path.Combine(episode.GetInternalMetadataPath(), filename));
        paths.RequireWritablePath(Path.Combine(tempDirectory, "fingerprint-validation"));
    }

    public async Task UpdateIntroMarkerForSeason(Season season, CancellationToken token, IProgress<double>? progress = null,
        IReadOnlySet<long>? allowedItems = null, IReadOnlyList<Episode>? scopedEpisodes = null,
        Action<BaseItem, CancellationToken>? scopeGuard = null,
        Action<Season, IReadOnlyList<Episode>, CancellationToken>? scopeBatchGuard = null)
    {
        token.ThrowIfCancellationRequested();
        scopeGuard?.Invoke(season, token);
        if (!IsSupported) throw new NotSupportedException(SupportDetail);
        var options = library.GetLibraryOptions(season);
        if (scopedEpisodes != null && (scopeGuard == null || scopeBatchGuard == null || allowedItems == null || scopedEpisodes.Count > 4096 ||
            scopedEpisodes.Any(e => e.InternalAlbumId != season.InternalId || !allowedItems.Contains(e.InternalId))))
            throw new InvalidOperationException("Explicit fingerprint episodes require their physical season, allowed IDs and a scope guard.");
        var source = scopedEpisodes ?? library.GetItemList(new InternalItemsQuery { ParentIds = new[] { season.InternalId }, Recursive = true,
            IncludeItemTypes = new[] { nameof(Episode) }, GroupByPresentationUniqueKey = false }, token).OfType<Episode>().ToArray();
        foreach (var episode in source) scopeGuard?.Invoke(episode, token);
        var episodes = source.Where(e => (allowedItems == null || allowedItems.Contains(e.InternalId)) && IsEpisodeSupported(e)).ToArray();
        if (episodes.Length < 2) throw new NotSupportedException("Official sequence matching needs at least two eligible episodes in one season.");
        foreach (var episode in episodes) RequireWritePaths(episode, options);
        var directory = new DirectoryService(logger, files);
        // Task<T> is invariant; await as Task, then read its public Result with the exact verified T.
        var info = await queue.RunSingle(season.InternalId, async ct =>
        {
            scopeBatchGuard?.Invoke(season, episodes, ct);
            ct.ThrowIfCancellationRequested();
            var task = (Task)Invoke(collect!, season, episodes, options, directory, ct)!;
            await task.ConfigureAwait(false);
            return collect!.ReturnType.GetProperty("Result")!.GetValue(task)!;
        }, token, secondTier: false).ConfigureAwait(false);
        var failures = new List<Exception>();
        for (var i = 0; i < episodes.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            // Native UpdateSequencesForSeason replaces marker data. Preserve all existing markers;
            // provider/playback writes go through ChapterApi, never through this compatibility path.
            var episode = episodes[i];
            try
            {
                await queue.RunSingle(episode.InternalId, async ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    scopeGuard?.Invoke(season, ct);
                    scopeGuard?.Invoke(episode, ct);
                    if (!BaseItem.ItemRepository.GetChapters(episode, ct).Any(c => c.MarkerType != MarkerType.Chapter))
                    {
                        Task Save(CancellationToken writeToken)
                        {
                            writeToken.ThrowIfCancellationRequested();
                            RequireWritePaths(episode, options);
                            scopeBatchGuard?.Invoke(season, new[] { episode }, writeToken);
                            writeToken.ThrowIfCancellationRequested();
                            Invoke(update!, season, info, episode, options, directory, writeToken);
                            return Task.CompletedTask;
                        }
                        if (persistMutation != null) await persistMutation(episode, Save, ct).ConfigureAwait(false);
                        else await Save(ct).ConfigureAwait(false);
                    }
                    return true;
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(ex); logger.Warn("[StrmAssistantRebuild] Native sequence update item {0} failed: {1}", episode.InternalId, ex.GetBaseException().Message); }
            progress?.Report((i + 1) * 100d / episodes.Length);
        }
        if (failures.Count > 0) throw new AggregateException("Native sequence update completed with failed items or cache writes.", failures);
    }
}

/// <summary>Server-only receipt. Bytes is the observed source size; Sha256 is null until the moved backup is hashed.
/// Backups are retained, never automatically restored or deleted.</summary>
public sealed record FingerprintResetFile(string OriginalPath, string BackupPath, long Bytes, string? Sha256);
public sealed record FingerprintResetReport(long ItemId, string RunId, IReadOnlyList<FingerprintResetFile> Items,
    bool? FailureBefore, bool? FailureAfter, bool FailureCleared);

// Kept beside the API so existing source-linked compatibility tests retain the same compilation boundary.
internal static class FingerprintStateReset
{
    internal const int MaxEntries = 4096, MaxFiles = 256;
    internal const long MaxFileBytes = 32L * 1024 * 1024, MaxTotalBytes = 256L * 1024 * 1024;

    internal static Action<long, long> RequireNativeClear(IItemRepository repository)
    {
        var type = repository.GetType();
        var assembly = type.Assembly;
        if (type.FullName != "Emby.Server.Implementations.Data.SqliteItemRepository" ||
            assembly.GetName().Name != "Emby.Server.Implementations" || assembly.GetName().Version != FingerprintApi.VerifiedVersion ||
            assembly.ManifestModule.ModuleVersionId != new Guid("2009e43d-fa42-4e57-a902-2784a8306072") || string.IsNullOrEmpty(assembly.Location))
            throw new NotSupportedException("Fingerprint reset requires the verified native 4.10.2.0 repository.");
        using (var stream = File.OpenRead(assembly.Location))
        using (var hash = SHA256.Create())
            if (Convert.ToHexString(hash.ComputeHash(stream)) != "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F")
                throw new NotSupportedException("Fingerprint reset repository assembly differs from the verified artifact.");
        MethodInfo Require(string name, Type result, string hash, params Type[] parameters)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, parameters, null);
            return method?.ReturnType == result && !method.ContainsGenericParameters && FingerprintApi.VerifyMethod(method, hash)
                ? method : throw new NotSupportedException("Fingerprint reset repository method differs: " + name);
        }
        var clear = Require("ClearItemExtradata", typeof(void), "72706D435FF8E0ADEDC425DEA71A4A3FFFA06D0880C963DD10085D659A0111C5", typeof(long), typeof(long));
        Require("GetExtradataTypeId", typeof(long), "A8AF7337CCE66B2A4DFE3D2AEB56C14F8BED9209515EE7C60D9EF1F1011FEA55", typeof(string));
        Require("GetIntroDetectionFailureResult", typeof(string), "B2E9BFA3F6BF55CF7D056A79CE84B510AF11783AEFECBF5EB711E749AEB54729", typeof(long));
        // The concrete public clear deletes one (ItemId, ExtradataTypeId) row. SetItemExtradata(null) does not delete it.
        return (itemId, typeId) =>
        {
            try { clear.Invoke(repository, new object[] { itemId, typeId }); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        };
    }

    internal static async Task<FingerprintResetReport> RunAsync(Episode episode, CancellationToken token,
        Action<BaseItem, CancellationToken>? guard, EmbyPathPolicy paths, IServerApplicationPaths? hostPaths,
        QueueManager queue, Func<BaseItem, Func<CancellationToken, Task>, CancellationToken, Task>? persist,
        ILogger logger, Func<IItemRepository, Action<long, long>> requireClear)
    {
        var itemId = episode?.InternalId ?? 0;
        var runId = Guid.NewGuid().ToString("N");
        var retired = new List<FingerprintResetFile>();
        bool? before = null, after = null;
        var cleared = false;
        FingerprintResetReport Report() => new(itemId, runId, Array.AsReadOnly(retired.ToArray()), before, after, cleared);
        try
        {
            ArgumentNullException.ThrowIfNull(episode);
            token.ThrowIfCancellationRequested();
            if (itemId <= 0 || episode.Id == Guid.Empty) throw new ArgumentException("Fingerprint reset requires a persisted episode ID and GUID.", nameof(episode));
            if (hostPaths == null || persist == null) throw new NotSupportedException("Fingerprint reset requires host metadata paths and a reset-specific persistence wrapper.");
            var id = episode.Id;
            var mediaPath = episode.Path;
            var created = episode.DateCreated;
            var root = Path.GetFullPath(hostPaths.InternalMetadataPath);
            var guid = id.ToString("N");
            var directory = Path.GetFullPath(Path.Combine(root, "library", guid[..2], guid));
            var repository = BaseItem.ItemRepository;
            var clear = requireClear(repository);
            bool? directoryExists = null;
            void ValidateFrozen(CancellationToken ct, CancellationToken writeToken)
            {
                ct.ThrowIfCancellationRequested(); writeToken.ThrowIfCancellationRequested();
                if (episode.InternalId != itemId || episode.Id != id || episode.Path != mediaPath || episode.DateCreated != created ||
                    !ReferenceEquals(repository, BaseItem.ItemRepository))
                    throw new InvalidOperationException("Fingerprint reset episode identity changed.");
                paths.RequireWritablePath(directory);
                // Directory creation time can fall back to ctime on Linux and changes on our own rename.
                // This is pathname/existence validation, not an OS inode identity claim.
                if (File.Exists(directory) && !Directory.Exists(directory) ||
                    directoryExists.HasValue && Directory.Exists(directory) != directoryExists)
                    throw new IOException("Fingerprint metadata directory changed during reset.");
            }
            void Validate(CancellationToken ct, CancellationToken writeToken)
            {
                ValidateFrozen(ct, writeToken);
                guard?.Invoke(episode, writeToken);
                var currentRoot = Path.GetFullPath(hostPaths.InternalMetadataPath);
                var configuredRoot = Path.GetFullPath(BaseItem.ConfigurationManager.ApplicationPaths.InternalMetadataPath);
                var actual = Path.GetFullPath(episode.GetInternalMetadataPath());
                if (currentRoot != root || configuredRoot != root || actual != directory)
                    throw new InvalidOperationException("Fingerprint reset metadata authority changed.");
                ValidateFrozen(ct, writeToken); // Pure comparison after every external callback; no callback between hashing and move.
            }
            async Task<string> Hash(string path, long bytes, CancellationToken ct, CancellationToken writeToken)
            {
                ValidateFrozen(ct, writeToken);
                using var stream = new FileStream(paths.RequireWritablePath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                if (stream.Length != bytes) throw new IOException("Fingerprint source length changed.");
                var buffer = new byte[81920];
                var remaining = bytes;
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested(); writeToken.ThrowIfCancellationRequested();
                    var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), writeToken).ConfigureAwait(false);
                    if (count == 0) throw new IOException("Fingerprint source was truncated while hashing.");
                    hash.AppendData(buffer, 0, count); remaining -= count;
                }
                ct.ThrowIfCancellationRequested(); writeToken.ThrowIfCancellationRequested();
                if (stream.ReadByte() != -1) throw new IOException("Fingerprint source grew while hashing.");
                return Convert.ToHexString(hash.GetHashAndReset());
            }
            string[] Candidates()
            {
                if (!Directory.Exists(directory)) return Array.Empty<string>();
                var entries = Directory.EnumerateFileSystemEntries(paths.RequireWritablePath(directory)).Take(MaxEntries + 1).ToArray();
                if (entries.Length > MaxEntries) throw new IOException("Fingerprint metadata entry budget exceeded (4096).");
                var result = new List<string>();
                foreach (var entry in entries)
                {
                    if (!Path.GetExtension(entry).Equals(".fp", StringComparison.OrdinalIgnoreCase)) continue;
                    paths.RequireWritablePath(entry);
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked fingerprint files are not reset.");
                    if ((attributes & FileAttributes.Directory) != 0) continue; // No recursion, including a directory named *.fp.
                    result.Add(entry);
                    if (result.Count > MaxFiles) throw new IOException("Fingerprint file budget exceeded (256).");
                }
                return result.OrderBy(p => p, StringComparer.Ordinal).ToArray();
            }
            Validate(token, token);
            await queue.RunSingle(itemId, async ct =>
            {
                Validate(ct, ct);
                async Task Save(CancellationToken writeToken)
                {
                    Validate(ct, writeToken);
                    directoryExists = Directory.Exists(directory);
                    var candidates = Candidates().Select(p => new FileInfo(p)).ToArray();
                    long total = 0;
                    foreach (var file in candidates)
                    {
                        // Native serialized fingerprints are nonempty. Reject corrupt zero-length entries before opening,
                        // also excluding ordinary Unix FIFO/socket entries without blocking on their stream open.
                        if (file.Length <= 0 || file.Length > MaxFileBytes || (total += file.Length) > MaxTotalBytes)
                            throw new IOException("Fingerprint byte budget invalid (1 byte–32 MiB/file, 256 MiB/run).");
                        _ = file.LastWriteTimeUtc; _ = file.CreationTimeUtc;
                    }
                    Validate(ct, writeToken);
                    var originalFailure = repository.GetIntroDetectionFailureResult(itemId);
                    before = !string.IsNullOrEmpty(originalFailure);
                    after = before;
                    Validate(ct, writeToken);
                    foreach (var file in candidates)
                    {
                        Validate(ct, writeToken);
                        void CheckFile()
                        {
                            paths.RequireWritablePath(file.FullName);
                            var current = new FileInfo(file.FullName);
                            if (!current.Exists || (current.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                                current.Length != file.Length || current.LastWriteTimeUtc != file.LastWriteTimeUtc || current.CreationTimeUtc != file.CreationTimeUtc)
                                throw new IOException("Fingerprint source changed during reset: " + file.FullName);
                        }
                        CheckFile();
                        var sha = await Hash(file.FullName, file.Length, ct, writeToken).ConfigureAwait(false);
                        var backup = file.FullName + ".retired-" + runId + ".bak";
                        ValidateFrozen(ct, writeToken);
                        CheckFile();
                        paths.RequireWritablePath(backup);
                        ct.ThrowIfCancellationRequested(); writeToken.ThrowIfCancellationRequested();
                        File.Move(file.FullName, backup, false);
                        var receipt = new FingerprintResetFile(file.FullName, backup, file.Length, null);
                        retired.Add(receipt); // Record the committed move even if verification or cancellation throws next.
                        try
                        {
                            var backupHash = await Hash(backup, file.Length, ct, writeToken).ConfigureAwait(false);
                            receipt = receipt with { Sha256 = backupHash };
                            retired[^1] = receipt;
                            if (backupHash != sha) throw new IOException("Retired fingerprint content changed during reset.");
                        }
                        finally
                        {
                            try { logger.Info("[FingerprintReset] item {0} run {1} retired {2} -> {3}; bytes={4} sha256={5}", itemId, runId, receipt.OriginalPath, receipt.BackupPath, receipt.Bytes, receipt.Sha256 ?? "unverified"); }
                            catch { /* Keep the committed receipt and original exception if the logger fails. */ }
                        }
                    }
                    Validate(ct, writeToken);
                    if (Candidates().Length != 0) throw new IOException("New fingerprint files appeared during reset; failure state retained.");
                    Validate(ct, writeToken);
                    if (originalFailure != null) // An empty-but-present row is still stale native failure extradata.
                    {
                        var typeId = repository.GetExtradataTypeId("IntroDetectionExtradata");
                        Validate(ct, writeToken);
                        var currentFailure = repository.GetIntroDetectionFailureResult(itemId);
                        after = !string.IsNullOrEmpty(currentFailure);
                        ValidateFrozen(ct, writeToken);
                        if (typeId <= 0) throw new InvalidOperationException("Native intro failure extradata type was not resolved.");
                        if (!string.Equals(currentFailure, originalFailure, StringComparison.Ordinal))
                            throw new IOException("Native intro failure changed during reset; newer state retained.");
                        if (Candidates().Length != 0) throw new IOException("New fingerprint files appeared before failure clearing.");
                        ValidateFrozen(ct, writeToken);
                        after = null; // A native writer can commit and then throw; do not label old observations as post-write truth.
                        clear(itemId, typeId);
                        cleared = true;
                        Validate(ct, writeToken);
                    }
                    Validate(ct, writeToken);
                    var finalFailure = repository.GetIntroDetectionFailureResult(itemId);
                    after = !string.IsNullOrEmpty(finalFailure);
                    ValidateFrozen(ct, writeToken);
                    if (finalFailure != null) throw new IOException("Native intro failure row remains after reset.");
                }
                await persist(episode, Save, ct).ConfigureAwait(false);
                ValidateFrozen(ct, ct);
                return true;
            }, token).ConfigureAwait(false);
            logger.Info("[FingerprintReset] item {0} run {1} completed; files={2} failureCleared={3}", itemId, runId, retired.Count, cleared);
            return Report();
        }
        catch (Exception ex)
        {
            ex.Data["FingerprintResetReport"] = Report();
            try { logger.Warn("[FingerprintReset] item {0} run {1} incomplete; retired={2} failureCleared={3}; backups retained: {4}", itemId, runId, retired.Count, cleared, ex.Message); }
            catch { /* Preserve the original failure/cancellation and its receipt even when logging fails. */ }
            throw;
        }
    }
}
