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
/// Not reproduced: per-item feature marks written by the original's own metadata task.
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
    public static void Clear() => selected = Array.Empty<string>();
    private static bool Has(string feature) => selected.Contains(feature);
    private static readonly string[] AllowFamily = { CatchAllAllow, CatchAllAllowChapterImage };
    /// <summary>Original HasAny(id, CatchAllAllow, CatchAllAllowChapterImage).</summary>
    internal static bool AllowAll => Has(CatchAllAllow) || Has(CatchAllAllowChapterImage);
    internal static bool ChapterImageSelected => Has(CatchAllAllowChapterImage);
    /// <summary>M159: the live native refresh is this item's own (parented file Video/Audio, not plugin-owned).</summary>
    internal static bool InNativeRefreshOf(BaseItem item) => ContextFor(item) != null;

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
        var block = !DynamicPermitted(false, item.HasImage(ImageType.Primary, 0), c.Options.ReplaceAllImages, AllowAll, Has(CatchAllBlock), Has(UnconditionalBlock));
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
        var allow = DynamicPermitted(c.IsNew, c.Item.HasImage(ImageType.Primary, 0), c.Options.ReplaceAllImages, AllowAll, Has(CatchAllBlock), Has(UnconditionalBlock));
        if (allow) Interlocked.Increment(ref imagesAllowed);
        return allow;
    }

    internal static bool AllowNativeProbe()
    {
        var context = ExclusiveSaverControl.CurrentNativeRefresh;
        if (context is not { } c) return false;
        var allow = Decide(c.Item, c.Options, c.IsNew, c.Ordinary, AllowAll, Has(CatchAllBlock), Has(UnconditionalBlock),
            fileChanged: c.FileChange?.Changed == true, ignoreFileChange: Has(ExclusiveFileChange.IgnoreFileChange));
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
