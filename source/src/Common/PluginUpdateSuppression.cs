using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Updates;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Updates;

namespace StrmAssistantRebuild.Common;

/// <summary>Filters only the witnessed native scheduled-task query, never a manual catalog/install call.</summary>
public static class PluginUpdateSuppression
{
    private const string Owner = "StrmAssistantRebuild.PluginUpdateSuppression.4.10.2";
    private const string ManagerName = "Emby.Server.Implementations.Updates.InstallationManager";
    private const string TaskName = "Emby.Server.Implementations.ScheduledTasks.PluginUpdateTask";
    private static readonly Guid Mvid = new("2009e43d-fa42-4e57-a902-2784a8306072");
    private static readonly object Gate = new();
    private sealed record Scope(HashSet<string> Names, MethodInfo Query, MethodInfo Execute, MethodInfo QueryState, MethodInfo ExecuteState);
    private static Scope? scope;
    private static Harmony? patcher;
    private static MethodInfo? installed;
    private static int witnessed;
    public static bool IsEnabled => Volatile.Read(ref scope) != null && Volatile.Read(ref witnessed) != 0;
    public static string Detail { get; private set; } = "Disabled; native update queries unchanged.";

    public static bool Configure(Version hostVersion, string? suppressPluginUpdates, IEnumerable<IPlugin> plugins, ILogger logger)
    {
        lock (Gate)
        {
            Volatile.Write(ref scope, null);
            Volatile.Write(ref witnessed, 0);
            try
            {
                Remove();
                var names = Capture(suppressPluginUpdates, plugins);
                if (names.Count == 0) { Detail = "Disabled; native update queries restored."; return true; }
                if (hostVersion != new Version(4, 10, 2, 0))
                { Detail = "Inactive: host version differs from validated 4.10.2.0; native updates retained."; return false; }
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.Server.Implementations");
                var query = assembly?.GetType(ManagerName)?.GetMethod("GetAvailablePluginUpdates", new[] { typeof(Version), typeof(LogSeverity), typeof(CancellationToken) });
                var execute = assembly?.GetType(TaskName)?.GetMethod("Execute", new[] { typeof(TaskOptions), typeof(IProgress<double>), typeof(CancellationToken) });
                if (query == null || execute == null || !Verify(query, execute))
                { Detail = "Inactive: public query/task signature, version, MVID, IL or EH differs; native updates retained."; return false; }
                var current = new Scope(names, query, execute, StateMethod(query)!, StateMethod(execute)!);
                if (HasForeignOwner(current))
                { Detail = "Inactive: foreign query/task patch; native updates retained."; return false; }
                installed = query; // Preserve ownership handle if Harmony throws partway through installation.
                (patcher ??= new Harmony(Owner)).Patch(query, postfix: new HarmonyMethod(typeof(PluginUpdateSuppression).GetMethod(nameof(AfterQuery), BindingFlags.Static | BindingFlags.NonPublic)!));
                Volatile.Write(ref scope, current);
                Detail = "Armed, inactive until exact scheduled update query callsite is witnessed; manual queries/installations remain native.";
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref scope, null);
                Detail = "Inactive: installation failed (" + error.GetType().Name + "); native updates retained.";
                try { Remove(); } catch { Detail += " Owned detour cleanup pending; filtering disabled."; }
                return false;
            }
            finally { logger.Info("[StrmAssistantRebuild] {0}", Detail); }
        }
    }

    internal static HashSet<string> Capture(string? value, IEnumerable<IPlugin> plugins)
    {
        var names = new HashSet<string>((value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(s => new[] { s.Trim(), Normalize(s) }).Where(s => s.Length != 0), StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return names;
        // Expand only from the originally selected exact identity, not transitively via another plugin's alias.
        var selected = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
        {
            var aliases = new[] { plugin.Name?.Trim() ?? "", Normalize(plugin.AssemblyFilePath), plugin.GetType().Assembly.GetName().Name ?? "" };
            if (aliases.Any(selected.Contains)) foreach (var alias in aliases.Where(s => s.Length != 0)) names.Add(alias);
        }
        return names;
    }

    internal static string Normalize(string? value)
    {
        var name = (value ?? "").Trim().Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    internal static PackageVersionInfo[] Filter(PackageVersionInfo[] packages, HashSet<string> names)
    {
        var kept = packages.Where(p => p == null || !(names.Contains(p.name?.Trim() ?? "") || names.Contains(Normalize(p.targetFilename)))).ToArray();
        return kept.Length == packages.Length ? packages : kept;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AfterQuery(ref Task<PackageVersionInfo[]> __result)
    {
        var current = Volatile.Read(ref scope);
        if (current == null || __result == null) return;
        try
        {
            if (HasForeignOwner(current))
            {
                Volatile.Write(ref scope, null); Volatile.Write(ref witnessed, 0);
                Detail = "Inactive: foreign query/task patch detected; native updates retained.";
                return;
            }
            // Only this postfix -> generated public-query wrapper -> exact scheduled MoveNext is accepted.
            // No stack scanning, AsyncLocal, private field, log-level heuristic or shared native state.
            // ponytail: two stack frames per update query; unsupported JIT layouts remain native.
            var frames = new StackTrace(1, false);
            var caller = frames.GetFrame(1);
            if (caller?.GetMethod() != current.ExecuteState || caller.GetILOffset() is < 0x3c or > 0x59)
            {
                Detail = "Inactive for this query: no exact scheduled callsite witness (manual, unknown or JIT layout); native result retained.";
                Volatile.Write(ref witnessed, 0);
                return;
            }
            Volatile.Write(ref witnessed, 1);
            Detail = "Active for witnessed native scheduled update query; manual queries and all installations unchanged.";
            __result = AwaitQuery(__result, current);
        }
        catch
        {
            Volatile.Write(ref witnessed, 0);
            Detail = "Inactive for this query: callsite observation failed; native result retained.";
        }
    }

    private static async Task<PackageVersionInfo[]> AwaitQuery(Task<PackageVersionInfo[]> native, Scope current)
    {
        var packages = await native.ConfigureAwait(false); // Preserve native fault/cancellation; never turn either into success.
        try
        {
            return packages != null && ReferenceEquals(current, Volatile.Read(ref scope)) && !HasForeignOwner(current)
                ? Filter(packages, current.Names) : packages!;
        }
        catch
        {
            Volatile.Write(ref witnessed, 0);
            Detail = "Inactive: result validation failed; native result retained.";
            return packages!;
        }
    }

    private static bool HasForeignOwner(Scope current) => new[] { current.Query, current.Execute, current.QueryState, current.ExecuteState }
        .Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(owner => owner != Owner) == true);

    internal static MethodInfo? StateMethod(MethodInfo method) => method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
        .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static bool Verify(MethodInfo query, MethodInfo execute)
    {
        var qs = StateMethod(query); var es = StateMethod(execute);
        return PublicMethod(query, ManagerName, "GetAvailablePluginUpdates", typeof(Task<PackageVersionInfo[]>),
                   typeof(Version), typeof(LogSeverity), typeof(CancellationToken)) && typeof(IInstallationManager).IsAssignableFrom(query.DeclaringType) &&
            PublicMethod(execute, TaskName, "Execute", typeof(Task), typeof(TaskOptions), typeof(IProgress<double>), typeof(CancellationToken)) &&
            typeof(IScheduledTaskWithExecuteOptions).IsAssignableFrom(execute.DeclaringType) &&
            Match(query, 0x0600052A, 79, 2, 0x110000D5, "E076B03877AF3E2E25A58ADC31ADB891B4DCD110AE7512D5FB2AD72BF3F0AD2C", "") &&
            Match(execute, 0x060007B9, 79, 2, 0x110001C2, "A5EBF0EBF520CECB53D2A67202366BEDF21237D334996DEF453DB398ABB023E0", "") &&
            qs?.DeclaringType?.DeclaringType == query.DeclaringType && es?.DeclaringType?.DeclaringType == execute.DeclaringType &&
            typeof(IAsyncStateMachine).IsAssignableFrom(qs.DeclaringType) && typeof(IAsyncStateMachine).IsAssignableFrom(es.DeclaringType) &&
            Match(qs, 0x0600127F, 368, 4, 0x110004F5, "62DB4AD2D7C66E282F907E361CBC3EEEB915F27AE2B3A6124EE855ECF0E3E972", "0:14:294:308:32:System.Exception:0") &&
            Match(es, 0x0600136D, 767, 7, 0x11000521, "5E0A95F1766D336672EE08E5D8639ED055746EA8638DA62520BF384799554937",
                "0:57:140:197:21:System.OperationCanceledException:0|0:57:140:218:30:System.Exception:0|0:330:133:463:18:System.OperationCanceledException:0|0:330:133:481:42:MediaBrowser.Model.Net.HttpException:0|0:330:133:523:42:System.IO.IOException:0|0:14:694:708:32:System.Exception:0");
    }

    private static bool PublicMethod(MethodInfo method, string type, string name, Type returns, params Type[] parameters) =>
        method.DeclaringType is { IsPublic: true, IsSealed: true } && method.DeclaringType.FullName == type && method.IsPublic && !method.IsStatic &&
        !method.ContainsGenericParameters && method.Name == name && method.ReturnType == returns && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters);

    private static bool Match(MethodInfo method, int token, int length, int stack, int locals, string hash, string eh)
    {
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray(); var assembly = method.Module.Assembly.GetName();
        return assembly.Name == "Emby.Server.Implementations" && assembly.Version == new Version(4, 10, 2, 0) && method.Module.ModuleVersionId == Mvid &&
            method.MetadataToken == token && body is { InitLocals: true } && body.MaxStackSize == stack && body.LocalSignatureMetadataToken == locals &&
            il?.Length == length && Convert.ToHexString(SHA256.HashData(il)) == hash &&
            string.Join("|", body.ExceptionHandlingClauses.Select(e => $"{(int)e.Flags}:{e.TryOffset}:{e.TryLength}:{e.HandlerOffset}:{e.HandlerLength}:{(e.Flags == ExceptionHandlingClauseOptions.Clause ? e.CatchType?.FullName : "")}:{(e.Flags == ExceptionHandlingClauseOptions.Filter ? e.FilterOffset : 0)}")) == eh;
    }

    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref scope, null); Volatile.Write(ref witnessed, 0);
            try { Remove(); Detail = "Disabled; native update queries restored."; }
            catch { Detail = "Disabled; owned detour cleanup pending; filtering inactive."; }
        }
    }
    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.Postfix, Owner);
        installed = null;
    }
}
