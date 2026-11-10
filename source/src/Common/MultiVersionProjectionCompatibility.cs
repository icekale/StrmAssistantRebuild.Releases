using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace StrmAssistantRebuild.Common;

/// <summary>Read-only validation for the native async source producer; patch ownership stays in the shared DTO coordinator.</summary>
internal static class MultiVersionProjectionCompatibility
{
    private const string Implementation = "Emby.Server.Implementations.Library.MediaSourceManager";
    internal static MethodInfo? FindTarget() => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => a.GetName().Name == "Emby.Server.Implementations")?.GetType(Implementation)?
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .SingleOrDefault(m => m.Name == nameof(IMediaSourceManager.GetPlayackMediaSources) && m.GetParameters().Length == 8);

    internal static MethodInfo[] FindEntryTargets() => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => a.GetName().Name == "Emby.Server.Implementations")?.GetType(Implementation)?
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.Name == nameof(IMediaSourceManager.GetPlayackMediaSources)).OrderBy(m => m.GetParameters().Length).ToArray() ?? Array.Empty<MethodInfo>();

    internal static bool VerifyEntryTarget(MethodInfo method)
    {
        var count = method.GetParameters().Length;
        if (count == 8) return VerifyTarget(method);
        var parameters = count switch
        {
            5 => new[] { typeof(BaseItem), typeof(User), typeof(bool), typeof(bool), typeof(CancellationToken) },
            6 => new[] { typeof(BaseItem), typeof(User), typeof(bool), typeof(bool), typeof(bool), typeof(CancellationToken) },
            7 => new[] { typeof(BaseItem), typeof(User), typeof(bool), typeof(string), typeof(bool), typeof(bool), typeof(CancellationToken) },
            _ => Array.Empty<Type>()
        };
        var type = method.DeclaringType;
        if (parameters.Length == 0 || type?.FullName != Implementation || !type.IsPublic || !type.IsSealed ||
            !typeof(IMediaSourceManager).IsAssignableFrom(type) || !method.IsPublic || method.IsStatic || method.ContainsGenericParameters ||
            method.Name != nameof(IMediaSourceManager.GetPlayackMediaSources) || method.ReturnType != typeof(Task<List<MediaSourceInfo>>) ||
            !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters) || method.GetCustomAttribute<AsyncStateMachineAttribute>() != null ||
            type.Assembly.GetName().Name != "Emby.Server.Implementations" || type.Assembly.GetName().Version != new Version(4, 10, 2, 0) ||
            method.Module.ModuleVersionId != new Guid("2009e43d-fa42-4e57-a902-2784a8306072")) return false;
        return count switch
        {
            5 => Match(method, 0x06000AC3, 15, 8, 0, 0, "357F5046D98E76790E0DB2785940BA40DADB3AFCDC7A4CED0B120A0ACEAE56EE", false),
            6 => Match(method, 0x06000AC4, 17, 8, 0, 0, "693303801F849A2FC5972EFB3B3839DB9DC7EBAC229418BE83472B2E0C69365E", false),
            7 => Match(method, 0x06000AC5, 19, 9, 0, 0, "9D3259D6B1D72487C05AD5BD03D66B8F222F2EAF37D59B747D3617ED0A4C7442"),
            _ => false
        };
    }

    internal static bool VerifyTarget(MethodInfo method)
    {
        var type = method.DeclaringType;
        var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        var moveNext = state?.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance);
        return type?.FullName == Implementation && type.IsPublic && type.IsSealed && typeof(IMediaSourceManager).IsAssignableFrom(type) &&
            method.Name == nameof(IMediaSourceManager.GetPlayackMediaSources) && method.IsPublic && !method.IsStatic && !method.ContainsGenericParameters &&
            method.ReturnType == typeof(Task<List<MediaSourceInfo>>) && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[]
            { typeof(BaseItem), typeof(User), typeof(bool), typeof(string), typeof(bool), typeof(bool), typeof(DeviceProfile), typeof(CancellationToken) }) &&
            type.Assembly.GetName().Name == "Emby.Server.Implementations" && type.Assembly.GetName().Version == new Version(4, 10, 2, 0) &&
            method.Module.ModuleVersionId == new Guid("2009e43d-fa42-4e57-a902-2784a8306072") &&
            Match(method, 0x06000AC6, 124, 2, 0x11000298, 0, "DBBCE77B33631AD204DB5875EF5D4DA90AF60C64B486E0AEEBDC9795EB060FA0") &&
            state?.DeclaringType == type && typeof(IAsyncStateMachine).IsAssignableFrom(state) && moveNext != null &&
            Match(moveNext, 0x060014A6, 1011, 8, 0x1100054F, 5, "D01F57F66689265E93B758F9486FF26A5A887F35F1D795627B0D6EA7BD0B6F93");
    }

    // Device-specific native sorting runs AFTER the manager. Project the final public response too,
    // including live-stream responses, rather than hooking the service's private helpers.
    internal static MethodInfo? FindResponseTarget() => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => a.GetName().Name == "Emby.Server.MediaEncoding")?.GetType("Emby.Server.MediaEncoding.Api.MediaInfoService")?
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).SingleOrDefault(m => m.Name == "GetPlaybackInfo" && m.GetParameters().Length == 4);

    internal static bool VerifyResponseTarget(MethodInfo method)
    {
        var type = method.DeclaringType; var parameters = method.GetParameters();
        var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        var moveNext = state?.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance);
        return type?.FullName == "Emby.Server.MediaEncoding.Api.MediaInfoService" && type.IsPublic && type.IsSealed &&
            method.IsPublic && !method.IsStatic && !method.ContainsGenericParameters && method.ReturnType == typeof(Task<PlaybackInfoResponse>) &&
            parameters.Length == 4 && parameters[0].ParameterType == type.Assembly.GetType("Emby.Server.MediaEncoding.Api.GetPostedPlaybackInfo") &&
            parameters.Skip(1).Select(p => p.ParameterType).SequenceEqual(new[] { typeof(bool), typeof(string), typeof(CancellationToken) }) &&
            type.Assembly.GetName().Name == "Emby.Server.MediaEncoding" && type.Assembly.GetName().Version == new Version(4, 10, 2, 0) &&
            method.Module.ModuleVersionId == new Guid("1a6398de-16b2-4e89-9e0c-2ed7d3859103") &&
            Match(method, 0x06000A5C, 88, 2, 0x1100020F, 0, "F441E2FBFBF823F2D628CF67F73E51F3473327E5E7C6386EA7AD31739CF89D5C") &&
            state?.DeclaringType == type && typeof(IAsyncStateMachine).IsAssignableFrom(state) && moveNext != null &&
            Match(moveNext, 0x06000F43, 3121, 18, 0x110002BF, 1, "3ABB52D05282222AFCDA0F77D78CADDC9D83DE4B9429E9428281D87BC3C38C70");
    }

    private static bool Match(MethodInfo method, int token, int length, int stack, int locals, int clauses, string hash, bool initLocals = true)
    {
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        return method.MetadataToken == token && body != null && body.InitLocals == initLocals && body.MaxStackSize == stack &&
            body.LocalSignatureMetadataToken == locals && body.ExceptionHandlingClauses.Count == clauses && il?.Length == length &&
            Convert.ToHexString(SHA256.HashData(il)) == hash;
    }
}
