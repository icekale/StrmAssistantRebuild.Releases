using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;

namespace StrmAssistantRebuild.Common;

/// <summary>
/// M157: the original's file-change handling in exclusive mode (image CanRefresh prefix
/// notes/decrypted/A----------/A----------.cs:70112-70598 and completion postfix :71190-71452, unflattened).
/// At the start of a native refresh of an old, parented, file-protocol Video/Audio item:
///  - the file is "changed" when its last write time differs from DateModified (a rollback is not a change);
///  - unless IgnoreExtSubChange (5): a Video whose external subtitle/audio files changed enables remote content
///    probing and, if ffprobe then does not run, gets its external tracks updated after the refresh;
///  - unless IgnoreFileChange (0), a changed file clears the item's media state (streams, chapters, chapter images,
///    Episode fingerprints, media-info JSON) when it had media, or only its JSON when it had none, and native ffprobe
///    may run for it; ExtractOnFileChange (1) also enables remote probing for a changed item that had media (STRM);
///  - CatchAllAllow / CatchAllAllowChapterImage enable remote probing too.
/// After a successful refresh DateModified is moved to the observed file time, as the original does.
/// Rebuild differences: cleared files are renamed to recovery backups rather than deleted; cleanup and track updates
/// use the rebuild's existing primitives.
/// </summary>
public static class ExclusiveFileChange
{
    public const string IgnoreFileChange = "IgnoreFileChange", ExtractOnFileChange = "ExtractOnFileChange", IgnoreExtSubChange = "IgnoreExtSubChange";
    public sealed class State
    {
        public bool Changed, HadMedia, ExtTracksChanged, Cleared;
        public DateTimeOffset? Stamp;
        public DateTimeOffset OriginalDate;
        public volatile bool ProbeAllowed;
    }
    public sealed record Hooks(Action<BaseItem, bool, CancellationToken> Clear, Func<BaseItem, bool> ExternalTracksChanged,
        Func<BaseItem, CancellationToken, Task> UpdateExternalTracks, Action<BaseItem, DateTimeOffset> UpdateDateModified);
    private static volatile Hooks? hooks;
    private static long cleared, trackUpdates, stamps, changes, completions;
    public static long Completions => Interlocked.Read(ref completions);
    public static long Changes => Interlocked.Read(ref changes);
    public static long Cleared => Interlocked.Read(ref cleared);
    public static long TrackUpdates => Interlocked.Read(ref trackUpdates);
    public static long StampUpdates => Interlocked.Read(ref stamps);
    public static string LastError { get; private set; } = "";
    private static void Fail(string step, Exception error) => LastError = step + ": " + error.GetType().Name + ": " + error.Message;
    public static void Configure(Hooks? value) => hooks = value;
    /// <summary>Test seam for the media-state probe; production uses ExclusiveEligibility.MediaState.</summary>
    internal static Func<BaseItem, (bool HasMedia, bool? Incomplete)> MediaState = ExclusiveEligibility.MediaState;

    /// <summary>Original IsFileChanged (AA---------.cs:43520): (changed, observed file time).</summary>
    public static (bool Changed, DateTimeOffset? Stamp) FileChanged(BaseItem item)
    {
        try
        {
            if (!item.IsFileProtocol || string.IsNullOrEmpty(item.Path)) return (false, null);
            var info = new FileInfo(item.Path);
            if (!info.Exists) return (false, null);
            var written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (item.DateModified > written) return (false, written);
            return (item.HasDateModifiedChanged(written), written);
        }
        catch { return (false, null); }
    }
    internal static bool Eligible(BaseItem item) => item is Video or Audio && item.InternalId > 0 && item.IsFileProtocol &&
        (item.ParentId > 0 || item.ExtraType.HasValue);

    internal static State? Begin(BaseItem item, MetadataRefreshOptions options, bool isNew, CancellationToken token)
    {
        var h = hooks;
        if (h == null || isNew || !Eligible(item) || ExclusiveMediaControl.IsOwnedOperation) return null;
        var selected = ExclusiveEligibility.SelectedFor(item.InternalId); // M160: per-item task marks replace the global set
        var state = new State();
        state.OriginalDate = item.DateModified;
        (state.Changed, state.Stamp) = FileChanged(item);
        if (state.Changed) Interlocked.Increment(ref changes);
        state.HadMedia = MediaState(item).HasMedia;
        if (!selected.Contains(IgnoreExtSubChange) && item is Video)
        {
            try { if (h.ExternalTracksChanged(item)) { state.ExtTracksChanged = true; options.EnableRemoteContentProbe = true; } } catch (Exception error) { Fail("external track check", error); }
        }
        if (!selected.Contains(IgnoreFileChange) && state.Changed)
        {
            try { h.Clear(item, state.HadMedia, token); state.Cleared = true; Interlocked.Increment(ref cleared); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { Fail("clear", error); /* The original swallows cleanup faults; native refresh continues. */ }
            if (selected.Contains(ExtractOnFileChange) && state.HadMedia) options.EnableRemoteContentProbe = true;
        }
        if (selected.Contains(ExclusiveEligibility.CatchAllAllow) || selected.Contains(ExclusiveEligibility.CatchAllAllowChapterImage)) options.EnableRemoteContentProbe = true;
        return state;
    }

    internal static async Task Complete(BaseItem item, State state, CancellationToken token)
    {
        Interlocked.Increment(ref completions);
        var h = hooks;
        if (h == null) return;
        // Native may move DateModified in memory without saving the item (nothing else changed), so compare with the
        // value seen at the start of the refresh and always persist the observed file time.
        if (state.Stamp is { } stamp && BaseItem.HasDateChanged(state.OriginalDate, stamp))
        {
            try { h.UpdateDateModified(item, stamp); Interlocked.Increment(ref stamps); } catch (Exception error) { Fail("date stamp", error); }
        }
        if (state.ExtTracksChanged && !state.ProbeAllowed)
        {
            try { await h.UpdateExternalTracks(item, token).ConfigureAwait(false); Interlocked.Increment(ref trackUpdates); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { Fail("external tracks", error); }
        }
    }
}
