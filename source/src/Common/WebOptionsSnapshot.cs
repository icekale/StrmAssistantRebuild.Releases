using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

/// <summary>Deliberate public projection. Never serialize tokens, passwords or server filesystem paths into JS.</summary>
public static class WebOptionsSnapshot
{
    public static Dictionary<string, object> Create(MediaInfoExtractOptions media, MetadataEnhanceOptions metadata,
        IntroSkipOptions intro, ExperienceEnhanceOptions experience, ModOptions? search = null, bool doubanFilterSupported = false,
        bool replaceIntroMarkersSupported = false)
    {
        var ui = experience.UIFunctionOptions;
        return new Dictionary<string, object>
        {
            ["BeautifyMultiVersion"] = experience.BeautifyMultiVersion,
            ["MultiVersionDisplayPreference"] = experience.MultiVersionDisplayPreference.ToString(),
            ["MultiVersionReorderPreference"] = experience.MultiVersionReorderPreference.ToString(),
            ["EnableIntroSkip"] = intro.EnableIntroSkip,
            ["EnableDoubanChannel"] = experience.EnableDoubanChannel,
            ["EnableDoubanAssistScraping"] = metadata.EnableDoubanAssistScraping,
            ["DoubanFilterSupported"] = metadata.EnableDoubanAssistScraping && doubanFilterSupported,
            ["ReplaceIntroMarkersSupported"] = intro.UnlockIntroSkip && replaceIntroMarkersSupported,
            ["ShowCollectionsFromSeasons"] = ui.ShowCollectionsFromSeasons,
            ["ShowLibraryItemCount"] = ui.ShowLibraryItemCount,
            ["BeautifyMissingMetadata"] = ui.BeautifyMissingMetadata,
            ["EnhanceMissingEpisodes"] = ui.EnhanceMissingEpisodes,
            ["ReplaceUnplayedWithTotal"] = ui.ReplaceUnplayedWithTotal,
            ["OptimizeMediaItemOrder"] = ui.OptimizeMediaItemOrder,
            ["ReverseLogLines"] = ui.ReverseLogLines,
            ["OptimizeItemOrderPreferences"] = ui.OptimizeItemOrderPreferences,
            ["MetadataRefreshPreferences"] = metadata.MetadataRefreshOptions.MetadataRefreshPreferences,
            ["ExclusiveExtract"] = media.ExclusiveExtract,
            ["UnlockIntroSkip"] = intro.UnlockIntroSkip,
            ["EnableAutomaticIntroScraping"] = intro.IntroSkipScrapeOptions.EnableAutomaticIntroScraping,
            ["MetadataCacheMode"] = metadata.MetadataBuildOptions.MetadataCacheMode,
            ["SearchShowMediaInfo"] = search?.EnhanceChineseSearch == true && ScopeRules.Parse(search.SearchTuningPreferences).Contains(nameof(ModOptions.SearchTuningOption.ShowMediaInfo))
        };
    }
    public static Dictionary<string, bool> Enabled(MediaInfoExtractOptions media, MetadataEnhanceOptions metadata,
        IntroSkipOptions intro, ExperienceEnhanceOptions experience, bool doubanFilterSupported = false)
    {
        var ui = experience.UIFunctionOptions;
        // No invented persisted options: the provided inventory has no ShortcutMenu property.
        return new Dictionary<string, bool>
        {
            ["components/strmassistant/strmassistant.js"] = true,
            ["modules/shortcuts.js"] = true,
            ["modules/mediainfo/mediainfo.js"] = metadata.EnableDoubanAssistScraping &&
                DoubanScope.Parse(metadata.DoubanAssistScrapeScope).Contains("Rating"),
            ["modules/refreshdialog/refreshdialog.js"] = true,
            ["modules/tabbedview/listcontroller.js"] = true,
            ["modules/indicators/indicators.js"] = ui.ShowLibraryItemCount || ui.ReplaceUnplayedWithTotal,
            ["modules/filtermenu/filtermenu.template.html"] = metadata.EnableDoubanAssistScraping && doubanFilterSupported,
            ["item/item.js"] = experience.BeautifyMultiVersion || intro.EnableIntroSkip || experience.EnableDoubanChannel || ui.ShowCollectionsFromSeasons,
            ["item/linkeditems.js"] = ui.ShowCollectionsFromSeasons,
            ["list/list.js"] = ui.ShowLibraryItemCount || ui.BeautifyMissingMetadata || ui.EnhanceMissingEpisodes,
            ["components/libraryoptionseditor/libraryoptionseditor.js"] = true
        };
    }
}
