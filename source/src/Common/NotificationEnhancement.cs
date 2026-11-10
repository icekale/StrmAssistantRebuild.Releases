using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Emby.Notifications;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;

/// <summary>Partial enhancement: recipient-filtered episode ranges on existing grouped notifications only.</summary>
public static class NotificationEnhancement
{
    private const string Owner = "StrmAssistantRebuild.NotificationEpisodes.4.10.2";
    private const string Manager = "Emby.Notifications.NotificationManager";
    private const string Queue = "Emby.Notifications.NotificationQueueManager";
    private const string HostSha = "86F3962F83D125216F7F6C715A1336FEAD9E96D57A0EBF43D6D255CCCA2FAE59";
    private static readonly object Gate = new();
    // All three pinned batch methods and enqueue are synchronous void. Do not flow a batch into the queue's Task.Run.
    [ThreadStatic] private static Batch? current;
    private static readonly List<MethodInfo> Installed = new();
    private static readonly Func<object, object> Copy = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Func<object, object>>();
    private sealed record Settings(IUserManager Users, ILibraryManager Library);
    private sealed record EpisodeEntry(BaseItem Item, long Series, int? Season, int? Episode, string[] Libraries);
    private sealed record Batch(Settings Settings, string Event, EpisodeEntry[] Entries);
    private static Settings? settings;
    private static Harmony? patcher;
    public static bool IsEnabled => Volatile.Read(ref settings) != null;
    public static string Detail { get; private set; } = "Disabled; native grouped notifications unchanged.";

    // Pin all patched methods AND the native recipient/configuration boundary; no dependency on names alone.
    private sealed record Pin(string Type, string Name, int Token, Type[] Args, Type Return, bool Public,
        int Length, int Stack, int Local, string LocalSha, string Eh, string IlSha);
    private static readonly Pin[] Pins = {
        new(Manager, "SendItemAddedNotifications", 0x0600002E, new[]{typeof(ItemChangeEventArgs[])}, typeof(void), false, 600,9,0x11000015,"1A0ABAA7B6225589B057F6AEB20800E4DDCCB1B567C0A5B0CC65B3FD7998E2AA","2:323:204:527:14:;2:65:507:572:14:","1B84BE613AF5AE0AFC229600341C145073FB5C3C1E92EE922EAB5B778A8E18DF"),
        new(Manager, "SendItemUpdatedNotifications", 0x06000030, new[]{typeof(ItemChangeEventArgs[])}, typeof(void), false, 543,9,0x11000017,"F680CF16053B7619F96AD356917F995B92E51B9188C8CDCAFD0CA8A111FB1D47","2:323:147:470:14:;2:65:450:515:14:","73A9A1A018F7750D82B9ED4251275072A9D80FED51878F40B3E48A6876DB95B6"),
        new(Manager, "SendItemRemovedNotifications", 0x06000035, new[]{typeof(ItemChangeEventArgs[])}, typeof(void), false, 553,10,0x11000017,"F680CF16053B7619F96AD356917F995B92E51B9188C8CDCAFD0CA8A111FB1D47","2:328:152:480:14:;2:65:460:525:14:","2B5EB0FBBB6DC5E93D7C888938A2A42E18F54E050C10E504418BA2A17BA38F3F"),
        new(Queue, "QueueNotification", 0x06000063, new[]{typeof(INotifier),typeof(InternalNotificationRequest),typeof(int)}, typeof(void), true, 103,4,0x11000019,"0DBDA4FBADE7EFD46565EFCEE084E0CB6DC4B78B39172585BFE7AF8AF61A6786","2:18:74:92:10:","281229E81ED338A47EAF952F84A2F2AA8993D0125059BF9D4FFB9ED8150463ED"),
        new(Manager, "SendNotification", 0x06000058, new[]{typeof(INotifier),typeof(NotificationInfo[]),typeof(NotificationRequest),typeof(bool)}, typeof(void), false, 1346,6,0x11000027,"79BC2C27B764055420CE85A7A2E91936687B70A85EA2FCC85FF4C86B6BCE9417","","5763DC4EAE3D907200CA058E8011A1633BC0018814706DDF0C4BA3F56FCB2B40"),
        new(Manager, "GetConfiguredNotifications", 0x0600003A, new[]{typeof(INotifier),typeof(string)}, typeof(NotificationInfo[]), false, 43,3,0x1100001C,"820AF9CB0800742FE74DB518C831ECCD79AEB3F81BEFFFB9350932920E0A1478","","39DBEAD53F21B332DD46FE498360546C326191F8366644C650D49F3FBEA19D7D")
    };

    internal static MethodInfo[] FindTargets()
    {
        var host = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Emby.Notifications");
        if (host == null) return Array.Empty<MethodInfo>();
        return Pins.Select(p => host.GetType(p.Type)?.GetMethod(p.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, p.Args, null)).Where(m => m != null).Cast<MethodInfo>().ToArray();
    }

    internal static bool VerifyTarget(MethodInfo method)
    {
        var p = Pins.FirstOrDefault(p => p.Token == method.MetadataToken && p.Type == method.DeclaringType?.FullName);
        var b = method.GetMethodBody(); var il = b?.GetILAsByteArray();
        if (p == null || b == null || il == null) return false;
        var eh = string.Join(";", b.ExceptionHandlingClauses.Select(c => $"{(int)c.Flags}:{c.TryOffset}:{c.TryLength}:{c.HandlerOffset}:{c.HandlerLength}:{(c.Flags == ExceptionHandlingClauseOptions.Clause ? c.CatchType?.FullName : c.Flags == ExceptionHandlingClauseOptions.Filter ? c.FilterOffset.ToString(CultureInfo.InvariantCulture) : "")}"));
        return method.Name == p.Name && method.ReturnType == p.Return && method.IsPublic == p.Public && !method.IsStatic && !method.ContainsGenericParameters &&
            method.GetParameters().Select(x => x.ParameterType).SequenceEqual(p.Args) &&
            method.Module.ModuleVersionId == new Guid("1fcc7aa8-56c2-41ca-acb0-04043e1d4ea9") &&
            method.Module.Assembly.GetName().Version == new Version(4,10,2,0) &&
            b.InitLocals && b.MaxStackSize == p.Stack && b.LocalSignatureMetadataToken == p.Local && eh == p.Eh &&
            Convert.ToHexString(SHA256.HashData(method.Module.ResolveSignature(p.Local))) == p.LocalSha &&
            il.Length == p.Length && Convert.ToHexString(SHA256.HashData(il)) == p.IlSha;
    }

    public static bool Probe(Version hostVersion, out string detail)
    {
        try
        {
            var targets = FindTargets();
            if (hostVersion != new Version(4,10,2,0) || targets.Length != Pins.Length || targets.Any(m => !VerifyTarget(m)))
            { detail = "Notification 4.10.2 signature/IL/EH/locals/MVID mismatch or not loaded."; return false; }
            var location = targets[0].Module.Assembly.Location;
            if (string.IsNullOrEmpty(location) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location))) != HostSha)
            { detail = "Notification host file SHA differs or no file identity."; return false; }
            if (targets.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(o => o != Owner) == true))
            { detail = "Notification boundary has a foreign patch owner."; return false; }
            detail = "Pinned native grouped notification and recipient boundary matched; partial episode-description scope.";
            return true;
        }
        catch (Exception e) { detail = "Notification probe: " + e.GetType().Name; return false; }
    }

    public static bool Configure(Version hostVersion, bool enabled, IUserManager users, ILibraryManager library, ILogger logger)
    {
        lock (Gate)
        {
            try
            {
                Remove();
                if (!enabled) return true;
                if (!Probe(hostVersion, out var detail)) { Detail = detail; return false; }
                foreach (var target in FindTargets().Take(4))
                {
                    Installed.Add(target);
                    (patcher ??= new Harmony(Owner)).Patch(target,
                        prefix: new HarmonyMethod(typeof(NotificationEnhancement), target.Name == "QueueNotification" ? nameof(ProjectQueue) : nameof(EnterBatch)),
                        finalizer: target.Name == "QueueNotification" ? null : new HarmonyMethod(typeof(NotificationEnhancement), nameof(ExitBatch)));
                }
                Volatile.Write(ref settings, new(users, library));
                Detail = "Partial: existing grouped series episode description, self-recipient only; native transport/auth retained. Metadata/image/delete changes excluded.";
                return true;
            }
            catch (Exception e)
            {
                Volatile.Write(ref settings, null);
                try { Remove(); } catch { }
                Detail = "Notification adapter inactive: " + e.GetType().Name;
                return false;
            }
            finally { try { logger.Info("[StrmAssistantRebuild] {0}", Detail); } catch { } }
        }
    }

    public static void Remove()
    {
        lock (Gate)
        {
            Volatile.Write(ref settings, null);
            for (var i = Installed.Count - 1; i >= 0; i--)
            { patcher?.Unpatch(Installed[i], HarmonyPatchType.All, Owner); Installed.RemoveAt(i); }
            current = null;
            Detail = "Disabled; original native notifications restored.";
        }
    }

    private static void EnterBatch(ItemChangeEventArgs[] list, MethodBase __originalMethod, out Batch? __state)
    {
        __state = current;
        current = null;
        var s = Volatile.Read(ref settings);
        if (s == null || list == null || list.Length > 2048) return;
        try
        {
            var entries = list.Where(e => e.Item is Episode && e.Item.SeriesId != 0).Select(e => new EpisodeEntry(e.Item,
                e.Item.SeriesId, e.Item.ParentIndexNumber, e.Item.IndexNumber,
                e.CollectionFolders?.Select(f => f.IdString).ToArray() ?? Array.Empty<string>())).ToArray();
            current = new(s, __originalMethod.Name switch { "SendItemAddedNotifications" => "library.new", "SendItemUpdatedNotifications" => "library.updated", _ => "library.deleted" }, entries);
        }
        catch { current = null; } // Projection failure leaves the original batch untouched.
    }

    private static void ExitBatch(Batch? __state) => current = __state;

    private static void ProjectQueue(ref InternalNotificationRequest request)
    {
        if (request == null) return;
        var batch = current;
        var config = request.Configuration;
        if (batch == null || !ReferenceEquals(batch.Settings, Volatile.Read(ref settings)) ||
            request.CancellationToken.IsCancellationRequested || config?.Enabled != true || !config.GroupItems ||
            !config.IsSelfNotification || string.IsNullOrEmpty(config.UserId) ||
            !string.Equals(batch.Event, request.Event, StringComparison.Ordinal) || request.OriginalItem is not Series series) return;
        try
        {
            var user = batch.Settings.Users.GetUserById(config.UserId);
            if (user == null) return;
            var entries = batch.Entries.Where(e => e.Series == series.InternalId && e.Season is >= 0 && e.Episode is >= 0 &&
                (config.LibraryIds.Length == 0 || e.Libraries.Any(id => config.LibraryIds.Contains(id, StringComparer.OrdinalIgnoreCase)))).ToArray();
            if (entries.Length == 0) return;
            var visible = batch.Settings.Library.FilterItemsToIdsForUser(entries.Select(e => e.Item).ToArray(), user, null, request.CancellationToken).ToHashSet();
            var description = FormatEpisodes(entries.Where(e => visible.Contains(e.Item.InternalId)).Select(e => (e.Season!.Value, e.Episode!.Value)));
            if (description.Length == 0 || description.Length + (request.Description?.Length ?? 0) + Environment.NewLine.Length > 4096 || request.CancellationToken.IsCancellationRequested || !ReferenceEquals(batch.Settings, Volatile.Read(ref settings))) return;
            var copy = (InternalNotificationRequest)Copy(request);
            copy.Description = string.IsNullOrEmpty(request.Description) ? description : request.Description + Environment.NewLine + description;
            request = copy;
        }
        catch { /* Native send/auth/cancellation remains authoritative; no extra event or lookup fallback. */ }
    }

    internal static string FormatEpisodes(IEnumerable<(int Season, int Episode)> episodes)
    {
        var seasons = new List<string>();
        foreach (var group in episodes.GroupBy(e => e.Season).OrderBy(g => g.Key))
        {
            var numbers = group.Select(e => e.Episode).Distinct().OrderBy(e => e).ToArray();
            var ranges = new List<string>(); var start = numbers[0]; var end = start;
            foreach (var n in numbers.Skip(1))
            {
                if ((long)n != (long)end + 1) { ranges.Add(Range(start, end)); start = n; }
                end = n;
            }
            ranges.Add(Range(start, end));
            seasons.Add("S" + group.Key.ToString("D2", CultureInfo.InvariantCulture) + " " + string.Join(", ", ranges));
        }
        return string.Join(" / ", seasons);
    }
    private static string Range(int start, int end) => "E" + start.ToString("D2", CultureInfo.InvariantCulture) + (start == end ? "" : "-E" + end.ToString("D2", CultureInfo.InvariantCulture));
}
