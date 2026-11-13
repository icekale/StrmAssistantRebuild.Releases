using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Options;
using StrmAssistantRebuild.Options.UIBaseClasses;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild;

public sealed class Plugin : BasePlugin, IHasThumbImage, IHasUIPages, IServerEntryPoint
{
    public static Plugin? Instance { get; private set; }
    public override string Name => "Strm Assistant Rebuild";
    public override Guid Id => new("97d51f81-cd66-4e74-a2f3-e0c3687cf738");
    public override string Description => "神医助手重建版 · STRM 媒体信息、元数据与播放体验增强";
    public override MediaBrowser.Model.Plugins.PluginInfo GetPluginInfo()
    {
        var info = base.GetPluginInfo();
        info.ConfigurationFileName = ConfigurationStore.OptionsFilePath;
        return info;
    }
    public ImageFormat ThumbImageFormat => ImageFormat.Png;
    public Stream GetThumbImage() => typeof(Plugin).Assembly.GetManifestResourceStream("StrmAssistantRebuild.Properties.thumb.png")!;
    public IReadOnlyCollection<IPluginUIPageController> UIPageControllers { get; }
    public ILogger Logger { get; }
    public EmbyPathPolicy Paths { get; }
    public SimpleFileStore<PluginOptions> ConfigurationStore { get; }
    public SimpleFileStore<MediaInfoExtractOptions> MediaStore { get; }
    public SimpleFileStore<MetadataEnhanceOptions> MetadataStore { get; }
    public SimpleFileStore<IntroSkipOptions> IntroStore { get; }
    public SimpleFileStore<ExperienceEnhanceOptions> ExperienceStore { get; }
    public SimpleFileStore<DashboardOptions> DashboardStore { get; }
    private readonly CancellationTokenSource dashboardLifetime = new();
    private readonly ILibraryManager libraries;
    private readonly IItemRepository repository;
    private readonly IProviderManager providers;
    private readonly IFileSystem nativeFiles;
    private readonly IUserDataManager userData;
    private readonly IUserManager users;
    private readonly INotificationManager notifications;
    private readonly IApplicationHost host;
    private readonly MediaBrowser.Common.Net.IHttpClient nativeHttpClient;
    private readonly string webRoot;
    private readonly string assetBackupRoot;
    public DashboardAssetManager? DashboardAssets { get; private set; }
    public QueueManager WorkQueue { get; }
    public EpisodeScopeBatch EpisodeScopeBatches { get; }
    public EpisodeScopeJournal EpisodeJournal { get; }
    public EpisodeScopeStartup EpisodeScopeStartupRecovery { get; }
    public EpisodeScopeCatchup EpisodeScopeNewItems { get; }
    private readonly object episodeShutdownGate = new();
    private Task? episodeShutdown;
    public Task EpisodeScopeShutdown => Volatile.Read(ref episodeShutdown) ?? Task.CompletedTask;
    public MediaInfoApi? MediaInfoApi { get; private set; }
    public ImageCaptureApi? ImageCaptureApi { get; private set; }
    public VideoThumbnailApi? VideoThumbnailApi { get; private set; }
    private readonly Action initializeMedia;
    public MediaCatchupService? MediaCatchup { get; private set; }
    public ChapterApi? ChapterApi { get; private set; }
    public IntroSkip.PlaySessionMonitor? PlaySessionMonitor { get; private set; }
    public HttpClientFactory OutboundHttp { get; } = new();
    public IntroSkip.IntroScrapingService? IntroScrapingService { get; private set; }
    public FingerprintApi? FingerprintApi { get; private set; }
    public MetadataEnhancePipeline? MetadataPipeline { get; private set; }
    public MultiVersionService? MultiVersionService { get; private set; }
    public LocalDeepDelete? LocalDeepDelete { get; private set; }
    public DeepDeleteNotification? DeepDeleteNotifications { get; private set; }
    public SearchCoordinator? SearchCoordinator { get; private set; }
    public UserExperienceService? UserExperienceService { get; private set; }
    public UserPreferenceService? UserPreferenceService { get; private set; }
    public FavoriteUpdateNotification? FavoriteNotifications { get; private set; }
    public MarkerUpdateNotification? MarkerNotifications { get; private set; }
    public MaintenanceService? MaintenanceService { get; private set; }
    public MemoryMonitor? MemoryMonitor { get; private set; }
    public DashboardActivity? Activity { get; private set; }
    public NetworkService? NetworkService { get; private set; }
    public PluginUpdateService? PluginUpdateService { get; private set; }
    public Provider.DoubanMetadataService? DoubanMetadataService { get; private set; }
    public Provider.MetadataLanguageService? MetadataLanguageService { get; private set; }
    public Provider.EpisodeGroupService? EpisodeGroupService { get; private set; }
    private string? searchIndexFile;
    public Provider.NfoActorService? NfoActorService { get; private set; }
    public Provider.PersonRefreshService? PersonRefreshService { get; private set; }
    public Plugin(IApplicationHost applicationHost, IApplicationPaths applicationPaths, ILogManager logManager,
        ILibraryManager libraryManager, IUserManager userManager, MediaBrowser.Controller.Configuration.IServerConfigurationManager serverConfiguration,
        IMediaSourceManager mediaSourceManager, IMediaProbeManager mediaProbeManager, IItemRepository itemRepository,
        IFileSystem fileSystem, IProviderManager providerManager, IImageExtractionManager imageExtractionManager, IMediaMountManager mediaMountManager,
        MediaBrowser.Controller.Drawing.IImageProcessor imageProcessor, MediaBrowser.Controller.Session.ISessionManager sessionManager,
        IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder, MediaBrowser.Model.Serialization.IJsonSerializer jsonSerializer, IServerApplicationHost serverApplicationHost,
        IUserDataManager userDataManager, IUserViewManager userViewManager,
        MediaBrowser.Model.Globalization.ILocalizationManager localizationManager, MediaBrowser.Model.Tasks.ITaskManager taskManager, MediaBrowser.Common.Net.IHttpClient httpClient, INotificationManager notificationManager)
    {
        Instance = this;
        Logger = logManager.GetLogger(Name);
        host = applicationHost;
        nativeHttpClient = httpClient;
        libraries = libraryManager;
        repository = itemRepository; providers = providerManager; userData = userDataManager; nativeFiles = fileSystem;
        ScopeSelection.ConfigureUsers(userManager);
        users = userManager;
        notifications = notificationManager;
        Paths = new EmbyPathPolicy(applicationPaths.ProgramSystemPath, applicationPaths.ProgramDataPath);
        embyCachePath = applicationPaths.CachePath;
        webRoot = string.IsNullOrWhiteSpace(serverConfiguration.Configuration.DashboardSourcePath)
            ? Path.Combine(serverConfiguration.ApplicationPaths.ApplicationResourcesPath, "dashboard-ui") : serverConfiguration.Configuration.DashboardSourcePath;
        assetBackupRoot = Path.Combine(applicationPaths.PluginConfigurationsPath, "StrmAssistantRebuild", "dashboard-backups");
        var folder = Path.Combine(applicationPaths.PluginConfigurationsPath, "StrmAssistantRebuild");
        SimpleFileStore<T> Store<T>() where T : EditableOptionsBase, new() => new(folder, typeof(T).Name + ".json", Paths, Logger);
        ConfigurationStore = Store<PluginOptions>();
        MediaStore = Store<MediaInfoExtractOptions>();
        MetadataStore = Store<MetadataEnhanceOptions>();
        IntroStore = Store<IntroSkipOptions>();
        ExperienceStore = Store<ExperienceEnhanceOptions>();
        DashboardStore = Store<DashboardOptions>();
        WorkQueue = new QueueManager(Logger);
        EpisodeJournal = new EpisodeScopeJournal(Paths, Path.Combine(folder, "episode-scope-journal.json"));
        EpisodeScopeBatches = new EpisodeScopeBatch(host.ApplicationVersion, libraryManager, userDataManager,
            Path.Combine(serverConfiguration.ApplicationPaths.DataPath, "library.db"), EpisodeJournal, WorkQueue, Logger);
        EpisodeScopeNewItems = new EpisodeScopeCatchup(libraryManager, EpisodeScopeBatches, Logger);
        EpisodeScopeStartupRecovery = new EpisodeScopeStartup(EpisodeJournal, EpisodeScopeBatches,
            () => serverApplicationHost.IsStartupComplete,
            () => users.GetUserList(new MediaBrowser.Model.Querying.UserQuery { IsDisabled=false, Limit=64, EnableTotalRecordCount=false })
                .Where(u=>u?.InternalId>0&&u.Policy?.IsDisabled==false).OrderBy(u=>u.InternalId).Select(u=>u.InternalId).FirstOrDefault(), Logger, catchup: EpisodeScopeNewItems);
        ConfigureQueue();
        MultiVersionService = new MultiVersionService(libraryManager, WorkQueue, Logger, () => ExperienceStore.GetOptions());
        LocalDeepDelete = new LocalDeepDelete(libraryManager, mediaSourceManager, Paths, () => ExperienceStore.GetOptions().EnableDeepDelete);
        mediaSources = mediaSourceManager;
        embyRoots = new[] { applicationPaths.ProgramDataPath, applicationPaths.ProgramSystemPath };
        DeepDeleteNotifications = new DeepDeleteNotification(libraryManager,userManager,notifications,() => ExperienceStore.GetOptions().EnhanceNotificationSystem,Logger);
        initializeMedia = () =>
        {
            NetworkService = new NetworkService(OutboundHttp);
            ConfigureNetwork();
            PluginUpdateService = new PluginUpdateService(OutboundHttp, applicationPaths.PluginsPath, typeof(Plugin).Assembly.GetName().Version!, applicationHost.NotifyPendingRestart, Logger,
                PluginUpdateService.DefaultReleaseFeed, () => { var about = ConfigurationStore.GetOptions().AboutOptions; return new(about.GitHubToken, about.GitHubProxy); });
            MaintenanceService = new MaintenanceService(libraryManager, itemRepository, fileSystem, localizationManager, serverApplicationHost, applicationPaths, Logger,
                () => ConfigurationStore.GetOptions().AboutOptions.DebugMode, () => MediaStore.GetOptions(), WorkQueue, PersistMediaMutation);
            Activity = new DashboardActivity(taskManager, sessionManager, Logger);
            MemoryMonitor = new MemoryMonitor(serverApplicationHost, sessionManager, taskManager, Logger,
                () => WorkQueue.IsBusy || EpisodeScopeNewItems.IsBusy || MediaCatchup?.IsBusy == true || MetadataPipeline?.IsBusy == true || SearchCoordinator?.IsBusy == true || FavoriteNotifications?.IsBusy == true);
            WorkQueue.WorkStarted += MemoryMonitor.NotifyActivity;
            MemoryMonitor.Configure(DashboardStore.GetOptions().MemoryOptions.MemoryUsageLimitMB);
            searchIndexFile = Path.Combine(folder, "search-index.json");
            SearchCoordinator = new SearchCoordinator(new ChineseSearchIndex(libraryManager, Paths, searchIndexFile, Logger), itemRepository, host.ApplicationVersion, Logger);
            SearchCoordinator.Configure(ConfigurationStore.GetOptions().ModOptions);
            UserExperienceService = new UserExperienceService(libraryManager, userDataManager, userManager, sessionManager, Logger,
                e => MediaCatchup?.OnPlaybackStop(e));
            UserPreferenceService = new UserPreferenceService(userManager, userViewManager, sessionManager, Logger);
            ConfigureUserExperience();
            FavoriteNotifications = new FavoriteUpdateNotification(libraryManager,userManager,userDataManager,notifications,Logger);
            MediaInfoApi = new MediaInfoApi(libraryManager, mediaSourceManager, mediaProbeManager, itemRepository, fileSystem, providerManager, Path.Combine(folder, "media-info"), imageProcessor: imageProcessor);
            ChapterApi = new ChapterApi(libraryManager, itemRepository, WorkQueue, Logger, persistMutation: async (item, mutation, token) =>
            {
                if (item.RunTimeTicks is > 0) await PersistMediaMutation(item, mutation, token).ConfigureAwait(false);
                else
                {
                    if (MediaStore.GetOptions().PersistMediaInfoMode != MediaInfoExtractOptions.PersistMediaInfoOption.None.ToString()) MediaInfoApi.InvalidateJsonCache(item, token);
                    await mutation(token).ConfigureAwait(false); // No invalid incomplete media document is exported.
                }
            });
            DoubanMetadataService = new Provider.DoubanMetadataService(libraryManager, providerManager, OutboundHttp, Paths, Path.Combine(folder, "douban"), () => MetadataStore.GetOptions(), Logger, fileSystem);
            MetadataLanguageService = new Provider.MetadataLanguageService(libraryManager, providerManager, () => new DirectoryService(Logger, fileSystem), () => MetadataStore.GetOptions(), OutboundHttp, Logger, Paths, Path.Combine(folder, "metadata-providers"));
            Provider.DoubanChannelService.Current = new Provider.DoubanChannelService(new Provider.DoubanFrodoClient(OutboundHttp),
                Path.Combine(folder, "douban-channel-images"), () => ExperienceStore.GetOptions().EnableDoubanChannel, Logger,
                invalidateCover: (externalId, oldPath) =>
                {
                    foreach (var item in libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery { ExternalId = externalId, Recursive = true }))
                        if (string.Equals(item.GetImageInfo(MediaBrowser.Model.Entities.ImageType.Primary, 0)?.Path, oldPath, StringComparison.Ordinal))
                            item.DeleteImage(MediaBrowser.Model.Entities.ImageType.Primary, 0);
                });
            EpisodeGroupService = new Provider.EpisodeGroupService(libraryManager, MetadataLanguageService, () => MetadataStore.GetOptions(), OutboundHttp, Paths, Path.Combine(folder, "episode-groups"), Logger);
            NfoActorService = new Provider.NfoActorService(libraryManager, () => MetadataStore.GetOptions(), Logger);
            ConfigureNativeMetadata();
            MetadataPipeline = new MetadataEnhancePipeline(libraryManager, providerManager, fileSystem, Paths, WorkQueue, () => MetadataStore.GetOptions(), EnhanceMetadata, Logger, AggregateSeriesCast);
            PersonRefreshService = new Provider.PersonRefreshService(libraryManager, providerManager, WorkQueue, OutboundHttp, () => MetadataStore.GetOptions(),
                (person, token, metadata, images, full, download, supplementOnly) => MetadataPipeline.RefreshAsync(person, token, metadata, images, full, download, supplementOnly), Logger);
            IntroScrapingService = new IntroSkip.IntroScrapingService(OutboundHttp, Logger);
            FingerprintApi = new FingerprintApi(libraryManager, fileSystem, applicationPaths, ffmpegManager, mediaEncoder, mediaMountManager, jsonSerializer, serverApplicationHost, Logger, WorkQueue, PersistMediaMutation,
                resetPersistMutation: (item, mutation, token) => MediaInfoApi.ApplyAndPersistAsync(item, mutation, token, invalidateExistingCache: true));
            ExclusiveFileChange.Configure(new ExclusiveFileChange.Hooks(ClearOnFileChange, item => MaintenanceService.ExternalTracksChanged(item),
                (item, token) => MaintenanceService.ScanExternalTracksAsync(item, token),
                (item, stamp) => { item.DateModified = stamp; libraries.UpdateItems(new List<MediaBrowser.Controller.Entities.BaseItem> { item }, null!, ItemUpdateType.None, false, false, null!, CancellationToken.None); }));
            MetadataEnhancePipeline.TaskItemMarks = ExclusiveEligibility.EnterTaskItem; // M160 original task item marks
            // M161 original override/tmdb-tv/{id}/episodegroup.json (cache mode not None).
            Provider.EpisodeGroupService.OriginalOverrideRoot = () =>
                string.Equals(MetadataStore.GetOptions().MetadataBuildOptions.MetadataCacheMode, "None", StringComparison.OrdinalIgnoreCase) ? null : MetadataCacheRoot();
            PatchHealth.Record("IntroRefreshBridge", IntroRefreshBridge.Configure(host.ApplicationVersion, true, this, libraryManager, userManager, taskManager, Logger, out var refreshBridge));
            Logger.Info("[StrmAssistantRebuild] {0}", refreshBridge);
            MarkerNotifications = new MarkerUpdateNotification(libraryManager,userManager,notifications,Logger,sessionManager);
            PlaySessionMonitor = new IntroSkip.PlaySessionMonitor(libraryManager, userManager, sessionManager,
                () => IntroStore.GetOptions(), ChapterApi.GetMarkers, ChapterApi.ApplyBehaviorAsync, Logger, MarkerNotifications.SendAsync);
            PlaySessionMonitor.Initialize();
            var customEncoder = new CustomEncoderRunner(() => MediaStore.GetOptions().CustomEncoderPath);
            ImageCaptureApi = new ImageCaptureApi(imageExtractionManager, itemRepository, mediaMountManager, serverConfiguration.ApplicationPaths, Path.Combine(folder, "image-work"), customEncoder, imageProcessor, () => ffmpegManager.FfmpegConfiguration.EncoderPath);
            VideoThumbnailApi = new VideoThumbnailApi(imageExtractionManager, itemRepository, mediaMountManager, Path.Combine(folder, "thumbnail-work"), imageProcessor, customEncoder);
        };
        var root = new OptionsController<PluginOptions>(Id.ToString("N"), ConfigurationStore, Prepare, true, RunGeneralCommand,
            (view, command, token) => Task.FromResult<IPluginUIView?>(command switch
            {
                "RuntimeDiagnostics" => new RuntimeDiagnosticsDialog(view, UiText("Runtime diagnostics", "运行时能力详情", "執行期能力詳情"), CreateCapabilities().Summary, UiText("Close", "关闭", "關閉")),
                "EpisodeStartupSettings" => new EpisodeStartupDialog(view, EpisodeJournal, libraries, users, dashboardLifetime.Token),
                "EpisodeMigrationPreview" => new EpisodeMigrationPreviewDialog(view, host.ApplicationVersion, libraries, userData, users, dashboardLifetime.Token, initialBatch: EpisodeScopeBatches),
                _ => null
            }));
        root.TabPageControllers = new IPluginUIPageController[]
        {
            new OptionsController<MediaInfoExtractOptions>(Id.ToString("N"), MediaStore, Prepare),
            new OptionsController<MetadataEnhanceOptions>(Id.ToString("N"), MetadataStore, Prepare, commandHandler: RunMetadataCommand),
            new OptionsController<IntroSkipOptions>(Id.ToString("N"), IntroStore, Prepare, commandHandler: RunIntroCommand),
            new OptionsController<ExperienceEnhanceOptions>(Id.ToString("N"), ExperienceStore, Prepare, commandHandler: RunExperienceCommand),
            new OptionsController<DashboardOptions>(Id.ToString("N"), DashboardStore, Prepare, viewCommandHandler: RunDashboardCommand, lifetime: dashboardLifetime.Token)
        };
        UIPageControllers = new[] { root };
        Logger.Info("[StrmAssistantRebuild] Loaded plugin {0}, host {1}", typeof(Plugin).Assembly.GetName().Version, host.ApplicationVersion);
    }
    private void Prepare<T>(T model) where T : EditableOptionsBase
    {
        OptionChoices.RefreshForCulture(model);
        if (model is IntroSkipOptions intro) intro.ClearIntroButton.CommandId = "ClearIntroMarkers";
        if (model is MetadataEnhanceOptions metadata) metadata.DoubanScrapeButton.CommandId = "ApplyDoubanMetadata";
        if (model is ExperienceEnhanceOptions experience) experience.SplitMoviesButton.CommandId = "SplitMultiVersionMovies";
        if (model is PluginOptions root)
        {
            NetworkService?.Prepare(root.NetworkOptions);
            root.AboutOptions.VersionInfoList = new Emby.Web.GenericEdit.Elements.List.GenericItemList
            {
                new() { PrimaryText = Name, SecondaryText = typeof(Plugin).Assembly.GetName().Version?.ToString() },
                new() { PrimaryText = "Emby", SecondaryText = host.ApplicationVersion.ToString() },
                new() { PrimaryText = ".NET", SecondaryText = Environment.Version.ToString() },
                new()
                {
                    PrimaryText = UiText("Rebuild updates", "重建版更新", "重建版更新"),
                    SecondaryText = UpdateStatusText(PluginUpdateService?.LastResult),
                    Button1 = new(UiText("Check for updates", "检查更新", "檢查更新")) { CommandId = "CheckPluginUpdate", IsEnabled = PluginUpdateService != null, IsVisible = true },
                    Button2 = new(Resources.UpdatePluginTask_Name_Update_Plugin) { CommandId = "UpdatePlugin", IsEnabled = PluginUpdateService?.LastResult.State == "update-available", IsVisible = true }
                },
                new()
                {
                    PrimaryText = UiText("Automatic plugin updates", "自动插件更新", "自動外掛更新"),
                    SecondaryText = SuppressionStatusText()
                },
                new()
                {
                    PrimaryText = Resources.NetworkOptions_EditorTitle_Network,
                    SecondaryText = root.NetworkOptions.ProxyServerStatus.StatusText,
                    Button1 = new(UiText("Check proxy", "测试代理", "測試代理")) { CommandId = Common.NetworkService.CheckCommand, IsEnabled = NetworkService != null && root.NetworkOptions.EnableProxyServer && !string.IsNullOrWhiteSpace(root.NetworkOptions.ProxyServerUrl), IsVisible = true }
                }
            };
        }
        if (model is DashboardOptions dashboard)
        {
            // M148: original two-item layout (memory usage with task/session sub-items, search index).
            var counts = Activity?.Counts() ?? (0, 0, 0);
            var restart = MemoryMonitor?.RestartState ?? (null, null, false);
            long working; using (var process = System.Diagnostics.Process.GetCurrentProcess()) working = process.WorkingSet64;
            int refreshQueue; try { refreshQueue = providers.GetRefreshQueue()?.Count ?? 0; } catch { refreshQueue = 0; }
            long? indexBytes = null; try { if (searchIndexFile != null && File.Exists(searchIndexFile)) indexBytes = new FileInfo(searchIndexFile).Length; } catch { }
            dashboard.SystemInfoItemList = DashboardOverview.Build(new DashboardSnapshot(working, restart.LimitMB, restart.CountdownSeconds, restart.BusyPending,
                counts.RunningTasks, MediaCatchup?.ActiveCount ?? 0, refreshQueue, counts.Sessions, counts.Playing,
                indexBytes, SearchCoordinator != null && ConfigurationStore.GetOptions().ModOptions.EnhanceChineseSearch,
                SearchCoordinator?.IsBusy == true, SearchCoordinator?.Progress ?? 0));
        }
        var capabilities = CreateCapabilities();
        capabilities.Apply(model);
        if (model is PluginOptions preparedRoot)
        {
            var groups = capabilities.Entries.Where(pair => pair.Key.EndsWith(".IsModSupported", StringComparison.Ordinal)).ToArray();
            preparedRoot.AboutOptions.VersionInfoList.Add(new()
            {
                PrimaryText = UiText("Episode library migration", "剧集库迁移", "劇集庫遷移"),
                SecondaryText = UiText("Preview candidates first; a separate confirmation is required for an initial migration.", "先预览候选与限制；首次迁移需要单独确认。", "先預覽候選與限制；首次遷移需要另行確認。"),
                Button1 = new(UiText("Preview", "预览", "預覽")) { CommandId = "EpisodeMigrationPreview", IsEnabled = true, IsVisible = true }
            });
            preparedRoot.AboutOptions.VersionInfoList.Add(new()
            {
                PrimaryText = UiText("Episode startup settings", "剧集启动设置", "劇集啟動設定"),
                SecondaryText = UiText("Recovery and new-episode handling for journaled libraries; changes apply at the next start.", "管理已登记库的恢复与新集接入；更改在下次启动生效。", "管理已登記媒體庫的復原與新集接入；變更於下次啟動生效。"),
                Button1 = new(UiText("Configure", "配置", "設定")) { CommandId = "EpisodeStartupSettings", IsEnabled = true, IsVisible = true }
            });
            preparedRoot.AboutOptions.VersionInfoList.Add(new()
            {
                PrimaryText = UiText("Runtime diagnostics", "运行时能力详情", "執行期能力詳情"),
                SecondaryText = $"{groups.Count(pair => pair.Value.Supported)}/{groups.Length}",
                Button1 = new(UiText("Details", "查看详情", "查看詳情")) { CommandId = "RuntimeDiagnostics", IsEnabled = true, IsVisible = true }
            });
        }
        // Runtime catalogs use public host interfaces; no private field writes.
        try
        {
            LibraryScopeCatalog.Apply(model, libraries, ScopeSelection.FavoritesAvailable);
            var userOptions = users.GetUserList(new MediaBrowser.Model.Querying.UserQuery()).Select(u => new EditorSelectOption { Value = u.InternalId.ToString(), Name = u.Name, IsEnabled = true }).ToList();
            FillUserCatalogs(model, userOptions);
        }
        catch (Exception ex) { Logger.Warn("[StrmAssistantRebuild] UI catalogs degraded: {0}", ex.Message); }
    }
    private RuntimeCapabilities CreateCapabilities() => new(host.ApplicationVersion, host.Plugins, providers, new RuntimeServicePresence
    {
        MediaInfo = MediaInfoApi, Images = ImageCaptureApi, Thumbnails = VideoThumbnailApi,
        Metadata = MetadataPipeline, Douban = DoubanMetadataService, Languages = MetadataLanguageService,
        Nfo = NfoActorService, Fingerprints = FingerprintApi, Search = SearchCoordinator, Repository = repository,
        Experience = UserExperienceService, Preferences = UserPreferenceService, Versions = MultiVersionService, Favorites = FavoriteNotifications, MarkerNotifications = MarkerNotifications, DeepDeleteNotifications = DeepDeleteNotifications,
        Network = NetworkService, Dashboard = DashboardAssets, EpisodeBatches = EpisodeScopeBatches, StartupRecovery = EpisodeScopeStartupRecovery, NewEpisodes = EpisodeScopeNewItems, MediaCatchup = MediaCatchup
    });
    private static string UiText(string english, string simplified, string traditional) => Common.UiLanguage.Text(english, simplified, traditional);
    private static string UpdateStatusText(Common.PluginUpdateResult? result) => result?.State switch
    {
        null => UiText("Update service is starting.", "更新服务正在启动。", "更新服務正在啟動。"),
        "not-checked" => UiText("Not checked yet; latest version unknown.", "尚未检查，最新版本未知。", "尚未檢查，最新版本未知。"),
        "no-update" => UiText("Up to date.", "已是最新版本。", "已是最新版本。") + (result.Version == null ? "" : " " + result.Version),
        "update-available" => UiText("New version available: ", "有新版本：", "有新版本：") + result.Version,
        "installed" or "rolled-back" => UiText("Installed; restart Emby Server to apply.", "已安装，重启 Emby Server 后生效。", "已安裝，重新啟動 Emby Server 後生效。"),
        "no-package" => UiText("The release feed has no package yet.", "发行源中还没有安装包。", "發行源中還沒有安裝包。"),
        "unavailable" => UiText("No release feed configured.", "没有配置发行源。", "沒有設定發行源。"),
        "cancelled" => UiText("Cancelled.", "已取消。", "已取消。"),
        _ => UiText("Failed: ", "失败：", "失敗：") + result.Detail
    };
    private static string SuppressionStatusText()
    {
        var detail = PluginUpdateSuppression.Detail;
        if (detail.StartsWith("Disabled", StringComparison.Ordinal)) return UiText("Off.", "未启用。", "未啟用。");
        if (detail.StartsWith("Active", StringComparison.Ordinal)) return UiText("Active for scheduled plugin updates.", "已对计划任务中的插件自动更新生效。", "已對排程工作中的外掛自動更新生效。");
        if (detail.StartsWith("Armed", StringComparison.Ordinal)) return UiText("Ready; takes effect at the next scheduled update check.", "已就绪，下次计划更新检查时生效。", "已就緒，下次排程更新檢查時生效。");
        return UiText("Not active: compatibility check failed; Emby's native behavior is kept.", "未生效：兼容性检查未通过，保持 Emby 原生行为。", "未生效：相容性檢查未通過，保持 Emby 原生行為。");
    }
    private static void FillUserCatalogs(EditableOptionsBase model, List<EditorSelectOption> users)
    {
        foreach (var prop in model.GetType().GetProperties())
        {
            if (prop.PropertyType == typeof(List<EditorSelectOption>) && prop.SetMethod?.IsPublic == true)
            {
                if (prop.Name == "UserList") prop.SetValue(model, users.ToList());
            }
            else if (typeof(EditableOptionsBase).IsAssignableFrom(prop.PropertyType) && prop.GetValue(model) is EditableOptionsBase child)
                FillUserCatalogs(child, users);
        }
    }
    public void Run()
    {
        try { initializeMedia(); MediaCatchup = new MediaCatchupService(this, libraries, userData, providers, users); ConfigurationStore.FileSaved += OnGeneralOptionsSaved; }
        catch (Exception ex) { Logger.Warn("[StrmAssistantRebuild] Media initialization degraded: {0}", ex.Message); }
        try
        {
            DashboardAssets = new DashboardAssetManager(webRoot, assetBackupRoot, host.ApplicationVersion, Paths, Logger);
            MediaStore.FileSaved += OnWebOptionsSaved;
            MetadataStore.FileSaved += OnWebOptionsSaved;
            IntroStore.FileSaved += OnWebOptionsSaved;
            ExperienceStore.FileSaved += OnWebOptionsSaved;
            DashboardStore.FileSaved += OnDashboardOptionsSaved;
            RefreshWebAssets();
        }
        catch (Exception ex) { Logger.Warn("[StrmAssistantRebuild] Web enhancements degraded: {0}", ex.Message); }
        try
        {
            // M162: original startup option, one shot (the original set it when it patched MovieDb.dll).
            var metadata = MetadataStore.GetOptions();
            if (metadata.ClearRecentTmdbCacheOnStartup)
            {
                var movieDb = host.Plugins.FirstOrDefault(p => p.GetType().FullName == "MovieDb.Plugin" && p.Name == "TheMovieDb");
                var (deleted, failed) = Common.MovieDbResponseCache.ClearRecentTmdbHttpCache(Paths, embyCachePath, movieDb?.Version, DateTimeOffset.UtcNow, dashboardLifetime.Token);
                metadata.ClearRecentTmdbCacheOnStartup = false;
                MetadataStore.Save(metadata);
                Logger.Info("[StrmAssistantRebuild] Recent TMDB HTTP cache cleared: deleted={0}, failed={1}.", deleted, failed);
            }
        }
        catch (Exception ex) { Logger.Warn("[StrmAssistantRebuild] Recent TMDB cache cleanup skipped: {0}", ex.Message); }
        _ = EpisodeScopeStartupRecovery.Start(dashboardLifetime.Token);
        Logger.Info("[StrmAssistantRebuild] Settings, Web assets and official media services initialized.");
    }
    private Task PersistMediaMutation(MediaBrowser.Controller.Entities.BaseItem item, Func<CancellationToken, Task> mutation, CancellationToken token) =>
        (MediaInfoApi ?? throw new InvalidOperationException("Media service is not initialized.")).ApplyAndPersistAsync(item, mutation, token);

    private async Task EnhanceMetadata(MediaBrowser.Controller.Entities.BaseItem item, CancellationToken token)
    {
        if (item.IsLocked) return;
        var failures = new List<Exception>();
        async Task Part(string name, Func<Task> run)
        {
            token.ThrowIfCancellationRequested();
            try { await run().ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(ex); Logger.Warn("[StrmAssistantRebuild] Metadata {0} item {1}: {2}", name, item.InternalId, ex.Message); }
        }
        if (MetadataLanguageService != null) await Part("language", async () => { await MetadataLanguageService.ApplyAsync(item, token).ConfigureAwait(false); });
        if (EpisodeGroupService != null) await Part("episode-group", async () => { await EpisodeGroupService.ApplyAsync(item, token).ConfigureAwait(false); });
        if (DoubanMetadataService != null) await Part("douban", async () =>
        {
            var result = await DoubanMetadataService.ApplyAsync(item, token).ConfigureAwait(false);
            Logger.Info("[StrmAssistantRebuild] Douban item {0}: {1}", item.InternalId, result.Status);
            if (result.Failed)
                throw new InvalidOperationException("Douban enhancement failed: " + result.Status);
        });
        if (NfoActorService != null) await Part("nfo-actor", async () => { await NfoActorService.ApplyAsync(item, token).ConfigureAwait(false); });
        var options = MetadataStore.GetOptions();
        // M161: original override tags/external_ids/episodeorder files (ChineseMovieDb on, cache mode not None).
        if (options.ChineseMovieDb && !string.Equals(options.MetadataBuildOptions.MetadataCacheMode, "None", StringComparison.OrdinalIgnoreCase))
            await Part("original-overrides", () =>
            {
                if (OriginalOverrides.Apply(item)) libraries.UpdateItems(new() { item }, null, ItemUpdateType.MetadataEdit, true, false, null, token);
                return Task.CompletedTask;
            });
        if (options.PinyinSortName && !item.IsFieldLocked(MediaBrowser.Model.Entities.MetadataFields.SortName))
        {
            var sort = PinyinUtility.GenerateSortName(item.Name, item is MediaBrowser.Controller.Entities.Audio.MusicArtist);
            if (!string.IsNullOrWhiteSpace(sort) && item.SortName != sort)
            {
                item.SortName = sort;
                libraries.UpdateItems(new() { item }, null, ItemUpdateType.MetadataEdit, true, false, null, token);
            }
        }
        if (MetadataLanguageService != null) await Part("final-policy", async () => { await MetadataLanguageService.ApplyFinalPolicyAsync(item, token).ConfigureAwait(false); });
        if (failures.Count > 0) throw new AggregateException("One or more metadata enhancements failed; other completed fields were retained.", failures);
    }
    private Task AggregateSeriesCast(MediaBrowser.Controller.Entities.TV.Series series, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!MetadataStore.GetOptions().EnhanceMovieDbPerson || libraries.GetItemById(series.InternalId) is not MediaBrowser.Controller.Entities.TV.Series current) return Task.CompletedTask;
        var people = new Provider.SeasonCastService(libraries).GetSeriesPeople(current, token);
        if (people == null) return Task.CompletedTask;
        var before = libraries.GetItemPeople(current);
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(people).AsSpan().SequenceEqual(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(before))) return Task.CompletedTask;
        Paths.RequireWritablePath(current.GetInternalMetadataPath());
        token.ThrowIfCancellationRequested();
        if (!MetadataStore.GetOptions().EnhanceMovieDbPerson || current.IsLocked || current.IsFieldLocked(MediaBrowser.Model.Entities.MetadataFields.Cast)) return Task.CompletedTask;
        libraries.UpdatePeople(current, people, false);
        Logger.Info("[StrmAssistantRebuild] Series {0} cast aggregated: {1} people.", current.InternalId, people.Count);
        return Task.CompletedTask;
    }
    private async Task<IPluginUIView?> RunDashboardCommand(OptionsView<DashboardOptions> view, string command, CancellationToken token)
    {
        if (command == DashboardOverview.ConfigureMemory) return new MemoryDialogView(view, DashboardStore);
        if (command == "RefreshActivity") return view.Refreshed("");
        if (command == DashboardOverview.CancelScheduledTasks)
        {
            var cancelled = (Activity ?? throw new InvalidOperationException("Activity service is not initialized.")).CancelRunningTasks(view.User);
            return view.Refreshed(UiText($"Cancellation requested for {cancelled} task(s).", $"已请求取消 {cancelled} 个任务。", $"已要求取消 {cancelled} 個工作。"));
        }
        if (command == DashboardOverview.CancelCatchup)
        {
            if (view.User?.Policy?.IsAdministrator != true) throw new System.Security.SecurityException("Administrator access required.");
            var cleared = MediaCatchup?.CancelPending() ?? 0;
            return view.Refreshed(UiText($"Cleared {cleared} catch-up item(s).", $"已清空 {cleared} 个追更项目。", $"已清空 {cleared} 個追更項目。"));
        }
        if (DashboardActivity.IsActivityCommand(command))
        {
            try { return view.Refreshed(await (Activity ?? throw new InvalidOperationException("Activity service is not initialized.")).RunAsync(command, view.User, token).ConfigureAwait(false)); }
            catch (OperationCanceledException) { return view.Refreshed("Activity operation cancelled."); }
            catch (Exception error) { return view.Refreshed("Activity operation failed: " + error.Message); }
        }
        if (command is not ("RebuildSearchIndex" or "CancelSearchRebuild")) return null;
        var search = SearchCoordinator ?? throw new InvalidOperationException("Search service is not initialized.");
        if (command == DashboardOverview.CancelIndexRebuild) { search.CancelRebuild(); return view.Refreshed(UiText("Index rebuild cancellation requested.", "已请求取消重建索引。", "已要求取消重建索引。")); }
        var model = (DashboardOptions)view.ContentData;
        var row = model.SystemInfoItemList.Last(i => i.Button1?.CommandId == DashboardOverview.RebuildIndex);
        row.Button1.IsEnabled = false;
        row.Button2 = new Emby.Web.GenericEdit.Elements.ButtonItem { Caption = UiText("Cancel", "取消", "取消"), Icon = Emby.Web.GenericEdit.Elements.IconNames.cancel, CommandId = DashboardOverview.CancelIndexRebuild, IsEnabled = true, IsVisible = true };
        try
        {
            await search.RebuildAsync(new InlineProgress(value =>
            {
                row.HasPercentage = true; row.PercentComplete = (int)Math.Clamp(value, 0, 100);
                row.SecondaryText = Resources.Dashboard_Search_Index_Rebuilding;
                try { view.NotifyChanged(); } catch (Exception error) { Logger.Warn("[StrmAssistantRebuild] Index UI progress: {0}", error.Message); }
            }), token).ConfigureAwait(false);
            return view.Refreshed(UiText($"Rebuilt {search.Count} search records.", $"已重建 {search.Count} 条搜索记录。", $"已重建 {search.Count} 筆搜尋記錄。"));
        }
        catch (OperationCanceledException) { return view.Refreshed(UiText("Index rebuild cancelled; native search remains active.", "已取消重建索引，原生搜索仍可用。", "已取消重建索引，原生搜尋仍可用。")); }
        catch (Exception error) { return view.Refreshed("Index rebuild failed: " + error.Message); }
    }
    private sealed class InlineProgress : IProgress<double>
    {
        private readonly Action<double> report;
        public InlineProgress(Action<double> report) => this.report = report;
        public void Report(double value) => report(value);
    }

    private async Task<string> RunGeneralCommand(PluginOptions options, string command, CancellationToken token)
    {
        if (command == Common.NetworkService.CheckCommand)
        {
            var network = NetworkService ?? throw new InvalidOperationException("Network service not initialized.");
            await network.CheckProxyAsync(options.NetworkOptions, token).ConfigureAwait(false);
            if (!ConfigurationStore.Save(options)) throw new IOException("Proxy result was not persisted.");
            return options.NetworkOptions.ProxyServerStatus.StatusText;
        }
        var update = PluginUpdateService ?? throw new InvalidOperationException("Update service not initialized.");
        if (command == "CheckPluginUpdate") return UpdateStatusText(await update.CheckAsync(token).ConfigureAwait(false));
        if (command == "UpdatePlugin") return UpdateStatusText(await update.UpdateAsync(new Progress<double>(p => Logger.Info("[StrmAssistantRebuild] Update progress: {0:F1}%", p)), token).ConfigureAwait(false));
        return "Unknown operation.";
    }
    private async Task<string> RunExperienceCommand(ExperienceEnhanceOptions options, string command, CancellationToken token)
    {
        if (command != "SplitMultiVersionMovies") return "Unknown operation.";
        var versions = MultiVersionService ?? throw new InvalidOperationException("Multi-version service not initialized.");
        var result = await versions.SplitMoviesAsync(new Progress<double>(p => Logger.Info("[StrmAssistantRebuild] Split progress: {0:F1}%", p)), token).ConfigureAwait(false);
        options.SplitMoviesProgress = new Emby.Web.GenericEdit.Elements.List.GenericItemList { new() { PrimaryText = $"Split {result.Done}/{result.Total}", SecondaryText = $"Failed: {result.Failed}" } };
        return $"Split {result.Done}/{result.Total}; failed {result.Failed}.";
    }
    private async Task<string> RunMetadataCommand(MetadataEnhanceOptions options, string command, CancellationToken token)
    {
        if (command != "ApplyDoubanMetadata") return "Unknown operation.";
        var douban = DoubanMetadataService ?? throw new InvalidOperationException("Douban service not initialized.");
        var items = libraries.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery { IncludeItemTypes = new[] { "Movie", "Series", "Episode" }, IsLocked = false, GroupByPresentationUniqueKey = false }, token);
        var done = 0; var changed = 0; var failed = 0;
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await WorkQueue.RunSingle(item.InternalId, ct => douban.ApplyAsync(item, ct), token).ConfigureAwait(false);
                if (result.Changed) changed++;
                if (result.Failed) failed++;
                done++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { failed++; done++; Logger.Warn("[StrmAssistantRebuild] Apply metadata item {0}: {1}", item.InternalId, ex.GetType().Name); }
        }
        options.DoubanScrapeProgress = new Emby.Web.GenericEdit.Elements.List.GenericItemList { new() { PrimaryText = $"Processed {done}/{items.Length}; changed {changed}", SecondaryText = $"Failed: {failed}" } };
        return $"Processed {done}/{items.Length}; changed {changed}; failed {failed}.";
    }
    private async Task<string> RunIntroCommand(IntroSkipOptions options, string command, CancellationToken token)
    {
        if (command != "ClearIntroMarkers") return "Unknown operation.";
        var api = ChapterApi ?? throw new InvalidOperationException("Marker service not initialized.");
        var scope = options.LibraryScope.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = libraries.GetVirtualFolders().Where(f => (scope.Count == 0 || scope.Contains(f.ItemId)) && string.Equals(f.CollectionType, "tvshows", StringComparison.OrdinalIgnoreCase)).Select(f => libraries.GetItemById(f.ItemId)?.InternalId ?? 0).Where(i => i > 0).ToArray();
        if (ids.Length == 0) return "No television libraries in scope.";
        var episodes = libraries.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery { ParentIds = ids, IncludeItemTypes = new[] { "Episode" }, Recursive = true, GroupByPresentationUniqueKey = false }, token);
        var done = 0; var failed = 0;
        foreach (var episode in episodes)
        {
            token.ThrowIfCancellationRequested();
            try { await api.ClearAsync(episode, token).ConfigureAwait(false); done++; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { failed++; Logger.Warn("[StrmAssistantRebuild] Clear marker item {0}: {1}", episode.InternalId, ex.Message); }
        }
        options.ClearIntroResult = new Emby.Web.GenericEdit.Elements.List.GenericItemList { new() { PrimaryText = $"Cleared {done}/{episodes.Length}", SecondaryText = $"Failed: {failed}" } };
        return $"Cleared {done}/{episodes.Length}; failed {failed}.";
    }
    private void OnGeneralOptionsSaved(object? sender, FileSavedEventArgs e) { ConfigureQueue(); ConfigureNetwork(); MediaCatchup?.Configure(); SearchCoordinator?.Configure(ConfigurationStore.GetOptions().ModOptions); RefreshWebAssets(); }
    private void ConfigureNetwork()
    {
        var options = ConfigurationStore.GetOptions();
        var priorCheck = options.NetworkOptions.LastProxyCheckSucceeded;
        NetworkService?.Configure(options.NetworkOptions);
        PatchHealth.Record("LocalDiscovery", LocalDiscoveryControl.Configure(host.ApplicationVersion, options.NetworkOptions.LocalDiscoveryUrl, Logger));
        PatchHealth.Record("NativeHttpRouting", NativeHttpRouting.Configure(host.ApplicationVersion, nativeHttpClient, options.NetworkOptions, out var nativeDetail));
        Logger.Info("[StrmAssistantRebuild] {0}", nativeDetail);
        if (NetworkService != null) { NetworkService.NativeRoutingDetail = nativeDetail; NetworkService.DiscoveryDetail = LocalDiscoveryControl.Detail; }
        // FileSaved runs after persistence and outside its lock. A changed endpoint invalidates the persisted check too.
        if (priorCheck != options.NetworkOptions.LastProxyCheckSucceeded) ConfigurationStore.Save(options);
    }
    private void OnDashboardOptionsSaved(object? sender, FileSavedEventArgs e) => MemoryMonitor?.Configure(DashboardStore.GetOptions().MemoryOptions.MemoryUsageLimitMB);
    private void ConfigureQueue()
    {
        var general = ConfigurationStore.GetOptions().GeneralOptions;
        WorkQueue.Configure(general.MaxConcurrentCount, general.Tier2MaxConcurrentCount, general.CooldownDurationSeconds);
    }
    private void OnWebOptionsSaved(object? sender, FileSavedEventArgs e)
    {
        if (e.Options is IntroSkipOptions) PlaySessionMonitor?.ResetObservations();
        if (e.Options is ExperienceEnhanceOptions) ConfigureUserExperience();
        if (e.Options is MetadataEnhanceOptions) ConfigureNativeMetadata();
        RefreshWebAssets(); MediaCatchup?.Configure();
    }
    private void ConfigureNativeMetadata()
    {
        var options = MetadataStore.GetOptions();
        PatchHealth.Record("NativeSeasonCast", Provider.NativeSeasonCastControl.Configure(host.ApplicationVersion, host.Plugins, options.EnhanceMovieDbPerson, out var seasonCast, options.EnableMovieDbEpisodeGroupExtendedInfo));
        Logger.Info("[StrmAssistantRebuild] {0}", seasonCast);
        PatchHealth.Record("PersonDetailRefresh", PersonDetailRefreshControl.Configure(host.ApplicationVersion, options.EnhanceMovieDbPerson, out var personDetail));
        Logger.Info("[StrmAssistantRebuild] {0}", personDetail);
        PatchHealth.Record("NativeMovieDbRouting", Provider.NativeMovieDbRouting.Configure(host.ApplicationVersion, host.Plugins, options, out var routing));
        PatchHealth.Record("NativeMovieDbCache", Provider.NativeMovieDbCache.Configure(host.ApplicationVersion, host.Plugins, options, Paths,
            Path.Combine(Path.GetDirectoryName(MetadataStore.OptionsFilePath)!, "metadata-cache"), out var cache));
        PatchHealth.Record("NativeImageCache", Provider.NativeImageCache.Configure(host.ApplicationVersion, options, Paths,
            Path.Combine(Path.GetDirectoryName(MetadataStore.OptionsFilePath)!, "metadata-cache"), out var imageCache));
        Logger.Info("[StrmAssistantRebuild] {0}", imageCache);
        PatchHealth.Record("NativePosterOrdering", Provider.NativePosterOrdering.Configure(host.ApplicationVersion, providers, options.PreferOriginalPoster, out var poster, host.Plugins));
        Logger.Info("[StrmAssistantRebuild] {0}", routing);
        Logger.Info("[StrmAssistantRebuild] {0}", cache);
        Logger.Info("[StrmAssistantRebuild] {0}", poster);
        ConfigureItemsQueryExtensions();
    }
    private void ConfigureItemsQueryExtensions()
    {
        PatchHealth.Record("SeasonCollections", SeasonCollectionQuery.Configure(host.ApplicationVersion, ExperienceStore.GetOptions().UIFunctionOptions.ShowCollectionsFromSeasons,
            MetadataStore.GetOptions().EnableDoubanAssistScraping, out var detail));
        Logger.Info("[StrmAssistantRebuild] {0}", detail);
    }
    internal string MetadataCacheRoot()
    {
        var configured = MetadataStore.GetOptions().MetadataBuildOptions.MetadataCacheRootFolder?.Trim();
        return string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetDirectoryName(MetadataStore.OptionsFilePath)!, "metadata-cache") : configured;
    }
    private Provider.OriginalOverrideFiles? originalOverrides;
    private readonly string embyCachePath;
    internal Provider.OriginalOverrideFiles OriginalOverrides => originalOverrides ??= new Provider.OriginalOverrideFiles(MetadataCacheRoot);
    internal long OriginalOverridesApplied => originalOverrides?.Applied ?? 0;
    private bool? lastDoubanChannel;
    /// <summary>
    /// M157 default file-change cleanup (original "Exclusive Delete on Change", A----------.cs:102844-102957): an item that
    /// had media loses its media state (size/runtime/bitrate/dimensions, streams, chapters, chapter images, Episode
    /// fingerprint files) and its media-info JSON; an item without media only its JSON. Files are renamed to recovery
    /// backups (the original deleted them).
    /// </summary>
    private void ClearOnFileChange(MediaBrowser.Controller.Entities.BaseItem item, bool hadMedia, CancellationToken token)
    {
        MediaInfoApi?.InvalidateJsonCache(item, token);
        if (!hadMedia) return;
        var images = item is MediaBrowser.Controller.Entities.Video video
            ? Web.Service.WebServiceSupport.ChapterImageFiles(video, repository.GetChapters(item, token), nativeFiles) : Array.Empty<string>();
        var retired = Web.Service.WebServiceSupport.RetireFiles(images, nativeFiles, token);
        try
        {
            Web.Service.MediaInfoService.ResetMediaInfo(item);
            repository.SaveMediaStreams(item.InternalId, new List<MediaBrowser.Model.Entities.MediaStream>(), token);
            if (item is MediaBrowser.Controller.Entities.Video) repository.SaveChapters(item.InternalId, true, new List<MediaBrowser.Model.Entities.ChapterInfo>());
            libraries.UpdateItems(new List<MediaBrowser.Controller.Entities.BaseItem> { item }, null!, ItemUpdateType.None, true, false, null!, token);
        }
        catch { Web.Service.WebServiceSupport.RestoreRetiredFiles(retired, nativeFiles); throw; }
        if (item is MediaBrowser.Controller.Entities.TV.Episode episode && FingerprintApi != null)
            _ = FingerprintApi.ResetEpisodeFingerprintStateAsync(episode, CancellationToken.None).ContinueWith(t => { if (t.IsFaulted) Logger.Warn("[StrmAssistantRebuild] Fingerprint reset after file change failed: {0}", t.Exception?.GetBaseException().Message); }, TaskScheduler.Default);
        Logger.Info("[StrmAssistantRebuild] Exclusive Delete on Change: {0}", item.Path);
    }
    private IMediaSourceManager? mediaSources;
    private string[] embyRoots = Array.Empty<string>();
    private void ConfigureUserExperience()
    {
        var options = ExperienceStore.GetOptions();
        UserExperienceService?.Configure(options, host.ApplicationVersion);
        UserPreferenceService?.Configure(options.UIFunctionOptions);
        PatchHealth.Record("PluginUpdateSuppression", PluginUpdateSuppression.Configure(host.ApplicationVersion, options.SuppressPluginUpdates, host.Plugins, Logger));
        PatchHealth.Record("NativeFolderGrouping", NativeFolderGrouping.Configure(host.ApplicationVersion, options.FolderVideoGroupingLimit, Logger));
        PatchHealth.Record("BoxsetAutoCreation", BoxsetAutoCreationControl.Configure(host.ApplicationVersion, options.UIFunctionOptions.NoBoxsetsAutoCreation, out var boxsets));
        Logger.Info("[StrmAssistantRebuild] {0}", boxsets);
        ConfigureItemsQueryExtensions();
        PatchHealth.Record("BoxsetUserViews", BoxsetUserViews.Configure(host.ApplicationVersion, options.UIFunctionOptions.NoBoxsetsAutoCreation,
            options.EnableDoubanChannel ? null : Provider.DoubanChannel.ChannelName, out var boxsetViews));
        Logger.Info("[StrmAssistantRebuild] {0}", boxsetViews);
        PatchHealth.Record("NativeChannelItemRefresh", Provider.NativeChannelItemRefresh.Configure(host.ApplicationVersion, options.EnableDoubanChannel, out var doubanChannel));
        Logger.Info("[StrmAssistantRebuild] {0}", doubanChannel);
        // Original: toggling EnableDoubanChannel raises ContentChanged so Emby re-lists (fills or clears) the channel.
        if (lastDoubanChannel != null && lastDoubanChannel != options.EnableDoubanChannel) Provider.DoubanChannel.Current?.NotifyContentChanged();
        lastDoubanChannel = options.EnableDoubanChannel;
        PatchHealth.Record("DisplayOrdering", DisplayOrderingControl.Configure(host.ApplicationVersion, options.UIFunctionOptions.OptimizeMediaItemOrder, options.UIFunctionOptions.OptimizeItemOrderPreferences, out var ordering));
        Logger.Info("[StrmAssistantRebuild] {0}", ordering);
        PatchHealth.Record("NotificationEnhancement", NotificationEnhancement.Configure(host.ApplicationVersion, options.EnhanceNotificationSystem, users, libraries, Logger));
        PatchHealth.Record("MetadataEditNotification", MetadataEditNotification.Configure(host.ApplicationVersion, options.EnhanceNotificationSystem, options.MetadataUpdateTrackedFields, libraries, notifications, Logger, userManager:users));
        PatchHealth.Record("ImageEditNotification", ImageEditNotification.Configure(host.ApplicationVersion, options.EnhanceNotificationSystem, libraries, notifications, Logger, userManager:users));
        PatchHealth.Record("SubtitleSearchOptimization", SubtitleSearchOptimization.Configure(host.ApplicationVersion, options.OptimizeSubtitle, host.Plugins, libraries, Logger));
        if (mediaSources != null)
        {
            PatchHealth.Record("DeepDelete", DeepDeleteControl.Configure(host.ApplicationVersion, options.EnableDeepDelete, libraries, mediaSources, nativeFiles, Logger, embyRoots, out var deepDelete));
            Logger.Info("[StrmAssistantRebuild] {0}", deepDelete);
        }
        PatchHealth.Record("LibraryScopePlayProgress", LibraryScopePlayProgressControl.Configure(host.ApplicationVersion, options.LibraryScopePlayProgress, libraries,
            ScopedEpisodeUserDataKey.RetainedCount > 0, out var libraryScope));
        Logger.Info("[StrmAssistantRebuild] {0}", libraryScope);
        PatchHealth.Record("MissingEpisodeGroup", Provider.MissingEpisodeGroupControl.Configure(host.ApplicationVersion, host.Plugins, options.UIFunctionOptions.EnhanceMissingEpisodes,
            () => MetadataStore.GetOptions(), () => EpisodeGroupService, Logger, out var missingEpisodes));
        Logger.Info("[StrmAssistantRebuild] {0}", missingEpisodes);
    }
    public void RefreshWebAssets()
    {
        var media = MediaStore.GetOptions();
        try
        {
            var applied = PatchHealth.Record("StrmThumbnail", StrmThumbnailCompatibility.Configure(host.ApplicationVersion, media.EnableImageCapture, out var detail));
            if (applied) Logger.Info("[StrmAssistantRebuild] {0}", detail);
            else Logger.Warn("[StrmAssistantRebuild] {0}", detail);
        }
        catch (Exception ex) { PatchHealth.Record("StrmThumbnail", false); Logger.Warn("[StrmAssistantRebuild] STRM BIF compatibility inactive: {0}", ex.Message); }
        try
        {
            ExclusiveEligibility.PersistMode = media.PersistMediaInfoMode;
            var supported = ExclusiveMediaControl.ValidateFeatures(media.ExclusiveControlFeatures, out _);
            var blockRemote = (media.ExclusiveControlFeatures ?? "").Split(',', StringSplitOptions.TrimEntries).Contains("BlockRemoteFetcher", StringComparer.OrdinalIgnoreCase);
            ExclusiveRemoteFetcherControl.ConfigureWithDefaults(host.ApplicationVersion, media.ExclusiveExtract && supported, blockRemote ? "BlockRemoteFetcher" : "", out var remoteDetail);
            Logger.Info("[StrmAssistantRebuild] {0}", remoteDetail);
            PatchHealth.Record("ExclusiveChapterImage", ExclusiveChapterImageControl.Configure(host.ApplicationVersion, media.ExclusiveExtract && supported, out var chapterDetail));
            Logger.Info("[StrmAssistantRebuild] {0}", chapterDetail);
            // Project only complete supported selections; invalid mixtures retain their full rejection context.
            var alternativeSelection = ExclusiveMediaControl.TryGetAlternativeSelection(media.ExclusiveControlFeatures, out var alternativeSaverFeatures);
            ExclusiveSaverControl.Configure(host.ApplicationVersion, media.ExclusiveExtract, alternativeSelection ? alternativeSaverFeatures :
                ExclusiveMediaControl.TryGetNoPersistIntegrationSelection(media.ExclusiveControlFeatures, out var noPersistSaverFeatures) ? noPersistSaverFeatures : media.ExclusiveControlFeatures, out var saverDetail);
            Logger.Info("[StrmAssistantRebuild] {0}", saverDetail);
            var applied = PatchHealth.Record("ExclusiveMediaControl", ExclusiveMediaControl.ConfigureWithAlternativeConsumer(host.ApplicationVersion, media, out var detail,
                alternativeConsumerReady: MediaCatchup?.IsAlternativeConsumerReady == true && ExclusiveSaverControl.IsEnabled));
            if (!applied) { ExclusiveSaverControl.Disable(); ExclusiveRemoteFetcherControl.Disable(); ExclusiveChapterImageControl.Disable(); }
            ExclusiveSaverControl.ConfigureNativeRestore(applied && media.ExclusiveExtract &&
                !ExclusiveMediaControl.TryGetNoPersistIntegrationSelection(media.ExclusiveControlFeatures, out _) &&
                (media.PersistMediaInfoMode == MediaInfoExtractOptions.PersistMediaInfoOption.Restore.ToString() ||
                 media.PersistMediaInfoMode == MediaInfoExtractOptions.PersistMediaInfoOption.Default.ToString()) && MediaInfoApi != null
                    ? MediaInfoApi.TryIntegrateAfterNativeRefreshAsync : null,
                (id, error) => Logger.Warn("[MediaInfo] Native refresh succeeded; optional " +
                    (media.PersistMediaInfoMode == MediaInfoExtractOptions.PersistMediaInfoOption.Restore.ToString() ? "restore" : "persist") +
                    " failed for item {0}: {1}", id, error.GetType().Name),
                System.Text.Json.JsonSerializer.Serialize(new[] { media.PersistMediaInfoMode, media.MediaInfoJsonRootFolder,
                    media.ExclusiveControlFeatures, media.MediaInfoExcludeMediaContainers, media.MediaInfoExcludeKeywords }));
            NativeChangedProbeControl.Configure(host.ApplicationVersion, providers, libraries, nativeFiles,
                () => { var current = MediaStore.GetOptions(); return (current.ExclusiveExtract, current.ExclusiveControlFeatures, current.PersistMediaInfoMode); }, out var changedProbeDetail);
            Logger.Info("[StrmAssistantRebuild] {0}", changedProbeDetail);
            if (applied) Logger.Info("[StrmAssistantRebuild] {0}", detail);
            else Logger.Warn("[StrmAssistantRebuild] {0}", detail);
        }
        catch (Exception ex)
        {
            NativeChangedProbeControl.Disable();
            ExclusiveMediaControl.Disable(); ExclusiveSaverControl.Disable(); ExclusiveRemoteFetcherControl.Disable(); ExclusiveChapterImageControl.Disable();
            PatchHealth.Record("ExclusiveMediaControl", !media.ExclusiveExtract);
            Logger.Warn("[StrmAssistantRebuild] Exclusive control inactive: {0}", ex.Message);
        }
        MediaCatchup?.Configure(); // Consumer readiness -> gate configuration -> queue activation, including startup.
        var metadata = MetadataStore.GetOptions();
        var intro = IntroStore.GetOptions();
        var experience = ExperienceStore.GetOptions();
        DashboardAssets?.Refresh(WebOptionsSnapshot.Enabled(media, metadata, intro, experience, SeasonCollectionQuery.IsDoubanFilterEnabled), WebOptionsSnapshot.Create(media, metadata, intro, experience, ConfigurationStore.GetOptions().ModOptions, SeasonCollectionQuery.IsDoubanFilterEnabled, IntroRefreshBridge.IsEnabled));
    }
    public override void OnUninstalling() { StopEpisodeBatches(); IntroRefreshBridge.Disable(); dashboardLifetime.Cancel(); PlaySessionMonitor?.Dispose(); FavoriteNotifications?.Dispose(); MetadataEditNotification.Remove(); ImageEditNotification.Remove(); SubtitleSearchOptimization.Disable(); PersonDetailRefreshControl.Disable(); SeasonCollectionQuery.Disable(); Provider.NativeSeasonCastControl.Disable(); Provider.NativeMovieDbCache.Disable(); Provider.NativeMovieDbRouting.Disable(); Provider.NativePosterOrdering.Disable(); LocalDiscoveryControl.Disable(); NativeHttpRouting.Disable(); NativeChangedProbeControl.Disable(); ExclusiveRemoteFetcherControl.Disable(); ExclusiveChapterImageControl.Disable(); ExclusiveMediaControl.Disable(); ExclusiveSaverControl.Disable(); StrmThumbnailCompatibility.Disable(); DashboardAssets?.Restore(); base.OnUninstalling(); }
    private void StopEpisodeBatches()
    {
        TaskCompletionSource<bool>? completion=null;Task cleanup;
        lock(episodeShutdownGate)
        {
            if(episodeShutdown==null||episodeShutdown.IsFaulted)
            {
                completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref episodeShutdown,completion.Task);
            }
            cleanup=episodeShutdown!;
        }
        if(completion!=null)
        {
            var errors=new List<Exception>();
            void Signal(Action action){try{action();}catch(Exception error){errors.Add(error);LogEpisodeShutdownFailure(error);}}
            // Publish the task before cancellation callbacks can re-enter teardown.
            Signal(EpisodeScopeStartupRecovery.Stop);Signal(EpisodeScopeNewItems.Stop);Signal(WorkQueue.Dispose);
            _=FinishEpisodeShutdown(completion,errors);
        }
        _=ObserveEpisodeShutdown(cleanup);
    }
    private async Task FinishEpisodeShutdown(TaskCompletionSource<bool> completion,List<Exception> errors)
    {
        try{await EpisodeScopeNewItems.Completion.ConfigureAwait(false);}catch(Exception error){errors.Add(error);}
        try{await EpisodeScopeBatches.StopAndClearAsync(CancellationToken.None).ConfigureAwait(false);}catch(Exception error){errors.Add(error);}
        if(errors.Count==0)completion.TrySetResult(true);
        else completion.TrySetException(new AggregateException("Episode worker shutdown did not fully complete.",errors));
    }
    private async Task ObserveEpisodeShutdown(Task cleanup)
    {
        try { await cleanup.ConfigureAwait(false); }
        catch (Exception error) { LogEpisodeShutdownFailure(error); }
    }
    private void LogEpisodeShutdownFailure(Exception error)
    {
        try { Logger.Warn("[StrmAssistantRebuild] Episode batch shutdown signal/cleanup error: {0}; state={1}",error.GetType().Name,EpisodeScopeBatches.ShutdownState); }
        catch { /* Keep teardown moving even when the logger is already shutting down. */ }
    }
    public void Dispose()
    {
        StopEpisodeBatches();
        IntroRefreshBridge.Disable();
        dashboardLifetime.Cancel();
        FavoriteNotifications?.Dispose();
        DashboardStore.FileSaved -= OnDashboardOptionsSaved;
        if (MemoryMonitor != null) WorkQueue.WorkStarted -= MemoryMonitor.NotifyActivity;
        MemoryMonitor?.Dispose();
        Activity?.Dispose();
        PluginUpdateSuppression.Disable();
        NativeFolderGrouping.Disable();
        BoxsetAutoCreationControl.Disable();
        BoxsetUserViews.Disable();
        SeasonCollectionQuery.Disable();
        DisplayOrderingControl.Disable();
        NotificationEnhancement.Remove();
        MetadataEditNotification.Remove();
        ImageEditNotification.Remove();
        SubtitleSearchOptimization.Disable();
        LocalDiscoveryControl.Disable();
        NativeHttpRouting.Disable();
        Provider.NativeMovieDbRouting.Disable();
        Provider.NativeMovieDbCache.Disable();
        Provider.NativeImageCache.Disable();
        Provider.NativeSeasonCastControl.Disable();
        PersonDetailRefreshControl.Disable();
        Provider.NativePosterOrdering.Disable();
        Provider.MissingEpisodeGroupControl.Disable();
        LibraryScopePlayProgressControl.Disable();
        DeepDeleteControl.Disable();
        ExclusiveFileChange.Configure(null);
        MetadataEnhancePipeline.TaskItemMarks = null;
        Provider.EpisodeGroupService.OriginalOverrideRoot = null;
        Provider.NativeChannelItemRefresh.Disable();
        Provider.DoubanChannelService.Current = null;
        ScopeSelection.ConfigureUsers(null);
        SearchCoordinator?.Dispose();
        UserExperienceService?.Dispose();
        UserPreferenceService?.Dispose();
        MetadataPipeline?.Dispose();
        PlaySessionMonitor?.Dispose();
        MediaCatchup?.Dispose();
        ConfigurationStore.FileSaved -= OnGeneralOptionsSaved;
        MediaStore.FileSaved -= OnWebOptionsSaved;
        MetadataStore.FileSaved -= OnWebOptionsSaved;
        IntroStore.FileSaved -= OnWebOptionsSaved;
        ExperienceStore.FileSaved -= OnWebOptionsSaved;
        DashboardAssets?.Restore();
        NativeChangedProbeControl.Disable();
        ExclusiveMediaControl.Disable();
        ExclusiveSaverControl.Disable();
        ExclusiveRemoteFetcherControl.Disable();
        ExclusiveChapterImageControl.Disable();
        StrmThumbnailCompatibility.Disable();
        OutboundHttp.Dispose();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
}
