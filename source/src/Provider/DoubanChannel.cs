using System.Collections.Concurrent;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace StrmAssistantRebuild.Provider;

public sealed record DoubanChannelFolder(string Key, string Name, string Overview);

/// <summary>M149/F2: original "豆瓣推荐" channel — hot movie/TV/variety folders whose items play Douban trailers.</summary>
public sealed class DoubanChannel : IChannel, IHasChannelFeatures, IHasChangeEvent, IRequiresMediaInfoCallback
{
    // The channel name is its identity in Emby (internal id derives from it) and the user-view filter matches it.
    public const string ChannelName = "豆瓣推荐";
    public static DoubanChannel? Current { get; private set; }
    public DoubanChannel() { Current = this; }
    public string Name => ChannelName;
    public string Description => "豆瓣热门电影、剧集与综艺";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public ChannelFeatures Features => new() { ShowRootFoldersAtTopLevel = false };
    public event EventHandler? ContentChanged;
    public void NotifyContentChanged() => ContentChanged?.Invoke(this, EventArgs.Empty);
    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) => Task.FromResult(new DynamicImageResponse());
    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken) =>
        DoubanChannelService.Current?.GetItemsAsync(query?.FolderId, cancellationToken) ?? Task.FromResult(DoubanChannelService.Empty());
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken) =>
        DoubanChannelService.Current is { } service ? await service.GetMediaSourcesAsync(id, cancellationToken).ConfigureAwait(false) : Array.Empty<MediaSourceInfo>();
}

public sealed class DoubanChannelService
{
    public const string ItemPrefix = "douban:v2:";
    public const int CollectionSize = 50;
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
    public static readonly DoubanChannelFolder[] Folders =
    {
        new("movie_hot_gaia", "热门电影", "豆瓣热门电影"),
        new("tv_hot", "热门剧集", "豆瓣热门剧集"),
        new("show_hot", "热门综艺", "豆瓣热门综艺")
    };
    public static DoubanChannelService? Current { get; set; }
    private readonly DoubanFrodoClient client;
    private readonly string imageRoot;
    private readonly Func<bool> enabled;
    private readonly ILogger logger;
    private readonly Func<DateTimeOffset> clock;
    private readonly Action<string, string>? invalidateCover;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Fetched, IReadOnlyList<DoubanHotItem> Items)> cache = new(StringComparer.Ordinal);
    /// <param name="invalidateCover">(channel external id, superseded poster path): drop that Primary image from the library item if it still points there.</param>
    public DoubanChannelService(DoubanFrodoClient client, string imageRoot, Func<bool> enabled, ILogger logger, Func<DateTimeOffset>? clock = null,
        Action<string, string>? invalidateCover = null)
    {
        this.client = client; this.imageRoot = imageRoot; this.enabled = enabled; this.logger = logger;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow); this.invalidateCover = invalidateCover;
    }
    public long CoverInvalidations => Interlocked.Read(ref coverInvalidations);
    private long coverInvalidations;
    public bool Enabled => enabled();
    public int CachedCollections => cache.Count;
    public static ChannelItemResult Empty() => new() { Items = new List<ChannelItemInfo>(), TotalRecordCount = 0 };
    public static bool IsChannelItemId(string? id) => id != null && id.StartsWith(ItemPrefix, StringComparison.Ordinal);
    public static string ItemId(string folder, string type, string id) => ItemPrefix + folder + ":" + type + ":" + id;
    public static bool TryParseItemId(string? value, out string folder, out string type, out string id)
    {
        folder = type = id = "";
        if (!IsChannelItemId(value)) return false;
        var parts = value!.Substring(ItemPrefix.Length).Split(':');
        if (parts.Length != 3 || Folders.All(f => f.Key != parts[0]) || parts[1] is not ("movie" or "tv") || !DoubanSubjectParser.IsSubjectId(parts[2])) return false;
        (folder, type, id) = (parts[0], parts[1], parts[2]);
        return true;
    }

    public async Task<ChannelItemResult> GetItemsAsync(string? folderId, CancellationToken token)
    {
        if (!Enabled) return Empty();
        if (folderId == null) return await RootAsync(token).ConfigureAwait(false);
        var folder = Folders.FirstOrDefault(f => string.Equals(f.Key, folderId, StringComparison.OrdinalIgnoreCase));
        if (folder == null) return Empty();
        var entry = await GetCollectionAsync(folder.Key, token).ConfigureAwait(false);
        if (entry == null) return Empty();
        var items = new List<ChannelItemInfo>();
        foreach (var hot in entry.Value.Items)
        {
            token.ThrowIfCancellationRequested();
            items.Add(new ChannelItemInfo
            {
                Id = ItemId(folder.Key, hot.Type, hot.Id),
                Name = hot.Title,
                OriginalTitle = hot.OriginalTitle,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Clip,
                ExtraType = ExtraType.Trailer,
                ImageUrl = await CacheImageAsync(hot, token).ConfigureAwait(false),
                Overview = hot.CardSubtitle ?? hot.Info,
                Genres = DoubanFrodo.Genres(hot.CardSubtitle),
                ProviderIds = new ProviderIdDictionary { [DoubanExternalId.StaticName] = hot.Id },
                CommunityRating = hot.Rating,
                ProductionYear = int.TryParse(hot.Year, out var year) ? year : null,
                PremiereDate = DoubanFrodo.PremiereDate(hot.Year, hot.ReleaseDate),
                // Hot-list order is kept by creation time; ForceUpdate refreshes rating/order on every native listing.
                DateCreated = entry.Value.Fetched.AddMinutes(-items.Count),
                DateModified = entry.Value.Fetched,
                ForceUpdate = true
            });
        }
        logger.Info("[StrmAssistantRebuild] DoubanChannel - {0}: {1} items", folder.Key, items.Count);
        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }
    private async Task<ChannelItemResult> RootAsync(CancellationToken token)
    {
        var now = clock();
        var items = new List<ChannelItemInfo>();
        foreach (var folder in Folders)
        {
            var entry = await GetCollectionAsync(folder.Key, token).ConfigureAwait(false);
            var sample = entry?.Items.FirstOrDefault(i => i.IsPortrait && i.ImageUrl != null) ?? entry?.Items.FirstOrDefault(i => i.ImageUrl != null);
            items.Add(new ChannelItemInfo
            {
                Id = folder.Key, Name = folder.Name, Overview = folder.Overview,
                Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container,
                ImageUrl = sample == null ? null : await CacheImageAsync(sample, token).ConfigureAwait(false),
                DateCreated = now.AddMinutes(-items.Count), DateModified = now, ForceUpdate = true
            });
        }
        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    /// <summary>30-minute cache per hot list; a failed refresh keeps serving the last good list.</summary>
    internal async Task<(DateTimeOffset Fetched, IReadOnlyList<DoubanHotItem> Items)?> GetCollectionAsync(string key, CancellationToken token)
    {
        if (cache.TryGetValue(key, out var fresh) && clock() - fresh.Fetched < CacheLifetime) return fresh;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(key, out fresh) && clock() - fresh.Fetched < CacheLifetime) return fresh;
            try
            {
                var items = await client.GetCollectionAsync(key, CollectionSize, token).ConfigureAwait(false);
                if (items.Count == 0)
                {
                    logger.Warn("[StrmAssistantRebuild] DoubanChannel - {0} returned no items", key);
                    return cache.TryGetValue(key, out var kept) ? kept : null;
                }
                var entry = (clock(), items);
                cache[key] = entry;
                // Only with every hot list known can an unreferenced poster be told apart from another folder's.
                if (Folders.All(f => cache.ContainsKey(f.Key))) PruneImages(TimeSpan.FromDays(7));
                return entry;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                logger.Warn("[StrmAssistantRebuild] DoubanChannel - {0} fetch failed: {1}", key, ex.Message);
                return cache.TryGetValue(key, out var stale) ? stale : null;
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Douban images reject requests without app UA/referer, so posters are served from a local copy named
    /// {subject}-{hash of cover URL}. A new cover URL yields a new file; like the original's cover invalidation, library items
    /// still showing the superseded file lose that Primary image so Emby applies the new one on this listing.
    /// </summary>
    internal async Task<string?> CacheImageAsync(DoubanHotItem item, CancellationToken token)
    {
        if (item.ImageUrl == null) return null;
        try
        {
            Directory.CreateDirectory(imageRoot);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.ImageUrl)))[..8].ToLowerInvariant();
            static bool Image(string f) => Path.GetExtension(f) is ".jpg" or ".png" or ".webp";
            var existing = Directory.EnumerateFiles(imageRoot, item.Id + "-" + hash + ".*").FirstOrDefault(Image);
            if (existing != null) return existing;
            // Older renditions: other hashes, or the unhashed M149 name.
            var superseded = Directory.EnumerateFiles(imageRoot, item.Id + "*").Where(f => Image(f) &&
                Path.GetFileNameWithoutExtension(f) is var stem && (stem == item.Id || stem.StartsWith(item.Id + "-", StringComparison.Ordinal))).ToArray();
            var (data, extension) = await client.GetImageAsync(item.ImageUrl, token).ConfigureAwait(false);
            var path = Path.Combine(imageRoot, item.Id + "-" + hash + extension);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, data, token).ConfigureAwait(false);
            File.Move(temp, path, true);
            foreach (var old in superseded)
            {
                // Every place this subject's poster can appear: its item in each hot folder, and each folder cover.
                foreach (var external in Folders.Select(f => ItemId(f.Key, item.Type, item.Id)).Concat(Folders.Select(f => f.Key)))
                {
                    try { invalidateCover?.Invoke(external, old); }
                    catch (Exception ex) { logger.Debug("[StrmAssistantRebuild] DoubanChannel - {0} cover invalidation failed: {1}", external, ex.Message); }
                }
                Interlocked.Increment(ref coverInvalidations);
                try { if (File.Exists(old)) File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            logger.Debug("[StrmAssistantRebuild] DoubanChannel - image failed for {0}: {1}", item.Id, ex.Message);
            return null;
        }
    }
    /// <summary>Drops posters of subjects that left every cached hot list and are older than the retention window.</summary>
    public int PruneImages(TimeSpan retention)
    {
        if (!Directory.Exists(imageRoot)) return 0;
        var keep = cache.Values.SelectMany(v => v.Items).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(imageRoot))
        {
            var name = Path.GetFileName(file);
            var stem = name.Split('.')[0].Split('-')[0];
            if (keep.Contains(stem) && !name.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            try { if (clock() - File.GetLastWriteTimeUtc(file) >= retention) { File.Delete(file); removed++; } } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesAsync(string id, CancellationToken token)
    {
        if (!Enabled || !TryParseItemId(id, out _, out var type, out var subject)) return Array.Empty<MediaSourceInfo>();
        IReadOnlyList<DoubanTrailerInfo> trailers;
        try { trailers = await client.GetTrailersAsync(type, subject, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            logger.Warn("[StrmAssistantRebuild] DoubanChannel - trailers for {0} failed: {1}", subject, ex.Message);
            return Array.Empty<MediaSourceInfo>();
        }
        return trailers.Select((t, i) => new MediaSourceInfo
        {
            Id = "douban-trailer-" + (t.Id ?? (i + 1).ToString()),
            Name = t.Title ?? "Trailer " + (i + 1),
            Path = t.VideoUrl,
            Protocol = MediaProtocol.Http,
            Container = Path.GetExtension(new Uri(t.VideoUrl).AbsolutePath).TrimStart('.').ToLowerInvariant() is { Length: > 0 } ext ? ext : "mp4",
            RunTimeTicks = DoubanFrodo.RuntimeTicks(t.Runtime),
            IsRemote = true,
            // Deviation from the original (DirectPlay=true): doubanio answers 418 to browser requests without the app
            // UA/Referer, so clients must go through Emby's stream endpoint, which sends RequiredHttpHeaders (M151 host check).
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = false,
            RequiredHttpHeaders = new Dictionary<string, string> { ["User-Agent"] = DoubanFrodo.UserAgent, ["Referer"] = "https://movie.douban.com/" }
        }).ToArray();
    }
}
