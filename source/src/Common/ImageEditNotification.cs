using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Emby.Notifications;
using HarmonyLib;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;

/// <summary>Native upload/URL image completion notifications; never alters image or transport behavior.</summary>
public static class ImageEditNotification
{
    public const string EventId = OriginalNotifications.Image; // M164: original event id
    private const string Owner = "StrmAssistantRebuild.ImageEdit.4.10.2";
    private const string Implementation = "Emby.Api.Images.ImageService";
    private const string RemoteImplementation = "Emby.Api.Images.RemoteImageService";
    private static readonly object Gate = new();
    private static readonly List<MethodInfo> installed = new();
    private static Harmony? patcher;
    private sealed record Settings(ILibraryManager Library, INotificationManager Notifications, IUserManager? Users);
    private sealed record ImageState(Settings Settings, long Id, Type ItemType, User Actor, long ActorId, ImageType ImageType, CancellationToken Token, string Language);
    private static Settings? current;
    public static bool IsEnabled => Volatile.Read(ref current) != null;
    public static string Detail { get; private set; } = "Disabled; native image notifications unchanged.";
    internal static MethodInfo[] FindTargets()
    {
        var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="Emby.Api");
        var type=assembly?.GetType(Implementation);var remote=assembly?.GetType(RemoteImplementation);
        if(type==null||remote==null)return Array.Empty<MethodInfo>();
        return type.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly)
            .Where(m=>(m.Name=="PostImage" && m.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(BaseItem),typeof(Stream),typeof(ImageType),typeof(int?),typeof(string)})) ||
                (m.Name=="Post" && m.GetParameters().Length==1 && m.GetParameters()[0].ParameterType.FullName=="Emby.Api.Images.UpdateItemImageFromUrl"))
            .OrderByDescending(m=>m.Name=="PostImage")
            .Concat(remote.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly).Where(m=>m.Name=="Post"&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType.FullName=="Emby.Api.Images.DownloadRemoteImage")).ToArray();
    }
    internal static bool Verify(MethodInfo method)
    {
        if(method.DeclaringType?.FullName==RemoteImplementation)return VerifyRemote(method);
        var type=method.DeclaringType;
        var state=method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        var move=state?.GetMethod("MoveNext",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
        if(type?.FullName!=Implementation || !type.IsPublic || !type.IsSealed || !method.IsPublic || method.IsStatic || method.ContainsGenericParameters ||
            method.ReturnType!=typeof(Task) || type.Assembly.GetName().Name!="Emby.Api" || type.Assembly.GetName().Version!=new Version(4,10,2,0) ||
            method.Module.ModuleVersionId!=new Guid("0873fdaf-f3f8-45f4-8d9c-b647b1edf372") || state?.DeclaringType!=type || move==null || !typeof(IAsyncStateMachine).IsAssignableFrom(state))return false;
        if(method.Name=="PostImage" && method.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(BaseItem),typeof(Stream),typeof(ImageType),typeof(int?),typeof(string)}))
            return Pin(method,0x06000744,97,2,0x11000100,"BFA0863D299306ADB11ED387FD524CA0850EC512F6A9061691660544B31A12A9","","24CF0A94C477417FFDA7D552E563C6D05C6D670823FA569A0D06816E6B9902C9") &&
                Pin(move,0x0600092A,488,11,0x11000151,"0B65CFB9CD013A1203431457852425D2A23A7545337CC0E29372190CF3B968D2","2:36:374:410:24:;0:14:429:443:25:System.Exception","26B026DA6754C0CCE4B054D260E47C6576A3BB9509440ACBDC9ABAA48E3E4406");
        return method.Name=="Post" && method.GetParameters().Length==1 && method.GetParameters()[0].ParameterType.FullName=="Emby.Api.Images.UpdateItemImageFromUrl" &&
            Pin(method,0x0600073B,63,2,0x110000F8,"449C539B1F8DC98346967AB573834BC61B4BFF61F5F46BEFD3E0674DD7090F1B","","95598D6A3AEA6329DD1A1DBF88747C78122AA7DADD54E5F2A9395F635DEAFDDA") &&
            Pin(move,0x06000928,368,10,0x11000150,"6E6E3CE9F5C7A836F86F8721CC46B9D2BA1001F5320629415BA651ABB2B632EC","0:14:295:309:32:System.Exception","21C621FA598B207B60C016181383275EE755A88CB9BA2F19DA546028C3E9B354");
    }
    internal static MethodInfo[] ExecutionTargets(MethodInfo target)
    {
        var worker=target.DeclaringType?.FullName==RemoteImplementation
            ? target.DeclaringType.GetMethod("DownloadRemoteImage",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly) : target;
        var move=worker?.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType.GetMethod("MoveNext",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
        if(worker==null||move==null)return Array.Empty<MethodInfo>();
        return worker==target?new[]{target,move}:new[]{target,worker,move};
    }
    private static bool VerifyRemote(MethodInfo method)
    {
        var type=method.DeclaringType;var chain=ExecutionTargets(method);
        if(type?.FullName!=RemoteImplementation||!type.IsPublic||!type.IsSealed||method.Name!="Post"||!method.IsPublic||method.IsStatic||method.ContainsGenericParameters||
            method.ReturnType!=typeof(Task)||method.GetParameters().Length!=1||method.GetParameters()[0].ParameterType.FullName!="Emby.Api.Images.DownloadRemoteImage"||
            type.Assembly.GetName().Name!="Emby.Api"||type.Assembly.GetName().Version!=new Version(4,10,2,0)||
            method.Module.ModuleVersionId!=new Guid("0873fdaf-f3f8-45f4-8d9c-b647b1edf372")||chain.Length!=3)return false;
        var worker=chain[1];var args=worker.GetParameters();var state=worker.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        return worker.IsPrivate&&!worker.IsStatic&&worker.ReturnType==typeof(Task)&&args.Length==2&&args[0].ParameterType==typeof(BaseItem)&&args[1].ParameterType.FullName=="Emby.Api.Images.BaseDownloadRemoteImage"&&
            method.GetCustomAttribute<AsyncStateMachineAttribute>()==null&&state.DeclaringType==type&&typeof(IAsyncStateMachine).IsAssignableFrom(state)&&
            Pin(method,0x0600076B,43,3,0x11000025,"0A1C71B2F6CFD9F3B9923D64307A15626C550DDEEF587D39D26062C005931D0E","","89E22A6977A786B08E4DA593EB6BAE71D513689856BEC68060258A90B3A39E5D")&&
            Pin(worker,0x0600076C,71,2,0x11000103,"7A009CBC2D6A66453860B83FE9FBE4F72F5050E06627B62EA76529FD5B6DB233","","1DD03AF00D4672922020446F67F06B82F40545BFBB1986BAF07BDEC26A73E2C6")&&
            Pin(chain[2],0x06000933,279,10,0x11000154,"CE831A025A8B09A9620A837C9E8E7050780AA3CE3D68B476AF18E1AD614E8E6E","0:14:220:234:25:System.Exception","F46500DB384F829071A90FEDF3E61B82B829045203D874C1C07480B5A38863C1");
    }
    private static bool Pin(MethodInfo method,int token,int length,int stack,int local,string localHash,string expectedEh,string hash)
    {
        var body=method.GetMethodBody();var code=body?.GetILAsByteArray();if(body==null||code==null)return false;
        var eh=string.Join(";",body.ExceptionHandlingClauses.Select(c=>$"{(int)c.Flags}:{c.TryOffset}:{c.TryLength}:{c.HandlerOffset}:{c.HandlerLength}:{(c.Flags==ExceptionHandlingClauseOptions.Clause?c.CatchType?.FullName:c.Flags==ExceptionHandlingClauseOptions.Filter?c.FilterOffset.ToString(CultureInfo.InvariantCulture):"")}"));
        return method.MetadataToken==token && body.InitLocals && body.MaxStackSize==stack && body.LocalSignatureMetadataToken==local && code.Length==length && eh==expectedEh &&
            Convert.ToHexString(SHA256.HashData(code))==hash && Convert.ToHexString(SHA256.HashData(method.Module.ResolveSignature(local)))==localHash;
    }
    public static bool Configure(Version version, bool enabled, ILibraryManager library, INotificationManager notifications, ILogger logger, IUserManager? userManager = null)
    {
        lock(Gate)
        {
            try
            {
                Remove();if(!enabled)return true;
                var targets=FindTargets();
                if(version!=new Version(4,10,2,0)||targets.Length!=3||targets.Any(m=>!Verify(m)))
                {Detail="Image upload/URL entry fingerprint differs.";return false;}
                var execution=targets.SelectMany(ExecutionTargets);
                if(execution.Any(method=>Harmony.GetPatchInfo(method)?.Owners.Any()==true))
                {Detail="Image upload/URL entry or state-machine ownership differs.";return false;}
                foreach(var target in targets)
                {
                    installed.Add(target);
                    (patcher??=new Harmony(Owner)).Patch(target,prefix:new HarmonyMethod(typeof(ImageEditNotification),target.Name=="PostImage"?nameof(BeforeUpload):nameof(BeforeUrl)),postfix:new HarmonyMethod(typeof(ImageEditNotification),nameof(After)));
                }
                Volatile.Write(ref current,new(library,notifications,userManager));Detail="Native upload/URL/remote-image-download completion; opt-in rebuild event. Provider search, delete/reorder and automatic images remain unchanged.";return true;
            }
            catch(Exception error){Volatile.Write(ref current,null);try{Remove();}catch{}Detail="Image notification inactive: "+error.GetType().Name;return false;}
            finally{try{logger.Info("[StrmAssistantRebuild] {0}",Detail);}catch{}}
        }
    }
    public static void Remove()
    {
        lock(Gate)
        {
            Volatile.Write(ref current,null);
            for(var i=installed.Count-1;i>=0;i--){patcher?.Unpatch(installed[i],HarmonyPatchType.All,Owner);installed.RemoveAt(i);}
            Detail="Disabled; native image notifications unchanged.";
        }
    }
    private static ImageState? Capture(BaseApiService service, BaseItem? item, ImageType imageType)
    {
        var settings=Volatile.Read(ref current);
        if(settings==null || item is not (Movie or Series or Season or Episode or BoxSet) || item.InternalId<=0 || !Enum.IsDefined(typeof(ImageType),imageType))return null;
        var token=service.Request.CancellationToken;if(token.IsCancellationRequested)return null;
        var actor=service.AuthorizationContext.GetAuthorizationInfo(service.Request).User;
        // The HTTP DTOs require admin. Do not notify for unrelated direct calls or avatar uploads.
        if(actor?.Policy is not {IsAdministrator:true,IsDisabled:false})return null;
        if(!settings.Library.FilterItemsToIdsForUser(new[]{item},actor,null,token).Contains(item.InternalId))return null;
        return new(settings,item.InternalId,item.GetType(),actor,actor.InternalId,imageType,token,CultureInfo.CurrentUICulture.Name);
    }
    private static void BeforeUpload(BaseApiService __instance, BaseItem __0, ImageType __2, out ImageState? __state)
    {__state=null;try{__state=Capture(__instance,__0,__2);}catch{}}
    private static void BeforeUrl(BaseApiService __instance, object __0, out ImageState? __state)
    {
        __state=null;var settings=Volatile.Read(ref current);if(settings==null||__0==null)return;
        try
        {
            var requestType=__0.GetType();var id=requestType.GetProperty("Id")?.GetValue(__0) as string;
            // URL updates and remote downloads accept the host's decimal/GUID aliases.
            var numeric=settings.Library.GetInternalId(id);
            if(numeric<=0)return;
            if(requestType.GetProperty("Type")?.GetValue(__0) is not ImageType imageType)return;
            var item=settings.Library.GetItemById(numeric);if(item?.InternalId!=numeric)return;
            __state=Capture(__instance,item,imageType);
        }
        catch{}
    }
    private static void After(ImageState? __state, ref Task __result)
    {if(__state!=null && __result!=null)__result=Complete(__result,__state);}
    private static async Task Complete(Task native, ImageState state)
    {
        // Never catch native faults/cancellation, and never observe an incomplete SaveImage as success.
        await native.ConfigureAwait(false);
        if(state.Token.IsCancellationRequested || !ReferenceEquals(state.Settings,Volatile.Read(ref current)))return;
        try
        {
            var item=state.Settings.Library.GetItemById(state.Id);
            if(item==null||item.InternalId!=state.Id||item.GetType()!=state.ItemType)return;
            var actor=NotificationActor.Current(state.Settings.Users,state.Actor,state.ActorId,requireAdmin:true);
            if(actor==null||!state.Settings.Library.FilterItemsToIdsForUser(new[]{item},actor,null,state.Token).Contains(state.Id))return;
            if(state.Token.IsCancellationRequested || !ReferenceEquals(state.Settings,Volatile.Read(ref current)))return;
            state.Settings.Notifications.SendNotification(new NotificationRequest{EventId=EventId,User=actor,Item=item,Title=Title(state.Language),Description=OriginalNotifications.ImageDescription(state.ImageType,state.Language),CancellationToken=state.Token});
        }
        catch{/* An optional notification failure must not turn a completed image save into a failed request. */}
    }
    public static string Title(string? language) => OriginalNotifications.Title(EventId, language);
}
