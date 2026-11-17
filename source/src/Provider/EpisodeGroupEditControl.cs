using System.Reflection;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Provider;

/// <summary>
/// M173: editing a series' TmdbEg in Emby's metadata editor. The original (notes/decrypted/A----------/A----------.cs:62869-63118)
/// wraps the private Emby.Api ItemUpdateService.UpdateItem(BaseItemDto, BaseItem) while MovieDbEpisodeGroup is on: when the value
/// changes (ordinal, case-insensitive), every other series with the same PresentationUniqueKey gets the new value (an empty value
/// removes it) and is updated as a metadata edit and saved; the series sidecar episodegroup.json of each is deleted so the new
/// group is loaded; the old group's cache is dropped. No lock checks, no refresh.
/// Rebuild additions: the pre-M170 TmdbEpisodeGroup key is removed with a change (it would otherwise keep the old group), the
/// cache of the old group is dropped for every language without needing the series TMDB id, an empty presentation key never
/// widens the query, and failures are logged instead of failing the edit request.
/// </summary>
public static class EpisodeGroupEditControl
{
    internal const string Owner = MetadataEditNotification.EpisodeGroupEditOwner;
    private sealed record State(ILibraryManager Library, IProviderManager Providers, Func<EpisodeGroupService?> Groups, ILogger? Logger);
    private static readonly object Gate = new();
    private static State? active;
    private static Harmony? patcher;
    private static MethodInfo? installed;
    private static long changes, synced, sidecarsDeleted, cachesCleared, failures;
    public static long Changes => Interlocked.Read(ref changes);
    public static long SeriesSynced => Interlocked.Read(ref synced);
    public static long SidecarsDeleted => Interlocked.Read(ref sidecarsDeleted);
    public static long CachesCleared => Interlocked.Read(ref cachesCleared);
    public static long Failures => Interlocked.Read(ref failures);
    public static bool IsEnabled => Volatile.Read(ref active) != null;
    public static string Detail { get; private set; } = "Disabled; native metadata editor unchanged.";

    public static bool Configure(Version hostVersion, bool enabled, ILibraryManager library, IProviderManager providers,
        Func<EpisodeGroupService?> groups, ILogger? logger, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try
            {
                Remove();
                if (!enabled) { detail = Detail = "Disabled; native metadata editor restored."; return true; }
                // Same pinned pair as the metadata edit notification; only the private UpdateItem is patched here.
                var targets = MetadataEditNotification.FindTargets();
                var target = targets.FirstOrDefault(m => m.Name == "UpdateItem");
                if (hostVersion != new Version(4, 10, 2, 0) || targets.Length != 2 || target == null || !MetadataEditNotification.Verify(target))
                { detail = Detail = "Inactive: exact Emby 4.10.2 ItemUpdateService.UpdateItem contract, MVID or IL differs; TmdbEg edits are native."; return false; }
                if (Harmony.GetPatchInfo(target)?.Owners.Any(owner => owner != Owner) == true)
                { detail = Detail = "Inactive: foreign ItemUpdateService.UpdateItem patch; TmdbEg edits are native."; return false; }
                installed = target; // Keep the removal handle even if installation throws partway through.
                (patcher ??= new Harmony(Owner)).Patch(target,
                    prefix: new HarmonyMethod(typeof(EpisodeGroupEditControl).GetMethod(nameof(Before), BindingFlags.Static | BindingFlags.NonPublic)!),
                    postfix: new HarmonyMethod(typeof(EpisodeGroupEditControl).GetMethod(nameof(After), BindingFlags.Static | BindingFlags.NonPublic)!));
                Volatile.Write(ref active, new State(library, providers, groups, logger));
                detail = Detail = "Enabled: a changed series TmdbEg is copied to series with the same presentation key, their episodegroup.json sidecars are deleted and the old group cache is dropped.";
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref active, null);
                Detail = "Inactive: UpdateItem hook installation failed (" + error.GetType().Name + "); TmdbEg edits are native.";
                try { Remove(); } catch { Detail += " Owned detour cleanup pending; hook inactive."; }
                detail = Detail;
                return false;
            }
        }
    }

    internal sealed record EditState(Series Series, string? Old);

    private static void Before(BaseItem __1, out EditState? __state) =>
        __state = Volatile.Read(ref active) != null && __1 is Series series ? new EditState(series, series.GetProviderId(EpisodeGroupService.ProviderKey)) : null;

    private static void After(EditState? __state)
    {
        var state = Volatile.Read(ref active);
        if (state == null || __state == null) return;
        try { Apply(state.Library, state.Providers, state.Groups(), state.Logger, __state.Series, __state.Old); }
        catch (Exception error)
        {
            Interlocked.Increment(ref failures);
            try { state.Logger?.Warn("[StrmAssistantRebuild] TmdbEg edit follow-up failed: {0}", error.GetType().Name); } catch { }
        }
    }

    /// <summary>The original postfix after the editor applied the request to <paramref name="series"/>; returns false when TmdbEg did not change.</summary>
    internal static bool Apply(ILibraryManager library, IProviderManager providers, EpisodeGroupService? groups, ILogger? logger, Series series, string? old)
    {
        var value = series.GetProviderId(EpisodeGroupService.ProviderKey);
        if (string.Equals(old, value, StringComparison.OrdinalIgnoreCase)) return false;
        Interlocked.Increment(ref changes);
        // The edited series itself is saved by the native request after this method.
        series.ProviderIds?.Remove(EpisodeGroupService.LegacyProviderKey);
        var siblings = string.IsNullOrWhiteSpace(series.PresentationUniqueKey) ? new List<Series>() :
            library.GetItemList(new InternalItemsQuery { PresentationUniqueKey = series.PresentationUniqueKey, GroupByPresentationUniqueKey = false })
                .OfType<Series>().Where(s => s.InternalId != series.InternalId).DistinctBy(s => s.InternalId).ToList();
        var changed = new List<BaseItem>();
        foreach (var other in siblings)
        {
            if (string.IsNullOrEmpty(value)) other.ProviderIds?.Remove(EpisodeGroupService.ProviderKey);
            else other.SetProviderId(EpisodeGroupService.ProviderKey, value);
            other.ProviderIds?.Remove(EpisodeGroupService.LegacyProviderKey);
            changed.Add(other);
        }
        foreach (var each in siblings.Prepend(series)) DeleteSidecar(each, logger);
        if (changed.Count > 0)
        {
            library.UpdateItems(changed, null, ItemUpdateType.MetadataEdit, true, false, null, CancellationToken.None);
            Interlocked.Add(ref synced, changed.Count);
            foreach (var item in changed)
            {
                // Like the original, the savers run without blocking the editor request; failures are logged.
                try
                {
                    providers.SaveMetadata(item, ItemUpdateType.MetadataEdit)?.ContinueWith(t =>
                    { try { logger?.Debug("[StrmAssistantRebuild] TmdbEg sibling save failed: {0}", t.Exception?.GetBaseException().GetType().Name); } catch { } },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                catch (Exception error) { logger?.Debug("[StrmAssistantRebuild] TmdbEg sibling save failed: {0}", error.GetType().Name); }
            }
        }
        if (!string.IsNullOrWhiteSpace(old) && groups?.ClearCache(old) == true) Interlocked.Increment(ref cachesCleared);
        return true;
    }

    /// <summary>The original deletes the series' episodegroup.json whatever LocalEpisodeGroup is, so a stale group cannot win later.</summary>
    private static void DeleteSidecar(Series series, ILogger? logger)
    {
        try
        {
            var folder = series.ContainingFolderPath;
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder)) return;
            var path = Path.Combine(folder, EpisodeGroupService.SidecarName);
            if (!File.Exists(path) && new FileInfo(path).LinkTarget == null) return;
            File.Delete(path); // A symbolic link is removed itself, never its target.
            Interlocked.Increment(ref sidecarsDeleted);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { logger?.Debug("[StrmAssistantRebuild] episodegroup.json not deleted: {0}", error.GetType().Name); }
    }

    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try { Remove(); Detail = "Disabled; native metadata editor restored."; }
            catch { Detail = "Disabled; owned detour cleanup pending; hook inactive."; }
        }
    }
    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.All, Owner);
        installed = null;
    }
}
