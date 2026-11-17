using System.Reflection;
using System.Text.Json;

namespace StrmAssistantRebuild.Provider;

/// <summary>
/// M174: read-only access to the native TheMovieDb 2.0 detail DTO, for the two original ChineseMovieDb rules that read raw TMDB
/// fields the public item does not carry: series alternative titles (EnsureSeriesInfo postfix, A:53162-53472) and US/JP rating
/// fallbacks (ImportData/ProcessMainInfo postfixes, A:52575/A:50639). The rebuild runs after the host refresh, so it asks the
/// same native provider instance for the same detail again: <c>MovieDbSeriesProvider.EnsureSeriesInfo(string, string, CancellationToken)</c>
/// (0x06000151) and <c>MovieDbProvider.EnsureMovieInfo(string, bool, string, CancellationToken)</c> (0x06000103), pinned by the
/// MovieDb 2.0 module MVID and token. Native transport, its 6-hour HTTP cache and the M155 detail cache stay in charge of the
/// request; nothing is patched. The DTO is returned as JSON so callers read plain TMDB field names.
/// </summary>
public static class NativeTmdbDetails
{
    private static readonly Guid Mvid = new("bf190786-42b7-4923-aea6-1851feea4259");
    private const int SeriesToken = 0x06000151, MovieToken = 0x06000103;
    private static long reads, failures;
    public static long Reads => Interlocked.Read(ref reads);
    public static long Failures => Interlocked.Read(ref failures);

    /// <summary>The pinned native method for the provider instance, or null when the provider is not MovieDb 2.0.</summary>
    internal static MethodInfo? Bind(object? provider, string kind)
    {
        if (provider == null) return null;
        var type = provider.GetType();
        var (typeName, token, dto, parameters) = kind == "series"
            ? ("MovieDb.MovieDbSeriesProvider", SeriesToken, "MovieDb.MovieDbSeriesProvider+SeriesRootObject", new[] { typeof(string), typeof(string), typeof(CancellationToken) })
            : ("MovieDb.MovieDbProvider", MovieToken, "MovieDb.MovieDbProvider+CompleteMovieData", new[] { typeof(string), typeof(bool), typeof(string), typeof(CancellationToken) });
        if (type.FullName != typeName || type.Assembly.GetName().Name != "MovieDb" || type.Module.ModuleVersionId != Mvid) return null;
        MethodBase? method;
        try { method = type.Module.ResolveMethod(token); }
        catch (Exception) { return null; }
        return method is MethodInfo info && info.DeclaringType == type && !info.IsStatic && info.Name == (kind == "series" ? "EnsureSeriesInfo" : "EnsureMovieInfo") &&
            info.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters) && info.ReturnType.IsGenericType &&
            info.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) && info.ReturnType.GetGenericArguments()[0].FullName == dto ? info : null;
    }

    /// <summary>The native detail as JSON; null when the provider is not pinned MovieDb 2.0, the id is not numeric or native returns nothing.</summary>
    public static async Task<JsonElement?> FetchAsync(object? provider, string kind, string? tmdbId, string? language, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(tmdbId) || tmdbId.Length > 10 || !tmdbId.All(c => c is >= '0' and <= '9') || Bind(provider, kind) is not { } method) return null;
        try
        {
            var args = kind == "series" ? new object?[] { tmdbId, language, token } : new object?[] { tmdbId, true, language, token };
            var task = (Task)method.Invoke(provider, args)!;
            await task.ConfigureAwait(false);
            var dto = task.GetType().GetProperty("Result")!.GetValue(task);
            if (dto == null) return null;
            Interlocked.Increment(ref reads);
            return JsonSerializer.SerializeToElement(dto, dto.GetType());
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            // A synchronous native throw arrives wrapped by Invoke; callers see the native exception itself.
            if (!(error.InnerException is OperationCanceledException && token.IsCancellationRequested)) Interlocked.Increment(ref failures);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { Interlocked.Increment(ref failures); throw; }
    }

    internal static string? String(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static IEnumerable<JsonElement> Array(JsonElement e, params string[] path)
    {
        foreach (var name in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return System.Array.Empty<JsonElement>();
        }
        return e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToArray() : System.Array.Empty<JsonElement>();
    }

    /// <summary>languages[0] (string list), the original's series "original language".</summary>
    public static string? FirstLanguage(JsonElement series) =>
        series.ValueKind == JsonValueKind.Object && series.TryGetProperty("languages", out var list) && list.ValueKind == JsonValueKind.Array &&
        list.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.String } first ? first.GetString() : null;

    /// <summary>alternative_titles.results[] as (iso_3166_1, title) in TMDB order.</summary>
    public static IReadOnlyList<(string? Country, string? Title)> AlternativeTitles(JsonElement series) =>
        Array(series, "alternative_titles", "results").Select(t => (String(t, "iso_3166_1"), String(t, "title"))).ToArray();

    /// <summary>Series content_ratings.results[] (all entries) or movie releases.countries[] with a non-blank certification.</summary>
    public static IReadOnlyList<(string? Country, string? Rating)> Ratings(JsonElement dto, bool movie) => movie
        ? Array(dto, "releases", "countries").Select(c => (String(c, "iso_3166_1"), String(c, "certification"))).Where(c => !string.IsNullOrWhiteSpace(c.Item2)).ToArray()
        : Array(dto, "content_ratings", "results").Select(c => (String(c, "iso_3166_1"), String(c, "rating"))).ToArray();
}
