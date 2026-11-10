using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace StrmAssistantRebuild.Web.Api;

public class MediaInfoBundle
{
    public MediaSourceInfo MediaSourceInfo { get; set; } = null!;
    public List<ChapterInfo> Chapters { get; set; } = new();
    public bool? ZeroFingerprintConfidence { get; set; }
    public string EmbeddedImage { get; set; } = null!;
    public EmbeddedInfo EmbeddedInfo { get; set; } = null!;
}

public class EmbeddedInfo
{
    public string Name { get; set; } = null!;
    public string Overview { get; set; } = null!;
    public int? Track { get; set; }
    public int? Disc { get; set; }
    public string Album { get; set; } = null!;
    public string[] AlbumArtists { get; set; } = null!;
    public string[] Artists { get; set; } = null!;
    public string[] Composers { get; set; } = null!;
    public string[] Genres { get; set; } = null!;
}
