using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace StrmAssistantRebuild.Common;

/// <summary>
/// M156: the original's ffprobe eligibility inside a native refresh (decompiled metadata CanRefresh prefix,
/// notes/decrypted/A----------/A----------.cs:70601-70985, unflattened). Applied where the rebuild enforces exclusive
/// mode: a non-owned probe during the native RefreshSingleItem of an old Video/Audio item may run when the original
/// would have let the "ffprobe" provider run:
///   ordinary refresh (metadata and image modes at most Default) -> blocked (file-changed exception stays M139);
///   CatchAllAllow / CatchAllAllowChapterImage -> allowed;  Identify (SearchResult) -> blocked;
///   non-STRM item with ReplaceAllImages and neither CatchAllBlock nor UnconditionalBlock -> allowed;
///   media complete -> blocked;  media missing/incomplete: allowed, except CatchAllBlock (only "metadata-only full
///   refresh") and UnconditionalBlock (never).
/// New items stay blocked (the rebuild extracts them itself, as the original did).
/// M160: per-item feature marks (original A----------.cs:19532 ff., item dictionary that replaces the global set) written
/// around the plugin's Refresh Metadata task and catch-up episode refresh for Video items: {CatchAllBlock, IgnoreExtSubChange}.
/// </summary>
public static class ExclusiveEligibility
{
    public const string CatchAllAllow = "CatchAllAllow", CatchAllAllowChapterImage = "CatchAllAllowChapterImage", CatchAllBlock = "CatchAllBlock", UnconditionalBlock = "UnconditionalBlock";
    private static readonly string[] Names = { CatchAllAllow, CatchAllAllowChapterImage, CatchAllBlock, UnconditionalBlock };
    // M157: IgnoreFileChange (0), ExtractOnFileChange (1) and IgnoreExtSubChange (5) are consumed by ExclusiveFileChange.
    // M159: CatchAllAllowChapterImage (3) behaves as CatchAllAllow and is consumed by ExclusiveChapterImageControl.
    private static readonly string[] Accepted = { CatchAllAllow, CatchAllAllowChapterImage, CatchAllBlock, UnconditionalBlock,
        ExclusiveFileChange.IgnoreFileChange, ExclusiveFileChange.ExtractOnFileChange, ExclusiveFileChange.IgnoreExtSubChange };
    private static volatile string[] selected = Array.Empty<string>();
    private static long allowed, denied;
    public static long Allowed => Interlocked.Read(ref allowed);
    public static long Denied => Interlocked.Read(ref denied);
    public static IReadOnlyCollection<string> Selected => selected;

    public static string Strip(string? features) => string.Join(",", Split(features).Where(f => !Accepted.Contains(f, StringComparer.OrdinalIgnoreCase)));
    private static string[] Split(string? features) => (features ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static bool ValidCombination(string? features, out string detail)
    {
        var chosen = Split(features).Where(f => Names.Contains(f, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (chosen.Length != chosen.Distinct(StringComparer.OrdinalIgnoreCase).Count()) { detail = "Exclusive gate inactive; duplicate eligibility feature."; return false; }
        if (chosen.Any(f => AllowFamily.Contains(f, StringComparer.OrdinalIgnoreCase)) && chosen.Any(f => !AllowFamily.Contains(f, StringComparer.OrdinalIgnoreCase)))
        { detail = "Exclusive gate inactive; CatchAllAllow/CatchAllAllowChapterImage cannot be combined with a block feature."; return false; }
        detail = "";
        return true;
    }
    /// <summary>Original implicit features (A----------.cs:20885-21002): PersistMediaInfoMode Restore adds IgnoreFileChange and CatchAllBlock.</summary>
    /// <summary>Set by the plugin before exclusive configuration (PersistMediaInfoMode).</summary>
    public static string? PersistMode { get; set; }
    public static void Configure(string? features, string? persistMode = null)
    {
        var chosen = Split(features).Where(f => Accepted.Contains(f, StringComparer.OrdinalIgnoreCase)).Select(f => Accepted.First(a => a.Equals(f, StringComparison.OrdinalIgnoreCase))).ToList();
        if (string.Equals(persistMode, "Restore", StringComparison.OrdinalIgnoreCase))
            foreach (var implicitFeature in new[] { ExclusiveFileChange.IgnoreFileChange, CatchAllBlock }) if (!chosen.Contains(implicitFeature)) chosen.Add(implicitFeature);
        selected = chosen.ToArray();
    }
    public static void Clear() { selected = Array.Empty<string>(); ItemMarks.Clear(); }

    // M160: original per-item feature dictionary. An item with an entry uses only its own features (the global selection,
    // including implicit Restore features, is ignored for it); the entry is removed when the marking operation ends.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string[]> ItemMarks = new();
    private static long marked;
    public static long ItemsMarked => Interlocked.Read(ref marked);
    public static int ActiveItemMarks => ItemMarks.Count;
    /// <summary>Original task marks (RefreshMetadataTask.cs:1283-1330): a Video refreshed by the plugin's metadata task or
    /// catch-up episode refresh is treated as {CatchAllBlock, IgnoreExtSubChange} until the refresh ends.</summary>
    public static readonly string[] TaskFeatures = { CatchAllBlock, ExclusiveFileChange.IgnoreExtSubChange };
    public static IDisposable? EnterTaskItem(BaseItem item)
    {
        if (item is not Video || item.InternalId <= 0) return null;
        MarkItem(item.InternalId, TaskFeatures);
        return new ItemMark(item.InternalId);
    }
    internal static void MarkItem(long id, params string[] features)
    {
        ItemMarks.AddOrUpdate(id, features.Distinct().ToArray(), (_, old) => old.Union(features).ToArray());
        Interlocked.Increment(ref marked);
    }
    internal static void ClearItem(long id) => ItemMarks.TryRemove(id, out _);
    private sealed class ItemMark(long id) : IDisposable { private int done; public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) ClearItem(id); } }
    /// <summary>Original Has(id, feature): the item's own marks when present, otherwise the global selection.</summary>
    public static IReadOnlyCollection<string> SelectedFor(long? id) => id is { } x && ItemMarks.TryGetValue(x, out var own) ? own : selected;
    private static bool Has(string feature, long? id = null) => SelectedFor(id).Contains(feature);
    private static readonly string[] AllowFamily = { CatchAllAllow, CatchAllAllowChapterImage };
    /// <summary>Original HasAny(id, CatchAllAllow, CatchAllAllowChapterImage).</summary>
    internal static bool AllowAllFor(long? id) => Has(CatchAllAllow, id) || Has(CatchAllAllowChapterImage, id);
    internal static bool AllowAll => AllowAllFor(null);
    internal static bool ChapterImageSelectedFor(long? id) => Has(CatchAllAllowChapterImage, id);
    internal static bool ChapterImageSelected => ChapterImageSelectedFor(null);
    /// <summary>M159: the live native refresh is this item's own (parented file Video/Audio, not plugin-owned).</summary>
    internal static bool InNativeRefreshOf(BaseItem item) => ContextFor(item) != null;

    private static long noContext;
    /// <summary>M165: non-owned probes that found no live native refresh context (and why, for the last one).</summary>
    public static long NoContext => Interlocked.Read(ref noContext);
    public static string LastNoContext { get; private set; } = "";
    private static long remoteSkipped, dynamicSkipped, imagesAllowed;
    public static long RemoteSkipped => Interlocked.Read(ref remoteSkipped);
    public static long DynamicSkipped => Interlocked.Read(ref dynamicSkipped);
    public static long ImagesAllowed => Interlocked.Read(ref imagesAllowed);
    /// <summary>The original's per-refresh context exists only for this item: parented (or extra) file-protocol Video/Audio, not plugin-owned.</summary>
    private static (BaseItem Item, MetadataRefreshOptions Options, bool IsNew, bool Ordinary, ExclusiveFileChange.State? FileChange)? ContextFor(BaseItem item)
    {
        if (item is not (Video or Audio) || item.InternalId <= 0 || !item.IsFileProtocol || (item.ParentId <= 0 && !item.ExtraType.HasValue) || ExclusiveMediaControl.IsOwnedOperation) return null;
        var c = ExclusiveSaverControl.CurrentNativeRefresh;
        return c is { } x && x.Item.InternalId == item.InternalId ? x : null;
    }
    /// <summary>Original metadata prefix L49/L18 and image prefix L6: remote providers skipped on an ordinary refresh of an old item.</summary>
    public static bool SkipRemoteOnOrdinary(BaseItem item)
    {
        var skip = ContextFor(item) is { IsNew: false, Ordinary: true };
        if (skip) Interlocked.Increment(ref remoteSkipped);
        return skip;
    }
    /// <summary>Original image prefix L4/L25/L29: an old item that already has a Primary image skips screen grabs unless allowed.</summary>
    public static bool BlockDynamicImage(BaseItem item)
    {
        if (ContextFor(item) is not { IsNew: false } c) return false;
        var block = !DynamicPermitted(false, item.HasImage(ImageType.Primary, 0), c.Options.ReplaceAllImages, AllowAllFor(item.InternalId), Has(CatchAllBlock, item.InternalId), Has(UnconditionalBlock, item.InternalId));
        if (block) Interlocked.Increment(ref dynamicSkipped);
        return block;
    }
    /// <summary>Pure screen-grab rule: new item or no Primary -> allowed; otherwise only CatchAllAllow or ReplaceAllImages without a block feature.</summary>
    public static bool DynamicPermitted(bool isNew, bool hasPrimary, bool replaceAllImages, bool catchAllAllow, bool catchAllBlock, bool unconditionalBlock) =>
        isNew || !hasPrimary || !catchAllBlock && !unconditionalBlock && (catchAllAllow || replaceAllImages);
    /// <summary>The rebuild's low-level gate: a non-owned single-image extraction inside a native refresh is the screen grab the original let run.</summary>
    internal static bool AllowNativeImage()
    {
        if (ExclusiveSaverControl.CurrentNativeRefresh is not { } c || ContextFor(c.Item) == null || c.Item.IsShortcut) return false;
        var allow = DynamicPermitted(c.IsNew, c.Item.HasImage(ImageType.Primary, 0), c.Options.ReplaceAllImages, AllowAllFor(c.Item.InternalId), Has(CatchAllBlock, c.Item.InternalId), Has(UnconditionalBlock, c.Item.InternalId));
        if (allow) Interlocked.Increment(ref imagesAllowed);
        return allow;
    }

    internal static bool AllowNativeProbe()
    {
        var context = ExclusiveSaverControl.CurrentNativeRefresh;
        if (context is not { } c)
        {
            Interlocked.Increment(ref noContext);
            try { LastNoContext = ExclusiveSaverControl.DescribeScope(); } catch { }
            return false;
        }
        var id = c.Item.InternalId;
        var allow = Decide(c.Item, c.Options, c.IsNew, c.Ordinary, AllowAllFor(id), Has(CatchAllBlock, id), Has(UnconditionalBlock, id),
            fileChanged: c.FileChange?.Changed == true, ignoreFileChange: Has(ExclusiveFileChange.IgnoreFileChange, id));
        if (allow && c.FileChange != null) c.FileChange.ProbeAllowed = true;
        Interlocked.Increment(ref allow ? ref allowed : ref denied);
        return allow;
    }

    /// <summary>Pure decision table (unit-tested); see the class summary.</summary>
    public static bool Decide(BaseItem item, MetadataRefreshOptions options, bool isNew, bool ordinary, bool catchAllAllow, bool catchAllBlock, bool unconditionalBlock, Func<BaseItem, (bool HasMedia, bool? Incomplete)>? media = null,
        bool fileChanged = false, bool ignoreFileChange = false)
    {
        if (item is not (Video or Audio) || item.InternalId <= 0 || isNew) return false;
        // M157: a changed file goes to native ffprobe unless IgnoreFileChange (original metadata prefix L12/L27).
        if (fileChanged && !ignoreFileChange) return true;
        if (ordinary) return false;
        if (catchAllAllow) return true;
        if (options.SearchResult != null) return false;
        var blockSet = catchAllBlock || unconditionalBlock;
        if (!blockSet && !item.IsShortcut && options.ReplaceAllImages) return true;
        var (hasMedia, incomplete) = (media ?? MediaState)(item);
        if (hasMedia && incomplete == false) return false;
        if (!blockSet && !item.IsShortcut) return true;
        if (catchAllBlock && !MetadataOnlyFull(options)) return false;
        return !unconditionalBlock;
    }
    public static bool MetadataOnlyFull(MetadataRefreshOptions o) =>
        o.MetadataRefreshMode == MetadataRefreshMode.FullRefresh && o.ImageRefreshMode == MetadataRefreshMode.Default && !o.ReplaceAllMetadata && !o.ReplaceAllImages;
    /// <summary>Original A----------.cs:100731 with strict=true: (has a non-external audio/video stream, incomplete).</summary>
    public static (bool HasMedia, bool? Incomplete) MediaState(BaseItem item)
    {
        List<MediaStream> streams;
        try { streams = item.GetMediaStreams() ?? new List<MediaStream>(); } catch { return (false, null); }
        if (!streams.Any(s => !s.IsExternal && s.Type is MediaStreamType.Video or MediaStreamType.Audio)) return (false, null);
        var incomplete = item is Movie or Episode ? item.Size < 1048576 || !item.RunTimeTicks.HasValue : item.Size == 0;
        return (true, incomplete);
    }
}
