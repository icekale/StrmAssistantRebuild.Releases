using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace StrmAssistantRebuild.Provider;

/// <summary>
/// M161: the original plugin's hand-written override files in its metadata cache (notes/decrypted/A----------/A----------.cs
/// readers :114053-114260, path builder :114448, applied by the ChineseMovieDb postfixes :50639/:52575/:54189/:54872):
///   override/tmdb-movies2/{tmdbId}/tags.json and external_ids.json (Movie)
///   override/tmdb-tv/{tmdbId}/tags.json and external_ids.json (Series; Season uses the series file's seasons[])
///   override/tmdb-tv/{seriesTmdbId}/episodeorder.json (season-0 specials: SortParentIndexNumber/SortIndexNumber)
/// Applied when ChineseMovieDb is on and the metadata cache mode is not None, like the original. Tags replace all tags when the
/// file has at least one non-blank tag; provider IDs overwrite (imdb_id, tvdb_id > 0, douban_id > 0, bgm_id when every
/// comma-separated part is a number). Rebuild difference: applied in the plugin's metadata enhancement after the native refresh
/// (the original patched the TheMovieDb provider result for the first language); fields locked by the user are not changed.
/// </summary>
public sealed class OriginalOverrideFiles
{
    private const int MaxBytes = 1024 * 1024;
    private readonly Func<string?> root;
    private long applied;
    public long Applied => Interlocked.Read(ref applied);
    public OriginalOverrideFiles(Func<string?> root) => this.root = root;

    public string? PathFor(string kind, string? tmdbId, string name)
    {
        var r = root();
        if (string.IsNullOrWhiteSpace(r) || string.IsNullOrWhiteSpace(tmdbId) || !tmdbId.All(c => c is >= '0' and <= '9') || tmdbId.Length > 10) return null;
        return Path.Combine(r, "override", kind == "movie" ? "tmdb-movies2" : "tmdb-tv", tmdbId, name + ".json");
    }

    private static JsonElement? Read(string? path)
    {
        try
        {
            if (path == null || !File.Exists(path) || new FileInfo(path).Length > MaxBytes) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (Exception) { return null; } // The original logs at Debug and continues.
    }

    public static string[]? Tags(JsonElement? file)
    {
        if (file is not { } f || !f.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) return null;
        var list = tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        return list.Length == 0 ? null : list;
    }

    /// <summary>Original helper A:51724: provider id values from an external_ids object.</summary>
    public static Dictionary<string, string> ProviderIds(JsonElement? ids)
    {
        var result = new Dictionary<string, string>();
        if (ids is not { ValueKind: JsonValueKind.Object } e) return result;
        if (e.TryGetProperty("imdb_id", out var imdb) && imdb.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(imdb.GetString())) result[MetadataProviders.Imdb.ToString()] = imdb.GetString()!;
        if (e.TryGetProperty("tvdb_id", out var tvdb) && tvdb.TryGetInt64(out var tv) && tv > 0) result[MetadataProviders.Tvdb.ToString()] = tv.ToString(CultureInfo.InvariantCulture);
        if (e.TryGetProperty("douban_id", out var douban) && douban.TryGetInt64(out var db) && db > 0) result["Douban"] = db.ToString(CultureInfo.InvariantCulture);
        if (e.TryGetProperty("bgm_id", out var bgm) && bgm.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(bgm.GetString()))
        {
            var parts = bgm.GetString()!.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.All(p => long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) result["Bangumi"] = string.Join(",", parts);
        }
        return result;
    }

    public static JsonElement? SeasonIds(JsonElement? file, int? season)
    {
        if (file is not { } f || season is null || !f.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array) return null;
        foreach (var s in seasons.EnumerateArray())
            if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("season_number", out var n) && n.TryGetInt32(out var number) && number == season &&
                s.TryGetProperty("external_ids", out var ids)) return ids;
        return null;
    }

    /// <summary>Original episode order (A:54872 ff.): only for season 0; (sort season, sort episode) for the episode.</summary>
    public static (int? SortSeason, int? SortEpisode)? EpisodeOrder(JsonElement? file, int? season, int? episode)
    {
        if (file is not { } f || season != 0 || episode is null || !f.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array) return null;
        foreach (var s in seasons.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("season_number", out var n) || !n.TryGetInt32(out var number) || number != season ||
                !s.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in episodes.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("episode_number", out var en) && en.TryGetInt32(out var value) && value == episode)
                    return (Int(e, "sort_season_number"), Int(e, "sort_episode_number"));
        }
        return null;
        static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;
    }

    /// <summary>Applies the matching override files to the item; returns true when something changed.</summary>
    public bool Apply(BaseItem item)
    {
        var changed = false;
        switch (item)
        {
            case Movie or Series:
            {
                var kind = item is Movie ? "movie" : "series";
                var id = item.GetProviderId(MetadataProviders.Tmdb);
                if (Tags(Read(PathFor(kind, id, "tags"))) is { } tags && !item.IsFieldLocked(MetadataFields.Tags) && !tags.SequenceEqual(item.Tags ?? Array.Empty<string>()))
                { item.Tags = tags; changed = true; }
                var file = Read(PathFor(kind, id, "external_ids"));
                if (file is { } f && f.TryGetProperty("external_ids", out var ids)) changed |= SetIds(item, ProviderIds(ids));
                break;
            }
            case Season season:
            {
                var id = season.Series?.GetProviderId(MetadataProviders.Tmdb);
                changed |= SetIds(item, ProviderIds(SeasonIds(Read(PathFor("series", id, "external_ids")), season.IndexNumber)));
                break;
            }
            case Episode episode:
            {
                var id = episode.Series?.GetProviderId(MetadataProviders.Tmdb);
                if (episode.ParentIndexNumber == 0 && EpisodeOrder(Read(PathFor("series", id, "episodeorder")), 0, episode.IndexNumber) is { } order)
                {
                    if (!episode.IsFieldLocked(MetadataFields.SortParentIndexNumber) && episode.SortParentIndexNumber != order.SortSeason) { episode.SortParentIndexNumber = order.SortSeason; changed = true; }
                    if (!episode.IsFieldLocked(MetadataFields.SortIndexNumber) && episode.SortIndexNumber != order.SortEpisode) { episode.SortIndexNumber = order.SortEpisode; changed = true; }
                }
                break;
            }
        }
        if (changed) Interlocked.Increment(ref applied);
        return changed;
    }

    private static bool SetIds(BaseItem item, Dictionary<string, string> ids)
    {
        if (ids.Count == 0) return false;
        var changed = false;
        foreach (var (key, value) in ids)
            if (!string.Equals(item.GetProviderId(key), value, StringComparison.Ordinal)) { item.SetProviderId(key, value); changed = true; }
        return changed;
    }
}
