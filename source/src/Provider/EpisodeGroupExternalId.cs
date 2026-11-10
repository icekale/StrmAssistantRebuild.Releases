using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace StrmAssistantRebuild.Provider;

/// <summary>Public Emby discovery point for the reconstruction's group ID / external JSON URL.</summary>
public sealed class EpisodeGroupExternalId : IExternalId
{
    public string Name => "TMDB Episode Group";
    public string Key => EpisodeGroupService.ProviderKey;
    public string UrlFormatString => null!; // ID may also be an external JSON URL; do not invent a universal website route.
    public bool Supports(IHasProviderIds item) => item is Series;
}
