using System.Collections.ObjectModel;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using StrmAssistantRebuild.Options;
using StrmAssistantRebuild.Provider;

namespace StrmAssistantRebuild.Common;

/// <summary>Pass actual composition-root references, freshly captured for each view. No option-derived booleans.</summary>
public sealed record RuntimeServicePresence
{
    public MediaInfoApi? MediaInfo { get; init; }
    public ImageCaptureApi? Images { get; init; }
    public VideoThumbnailApi? Thumbnails { get; init; }
    public MetadataEnhancePipeline? Metadata { get; init; }
    public DoubanMetadataService? Douban { get; init; }
    public MetadataLanguageService? Languages { get; init; }
    public NfoActorService? Nfo { get; init; }
    public FingerprintApi? Fingerprints { get; init; }
    public SearchCoordinator? Search { get; init; }
    public IItemRepository? Repository { get; init; }
    public UserExperienceService? Experience { get; init; }
    public UserPreferenceService? Preferences { get; init; }
    public MultiVersionService? Versions { get; init; }
    public FavoriteUpdateNotification? Favorites { get; init; }
    public MarkerUpdateNotification? MarkerNotifications { get; init; }
    public DeepDeleteNotification? DeepDeleteNotifications { get; init; }
    public NetworkService? Network { get; init; }
    public DashboardAssetManager? Dashboard { get; init; }
    public EpisodeScopeBatch? EpisodeBatches { get; init; }
    public EpisodeScopeStartup? StartupRecovery { get; init; }
    public EpisodeScopeCatchup? NewEpisodes { get; init; }
    public MediaCatchupService? MediaCatchup { get; init; }
}

public sealed record RuntimeCapability(bool Supported, string Detail);

/// <summary>
/// View-time, host-owned evidence, not configuration. Compatibility is not activation and a platform
/// signature is not business completeness. Never calls Configure, GetExports (creates instances),
/// provider metadata fetches, or a detour installation/removal API.
/// </summary>
public sealed class RuntimeCapabilities
{
    private static readonly Version HostVersion = new(4, 10, 2, 0);
    private readonly Dictionary<string, RuntimeCapability> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> fieldGates = new(StringComparer.Ordinal);
    private readonly RuntimeServicePresence services;
    public IReadOnlyDictionary<string, RuntimeCapability> Entries { get; }
    public string Summary => string.Join("\n", entries.Select(e => e.Key + "=" + (e.Value.Supported ? "supported" : "unavailable") + ": " + e.Value.Detail));
    public IReadOnlyList<string> Conflicts { get; }

    public RuntimeCapabilities(Version hostVersion, IEnumerable<IPlugin>? loadedPlugins, IProviderManager? providerManager, RuntimeServicePresence services)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
        Entries = new ReadOnlyDictionary<string, RuntimeCapability>(entries);
        var plugins = (loadedPlugins ?? Array.Empty<IPlugin>()).Where(p => p != null).ToArray();
        // Names are exact assembly identities of already loaded plugin objects, never substring/disk discovery.
        Conflicts = Array.AsReadOnly(plugins.Select(p => p.GetType().Assembly.GetName().Name ?? "")
            .Where(n => n is "StrmExtract" or "InfuseSync" or "StrmAssistant" or "StrmAssistantPro").Distinct(StringComparer.Ordinal).ToArray());
        Set("Host", hostVersion == HostVersion, hostVersion == HostVersion ? "Recorded public API baseline 4.10.2.0; not full feature parity." : "Host version differs from recorded 4.10.2.0: " + hostVersion);
        MetadataPluginSummary[] inventory = Array.Empty<MetadataPluginSummary>();
        try
        {
            if (providerManager != null) inventory = providerManager.GetAllMetadataPlugins() ?? Array.Empty<MetadataPluginSummary>();
            Set("ProviderManager", providerManager != null, providerManager == null ? "Service missing / initialization incomplete." : "Public registered-provider inventory read; no remote metadata calls.");
        }
        catch (Exception ex) { Set("ProviderManager", false, "Provider inventory failed: " + ex.GetType().Name + ": " + ex.Message); }

        Dependency("MovieDb", "TheMovieDb", "2.0.0.0", "bf190786-42b7-4923-aea6-1851feea4259", "759EDC7BF7A3B43A2BBDC34154471811F1931A7A7EF59DD25E9B00B4A6B5FC86",
            "TheMovieDb", plugins, inventory, false, typeof(Movie), typeof(Series), typeof(Season), typeof(Episode), typeof(Person));
        Dependency("Tvdb", "TheTVDB", "1.6.7.0", "7245186e-d86d-4385-8dfb-3582ab8e1c2a", "FE796EC38E8C6A634C111A103E60A93570ED9625236E11A3E4BC274C2889498C",
            "TheTVDB", plugins, inventory, false, typeof(Movie), typeof(Series), typeof(Season), typeof(Episode), typeof(Person));
        Dependency("NfoMetadata", "Nfo Metadata", "1.0.88.0", "50621a3b-1fde-4439-a9f2-f2cb6b2fdad4", "98AB5E4BADD07F8566F235CB202C78F74579E792DF59D9FA84C548A9B6F569C4",
            "Nfo", plugins, inventory, true, typeof(Movie), typeof(Series), typeof(Season), typeof(Episode));
        Dependency("OpenSubtitles", "Open Subtitles", "1.0.71.0", "9748e51c-a9ed-4618-8ae7-644ab2bdcf3c", "0214AA7E918DE6B61071553F122B6FA322FD8DAC099E6DD5945006DD600AD1F9",
            "Open Subtitles", plugins, inventory, false);

        Set("Platform.Thumbnail", StrmThumbnailCompatibility.Probe(hostVersion, out var thumbnail), thumbnail);
        Set("Platform.Exclusive", ExclusiveMediaControl.Probe(hostVersion, out var exclusive), exclusive);
        Set("Activation.ExclusiveSaver", Good("Host") && ExclusiveSaverControl.IsEnabled, ExclusiveSaverControl.Detail);
        Set("Platform.Search", ChineseSearchCompatibility.Probe(hostVersion, services.Repository, out var search), search);
        Set("Platform.People", UserExperienceGetterCompatibility.Probe(hostVersion, out var people), people);
        Set("Platform.MultiVersion", UserExperienceGetterCompatibility.ProbeMultiVersion(hostVersion, out var versions), versions);
        Set("Activation.NativeFolderGrouping", Good("Host") && NativeFolderGrouping.IsSupported, NativeFolderGrouping.Detail);
        Set("Activation.BoxsetAutoCreation", Good("Host") && BoxsetAutoCreationControl.IsEnabled, BoxsetAutoCreationControl.Detail + " SuppressedRuns=" + BoxsetAutoCreationControl.SuppressedRuns);
        Set("Activation.BoxsetUserViews", Good("Host") && BoxsetUserViews.IsEnabled, BoxsetUserViews.Detail);
        Set("Activation.DisplayOrdering", Good("Host") && DisplayOrderingControl.IsEnabled, DisplayOrderingControl.Detail + " ChangedNaturalComparisons=" + DisplayOrderingControl.ChangedNaturalComparisons + "; ChangedCollectionSorts=" + DisplayOrderingControl.ChangedCollectionSorts);
        Set("Backend.MarkerNotifications", services.MarkerNotifications != null, "Verified changed playback marker outcome only; native opt-in event, fresh user/episode visibility. Emitted=" + (services.MarkerNotifications?.EmittedRequests ?? 0) + "; OSD commands completed=" + (services.MarkerNotifications?.DisplayedRequests ?? 0) + "; exact session/user binding and 500ms native DisplayMessage. External delivery, full clients and other marker origins remain separate.");
        Set("Backend.DeepDeleteNotifications", services.DeepDeleteNotifications != null, "Local DeleteVersion target retirement + repository commit; live pre-delete visibility/library snapshot, retained recovery backups, opt-in notification master. Emitted=" + (services.DeepDeleteNotifications?.EmittedRequests ?? 0) + "; broader native deep-delete hooks remain incomplete.");
        var fingerprint = "Fingerprint service missing / initialization incomplete.";
        var fingerprintOk = services.Fingerprints?.Probe(out fingerprint) == true;
        Set("Platform.Fingerprint", Good("Host") && fingerprintOk, fingerprint);
        Set("Backend.ScopedFingerprintTask", Good("Host") && fingerprintOk, "Server-bound one-shot TaskOptions select a physical Series or positive Season within the configured marker scope; native fingerprint-only, without global enumeration or IntroDb import. Does not clear or replace existing intro/credits markers.");
        Set("Backend.LocalStrmIntroRebuild", Good("Host") && fingerprintOk,
            "Explicit Rebuild local-file STRM subset: original episode identity, bounded wrapper proof, official static source and native STRM mount; source changes fail validation. No global native shortcut-unlock, remote/live/header-dependent source or disc support is implied. Eligibility is checked per input; actual host MP4 path is tested separately.");
        Set("Backend.RemoteStrmIntroRebuild", Good("Host") && fingerprintOk,
            "Restricted headerless HTTP(S) STRM input through native FFmpeg: exact initial raw URL and bounded wrapper are bound; userinfo/fragment, extra headers and live/alternate inputs are excluded; query inputs additionally require the protected signed-generation adapter. No HEAD/download layer; remote content, ETag, final redirects, native proxy and cancellation-drain guarantees are not inferred. Per-input eligibility and native runtime behavior remain separate.");
        Set("Backend.SignedQueryFingerprint", Good("Host") && services.Fingerprints?.CanProtectSignedInput() == true,
            "Query URL generation uses operation-local native provider and native runner with metadata-only loggers; raw URL spelling is retained. No global logger/manager replacement. Functional STRM/cache input and local process argv may still contain the address; unrelated native probing, headers, TLS trust and final redirect identity remain separate.");
        Set("Backend.IncrementalStrmFingerprintTask", Good("Host") && fingerprintOk,
            "Ordinary plugin fingerprint task routes selected STRM/mixed seasons through bounded incremental generation and target-only detection. Existing intro references are read-only; no fingerprint Reset/retirement; concurrent intro rows are preserved in the native writer. Original selected IDs and settings stay bound; ParallelMarkerComputation chooses a bounded season-worker snapshot (1 when disabled, Tier2 limit clamped to 1..20 when enabled), while shared generation/calculation quotas still apply. Per-season work bounds are not a whole-library atomic budget, and active-season diagnostics are not encoder concurrency. Native host-wide shortcut eligibility, header-authenticated remote inputs and disc inputs remain separate; protected query URL and headerless HTTP(S) subsets are described independently.");
        var introRefresh = IntroRefreshBridge.LastReport;
        Set("Backend.IntroRefreshBridge", Good("Host") && IntroRefreshBridge.IsEnabled,
            IntroRefreshBridge.Detail + "; Pending=" + IntroRefreshBridge.PendingCount + "; Last=" + (introRefresh?.State ?? "None") +
            "; Target=" + (introRefresh?.TargetId.ToString() ?? "None") + "; Reason=" + (introRefresh?.Reason ?? "None"));
        var scopedKey = ScopedEpisodeUserDataKey.Probe(hostVersion, out var scopedKeyDetail);
        Set("Platform.ScopedEpisodeKeySave", Good("Host") && scopedKey, scopedKeyDetail);
        Set("Activation.RetainedEpisodeKeys", ScopedEpisodeUserDataKey.RetainedCount > 0, "Explicit in-memory retained key snapshots=" + ScopedEpisodeUserDataKey.RetainedCount + ". " + ScopedEpisodeUserDataKey.Detail + " Runtime policy only; journal startup recovery requires its separate explicit opt-in and does not enable library-wide migration.");
        Set("Backend.EpisodeScopePlan", Good("Host") && scopedKey, "Read-only explicit Episode selection planner: native/stored/library-domain keys, ambiguous membership and potential presentation conflicts. No automatic scan, data copy, mapping write or global activation.");
        var batch=services.EpisodeBatches?.LastReport;
        Set("Backend.EpisodeScopeBatch", Good("Host") && scopedKey && services.EpisodeBatches!=null, "Explicit bounded item/library Apply/Restore batches and item-only Append preserving existing rules use shared admission and durable intents; whole-journal recovery shares shutdown. Last="+(batch?.Phase??"Idle")+"; operation="+(batch?.Operation.ToString()??"None")+"; retained="+(batch?.Retained??0)+"; retained count authoritative="+(batch?.RetainedCountAuthoritative??true)+"; observer errors="+(batch?.ProgressErrors??0)+"; shutdown="+(services.EpisodeBatches?.ShutdownState??"Unavailable")+". Partial/cancelled results are not rollback or global activation. Startup recovers only explicitly opted-in journals; no global-option wiring or native writer drain.");
        var added=services.NewEpisodes;
        Set("Backend.EpisodeScopeCatchup", Good("Host") && added!=null, "Explicit runtime or journal-bound startup ItemAdded processing for selected retained library ID/GUID pairs only. State="+(added?.Status??"Unavailable")+"; enabled="+(added?.Enabled??false)+"; pending="+(added?.PendingCount??0)+"; applied="+(added?.AppliedItems??0)+"; "+added?.Reason+" Startup enablement requires matching persisted library bindings and successful opted-in recovery; no global activation or automatic retry.");
        var startup=services.StartupRecovery?.Status;
        Set("Backend.EpisodeScopeStartup", Good("Host") && services.StartupRecovery!=null, "One asynchronous startup recovery attempt for an explicitly opted-in existing journal. State="+(startup?.State??"Unavailable")+"; last retained="+(startup?.Recovery?.Retained??0)+"; "+startup?.Detail+" Only explicit persisted selected-library bindings may enable subsequent ItemAdded handling; no broad scan or global scope activation.");
        var fingerprintCatchup = services.MediaCatchup?.LastFingerprintBatch;
        Set("Backend.FingerprintCatchup", Good("Host") && fingerprintOk && services.MediaCatchup != null,
            "Event-driven fingerprint catch-up uses the existing delayed worker and exact selected STRM/mixed-season incremental pipeline. Last=" +
            (fingerprintCatchup?.State ?? "None") + "; Seasons=" + (fingerprintCatchup?.Seasons.Count ?? 0) +
            "; Pending=" + (services.MediaCatchup?.PendingCount ?? 0) + "; Busy=" + (services.MediaCatchup?.IsBusy ?? false) +
            ". Physical-only native behavior remains separate; cancellation signals the worker, not a synchronous Plugin.Dispose drain guarantee.");
        Set("Backend.Media", Good("Host") && services.MediaInfo != null && services.Images != null && services.Thumbnails != null,
            "Assembled MediaInfo/ImageCapture/VideoThumbnail=" + Present(services.MediaInfo, services.Images, services.Thumbnails) + "; supports owned extraction, JSON, image-specific exclusions, guarded missing-artwork capture, source-derived aspect crops and images/BIF; independent of options OFF. New Default caches produced after a successful probe can carry a stable raw local carrier length/UTC-mtime snapshot; paired-cache hits compare current carrier metadata rather than stored DB modification time. Public export does not assert physical freshness, legacy documents retain their DB/null predicate, and same-stat rewrites or STRM target changes are outside this check. Default mode can also create an absent cache after a healthy ordinary native-success refresh of an existing exact Movie, unless NoPersistIntegration is selected; this create-only DB/media snapshot carries no physical-probe pair and never replaces an existing destination. Explicit Restore mode with healthy exclusive controls, unless NoPersistIntegration is selected, also supports awaited ordinary native-success media-only recovery for existing exact Movies, Episodes or Audio with empty cached/repository streams and null runtime, using one identity-matched versioned document and a nonblocking shared item/quota lease. Episode raw SeriesId/InternalAlbumId and Audio raw InternalAlbumId links must remain unchanged; valid zero/fallback values are not synthesized or rejected. Audio documents require internal audio without internal video; bounded exported embedded text may be present but is not restored, and embedded artwork is excluded. Busy, new, FullRefresh, manual/owned and invalidated scopes skip this optional recovery; chapters, failure metadata and artwork are not restored. A separately verified default-exclusive native path permits one primary probe for an existing ordinary physical non-STRM Movie when its file time advances across a second, the registered FFProbe Fetch and request match, both automatic refresh intervals are nonpositive, the feature selection is empty or solely NoPersistIntegration, and persistence is None/Default. Images, other requests and selections retain the normal gate; native media/chapter/date updates remain native. This is not default file-change clearing or complete IgnoreFileChange/ExtractOnFileChange support.");
        Set("Backend.Experience", services.Experience != null && services.Preferences != null && services.Versions != null,
            "Assembled UserExperience/UserPreference/MultiVersion=" + Present(services.Experience, services.Preferences, services.Versions) + "; previous-episode marking uses native user-scoped display-season/Series-PUK selection for unplayed OR nonzero-progress entries without changing play counts; nonempty stored keys and source/candidate visibility are required, not library-scoped userdata migration; existing preferences/merge branches; response-only version format/quality projection=" + entries["Platform.MultiVersion"].Supported + "; " + entries["Platform.MultiVersion"].Detail);
        Set("Backend.Search", Good("Platform.Search") && services.Search != null,
            "SearchCoordinator=" + Present(services.Search) + "; platform=" + entries["Platform.Search"].Detail + "; index activation is separate from compatibility.");
        Set("Backend.Network", services.Network != null, "NetworkService=" + Present(services.Network) + "; plugin-owned HTTP transport assembled; native routing status is independent.");
        Set("Activation.SeasonCollections", Good("Host") && SeasonCollectionQuery.IsEnabled, SeasonCollectionQuery.Detail);
        Set("Activation.DoubanFilter", Good("Host") && SeasonCollectionQuery.IsDoubanFilterEnabled, SeasonCollectionQuery.DoubanDetail);
        Set("Activation.LocalDiscovery", Good("Host") && LocalDiscoveryControl.IsEnabled, LocalDiscoveryControl.Detail);
        Set("Activation.NativeHttpRouting", Good("Host") && NativeHttpRouting.IsEnabled, NativeHttpRouting.Detail + " NewHandlers=" + NativeHttpRouting.AppliedNewHandlerCount);
        Set("Activation.NativeMovieDbRouting", Good("Host") && NativeMovieDbRouting.IsEnabled, NativeMovieDbRouting.Detail);
        Set("Activation.NativeMovieDbCache", Good("Host") && NativeMovieDbCache.IsEnabled, NativeMovieDbCache.Detail);
        Set("Activation.NativeImageCache", Good("Host") && Provider.NativeImageCache.IsEnabled, Provider.NativeImageCache.Detail +
            " Restored=" + Provider.NativeImageCache.Restored + "; remote skipped=" + Provider.NativeImageCache.SkippedRemote + "; written=" + Provider.NativeImageCache.Written + ".");
        Set("Activation.NativeSeasonCast", Good("Host") && NativeSeasonCastControl.IsEnabled, NativeSeasonCastControl.Detail);
        Set("Activation.PersonDetailRefresh", Good("Host") && PersonDetailRefreshControl.IsEnabled, PersonDetailRefreshControl.Detail);
        Set("Activation.NativePosterOrdering", Good("Host") && NativePosterOrdering.IsEnabled, NativePosterOrdering.Detail);
        Set("Activation.NativeApiQueryEntry", Good("Host") && NativePosterOrdering.IsApiQueryEntryEnabled,
            "Optional pinned, already-loaded RemoteImages API caller protects late query entry. Movie operations begin at the pinned native initial GetImages MoveNext and publish only at its normal completion. No ProviderManager GetImages MoveNext rewrite; original cancellation/EH remains native.");
        Set("Activation.NativeRefreshProviderExpansion", Good("Host") && NativePosterOrdering.IsRefreshProviderExpansionEnabled,
            "Only matching queries in the pinned non-nested synchronous image-refresh scope clear ProviderName, including matching synchronous callbacks. Queries outside that scope and post-kickoff continuations retain provider filters; later ambiguity does not roll back prior mutations. Native type/library/disabled-provider admission remains; the original deep-nesting counter branch is not reproduced, and the native refresh MoveNext is observed for integrity but not patched.");
        Set("Activation.NativeBackdropExpansion", Good("Host") && NativePosterOrdering.IsBackdropExpansionEnabled,
            "Entry null/Backdrop queries in verified TMDB Movie/Series image operations can admit labelled backdrops and fill raw language on newly allocated native results. Source DTO/cache ISO fields and Thumb results are unchanged; no global path dictionary or extra origin fetch. Explicit other image types and unsupported callers retain native backdrop admission; later option changes do not roll back prior admissions.");
        Set("Activation.NativeSeriesPosterOrdering", Good("Host") && NativePosterOrdering.IsSeriesEnabled,
            "Series provider original_language comes from the existing detail response or validated default-language cache metadata. Missing valid provider origin can use the separate Movie/Series OriginalTitle policy (kana, Hangul, BMP Han, else nonempty en); that heuristic is not provider authority or cached language. Native image-operation success controls provider publication; no separate origin request or image write.");
        Set("Activation.NativeTvdbPosterOrdering", Good("Host") && (NativePosterOrdering.IsTvdbMovieEnabled || NativePosterOrdering.IsTvdbSeriesEnabled),
            "Existing native typed originalLanguage and native server-language conversion; Movie=" + NativePosterOrdering.IsTvdbMovieEnabled + "; Series=" + NativePosterOrdering.IsTvdbSeriesEnabled + ". TVDB is a query-local fallback after valid TMDB language; no extra metadata request or image write. Admitted candidates only.");
        Set("Activation.NotificationEpisodes", Good("Host") && NotificationEnhancement.IsEnabled, NotificationEnhancement.Detail);
        Set("Activation.MetadataEditNotification", Good("Host") && MetadataEditNotification.IsEnabled, MetadataEditNotification.Detail);
        Set("Activation.ImageEditNotification", Good("Host") && ImageEditNotification.IsEnabled, ImageEditNotification.Detail);
        Set("Activation.MissingEpisodeGroup", Good("Host") && Provider.MissingEpisodeGroupControl.IsEnabled,
            Provider.MissingEpisodeGroupControl.Detail + " Replaced=" + Provider.MissingEpisodeGroupControl.ReplacedResults);
        Set("Activation.DoubanChannel", Good("Host") && Provider.DoubanChannelService.Current?.Enabled == true && Provider.NativeChannelItemRefresh.IsEnabled,
            "Channel '" + Provider.DoubanChannel.ChannelName + "' service=" + (Provider.DoubanChannelService.Current != null) + "; enabled=" + (Provider.DoubanChannelService.Current?.Enabled == true) +
            "; cachedLists=" + (Provider.DoubanChannelService.Current?.CachedCollections ?? 0) + "; refresh: " + Provider.NativeChannelItemRefresh.Detail + " Skipped=" + Provider.NativeChannelItemRefresh.Skipped +
            "; user views: hidden=" + (BoxsetUserViews.HiddenChannel ?? "none") + ".");
        Set("Activation.PerfectSubtitle",Good("Host") && SubtitleSearchOptimization.IsEnabled, SubtitleSearchOptimization.Detail);
        Set("Backend.FavoriteNotifications", services.Favorites != null, "ItemAdded favorite update worker; opt-in rebuild event, current user visibility and favorites. Pending=" + (services.Favorites?.PendingCount ?? 0) + "; completed=" + (services.Favorites?.CompletedItems ?? 0) + "; emitted=" + (services.Favorites?.EmittedRequests ?? 0) + "; rejected=" + (services.Favorites?.RejectedEvents ?? 0) + ". PUK/user budgets and native notification destination rules apply.");
        Set("Activation.ExclusiveRemoteFetcher", Good("Host") && ExclusiveRemoteFetcherControl.IsEnabled, ExclusiveRemoteFetcherControl.Detail);
        Set("Activation.ExtractAlternative", Good("Host") && services.MediaCatchup?.IsAlternativeExtractActive == true,
            "ExtractAlternative with optional NoNfoSaverOptimization and/or BlockRemoteFetcher, each selected once: actual stop source membership, exact local STRM Movie group, visible no-audio alternatives, bounded 64-member selection and shared item/quota queue; awaited native full refresh with original options and probe-only ownership. CatchupMode independent. Media-side save options and metadata savers must be disabled in the target library. Combinations containing BlockRemoteFetcher additionally require healthy remote eligibility hooks, the same native provider manager, and a nonempty non-null image-provider registration array; filtering keeps the scoped remote-only limits. Refresh completion is not proof that an audio stream was produced. Other feature combinations, media types, protocols and original item-op8/queue/opaque branches remain incomplete.");
        Set("Activation.EpisodeTotal", Good("Host") && UserExperienceGetterCompatibility.IsEpisodeTotalEnabled,
            "Shared DTO owner: visible physical-root constrained Series/Season recursive total; successful response count only, persistent and response Played flags unchanged. Original Folder IL and other types remain incomplete.");
        Set("Activation.LibraryItemCount", Good("Host") && UserExperienceGetterCompatibility.IsLibraryCountEnabled,
            "Shared DTO owner: visible movies/TV/home-video+photo/music-video/book totals and music/audiobook album totals plus native collection/playlist view counts, native home-view compatible without ChildCount/SubViews; count query retains native user filters, other collection types remain unchanged.");
        Set("Activation.MissingEpisodeName", Good("Host") && UserExperienceGetterCompatibility.IsMissingMetadataEnabled,
            "Shared native DTO factory projection for Episode Name equal to the authorized item's basename; invariant Episode N label is a reconstruction. Batch AdditionalPart Video labels are independently projected using explicit English conventions; original hidden type/label literals, parser and query expansion remain incomplete.");

        var pinyin = false; string pinyinDetail;
        try { pinyin = PinyinUtility.GenerateSortName("中文") == "ZW"; pinyinDetail = pinyin ? "Dictionary self-check passed." : "Pinyin dictionary self-check differs."; }
        catch (Exception ex) { pinyinDetail = "Pinyin initialization failed: " + ex.GetType().Name + ": " + ex.Message; }
        Set("Backend.Pinyin", pinyin, pinyinDetail);
        Set("Metadata.IsModSupported", Good("Host") && Good("ProviderManager") && services.Metadata != null && services.Douban != null && pinyin,
            "Controlled fields: PinyinSortName, EnableDoubanAssistScraping. Pipeline/Douban=" + Present(services.Metadata, services.Douban) + "; " + pinyinDetail +
            "; " + entries["ProviderManager"].Detail + "; " + entries["Host"].Detail + " Live provider responses/credentials are per-operation, not capability gates.");
        Set("Media.IsModSupported", false,
            "Incomplete controlled group: ExclusiveControlFeatures has seven unimplemented non-default branches; NoPersistIntegration opts out of the implemented native-success cache integration (restore and missing-cache export) while preserving explicit media APIs and native media/date writes; combinations with ExtractAlternative remain outside this opt-out subset; ExtractAlternative alone or combined once with NoNfoSaverOptimization and/or BlockRemoteFetcher supports the bounded stop-triggered local STRM Movie subset under the corresponding consumer-health and native-registration checks, not other combinations or full original scheduling; BlockRemoteFetcher covers Series/Season/MusicAlbum/BoxSet and bounded remote-metadata-only file Movie/Episode/Audio: ordinary automatic refreshes for both existing and first-refresh-marked persisted items after matching image eligibility capture, remote-only providers during FullRefresh metadata plus ValidationOnly images with that same witnessed scope, or public enumeration with genuinely no current scope and healthy shared boundaries. Missing typed capture inside a refresh, manual/owned/expired scopes, dual pre-refresh providers in the added full mode and other special refreshes remain native; media remote-image, file-change invalidation/restoration and broader ownership/CatchAll behavior remain incomplete. Typed-refresh NoNfo saver control is independent; HDR/DV/3D and full ISO/disc behavior absent. CustomEncoderPath now handles ordinary SDR capture/BIF/chapter frames without changing host encoder settings; remaining image-position/postprocess/default exclusive branches are independent. " + entries["Backend.Media"].Detail);
        Set("MetadataBuild.IsModSupported", false,
            "MetadataCacheMode/RootFolder cover Douban and pinned native Movie/Series/Season/Episode TMDB typed-detail JSON subsets; M150 adds TheMovieDb Movie/Series/Season/BoxSet image restore/write with optional types (reconstructed layout); original disk layout and limited retention/quota remain incomplete; external-root semantics restricted. Douban=" + Present(services.Douban));
        Set("Intro.IsModSupported", false,
            "Native host-wide STRM eligibility and the full marker-scope contract remain incomplete; explicit and ordinary plugin tasks support the bounded local-STRM subset. ParallelMarkerComputation now controls season concurrency for the ordinary incremental path without bypassing shared quotas. Header-authenticated remote inputs, live streams and discs remain outside this subset; protected query URLs and plain headerless HTTP(S) are checked separately. Verified playback marker changes send an independent native notification; Session context and 500ms OSD use exact live session matching; full clients and other origins remain incomplete. Physical native fingerprint support: " + fingerprint);
        Set("Experience.IsModSupported", false,
            "Controlled group incomplete: full deep-delete cascade and LibraryScopePlayProgress end-to-end coordination/presentation. The three observed original image-notification bindings are upload/URL/remote download; delete/reorder/automatic image notifications are unproven extensions, not established parity gaps. Selected 20 metadata tokens (SeriesOrder reads the fileorder provider value) emit an opt-in rebuild event after successful native editor POST; native manual upload/URL/remote-image download saves emit a separate opt-in event only after their Task succeeds; before optional metadata/image handoff, captured actor ID resolves through the current user manager and image admin status is rechecked. Original event IDs remain unverified; the ProviderIds exclusion key fileorder and independent SeriesOrder selector are recovered from pinned original CIL plus the captured string table. ItemAdded favorite notifications use bounded current-user/PUK witnesses independently of catch-up options. Existing grouped notifications support recipient-visible episode ranges; DeleteVersion supports bounded unshared local STRM target retirement with opt-in notification after actual target retirement and repository commit; recovery backups remain. SuppressPluginUpdates is independently armed only for the fingerprint-checked native scheduled callsite, with manual queries retained. SeasonPUK and child SeriesPUK merge consistency plus backend response-only version name/quality ordering are independently implemented; names select the largest valid video for display; basic names expose known HDR range and extended names add native Dolby profile and defined 3D metadata. Quality sorting retains its separate first-video rule; no HDR processing or disc decoding. " + entries["Backend.Experience"].Detail);
        Set("UIFunction.IsModSupported", false,
            "Controlled group incomplete: missing-metadata original AdditionalPart literals/parser/query fields, remaining mixed and virtual-view library counts, total-episode replacement and full client UI validation; response-only basename-equivalent Episode N naming is independently implemented. Natural digit-boundary and collection-date aggregate ordering are independent pinned subfeatures. NoBoxsetsAutoCreation suppresses the verified automatic post-scan path and filters collection-library inputs to native user views. ReverseLogLines uses bounded global page mapping in the native Web log view, not original tail streaming; client and same-count rotation limitations remain. Existing People projection and preferences are partial branches; Dashboard=" + Present(services.Dashboard) + "; " + entries["Platform.People"].Detail);
        Set("Mod.IsModSupported", false,
            "EnhanceChineseSearch/Mode/SearchScope/SearchTuningPreferences incomplete: default mixed named/LiveTV/projection coverage remains partial beyond ordinary-media/Person mixed composition; explicit Tag/Genre/Studio and Person searches use authorized media witnesses, including explicit supported multi-type mixtures with original presentation grouping retained and cross-family PUK collisions delegated to native; Full/Lite semantics; query-only TraditionalToSimplifiedChinese and MixedChineseVariants controls are independent with raw/phonetic schema3 index; original region overrides and exact FTS behavior remain incomplete. DigitsAsTmdbId supports strict positive ASCII IDs for in-scope Movie/Series/Episode; recent WasSearched suppression and native list-search media fields are implemented subsets. " + entries["Backend.Search"].Detail);
        Set("Network.IsModSupported", false,
            "Local discovery UDP/authenticated address projection is implemented on the pinned host. Native proxy/decompression changes only future factory clients; existing cached/explicit contexts retain prior settings and require a controlled drain/restart for uniform application. Exact original whitelist and full host-wide lifecycle parity remain incomplete. " + entries["Backend.Network"].Detail);

        Set("Metadata.IsMovieDbPluginSupported", false,
            "Source AND: MovieDb adapter && Metadata.IsModSupported; adapter business group incomplete: full episode-group sidecar/extended info; first-language season cast DTO, ExtendedInfo/nonblank-group empty-cast branch and bounded Series-first aggregation are independent; pinned native alternate API/image routing and blank-native-key retention are independently implemented. " + entries["Dependency.MovieDb"].Detail);
        Set("Metadata.IsTvdbPluginSupported", Good("Metadata.IsModSupported") && Good("Dependency.Tvdb") && services.Languages != null,
            "Source AND: TVDB typed providers && Metadata.IsModSupported && MetadataLanguageService=" + Present(services.Languages) + "; " + entries["Dependency.Tvdb"].Detail + "; base=" + Good("Metadata.IsModSupported"));
        Set("Metadata.IsNfoMetadataPluginSupported", Good("Metadata.IsModSupported") && Good("Dependency.NfoMetadata") && services.Nfo != null,
            "Source AND: NFO typed providers && Metadata.IsModSupported && NfoActorService=" + Present(services.Nfo) + "; actor thumbnail URL association implemented, not image download; " + entries["Dependency.NfoMetadata"].Detail + "; base=" + Good("Metadata.IsModSupported"));
        Set("Metadata.IsOriginalPosterSupported", false,
            "Source (MovieDb OR TVDB) && Metadata.IsModSupported: dependency OR=" + (Good("Dependency.MovieDb") || Good("Dependency.Tvdb")) +
            "; business contract incomplete: admitted Movie/Series candidates use native TMDB language, validated Series-cache metadata or TVDB typed originalLanguage fallback. Enabled queries include all languages and matching queries in the pinned non-nested synchronous refresh scope can expand the provider filter; native type/library admission remains. Stable admitted results remove Douban-labelled candidates and order the remaining image types without changing retained fields; ordinary missing origin/title still uses preferred-language and raw-empty priorities; Backdrop raw null/empty languages share preferred-exact priority. Movie/Series title fallback is a separate final heuristic after valid provider origins. Exact Season/Episode with a positive unchanged SeriesId can use the associated Series title after the query; zero SeriesId uses at most 64 native parent edges plus one ID re-resolution with stable late path identities. The existing preferred-language/native pipeline is not globally bounded. Episode raw null/empty eligible languages share preferred-exact priority. Selected Chinese origins use a valid late library metadata locale as the local sorting origin; image preference remains the entry snapshot. BoxSet title fallback joins the first native CollectionIds query result (Limit 10) with pipe separators; no collection-owned title or metadata is rewritten. TMDB Movie/Series can preserve labelled Backdrop languages through bounded query-operation association without mutating source DTOs. Over-budget/cyclic or unstable parent relations remain fail-closed; original unbounded ancestry, deep-nesting provider-scope parity, other Backdrop callers/legacy shared-state behavior, broader guard-abort/item-scope policies and malformed-tag/lazy preference semantics remain incomplete. Existing missing-image fallback separately retains its conservative kana/Hangul hint; no sorting operation writes images.");
        Set("UIFunction.IsMovieDbPluginSupported", false,
            "Source AND: MovieDb adapter && UIFunction.IsModSupported; EnhanceMissingEpisodes has the episode-group replacement of the native MovieDb list (M146) but not the original own TMDB series/season fetch; " + entries["Dependency.MovieDb"].Detail);
        Set("Experience.IsOpenSubtitlesPluginSupported", false,
            "Source AND: OpenSubtitles adapter && Experience.IsModSupported; " + entries["Dependency.OpenSubtitles"].Detail);
        // M148: original meaning (decompiled IsModSuccess): every patch is in its desired state. Feature completeness is
        // reported by the *.IsModSupported groups above, not by this banner flag.
        var failedPatches = PatchHealth.Failed;
        Set("Root.IsModSuccess", Good("Host") && Conflicts.Count == 0 && failedPatches.Count == 0,
            "Host=" + Good("Host") + "; loaded conflicts=" + string.Join(",", Conflicts) + "; patches recorded=" + PatchHealth.Recorded +
            "; not in desired state=" + (failedPatches.Count == 0 ? "none" : string.Join(",", failedPatches)) +
            ". Complete-group feature parity is reported separately by the *.IsModSupported entries.");
        DefineFieldGates();
    }

    /// <summary>
    /// M143/F8: field-level editability for controls whose own consumer is assembled and host-verified while the
    /// complete group stays false. Group flags, IsModSuccess and parity claims are unchanged. Fields without a
    /// complete consumer (LibraryScopePlayProgress, PreferOriginalPoster) stay gated.
    /// </summary>
    private void DefineFieldGates()
    {
        var host = Good("Host");
        bool Fields(string key, bool ok, string detail, params string[] fields)
        {
            Set("Field." + key, ok, (ok ? "Editable: " : "Gated: ") + string.Join(", ", fields) + ". " + detail);
            if (ok) fieldGates[key] = fields;
            return ok;
        }
        var media = entries["Backend.Media"].Supported;
        Fields("Media.ImageCapture", media && entries["Platform.Thumbnail"].Supported, "Assembled ImageCapture/VideoThumbnail with STRM thumbnail platform probe.",
            "EnableImageCapture", "ImageCapturePosition", "ImageCapturePostProcessMode");
        Fields("Media.Exclusive", media && entries["Platform.Exclusive"].Supported,
            "Default exclusive gate and implemented feature selections; unimplemented ExclusiveControlFeatures items are disabled in the list unless already selected.",
            "ExclusiveExtract", "ExclusiveControlFeatures");
        Fields("Media.CustomTools", media, "Custom ffprobe and ordinary SDR custom encoder consumers; HDR/DV/3D/disc conversion still excluded.", "CustomProbePath", "CustomEncoderPath");
        Fields("MetadataBuild.Cache", host && services.Douban != null, "Douban and pinned TMDB typed-detail JSON caches; TheMovieDb image cache with optional types (M150, reconstructed layout); original disk layout incomplete.",
            "MetadataCacheMode", "MetadataCacheRootFolder");
        Fields("Intro.Unlock", entries["Platform.Fingerprint"].Supported, "Bounded local-STRM/plugin task subset and season concurrency; host-wide eligibility incomplete.",
            "UnlockIntroSkip", "ParallelMarkerComputation", "MarkerEnabledLibraryScope");
        Fields("Experience.Notifications", host && services.DeepDeleteNotifications != null, "Episode-range, metadata/image edit and deep-delete notification producers.", "EnhanceNotificationSystem");
        Fields("Experience.DeepDelete", host && services.DeepDeleteNotifications != null, "DeleteVersion local STRM target retirement only; broad delete cascade not implemented.", "EnableDeepDelete");
        Fields("Experience.PreviousEpisodes", services.Experience != null, "Previous-episode played/progress marking.", "MarkPreviousEpisodesPlayed", "MarkPreviousEpisodesPlayedThreshold");
        Fields("Experience.SuppressUpdates", host, "Fingerprint-checked native scheduled update callsite only.", "SuppressPluginUpdates");
        Fields("Experience.MultiVersion", services.Versions != null && entries["Platform.MultiVersion"].Supported, "Series merge scope, folder grouping limit and response-only version projection.",
            "MergeSeriesPreference", "FolderVideoGroupingLimit", "BeautifyMultiVersion", "MultiVersionDisplayPreference", "MultiVersionReorderPreference");
        Fields("Experience.Subtitle", host && entries["Dependency.OpenSubtitles"].Detail.Contains("pinned artifact SHA256 matched", StringComparison.Ordinal),
            "Pinned OpenSubtitles 1.0.71 artifact; local STRM hash/original-name search adapter. Online search/download unverified.", "OptimizeSubtitle");
        var people = entries["Platform.People"].Supported;
        Fields("UIFunction.People", people, "Hidden-person DTO projection.", "HidePersonNoImage", "HidePersonPreference");
        Fields("UIFunction.Ordering", host, "Natural digit-boundary and collection-date ordering subfeatures.", "OptimizeMediaItemOrder", "OptimizeItemOrderPreferences");
        Fields("UIFunction.Preferences", services.Preferences != null, "First-admin library order/display preference copy.", "EnforceUserPreferences", "EnforceUserPreferencesScope");
        Fields("UIFunction.Projections", people, "Missing-name, total-episode and library-count response projections.", "BeautifyMissingMetadata", "ReplaceUnplayedWithTotal", "ShowLibraryItemCount");
        Fields("UIFunction.Collections", host, "Post-scan boxset suppression and season collection query.", "NoBoxsetsAutoCreation", "ShowCollectionsFromSeasons");
        Fields("UIFunction.MissingEpisodes", host && Good("Dependency.MovieDb"),
            "Pinned MovieDb 2.0 missing/upcoming list: episode-group numbering replacement when MovieDbEpisodeGroup is on; the original own TMDB series fetch is not reproduced.",
            "EnhanceMissingEpisodes");
        Fields("UIFunction.Logs", host && services.Dashboard != null, "Bounded global page mapping in native log view.", "ReverseLogLines");
        Fields("Mod.Search", entries["Backend.Search"].Supported, "Typed Chinese search index subsets; default mixed named/LiveTV coverage partial.",
            "EnhanceChineseSearch", "EnhanceChineseSearchMode", "SearchScope", "SearchTuningPreferences");
        Fields("Network.Proxy", services.Network != null, "Local discovery and future native handler proxy; cached clients keep prior settings.",
            "LocalDiscoveryUrl", "EnableProxyServer", "ProxyServerUrl", "ProxyMode");
        Fields("Metadata.MovieDb", Good("Metadata.IsModSupported") && Good("Dependency.MovieDb") && services.Languages != null,
            "Verified TheMovieDb 2.0 adapter: language fallback, episode groups, person enhancement and alternate endpoints.",
            "ChineseMovieDb", "FallbackLanguages", "MovieDbEpisodeGroup", "LocalEpisodeGroup", "EnhanceMovieDbPerson",
            "AltMovieDbConfig", "AltMovieDbApiUrl", "AltMovieDbImageUrl", "AltMovieDbApiKey");
        Set("Field.Gated", true, "Still gated by complete-group flags: LibraryScopePlayProgress (global option inactive), PreferOriginalPoster (poster group incomplete).");
    }

    private bool Good(string key) => entries[key].Supported;

    /// <summary>Shown only when IsModSuccess is false (generated VisibleCondition), like the original banner.</summary>
    private StatusItem BannerStatus(bool success)
    {
        if (success) return new StatusItem { Status = ItemStatus.None };
        var reasons = new List<string>();
        if (!Good("Host")) reasons.Add(UiLanguage.Text("Emby version differs from the validated 4.10.2.0", "Emby 版本与已验证的 4.10.2.0 不同", "Emby 版本與已驗證的 4.10.2.0 不同"));
        if (Conflicts.Count != 0) reasons.Add(UiLanguage.Text("conflicting plugins loaded: ", "检测到冲突插件：", "偵測到衝突外掛：") + string.Join(", ", Conflicts));
        var failed = PatchHealth.Failed;
        if (failed.Count != 0) reasons.Add(UiLanguage.Text("not in effect: ", "未能生效：", "未能生效：") + string.Join(", ", failed));
        return new StatusItem
        {
            Caption = UiLanguage.Text("Some enhancements are not in effect", "部分增强功能未能生效", "部分增強功能未能生效"),
            StatusText = string.Join(UiLanguage.Text("; ", "；", "；"), reasons) + UiLanguage.Text(". See Runtime diagnostics on the About page.", "。详见关于页的运行时能力详情。", "。詳見關於頁的執行期能力詳情。"),
            Status = ItemStatus.Warning
        };
    }
    private void Set(string key, bool supported, string detail) => entries[key] = new(supported, detail);
    private static string Present(params object?[] values) => string.Join("/", values.Select(v => v == null ? "missing" : "present"));

    private void Dependency(string assemblyName, string pluginName, string version, string mvid, string hash, string providerName,
        IPlugin[] plugins, MetadataPluginSummary[] inventory, bool local, params Type[] itemTypes)
    {
        var key = "Dependency." + assemblyName;
        try
        {
            var found = plugins.Where(p => p.GetType().Assembly.GetName().Name == assemblyName && p.GetType().FullName == assemblyName + ".Plugin" && p.Name == pluginName).ToArray();
            if (found.Length != 1) { Set(key, false, "Exact loaded " + assemblyName + ".Plugin / " + pluginName + " count=" + found.Length); return; }
            var assembly = found[0].GetType().Assembly;
            var identity = assembly.GetName();
            var stamp = identity.Name + " " + identity.Version + " MVID=" + assembly.ManifestModule.ModuleVersionId;
            if (identity.Version != Version.Parse(version) || assembly.ManifestModule.ModuleVersionId != Guid.Parse(mvid))
            { Set(key, false, "Version/module differs from recorded public contract: " + stamp); return; }
            if (!VerifyBackingArtifact(found[0], assembly, hash, out var artifactDetail))
            { Set(key, false, artifactDetail + ": " + stamp); return; }
            // Host exports include internal concrete types (TVDB Episode is internal). Binding remains
            // through public typed interfaces; inspect metadata only, never invoke a private member.
            var exports = assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters).ToArray();
            var missing = new List<string>();
            if (assemblyName == "OpenSubtitles")
            {
                var contract = typeof(MediaBrowser.Controller.Subtitles.ISubtitleProvider);
                var subtitleExports = exports.Where(contract.IsAssignableFrom).ToArray();
                if (subtitleExports.Length == 0 || subtitleExports.Any(t => t.GetInterfaceMap(contract).TargetMethods.Any(m =>
                    !m.IsPublic || m.IsStatic || m.GetMethodBody()?.GetILAsByteArray()?.Length is not > 0)))
                    missing.Add("public ISubtitleProvider export/member/IL");
                missing.Add("SubtitleManager registration unobserved; explicit/library-perfect local STRM hash and original filename adapter implemented; remote target/download/full-client parity incomplete");
            }
            foreach (var item in itemTypes)
            {
                var contract = local ? typeof(ILocalMetadataProvider<>).MakeGenericType(item) : typeof(IRemoteMetadataProvider<,>).MakeGenericType(item, Lookup(item));
                var export = exports.FirstOrDefault(contract.IsAssignableFrom);
                // Public concrete implementation IL must be available, but it is not executed here.
                var methods = export == null ? Array.Empty<MethodInfo>() : export.GetInterfaceMap(contract).TargetMethods;
                if (export == null || methods.Length == 0 || methods.Any(m => !m.IsPublic || m.IsStatic || m.GetMethodBody()?.GetILAsByteArray()?.Length is not > 0))
                    missing.Add(item.Name + " public typed export/member/IL");
                // GetAllMetadataPlugins has a fixed 14-type list on 4.10.2 and omits Person by design.
                // Person typed exports are verified above; actual enablement is checked per item at operation time.
                if (item != typeof(Person) && !inventory.Any(row => row.ItemType == item.Name && row.Plugins?.Any(p => p.Name == providerName &&
                    p.Type == (local ? MetadataPluginType.LocalMetadataProvider : MetadataPluginType.MetadataFetcher)) == true))
                    missing.Add(item.Name + " registered " + providerName);
            }
            Set(key, Good("Host") && Good("ProviderManager") && missing.Count == 0,
                stamp + "; " + artifactDetail + "; public typed exports/members/IL checked; " + (missing.Count == 0 ? "registered contracts complete" : "missing: " + string.Join(", ", missing)) +
                "; host=" + Good("Host") + "; inventory=" + Good("ProviderManager") + (itemTypes.Contains(typeof(Person)) ? "; Person is omitted from the host catalog by design; loaded typed export checked, per-item enablement checked during operations." : "") + " No provider fetch/constructor invoked.");
        }
        catch (Exception ex) { Set(key, false, "Dependency inspection failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static bool VerifyBackingArtifact(IPlugin plugin, Assembly assembly, string expectedHash, out string detail)
    {
        // Emby loads plugin bytes, then records the source path through public IPluginAssembly.SetAttributes.
        // Use only that already-loaded object's public path when CLR Location is empty; never search directories.
        var path = assembly.Location;
        var source = "Assembly.Location";
        if (string.IsNullOrEmpty(path)) { path = plugin.AssemblyFilePath; source = "IPlugin.AssemblyFilePath"; }
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        { detail = "Loaded module has no explicit absolute backing artifact path (" + source + ")"; return false; }
        using var file = File.OpenRead(path);
        using (var pe = new PEReader(file, PEStreamOptions.LeaveOpen))
        {
            if (!pe.HasMetadata) { detail = "Plugin backing artifact has no CLI metadata"; return false; }
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) { detail = "Plugin backing artifact is not an assembly"; return false; }
            var definition = metadata.GetAssemblyDefinition();
            var loaded = assembly.GetName();
            if (metadata.GetString(definition.Name) != loaded.Name || definition.Version != loaded.Version ||
                metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != assembly.ManifestModule.ModuleVersionId)
            { detail = "Plugin backing artifact PE name/version/MVID differs from loaded module"; return false; }
        }
        file.Position = 0;
        using var sha = SHA256.Create();
        if (Convert.ToHexString(sha.ComputeHash(file)) != expectedHash)
        { detail = "Loaded plugin backing artifact digest differs"; return false; }
        detail = source + " PE name/version/MVID bound to loaded module; pinned artifact SHA256 matched";
        return true;
    }

    private static Type Lookup(Type item) => item == typeof(Movie) ? typeof(MovieInfo) : item == typeof(Series) ? typeof(SeriesInfo) :
        item == typeof(Season) ? typeof(SeasonInfo) : item == typeof(Episode) ? typeof(EpisodeInfo) : typeof(PersonLookupInfo);

    /// <summary>Overwrite every host-owned bit for this page, including nested groups. Call after store.Copy/Prepare decoration.</summary>
    private static readonly (string Prefix, Type Model)[] FieldOwners =
    {
        ("Media.", typeof(MediaInfoExtractOptions)), ("MetadataBuild.", typeof(MetadataBuildOptions)), ("Intro.", typeof(IntroSkipOptions)),
        ("Experience.", typeof(ExperienceEnhanceOptions)), ("UIFunction.", typeof(UIFunctionOptions)), ("Mod.", typeof(ModOptions)),
        ("Network.", typeof(NetworkOptions)), ("Metadata.", typeof(MetadataEnhanceOptions))
    };
    // Values ValidateFeatures accepts in at least one supported selection; all others are explicitly not implemented.
    private static readonly HashSet<string> ImplementedExclusiveFeatures = new(StringComparer.OrdinalIgnoreCase)
        { "NoPersistIntegration", "NoNfoSaverOptimization", "ExtractAlternative", "BlockRemoteFetcher" };

    /// <summary>Editable field names for one option model type under the current host evidence.</summary>
    public IReadOnlyCollection<string> EditableFields(Type model)
    {
        var prefix = FieldOwners.FirstOrDefault(owner => owner.Model == model).Prefix;
        return prefix == null ? Array.Empty<string>() :
            fieldGates.Where(gate => gate.Key.StartsWith(prefix, StringComparison.Ordinal)).SelectMany(gate => gate.Value).ToArray();
    }

    private void ApplyFieldGates(EditableOptionsBase model)
    {
        var fields = EditableFields(model.GetType());
        Options.UIBaseClasses.FieldCapabilityGates.Set(model, fields);
        if (model is MediaInfoExtractOptions media && fields.Contains("ExclusiveControlFeatures"))
        {
            var selected = (media.ExclusiveControlFeatures ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // Unimplemented items remain deselectable when already persisted; they are never offered as new choices.
            foreach (var option in media.ExclusiveControlList)
                option.IsEnabled = ImplementedExclusiveFeatures.Contains(option.Value) || selected.Contains(option.Value, StringComparer.OrdinalIgnoreCase);
        }
    }

    public string Apply(EditableOptionsBase model)
    {
        ApplyFieldGates(model);
        switch (model)
        {
            case PluginOptions root:
                root.IsModSuccess = Good("Root.IsModSuccess");
                root.ModStatus = BannerStatus(root.IsModSuccess);
                root.ShowConflictPluginLoadedStatus = Conflicts.Count != 0;
                root.ConflictPluginLoadedStatus = new StatusItem { Caption = Conflicts.Count == 0 ? "" : "Loaded plugin conflicts", StatusText = string.Join(", ", Conflicts), Status = Conflicts.Count == 0 ? ItemStatus.None : ItemStatus.Warning };
                root.LicenseWarningStatus = new StatusItem(); // ShowLicenseWarning is intentionally an immutable false getter.
                Apply(root.ModOptions); Apply(root.NetworkOptions); break;
            case MediaInfoExtractOptions media: media.IsModSupported = Good("Media.IsModSupported"); break;
            case MetadataEnhanceOptions metadata:
                metadata.IsModSupported = Good("Metadata.IsModSupported");
                metadata.IsMovieDbPluginSupported = Good("Metadata.IsMovieDbPluginSupported");
                metadata.IsTvdbPluginSupported = Good("Metadata.IsTvdbPluginSupported");
                metadata.IsOriginalPosterSupported = Good("Metadata.IsOriginalPosterSupported");
                metadata.IsNfoMetadataPluginSupported = Good("Metadata.IsNfoMetadataPluginSupported");
                Apply(metadata.MetadataBuildOptions); break;
            case MetadataBuildOptions build: build.IsModSupported = Good("MetadataBuild.IsModSupported"); break;
            case IntroSkipOptions intro: intro.IsModSupported = Good("Intro.IsModSupported"); break;
            case ExperienceEnhanceOptions experience:
                experience.IsModSupported = Good("Experience.IsModSupported");
                experience.IsOpenSubtitlesPluginSupported = Good("Experience.IsOpenSubtitlesPluginSupported");
                Apply(experience.UIFunctionOptions); break;
            case UIFunctionOptions ui: ui.IsModSupported = Good("UIFunction.IsModSupported"); ui.IsMovieDbPluginSupported = Good("UIFunction.IsMovieDbPluginSupported"); break;
            case ModOptions mod: mod.IsModSupported = Good("Mod.IsModSupported"); break;
            case NetworkOptions network: network.IsModSupported = Good("Network.IsModSupported"); break;
        }
        return Summary;
    }
}
