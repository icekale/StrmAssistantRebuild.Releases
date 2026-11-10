using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StrmAssistantRebuild.Common;

/// <summary>One reversible, fingerprint-locked public getter patch. No media state is changed.</summary>
public static class StrmThumbnailCompatibility
{
    private const string Owner = "StrmAssistantRebuild.StrmThumbnails.4.10.2";
    private const string ExpectedIlSha256 = "413AA0AAB2E13C057B3353878DE550C3AF4DEB4A76553A9F584B020C3A1A019F";
    private static readonly Guid ExpectedMvid = new("58dae430-7295-47bb-a62f-b26bf0d89604");
    private static readonly object Sync = new();
    private static Harmony? patcher;
    private static MethodInfo? installedGetter;
    private static volatile bool enabled;

    public static bool IsEnabled => enabled;

    /// <summary>Read-only support check, independent of the current option; never installs/removes a patch.</summary>
    public static bool Probe(Version hostVersion, out string detail)
    {
        try
        {
            if (hostVersion != new Version(4, 10, 2, 0)) { detail = "Host version differs from validated 4.10.2.0."; return false; }
            var getter = typeof(Video).GetProperty(nameof(Video.SupportsThumbnails), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)?.GetMethod;
            detail = "Public SupportsThumbnails getter missing.";
            if (getter == null || !VerifyGetter(getter, out detail)) return false;
            if (Harmony.GetPatchInfo(getter)?.Owners.Any(owner => owner != Owner) == true)
            { detail = "Foreign patch owns thumbnail eligibility."; return false; }
            return true;
        }
        catch (Exception ex) { detail = "Thumbnail probe failed: " + ex.GetType().Name + ": " + ex.Message; return false; }
    }

    public static bool Configure(Version hostVersion, bool enableImageCapture, out string detail)
    {
        lock (Sync)
        {
            // Switch off first, including while removing an existing detour.
            enabled = false;
            try
            {
                RemovePatch();
                if (!enableImageCapture) { detail = "Disabled; native thumbnail eligibility restored."; return true; }
                if (hostVersion.Major != 4 || hostVersion.Minor != 10 || hostVersion.Build != 2 || hostVersion.Revision > 0)
                { detail = "Host version differs from validated 4.10.2.0."; return false; }
                detail = "Validated public getter was not found.";
                var getter = typeof(Video).GetProperty(nameof(Video.SupportsThumbnails), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)?.GetMethod;
                if (getter is null || !VerifyGetter(getter, out detail)) return false;
                if (Harmony.GetPatchInfo(getter)?.Owners.Any() == true)
                { detail = "Another patch owns thumbnail eligibility; compatibility remains disabled."; return false; }
                var postfix = typeof(StrmThumbnailCompatibility).GetMethod(nameof(AllowStrm), BindingFlags.NonPublic | BindingFlags.Static)!;
                installedGetter = getter; // Keep the handle for cleanup even if Harmony throws during installation.
                (patcher ??= new Harmony(Owner)).Patch(getter, postfix: new HarmonyMethod(postfix));
                enabled = true;
                detail = "Enabled: validated 4.10.2.0 public SupportsThumbnails getter; STRM-only postfix.";
                return true;
            }
            catch (Exception error)
            {
                enabled = false;
                detail = "Thumbnail compatibility inactive: " + error.GetType().Name + ": " + error.Message;
                try { RemovePatch(); } catch (Exception cleanup) { detail += "; detour cleanup pending: " + cleanup.Message; }
                return false;
            }
        }
    }

    public static void Disable()
    {
        lock (Sync)
        {
            enabled = false;
            // Functional rollback is immediate even when the runtime detour cannot be removed.
            // Retain installedGetter on failure so a later Configure/Disable retries cleanup.
            try { RemovePatch(); } catch { }
        }
    }

    private static void RemovePatch()
    {
        if (installedGetter is null) return;
        // Deliberately target only our postfix, never UnpatchAll or another plugin's patches.
        patcher?.Unpatch(installedGetter, HarmonyPatchType.Postfix, Owner);
        installedGetter = null;
    }

    internal static bool VerifyGetter(MethodInfo getter, out string detail)
    {
        var body = getter.GetMethodBody();
        var il = body?.GetILAsByteArray();
        var assembly = getter.DeclaringType?.Assembly.GetName();
        var verified = getter.DeclaringType == typeof(Video) && getter.Name == "get_SupportsThumbnails" &&
            getter.IsPublic && !getter.IsStatic && getter.ReturnType == typeof(bool) && getter.GetParameters().Length == 0 &&
            assembly?.Name == "MediaBrowser.Controller" && assembly.Version == new Version(4, 10, 2, 0) &&
            getter.Module.ModuleVersionId == ExpectedMvid && getter.MetadataToken == 0x06001298 &&
            body is { MaxStackSize: 4, InitLocals: true, LocalSignatureMetadataToken: 0x110000DE } &&
            body.ExceptionHandlingClauses.Count == 0 && il is { Length: 109 } &&
            Convert.ToHexString(SHA256.HashData(il)) == ExpectedIlSha256;
        detail = verified ? "Getter fingerprint matched." : "Getter fingerprint differs; native eligibility retained.";
        return verified;
    }

    private static void AllowStrm(Video __instance, ref bool __result)
    {
        if (!enabled || __result || !__instance.IsShortcut) return;
        // Keep every native guard except IsShortcut. Public getters only; no Path/IsShortcut writes.
        if (__instance.PathProtocol.GetValueOrDefault() != MediaProtocol.File) return;
        var container = __instance.MediaContainer;
        if (container is MediaContainers.Iso or MediaContainers.Bluray or MediaContainers.BlurayIso or MediaContainers.Dvd or MediaContainers.DvdIso) return;
        if (__instance.ExtraType is ExtraType.Trailer or ExtraType.ThemeVideo || !__instance.IsCompleteMedia) return;
        __result = true;
    }
}
