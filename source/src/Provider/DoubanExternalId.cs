using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace StrmAssistantRebuild.Provider;

public sealed class DoubanExternalId : IExternalId, IHasWebsite
{
    public static string StaticName => "Douban";
    public string Name => StaticName;
    public string Key => StaticName;
    public string UrlFormatString => "https://movie.douban.com/subject/{0}/";
    public string Website => "https://movie.douban.com";
    public bool Supports(IHasProviderIds item) => item is Movie or Series or Season or Trailer;
}
