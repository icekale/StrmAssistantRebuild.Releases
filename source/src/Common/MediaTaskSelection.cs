using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

public static class MediaTaskSelection
{
    public static BaseItem[] Select(ILibraryManager library, MediaInfoExtractOptions options, CancellationToken token, bool videoOnly = false)
    {
        var items = ScopeSelection.SelectForServer(library, options.LibraryScope, LibraryScopeKind.Media, token);
        return items.Where(item => IsEligible(item, options, videoOnly)).ToArray();
    }

    public static bool IsEligible(BaseItem item, MediaInfoExtractOptions options, bool videoOnly = false) =>
        (item is Video || !videoOnly && item is Audio) && !string.IsNullOrWhiteSpace(item.Path) &&
        !MediaExclusionRules.Excludes(item, options.MediaInfoExcludeMediaContainers, options.MediaInfoExcludeKeywords);

    public static bool Matches(ILibraryManager library, BaseItem item, MediaInfoExtractOptions options,
        CancellationToken token, ISet<long>? favoriteIds = null, bool videoOnly = false) =>
        IsEligible(item, options, videoOnly) && ScopeSelection.Matches(library, item, options.LibraryScope,
            LibraryScopeKind.Media, token, favoriteIds);
}
