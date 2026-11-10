using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Emby.Notifications;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;

/// <summary>Selected metadata fields after a successful native editor POST; not automatic refresh tracking.</summary>
public static class MetadataEditNotification
{
    public const string EventId = "strmassistant.rebuild.metadata.updated";
    private const string Owner = "StrmAssistantRebuild.MetadataEdit.4.10.2";
    private static readonly object Gate = new();
    private static Harmony? patcher;
    private static MethodInfo? installed;
    private sealed record Settings(string[] Fields, ILibraryManager Library, INotificationManager Notifications, IUserManager? Users);
    private sealed record EditState(Settings Settings, BaseItem Item, User Actor, long ActorId, Dictionary<string, object?> Before, CancellationToken Token, CultureInfo Culture);
    private static Settings? current;
    public static bool IsEnabled => Volatile.Read(ref current) != null;
    public static string Detail { get; private set; } = "Disabled; native metadata notifications unchanged.";
    // D93 original selector evidence: SeriesOrder is a provider value, not the Series CLR property.
    private const string FileOrderProviderKey = "fileorder";
    internal static readonly string[] Fields = { "Name", "Overview", "OriginalTitle", "Tagline", "OfficialRating", "CustomRating", "CriticRating", "CommunityRating", "IndexNumber", "ParentIndexNumber", "PremiereDate", "ProductionYear", "EndDate", "RunTimeTicks", "Tags", "Genres", "Studios", "ProductionLocations", "ProviderIds", "SeriesOrder" };
    internal static string[] Parse(string? value) => (value ?? "").Split(new[]{',',';'}, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(key => Fields.FirstOrDefault(field => field.Equals(key,StringComparison.OrdinalIgnoreCase))).Where(key => key != null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
    internal static Dictionary<string, object?> Snapshot(BaseItem item, IEnumerable<string> fields) => fields.ToDictionary(key => key, key => Read(item,key), StringComparer.Ordinal);
    private static object? Read(BaseItem item, string field) => field switch
    {
        "Name" => item.Name?.Trim(), "Overview" => item.Overview?.Trim(), "OriginalTitle" => item.OriginalTitle?.Trim(),
        "Tagline" => item.Tagline?.Trim(), "OfficialRating" => item.OfficialRating?.Trim(), "CustomRating" => item.CustomRating?.Trim(),
        "CriticRating" => item.CriticRating, "CommunityRating" => item.CommunityRating, "IndexNumber" => item.IndexNumber,
        "ParentIndexNumber" => item.ParentIndexNumber, "PremiereDate" => item.PremiereDate, "ProductionYear" => item.ProductionYear,
        "EndDate" => item.EndDate, "RunTimeTicks" => item.RunTimeTicks.GetValueOrDefault(),
        "Tags" => item.Tags?.ToArray() ?? Array.Empty<string>(), "Genres" => item.Genres?.ToArray() ?? Array.Empty<string>(),
        "Studios" => item.Studios?.ToArray() ?? Array.Empty<string>(), "ProductionLocations" => item.ProductionLocations?.ToArray() ?? Array.Empty<string>(),
        // Preserve the original direct-reference snapshot and same-reference comparator fast path.
        "ProviderIds" => item.ProviderIds, "SeriesOrder" => item.GetProviderId(FileOrderProviderKey)?.Trim(),
        _ => throw new ArgumentException("Unknown metadata field",nameof(field))
    };
    internal static string[] Changes(BaseItem item, Dictionary<string, object?> before) => before.Where(pair => !Equal(pair.Key, pair.Value, Read(item,pair.Key))).Select(pair => pair.Key).ToArray();
    private static bool Equal(string field, object? a, object? b) => field == "ProviderIds" ? EqualProviders(a as ProviderIdDictionary, b as ProviderIdDictionary)
        : a is string[] array && b is string[] other ? array.SequenceEqual(other,StringComparer.Ordinal) : Equals(a,b);
    private static bool FileOrder(string key) => string.Equals(key,FileOrderProviderKey,StringComparison.OrdinalIgnoreCase);
    private static bool EqualProviders(ProviderIdDictionary? a, ProviderIdDictionary? b)
    {
        if(ReferenceEquals(a,b))return true;
        if(a==null || b==null || a.Count(pair=>!FileOrder(pair.Key))!=b.Count(pair=>!FileOrder(pair.Key)))return false;
        foreach(var pair in a)
            if(!FileOrder(pair.Key) && (!b.TryGetValue(pair.Key,out var value) || !StringComparer.OrdinalIgnoreCase.Equals(pair.Value,value)))return false;
        return true;
    }
    private static bool Supported(BaseItem item) => item is Movie or Series or Season or Episode or BoxSet;
    internal static MethodInfo[] FindTargets()
    {
        var type=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="Emby.Api")?.GetType("Emby.Api.ItemUpdateService");
        if(type==null)return Array.Empty<MethodInfo>();
        return type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)
            .Where(m=>(m.Name=="Post" && m.GetParameters().Length==1 && m.GetParameters()[0].ParameterType.FullName=="Emby.Api.UpdateItem") ||
                (m.Name=="UpdateItem" && m.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(BaseItemDto),typeof(BaseItem)})))
            .OrderByDescending(m=>m.IsPublic).ToArray();
    }
    internal static bool Verify(MethodInfo method)
    {
        var body=method.GetMethodBody();var code=body?.GetILAsByteArray();
        if(method.DeclaringType?.FullName!="Emby.Api.ItemUpdateService" || !method.DeclaringType.IsPublic || !method.DeclaringType.IsSealed ||
            method.ReturnType!=typeof(void) || method.IsStatic || method.ContainsGenericParameters || body==null || code==null || !body.InitLocals ||
            method.Module.ModuleVersionId!=new Guid("0873fdaf-f3f8-45f4-8d9c-b647b1edf372") || method.Module.Assembly.GetName().Name!="Emby.Api" ||
            method.Module.Assembly.GetName().Version!=new Version(4,10,2,0))return false;
        var eh=string.Join(";",body.ExceptionHandlingClauses.Select(c=>$"{(int)c.Flags}:{c.TryOffset}:{c.TryLength}:{c.HandlerOffset}:{c.HandlerLength}:{(c.Flags==ExceptionHandlingClauseOptions.Clause?c.CatchType?.FullName:c.Flags==ExceptionHandlingClauseOptions.Filter?c.FilterOffset.ToString(CultureInfo.InvariantCulture):"")}"));
        var hash=Convert.ToHexString(SHA256.HashData(code));
        var localHash=Convert.ToHexString(SHA256.HashData(method.Module.ResolveSignature(body.LocalSignatureMetadataToken)));
        if(method.IsPublic && method.Name=="Post" && method.GetParameters().Length==1 && method.GetParameters()[0].ParameterType.FullName=="Emby.Api.UpdateItem")
            return method.MetadataToken==0x060000CC && body.MaxStackSize==5 && body.LocalSignatureMetadataToken==0x1100002D && eh=="" && code.Length==441 &&
                hash=="653F0EF21D8595E06485A0618AFF5EF6DEDC56AC2C5ECE82CC1CDD0CDEFD2F1B" && localHash=="DDB06B97492FD7353B829377AC29DBFA199101645D3EF7DFCF8D3FAF30C8F021";
        return method.IsPrivate && method.Name=="UpdateItem" && method.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(BaseItemDto),typeof(BaseItem)}) &&
            method.MetadataToken==0x060000CE && body.MaxStackSize==4 && body.LocalSignatureMetadataToken==0x1100002E && eh=="2:818:55:873:14:" && code.Length==1177 &&
            hash=="1F6718BA789C1870628910CC726C64F797CEA66D9088365178451B17E424DB00" && localHash=="66E01BA78DE01B9406766C5DF79A26D1AF23EC48115DA59C81885739BF4333F3";
    }
    public static bool Configure(Version version, bool enabled, string? fields, ILibraryManager library, INotificationManager notifications, ILogger logger, IUserManager? userManager = null)
    {
        lock(Gate)
        {
            try
            {
                Remove();var selected=Parse(fields);
                if(!enabled || selected.Length==0){Detail="Disabled; no selected supported metadata fields.";return true;}
                var targets=FindTargets();
                if(version!=new Version(4,10,2,0)||targets.Length!=2||targets.Any(m=>!Verify(m))||targets.Any(m=>Harmony.GetPatchInfo(m)?.Owners.Any()==true))
                {Detail="Metadata edit entry fingerprint/ownership differs.";return false;}
                installed=targets[0];(patcher??=new Harmony(Owner)).Patch(installed,prefix:new HarmonyMethod(typeof(MetadataEditNotification),nameof(Before)),postfix:new HarmonyMethod(typeof(MetadataEditNotification),nameof(After)));
                Volatile.Write(ref current,new(selected,library,notifications,userManager));Detail="Native editor metadata notification: "+string.Join(", ",selected)+"; custom opt-in event; image/automatic refresh changes not tracked.";return true;
            }
            catch(Exception e){Volatile.Write(ref current,null);try{Remove();}catch{/* Keep the installed handle for a later cleanup attempt. */}Detail="Metadata edit tracking inactive: "+e.GetType().Name;return false;}
            finally{try{logger.Info("[StrmAssistantRebuild] {0}",Detail);}catch{}}
        }
    }
    public static void Remove(){lock(Gate){Volatile.Write(ref current,null);if(installed!=null){patcher?.Unpatch(installed,HarmonyPatchType.All,Owner);installed=null;}Detail="Disabled; native metadata notifications unchanged.";}}
    private static void Before(BaseApiService __instance, BaseItemDto __0, out EditState? __state)
    {
        __state=null;var settings=Volatile.Read(ref current);if(settings==null || __0==null)return;
        try
        {
            var token=__instance.Request.CancellationToken;if(token.IsCancellationRequested)return;
            var actor=__instance.AuthorizationContext.GetAuthorizationInfo(__instance.Request).User;
            if(actor?.Policy?.IsDisabled!=false)return;
            var id=__0.GetType().GetProperty("ItemId")?.GetValue(__0) as string;
            // Use the same resolver as the native editor: decimal and GUID aliases share one identity.
            var numeric=settings.Library.GetInternalId(id);
            if(numeric<=0)return;
            var item=settings.Library.GetItemById(numeric);if(item==null||item.InternalId!=numeric||!Supported(item))return;
            if(!settings.Library.FilterItemsToIdsForUser(new[]{item},actor,null,token).Contains(item.InternalId))return;
            // This read-only snapshot precedes native AssertCanEditItem. A failing POST has no postfix.
            __state=new(settings,item,actor,actor.InternalId,Snapshot(item,settings.Fields),token,CultureInfo.CurrentUICulture);
        }
        catch{__state=null;}
    }
    private static void After(EditState? __state)
    {
        var state=__state;
        if(state==null||state.Token.IsCancellationRequested||!ReferenceEquals(state.Settings,Volatile.Read(ref current)))return;
        try
        {
            // Native POST may resolve a different object than the prefix's read. Compare a fresh
            // persisted item, never assume our pre-edit instance was the one mutated by the host.
            var item=state.Settings.Library.GetItemById(state.Item.InternalId);
            if(item==null||item.InternalId!=state.Item.InternalId||item.GetType()!=state.Item.GetType())return;
            var fields=Changes(item,state.Before);
            if(fields.Length==0)return;
            var actor=NotificationActor.Current(state.Settings.Users,state.Actor,state.ActorId,requireAdmin:false);
            if(actor==null||!state.Settings.Library.FilterItemsToIdsForUser(new[]{item},actor,null,state.Token).Contains(item.InternalId))return;
            if(state.Token.IsCancellationRequested||!ReferenceEquals(state.Settings,Volatile.Read(ref current)))return;
            state.Settings.Notifications.SendNotification(new NotificationRequest{EventId=EventId,User=actor,Item=item,
                Title=Title(state.Culture.Name),Description=string.Join(", ",fields),CancellationToken=state.Token});
        }
        catch { /* Notification failure never converts a committed metadata edit to a failed API call. */ }
    }
    public static string Title(string? language) => language?.StartsWith("zh",StringComparison.OrdinalIgnoreCase)==true
        ? language.Contains("Hant",StringComparison.OrdinalIgnoreCase)||language.Equals("zh-TW",StringComparison.OrdinalIgnoreCase)||language.Equals("zh-HK",StringComparison.OrdinalIgnoreCase)?"中繼資料欄位已更新":"元数据字段已更新"
        : "Metadata fields updated";
}
