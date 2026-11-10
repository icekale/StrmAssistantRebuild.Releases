using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace StrmAssistantRebuild.Common;

/// <summary>Skips only the native detail-request refresh for a Person with a registered primary image.</summary>
public static class PersonDetailRefreshControl
{
    private const string Owner = "StrmAssistantRebuild.PersonDetailRefresh.4.10.2";
    private const string ServiceName = "Emby.Api.UserLibrary.UserLibraryService";
    private static readonly Version SupportedVersion = new(4, 10, 2, 0);
    private static readonly object Gate = new();
    private static Harmony? patcher;
    private static MethodInfo? installed;
    private static volatile bool enabled;
    public static bool IsEnabled => enabled;
    public static string Detail { get; private set; } = "Disabled; native Person detail refresh unchanged.";

    public static bool Configure(Version hostVersion, bool enable, out string detail)
    {
        lock (Gate)
        {
            enabled = false;
            try
            {
                Remove();
                if (!enable)
                { detail = Detail = "Disabled; native Person detail refresh restored."; return true; }
                var api = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Emby.Api");
                var target = api?.GetType(ServiceName)?.GetMethod("RefreshItemOnDemandIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(BaseItem), typeof(DtoOptions), typeof(CancellationToken) }, null);
                if (hostVersion != SupportedVersion || target == null || !Verify(target))
                { detail = Detail = "Inactive: exact native detail-refresh file/MVID/signature/token/IL differs; native behavior retained."; return false; }
                if (ForeignOwner(target))
                { detail = Detail = "Inactive: foreign detail-refresh patch; native behavior retained."; return false; }
                installed = target; // Keep removal handle if installation throws partway through.
                (patcher ??= new Harmony(Owner)).Patch(target, prefix: new HarmonyMethod(typeof(PersonDetailRefreshControl)
                    .GetMethod(nameof(BeforeDetailRefresh), BindingFlags.Static | BindingFlags.NonPublic)!));
                enabled = true;
                detail = Detail = "Enabled: Person with registered Primary image skips detail-request on-demand metadata/image refresh only; manual/scheduled refresh and remote image picker unchanged. Pre-cancelled shortcut remains cancelled.";
                return true;
            }
            catch (Exception error)
            {
                enabled = false;
                Detail = "Inactive: detail-refresh installation failed (" + error.GetType().Name + "); native behavior retained.";
                try { Remove(); } catch { Detail += " Owned cleanup pending; guard inactive."; }
                detail = Detail;
                return false;
            }
        }
    }

    private static bool BeforeDetailRefresh(BaseItem item, CancellationToken cancellationToken, ref Task __result)
    {
        lock (Gate)
        {
            if (!enabled || installed == null) return true;
            try
            {
                if (ForeignOwner(installed))
                {
                    enabled = false;
                    Detail = "Inactive: foreign detail-refresh patch detected; native behavior retained.";
                    return true;
                }
            }
            catch
            {
                enabled = false;
                Detail = "Inactive: detail-refresh ownership observation failed; native behavior retained.";
                return true;
            }
            if (item is not Person || !item.HasImage(ImageType.Primary)) return true;
            // Original uses registered image presence, not file existence, URL provenance, TMDB identity or image freshness.
            // Reconstruction difference: do not turn an already-cancelled request into successful completion.
            __result = cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;
            return false;
        }
    }

    private static bool ForeignOwner(MethodInfo method) => Harmony.GetPatchInfo(method)?.Owners.Any(o => o != Owner) == true;

    internal static bool Verify(MethodInfo method)
    {
        if (!Pin(method.Module.Assembly, "Emby.Api", "0873fdaf-f3f8-45f4-8d9c-b647b1edf372", "2D5F2B4CB92DE964B478A3C50787515AEFB750C0041BC019E6BCCDFDF231AD4C") ||
            !Pin(typeof(BaseItem).Assembly, "MediaBrowser.Controller", "58dae430-7295-47bb-a62f-b26bf0d89604", "EAA6B9EDEBB21FE3AF9C272DEF8C913D9EC6B177B552E1B789FEE15CB59289DA") ||
            !Pin(typeof(ImageType).Assembly, "MediaBrowser.Model", "3d56dcbb-6440-4e93-a3ad-05e588af05c2", "2B7DA29239811F8C035784DF7B3F48A1668E9537784B6524DDE43B3905572617")) return false;
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        return method.DeclaringType is { IsPublic: true, IsSealed: true } && method.DeclaringType.FullName == ServiceName &&
            method.Name == "RefreshItemOnDemandIfNeeded" && method.IsPrivate && !method.IsStatic && !method.IsVirtual &&
            !method.ContainsGenericParameters && method.ReturnType == typeof(Task) &&
            method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(BaseItem), typeof(DtoOptions), typeof(CancellationToken) }) &&
            method.MetadataToken == 0x060003DF && method.GetMethodImplementationFlags() == MethodImplAttributes.IL &&
            body is { InitLocals: true, MaxStackSize: 3, LocalSignatureMetadataToken: 0x11000094 } &&
            body.ExceptionHandlingClauses.Count == 0 && body.LocalVariables.All(v => !v.IsPinned) &&
            body.LocalVariables.Select(v => v.LocalType).SequenceEqual(new[] { typeof(double), typeof(TimeSpan), typeof(MetadataRefreshOptions) }) &&
            il?.Length == 148 && Convert.ToHexString(SHA256.HashData(il)) == "6BF2234017E77F7A180D95A223C72927AF37D91BEF7D4C0D8C172E057E0660E9";
    }

    private static bool Pin(Assembly assembly, string name, string mvid, string hash)
    {
        if (assembly.GetName().Name != name || assembly.GetName().Version != SupportedVersion ||
            assembly.ManifestModule.ModuleVersionId != Guid.Parse(mvid) || string.IsNullOrEmpty(assembly.Location)) return false;
        using var file = File.OpenRead(assembly.Location);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(file)) == hash;
    }

    public static void Disable()
    {
        lock (Gate)
        {
            enabled = false;
            try { Remove(); Detail = "Disabled; native Person detail refresh restored."; }
            catch { Detail = "Disabled; owned cleanup pending; guard inactive."; }
        }
    }

    private static void Remove()
    {
        if (installed == null) return;
        patcher?.Unpatch(installed, HarmonyPatchType.Prefix, Owner);
        installed = null;
    }
}
