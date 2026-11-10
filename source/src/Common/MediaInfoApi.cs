using System.Net;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Net;
using StrmAssistantRebuild.Web.Api;
using static StrmAssistantRebuild.Options.MediaInfoExtractOptions;

namespace StrmAssistantRebuild.Common;

/// <summary>Single-item operations. The caller owns concurrency, per-item failures and progress.</summary>
public sealed class MediaInfoApi
{
    private readonly ILibraryManager library;
    private readonly IMediaSourceManager sources;
    private readonly IMediaProbeManager probe;
    private readonly IItemRepository repository;
    private readonly IFileSystem files;
    private readonly IProviderManager providers;
    public bool SupportsAlternativeRefresh => files != null && providers != null;
    private readonly string? defaultRoot;
    private readonly MediaBrowser.Controller.Drawing.IImageProcessor? imageProcessor;

    public MediaInfoApi(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager,
        IMediaProbeManager mediaProbeManager, IItemRepository itemRepository, IFileSystem fileSystem,
        IProviderManager providerManager, string? defaultJsonRoot = null,
        MediaBrowser.Controller.Drawing.IImageProcessor? imageProcessor = null)
    {
        library = libraryManager;
        sources = mediaSourceManager;
        probe = mediaProbeManager;
        repository = itemRepository;
        files = fileSystem; providers = providerManager;
        defaultRoot = defaultJsonRoot;
        this.imageProcessor = imageProcessor;
        // Retain the integration signature. Artwork uses the native decoder and explicit image DB
        // commit; SaveImage would overwrite/delete old Primary bytes before that commit.
    }

    private static Plugin Host => Plugin.Instance ?? throw new InvalidOperationException("Plugin has not been initialized.");
    private static void RequireMedia(BaseItem item)
    {
        if (item == null || item.InternalId <= 0 || item is not (Video or Audio) || string.IsNullOrWhiteSpace(item.Path))
            throw new ArgumentException("Select one persisted video or audio item with a path.");
    }

    public BaseItem ResolveItem(string? id, string? path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BaseItem? item;
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (!(Guid.TryParse(id, out _) || (long.TryParse(id, out var number) && number > 0)))
                throw new ArgumentException("A valid item Id is required.");
            item = library.GetItemById(id);
        }
        else if (!string.IsNullOrWhiteSpace(path)) item = library.FindByPath(path, false);
        else throw new ArgumentException("Id or exact Path is required; implicit whole-library selection is disabled.");
        token.ThrowIfCancellationRequested();
        if (item == null) throw new HttpException("Media item was not found.") { StatusCode = HttpStatusCode.NotFound };
        RequireMedia(item);
        return item;
    }

    public async Task<MediaSourceInfo> ResolveMediaSourceAsync(BaseItem item, CancellationToken token)
    {
        RequireMedia(item);
        token.ThrowIfCancellationRequested();
        // Emby's public playback resolver expands STRM/shortcut targets and keeps required HTTP headers.
        // Do not parse STRM, follow redirects, invent URLs or replace the item's persisted Path here.
        var resolved = await sources.GetPlayackMediaSources(item, null, false, false, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var source = resolved?.FirstOrDefault(s => s.ItemId == item.InternalId.ToString()) ?? resolved?.FirstOrDefault();
        if (source == null || string.IsNullOrWhiteSpace(source.Path))
            throw new NotSupportedException("The official media-source resolver returned no usable source.");
#pragma warning disable CS0612 // Emby 4.10.2 still exposes this source capability; do not probe a source it marks false.
        if (!source.SupportsProbing)
            throw new NotSupportedException("The official media source does not support probing.");
#pragma warning restore CS0612
        if (source.RequiresOpening)
            throw new NotSupportedException("The official source requires a live-stream session; offline extraction is unavailable.");
        if (item.IsShortcut && IsStrm(source.Path) && string.IsNullOrWhiteSpace(source.ProbePath))
            throw new NotSupportedException("The official media-source resolver left the STRM target unresolved.");
        return source;
    }

    /// <summary>Read-only admission for the bounded alternative refresh. FullItem has no per-call
    /// LibraryOptions argument: never temporarily mutate shared settings to disable sidecar writes.</summary>
    public bool CanRefreshAlternative(BaseItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SupportsAlternativeRefresh || item.GetType() != typeof(MediaBrowser.Controller.Entities.Movies.Movie) ||
            item.InternalId <= 0 || item.Id == Guid.Empty || item.ParentId <= 0 || item.ExtraType.HasValue || item.IsVirtualItem ||
            string.IsNullOrWhiteSpace(item.PresentationUniqueKey) || !Path.IsPathRooted(item.Path) ||
            !Path.GetExtension(item.Path).Equals(".strm", StringComparison.OrdinalIgnoreCase) || !item.IsFileProtocol) return false;
        Host.Paths.RequireWritablePath(item.GetInternalMetadataPath());
        var folders = library.GetCollectionFolders(item, token);
        var options = library.GetLibraryOptions(item, folders, token);
        return AlternativeWritePolicy(options);
    }
    private static bool AlternativeWritePolicy(MediaBrowser.Model.Configuration.LibraryOptions? options) =>
        options != null && !options.SaveLocalMetadata && !options.SaveLocalThumbnailSets &&
        !options.SaveSubtitlesWithMedia && !(options.MetadataSavers?.Any() == true);

    /// <summary>Caller owns item lease/quota and revalidates selection immediately before native entry.
    /// Original 019FA refresh options, but awaited owned work replaces native QueueRefresh/item-op8 state.</summary>
    public async Task RefreshAlternativeAsync(BaseItem item, CancellationToken token, Action<CancellationToken> validate, Action validateSnapshot)
    {
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(validateSnapshot);
        if (!CanRefreshAlternative(item, token)) throw new NotSupportedException("Alternative library write policy is not eligible.");
        var folders = library.GetCollectionFolders(item, token);
        var settings = library.GetLibraryOptions(item, folders, token);
        var refresh = new MetadataRefreshOptions(new DirectoryService(Host.Logger, files))
        {
            EnableRemoteContentProbe = true, MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = false, ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
            ReplaceAllImages = false, EnableThumbnailImageExtraction = false, EnableSubtitleDownloading = false,
            Recursive = false, ForceSave = false, IsAutomated = true
        };
        validate(token);
        if (!CanRefreshAlternative(item, token) || !ReferenceEquals(settings, library.GetLibraryOptions(item, folders, token)) || !AlternativeWritePolicy(settings))
            throw new InvalidOperationException("Alternative library configuration changed.");
        validate(token);
        var finalSettings = library.GetLibraryOptions(item, folders, token);
        if (!ReferenceEquals(settings, finalSettings) || !AlternativeWritePolicy(finalSettings))
            throw new InvalidOperationException("Alternative library write settings changed.");
        validateSnapshot(); // No host callbacks after the final library read.
        token.ThrowIfCancellationRequested();
        await providers.RefreshFullItem(item, refresh, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    public async Task ExtractAsync(BaseItem item, CancellationToken token)
    {
        RequireMedia(item);
        token.ThrowIfCancellationRequested();
        var options = Host.MediaStore.GetOptions();
        if (MediaExclusionRules.Excludes(item, options.MediaInfoExcludeMediaContainers, options.MediaInfoExcludeKeywords))
            throw new NotSupportedException("Media extraction is excluded by configured container/filename rules.");
        if (options.PersistMediaInfoMode == PersistMediaInfoOption.Restore.ToString())
        {
            await RestoreAsync(item, token).ConfigureAwait(false);
            return;
        }
        var persistDefault = options.PersistMediaInfoMode == PersistMediaInfoOption.Default.ToString();
        var identity = (item.InternalId, item.Id, item.Path, item.DateModified);
        var settings = (options.PersistMediaInfoMode, options.MediaInfoJsonRootFolder, options.MediaInfoExcludeMediaContainers,
            options.MediaInfoExcludeKeywords, options.CustomProbePath, options.MediaInfoExtractionTimeoutMs, options.ClearEmbeddedMetadata);
        // Capture before the resolver reads a STRM carrier. Keep values, not mutable native metadata.
        var inputStamp = persistDefault ? ReadInputStamp(item) : null;
        bool Admit()
        {
            token.ThrowIfCancellationRequested();
            if ((item.InternalId, item.Id, item.Path, item.DateModified) != identity) return false;
            var currentStamp = inputStamp.HasValue ? ReadInputStamp(item) : null;
            var current = Host.MediaStore.GetOptions();
            return (item.InternalId, item.Id, item.Path, item.DateModified) == identity &&
                (current.PersistMediaInfoMode, current.MediaInfoJsonRootFolder, current.MediaInfoExcludeMediaContainers,
                 current.MediaInfoExcludeKeywords, current.CustomProbePath, current.MediaInfoExtractionTimeoutMs, current.ClearEmbeddedMetadata) == settings &&
                (!inputStamp.HasValue || inputStamp == currentStamp);
        }
        if (persistDefault)
        {
            if (!Admit()) throw new IOException("Media input or extraction settings changed.");
            var jsonPath = GetJsonPath(item);
            if (File.Exists(jsonPath))
            {
                var document = await MediaInfoDocument.ReadAsync(jsonPath, token).ConfigureAwait(false);
                // Legacy documents retain their DB/null rule; they are not physically authenticated.
                var fresh = document.SourceFileLength.HasValue
                    ? inputStamp.HasValue && document.ItemPath == identity.Path &&
                      document.SourceFileLength == inputStamp.Value.Length && document.SourceFileModifiedUtc == inputStamp.Value.ModifiedUtc
                    : document.SourceModifiedUtc == null || document.SourceModifiedUtc == item.DateModified;
                if (fresh)
                {
                    if (document.MediaSources.Any(bundle => MediaExclusionRules.Container(item, options.MediaInfoExcludeMediaContainers, bundle.MediaSourceInfo, true)))
                        throw new NotSupportedException("Cached media container is excluded by configuration.");
                    if (!await ImportCoreAsync(item, document.MediaSources, token, false, Admit).ConfigureAwait(false))
                        throw new IOException("Media input or extraction settings changed before cache import.");
                    return;
                }
                Host.Logger.Info("[MediaInfo] Stale JSON for item {0}; probing current source.", item.InternalId);
            }
        }
        var timeoutMs = options.MediaInfoExtractionTimeoutMs > 0 ? options.MediaInfoExtractionTimeoutMs : 60000;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutMs);
        try
        {
            var source = await ResolveMediaSourceAsync(item, timeout.Token).ConfigureAwait(false);
            var path = source.ProbePath ?? source.Path;
            var extension = Path.GetExtension(Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.AbsolutePath : path).TrimStart('.');
            if (MediaExclusionRules.Excludes(item, options.MediaInfoExcludeMediaContainers, options.MediaInfoExcludeKeywords, source))
                throw new NotSupportedException("Media extraction is excluded by configured container/keyword rules.");
            if (extension.Equals("iso", StringComparison.OrdinalIgnoreCase))
                Host.Logger.Warn("[MediaInfo] ISO item {0}: using official probe only; custom ISO/UDF mounting is not implemented.", item.InternalId);
            if (persistDefault && !Admit()) throw new IOException("Media input or extraction settings changed before probe.");
            MediaInfoBundle bundle;
            if (!string.IsNullOrWhiteSpace(options.CustomProbePath))
            {
                bundle = await new CustomProbeRunner().ProbeAsync(options.CustomProbePath, source, item is Video, timeoutMs, timeout.Token).ConfigureAwait(false);
                if (options.ClearEmbeddedMetadata && item is Video) bundle.EmbeddedInfo = null!;
                Host.Logger.Info("[MediaInfo] Custom probe completed for item {0}.", item.InternalId);
            }
            else
            {
            var result = await probe.GetMediaInfo(new MediaInfoRequest
            {
                MediaSource = source, ExtractChapters = true,
                MediaType = item is Audio ? DlnaProfileType.Audio : DlnaProfileType.Video
            }, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (result == null) throw new InvalidDataException("Official probe returned no media information.");
            bundle = new MediaInfoBundle
            {
                MediaSourceInfo = result,
                Chapters = result.Chapters?.ToList() ?? new(),
                EmbeddedInfo = options.ClearEmbeddedMetadata && item is Video ? null! : new EmbeddedInfo
                {
                    Name = result.Name, Overview = result.Overview, Track = result.IndexNumber,
                    Disc = result.ParentIndexNumber, Album = result.Album, AlbumArtists = result.AlbumArtists,
                    Artists = result.Artists, Composers = result.Composers, Genres = result.Genres
                }
            };
            }
            if (!await ImportCoreAsync(item, new[] { bundle }, timeout.Token, false, persistDefault ? Admit : null).ConfigureAwait(false))
                throw new IOException("Media input or extraction settings changed during probe.");
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Media extraction exceeded {timeoutMs} ms for item {item.InternalId}.", ex);
        }
        if (persistDefault)
            await ExportCoreAsync(item, token, inputStamp, Admit).ConfigureAwait(false);
    }

    private (long Length, DateTimeOffset ModifiedUtc)? ReadInputStamp(BaseItem item)
    {
        // ponytail: size+mtime is not a content hash; same-stat rewrites and resolved targets are outside this evidence.
        if (files == null || !Path.IsPathRooted(item.Path) || !item.IsFileProtocol) return null;
        try
        {
            var info = files.GetFileInfo(item.Path);
            return info != null && info.Exists && !info.IsDirectory && info.Length >= 0 &&
                info.LastWriteTimeUtc != default && info.LastWriteTimeUtc.Offset == TimeSpan.Zero
                ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { return null; }
    }

    private static bool IsStrm(string path) => Path.GetExtension(Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.AbsolutePath : path).Equals(".strm", StringComparison.OrdinalIgnoreCase);

    public string GetJsonPath(BaseItem item)
    {
        RequireMedia(item);
        var configured = Host.MediaStore.GetOptions().MediaInfoJsonRootFolder;
        var root = string.IsNullOrWhiteSpace(configured)
            ? defaultRoot ?? Path.Combine(Path.GetDirectoryName(Host.MediaStore.OptionsFilePath)!, "media-info")
            : configured;
        if (!Path.IsPathRooted(root)) throw new IOException("Media JSON root must be an absolute Emby data/config path.");
        var directory = Path.GetFullPath(root);
        if (Path.IsPathRooted(item.Path) && string.Equals(Path.TrimEndingDirectorySeparator(directory),
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(item.Path))!),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Media JSON must not be written beside media.");
        return Host.Paths.RequireWritablePath(Path.Combine(directory, MediaInfoDocument.FileName(item.Path)));
    }

    public async Task<List<MediaInfoBundle>> GetBundlesAsync(BaseItem item, CancellationToken token)
    {
        RequireMedia(item);
        token.ThrowIfCancellationRequested();
        // Use repository state for media fields, not a playback response with short-lived URLs/headers.
        var streams = repository.GetMediaStreams(new MediaStreamQuery { ItemId = item.InternalId }, token);
        var bundle = new MediaInfoBundle
        {
            MediaSourceInfo = new MediaSourceInfo
            {
                ItemId = item.InternalId.ToString(), Path = item.Path, Id = item.GetDefaultMediaSourceId(),
                Container = item.Container, RunTimeTicks = item.RunTimeTicks, Size = item.Size,
                Bitrate = item.TotalBitrate, MediaStreams = streams,
                Protocol = Uri.TryCreate(item.Path, UriKind.Absolute, out var itemUri) &&
                    (itemUri.Scheme == "http" || itemUri.Scheme == "https") ? MediaProtocol.Http : MediaProtocol.File
            },
            Chapters = item is Video ? repository.GetChapters(item, token) : new(),
            ZeroFingerprintConfidence = item is Episode && !string.IsNullOrEmpty(repository.GetIntroDetectionFailureResult(item.InternalId)) ? true : null,
            EmbeddedInfo = item is Audio audio ? new EmbeddedInfo
            {
                Name = audio.Name, Overview = audio.Overview, Track = audio.IndexNumber, Disc = audio.ParentIndexNumber,
                Album = audio.Album, AlbumArtists = audio.AlbumArtists, Artists = audio.Artists, Genres = audio.Genres,
                Composers = audio.Composers.Select(composer => composer.Name).ToArray()
            } : null!,
            EmbeddedImage = (await EmbeddedMediaMetadata.ExportImageAsync(item, Host.Paths, token).ConfigureAwait(false))!
        };
        var bundles = new List<MediaInfoBundle> { bundle };
        MediaInfoDocument.ValidateBundles(bundles);
        // Copies prevent serialization cleanup from changing host-owned chapter/stream instances.
        var copy = JsonSerializer.Deserialize<List<MediaInfoBundle>>(JsonSerializer.Serialize(bundles, MediaInfoDocument.JsonOptions), MediaInfoDocument.JsonOptions)!;
        foreach (var chapter in copy[0].Chapters) { chapter.ImageTag = null; chapter.ImagePath = null; }
        token.ThrowIfCancellationRequested();
        return copy;
    }

    /// <summary>Caller holds the shared item lock. Retire old cache before a DB mutation so failure cannot resurrect stale fields.</summary>
    public async Task ApplyAndPersistAsync(BaseItem item, Func<CancellationToken, Task> mutation, CancellationToken token, bool invalidateExistingCache = false)
    {
        RequireMedia(item);
        token.ThrowIfCancellationRequested();
        var persist = Host.MediaStore.GetOptions().PersistMediaInfoMode != PersistMediaInfoOption.None.ToString();
        if (persist || invalidateExistingCache) InvalidateJsonCache(item, token);
        await mutation(token).ConfigureAwait(false);
        if (persist) await ExportAsync(item, token).ConfigureAwait(false);
    }

    public string? InvalidateJsonCache(BaseItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = GetJsonPath(item);
        if (!File.Exists(path)) return null;
        var backup = Host.Paths.RequireWritablePath(path + ".invalidated-" + Guid.NewGuid().ToString("N") + ".bak");
        Host.Paths.RequireWritablePath(path);
        token.ThrowIfCancellationRequested();
        File.Move(path, backup); // Same-volume rename; do not destroy a user's previous cache snapshot.
        Host.Logger.Info("[MediaInfo] Invalidated old JSON for item {0}; backup retained.", item.InternalId);
        return backup;
    }

    public Task ExportAsync(BaseItem item, CancellationToken token) => ExportCoreAsync(item, token, null, null);

    private async Task ExportCoreAsync(BaseItem item, CancellationToken token,
        (long Length, DateTimeOffset ModifiedUtc)? inputStamp, Func<bool>? admission)
    {
        // Public export reads a DB snapshot, not a fresh probe: it never creates physical evidence.
        if (admission != null && !admission()) throw new IOException("Media input changed before cache export.");
        var bundles = await GetBundlesAsync(item, token).ConfigureAwait(false);
        if (admission != null && !admission()) throw new IOException("Media input changed while gathering cache data.");
        var document = new MediaInfoDocument
        {
            ItemId = item.InternalId.ToString(), ItemPath = item.Path, SourceModifiedUtc = item.DateModified,
            SourceFileLength = inputStamp?.Length, SourceFileModifiedUtc = inputStamp?.ModifiedUtc,
            MediaSources = bundles
        };
        await document.WriteGuardedAsync(GetJsonPath(item), Host.Paths, token, admission).ConfigureAwait(false);
        Host.Logger.Info("[MediaInfo] Exported item {0} to versioned JSON.", item.InternalId);
    }

    public async Task RestoreAsync(BaseItem item, CancellationToken token)
    {
        RequireMedia(item); token.ThrowIfCancellationRequested();
        var options = Host.MediaStore.GetOptions();
        if (MediaExclusionRules.Excludes(item, options.MediaInfoExcludeMediaContainers, options.MediaInfoExcludeKeywords))
            throw new NotSupportedException("Media restore is excluded by configured container/filename rules.");
        var document = await MediaInfoDocument.ReadAsync(GetJsonPath(item), token).ConfigureAwait(false);
        if (document.MediaSources.Any(bundle => MediaExclusionRules.Container(item, options.MediaInfoExcludeMediaContainers, bundle.MediaSourceInfo, true)))
            throw new NotSupportedException("Cached media container is excluded by configuration.");
        await ImportAsync(item, document.MediaSources, token).ConfigureAwait(false);
    }

    internal Task<bool> TryIntegrateAfterNativeRefreshAsync(BaseItem item, CancellationToken token, Func<bool> scopeValid) =>
        Host.MediaStore.GetOptions().PersistMediaInfoMode == PersistMediaInfoOption.Restore.ToString()
            ? TryRestoreAfterNativeRefreshAsync(item, token, scopeValid)
            : TryPersistAfterNativeRefreshAsync(item, token, scopeValid);

    // Native success is not fresh-probe provenance: this only creates a missing DB/media snapshot.
    private async Task<bool> TryPersistAfterNativeRefreshAsync(BaseItem item, CancellationToken token, Func<bool> scopeValid)
    {
        if (item.GetType() != typeof(MediaBrowser.Controller.Entities.Movies.Movie)) return false;
        var options = Host.MediaStore.GetOptions();
        var settings = (options.MediaInfoJsonRootFolder, options.ExclusiveControlFeatures,
            options.MediaInfoExcludeMediaContainers, options.MediaInfoExcludeKeywords);
        var identity = (item.InternalId, item.Id, item.Path, item.ParentId, item.DateCreated);
        var paths = Host.Paths;
        BaseItem? Current()
        {
            token.ThrowIfCancellationRequested();
            var row = library.GetItemById(identity.InternalId);
            var current = Host.MediaStore.GetOptions();
            return row?.GetType() == item.GetType() && row.ExtraType == item.ExtraType &&
                (row.InternalId, row.Id, row.Path, row.ParentId, row.DateCreated) == identity &&
                (item.InternalId, item.Id, item.Path, item.ParentId, item.DateCreated) == identity &&
                scopeValid() && ReferenceEquals(Host.Paths, paths) && current.ExclusiveExtract && ExclusiveMediaControl.IsHealthy &&
                ExclusiveMediaControl.ValidateFeatures(current.ExclusiveControlFeatures, out _) &&
                !ExclusiveMediaControl.TryGetNoPersistIntegrationSelection(current.ExclusiveControlFeatures, out _) &&
                current.PersistMediaInfoMode == PersistMediaInfoOption.Default.ToString() &&
                (current.MediaInfoJsonRootFolder, current.ExclusiveControlFeatures,
                 current.MediaInfoExcludeMediaContainers, current.MediaInfoExcludeKeywords) == settings ? row : null;
        }
        if (Current() == null) return false;
        return await Host.WorkQueue.TryRunSingle(identity.InternalId, async ct =>
        {
            ct.ThrowIfCancellationRequested();
            var row = Current();
            if (row == null || row.Size <= 0 || row.RunTimeTicks is not > 0 || string.IsNullOrWhiteSpace(row.Container) ||
                MediaExclusionRules.Excludes(row, settings.MediaInfoExcludeMediaContainers, settings.MediaInfoExcludeKeywords)) return false;
            var jsonPath = GetJsonPath(row);
            if (File.Exists(jsonPath)) return false; // No reader, legacy conversion, invalidation, or overwrite.
            var before = (row.DateModified, row.Size, row.RunTimeTicks, row.Container, row.TotalBitrate, row.Width, row.Height);
            var sourceId = row.GetDefaultMediaSourceId();
            var streams = JsonSerializer.Serialize(repository.GetMediaStreams(new MediaStreamQuery { ItemId = identity.InternalId }, ct), MediaInfoDocument.JsonOptions);
            var bundles = await GetBundlesAsync(row, ct).ConfigureAwait(false);
            var source = bundles.Single().MediaSourceInfo;
            bool SameMedia(BaseItem current) =>
                (current.DateModified, current.Size, current.RunTimeTicks, current.Container, current.TotalBitrate, current.Width, current.Height) == before;
            bool SameCachedStreams(BaseItem current) => current.MediaStreams == null ||
                JsonSerializer.Serialize(current.MediaStreams, MediaInfoDocument.JsonOptions) == streams;
            bool Admit()
            {
                ct.ThrowIfCancellationRequested();
                var currentRow = Current();
                if (currentRow == null || !SameMedia(currentRow)) return false;
                var currentStreams = JsonSerializer.Serialize(repository.GetMediaStreams(new MediaStreamQuery { ItemId = identity.InternalId }, ct), MediaInfoDocument.JsonOptions);
                var currentSourceId = currentRow.GetDefaultMediaSourceId();
                // Re-read identity/options/scalars after native getters; compare detached actual bundle values too.
                currentRow = Current();
                return currentRow != null && SameMedia(currentRow) && SameMedia(row) && currentSourceId == sourceId &&
                    source.ItemId == identity.InternalId.ToString() && source.Path == identity.Path && source.Id == sourceId &&
                    source.Protocol == MediaProtocol.File && source.Size == before.Size && source.RunTimeTicks == before.RunTimeTicks &&
                    source.Container == before.Container && source.Bitrate == before.TotalBitrate && currentStreams == streams &&
                    SameCachedStreams(currentRow) && SameCachedStreams(row) &&
                    JsonSerializer.Serialize(source.MediaStreams, MediaInfoDocument.JsonOptions) == streams &&
                    source.MediaStreams.Any(s => !s.IsExternal && (s.Type == MediaStreamType.Audio || s.Type == MediaStreamType.Video)) &&
                    !MediaExclusionRules.Container(currentRow, settings.MediaInfoExcludeMediaContainers, source, true) &&
                    GetJsonPath(currentRow) == jsonPath && !File.Exists(jsonPath) &&
                    Current() is { } finalRow && SameMedia(finalRow) && SameCachedStreams(finalRow);
            }
            if (!Admit()) return false;
            var document = new MediaInfoDocument
            {
                ItemId = identity.InternalId.ToString(), ItemPath = identity.Path, SourceModifiedUtc = before.DateModified,
                MediaSources = bundles // No SourceFileLength/Time pair: no carrier/STRM target probe occurred here.
            };
            // ponytail: plugin lease + bounded rechecks, not a transaction with every native DB writer.
            await document.WriteGuardedAsync(jsonPath, paths, ct, Admit, overwrite: false).ConfigureAwait(false);
            Host.Logger.Info("[MediaInfo] Created missing native media snapshot for item {0}.", identity.InternalId);
            return true;
        }, token).ConfigureAwait(false);
    }

    /// <summary>Optional ordinary native-success recovery, not the original default clear/IgnoreFileChange path.</summary>
    internal async Task<bool> TryRestoreAfterNativeRefreshAsync(BaseItem item, CancellationToken token, Func<bool> scopeValid)
    {
        var options = Host.MediaStore.GetOptions();
        var root = options.MediaInfoJsonRootFolder;
        var features = options.ExclusiveControlFeatures;
        var containers = options.MediaInfoExcludeMediaContainers; var keywords = options.MediaInfoExcludeKeywords;
        var identity = (item.InternalId, item.Id, item.Path, item.ParentId, item.DateCreated);
        var episode = item as Episode;
        var episodeIdentity = (episode?.SeriesId, episode?.InternalAlbumId);
        var audio = item as Audio;
        var audioAlbumId = audio?.InternalAlbumId;
        bool Ready()
        {
            token.ThrowIfCancellationRequested();
            var currentItem = library.GetItemById(identity.InternalId);
            var current = Host.MediaStore.GetOptions();
            // Native repository may return fresh identity-equivalent media instances.
            return currentItem?.GetType() == item.GetType() &&
                (currentItem.InternalId, currentItem.Id, currentItem.Path, currentItem.ParentId, currentItem.DateCreated) == identity &&
                (item.InternalId, item.Id, item.Path, item.ParentId, item.DateCreated) == identity &&
                (episode?.SeriesId, episode?.InternalAlbumId) == episodeIdentity &&
                ((currentItem as Episode)?.SeriesId, (currentItem as Episode)?.InternalAlbumId) == episodeIdentity &&
                audio?.InternalAlbumId == audioAlbumId && (currentItem as Audio)?.InternalAlbumId == audioAlbumId &&
                currentItem.RunTimeTicks == null && (currentItem.MediaStreams == null || currentItem.MediaStreams.Count == 0) &&
                scopeValid() && current.ExclusiveExtract && ExclusiveMediaControl.IsHealthy &&
                ExclusiveMediaControl.ValidateFeatures(current.ExclusiveControlFeatures, out _) &&
                !ExclusiveMediaControl.TryGetNoPersistIntegrationSelection(current.ExclusiveControlFeatures, out _) &&
                current.PersistMediaInfoMode == PersistMediaInfoOption.Restore.ToString() &&
                current.MediaInfoJsonRootFolder == root && current.ExclusiveControlFeatures == features &&
                current.MediaInfoExcludeMediaContainers == containers && current.MediaInfoExcludeKeywords == keywords &&
                (item.GetType() == typeof(MediaBrowser.Controller.Entities.Movies.Movie) || item.GetType() == typeof(Episode) || item.GetType() == typeof(Audio));
        }
        bool Empty() => item.RunTimeTicks == null && (item.MediaStreams == null || item.MediaStreams.Count == 0) &&
            repository.GetMediaStreams(new MediaStreamQuery { ItemId = item.InternalId }, token).Count == 0;
        if (!Ready() || !Empty()) return false; // Healthy/native-probed items never even resolve the JSON path.
        return await Host.WorkQueue.TryRunSingle(item.InternalId, async ct =>
        {
            ct.ThrowIfCancellationRequested();
            if (!Ready() || !Empty()) return false;
            var id = item.InternalId.ToString(); var path = item.Path;
            var before = (item.Size, item.RunTimeTicks, item.Container, item.TotalBitrate, item.Width, item.Height);
            bool Admit()
            {
                ct.ThrowIfCancellationRequested();
                return Ready() && Empty() && Ready() && item.InternalId.ToString() == id && item.Path == path &&
                    before == (item.Size, item.RunTimeTicks, item.Container, item.TotalBitrate, item.Width, item.Height);
            }
            if (MediaExclusionRules.Excludes(item, options.MediaInfoExcludeMediaContainers, options.MediaInfoExcludeKeywords)) return false;
            var jsonPath = GetJsonPath(item); // Only an existing hash-path under Host.Paths; never a sidecar or scan.
            if (!File.Exists(jsonPath) || !Admit()) return false;
            var document = await MediaInfoDocument.ReadAsync(jsonPath, ct).ConfigureAwait(false);
            if (document.Version != MediaInfoDocument.CurrentVersion || document.ItemId != id || document.ItemPath != path || document.MediaSources.Count != 1) return false;
            var bundle = document.MediaSources[0]; var source = bundle.MediaSourceInfo;
            if (source.ItemId != id || source.Path != path || source.Size is not > 0 || source.RunTimeTicks is not > 0 ||
                !source.MediaStreams.Any(s => !s.IsExternal && (s.Type == MediaStreamType.Audio || s.Type == MediaStreamType.Video)) ||
                (audio != null && (!source.MediaStreams.Any(s => !s.IsExternal && s.Type == MediaStreamType.Audio) ||
                    source.MediaStreams.Any(s => !s.IsExternal && s.Type == MediaStreamType.Video))) ||
                (audio == null && bundle.EmbeddedInfo != null) || !string.IsNullOrEmpty(bundle.EmbeddedImage) || bundle.ZeroFingerprintConfidence != null ||
                MediaExclusionRules.Container(item, options.MediaInfoExcludeMediaContainers, source, true) || !Admit()) return false;
            // The shared lease excludes this plugin's writers, not arbitrary host writers. Recheck at first write;
            // this is not a transaction with native code that does not participate in the plugin's lease.
            return await ImportCoreAsync(item, document.MediaSources, ct, true, Admit).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    public async Task ImportAsync(BaseItem item, IReadOnlyList<MediaInfoBundle> bundles, CancellationToken token) =>
        await ImportCoreAsync(item, bundles, token, false, null).ConfigureAwait(false);

    private async Task<bool> ImportCoreAsync(BaseItem item, IReadOnlyList<MediaInfoBundle> bundles, CancellationToken token,
        bool mediaOnly, Func<bool>? admission)
    {
        RequireMedia(item);
        token.ThrowIfCancellationRequested();
        MediaInfoDocument.ValidateBundles(bundles);
        if (bundles.Count != 1) throw new NotSupportedException("One selected library item accepts exactly one media source; select alternate versions separately.");
        EmbeddedMediaMetadata.Validate(item, bundles[0]); // Bounds before the JSON clone allocates another image string.
        // Clone before rebinding IDs/paths: imported IDs never select or overwrite another library item.
        var bundle = JsonSerializer.Deserialize<MediaInfoBundle>(JsonSerializer.Serialize(bundles[0], MediaInfoDocument.JsonOptions), MediaInfoDocument.JsonOptions)!;
        var imageBytes = string.IsNullOrEmpty(bundle.EmbeddedImage) ? null : EmbeddedMediaMetadata.Decode(bundle.EmbeddedImage);
        var source = bundle.MediaSourceInfo;
        var localChapters = !mediaOnly && item is Video ? repository.GetChapters(item, token) : new List<ChapterInfo>();
        foreach (var chapter in bundle.Chapters)
        {
            // Foreign cache paths never cross servers; preserve this server's existing image/BIF registration.
            var local = localChapters.FirstOrDefault(c => c.StartPositionTicks == chapter.StartPositionTicks && c.MarkerType == chapter.MarkerType);
            chapter.ImagePath = local?.ImagePath;
            chapter.ImageTag = local?.ImageTag;
        }
        // Probe output has no intro/credits markers or BIF-only entries. Keep host-local registrations
        // absent from the incoming chapter list; explicit chapter-clearing belongs to ClearMediaInfo.
        bundle.Chapters.AddRange(localChapters.Where(c =>
            (!string.IsNullOrEmpty(c.ImagePath) || c.MarkerType == MarkerType.IntroStart ||
             c.MarkerType == MarkerType.IntroEnd || c.MarkerType == MarkerType.CreditsStart) &&
            !bundle.Chapters.Any(incoming => incoming.StartPositionTicks == c.StartPositionTicks && incoming.MarkerType == c.MarkerType)));
        bundle.Chapters.Sort((left, right) => left.StartPositionTicks.CompareTo(right.StartPositionTicks));
        foreach (var stream in source.MediaStreams.Where(s => s.IsExternal && s.Protocol == MediaProtocol.File && !string.IsNullOrWhiteSpace(s.Path)))
        {
            if (stream.Type != MediaStreamType.Subtitle && stream.Type != MediaStreamType.Audio) continue;
            var name = Path.GetFileName(stream.Path.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") throw new InvalidDataException("External stream path is invalid.");
            if (!Path.IsPathRooted(item.Path)) throw new NotSupportedException("Local external tracks require a file-backed item.");
            stream.Path = Path.Combine(Path.GetDirectoryName(item.Path)!, name);
        }
        // Snapshot native links before setters: EnsureTaggedItemsLoaded during SaveItems must not
        // overwrite new composers, and rollback must retain IDs/provider metadata, not just names.
        var audioItem = item as Audio;
        var previous = new
        {
            Streams = repository.GetMediaStreams(new MediaStreamQuery { ItemId = item.InternalId }, token),
            item.Size, item.RunTimeTicks, item.Container, item.TotalBitrate, item.Width, item.Height,
            item.Name, item.Overview, item.GenreItems, item.IndexNumber, item.ParentIndexNumber,
            Album = audioItem?.Album, AlbumArtists = audioItem?.AlbumArtistItems,
            Artists = audioItem?.ArtistItems, Composers = audioItem?.Composers,
            Images = item.ImageInfos.Select(image => new ItemImageInfo(image)).ToArray()
        };
        token.ThrowIfCancellationRequested();
        // Decode every supplied image even for a locked item; malformed input never reaches the DB.
        var preparedImage = mediaOnly || imageBytes == null ? null : await EmbeddedMediaMetadata.PrepareImageAsync(item, imageBytes, imageProcessor, Host.Paths, token).ConfigureAwait(false);
        // Optional automatic recovery is still read-only here. A veto must not run compensation writes.
        if (admission != null)
        {
            var admitted = false;
            try { admitted = admission(); }
            finally { if (!admitted && preparedImage != null) EmbeddedMediaMetadata.DeleteOwned(preparedImage.Path, Host.Paths); }
            if (!admitted) return false;
        }
        var streamsAttempted = false; var chaptersAttempted = false; var itemAttempted = false; var imagesAttempted = false;
        var keepImage = false;
        try
        {
            token.ThrowIfCancellationRequested();
            streamsAttempted = true;
            repository.SaveMediaStreams(item.InternalId, source.MediaStreams, token);
            item.MediaStreams = source.MediaStreams;
            item.Size = source.Size.GetValueOrDefault();
            item.RunTimeTicks = source.RunTimeTicks;
            item.Container = source.Container;
            item.TotalBitrate = source.Bitrate.GetValueOrDefault();
            var video = source.MediaStreams.Where(s => s.Type == MediaStreamType.Video && !s.IsExternal)
                .OrderByDescending(s => (long)s.Width.GetValueOrDefault() * s.Height.GetValueOrDefault()).FirstOrDefault();
            item.Width = video?.Width ?? 0;
            item.Height = video?.Height ?? 0;
            if (!mediaOnly && bundle.EmbeddedInfo is { } embedded && !item.IsLocked && (item is Audio || !Host.MediaStore.GetOptions().ClearEmbeddedMetadata))
            {
                if (!item.IsFieldLocked(MetadataFields.Name) && !string.IsNullOrWhiteSpace(embedded.Name)) item.Name = embedded.Name;
                if (!item.IsFieldLocked(MetadataFields.Overview) && !string.IsNullOrWhiteSpace(embedded.Overview)) item.Overview = embedded.Overview;
                if (!item.IsFieldLocked(MetadataFields.Genres) && embedded.Genres != null) item.Genres = embedded.Genres;
                if (audioItem != null)
                {
                    audioItem.IndexNumber = embedded.Track; audioItem.ParentIndexNumber = embedded.Disc;
                    if (embedded.Album != null) audioItem.Album = embedded.Album;
                    if (!item.IsFieldLocked(MetadataFields.AlbumArtists) && embedded.AlbumArtists != null) audioItem.SetAlbumArtists(embedded.AlbumArtists);
                    if (!item.IsFieldLocked(MetadataFields.Artists) && embedded.Artists != null) audioItem.SetArtists(embedded.Artists);
                    // Audio.SupportsPeople=false: UpdatePeople is a native no-op. Use the actual
                    // public extension and SaveItems' ItemLinkType.Composers persistence instead.
                    if (!item.IsFieldLocked(MetadataFields.Composers) && embedded.Composers != null) audioItem.SetComposers(embedded.Composers);
                }
            }
            token.ThrowIfCancellationRequested();
            if (!mediaOnly && item is Video) { chaptersAttempted = true; repository.SaveChapters(item.InternalId, true, bundle.Chapters); }
            token.ThrowIfCancellationRequested();
            if (!mediaOnly && item is Episode && bundle.ZeroFingerprintConfidence == true)
                repository.LogIntroDetectionFailureFailure(item.InternalId, item.DateModified.ToUnixTimeSeconds());
            token.ThrowIfCancellationRequested();
            itemAttempted = true;
            library.UpdateItems(new List<BaseItem> { item }, null, ItemUpdateType.MetadataImport, false, false, null, token);
            if (preparedImage != null && !item.IsLocked)
            {
                token.ThrowIfCancellationRequested();
                item.SetImage(preparedImage, 0, false);
                imagesAttempted = true;
                library.UpdateImages(item); // SetImage alone never commits the images table.
                keepImage = true;
            }
        }
        catch (Exception failure)
        {
            item.MediaStreams = previous.Streams;
            item.Size = previous.Size; item.RunTimeTicks = previous.RunTimeTicks; item.Container = previous.Container;
            item.TotalBitrate = previous.TotalBitrate; item.Width = previous.Width; item.Height = previous.Height;
            if (!mediaOnly)
            {
                item.Name = previous.Name; item.Overview = previous.Overview; item.GenreItems = previous.GenreItems;
                item.IndexNumber = previous.IndexNumber; item.ParentIndexNumber = previous.ParentIndexNumber;
                item.ImageInfos = previous.Images;
            }
            if (!mediaOnly && audioItem != null)
            {
                audioItem.Album = previous.Album; audioItem.AlbumArtistItems = previous.AlbumArtists!;
                audioItem.ArtistItems = previous.Artists!; audioItem.Composers = previous.Composers!;
            }
            // Independent public repository calls are not one transaction. Best-effort compensation
            // uses a live token so cancellation cannot prevent restoration; report any failed step.
            var failures = new List<Exception> { failure };
            void Restore(Action action) { try { action(); } catch (Exception ex) { failures.Add(ex); } }
            if (imagesAttempted) Restore(() => library.UpdateImages(item));
            if (itemAttempted) Restore(() => library.UpdateItems(new List<BaseItem> { item }, null, ItemUpdateType.MetadataImport, false, false, null, CancellationToken.None));
            if (chaptersAttempted) Restore(() => repository.SaveChapters(item.InternalId, true, localChapters));
            if (streamsAttempted) Restore(() => repository.SaveMediaStreams(item.InternalId, previous.Streams, CancellationToken.None));
            if (failures.Count > 1)
            {
                keepImage = imagesAttempted; // An unresolved image commit may still reference it.
                if (!mediaOnly && Host.MediaStore.GetOptions().PersistMediaInfoMode != PersistMediaInfoOption.None.ToString())
                    Restore(() => InvalidateJsonCache(item, CancellationToken.None));
                throw new AggregateException("Media import failed; compensation was incomplete. Inspect repository state before retrying.", failures);
            }
            throw;
        }
        finally { if (preparedImage != null && !keepImage) EmbeddedMediaMetadata.DeleteOwned(preparedImage.Path, Host.Paths); }
        Host.Logger.Info(mediaOnly ? "[MediaInfo] Restored native-refresh media fields for item {0}." :
            "[MediaInfo] Imported streams/runtime/container and unlocked embedded metadata for item {0}.", item.InternalId);
        return true;
    }

    public async Task<List<MediaInfoBundle>> SyncAsync(string? id, string? path,
        IReadOnlyList<MediaInfoBundle> body, CancellationToken token)
    {
        var item = ResolveItem(id, path, token);
        return await Host.WorkQueue.RunSingle(item.InternalId, async ct =>
        {
            if (body.Count > 0)
            {
                var persist = Host.MediaStore.GetOptions().PersistMediaInfoMode != PersistMediaInfoOption.None.ToString();
                if (persist) GetJsonPath(item); // Fail an invalid configured root before any repository mutation.
                await ImportAsync(item, body, ct).ConfigureAwait(false);
                if (persist)
                {
                    try { await ExportAsync(item, ct).ConfigureAwait(false); }
                    catch
                    {
                        // DB is already committed; a failed export must not leave stale active JSON.
                        InvalidateJsonCache(item, CancellationToken.None);
                        throw;
                    }
                }
            }
            var result = await GetBundlesAsync(item, ct).ConfigureAwait(false);
            Host.Logger.Info("[MediaInfo] Sync {0} completed for item {1}.", body.Count > 0 ? "import" : "export", item.InternalId);
            return result;
        }, token).ConfigureAwait(false);
    }
}
