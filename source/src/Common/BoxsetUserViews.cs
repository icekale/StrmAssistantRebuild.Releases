using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Data;
using MediaBrowser.Model.Library;

namespace StrmAssistantRebuild.Common;

/// <summary>Remove collection-library inputs before native user-view exclusions, expansion, sorting and root pagination.</summary>
public static class BoxsetUserViews
{
    private const string Owner = "StrmAssistantRebuild.BoxsetUserViews.4.10.2";
    private static readonly object Gate = new();
    private static MethodInfo? installed;
    private static Harmony? patcher;
    private static int enabled;
    public static bool IsEnabled => Volatile.Read(ref enabled) != 0;
    public static string Detail { get; private set; } = "Disabled; native user views unchanged.";
    public static bool Configure(Version hostVersion, bool hide, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref enabled, 0);
            try
            {
                Remove();
                if (!hide) { detail = Detail = "Disabled; native user views restored."; return true; }
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.Server.Implementations");
                var method = assembly?.GetType("Emby.Server.Implementations.Library.UserViewManager")?.GetMethod("GetUserViews",
                    new[] { typeof(UserViewQuery), typeof(User), typeof(Folder[]), typeof(IDataContext), typeof(CancellationToken) });
                if (hostVersion != new Version(4, 10, 2, 0) || method == null || !Verify(method))
                    throw new NotSupportedException("host file, signature, MVID, IL or locals differ");
                if (Harmony.GetPatchInfo(method)?.Owners.Any(x => x != Owner) == true)
                    throw new NotSupportedException("foreign user-view patch");
                installed = method;
                (patcher ??= new Harmony(Owner)).Patch(method, prefix: new HarmonyMethod(typeof(BoxsetUserViews).GetMethod(nameof(Before), BindingFlags.Static | BindingFlags.NonPublic)!));
                Volatile.Write(ref enabled, 1);
                detail = Detail = "Active; collection-library inputs hidden from native user views only; library administration, direct access and collection data unchanged.";
                return true;
            }
            catch (Exception error)
            {
                detail = Detail = "Inactive: " + error.Message + "; native user views retained.";
                try { Remove(); } catch { detail = Detail += " Cleanup pending; filter inactive."; }
                return false;
            }
        }
    }
    private static void Before(ref Folder[] __2, CancellationToken __4)
    {
        if (!IsEnabled || __2 == null) return;
        var current = installed;
        if (current == null || Harmony.GetPatchInfo(current)?.Owners.Any(x => x != Owner) == true)
        { Volatile.Write(ref enabled, 0); Detail = "Inactive: user-view ownership changed; native inputs retained."; return; }
        // Do not invent cancellation behavior: native body forwards the same token and decides where to observe it.
        __2 = __2.Where(folder => folder is not CollectionFolder collection ||
            !MemoryExtensions.Equals(collection.CollectionType, "boxsets".AsSpan(), StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    internal static bool Verify(MethodInfo method)
    {
        var assembly = method.Module.Assembly; var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        var type = method.DeclaringType;
        return type is { IsPublic: true, IsSealed: true } && type.FullName == "Emby.Server.Implementations.Library.UserViewManager" &&
            typeof(IUserViewManager).IsAssignableFrom(type) && method.IsPublic && method.IsFinal && method.IsVirtual && !method.IsStatic && !method.ContainsGenericParameters &&
            method.Name == "GetUserViews" && method.ReturnType == typeof(Folder[]) && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] {typeof(UserViewQuery),typeof(User),typeof(Folder[]),typeof(IDataContext),typeof(CancellationToken)}) &&
            assembly.GetName().Name == "Emby.Server.Implementations" && assembly.GetName().Version == new Version(4,10,2,0) &&
            method.Module.ModuleVersionId == new Guid("2009e43d-fa42-4e57-a902-2784a8306072") && method.MetadataToken == 0x06000BA6 && method.GetMethodImplementationFlags() == MethodImplAttributes.IL &&
            body is {InitLocals:true,MaxStackSize:4,LocalSignatureMetadataToken:0x110002F0} && body.ExceptionHandlingClauses.Count == 0 &&
            Convert.ToHexString(method.Module.ResolveSignature(body.LocalSignatureMetadataToken)) == "0704128BC8151280F101128721151280F101128859151280F101128721" &&
            body.LocalVariables.Count == 4 && body.LocalVariables.All(v => !v.IsPinned) &&
            body.LocalVariables[0].LocalType == type.GetNestedType("<>c__DisplayClass13_0",BindingFlags.NonPublic) &&
            body.LocalVariables.Skip(1).Select(v => v.LocalType).SequenceEqual(new[]{typeof(List<Folder>),typeof(List<UserView>),typeof(List<Folder>)}) &&
            il?.Length == 614 && Convert.ToHexString(SHA256.HashData(il)) == "BC63BE59DFC9A0DB61A41FC202EF808C878B13449CA40B825D2F1981D8159CF9" &&
            !string.IsNullOrEmpty(assembly.Location) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) == "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F";
    }
    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref enabled, 0);
            try { Remove(); Detail = "Disabled; native user views restored."; }
            catch { Detail = "Disabled; cleanup pending; native inputs retained."; }
        }
    }
    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed,HarmonyPatchType.Prefix,Owner);installed=null;
    }
}
