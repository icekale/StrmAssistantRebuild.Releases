using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Data;
using MediaBrowser.Model.Querying;

namespace StrmAssistantRebuild.Common;

/// <summary>Original linked-collections request: Series plus its visible Seasons, before native SQL paging.</summary>
public static class SeasonCollectionQuery
{
    private const string Owner = "StrmAssistantRebuild.SeasonCollectionQuery.4.10.2";
    private const string ServiceName = "Emby.Api.UserLibrary.ItemsService";
    private const string RequestName = "Emby.Api.UserLibrary.GetItems";
    internal const int SeasonBudget = 1024;
    private static readonly object Gate = new();
    private static readonly Version SupportedVersion = new(4, 10, 2, 0);
    private static Harmony? patcher;
    private static MethodInfo? installed, caller;
    private static Type? serviceType, requestType;
    private static volatile bool enabled, doubanEnabled;
    private static long generation;
    public static bool IsEnabled { get { lock (Gate) return enabled && CheckHealth(); } }
    public static bool IsDoubanFilterEnabled { get { lock (Gate) return doubanEnabled && CheckHealth(); } }
    public static bool IsSupported { get; private set; }
    public static string Detail { get; private set; } = "Disabled; native collection membership unchanged.";
    public static string DoubanDetail { get; private set; } = "Disabled; native provider filters unchanged.";

    // Legacy callers configure only the season feature, as before.
    public static bool Configure(Version hostVersion, bool enable, out string detail) => Configure(hostVersion, enable, false, out detail);

    public static bool Configure(Version hostVersion, bool enable, bool enableDoubanFilter, out string detail)
    {
        lock (Gate)
        {
            enabled = doubanEnabled = false;
            Interlocked.Increment(ref generation);
            IsSupported = false;
            try
            {
                if (!enable && !enableDoubanFilter)
                {
                    Remove();
                    Detail = DoubanDetail = "Disabled; owned query postfix removed.";
                    detail = Detail;
                    return true;
                }
                if (hostVersion != SupportedVersion) throw new NotSupportedException("host version differs from 4.10.2.0");
                var api = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.Api")
                    ?? throw new NotSupportedException("native API assembly not loaded");
                var host = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.Server.Implementations")
                    ?? throw new NotSupportedException("native repository assembly not loaded");
                if (!PinAssembly(typeof(BaseApiService).Assembly, "MediaBrowser.Controller", "EAA6B9EDEBB21FE3AF9C272DEF8C913D9EC6B177B552E1B789FEE15CB59289DA") ||
                    !PinAssembly(typeof(ItemFields).Assembly, "MediaBrowser.Model", "2B7DA29239811F8C035784DF7B3F48A1668E9537784B6524DDE43B3905572617") ||
                    !PinAssembly(api, "Emby.Api", "2D5F2B4CB92DE964B478A3C50787515AEFB750C0041BC019E6BCCDFDF231AD4C") ||
                    !PinAssembly(host, "Emby.Server.Implementations", "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F"))
                    throw new NotSupportedException("native assembly identity/bytes differ");
                serviceType = api.GetType(ServiceName, true)!;
                requestType = api.GetType(RequestName, true)!;
                var target = typeof(BaseApiService).GetMethod("GetItemsQuery", BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(BaseItem), typeof(BaseItemsRequest), typeof(DtoOptions), typeof(User), typeof(IDataContext) }, null);
                caller = serviceType.GetMethod("GetQueryResult", BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { requestType, typeof(DtoOptions), typeof(User), typeof(IDataContext), typeof(CancellationToken) }, null);
                if (target == null || caller == null || !VerifyTarget(target) || !VerifyCaller(caller))
                    throw new NotSupportedException("native signature/MVID/token/IL differs");
                if (!ValidPatches(Harmony.GetPatchInfo(target), allowMissing: true) || !Unpatched(Harmony.GetPatchInfo(caller))) throw new NotSupportedException("foreign or altered query/caller patch present");
                if (installed != target || !ValidPatches(Harmony.GetPatchInfo(target)))
                {
                    Remove();
                    installed = target;
                    (patcher ??= new Harmony(Owner)).Patch(target, postfix: new HarmonyMethod(typeof(SeasonCollectionQuery)
                        .GetMethod(nameof(AfterQuery), BindingFlags.Static | BindingFlags.NonPublic)!));
                }
                if (!CheckHealth()) throw new NotSupportedException("owned query postfix verification failed");
                IsSupported = true;
                enabled = enable;
                doubanEnabled = enableDoubanFilter;
                Detail = enable ? "Active; GetItems BoxSet Limit=12 single Series expands visible Seasons before native membership/count/page." : "Disabled; native collection membership unchanged.";
                DoubanDetail = enableDoubanFilter ? "Active; GetItems raw HasDoubanId maps to native HasAnyProviderId/MissingAnyProviderId (OR within each array)." : "Disabled; native provider filters unchanged.";
                detail = Detail;
                return true;
            }
            catch (Exception error)
            {
                try { Remove(); } catch { }
                Detail = DoubanDetail = "Inactive: " + error.GetType().Name + ": " + error.Message;
                detail = Detail;
                return false;
            }
        }
    }

    public static void Disable()
    {
        lock (Gate)
        {
            enabled = doubanEnabled = false;
            Interlocked.Increment(ref generation);
            IsSupported = false;
            try { Remove(); Detail = DoubanDetail = "Disabled; owned query postfix removed."; }
            catch { Detail = DoubanDetail = "Disabled; owned postfix cleanup pending, callback remains inert."; }
        }
    }

    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.Postfix, Owner);
        installed = null;
    }

    private static bool Unpatched(Patches? p) => p == null ||
        p.Prefixes.Count + p.Postfixes.Count + p.Transpilers.Count + p.Finalizers.Count + p.InnerPrefixes.Count + p.InnerPostfixes.Count == 0;

    private static bool ValidPatches(Patches? p, bool allowMissing = false)
    {
        if (p == null) return allowMissing;
        if (p.Prefixes.Count + p.Transpilers.Count + p.Finalizers.Count + p.InnerPrefixes.Count + p.InnerPostfixes.Count != 0) return false;
        if (p.Postfixes.Count == 0) return allowMissing;
        if (p.Postfixes.Count != 1) return false;
        var hook = p.Postfixes[0];
        return hook.owner == Owner && hook.PatchMethod == typeof(SeasonCollectionQuery).GetMethod(nameof(AfterQuery), BindingFlags.Static | BindingFlags.NonPublic) &&
            hook.priority == Priority.Normal && hook.before.Length == 0 && hook.after.Length == 0 && !hook.debug;
    }

    // Called under Gate; check all six categories, not merely the Harmony owner string.
    private static bool CheckHealth()
    {
        if (installed != null && caller != null && ValidPatches(Harmony.GetPatchInfo(installed)) && Unpatched(Harmony.GetPatchInfo(caller))) return true;
        enabled = doubanEnabled = false;
        Interlocked.Increment(ref generation);
        IsSupported = false;
        Detail = DoubanDetail = "Inactive: foreign, altered or missing query/caller patch detected; native query retained.";
        return false;
    }
    private static bool PinAssembly(Assembly assembly, string name, string hash) => assembly.GetName().Name == name &&
        assembly.GetName().Version == SupportedVersion && !string.IsNullOrEmpty(assembly.Location) &&
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) == hash;

    internal static bool VerifyTarget(MethodInfo m) => m.DeclaringType == typeof(BaseApiService) && m.IsFamily && !m.IsStatic &&
        m.Name == "GetItemsQuery" && m.ReturnType == typeof(InternalItemsQuery) &&
        m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(BaseItem), typeof(BaseItemsRequest), typeof(DtoOptions), typeof(User), typeof(IDataContext) }) &&
        PinMethod(m, "58dae430-7295-47bb-a62f-b26bf0d89604", 0x060015D3, 3432, "F31A3E8FCDF6F60EE6A67C0349895EBF2037CF93CD27CE8B24260A543F4E5E67", 6, 0x1100010E, 22, 1);

    private static bool VerifyCaller(MethodInfo m) => m.DeclaringType == serviceType && m.IsPrivate && !m.IsStatic &&
        m.ReturnType == typeof(MediaBrowser.Model.Querying.QueryResult<BaseItem>) &&
        PinMethod(m, "0873fdaf-f3f8-45f4-8d9c-b647b1edf372", 0x060002C5, 191, "F8A8203BD9E8285F2689AE9C28B2DE6641EE367F8FFBFAE2ADF5865A2B1CADA4", 6, 0x1100007E, 5, 0);

    private static bool PinMethod(MethodInfo m, string mvid, int token, int length, string hash, int stack, int locals, int localCount, int eh)
    {
        var body = m.GetMethodBody(); var il = body?.GetILAsByteArray();
        return !m.ContainsGenericParameters && m.Module.ModuleVersionId == new Guid(mvid) && m.MetadataToken == token &&
            body != null && body.InitLocals && body.MaxStackSize == stack && body.LocalSignatureMetadataToken == locals &&
            body.LocalVariables.Count == localCount && body.LocalVariables.All(v => !v.IsPinned) && body.ExceptionHandlingClauses.Count == eh &&
            il?.Length == length && Convert.ToHexString(SHA256.HashData(il)) == hash;
    }

    private static void AfterQuery(BaseApiService __instance, BaseItemsRequest request, User user, IDataContext dataContext, InternalItemsQuery __result)
    {
        var epoch = Volatile.Read(ref generation);
        if ((!enabled && !doubanEnabled) || __result == null || __instance == null || __instance.GetType() != serviceType || request == null || request.GetType() != requestType) return;
        lock (Gate) if (epoch != generation || !CheckHealth()) return;
        var token = __instance.Request?.CancellationToken ?? CancellationToken.None;
        if (doubanEnabled)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                // Request getters may re-enter Configure: never hold Gate while reading them.
                var raw = __instance.Request?.QueryString?["HasDoubanId"];
                lock (Gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (epoch != generation || !doubanEnabled || !CheckHealth()) return;
                    ApplyDoubanFilter(__result, raw);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* A missing raw request leaves provider filters unchanged, independently of seasons. */ }
        }
        try
        {
            if (!enabled || epoch != Volatile.Read(ref generation) || user == null || user.Policy.IsDisabled ||
                request.Limit != 12 || !string.Equals(request.IncludeItemTypes, "BoxSet", StringComparison.Ordinal) ||
                !request.Recursive || string.IsNullOrEmpty(request.ListItemIds) || request.ListItemIds.Length > 128 ||
                request.ListItemIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Length != 1 ||
                __result.ListItemIds.Length != 1 || __result.ListItemIds[0] <= 0) return;
            token.ThrowIfCancellationRequested();
            var library = __instance.LibraryManager;
            var id = __result.ListItemIds[0];
            if (library.GetItemById(id, dataContext) is not Series series || series.InternalId != id) return;
            if (!library.FilterItemsToIdsForUser(new BaseItem[] { series }, user, dataContext, token).Contains(id)) return;
            token.ThrowIfCancellationRequested();
            // Same virtual Series path as GetChildren(user): native presentation-key grouping, not guessed ParentId.
            // ponytail: 1025 is an overflow witness; >1024 stays native, never silently truncates membership.
            var children = series.GetItemList(new InternalItemsQuery(user)
            {
                Limit = SeasonBudget + 1,
                DtoOptions = new DtoOptions { EnableImages = false, EnableUserData = false, Fields = new[] { ItemFields.Id } }
            }, dataContext, token);
            token.ThrowIfCancellationRequested();
            if (children.Length > SeasonBudget || children.Any(i => i == null || i.InternalId <= 0)) return;
            var seasons = children.OfType<Season>().Cast<BaseItem>().ToArray();
            if (seasons.Length == 0) return;
            var visible = library.FilterItemsToIdsForUser(seasons, user, dataContext, token).ToHashSet();
            token.ThrowIfCancellationRequested();
            var ids = new[] { id }.Concat(seasons.Where(s => visible.Contains(s.InternalId)).Select(s => s.InternalId)).Distinct().ToArray();
            lock (Gate)
            {
                token.ThrowIfCancellationRequested();
                if (ids.Length > 1 && enabled && epoch == generation && CheckHealth()) __result.ListItemIds = ids;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Each optional feature leaves its native fields intact on unavailable evidence. */ }
    }

    internal static void ApplyDoubanFilter(InternalItemsQuery query, string? raw)
    {
        if (!bool.TryParse(raw, out var hasId)) return;
        // Match the captured original: append even an existing Douban key; preserve native OR semantics.
        if (hasId) query.HasAnyProviderId = query.HasAnyProviderId.Concat(new[] { "Douban" }).ToArray();
        else query.MissingAnyProviderId = query.MissingAnyProviderId.Concat(new[] { "Douban" }).ToArray();
    }
}
