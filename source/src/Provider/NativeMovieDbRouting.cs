using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using StrmAssistantRebuild.Options;
using HttpRequestOptions = MediaBrowser.Common.Net.HttpRequestOptions;

namespace StrmAssistantRebuild.Provider;

/// <summary>Initial native TMDB request URLs and known image builders only. No transport replacement or redirect replay.</summary>
public static class NativeMovieDbRouting
{
    private const string Owner = "StrmAssistantRebuild.NativeMovieDbRouting.2.0";
    private const string ApiOrigin = "https://api.themoviedb.org";
    private const string ImageOrigin = "https://image.tmdb.org";
    private const string Hash = "759EDC7BF7A3B43A2BBDC34154471811F1931A7A7EF59DD25E9B00B4A6B5FC86";
    private static readonly Guid Mvid = new("bf190786-42b7-4923-aea6-1851feea4259");
    private static readonly object Gate = new();
    private sealed record State(Assembly Assembly, MovieDbEndpointRules.RouteSettings Settings, MethodInfo Key, MethodInfo[] Contracts);
    private static State? active;
    private static Harmony? patcher;
    private static readonly List<MethodInfo> installed = new();
    private static long apiChanges, imageChanges;
    public static bool IsEnabled => Volatile.Read(ref active) != null;
    public static long ChangedApiRequests => Interlocked.Read(ref apiChanges);
    public static long ChangedImageUrls => Interlocked.Read(ref imageChanges);
    public static string Detail { get; private set; } = "Inactive; native TMDB URLs retained.";
    private const string Scope = " Initial native API /3 and image /t/p URLs only; native redirects retained, no key/header reinjection or redirect replay. Blank key retains the request's existing native key.";

    public static bool ValidateOptions(MetadataEnhanceOptions options, out string detail) => MovieDbEndpointRules.ValidateOptions(options, out detail);

    public static bool Configure(Version hostVersion, IEnumerable<IPlugin>? plugins, MetadataEnhanceOptions options, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try
            {
                Remove();
                if (!MovieDbEndpointRules.Parse(options, out var settings, out detail)) { Detail = detail; return false; }
                if (!options.AltMovieDbConfig || settings is { Api: null, Image: null, Key.Length: 0 })
                { detail = Detail = "Inactive; native TMDB URLs retained." + Scope; return true; }
                var matches = (plugins ?? Array.Empty<IPlugin>()).Where(p => p.GetType().FullName == "MovieDb.Plugin" && p.GetType().Assembly.GetName().Name == "MovieDb" && p.Name == "TheMovieDb").ToArray();
                if (hostVersion != new Version(4, 10, 2, 0) || matches.Length != 1 || !VerifyArtifact(matches[0]))
                { detail = Detail = "Inactive: exact loaded TheMovieDb plugin / host / backing artifact differs."; return false; }
                var assembly = matches[0].GetType().Assembly;
                var type = assembly.GetType("MovieDb.MovieDbProviderBase", true)!;
                var api = type.GetMethod("GetMovieDbResponse", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var image = type.GetMethod("GetImageResponse", new[] { typeof(string), typeof(CancellationToken) })!;
                var key = type.GetProperty("Key")!.GetMethod!;
                var builders = assembly.GetType("MovieDb.TmdbImageSettings", true)!.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.Name.EndsWith("ImageUrl", StringComparison.Ordinal)).OrderBy(m => m.MetadataToken).ToArray();
                var move = api.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance);
                if (!VerifyMethods(api, image, key, move, builders))
                { detail = Detail = "Inactive: native TMDB request/key/image builder signatures or IL differ."; return false; }
                var contracts = new[] { api, image, key, move! }.Concat(builders).ToArray();
                var next = new State(assembly, settings, key, contracts);
                if (Foreign(next)) { detail = Detail = "Inactive: foreign TMDB routing contract patch owner."; return false; }
                if (settings.Api != null || settings.Key.Length != 0) Patch(api, nameof(BeforeApi));
                if (settings.Image != null)
                {
                    Patch(image, nameof(BeforeImage));
                    // Also patch complete builders: callers may have inlined the tiny shared GetImageUrl before activation.
                    foreach (var builder in builders) Patch(builder, nameof(AfterImage), postfix: true);
                }
                Volatile.Write(ref active, next);
                detail = Detail = "Configured pinned TheMovieDb native routing." + Scope;
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref active, null);
                Detail = "Inactive: native TMDB setup failed (" + error.GetType().Name + ").";
                try { Remove(); } catch { Detail += " Owned cleanup pending; callback inactive."; }
                detail = Detail;
                return false;
            }
        }
    }

    private static State? Current(object instance, bool provider)
    {
        var state = Volatile.Read(ref active);
        if (state == null || instance.GetType().Assembly != state.Assembly) return null;
        if (Foreign(state)) { Volatile.Write(ref active, null); Detail = "Inactive: native TMDB contract ownership changed."; return null; }
        return !provider || state.Key.Invoke(instance, null) as string == "Tmdb" ? state : null;
    }

    private static void BeforeApi(object __instance, HttpRequestOptions __0)
    {
        var state = Current(__instance, true);
        if (state == null || __0 == null || __0.CancellationToken.IsCancellationRequested) return;
        var changed = Rewrite(__0.Url, ApiOrigin, "/3/", state.Settings.Api, state.Settings.Key);
        if (changed == __0.Url) return;
        __0.Url = changed;
        // Native request logs may include URL, headers or a remote error body. Never disclose credentials through this feature.
        __0.LogRequest = __0.LogResponse = __0.LogErrors = __0.LogResponseHeaders = __0.LogErrorResponseBody = false;
        Interlocked.Increment(ref apiChanges);
    }

    private static void BeforeImage(object __instance, ref string __0, CancellationToken __1)
    {
        if (__1.IsCancellationRequested) return;
        var state = Current(__instance, true);
        if (state != null) MapImage(state, ref __0);
    }
    private static void AfterImage(object __instance, ref string __result)
    {
        var state = Current(__instance, false);
        if (state != null) MapImage(state, ref __result);
    }
    private static void MapImage(State state, ref string value)
    {
        var changed = Rewrite(value, ImageOrigin, "/t/p/", state.Settings.Image, "");
        if (changed == value) return;
        value = changed;
        Interlocked.Increment(ref imageChanges);
    }

    // Only called at the verified native context/builder. No substring rewrite of arbitrary HTTP requests.
    internal static string Rewrite(string value, string origin, string path, string? replacement, string key)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(origin + path, StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var original) || original.UserInfo.Length != 0 || original.GetLeftPart(UriPartial.Authority) != origin) return value;
        var tail = value[origin.Length..];
        var queryAt = tail.IndexOf('?'); var fragmentAt = tail.IndexOf('#');
        var pathEnd = new[] { queryAt, fragmentAt, tail.Length }.Where(n => n >= 0).Min();
        if (!MovieDbEndpointRules.CleanPath(tail[..pathEnd])) return value;
        // Preserve every original byte in path/query/fragment except the exact api_key query value.
        if (key.Length > 0 && queryAt >= 0 && (fragmentAt < 0 || queryAt < fragmentAt))
        {
            var end = fragmentAt < 0 ? tail.Length : fragmentAt;
            var parts = tail[(queryAt + 1)..end].Split('&');
            for (var i = 0; i < parts.Length; i++)
            {
                var equals = parts[i].IndexOf('=');
                var name = equals < 0 ? parts[i] : parts[i][..equals];
                if (Uri.UnescapeDataString(name) == "api_key") parts[i] = name + "=" + Uri.EscapeDataString(key);
            }
            tail = tail[..(queryAt + 1)] + string.Join("&", parts) + tail[end..];
        }
        var root = replacement ?? origin;
        var built = root + tail;
        // Credential-scope guard: concatenated data must stay beneath exactly the configured authority and path.
        if (!Uri.TryCreate(root + "/", UriKind.Absolute, out var target) || !Uri.TryCreate(built, UriKind.Absolute, out var result) ||
            result.UserInfo.Length != 0 || result.GetLeftPart(UriPartial.Authority) != target.GetLeftPart(UriPartial.Authority) ||
            !result.AbsolutePath.StartsWith(target.AbsolutePath.TrimEnd('/') + path, StringComparison.Ordinal)) return value;
        return built;
    }

    private static bool VerifyArtifact(IPlugin plugin)
    {
        var assembly = plugin.GetType().Assembly; var name = assembly.GetName();
        if (name.Name != "MovieDb" || name.Version != new Version(2, 0, 0, 0) || assembly.ManifestModule.ModuleVersionId != Mvid) return false;
        var path = string.IsNullOrEmpty(assembly.Location) ? plugin.AssemblyFilePath : assembly.Location;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        using var file = File.OpenRead(path);
        using (var pe = new PEReader(file, PEStreamOptions.LeaveOpen))
        {
            if (!pe.HasMetadata) return false;
            var metadata = pe.GetMetadataReader(); if (!metadata.IsAssembly) return false;
            var definition = metadata.GetAssemblyDefinition();
            if (metadata.GetString(definition.Name) != name.Name || definition.Version != name.Version || metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != Mvid) return false;
        }
        file.Position = 0;
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(file)) == Hash;
    }

    private static bool VerifyMethods(MethodInfo api, MethodInfo image, MethodInfo key, MethodInfo? move, MethodInfo[] builders)
    {
        if (api.ReturnType != typeof(Task<HttpResponseInfo>) || !api.IsAssembly || api.IsStatic ||
            !api.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(HttpRequestOptions) }) ||
            image.ReturnType != typeof(Task<HttpResponseInfo>) || !image.IsPublic || !image.IsFinal ||
            key.ReturnType != typeof(string) || !key.IsPublic || key.GetParameters().Length != 0 || builders.Length != 7 || move == null) return false;
        if (!Match(api, 0x0600011A, 63, "DB72E5A88550E52527FA386EFCCA930D1C305E69A5BBE9D673FA17FA6B40D422") ||
            !Match(image, 0x06000119, 38, "AC04C58BCD3B9607FA8E758475C61FECE2A0BC8DE83C167EB803DFCDC74DAA08") ||
            !Match(key, 0x06000108, 16, "60D824B8E64F61BDB009F17406BDFC22A92717B4131365DD74C0D74C2D1EDC9D") ||
            !Match(move, 0x060002F2, 468, "D41C4F2413A5D293FF506E001B3878D6A2465660EB0640CF62F7711BB99C9668")) return false;
        var hashes = new[] { "35BCCA3338AC418E4EC45AB048DD33BFF509EA67FB7FB22E01F77C8A09F8CE0C", "DC807012D9856F75C10ECA04CB010A6F6724256EFADFECDB3724D37752AEF652", "73B7ACE7B4ADA5614E514D3F198B892EDE81BE4FABD1B3E5057415215C26A35E", "BA457DE030FDAF867790046DD666CD56F32E4C084FEBD1855FA8FEF112732C66", "A15A3683B5A91DBD09E16FECE3F0E404903795FE3DEB8C05A2A3FFDB86CA6667", "8B8F474204BD138296E3B205E8C9A8815A23F0E9EBDFB9E3DFBE9688549FF1F7", "8BEEC9574220D9AC703BA923AD9192B6C10A6B60674C11353188EA36A925EEA7" };
        var lengths = new[] { 13, 18, 66, 8, 66, 98, 50 };
        return builders.Select((method, i) => method.ReturnType == typeof(string) && !method.IsStatic && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string) }) && Match(method, 0x06000185 + i, lengths[i], hashes[i])).All(ok => ok);
    }
    private static bool Match(MethodInfo method, int token, int length, string hash)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        return method.Module.ModuleVersionId == Mvid && method.MetadataToken == token && method.GetMethodImplementationFlags() == MethodImplAttributes.IL &&
            il?.Length == length && Convert.ToHexString(SHA256.HashData(il)) == hash;
    }
    private static bool Foreign(State state) => state.Contracts.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(o => o != Owner) == true);
    private static void Patch(MethodInfo method, string callback, bool postfix = false)
    {
        installed.Add(method);
        var hook = new HarmonyMethod(typeof(NativeMovieDbRouting).GetMethod(callback, BindingFlags.NonPublic | BindingFlags.Static)!);
        (patcher ??= new Harmony(Owner)).Patch(method, prefix: postfix ? null : hook, postfix: postfix ? hook : null);
    }
    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try { Remove(); Detail = "Inactive; native TMDB URLs retained." + Scope; }
            catch { Detail = "Inactive callback; owned TMDB detour cleanup pending."; }
        }
    }
    private static void Remove()
    {
        foreach (var method in installed) patcher?.Unpatch(method, HarmonyPatchType.All, Owner);
        installed.Clear();
    }
}
