using System.Collections;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using StrmAssistantRebuild.ScheduledTask;

namespace StrmAssistantRebuild.Common;

/// <summary>Explicit native refresh opt-in. Never changes native refresh options or owns its metadata Task.
/// NativeCompletionPathReached is the host's best-effort completion branch, not proof that all child providers succeeded.</summary>
public static class IntroRefreshBridge
{
    private const string PatchOwner = "StrmAssistantRebuild.IntroRefresh.4.10.2";
    private const int Capacity = 16;
    private static readonly object ConfigureGate = new();
    private static readonly ConditionalWeakTable<MetadataRefreshOptions, Ticket> Bindings = new();
    private static readonly Harmony Patcher = new(PatchOwner);
    private static Coordinator? current;
    private static MethodInfo[] targets = Array.Empty<MethodInfo>();
    private static MethodInfo[] observed = Array.Empty<MethodInfo>();
    private static PropertyInfo? requestId;
    private static FieldInfo? queueLock, nativeQueue, disposed, queuedId, queuedDisabled, queuedOptions;
    private static PropertyInfo? queueCount, unordered;
    private static FieldInfo? tupleElement;
    public static bool IsEnabled => Volatile.Read(ref current)?.Accepting == true && Healthy();
    public static string Detail { get; private set; } = "Disabled; native refresh unchanged.";
    public sealed record RefreshReport(Guid OperationId, long TargetId, long ActorId, string State, string Reason,
        bool NativeCompletionPathReached, double Progress, int Pending, IntroRebuildReport? Rebuild);
    private static RefreshReport? lastReport;
    public static RefreshReport? LastReport => Volatile.Read(ref lastReport);
    public static int PendingCount => Volatile.Read(ref current)?.Count ?? 0;

    public static bool Configure(Version version, bool enabled, Plugin owner, ILibraryManager library,
        IUserManager users, ITaskManager tasks, ILogger logger, out string detail)
    {
        lock (ConfigureGate)
        {
            Disable();
            try
            {
                if (version != new Version(4, 10, 2, 0)) throw new NotSupportedException("Host version differs from the native refresh contract.");
                var api = AssemblyPin("Emby.Api", "0873fdaf-f3f8-45f4-8d9c-b647b1edf372", "2D5F2B4CB92DE964B478A3C50787515AEFB750C0041BC019E6BCCDFDF231AD4C");
                var providers = AssemblyPin("Emby.Providers", "29b37ec2-5f02-443c-a995-1f61c71f43c6", "16874EA37E2EC8152E7F2FA9F9D7FA51A284959DCE3A918694003D009B2B2A94");
                AssemblyPin("MediaBrowser.Controller", "58dae430-7295-47bb-a62f-b26bf0d89604", "EAA6B9EDEBB21FE3AF9C272DEF8C913D9EC6B177B552E1B789FEE15CB59289DA");
                AssemblyPin("MediaBrowser.Model", "3d56dcbb-6440-4e93-a3ad-05e588af05c2", "2B7DA29239811F8C035784DF7B3F48A1668E9537784B6524DDE43B3905572617");
                var server = AssemblyPin("Emby.Server.Implementations", "2009e43d-fa42-4e57-a902-2784a8306072", "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F");
                var apiType = api.GetType("Emby.Api.ItemRefreshService", true)!;
                var dto = api.GetType("Emby.Api.RefreshItem", true)!;
                var provider = providers.GetType("Emby.Providers.Manager.ProviderManager", true)!;
                requestId = dto.GetProperty("Id") ?? throw new MissingMemberException("Native RefreshItem.Id");
                if (requestId.PropertyType != typeof(string)) throw new NotSupportedException("Native request ID shape changed.");
                targets = new[] {
                    Method(apiType, "GetRefreshOptions", false, typeof(MetadataRefreshOptions), 0x060000c3, dto),
                    Method(provider, "QueueRefresh", true, typeof(void), 0x06000284, typeof(long), typeof(MetadataRefreshOptions), typeof(RefreshPriority), typeof(bool)),
                    Method(provider, "RefreshItem", false, typeof(Task), 0x0600028e, typeof(BaseItem), typeof(MetadataRefreshOptions), typeof(CancellationToken)),
                    Method(provider, "LogRefreshItemComplete", false, typeof(void), 0x0600028d, typeof(BaseItem), typeof(MetadataRefreshOptions)) };
                observed = new[] {
                    Method(apiType, "Post", true, typeof(void), 0x060000c1, dto),
                    Method(apiType, "QueueRefresh", false, typeof(void), 0x060000c2, typeof(long), dto),
                    Method(provider, "QueueRefresh", true, typeof(void), 0x06000283, typeof(long), typeof(MetadataRefreshOptions), typeof(RefreshPriority)),
                    providers.ManifestModule.ResolveMethod(0x060004b5) as MethodInfo ?? throw new MissingMethodException("Native root state machine"),
                    providers.ManifestModule.ResolveMethod(0x060004bd) as MethodInfo ?? throw new MissingMethodException("Native queue state machine"),
                    Method(server.GetType("Emby.Server.Implementations.ScheduledTasks.TaskManager", true)!, "Execute", true, typeof(Task), 0x06000844, typeof(IScheduledTaskWorker), typeof(TaskOptions)),
                    Method(server.GetType("Emby.Server.Implementations.ScheduledTasks.ScheduledTaskWorker", true)!, "Execute", true, typeof(Task), 0x060007e9, typeof(TaskOptions)),
                    Method(server.GetType("Emby.Server.Implementations.ScheduledTasks.ScheduledTaskWorker", true)!, "ExecuteInternal", false, typeof(Task), 0x060007ea, typeof(TaskOptions)),
                    server.ManifestModule.ResolveMethod(0x06000e3b) as MethodInfo ?? throw new MissingMethodException("Native authorization helper"),
                    server.ManifestModule.ResolveMethod(0x06000e3c) as MethodInfo ?? throw new MissingMethodException("Native request authorization helper"),
                    server.ManifestModule.ResolveMethod(0x06000e3e) as MethodInfo ?? throw new MissingMethodException("Native authorization resolution"),
                    AsyncTransfer(server, 0x06001376, "<Execute>d__67", "MoveNext", typeof(void), "9B6D5544E16B7245BC858558B98F6FD014A6B872CD1C6104447A009F03AE64EB"),
                    AsyncTransfer(server, 0x06001378, "<ExecuteInternal>d__68", "MoveNext", typeof(void), "7C1B0522902F74D88F1E6F983A28D86AA14B67A3C39CC909C326A378DA2327BB"),
                    AsyncTransfer(server, 0x06001375, "<>c__DisplayClass67_0", "<Execute>b__0", typeof(Task), "7EAF5A4D18EAFA5738343B6FB7278DE1844490D91B5D031C6E71680ABDE7862D"),
                    AsyncTransfer(server, 0x06001757, "<<Execute>b__0>d", "MoveNext", typeof(void), "2AA98E402E4A7458B02F588E0E7FDE7B9F6AA9BAE71043BD107028109FA6C16C") };
                if (targets.Concat(observed).Any(m => !Empty(Harmony.GetPatchInfo(m)))) throw new NotSupportedException("Foreign native refresh hooks detected.");
                if (tasks.GetType() != server.GetType("Emby.Server.Implementations.ScheduledTasks.TaskManager", true)) throw new NotSupportedException("Native task manager identity differs.");
                var worker = tasks.ScheduledTasks.Single(w => w.ScheduledTask.GetType() == typeof(ExtractIntroFingerprintTask));
                if (worker.GetType() != server.GetType("Emby.Server.Implementations.ScheduledTasks.ScheduledTaskWorker", true)) throw new NotSupportedException("Native task worker identity differs.");
                QueueFields(provider, providers.GetType("Emby.Providers.Manager.RefreshQueueItem", true)!);
                Patcher.Patch(targets[0], postfix: Hook(nameof(AfterOptions)));
                Patcher.Patch(targets[1], prefix: Hook(nameof(BeforeQueue)), finalizer: Hook(nameof(AfterQueue)));
                Patcher.Patch(targets[2], postfix: Hook(nameof(AfterRefresh)));
                Patcher.Patch(targets[3], postfix: Hook(nameof(AfterNativeComplete)));
                if (!Healthy()) throw new NotSupportedException("Native refresh hook ownership differs after installation.");
                var coordinator = new Coordinator(owner, library, users, tasks, worker, logger, enabled);
                Volatile.Write(ref current, coordinator);
                coordinator.Start();
                Detail = enabled ? "Native POST opt-in; exact options, NativeCompletionPathReached + settled root Task, bounded 16-request FIFO, existing intro worker. Child-provider success is not asserted."
                    : "Intro refresh rebuild disabled; explicit true requests report unsupported, false/unset/malformed remain native.";
                detail = Detail; return enabled;
            }
            catch (Exception ex)
            {
                try { Disable(); } catch { /* Keep exact installed handles for the next cleanup attempt. */ }
                Detail = "Intro refresh bridge unsupported; " + (targets.Length == 0 ? "no interception: " : "hook cleanup pending: ") + ex.GetBaseException().Message;
                detail = Detail;
                try { logger.Warn("[StrmAssistantRebuild] {0}", Detail); } catch { }
                return false;
            }
        }
    }

    public static async Task StopTrackingAsync()
    {
        Coordinator? prior = null;
        try
        {
            lock (ConfigureGate) { prior = Interlocked.Exchange(ref current, null); prior?.Stop(); RemovePatches(); Detail = "Disabled; native refresh unchanged."; }
        }
        finally { if (prior != null) await prior.JoinAsync().ConfigureAwait(false); }
    }
    public static void Disable() => StopTrackingAsync().GetAwaiter().GetResult();
    private static void RemovePatches()
    {
        foreach (var method in targets) Patcher.Unpatch(method, HarmonyPatchType.All, PatchOwner);
        targets = Array.Empty<MethodInfo>(); observed = Array.Empty<MethodInfo>();
    }
    private static HarmonyMethod Hook(string name) => new(typeof(IntroRefreshBridge), name);
    private static MethodInfo Method(Type type, string name, bool isPublic, Type result, int token, params Type[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, args, null);
        if (method == null || method.IsPublic != isPublic || method.IsStatic || method.ReturnType != result || method.MetadataToken != token || method.GetMethodBody() == null)
            throw new NotSupportedException("Native method differs: " + type.FullName + "." + name);
        return method;
    }
    private static MethodInfo AsyncTransfer(Assembly assembly, int token, string declaring, string name, Type result, string sha)
    {
        var method = assembly.ManifestModule.ResolveMethod(token) as MethodInfo;
        var code = method?.GetMethodBody()?.GetILAsByteArray();
        if (method == null || method.DeclaringType?.Name != declaring || method.Name != name || method.IsStatic ||
            method.ReturnType != result || method.GetParameters().Length != 0 || code == null || Convert.ToHexString(SHA256.HashData(code)) != sha)
            throw new NotSupportedException("Native task options transfer differs: " + declaring + "." + name);
        return method;
    }
    private static Assembly AssemblyPin(string name, string mvid, string sha)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == name);
        if (assembly.GetName().Version != new Version(4, 10, 2, 0) || assembly.ManifestModule.ModuleVersionId != Guid.Parse(mvid))
            throw new NotSupportedException("Native assembly identity differs: " + name);
        using var file = File.OpenRead(assembly.Location);
        using var hash = SHA256.Create();
        if (Convert.ToHexString(hash.ComputeHash(file)) != sha) throw new NotSupportedException("Native assembly bytes differ: " + name);
        return assembly;
    }
    private static bool Empty(Patches? p) => p == null || p.Prefixes.Count + p.Postfixes.Count + p.Transpilers.Count + p.Finalizers.Count + p.InnerPrefixes.Count + p.InnerPostfixes.Count == 0;
    private static bool Exact(IEnumerable<Patch> patches, string? name)
    {
        var values = patches.ToArray();
        return name == null ? values.Length == 0 : values.Length == 1 && values[0].owner == PatchOwner && values[0].PatchMethod == typeof(IntroRefreshBridge).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            && values[0].priority == Priority.Normal && values[0].before.Length == 0 && values[0].after.Length == 0 && !values[0].debug;
    }
    private static bool Healthy()
    {
        if (targets.Length != 4 || observed.Any(m => !Empty(Harmony.GetPatchInfo(m)))) return false;
        for (var n = 0; n < targets.Length; n++)
        {
            var p = Harmony.GetPatchInfo(targets[n]);
            if (p == null || !Exact(p.Prefixes, n == 1 ? nameof(BeforeQueue) : null) || !Exact(p.Finalizers, n == 1 ? nameof(AfterQueue) : null) ||
                !Exact(p.Postfixes, n switch { 0 => nameof(AfterOptions), 2 => nameof(AfterRefresh), 3 => nameof(AfterNativeComplete), _ => null }) ||
                p.Transpilers.Count + p.InnerPrefixes.Count + p.InnerPostfixes.Count != 0) return false;
        }
        return true;
    }
    private static void QueueFields(Type provider, Type entry)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        queueLock = provider.GetField("_refreshQueueLock", fields);
        nativeQueue = provider.GetField("_refreshQueue", fields);
        disposed = provider.GetField("_disposed", fields);
        queuedId = entry.GetField("ItemId"); queuedDisabled = entry.GetField("Disabled"); queuedOptions = entry.GetField("RefreshOptions");
        if (queueLock?.FieldType != typeof(object) || disposed?.FieldType != typeof(bool) || nativeQueue == null ||
            nativeQueue.FieldType != typeof(PriorityQueue<,>).MakeGenericType(entry, typeof(int)) || queuedId?.FieldType != typeof(long) || queuedDisabled?.FieldType != typeof(bool) ||
            queuedOptions?.FieldType != typeof(MetadataRefreshOptions) || queuedId.MetadataToken != 0x04000100 || queuedOptions.MetadataToken != 0x04000101 || queuedDisabled.MetadataToken != 0x04000102)
            throw new NotSupportedException("Native refresh queue field contract differs.");
        queueCount = nativeQueue.FieldType.GetProperty("Count"); unordered = nativeQueue.FieldType.GetProperty("UnorderedItems");
        tupleElement = typeof(ValueTuple<,>).MakeGenericType(entry, typeof(int)).GetField("Item1");
        if (queueCount?.PropertyType != typeof(int) || unordered == null || tupleElement == null) throw new NotSupportedException("Native refresh queue view differs.");
    }
    internal static bool Requested(IRequest request) => string.Equals(request.Verb, "POST", StringComparison.OrdinalIgnoreCase) &&
        bool.TryParse(request.QueryString["ReplaceIntroMarkers"], out var value) && value;
    // The pinned listener maps exception TYPE, not Model.Net.HttpException.StatusCode.
    private static Exception Error(HttpStatusCode code, string reason) => code switch
    {
        HttpStatusCode.BadRequest => new ArgumentException(reason),
        HttpStatusCode.Forbidden => new MediaBrowser.Controller.Net.SecurityException(reason, MediaBrowser.Controller.Net.SecurityExceptionType.Generic),
        HttpStatusCode.Conflict => new MediaBrowser.Common.Extensions.ConflictException(reason),
        HttpStatusCode.ServiceUnavailable => new MediaBrowser.Common.Extensions.ServiceUnavailableException(reason),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unmapped native HTTP error status.")
    };

    private static void AfterOptions(BaseApiService __instance, object __0, MetadataRefreshOptions __result)
    {
        if (!Requested(__instance.Request)) return;
        if (!__result.Recursive) throw Error(HttpStatusCode.ServiceUnavailable, "Native root recursive refresh contract changed.");
        var state = Volatile.Read(ref current);
        if (state?.Accepting != true || !Healthy()) throw Error(HttpStatusCode.ServiceUnavailable, "Intro rebuild refresh is unsupported or disabled.");
        var token = __instance.Request.CancellationToken;
        token.ThrowIfCancellationRequested();
        var actor = __instance.AuthorizationContext.GetAuthorizationInfo(__instance.Request).User;
        if (actor == null || actor.InternalId <= 0 || actor.Id == Guid.Empty || actor.Policy?.IsDisabled != false || !actor.Policy.IsAdministrator)
            throw Error(HttpStatusCode.Forbidden, "An active administrator user is required for intro rebuild refresh.");
        var id = requestId!.GetValue(__0) as string;
        var item = state.Library.GetItemById(id);
        if (item == null || item.GetType() != typeof(Series) && item.GetType() != typeof(Season) || item is Season && item.IndexNumber is not >= 1)
            throw Error(HttpStatusCode.BadRequest, "Intro rebuild refresh requires an actual Series or numbered Season.");
        var ticket = state.Admit(item, actor.InternalId, actor.Id, token);
        try { state.Bind(ticket, __result); }
        catch { state.Finish(ticket, "Failed", "Native options binding failed."); throw; }
    }
    private sealed class QueueLease
    {
        internal readonly Ticket Ticket;
        internal readonly object Provider;
        internal readonly MetadataRefreshOptions Options;
        internal object? Held;
        internal QueueLease(Ticket ticket, object provider, MetadataRefreshOptions options) { Ticket = ticket; Provider = provider; Options = options; }
    }
    private static void BeforeQueue(object __instance, long __0, MetadataRefreshOptions __1, bool __3, out QueueLease? __state)
    {
        __state = null;
        if (__1 == null || !Bindings.TryGetValue(__1, out var ticket)) return;
        __state = new QueueLease(ticket, __instance, __1);
        ticket.Owner.Guard(ticket, ticket.Owner.Token);
        if (__0 != ticket.TargetId || __3 || ticket.Finished || !__1.Recursive) throw Error(HttpStatusCode.Conflict, "Native refresh binding differs or was already consumed.");
        var monitor = queueLock!.GetValue(__instance) ?? throw Error(HttpStatusCode.ServiceUnavailable, "Native refresh queue lock missing.");
        Monitor.Enter(monitor); __state.Held = monitor;
        // Native original enters the SAME monitor reentrantly. No manager/store/options callbacks under it.
        if ((bool)disposed!.GetValue(__instance)!) throw Error(HttpStatusCode.ServiceUnavailable, "Native metadata queue is stopping.");
        var queue = nativeQueue!.GetValue(__instance)!;
        if ((int)queueCount!.GetValue(queue)! >= 10000) throw Error(HttpStatusCode.Conflict, "Native metadata queue exceeds inspection budget.");
        foreach (var tuple in (IEnumerable)unordered!.GetValue(queue)!)
        {
            var entry = tupleElement!.GetValue(tuple)!;
            if (!(bool)queuedDisabled!.GetValue(entry)! && (long)queuedId!.GetValue(entry)! == __0)
                throw Error(HttpStatusCode.Conflict, "The target already has a native metadata refresh queued.");
        }
        if (Interlocked.CompareExchange(ref ticket.QueueClaimed, 1, 0) != 0) throw Error(HttpStatusCode.Conflict, "Native refresh ticket already queued.");
        ticket.Provider = __instance;
    }
    private static Exception? AfterQueue(QueueLease? __state, Exception? __exception)
    {
        if (__state == null) return __exception;
        var held = Interlocked.Exchange(ref __state.Held, null);
        try
        {
            if (held != null && __exception == null)
            {
                var queue = nativeQueue!.GetValue(__state.Provider)!;
                var found = false;
                if ((int)queueCount!.GetValue(queue)! <= 10000)
                    foreach (var tuple in (IEnumerable)unordered!.GetValue(queue)!)
                    {
                        var entry = tupleElement!.GetValue(tuple)!;
                        if (!(bool)queuedDisabled!.GetValue(entry)! && (long)queuedId!.GetValue(entry)! == __state.Ticket.TargetId &&
                            ReferenceEquals(queuedOptions!.GetValue(entry), __state.Options)) { found = true; break; }
                    }
                // _disposed can change without taking this monitor. The original may silently return; prove enqueue.
                if (!found) __exception = Error(HttpStatusCode.ServiceUnavailable, "Native metadata queue did not retain this exact request.");
            }
        }
        catch (Exception ex) { __exception ??= Error(HttpStatusCode.ServiceUnavailable, "Native metadata admission proof failed: " + ex.GetType().Name); }
        finally { if (held != null) Monitor.Exit(held); }
        if (__exception != null) __state.Ticket.Owner.Finish(__state.Ticket, "Failed", "Metadata admission failed: " + __exception.GetBaseException().GetType().Name);
        return __exception;
    }
    private static void AfterNativeComplete(object __instance, BaseItem __0, MetadataRefreshOptions __1)
    {
        if (__1 != null && Bindings.TryGetValue(__1, out var ticket) && !ticket.Finished && ReferenceEquals(ticket.Provider, __instance) && ticket.Matches(__0))
            Volatile.Write(ref ticket.NativeComplete, 1);
    }
    private static void AfterRefresh(object __instance, BaseItem __0, MetadataRefreshOptions __1, Task __result)
    {
        if (__1 == null || !Bindings.TryGetValue(__1, out var ticket) || ticket.Finished || !ReferenceEquals(ticket.Provider, __instance)) return;
        if (Interlocked.CompareExchange(ref ticket.MetadataClaimed, 1, 0) != 0) return;
        // Observe but DO NOT replace the native Task. Plugin stop/rebuild errors never poison the metadata queue.
        ticket.Owner.StartObserving(ticket, __0, __1, __result);
    }

    private sealed class Ticket
    {
        internal readonly Coordinator Owner;
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly long TargetId, ActorId;
        internal readonly Guid TargetGuid, ActorGuid;
        internal readonly Type RootType;
        internal readonly ScopedFingerprintSelection Scope;
        internal readonly DateTimeOffset Expires = DateTimeOffset.UtcNow.AddMinutes(30);
        internal int QueueClaimed, MetadataClaimed, NativeComplete, Done;
        internal object? Provider;
        internal Task? MetadataObserver;
        internal MetadataRefreshOptions? Options;
        internal readonly CancellationTokenSource Waiting;
        internal readonly CancellationToken WaitToken;
        internal bool CancelFinished;
        internal IntroRebuildReport? Rebuild;
        internal Exception? Failure;
        internal bool Executed;
        internal double Progress;
        internal Ticket(Coordinator owner, BaseItem item, long actorId, Guid actorGuid, ScopedFingerprintSelection scope)
        {
            Owner = owner; TargetId = item.InternalId; TargetGuid = item.Id; RootType = item.GetType(); ActorId = actorId; ActorGuid = actorGuid; Scope = scope;
            Waiting = CancellationTokenSource.CreateLinkedTokenSource(owner.Token); WaitToken = Waiting.Token;
        }
        internal bool Finished => Volatile.Read(ref Done) != 0;
        internal bool Matches(BaseItem item) => item.InternalId == TargetId && item.Id == TargetGuid && item.GetType() == RootType;
    }
    private sealed class Coordinator
    {
        internal readonly Plugin Plugin;
        internal readonly ILibraryManager Library;
        private readonly IUserManager users;
        private readonly ITaskManager tasks;
        private readonly IScheduledTaskWorker worker;
        private readonly ILogger logger;
        internal readonly bool Enabled;
        private int failed;
        internal bool Accepting => Enabled && !Token.IsCancellationRequested && Volatile.Read(ref failed) == 0;
        private readonly object gate = new();
        private readonly Dictionary<long, Ticket> pending = new();
        private readonly Queue<Ticket> ready = new();
        private readonly HashSet<Ticket> observers = new();
        internal int ObserverCount { get { lock (gate) return observers.Count; } }
        private readonly CancellationTokenSource stopping = new();
        internal readonly CancellationToken Token;
        private TaskCompletionSource<bool> changed = Signal();
        private Task pump = Task.CompletedTask;
        private Task[] stoppedObservers = Array.Empty<Task>();
        private Timer? expiry;
        internal int Count { get { lock (gate) return pending.Count; } }
        internal Coordinator(Plugin plugin, ILibraryManager library, IUserManager users, ITaskManager tasks,
            IScheduledTaskWorker worker, ILogger logger, bool enabled)
        { Plugin = plugin; Library = library; this.users = users; this.tasks = tasks; this.worker = worker; this.logger = logger; Enabled = enabled; Token = stopping.Token; }
        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Pulse() { var old = changed; changed = Signal(); old.TrySetResult(true); }
        internal void Start()
        {
            tasks.TaskCompleted += OnCompleted;
            expiry = new Timer(_ => Expire(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            pump = Task.Run(Drain);
        }
        private void OnCompleted(object? sender, TaskCompletionEventArgs e) { if (ReferenceEquals(e.Task, worker)) lock (gate) Pulse(); }
        internal Ticket Admit(BaseItem item, long actorId, Guid actorGuid, CancellationToken token)
        {
            RequireActor(actorId, actorGuid, token);
            if (Plugin.FingerprintApi?.IsSupported != true) throw Error(HttpStatusCode.ServiceUnavailable, "The native intro rebuild service is unsupported.");
            ScopedFingerprintSelection scope;
            try { scope = ScopedFingerprintSelection.Capture(Library, item.InternalId, Plugin.IntroStore.GetOptions(), token); }
            catch (InvalidOperationException ex) { throw Error(HttpStatusCode.BadRequest, ex.Message); }
            var ticket = new Ticket(this, item, actorId, actorGuid, scope);
            try
            {
                Guard(ticket, token);
                lock (gate)
                {
                    Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(current, this)) throw new OperationCanceledException("Intro refresh owner stopped.");
                    if (pending.Count + observers.Count(t => t.Finished) >= Capacity || pending.ContainsKey(item.InternalId))
                        throw Error(HttpStatusCode.Conflict, "Intro refresh queue is full or this root already has pending work.");
                    pending.Add(ticket.TargetId, ticket);
                }
            }
            catch { ticket.Waiting.Dispose(); throw; }
            Report(ticket, "MetadataQueued", "Awaiting this exact native metadata options object.");
            return ticket;
        }
        internal void Bind(Ticket ticket, MetadataRefreshOptions options)
        {
            lock (gate)
            {
                Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(current, this) || ticket.Finished || pending.GetValueOrDefault(ticket.TargetId) != ticket)
                    throw Error(HttpStatusCode.Conflict, "Intro refresh ticket expired before native options binding.");
                Bindings.Add(options, ticket);
                ticket.Options = options; // Publish only after Add succeeds, under the same gate used by Finish.
            }
        }
        private User RequireActor(long id, Guid guid, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Token.ThrowIfCancellationRequested();
            var user = users.GetUserById(id);
            CheckActor(user, id, guid);
            return user;
        }
        private static void CheckActor(User? user, long id, Guid guid)
        {
            if (user == null || user.InternalId != id || user.Id != guid || user.Policy?.IsDisabled != false || !user.Policy.IsAdministrator)
                throw Error(HttpStatusCode.Forbidden, "The captured requesting administrator was removed, disabled, replaced, or demoted.");
        }
        internal void Guard(Ticket ticket, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Token.ThrowIfCancellationRequested(); Plugin.WorkQueue.StoppingToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(current, this) || !ReferenceEquals(StrmAssistantRebuild.Plugin.Instance, Plugin) || ticket.Finished || !Healthy())
                throw new InvalidOperationException("Intro refresh owner, epoch, ticket, or native hook ownership changed.");
            var actor = RequireActor(ticket.ActorId, ticket.ActorGuid, token);
            ticket.Scope.ValidateApplyRoot(Plugin.IntroStore.GetOptions(), token);
            // End with retained actor/token checks, not another manager callback after the root proof.
            CheckActor(actor, ticket.ActorId, ticket.ActorGuid);
            if (!ReferenceEquals(current, this) || !ReferenceEquals(StrmAssistantRebuild.Plugin.Instance, Plugin) || ticket.Finished || !Healthy())
                throw new InvalidOperationException("Intro refresh authority changed during native reads.");
            token.ThrowIfCancellationRequested(); Token.ThrowIfCancellationRequested(); Plugin.WorkQueue.StoppingToken.ThrowIfCancellationRequested();
        }
        internal void StartObserving(Ticket ticket, BaseItem item, MetadataRefreshOptions options, Task native)
        {
            lock (gate)
            {
                if (ticket.Finished || Token.IsCancellationRequested || !ReferenceEquals(current, this)) return;
                if (observers.Count >= Capacity) { Finish(ticket, "Failed", "Native metadata observer capacity reached."); return; }
                observers.Add(ticket);
                // Scheduling under the gate prevents synchronous completion from escaping task registration.
                ticket.MetadataObserver = Task.Run(() => ObserveMetadata(ticket, item, options, native));
            }
        }
        private async Task ObserveMetadata(Ticket ticket, BaseItem item, MetadataRefreshOptions options, Task native)
        {
            try
            {
                await native.WaitAsync(ticket.WaitToken).ConfigureAwait(false);
                if (ticket.Finished || Token.IsCancellationRequested) return;
                if (!options.Recursive || !ticket.Matches(item) || Volatile.Read(ref ticket.NativeComplete) != 1)
                { Finish(ticket, "Failed", "Native root Task settled without the matching NativeCompletionPathReached witness."); return; }
                Guard(ticket, Token);
                lock (gate) { if (!ticket.Finished && !Token.IsCancellationRequested) { ready.Enqueue(ticket); Pulse(); } }
                Report(ticket, "RebuildQueued", "NativeCompletionPathReached; child-provider success is not asserted.");
            }
            catch (OperationCanceledException) { Finish(ticket, "Cancelled", "Native metadata or bridge authority was cancelled."); }
            catch (Exception ex) { Finish(ticket, "Failed", "Metadata completion failed: " + ex.GetBaseException().GetType().Name); }
            finally
            {
                lock (gate)
                {
                    observers.Remove(ticket); ticket.MetadataObserver = null;
                    if (ticket.CancelFinished) ticket.Waiting.Dispose();
                }
            }
        }
        private async Task Drain()
        {
            try
            {
                while (true)
                {
                    Token.ThrowIfCancellationRequested();
                    Ticket? ticket; Task wake;
                    lock (gate) { while (ready.Count != 0 && ready.Peek().Finished) ready.Dequeue(); ticket = ready.Count == 0 ? null : ready.Peek(); wake = changed.Task; }
                    if (ticket == null) { await wake.WaitAsync(Token).ConfigureAwait(false); continue; }
                    if (worker.State != TaskState.Idle || ExtractIntroFingerprintTask.IsRunning)
                    { Report(ticket, "Paused", "Waiting for the existing native intro worker to become idle."); await wake.WaitAsync(Token).ConfigureAwait(false); continue; }
                    lock (gate) { if (ready.Count != 0 && ReferenceEquals(ready.Peek(), ticket)) ready.Dequeue(); else continue; }
                    try
                    {
                        Guard(ticket, Token);
                        if (!tasks.ScheduledTasks.Any(w => ReferenceEquals(w, worker))) throw new InvalidOperationException("Native worker was replaced.");
                        var task = (ExtractIntroFingerprintTask)worker.ScheduledTask;
                        var options = task.CreateRebuildOptions(ticket.TargetId, async (progress, ct) =>
                        {
                            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Token);
                            try
                            {
                                Guard(ticket, linked.Token);
                                lock (gate)
                                {
                                    linked.Token.ThrowIfCancellationRequested();
                                    if (ticket.Finished) throw new InvalidOperationException("Intro refresh ticket ended before rebuild start.");
                                    Volatile.Write(ref ticket.Executed, true);
                                }
                                Report(ticket, "Rebuilding", "M103 fresh child selection on the request-bound root.");
                                var api = Plugin.FingerprintApi ?? throw new NotSupportedException("Intro rebuild service is unavailable.");
                                ticket.Rebuild = await api.RebuildIntroForRefreshAsync(ticket.TargetId, new ForwardProgress(progress, v =>
                                { ticket.Progress = v; Report(ticket, "Rebuilding", "M103 native-worker progress."); }), linked.Token, ticket.Scope, t => Guard(ticket, t)).ConfigureAwait(false);
                                Guard(ticket, linked.Token);
                                if (ticket.Rebuild.State != "Completed") throw new InvalidOperationException("Intro rebuild completed with failures; inspect its report.");
                            }
                            catch (Exception ex)
                            { ticket.Failure = ex; ticket.Rebuild ??= ex.Data["IntroRebuildReport"] as IntroRebuildReport; throw; }
                        }, Token);
                        // Native QueueScheduledTask coalesces by task Type. Direct Execute once per FIFO entry preserves each bound object.
                        await tasks.Execute(worker, options).ConfigureAwait(false);
                        if (ticket.Failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ticket.Failure).Throw();
                        if (!ticket.Executed || task.LastRebuild?.TargetId != ticket.TargetId || task.LastRebuild.State != "Completed")
                            throw new InvalidOperationException("Native worker did not complete this bound rebuild: " + task.LastRebuild?.State);
                        Finish(ticket, "Completed", "Native metadata completion path followed by M103 rebuild completed.");
                    }
                    catch (OperationCanceledException) { Finish(ticket, "Cancelled", "Intro rebuild cancelled."); }
                    catch (Exception ex)
                    {
                        ticket.Rebuild ??= ex.Data["IntroRebuildReport"] as IntroRebuildReport;
                        Finish(ticket, "Failed", "Intro rebuild failed: " + ex.GetBaseException().GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Volatile.Write(ref failed, 1);
                Ticket[] items; lock (gate) { items = pending.Values.ToArray(); ready.Clear(); }
                foreach (var ticket in items) Finish(ticket, "Failed", "Intro worker coordinator stopped: " + ex.GetBaseException().GetType().Name);
            }
        }
        private sealed class ForwardProgress : IProgress<double>
        {
            private readonly IProgress<double> native;
            private readonly Action<double> report;
            internal ForwardProgress(IProgress<double> native, Action<double> report) { this.native = native; this.report = report; }
            public void Report(double value) { report(value); native.Report(value); }
        }
        internal void Finish(Ticket ticket, string state, string reason, bool onlyIfUnstarted = false)
        {
            lock (gate)
            {
                if (onlyIfUnstarted && ticket.Executed || Interlocked.Exchange(ref ticket.Done, 1) != 0) return;
                if (ticket.Options is { } options && Bindings.TryGetValue(options, out var bound) && ReferenceEquals(bound, ticket)) Bindings.Remove(options);
                ticket.Options = null;
                if (pending.GetValueOrDefault(ticket.TargetId) == ticket) pending.Remove(ticket.TargetId);
                Pulse();
            }
            ticket.Waiting.Cancel(); // Cancels only our WaitAsync observer, never the original native metadata Task.
            lock (gate)
            {
                ticket.CancelFinished = true;
                if (!observers.Contains(ticket)) ticket.Waiting.Dispose();
            }
            Report(ticket, state, reason);
        }
        private void Report(Ticket ticket, string state, string reason)
        {
            lock (gate)
            {
                if (state is not ("Completed" or "Failed" or "Cancelled") && (ticket.Finished || !ReferenceEquals(current, this))) return;
                Volatile.Write(ref lastReport, new(ticket.Id, ticket.TargetId, ticket.ActorId, state, reason,
                    Volatile.Read(ref ticket.NativeComplete) == 1, ticket.Progress, pending.Count, ticket.Rebuild));
            }
            if (state is "Completed" or "Failed" or "Cancelled")
                try { logger.Info("[StrmAssistantRebuild] Intro refresh {0} target {1}: {2}; {3}", ticket.Id, ticket.TargetId, state, reason); } catch { }
        }
        private void Expire()
        {
            Ticket[] expired;
            lock (gate) expired = pending.Values.Where(t => !t.Executed && t.Expires <= DateTimeOffset.UtcNow).ToArray();
            foreach (var ticket in expired) Finish(ticket, "Failed", "Intro refresh admission expired after 30 minutes without starting rebuild; no options were adopted or retried.", onlyIfUnstarted: true);
        }
        internal void Stop()
        {
            Ticket[] items;
            lock (gate)
            {
                // current was cleared before Stop: no new observer can register past this snapshot.
                stoppedObservers = observers.Select(t => t.MetadataObserver!).ToArray();
                items = pending.Values.ToArray(); ready.Clear(); Pulse();
            }
            stopping.Cancel(); tasks.TaskCompleted -= OnCompleted;
            foreach (var ticket in items)
                if (!Volatile.Read(ref ticket.Executed)) Finish(ticket, "Cancelled", "Plugin bridge stopped or was reconfigured.", onlyIfUnstarted: true);
            // Active M103 work is joined below; let its settled cancellation carry any committed receipts.
        }
        internal async Task JoinAsync()
        {
            if (expiry != null) await expiry.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(stoppedObservers.Append(pump)).ConfigureAwait(false);
            stoppedObservers = Array.Empty<Task>(); stopping.Dispose();
        }
    }
}
