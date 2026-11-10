using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using StrmAssistantRebuild.Options;
using StrmAssistantRebuild.Properties;

namespace StrmAssistantRebuild.Common;

/// <summary>Explicit per-model catalogs; a LibraryList suffix alone says nothing about its consumer.</summary>
public static class LibraryScopeCatalog
{
    public static void Apply(EditableOptionsBase model, ILibraryManager library, bool favoritesAvailable)
    {
        var folders = library.GetVirtualFolders();
        switch (model)
        {
            case MediaInfoExtractOptions media: Apply(media, folders, favoritesAvailable); break;
            case IntroSkipOptions intro: Apply(intro, folders, favoritesAvailable); break;
            case MetadataEnhanceOptions metadata: Apply(metadata.MetadataRefreshOptions, folders); break;
            case MetadataRefreshOptions refresh: Apply(refresh, folders); break;
        }
    }

    public static void Apply(MediaInfoExtractOptions model, IEnumerable<VirtualFolderInfo> folders, bool favoritesAvailable) =>
        model.LibraryList = Choices(folders, LibraryScopeKind.Media, favoritesAvailable);

    public static void Apply(IntroSkipOptions model, IEnumerable<VirtualFolderInfo> folders, bool favoritesAvailable)
    {
        // Reconstruction choice: behavior and Movie/Episode scraping share this scope, hence their union.
        // No Favorites here: playback behavior does not consume that pseudo-scope.
        model.LibraryList = Choices(folders, LibraryScopeKind.Intro, false);
        model.MarkerEnabledLibraryList = Choices(folders, LibraryScopeKind.Marker, favoritesAvailable);
    }

    public static void Apply(MetadataRefreshOptions model, IEnumerable<VirtualFolderInfo> folders) =>
        model.MetadataRefreshLibraryList = Choices(folders, LibraryScopeKind.Metadata, false);

    private static List<EditorSelectOption> Choices(IEnumerable<VirtualFolderInfo> folders, LibraryScopeKind kind, bool favorites)
    {
        var choices = folders.Where(f => ScopeSelection.EligibleFolder(f, kind)).Select(f => new EditorSelectOption
        { Value = f.ItemId, Name = f.Name, IsEnabled = true }).ToList();
        if (favorites && choices.Count > 0)
            choices.Insert(0, new EditorSelectOption { Value = ScopeSelection.Favorites, Name = Resources.Favorites, IsEnabled = true });
        return choices;
    }
}
