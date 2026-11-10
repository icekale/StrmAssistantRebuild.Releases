using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

/// <summary>One owner for People/version projections at public DTO and awaited playback return boundaries.</summary>
public static class UserExperienceGetterCompatibility
{
    private const string Owner = "StrmAssistantRebuild.UserExperience.People.4.10.2";
    private const string Implementation = "Emby.Server.Implementations.Dto.DtoService";
    private static readonly Guid ExpectedMvid = new("2009e43d-fa42-4e57-a902-2784a8306072");
    private static readonly object Gate = new();
    private static Harmony? patcher;
    private static readonly List<MethodInfo> installed = new();
    private sealed record Filter(bool NoImage, bool ActorOnly, bool NonChinese);
    private sealed record Projection(Filter? People, MultiVersionProjection.Settings? Versions, bool MissingMetadata, ILibraryManager? LibraryCounts, ILibraryManager? EpisodeTotals);
    private static Projection? projection;
    private static readonly ConditionalWeakTable<Task<List<MediaSourceInfo>>, object> PlaybackWrappers = new();
    private static readonly object PlaybackWrapperMarker = new();
    public static bool IsHidePersonEnabled => Volatile.Read(ref projection)?.People != null;
    public static bool IsMultiVersionEnabled => Volatile.Read(ref projection)?.Versions != null;
    public static bool IsMissingMetadataEnabled => Volatile.Read(ref projection)?.MissingMetadata == true;
    public static bool IsLibraryCountEnabled => Volatile.Read(ref projection)?.LibraryCounts != null;
    public static bool IsEpisodeTotalEnabled => Volatile.Read(ref projection)?.EpisodeTotals != null;
    public static string Detail { get; private set; } = "Disabled";

    /// <summary>Read-only single/batch factory validation, independent of HidePersonNoImage.</summary>
    public static bool Probe(Version hostVersion, out string detail)
    {
        try
        {
            if (hostVersion != new Version(4, 10, 2, 0)) { detail = "Host version differs from validated 4.10.2.0."; return false; }
            var targets = FindTargets();
            if (targets.Length != 2 || targets.Any(m => !VerifyTarget(m)))
            { detail = "Public DtoService single/batch signature/MVID/IL differs or implementation missing."; return false; }
            if (targets.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(owner => owner != Owner) == true))
            { detail = "Foreign patch owns DtoService factory."; return false; }
            detail = "People projection factory fingerprints matched; read-only probe.";
            return true;
        }
        catch (Exception ex) { detail = "People probe failed: " + ex.GetType().Name + ": " + ex.Message; return false; }
    }

    public static bool ProbeMultiVersion(Version hostVersion, out string detail)
    {
        if (!Probe(hostVersion, out detail)) return false;
        try
        {
            var producers = MultiVersionProjectionCompatibility.FindEntryTargets();
            var response = MultiVersionProjectionCompatibility.FindResponseTarget();
            if (producers.Length != 4 || producers.Any(m => !MultiVersionProjectionCompatibility.VerifyEntryTarget(m)) ||
                response == null || !MultiVersionProjectionCompatibility.VerifyResponseTarget(response))
            { detail = "Public playback producer/final response signature/MVID/IL differs or implementation missing."; return false; }
            if (producers.Append(response).Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(owner => owner != Owner) == true))
            { detail = "Foreign patch owns playback producer/final response."; return false; }
            detail = "Single/batch DTO plus four public playback entries/final response fingerprints matched; read-only probe.";
            return true;
        }
        catch (Exception error) { detail = "MultiVersion probe failed: " + error.GetType().Name + ": " + error.Message; return false; }
    }

    // Legacy UI-only callers update People without turning off the independently configured versions.
    public static bool Configure(Version hostVersion, UIFunctionOptions? options, ILogger logger, ILibraryManager? library = null)
    {
        lock (Gate) return ConfigureCore(hostVersion, options, projection?.Versions, logger, library);
    }

    public static bool Configure(Version hostVersion, ExperienceEnhanceOptions options, ILogger logger, ILibraryManager? library = null)
    {
        lock (Gate) return ConfigureCore(hostVersion, options.UIFunctionOptions, MultiVersionProjection.Capture(options), logger, library);
    }

    private static bool ConfigureCore(Version hostVersion, UIFunctionOptions? options, MultiVersionProjection.Settings? versions, ILogger logger, ILibraryManager? library)
    {
        try
        {
            Filter? people = null;
            var peopleUnavailable = false;
            if (options?.HidePersonNoImage == true)
            {
                var selected = UserPreferenceService.Selected(options.HidePersonPreference);
                people = new Filter(selected.Count == 0 || selected.Contains("NoImage") || selected.Contains("0"),
                    selected.Contains("ActorOnly") || selected.Contains("1"), selected.Contains("NonChinese") || selected.Contains("2"));
                if (!people.NoImage && !people.ActorOnly && !people.NonChinese)
                { people = null; peopleUnavailable = true; }
            }
            var missingMetadata = options?.BeautifyMissingMetadata == true;
            var libraryCounts = options?.ShowLibraryItemCount == true ? library : null;
            var episodeTotals = options?.ReplaceUnplayedWithTotal == true ? library : null;
            var current = Volatile.Read(ref projection);
            if (!peopleUnavailable && hostVersion == new Version(4, 10, 2, 0) && current != null &&
                current.People == people && current.Versions == versions && current.MissingMetadata == missingMetadata &&
                ReferenceEquals(current.LibraryCounts, libraryCounts) && ReferenceEquals(current.EpisodeTotals, episodeTotals) &&
                HasExpectedPatches(current))
            {
                Detail = $"Unchanged effective response projection: {installed.Count} exact owned hooks retained; People={people != null}; MultiVersion={versions != null}; MissingMetadata={missingMetadata}; LibraryCounts={libraryCounts != null}; EpisodeTotals={episodeTotals != null}.";
                return true;
            }
            // A cold repair or changed request is a new epoch even for legacy callers that pass
            // the old Settings reference back into this coordinator.
            Volatile.Write(ref projection, null);
            Remove();
            if (versions != null) versions = versions with { };
            if (people == null && versions == null && !missingMetadata && libraryCounts == null && episodeTotals == null)
            { Detail = peopleUnavailable ? "No recognized people filters selected." : "Disabled; native DTO and playback factory results restored."; return !peopleUnavailable; }
            if (hostVersion != new Version(4, 10, 2, 0)) { Detail = "Host differs from validated 4.10.2.0."; return false; }
            var targets = FindTargets();
            if (targets.Length != 2 || targets.Any(m => !VerifyTarget(m)))
            { Detail = "Public DtoService single/batch factory fingerprint differs or implementation is not loaded."; return false; }
            if (targets.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any() == true))
            { Detail = "A public DtoService factory already has another owner."; return false; }
            var playback = versions == null ? Array.Empty<MethodInfo>() : MultiVersionProjectionCompatibility.FindEntryTargets();
            var response = versions == null ? null : MultiVersionProjectionCompatibility.FindResponseTarget();
            var playbackUnavailable = versions != null && (playback.Length != 4 || playback.Any(m => !MultiVersionProjectionCompatibility.VerifyEntryTarget(m)) ||
                response == null || !MultiVersionProjectionCompatibility.VerifyResponseTarget(response) ||
                playback.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any() == true) || Harmony.GetPatchInfo(response)?.Owners.Any() == true);
            // Keep People working when the independent playback contract is unavailable. Never claim DTO-only versions as enabled.
            if (playbackUnavailable) versions = null;
            if (people != null || versions != null || missingMetadata || libraryCounts != null || episodeTotals != null)
                foreach (var target in targets) Install(target, target.ReturnType == typeof(BaseItemDto) ? nameof(ProjectOne) : nameof(ProjectMany));
            if (versions != null)
            {
                foreach (var entry in playback) Install(entry, nameof(ProjectPlayback));
                Install(response!, nameof(ProjectPlaybackResponse));
            }
            Volatile.Write(ref projection, new(people, versions, missingMetadata, libraryCounts, episodeTotals));
            Detail = $"Response projection: People={people != null}; MultiVersion={versions != null}; MissingEpisodeName={missingMetadata}; LibraryCounts={libraryCounts != null} (movies/tvshows/homevideos/musicvideos/books/music/audiobooks/collections/playlists, no ChildCount/SubViews); EpisodeTotals={episodeTotals != null} (visible Series/Season, Played preserved); shared public single+batch DTO owner" +
                (versions != null ? "; four fingerprint-locked public playback entries; nested task projection deduplicated." : ".") +
                (playbackUnavailable ? " MultiVersion inactive: playback signature/MVID/IL/owner validation failed (no DTO-only partial activation)." : "") +
                (peopleUnavailable ? " People inactive: no recognized filters selected." : "");
            return !playbackUnavailable && !peopleUnavailable;
        }
        catch (Exception error)
        {
            Volatile.Write(ref projection, null); Detail = "Response projection inactive: " + error.Message;
            try { Remove(); } catch (Exception cleanup) { Detail += "; cleanup pending: " + cleanup.Message; }
            return false;
        }
        finally { logger.Info("[StrmAssistantRebuild] {0}", Detail); }
    }

    private static bool HasExpectedPatches(Projection current)
    {
        if (current.People == null && current.Versions == null && !current.MissingMetadata && current.LibraryCounts == null && current.EpisodeTotals == null) return false;
        var targets = FindTargets();
        if (targets.Length != 2 || targets.Any(m => !VerifyTarget(m))) return false;
        if (current.Versions != null)
        {
            var playback = MultiVersionProjectionCompatibility.FindEntryTargets();
            var response = MultiVersionProjectionCompatibility.FindResponseTarget();
            if (playback.Length != 4 || playback.Any(m => !MultiVersionProjectionCompatibility.VerifyEntryTarget(m)) ||
                response == null || !MultiVersionProjectionCompatibility.VerifyResponseTarget(response)) return false;
            targets = targets.Concat(playback).Append(response).ToArray();
        }
        if (installed.Count != targets.Length || !targets.SequenceEqual(installed)) return false;
        return targets.All(target => IsExpectedPatch(target, Harmony.GetPatchInfo(target)));
    }

    private static bool IsExpectedPatch(MethodInfo target, Patches? info)
    {
        var name = target.ReturnType == typeof(BaseItemDto) ? nameof(ProjectOne) : target.ReturnType == typeof(BaseItemDto[]) ? nameof(ProjectMany) :
            target.ReturnType == typeof(Task<List<MediaSourceInfo>>) ? nameof(ProjectPlayback) : nameof(ProjectPlaybackResponse);
        var expected = typeof(UserExperienceGetterCompatibility).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        return info != null && !info.Owners.Any(owner => owner != Owner) && info.Postfixes.Count == 1 &&
            info.Postfixes[0].owner == Owner && info.Postfixes[0].PatchMethod == expected && info.Prefixes.Count == 0 &&
            info.Transpilers.Count == 0 && info.Finalizers.Count == 0 && info.InnerPrefixes.Count == 0 && info.InnerPostfixes.Count == 0;
    }

    private static void Install(MethodInfo target, string postfix)
    {
        installed.Add(target); // Retain the handle if a partial install throws.
        (patcher ??= new Harmony(Owner)).Patch(target,
            postfix: new HarmonyMethod(typeof(UserExperienceGetterCompatibility).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)!));
    }

    internal static MethodInfo[] FindTargets()
    {
        // Bind only the host's already-loaded implementation, never a private copy or an interface without IL.
        var type = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Emby.Server.Implementations")?.GetType(Implementation);
        if (type == null) return Array.Empty<MethodInfo>();
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name is nameof(IDtoService.GetBaseItemDto) or nameof(IDtoService.GetBaseItemDtos) && m.GetParameters().Length == 4).ToArray();
    }

    internal static bool VerifyTarget(MethodInfo method)
    {
        bool batch = method.Name == nameof(IDtoService.GetBaseItemDtos);
        if (!batch && method.Name != nameof(IDtoService.GetBaseItemDto)) return false;
        var type = method.DeclaringType; var assembly = type?.Assembly.GetName();
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        var parameters = new[] { batch ? typeof(BaseItem[]) : typeof(BaseItem), typeof(DtoOptions), typeof(User), typeof(CancellationToken) };
        return type?.FullName == Implementation && type.IsPublic && type.IsSealed && typeof(IDtoService).IsAssignableFrom(type) &&
            method.IsPublic && !method.IsStatic && !method.ContainsGenericParameters &&
            method.ReturnType == (batch ? typeof(BaseItemDto[]) : typeof(BaseItemDto)) &&
            method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters) &&
            assembly?.Name == "Emby.Server.Implementations" && assembly.Version == new Version(4, 10, 2, 0) &&
            method.Module.ModuleVersionId == ExpectedMvid && method.MetadataToken == (batch ? 0x06000F1D : 0x06000F1F) &&
            body is { MaxStackSize: 6, InitLocals: true } && body.ExceptionHandlingClauses.Count == 0 &&
            body.LocalSignatureMetadataToken == (batch ? 0x110003D2 : 0x110003D3) && il?.Length == (batch ? 221 : 141) &&
            Convert.ToHexString(SHA256.HashData(il)) == (batch
                ? "8D0F5CFCD2B93299A2A3124BC0FD33A5D8B8E5EA97538D9C3D4DE23EF10EBF6B"
                : "89A9DF2FE488D4BB6650843BE3599E7FB3B1F11BF13BCD7C74454E49A02510FF");
    }

    private static void ProjectOne(BaseItem __0, DtoOptions __1, User __2, CancellationToken __3, ref BaseItemDto __result) => __result = ProjectDto(__result, Volatile.Read(ref projection), __0, __1, __2, __3);
    private static void ProjectMany(BaseItem[] __0, DtoOptions __1, User __2, CancellationToken __3, ref BaseItemDto[] __result)
    {
        var current = Volatile.Read(ref projection);
        if (current == null || __result == null) return;
        var paired = __0 != null && __0.Length == __result.Length;
        __result = __result.Select((dto, index) => ProjectDto(dto, current, paired ? __0![index] : null, __1, __2, __3)).ToArray();
        if (!current.MissingMetadata || !paired || __0!.Length == 0 || __0.Length > 20000 ||
            __0[0]?.ExtraType != ExtraType.AdditionalPart) return;
        for (var i = 0; i < __0.Length; i++)
        {
            __3.ThrowIfCancellationRequested();
            var item = __0[i]; var dto = __result[i];
            // Do not infer missing identities or number unrelated Videos in a mixed response.
            if (item == null || item.GetType() != typeof(Video) || item.ExtraType != ExtraType.AdditionalPart ||
                item.InternalId <= 0 || dto?.Type != "Video" || dto.Id != item.GetClientId() || dto.Name != item.Name) return;
        }
        if (ReferenceEquals(current, Volatile.Read(ref projection))) __result = MissingMetadataProjection.ProjectAdditionalParts(__result, true);
    }
    private static BaseItemDto ProjectDto(BaseItemDto dto, Projection? current, BaseItem? item, DtoOptions? options, User? user, CancellationToken token)
    {
        if (current == null || dto == null) return dto!;
        // 4.10.2 UserViewsService strips BasicSyncInfo before this factory; requiring that field would
        // suppress the very home-library badge. Keep the explicit ChildCount/SubViews exclusions.
        if (current.LibraryCounts != null && item is CollectionFolder folder && dto.Type == "CollectionFolder" &&
            item.InternalId > 0 && dto.Id == item.GetClientId() && user != null && user.Policy?.IsDisabled == false &&
            options?.EnableUserData == true && dto.UserData != null &&
            !options.ContainsField(ItemFields.ChildCount) && !options.ContainsField(ItemFields.SubViews))
        {
            // Native home-video libraries contain both videos and photos, not just one media family.
            var countTypes = (folder.CollectionType ?? "").ToLowerInvariant() switch
            {
                "movies" => new[] { "Movie" }, "tvshows" => new[] { "Series" },
                "homevideos" => new[] { "Video", "Photo" }, "musicvideos" => new[] { "MusicVideo" },
                "books" => new[] { "Book" }, "music" or "audiobooks" => new[] { "MusicAlbum" },
                "boxsets" => new[] { "BoxSet" }, "playlists" => new[] { "Playlist" }, _ => null
            };
            if (countTypes != null)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var library = current.LibraryCounts;
                    if (library.FilterItemsToIdsForUser(new[] { item }, user, null, token).Contains(item.InternalId))
                    {
                        var query = new InternalItemsQuery(user)
                        {
                            Parent = item, Recursive = true, IncludeItemTypes = countTypes,
                            Limit = 0, EnableTotalRecordCount = true,
                            DtoOptions = new DtoOptions { EnableImages = false, EnableUserData = false, Fields = Array.Empty<ItemFields>() }
                        };
                        // Collection/playlist libraries have native virtual-root and share semantics.
                        // Let CollectionFolder normalize them; do not turn them into an unscoped metadata COUNT.
                        var count = (countTypes[0] is "BoxSet" or "Playlist" ? folder.GetItems(query, null, token) : library.GetItemsResult(query, token)).TotalRecordCount;
                        token.ThrowIfCancellationRequested();
                        if (ReferenceEquals(current, Volatile.Read(ref projection))) dto = ItemCountProjection.Project(dto, true, count)!;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* A failed visible-count query retains the original response; no invented zero. */ }
            }
        }
        if (current.EpisodeTotals != null && item is Folder episodes &&
            item is MediaBrowser.Controller.Entities.TV.Series or MediaBrowser.Controller.Entities.TV.Season && dto.Type == item.GetClientTypeName() &&
            item.InternalId > 0 && dto.Id == item.GetClientId() && user != null && user.Policy?.IsDisabled == false &&
            options?.EnableUserData == true && dto.UserData != null)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var library = current.EpisodeTotals;
                if (library.FilterItemsToIdsForUser(new[] { item }, user, null, token).Contains(item.InternalId))
                {
                    // A grouped Series query clears Parent and skips the ordinary user-root fallback.
                    // Bind visible physical roots explicitly; never let an empty/overflowed set widen it.
                    var views = library.GetUserRootFolder().GetItemList(new InternalItemsQuery(user)
                    {
                        Recursive = false, ForceOriginalFolders = true, IncludeHiddenUserViews = true,
                        Limit = 1025, EnableTotalRecordCount = false
                    }, token);
                    var roots = new Dictionary<long, BaseItem>();
                    var usable = views.Length <= 1024;
                    foreach (var view in views)
                    {
                        if (!usable) break;
                        token.ThrowIfCancellationRequested();
                        foreach (var root in view.GetItemsForAncestorIdsQuery(user, null, token))
                        {
                            if (root == null || root.InternalId <= 0) { usable = false; break; }
                            roots.TryAdd(root.InternalId, root);
                            if (roots.Count > 1024) { usable = false; break; }
                        }
                    }
                    if (usable && roots.Count > 0)
                    {
                        var total = episodes.GetItems(new InternalItemsQuery(user)
                        {
                            Recursive = true, IsFolder = false, Limit = 0, EnableTotalRecordCount = true, EnableAutoSort = false,
                            Ancestors = roots.Values.ToArray(),
                            DtoOptions = new DtoOptions { EnableImages = false, EnableUserData = false, Fields = Array.Empty<ItemFields>() }
                        }, token).TotalRecordCount;
                        token.ThrowIfCancellationRequested();
                        if (ReferenceEquals(current, Volatile.Read(ref projection))) dto = ItemCountProjection.ProjectEpisodes(dto, true, total)!;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Keep the native response if a visible-total witness is unavailable. */ }
        }
        if (current.MissingMetadata && item is MediaBrowser.Controller.Entities.TV.Episode && dto.Type == "Episode" &&
            item.InternalId > 0 && dto.Id == item.GetClientId() && dto.Name == item.Name && dto.IndexNumber == item.IndexNumber)
        {
            // Reuse only the exact item paired with this native authorized result. Do not request/export Path.
            try { dto = MissingMetadataProjection.Project(dto, true, item.FileNameWithoutExtension)!; }
            catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException) { /* Retain the native DTO when its basename is unavailable. */ }
        }
        var people = current.People;
        var versions = current.Versions;
        var filterPeople = people != null && dto.People != null && dto.Type is "Movie" or "Series" or "Season" or "Episode";
        if (!filterPeople && (versions == null || dto.MediaSources == null)) return dto;
        var copy = MultiVersionProjection.Clone(dto);
        if (filterPeople) copy.People = ProjectPeople(dto.People!, people!.NoImage, people.ActorOnly, people.NonChinese);
        if (versions != null && dto.MediaSources != null) copy.MediaSources = MultiVersionProjection.Project(dto.MediaSources, versions);
        return copy;
    }

    private static void ProjectPlayback(ref Task<List<MediaSourceInfo>> __result)
    {
        var current = Volatile.Read(ref projection)?.Versions;
        if (current == null || __result == null || PlaybackWrappers.TryGetValue(__result, out _)) return;
        if (__result.IsCompletedSuccessfully && __result.Result == null) return;
        var wrapped = AwaitPlayback(__result, current);
        // Nested public wrappers retain the first captured settings and create only one projection.
        // Weak keys survive OFF/reconfigure without retaining completed caller tasks indefinitely.
        if (!(wrapped.IsCompletedSuccessfully && wrapped.Result == null))
            PlaybackWrappers.GetValue(wrapped, _ => PlaybackWrapperMarker);
        __result = wrapped;
    }

    private static async Task<List<MediaSourceInfo>> AwaitPlayback(Task<List<MediaSourceInfo>> native, MultiVersionProjection.Settings current)
    {
        // Await the original task exactly once. Native exceptions/cancellation propagate unchanged;
        // no early result, blocking wait, private fields, source re-query, or permission bypass.
        var sources = await native.ConfigureAwait(false);
        return sources != null && ReferenceEquals(current, Volatile.Read(ref projection)?.Versions)
            ? MultiVersionProjection.Project(sources, current).ToList() : sources!;
    }

    private static void ProjectPlaybackResponse(ref Task<PlaybackInfoResponse> __result)
    {
        var current = Volatile.Read(ref projection)?.Versions;
        if (current != null && __result != null) __result = AwaitPlaybackResponse(__result, current);
    }

    private static async Task<PlaybackInfoResponse> AwaitPlaybackResponse(Task<PlaybackInfoResponse> native, MultiVersionProjection.Settings current)
    {
        var response = await native.ConfigureAwait(false);
        if (response?.MediaSources == null || response.ErrorCode.HasValue || !ReferenceEquals(current, Volatile.Read(ref projection)?.Versions)) return response!;
        var copy = MultiVersionProjection.Clone(response);
        copy.MediaSources = MultiVersionProjection.Project(response.MediaSources, current);
        return copy;
    }

    internal static BaseItemPerson[] ProjectPeople(BaseItemPerson[] source, bool noImage, bool actorOnly, bool nonChinese) =>
        source.Where(p => p != null && (!noImage || p.HasPrimaryImage) &&
            (!actorOnly || p.Type is PersonType.Actor or PersonType.GuestStar) &&
            (!nonChinese || HasHan(p.Name))).ToArray();

    private static bool HasHan(string? name) => name?.EnumerateRunes().Any(r => r.Value is >= 0x3400 and <= 0x4dbf or
        >= 0x4e00 and <= 0x9fff or >= 0xf900 and <= 0xfaff or >= 0x20000 and <= 0x323af) == true;

    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref projection, null);
            try { Remove(); Detail = "Disabled; native DTO and playback factory results restored."; }
            catch (Exception error) { Detail = "Projection disabled; detour cleanup pending: " + error.Message; }
        }
    }
    private static void Remove()
    {
        for (var i = installed.Count - 1; i >= 0; i--)
        {
            patcher?.Unpatch(installed[i], HarmonyPatchType.All, Owner);
            installed.RemoveAt(i);
        }
    }
}
