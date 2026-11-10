using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

public sealed class MaintenanceService
{
    private readonly ILibraryManager library;
    private readonly IItemRepository repository;
    private readonly IFileSystem files;
    private readonly ILocalizationManager localization;
    private readonly IServerApplicationHost host;
    private readonly IApplicationPaths paths;
    private readonly ILogger logger;
    private readonly Func<bool> debugMode;
    private readonly Func<MediaInfoExtractOptions> mediaOptions;
    private readonly QueueManager queue;
    private readonly Func<BaseItem, Func<CancellationToken, Task>, CancellationToken, Task>? persistMutation;
    private readonly Lazy<(object Subtitle, object Audio, MethodInfo Discover, MethodInfo Supports, MethodInfo Naming, IMediaProbeManager Prober)> resolvers;
    // Emby 4.10.2 InsertMediaStreams (source 13622-13660) persists only these fields.
    // Response-only DisplayTitle/delivery state must not force a DB rewrite on every scan.
    private static readonly string[] PersistedStreamFields = ("Index Type Codec Language ChannelLayout Profile AspectRatio Path IsInterlaced BitRate Channels SampleRate " +
        "IsDefault IsForced IsHearingImpaired IsExternal Width Height AverageFrameRate RealFrameRate Level PixelFormat BitDepth IsAnamorphic RefFrames " +
        "CodecTag Comment NalLengthSize Title TimeBase ColorPrimaries ColorSpace ColorTransfer Extradata AttachmentSize MimeType Rotation ExtendedVideoType ExtendedVideoSubType").Split(' ');
    private static readonly PropertyInfo[] StreamFields = PersistedStreamFields.Select(name => typeof(MediaStream).GetProperty(name)!).ToArray();

    public MaintenanceService(ILibraryManager library, IItemRepository repository, IFileSystem files,
        ILocalizationManager localization, IServerApplicationHost host, IApplicationPaths paths, ILogger logger,
        Func<bool> debugMode, Func<MediaInfoExtractOptions> mediaOptions, QueueManager queue,
        Func<BaseItem, Func<CancellationToken, Task>, CancellationToken, Task>? persistMutation = null)
    {
        this.library = library; this.repository = repository; this.files = files; this.localization = localization;
        this.host = host; this.paths = paths; this.logger = logger; this.debugMode = debugMode;
        this.mediaOptions = mediaOptions; this.queue = queue; this.persistMutation = persistMutation;
        resolvers = new(BindResolvers);
    }

    public bool CanDeletePersons => debugMode();

    // OSS DeletePersonTask is an explicit debug-only reset of ALL person records, not orphan cleanup.
    public Task DeletePersonsAsync(CancellationToken token, IProgress<double> progress)
    {
        token.ThrowIfCancellationRequested();
        RequireDebug();
        progress.Report(0);
        var people = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { "Person" }, GroupByPresentationUniqueKey = false }, token).OfType<Person>().ToArray();
        logger.Warn("[StrmAssistantRebuild] Debug person metadata reset: {0} records; media files retained.", people.Length);
        var failures = new List<Exception>();
        for (var i = 0; i < people.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            RequireDebug();
            try { library.DeleteItem(people[i], new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failures.Add(ex);
                logger.Warn("[StrmAssistantRebuild] Person reset failed for {0}: {1}", people[i].InternalId, ex.Message);
            }
            progress.Report((i + 1) * 100d / people.Length);
        }
        token.ThrowIfCancellationRequested();
        progress.Report(100);
        if (failures.Count > 0) throw new AggregateException("Person reset completed with failed records; see per-item log.", failures);
        return Task.CompletedTask;
    }

    private void RequireDebug()
    {
        if (!CanDeletePersons) throw new InvalidOperationException("Person reset not executed: DebugMode is off.");
    }

    public async Task ScanExternalTracksAsync(CancellationToken token, IProgress<double> progress)
    {
        token.ThrowIfCancellationRequested();
        _ = resolvers.Value; // A missing contract is a visible failure, never a successful empty scan.
        var items = MediaTaskSelection.Select(library, mediaOptions(), token, videoOnly: true);
        var result = await queue.RunBatch(items, ScanExternalTracksAsync, progress, token, secondTier: true,
            itemKey: item => item.InternalId).ConfigureAwait(false);
        if (result.Failed > 0) throw new InvalidOperationException($"External-track scan completed with {result.Failed} failed items.");
    }

    internal async Task ScanExternalTracksAsync(BaseItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!item.IsFileProtocol || string.IsNullOrWhiteSpace(item.Path)) return;
        var binding = resolvers.Value;
        var options = library.GetLibraryOptions(item);
        var naming = Invoke(binding.Naming, library, Array.Empty<object>())!;
        var directory = new DirectoryService(logger, files);
        var current = repository.GetMediaStreams(new MediaStreamQuery { ItemId = item.InternalId }, token);
        bool LocalTrack(MediaStream stream) => stream.IsExternal && stream.Protocol == MediaProtocol.File &&
            stream.Type is MediaStreamType.Subtitle or MediaStreamType.Audio;
        var discovered = new List<MediaStream>();
        var supported = new HashSet<MediaStreamType>();
        foreach (var (resolver, type) in new[] { (binding.Subtitle, MediaStreamType.Subtitle), (binding.Audio, MediaStreamType.Audio) })
        {
            token.ThrowIfCancellationRequested();
            if (!(bool)Invoke(binding.Supports, resolver, new object[] { item, options })!) continue;
            supported.Add(type);
            discovered.AddRange((List<MediaStream>)Invoke(binding.Discover, resolver,
                new object[] { item, 0, directory, options, naming, true })!);
        }
        token.ThrowIfCancellationRequested();
        var next = current.Where(stream => !LocalTrack(stream) || !supported.Contains(stream.Type)).ToList();
        var index = current.Count == 0 ? 0 : current.Max(stream => stream.Index) + 1;
        foreach (var stream in discovered.Where(LocalTrack).DistinctBy(stream => (stream.Type, stream.Path)))
        {
            var previous = current.FirstOrDefault(old => LocalTrack(old) && old.Type == stream.Type && string.Equals(old.Path, stream.Path, StringComparison.Ordinal));
            // Re-probe discovered audio/special subtitles even when the path is unchanged. A stamp-only cache
            // would miss same-size/mtime replacement; a scan is explicit work, not the playback hot path.
            MediaStream candidate = stream;
            if (stream.Type == MediaStreamType.Audio || new[] { ".sub", ".smi", ".sami", ".mpl" }.Contains(Path.GetExtension(stream.Path), StringComparer.OrdinalIgnoreCase))
            {
                MediaInfo mediaInfo;
                // This selected sidecar probe is plugin-owned. Keep the scope around its full await only,
                // not discovery, repository commits, or native background scheduling.
                using (ExclusiveMediaControl.EnterOwnedOperation())
                    mediaInfo = await binding.Prober.GetMediaInfo(new MediaInfoRequest
                    {
                        ExtractChapters = false, MediaType = MediaBrowser.Model.Dlna.DlnaProfileType.Audio,
                        MediaSource = new MediaBrowser.Model.Dto.MediaSourceInfo { Path = stream.Path, Protocol = MediaProtocol.File }
                    }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var probed = mediaInfo?.MediaStreams?.FirstOrDefault(s => s.Type == stream.Type);
                if (probed == null || string.IsNullOrWhiteSpace(probed.Codec)) throw new InvalidDataException("No complete matching stream from external-track probe: " + stream.Path);
                candidate = new MediaStream();
                foreach (var field in StreamFields) field.SetValue(candidate, field.GetValue(probed));
                // Provider result supplies technical fields, resolver supplies identity/filename flags.
                candidate.Type = stream.Type; candidate.Path = stream.Path; candidate.Protocol = stream.Protocol; candidate.IsExternal = true;
                candidate.Language = stream.Language ?? probed.Language; candidate.Title = stream.Title ?? probed.Title;
                candidate.IsDefault = stream.IsDefault; candidate.IsForced = stream.IsForced; candidate.IsHearingImpaired = stream.IsHearingImpaired;
                candidate.DeliveryMethod = stream.DeliveryMethod; candidate.DeliveryUrl = stream.DeliveryUrl;
                candidate.DeliveryFormat = stream.DeliveryFormat; candidate.IsExternalUrl = stream.IsExternalUrl;
                candidate.SupportsExternalStream = stream.SupportsExternalStream;
                if (previous != null) { candidate.ItemId = previous.ItemId; candidate.ServerId = previous.ServerId; }
            }
            candidate.Index = previous?.Index ?? index++;
            candidate.AttachmentSize ??= 0; // Native GetMediaStream reads SQL NULL as zero for this field.
            next.Add(previous != null && StreamFields.All(field => Equals(field.GetValue(previous), field.GetValue(candidate))) ? previous : candidate);
        }
        if (current.Count != next.Count || current.Any(stream => !next.Contains(stream)))
        {
            token.ThrowIfCancellationRequested();
            Task Save(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                repository.SaveMediaStreams(item.InternalId, next.OrderBy(stream => stream.Index).ToList(), ct);
                return Task.CompletedTask;
            }
            if (persistMutation != null) await persistMutation(item, Save, token).ConfigureAwait(false);
            else await Save(token).ConfigureAwait(false);
        }
    }

    private (object Subtitle, object Audio, MethodInfo Discover, MethodInfo Supports, MethodInfo Naming, IMediaProbeManager Prober) BindResolvers()
    {
        var assembly = Assembly.Load("Emby.Providers");
        var version = assembly.GetName().Version;
        if (version is not { Major: 4, Minor: 10, Build: 2 })
            throw new NotSupportedException($"External-track resolver contract is verified for Emby 4.10.2; loaded {version}.");
        var baseType = assembly.GetType("Emby.Providers.MediaInfo.BaseTrackResolver", true)!;
        var naming = typeof(ILibraryManager).GetMethod("GetNamingOptions", Type.EmptyTypes)!;
        var discover = baseType.GetMethod("GetExternalTracks", BindingFlags.Public | BindingFlags.Instance, null,
            new[] { typeof(BaseItem), typeof(int), typeof(IDirectoryService), typeof(LibraryOptions), naming.ReturnType, typeof(bool) }, null);
        var supports = baseType.GetMethod("Supports", new[] { typeof(BaseItem), typeof(LibraryOptions) });
        if (!baseType.IsPublic || discover?.ReturnType != typeof(List<MediaStream>) || supports?.ReturnType != typeof(bool))
            throw new NotSupportedException("Public external-track discovery contract changed.");
        object Create(string name)
        {
            var type = assembly.GetType("Emby.Providers.MediaInfo." + name, true)!;
            var constructor = type.GetConstructor(new[] { typeof(ILocalizationManager), typeof(IFileSystem), typeof(ILibraryManager) });
            if (!type.IsPublic || !baseType.IsAssignableFrom(type) || constructor == null)
                throw new NotSupportedException("Public track resolver constructor changed: " + name);
            return constructor.Invoke(new object[] { localization, files, library });
        }
        var manager = host.Resolve<IMediaProbeManager>() ?? throw new InvalidOperationException("Media probe manager is unavailable.");
        return (Create("SubtitleResolver"), Create("AudioTrackResolver"), discover, supports, naming, manager);
    }

    private static object? Invoke(MethodInfo method, object instance, object[] arguments)
    {
        try { return method.Invoke(instance, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }

    public static void RequestRestart(IServerApplicationHost host, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!host.CanSelfRestart) throw new NotSupportedException("The host does not support self restart.");
        if (host.IsShuttingDown) throw new InvalidOperationException("The host is already shutting down.");
        token.ThrowIfCancellationRequested();
        host.Restart();
    }

    public GenericItemList GetSystemInfo()
    {
        using var process = Process.GetCurrentProcess();
        var result = new GenericItemList
        {
            new() { PrimaryText = "processWorkingSet", SecondaryText = $"{process.WorkingSet64} bytes" },
            new() { PrimaryText = "Plugin version", SecondaryText = typeof(MaintenanceService).Assembly.GetName().Version?.ToString() ?? "unknown" },
            new() { PrimaryText = "Emby version", SecondaryText = host.ApplicationVersion.ToString() }
        };
        try
        {
            var policy = new EmbyPathPolicy(paths.ProgramDataPath);
            var data = Path.GetFullPath(paths.DataPath);
            policy.RequireWritablePath(Path.Combine(data, ".maintenance-scope-check"));
            long total = 0;
            // Only the host's immediate DataPath. No recursive walk, linked roots, or user-directory discovery.
            foreach (var path in Directory.EnumerateFiles(data, "*", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                var stem = name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
                if (!new[] { ".db", ".sqlite", ".sqlite3" }.Contains(Path.GetExtension(stem), StringComparer.OrdinalIgnoreCase)) continue;
                policy.RequireWritablePath(path);
                var size = new FileInfo(path).Length;
                total += size;
                result.Add(new() { PrimaryText = "Data DB: " + name, SecondaryText = $"{size} bytes" });
            }
            result.Add(new() { PrimaryText = "dataDBsizes", SecondaryText = $"{total} bytes (DataPath, top level; DB/WAL/SHM)" });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { result.Add(new() { PrimaryText = "dataDBsizes", SecondaryText = "Unavailable: " + ex.Message }); }
        return result;
    }
}
