using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace StrmAssistantRebuild.Provider;

/// <summary>
/// M170: the original MovieDbEpisodeGroupExternalId (notes/decrypted/StrmAssistant.Provider/MovieDbEpisodeGroupExternalId.cs):
/// key TmdbEg, a TMDB website link built from the series TMDB id, or the reference itself when it is an external JSON URL.
/// The original keeps the Supports() item in instance fields; here they are per thread so concurrent pages cannot mix series.
/// </summary>
public sealed class EpisodeGroupExternalId : IExternalId, IHasWebsite
{
    [ThreadStatic] static string? seriesTmdbId;
    [ThreadStatic] static string? reference;
    public string Name => "MovieDb Episode Group";
    public string Key => EpisodeGroupService.ProviderKey;
    public string Website => "https://www.themoviedb.org";
    public string UrlFormatString => MetadataLanguageService.IsHttpUrl(reference) ? reference! :
        string.IsNullOrWhiteSpace(seriesTmdbId) ? null! : "https://www.themoviedb.org/tv/" + seriesTmdbId + "/episode_group/{0}";
    public bool Supports(IHasProviderIds item)
    {
        seriesTmdbId = MetadataLanguageService.PositiveId(item.GetProviderId(MetadataProviders.Tmdb));
        reference = item.GetProviderId(EpisodeGroupService.ProviderKey);
        return item is Series;
    }
}

/// <summary>Pre-M170 rebuild key, kept visible so existing values can be seen and moved to TmdbEg.</summary>
public sealed class LegacyEpisodeGroupExternalId : IExternalId
{
    public string Name => "TMDB Episode Group (legacy)";
    public string Key => EpisodeGroupService.LegacyProviderKey;
    public string UrlFormatString => null!;
    public bool Supports(IHasProviderIds item) => item is Series && !string.IsNullOrWhiteSpace(item.GetProviderId(EpisodeGroupService.LegacyProviderKey));
}
