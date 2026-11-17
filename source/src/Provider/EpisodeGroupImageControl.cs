using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Provider;

/// <summary>
/// M173: grouped season and episode images. The original (notes/decrypted/A----------/A----------.cs:62313-62801, helper
/// 60405-60550) wraps the pinned MovieDb 2.0 MovieDbSeasonImageProvider/MovieDbEpisodeImageProvider.GetImages so TMDB is asked
/// for the aired coordinates of a grouped item:
///   season  - pairs (group order, aired season) over every group episode; among the pairs of this group the most frequent aired
///             season wins (ties: the lowest); the number is swapped only when the group season is above every aired season;
///   episode - the first group row with this (group season, group episode) swaps ParentIndexNumber/IndexNumber to the aired pair.
/// The group comes from the same sources as metadata (override file, series sidecar, TMDB with AltMovieDbApiKey), without a
/// language; series with a fileorder id are skipped. Native reads both numbers synchronously in the async stub before its first
/// await, so the numbers are swapped by the prefix and restored by the finalizer after the stub returns.
/// Differences: the original lets group load failures propagate into the native image request; the rebuild logs them and keeps
/// native numbering. The stub and both state machines are pinned by IL hash, so a changed read order cannot be patched silently.
/// </summary>
public static class EpisodeGroupImageControl
{
    private const string Owner = "StrmAssistantRebuild.EpisodeGroupImages.MovieDb2";
    private const string SeasonType = "MovieDb.MovieDbSeasonImageProvider";
    private const string EpisodeType = "MovieDb.MovieDbEpisodeImageProvider";
    private static readonly Version HostVersion = new(4, 10, 2, 0);
    private static readonly Version MovieDbVersion = new(2, 0, 0, 0);
    private static readonly Guid Mvid = new("bf190786-42b7-4923-aea6-1851feea4259");
    private sealed record State(Func<MetadataEnhanceOptions> Options, Func<EpisodeGroupService?> Groups, ILogger? Logger);
    /// <summary>Numbers to restore after the native stub returned.</summary>
    internal sealed record Swap(BaseItem Item, int? ParentIndexNumber, int? IndexNumber);
    private sealed record Pin(string Type, int Token, int Locals, string Hash, string StateMachine, int MoveNextToken, int MoveNextLength, string MoveNextHash);
    private static readonly Pin SeasonPin = new(SeasonType, 0x06000132, 0x11000030, "EB77B9BAD43C6DBCDF81AB9278DD5AEB1B174A5B8FDEB822B52BA69397BA0BBA",
        "<GetImages>d__4", 0x06000356, 609, "FC10C4D85C068EACDC71E051318BBFC8047462811D8EA3C043199CC05219BC82");
    private static readonly Pin EpisodePin = new(EpisodeType, 0x060000C9, 0x1100000D, "9FF70CE4C42741F523FDFAEEEA262815B0EAF25BCD21547315B73C8AC1036BB5",
        "<GetImages>d__2", 0x060001D5, 689, "07225CDB4C322C41C26D7DF1A2EB704936DB2436F280929FD91D9EE710924CCA");
    private static readonly object Gate = new();
    private static State? active;
    private static Harmony? patcher;
    private static readonly List<MethodInfo> installed = new();
    private static long seasonRemaps, episodeRemaps, failures;
    public static long SeasonRemaps => Interlocked.Read(ref seasonRemaps);
    public static long EpisodeRemaps => Interlocked.Read(ref episodeRemaps);
    public static long Failures => Interlocked.Read(ref failures);
    public static bool IsEnabled => Volatile.Read(ref active) != null;
    public static string Detail { get; private set; } = "Disabled; native MovieDb season/episode images unchanged.";

    public static bool Configure(Version hostVersion, IEnumerable<IPlugin>? plugins, bool enabled, Func<MetadataEnhanceOptions> options,
        Func<EpisodeGroupService?> groups, ILogger? logger, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try
            {
                Remove();
                if (!enabled) { detail = Detail = "Disabled; native MovieDb season/episode images restored."; return true; }
                var targets = FindTargets(plugins);
                if (hostVersion != HostVersion || targets == null || !Verify(targets.Value.Season, SeasonPin) || !Verify(targets.Value.Episode, EpisodePin))
                { detail = Detail = "Inactive: exact MovieDb 2.0 season/episode GetImages contract, MVID or IL differs; native images retained."; return false; }
                var methods = new[] { targets.Value.Season, targets.Value.Episode };
                if (methods.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(owner => owner != Owner) == true))
                { detail = Detail = "Inactive: foreign GetImages patch; native images retained."; return false; }
                patcher ??= new Harmony(Owner);
                var finalizer = new HarmonyMethod(typeof(EpisodeGroupImageControl).GetMethod(nameof(Restore), BindingFlags.Static | BindingFlags.NonPublic)!);
                foreach (var (method, prefix) in new[] { (targets.Value.Season, nameof(SeasonBefore)), (targets.Value.Episode, nameof(EpisodeBefore)) })
                {
                    installed.Add(method); // Keep the removal handle even if installation throws partway through.
                    patcher.Patch(method, prefix: new HarmonyMethod(typeof(EpisodeGroupImageControl).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)!),
                        finalizer: finalizer);
                }
                Volatile.Write(ref active, new State(options, groups, logger));
                detail = Detail = "Enabled: grouped seasons and episodes ask TheMovieDb for images by their aired season/episode (original rule); other items keep native numbering.";
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref active, null);
                Detail = "Inactive: GetImages hook installation failed (" + error.GetType().Name + "); native images retained.";
                try { Remove(); } catch { Detail += " Owned detour cleanup pending; hook inactive."; }
                detail = Detail;
                return false;
            }
        }
    }

    internal static (MethodInfo Season, MethodInfo Episode)? FindTargets(IEnumerable<IPlugin>? plugins)
    {
        // Only the already loaded MovieDb plugin instance; no disk search or assembly load.
        var assembly = (plugins ?? Array.Empty<IPlugin>()).Where(p => p != null).Select(p => p.GetType().Assembly)
            .FirstOrDefault(a => a.GetName().Name == "MovieDb");
        MethodInfo? Find(string type) => assembly?.GetType(type)?.GetMethod("GetImages", BindingFlags.Public | BindingFlags.Instance, null,
            new[] { typeof(RemoteImageFetchOptions), typeof(CancellationToken) }, null);
        var season = Find(SeasonType);
        var episode = Find(EpisodeType);
        return season == null || episode == null ? null : (season, episode);
    }

    internal static bool Verify(MethodInfo method, bool season) => Verify(method, season ? SeasonPin : EpisodePin);
    private static bool Verify(MethodInfo method, Pin pin)
    {
        var assembly = method.Module.Assembly;
        if (assembly.GetName().Name != "MovieDb" || assembly.GetName().Version != MovieDbVersion || method.Module.ModuleVersionId != Mvid) return false;
        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (method.DeclaringType?.FullName != pin.Type || !method.IsPublic || method.IsStatic || !method.IsVirtual || !method.IsFinal ||
            method.ReturnType != typeof(Task<IEnumerable<RemoteImageInfo>>) || method.MetadataToken != pin.Token ||
            !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(RemoteImageFetchOptions), typeof(CancellationToken) }) ||
            !method.GetParameters().Select(p => p.Name).SequenceEqual(new[] { "options", "cancellationToken" }) ||
            body is not { InitLocals: true, MaxStackSize: 2 } || body.LocalSignatureMetadataToken != pin.Locals || body.ExceptionHandlingClauses.Count != 0 ||
            il?.Length != 71 || Convert.ToHexString(SHA256.HashData(il)) != pin.Hash) return false;
        // The swap relies on the state machine reading the numbers before its first await: pin it as well.
        var machine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        var moveNext = machine?.Name == pin.StateMachine ? machine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic) : null;
        var moveNextIl = moveNext?.GetMethodBody()?.GetILAsByteArray();
        return moveNext?.MetadataToken == pin.MoveNextToken && moveNextIl?.Length == pin.MoveNextLength &&
            Convert.ToHexString(SHA256.HashData(moveNextIl)) == pin.MoveNextHash;
    }

    /// <summary>The loaded group of a series, or null when the series has no group source or loading failed.</summary>
    private static EpisodeGroupDocument? Group(State state, Series? series, CancellationToken token)
    {
        var settings = state.Options();
        var groups = state.Groups();
        if (series == null || groups == null || !settings.MovieDbEpisodeGroup || !EpisodeGroupService.HasGroupSource(series, settings)) return null;
        // Original: the group is loaded without a language (A:62380); the async load completes before native runs.
        var document = groups.LoadAsync(series.Path, EpisodeGroupService.Reference(series), token, series.GetProviderId(MetadataProviders.Tmdb), null)
            .GetAwaiter().GetResult();
        return document is { Episodes.Count: > 0 } ? document : null;
    }

    /// <summary>The aired season whose images a grouped season uses, or null to keep native numbering.</summary>
    internal static int? AiredSeason(EpisodeGroupDocument document, int groupSeason)
    {
        if (document.Episodes.Count == 0) return null;
        var best = document.Episodes.Where(e => e.GroupSeason == groupSeason).GroupBy(e => e.AiredSeason)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
        if (best == null) return null;
        // Original: only a group season beyond every aired season is remapped; lower numbers already exist on TMDB.
        return groupSeason > document.Episodes.Max(e => e.AiredSeason) ? best.Key : null;
    }

    /// <summary>The aired (season, episode) of a grouped episode, or null.</summary>
    internal static (int Season, int Episode)? AiredEpisode(EpisodeGroupDocument document, int groupSeason, int groupEpisode) =>
        document.Episodes.FirstOrDefault(e => e.GroupSeason == groupSeason && e.GroupEpisode == groupEpisode) is { } map ? (map.AiredSeason, map.AiredEpisode) : null;

    private static void SeasonBefore(RemoteImageFetchOptions options, CancellationToken cancellationToken, out Swap? __state)
    {
        __state = null;
        var state = Volatile.Read(ref active);
        if (state == null || options?.Item is not Season season || season.IndexNumber is not int index) return;
        try
        {
            var series = season.Series;
            if (series == null || EpisodeGroupService.FileOrdered(series)) return;
            var document = Group(state, series, cancellationToken);
            if (document == null || AiredSeason(document, index) is not int aired || aired == index) return;
            __state = new Swap(season, season.ParentIndexNumber, index);
            season.IndexNumber = aired;
            Interlocked.Increment(ref seasonRemaps);
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref failures);
            try { state.Logger?.Debug("[StrmAssistantRebuild] Grouped season image remap skipped: {0}", error.GetType().Name); } catch { }
        }
    }

    private static void EpisodeBefore(RemoteImageFetchOptions options, CancellationToken cancellationToken, out Swap? __state)
    {
        __state = null;
        var state = Volatile.Read(ref active);
        if (state == null || options?.Item is not Episode episode || episode.ParentIndexNumber is not int season || episode.IndexNumber is not int number) return;
        try
        {
            var series = episode.Series;
            if (series == null || EpisodeGroupService.FileOrdered(series)) return;
            var document = Group(state, series, cancellationToken);
            if (document == null || AiredEpisode(document, season, number) is not { } aired || aired == (season, number)) return;
            __state = new Swap(episode, season, number);
            episode.ParentIndexNumber = aired.Season;
            episode.IndexNumber = aired.Episode;
            Interlocked.Increment(ref episodeRemaps);
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref failures);
            try { state.Logger?.Debug("[StrmAssistantRebuild] Grouped episode image remap skipped: {0}", error.GetType().Name); } catch { }
        }
    }

    /// <summary>Runs after the stub returned (its first await) or threw: the item gets its group numbering back.</summary>
    private static void Restore(Swap? __state)
    {
        if (__state == null) return;
        __state.Item.ParentIndexNumber = __state.ParentIndexNumber;
        __state.Item.IndexNumber = __state.IndexNumber;
    }

    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try { Remove(); Detail = "Disabled; native MovieDb season/episode images restored."; }
            catch { Detail = "Disabled; owned detour cleanup pending; hook inactive."; }
        }
    }
    private static void Remove()
    {
        foreach (var method in installed.ToArray())
        {
            patcher?.Unpatch(method, HarmonyPatchType.Prefix, Owner);
            patcher?.Unpatch(method, HarmonyPatchType.Finalizer, Owner);
            installed.Remove(method);
        }
    }
}
