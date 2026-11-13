using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Provider;

/// <summary>
/// M146/F3 (EnhanceMissingEpisodes): when a series has a TMDB episode group, the pinned MovieDb 2.0
/// MovieDbSeriesProvider.GetAllEpisodes list (used by Emby's missing/upcoming episode views) is replaced by the
/// group's numbering. Observed original contract (decompiled m_4bb7…/m_bcb9… and the group converter):
///   groups by order, episodes by order; IndexNumber = episode order + 1, ParentIndexNumber = group order;
///   when the first group's first episode has no name, native results overwrite name/overview/air date by aired
///   (season_number, episode_number); otherwise group text is used as is.
/// Not reproduced: the original's own TMDB series/season fetch in IProviderManager.GetAllEpisodes. Emby's native
/// MovieDb provider already lists missing episodes for TMDB-enabled libraries; only the group replacement is added.
/// The native method always runs (postfix), so its TMDB requests happen as without this feature.
/// </summary>
public static class MissingEpisodeGroupControl
{
    private const string Owner = "StrmAssistantRebuild.MissingEpisodeGroup.MovieDb2";
    private const string TypeName = "MovieDb.MovieDbSeriesProvider";
    private static readonly Version HostVersion = new(4, 10, 2, 0);
    private static readonly Version MovieDbVersion = new(2, 0, 0, 0);
    private static readonly Guid Mvid = new("bf190786-42b7-4923-aea6-1851feea4259");
    private sealed record State(Func<MetadataEnhanceOptions> Options, Func<EpisodeGroupService?> Groups, ILogger? Logger);
    private static readonly object Gate = new();
    private static State? active;
    private static Harmony? patcher;
    private static MethodInfo? installed;
    private static long replaced;
    /// <summary>Lifetime count of native lists replaced by an episode group.</summary>
    public static long ReplacedResults => Interlocked.Read(ref replaced);
    public static bool IsEnabled => Volatile.Read(ref active) != null;
    public static string Detail { get; private set; } = "Disabled; native MovieDb episode listing unchanged.";

    public static bool Configure(Version hostVersion, IEnumerable<IPlugin>? plugins, bool enabled, Func<MetadataEnhanceOptions> options,
        Func<EpisodeGroupService?> groups, ILogger? logger, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try
            {
                Remove();
                if (!enabled) { detail = Detail = "Disabled; native MovieDb episode listing restored."; return true; }
                var target = FindTarget(plugins);
                if (hostVersion != HostVersion || target == null || !Verify(target))
                { detail = Detail = "Inactive: exact MovieDb 2.0 GetAllEpisodes contract, MVID or IL differs; native listing retained."; return false; }
                if (Harmony.GetPatchInfo(target)?.Owners.Any(owner => owner != Owner) == true)
                { detail = Detail = "Inactive: foreign GetAllEpisodes patch; native listing retained."; return false; }
                installed = target; // Keep the removal handle even if installation throws partway through.
                (patcher ??= new Harmony(Owner)).Patch(target,
                    postfix: new HarmonyMethod(typeof(MissingEpisodeGroupControl).GetMethod(nameof(After), BindingFlags.Static | BindingFlags.NonPublic)!));
                Volatile.Write(ref active, new State(options, groups, logger));
                detail = Detail = "Enabled: series with a TMDB episode group (MovieDbEpisodeGroup on) list missing/upcoming episodes in group numbering; other series keep the native MovieDb list.";
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref active, null);
                Detail = "Inactive: GetAllEpisodes hook installation failed (" + error.GetType().Name + "); native listing retained.";
                try { Remove(); } catch { Detail += " Owned detour cleanup pending; hook inactive."; }
                detail = Detail;
                return false;
            }
        }
    }

    internal static MethodInfo? FindTarget(IEnumerable<IPlugin>? plugins)
    {
        // Only the already loaded MovieDb plugin instance; no disk search or assembly load.
        var assembly = (plugins ?? Array.Empty<IPlugin>()).Where(p => p != null).Select(p => p.GetType().Assembly)
            .FirstOrDefault(a => a.GetName().Name == "MovieDb");
        return assembly?.GetType(TypeName)?.GetMethod("GetAllEpisodes", BindingFlags.Public | BindingFlags.Instance, null,
            new[] { typeof(SeriesInfo), typeof(CancellationToken) }, null);
    }

    internal static bool Verify(MethodInfo method)
    {
        var assembly = method.Module.Assembly;
        if (assembly.GetName().Name != "MovieDb" || assembly.GetName().Version != MovieDbVersion || method.Module.ModuleVersionId != Mvid) return false;
        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        return method.DeclaringType?.FullName == TypeName && method.IsPublic && !method.IsStatic && method.IsVirtual && method.IsFinal &&
            method.ReturnType == typeof(Task<RemoteSearchResult[]>) && method.MetadataToken == 0x0600014C &&
            method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(SeriesInfo), typeof(CancellationToken) }) &&
            method.GetParameters().Select(p => p.Name).SequenceEqual(new[] { "searchInfo", "cancellationToken" }) &&
            body is { InitLocals: true, MaxStackSize: 2, LocalSignatureMetadataToken: 0x11000039 } && body.ExceptionHandlingClauses.Count == 0 &&
            il?.Length == 71 && Convert.ToHexString(SHA256.HashData(il)) == "B44EB8ABA3ECBEF9A1DE51663B478439A0D05459AC3113EA804DE6FF521DFB2C";
    }

    private static void After(SeriesInfo searchInfo, CancellationToken cancellationToken, ref Task<RemoteSearchResult[]> __result)
    {
        var state = Volatile.Read(ref active);
        if (state == null || __result == null || searchInfo == null) return;
        try
        {
            var settings = state.Options();
            var groups = state.Groups();
            var reference = searchInfo.GetProviderId(EpisodeGroupService.ProviderKey);
            var seriesTmdb = searchInfo.GetProviderId(MetadataProviders.Tmdb);
            if (groups == null || !settings.MovieDbEpisodeGroup || !EpisodeGroupService.HasGroupSource(searchInfo.Path, reference, settings, seriesTmdb)) return;
            __result = ReplaceAsync(__result, searchInfo.Path, reference, groups, state.Logger, cancellationToken, seriesTmdb);
        }
        catch (Exception error) { try { state.Logger?.Debug("[StrmAssistantRebuild] Missing-episode group skipped: {0}", error.GetType().Name); } catch { } }
    }

    internal static async Task<RemoteSearchResult[]> ReplaceAsync(Task<RemoteSearchResult[]> native, string? seriesPath, string? reference,
        EpisodeGroupService groups, ILogger? logger, CancellationToken token, string? seriesTmdbId = null)
    {
        EpisodeGroupDocument? document;
        try { document = await groups.LoadAsync(seriesPath, reference, token, seriesTmdbId).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { logger?.Debug("[StrmAssistantRebuild] Missing-episode group load failed: {0}", error.GetType().Name); document = null; }
        if (document == null || document.Episodes.Count == 0) return await native.ConfigureAwait(false);
        RemoteSearchResult[]? nativeResults = null;
        // Original: a failed native list still yields the group list.
        try { nativeResults = await native.ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { logger?.Debug("[StrmAssistantRebuild] Native episode list failed under group: {0}", error.GetType().Name); }
        var results = Build(document, nativeResults);
        Interlocked.Increment(ref replaced);
        return results;
    }

    internal static RemoteSearchResult[] Build(EpisodeGroupDocument document, RemoteSearchResult[]? native)
    {
        var firstGroup = document.Groups.Count == 0 ? (int?)null : document.Groups[0].Order;
        var first = firstGroup == null ? null : document.Episodes.FirstOrDefault(e => e.GroupSeason == firstGroup);
        var useGroupText = !string.IsNullOrEmpty(first?.Name);
        var aired = useGroupText || native == null ? new Dictionary<(int, int), RemoteSearchResult>() :
            native.Where(r => r?.ParentIndexNumber != null && r.IndexNumber != null)
                .GroupBy(r => (r.ParentIndexNumber!.Value, r.IndexNumber!.Value)).ToDictionary(g => g.Key, g => g.First());
        var provider = native?.FirstOrDefault(r => !string.IsNullOrEmpty(r?.SearchProviderName))?.SearchProviderName ?? "TheMovieDb";
        return document.Episodes.OrderBy(e => e.GroupSeason).ThenBy(e => e.GroupEpisode).Select(e =>
        {
            string? name = e.Name, overview = e.Overview;
            var date = e.AirDate;
            if (aired.TryGetValue((e.AiredSeason, e.AiredEpisode), out var match)) { name = match.Name; overview = match.Overview; date = match.PremiereDate; }
            var result = new RemoteSearchResult
            {
                SearchProviderName = provider, IndexNumber = e.GroupEpisode, ParentIndexNumber = e.GroupSeason,
                Name = name, Overview = overview, PremiereDate = date, ProductionYear = date?.Year,
                ProviderIds = new ProviderIdDictionary()
            };
            if (e.TmdbId != null) result.ProviderIds[MetadataProviders.Tmdb.ToString()] = e.TmdbId;
            return result;
        }).ToArray();
    }

    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try { Remove(); Detail = "Disabled; native MovieDb episode listing restored."; }
            catch { Detail = "Disabled; owned detour cleanup pending; hook inactive."; }
        }
    }
    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.Postfix, Owner);
        installed = null;
    }
}
