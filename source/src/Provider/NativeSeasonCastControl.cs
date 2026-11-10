using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace StrmAssistantRebuild.Provider;

/// <summary>Populate the native Season metadata DTO before the host's normal Cast merge/save. No library writes or HTTP client.</summary>
public static class NativeSeasonCastControl
{
    private const string Owner = "StrmAssistantRebuild.NativeSeasonCastControl.4.10.2";
    private static readonly object Gate = new();
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly List<MethodInfo> Installed = new();
    private static Harmony? patcher;
    private static State? active;
    private sealed record State(Assembly Assembly, MethodInfo Get, MethodInfo Import, MethodInfo Move,
        PropertyInfo Credits, PropertyInfo Cast, PropertyInfo Number, PropertyInfo Order, PropertyInfo Name,
        PropertyInfo Role, PropertyInfo Id, PropertyInfo Profile, bool EpisodeGroupExtendedInfo);
    private sealed class Scope
    {
        public State State = null!;
        public object Provider = null!;
        public int Number;
        public CancellationToken Token;
        public Season? Item;
        public List<PersonInfo>? People;
        public bool Closed;
        public bool SuppressCapturedCast;
    }
    private sealed class Frame { public Scope? Previous, Query; public bool Detached; }
    public static bool IsEnabled => ReadState() != null;
    public static string Detail { get; private set; } = "Inactive; native season metadata retained.";

    public static bool Configure(Version hostVersion, IEnumerable<IPlugin>? plugins, bool enabled, out string detail, bool episodeGroupExtendedInfo = true)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try
            {
                Remove();
                if (!enabled) { detail = Detail = "Inactive; native season metadata retained."; return true; }
                var selected = (plugins ?? Array.Empty<IPlugin>()).Where(p => p.GetType().FullName == "MovieDb.Plugin" && p.Name == "TheMovieDb").ToArray();
                if (hostVersion != new Version(4, 10, 2, 0) || selected.Length != 1 ||
                    !Pin(selected[0].GetType().Assembly, "MovieDb", "2.0.0.0", "bf190786-42b7-4923-aea6-1851feea4259", "759EDC7BF7A3B43A2BBDC34154471811F1931A7A7EF59DD25E9B00B4A6B5FC86", selected[0].AssemblyFilePath) ||
                    !Pin(typeof(Season).Assembly, "MediaBrowser.Controller", "4.10.2.0", "58dae430-7295-47bb-a62f-b26bf0d89604", "EAA6B9EDEBB21FE3AF9C272DEF8C913D9EC6B177B552E1B789FEE15CB59289DA") ||
                    !Pin(typeof(PersonType).Assembly, "MediaBrowser.Model", "4.10.2.0", "3d56dcbb-6440-4e93-a3ad-05e588af05c2", "2B7DA29239811F8C035784DF7B3F48A1668E9537784B6524DDE43B3905572617"))
                    throw new InvalidDataException("Host or MovieDb artifact differs.");
                var assembly = selected[0].GetType().Assembly;
                var type = assembly.GetType("MovieDb.MovieDbSeasonProvider", true)!;
                var dto = assembly.GetType("MovieDb.MovieDbSeasonProvider+SeasonRootObject", true)!;
                var cast = assembly.GetType("MovieDb.TmdbCast", true)!;
                var get = type.GetMethod("GetMetadata", new[] { typeof(RemoteMetadataFetchOptions<SeasonInfo>), typeof(CancellationToken) })!;
                var import = type.GetMethod("ImportData", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
                var move = get.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
                if (get.ReturnType != typeof(Task<MetadataResult<Season>>) || import.ReturnType != typeof(void) ||
                    !import.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Season), dto, typeof(string), typeof(int), typeof(bool) }) ||
                    !MethodPin(get, 0x06000136, "A3CB96D6A6B0B3D5DF9EBA7240F3064CF279F852F0483465576B533F8C2EA8A6") ||
                    !MethodPin(import, 0x06000138, "379CB553038474141CEEDA879657F2C4A14FB042139B3BF0DE9D5BD9888322DB") ||
                    !MethodPin(move, 0x0600036F, "A35E60909BA1CEFA6314626B867DB8F3326825ACADAE762E9DB87EAC8E1981F5"))
                    throw new InvalidDataException("Season method contract differs.");
                var credits = dto.GetProperty("credits")!;
                var next = new State(assembly, get, import, move, credits, credits.PropertyType.GetProperty("cast")!, dto.GetProperty("season_number")!,
                    cast.GetProperty("order")!, cast.GetProperty("name")!, cast.GetProperty("character")!, cast.GetProperty("id")!, cast.GetProperty("profile_path")!, episodeGroupExtendedInfo);
                if (Foreign(next)) throw new InvalidDataException("Foreign season hook owner.");
                patcher ??= new Harmony(Owner);
                Installed.AddRange(new[] { get, import, move });
                patcher.Patch(get, prefix: Hook(nameof(Before)), postfix: Hook(nameof(After)), finalizer: Hook(nameof(Finish)));
                patcher.Patch(import, postfix: Hook(nameof(Capture)));
                // Recompile the exact caller, which may have inlined ImportData while OFF.
                patcher.Patch(move, prefix: Hook(nameof(Barrier)));
                Volatile.Write(ref active, next);
                detail = Detail = "Active: first-language season cast DTO; host retains Cast lock/merge/save. Nonblank episode-group IDs suppress captured cast only when ExtendedInfo=" + episodeGroupExtendedInfo + "; valid capture produces an explicit empty DTO, no mapped cast fetch.";
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref active, null);
                try { Remove(); } catch { /* callbacks stay inactive */ }
                detail = Detail = "Inactive: season cast setup " + error.GetType().Name + ".";
                return false;
            }
        }
    }
    private static HarmonyMethod Hook(string name) => new(typeof(NativeSeasonCastControl).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!);
    private static void Barrier() { }
    private static bool Foreign(State state) => new[] { state.Get, state.Import, state.Move }.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(owner => owner != Owner) == true);
    private static State? ReadState()
    {
        var state = Volatile.Read(ref active);
        if (state != null && Foreign(state))
        {
            Interlocked.CompareExchange(ref active, null, state);
            Detail = "Inactive: native season hook ownership changed.";
            return null;
        }
        return state;
    }
    private static void Before(object __instance, RemoteMetadataFetchOptions<SeasonInfo> __0, CancellationToken __1, out Frame __state)
    {
        __state = new Frame { Previous = Current.Value };
        Current.Value = null;
        var state = ReadState();
        var info = __0?.SearchInfo;
        if (state == null || __instance.GetType().Assembly != state.Assembly || __1.IsCancellationRequested ||
            info?.IndexNumber is not int number || number < 0 || info.SeriesProviderIds == null ||
            !info.SeriesProviderIds.TryGetValue("Tmdb", out var id) || !int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) || numeric <= 0) return;
        __state.Query = Current.Value = new Scope
        {
            State = state, Provider = __instance, Number = number, Token = __1,
            SuppressCapturedCast = state.EpisodeGroupExtendedInfo && info.SeriesProviderIds.TryGetValue("TmdbEpisodeGroup", out var group) && !string.IsNullOrWhiteSpace(group)
        };
    }
    private static void Detach(Frame? frame)
    {
        if (frame == null || frame.Detached) return;
        Current.Value = frame.Previous; frame.Previous = null; frame.Detached = true;
    }
    private static void After(ref Task<MetadataResult<Season>> __result, Frame __state)
    {
        Detach(__state);
        if (__state.Query != null) __result = Complete(__result, __state.Query);
    }
    private static Exception? Finish(Exception? __exception, Frame? __state)
    {
        Detach(__state);
        if (__exception != null && __state?.Query != null) __state.Query.Closed = true;
        return __exception;
    }
    private static void Capture(object __instance, Season __0, object __1, int __3, bool __4)
    {
        var scope = Current.Value;
        if (scope == null || !__4 || scope.Closed || scope.Token.IsCancellationRequested ||
            !ReferenceEquals(scope.Provider, __instance) || !ReferenceEquals(ReadState(), scope.State) || __3 != scope.Number) return;
        try
        {
            var state = scope.State;
            if ((int)state.Number.GetValue(__1)! != scope.Number || state.Credits.GetValue(__1) is not { } credits || state.Cast.GetValue(credits) is not IList cast || cast.Count > 20000) return;
            var rows = new List<(int Order, PersonInfo Person)>();
            foreach (var row in cast)
            {
                if (scope.Token.IsCancellationRequested || row == null) return;
                var name = (state.Name.GetValue(row) as string)?.Trim();
                var role = (state.Role.GetValue(row) as string)?.Trim();
                if (string.IsNullOrEmpty(name) || name.Length > 1024 || role?.Length > 4096) return; // malformed/oversize is unknown, not empty
                var person = new PersonInfo { Name = name, Role = role, Type = PersonType.Actor };
                var id = (int)state.Id.GetValue(row)!;
                if (id > 0) person.SetProviderId(MetadataProviders.Tmdb, id.ToString(CultureInfo.InvariantCulture));
                var profile = state.Profile.GetValue(row) as string;
                // Reconstructed TMDB original-image URL; no configuration/key lookup or image download.
                if (!string.IsNullOrWhiteSpace(profile))
                {
                    if (profile.Length > 256 || profile[0] != '/' || profile.Length < 2 ||
                        profile.Skip(1).Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.')) ||
                        profile.Contains("..", StringComparison.Ordinal)) return;
                    person.ImageUrl = "https://image.tmdb.org/t/p/original" + profile;
                }
                rows.Add(((int)state.Order.GetValue(row)!, person));
            }
            var result = new MetadataResult<Season>();
            foreach (var row in rows.OrderBy(row => row.Order)) result.AddPerson(row.Person); // host dedupe semantics
            scope.Item = __0;
            scope.People = result.People ?? new List<PersonInfo>();
        }
        catch (Exception) { scope.People = null; } // unsupported data never turns into a destructive empty list
    }
    private static async Task<MetadataResult<Season>> Complete(Task<MetadataResult<Season>> original, Scope scope)
    {
        try
        {
            var result = await original.ConfigureAwait(false); // preserve native fault/cancellation
            if (!scope.Token.IsCancellationRequested && ReferenceEquals(ReadState(), scope.State) && result?.HasMetadata == true &&
                ReferenceEquals(result.Item, scope.Item) && scope.People != null && !result.Item.IsLocked && !result.Item.IsFieldLocked(MetadataFields.Cast))
                // Source resets People before the ExtendedInfo/nonblank-group branch. Keep an explicit
                // empty DTO (not null/native), while unknown captures still leave the native result alone.
                result.People = scope.SuppressCapturedCast ? new List<PersonInfo>() : scope.People;
            return result!;
        }
        finally { scope.Closed = true; scope.People = null; scope.Item = null; }
    }
    private static bool Pin(Assembly assembly, string name, string version, string mvid, string hash, string? backing = null)
    {
        if (assembly.GetName().Name != name || assembly.GetName().Version?.ToString() != version || assembly.ManifestModule.ModuleVersionId != Guid.Parse(mvid)) return false;
        var path = string.IsNullOrEmpty(assembly.Location) ? backing : assembly.Location;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)) == hash;
    }
    private static bool MethodPin(MethodInfo method, int token, string hash) => method.MetadataToken == token && !method.IsStatic &&
        Convert.ToHexString(SHA256.HashData(method.GetMethodBody()!.GetILAsByteArray()!)) == hash;
    public static void Disable()
    {
        lock (Gate) { Volatile.Write(ref active, null); try { Remove(); Detail = "Inactive; native season metadata retained."; } catch { Detail = "Inactive callback; owned cleanup pending."; } }
    }
    private static void Remove() { foreach (var method in Installed) patcher?.Unpatch(method, HarmonyPatchType.All, Owner); Installed.Clear(); }
}
