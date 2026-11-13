using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

namespace StrmAssistantRebuild.Common;

/// <summary>
/// M165: exclusive-mode patches are installed only when the mode is switched on, possibly long after Emby started. All of
/// their pinned 4.10.2 targets are small methods without exception handling, so .NET 8 tier-1 recompilation (with dynamic PGO
/// guarded devirtualization for interface calls) may have already inlined the original bodies into hot host callers; such
/// callers never reach the later Harmony detour until Emby restarts. Observed on the M164 host: after a few library refreshes
/// with exclusive mode off, every manual refresh bypassed the RefreshSingleItem prefix (native refresh entries=0/0), so native
/// ffprobe lost its refresh context. At plugin construction (before the host's refresh callers become hot) this marks the
/// same fingerprint-verified targets as not inlinable, without patching them, so a later detour is always reached.
/// </summary>
public static class NativeInlineGuard
{
    private static readonly object Gate = new();
    private static readonly HashSet<MethodBase> Guarded = new();
    private static Func<MethodBase, bool>? disable;
    public static int Count { get { lock (Gate) return Guarded.Count; } }
    public static string Detail { get; private set; } = "Not applied.";

    /// <summary>Exclusive-mode targets on the pinned 4.10.2 host: saver 2, remote fetcher 2, chapter images 1, Movie ffprobe 1, extraction 8.</summary>
    public const int ExpectedExclusive = 14;
    /// <summary>All guarded targets: the exclusive ones plus <see cref="Known"/>.</summary>
    public static int Expected => ExpectedExclusive + Known.Length;

    internal sealed record Target(string Assembly, string Mvid, int Token, int Length, string Hash);
    /// <summary>M166: every other method this plugin patches on the pinned host whose body can be inlined (no exception handling,
    /// at most 1,024 IL bytes), as enumerated on the 4.10.2 test host with all features enabled in two configurations
    /// (<see cref="Coverage"/>). Bound by assembly MVID, token and IL digest; an entry that does not match exactly is skipped.</summary>
    internal static readonly Target[] Known =
    {
        new("Emby.Api", "0873fdaf-f3f8-45f4-8d9c-b647b1edf372", 0x060000C3, 97, "92FFE6501A6C8B58C1A6BE8A75BACDCAD0D60A6D03866DE9476B6A5931D4BBB7"), // Emby.Api.ItemRefreshService.GetRefreshOptions
        new("Emby.Api", "0873fdaf-f3f8-45f4-8d9c-b647b1edf372", 0x060000CC, 441, "653F0EF21D8595E06485A0618AFF5EF6DEDC56AC2C5ECE82CC1CDD0CDEFD2F1B"), // Emby.Api.ItemUpdateService.Post
        new("Emby.Api", "0873fdaf-f3f8-45f4-8d9c-b647b1edf372", 0x060003DF, 148, "6BF2234017E77F7A180D95A223C72927AF37D91BEF7D4C0D8C172E057E0660E9"), // Emby.Api.UserLibrary.UserLibraryService.RefreshItemOnDemandIfNeeded
        new("Emby.Api", "0873fdaf-f3f8-45f4-8d9c-b647b1edf372", 0x0600073B, 63, "95598D6A3AEA6329DD1A1DBF88747C78122AA7DADD54E5F2A9395F635DEAFDDA"), // Emby.Api.Images.ImageService.Post
        new("Emby.Api", "0873fdaf-f3f8-45f4-8d9c-b647b1edf372", 0x06000744, 97, "24CF0A94C477417FFDA7D552E563C6D05C6D670823FA569A0D06816E6B9902C9"), // Emby.Api.Images.ImageService.PostImage
        new("Emby.Api", "0873fdaf-f3f8-45f4-8d9c-b647b1edf372", 0x0600076B, 43, "89E22A6977A786B08E4DA593EB6BAE71D513689856BEC68060258A90B3A39E5D"), // Emby.Api.Images.RemoteImageService.Post
        new("Emby.Providers", "29b37ec2-5f02-443c-a995-1f61c71f43c6", 0x060001F1, 142, "4B86BF98E64BF0581630A6D98225B2298190B49C9D5484D85DDDDBB4DFBF6127"), // Emby.Providers.Manager.ItemImageProvider.RefreshFromProvider
        new("Emby.Providers", "29b37ec2-5f02-443c-a995-1f61c71f43c6", 0x06000243, 29, "57B6F4749754D885AC16CF82C24F9A2CA23CE72287B2544B5D3EE3F5D0637152"), // Emby.Providers.Manager.ProviderManager.GetAvailableRemoteImages
        new("Emby.Providers", "29b37ec2-5f02-443c-a995-1f61c71f43c6", 0x06000247, 97, "15679123391C547DCA4FABEF6A3D1C69A86FE55C22C1FAB4E4EB1FC902CF9BF2"), // Emby.Providers.Manager.ProviderManager.GetAvailableRemoteImages
        new("Emby.Providers", "29b37ec2-5f02-443c-a995-1f61c71f43c6", 0x0600028D, 78, "C9F0CEC8712A1932392C60F95CD15BC32113E6462288C5C6C7A86E206B680C6A"), // Emby.Providers.Manager.ProviderManager.LogRefreshItemComplete
        new("Emby.Providers", "29b37ec2-5f02-443c-a995-1f61c71f43c6", 0x0600028E, 79, "6A54A72466850EA26612CE20E2BEE12CEE77004D7EB0132581BD534C6B2640C8"), // Emby.Providers.Manager.ProviderManager.RefreshItem
        new("Emby.Server.Implementations", "2009e43d-fa42-4e57-a902-2784a8306072", 0x0600052A, 79, "E076B03877AF3E2E25A58ADC31ADB891B4DCD110AE7512D5FB2AD72BF3F0AD2C"), // Emby.Server.Implementations.Updates.InstallationManager.GetAvailablePluginUpdates
        new("Emby.Server.Implementations", "2009e43d-fa42-4e57-a902-2784a8306072", 0x06000535, 163, "8CE9BB1C1D992C5BD0D80209D35A4586BF2CD4F8A7F529F5366419553E143629"), // Emby.Server.Implementations.Udp.UdpServer.SendMessage
        new("Emby.Server.Implementations", "2009e43d-fa42-4e57-a902-2784a8306072", 0x06000977, 381, "D2355227466BA1AF72F918516FB347A759D1540FA05007BA6315F2B0EED52244"), // Emby.Server.Implementations.Localization.AlphanumComparator.CompareValues
        new("Emby.Server.Implementations", "2009e43d-fa42-4e57-a902-2784a8306072", 0x06000BA6, 614, "BC63BE59DFC9A0DB61A41FC202EF808C878B13449CA40B825D2F1981D8159CF9"), // Emby.Server.Implementations.Library.UserViewManager.GetUserViews
        new("Emby.Server.Implementations", "2009e43d-fa42-4e57-a902-2784a8306072", 0x06000F1D, 221, "8D0F5CFCD2B93299A2A3124BC0FD33A5D8B8E5EA97538D9C3D4DE23EF10EBF6B"), // Emby.Server.Implementations.Dto.DtoService.GetBaseItemDtos
        new("Emby.Server.Implementations", "2009e43d-fa42-4e57-a902-2784a8306072", 0x06000F1F, 141, "89A9DF2FE488D4BB6650843BE3599E7FB3B1F11BF13BCD7C74454E49A02510FF"), // Emby.Server.Implementations.Dto.DtoService.GetBaseItemDto
        new("Emby.Server.Implementations", "2009e43d-fa42-4e57-a902-2784a8306072", 0x0600102C, 79, "529C190A4E845BD0BC96C21890F2083D1BAE7F93745D1B71180FCB0872FC4E77"), // Emby.Server.Implementations.Data.SqliteItemRepository.GetOrderByBeforeGroupingIntoCollections
        new("Emby.Server.Implementations", "2009e43d-fa42-4e57-a902-2784a8306072", 0x0600111D, 98, "F061DF82BBD85D5F9FF330EBFC95CEBACDE45A2F6212287187EC32964180020C"), // Emby.Server.Implementations.Collections.CollectionLibraryPostScanTask.Run
        new("MediaBrowser.Controller", "58dae430-7295-47bb-a62f-b26bf0d89604", 0x0600128B, 33, "00EFFB7D4AD7AEB2FC01B0FEE808AE4B72C4DF919B60EAFF6871E7C21CDC5562"), // MediaBrowser.Controller.Entities.Video.GetUserDataKeyInternal
        new("MediaBrowser.Controller", "58dae430-7295-47bb-a62f-b26bf0d89604", 0x06001298, 109, "413AA0AAB2E13C057B3353878DE550C3AF4DEB4A76553A9F584B020C3A1A019F"), // MediaBrowser.Controller.Entities.Video.get_SupportsThumbnails
        new("MediaBrowser.Controller", "58dae430-7295-47bb-a62f-b26bf0d89604", 0x060012C0, 145, "E7EFBD400B99D43DD7F3230CD73A76D0D59B9E9B90F474AD3C7F5B158F85AC16"), // MediaBrowser.Controller.Entities.TV.Episode.GetUserDataKeyInternal
        new("MediaBrowser.Controller", "58dae430-7295-47bb-a62f-b26bf0d89604", 0x0600131D, 105, "8C57EE31009248F0831C17C966D011354E04C73C0E83066960A99F0E8FC76436"), // MediaBrowser.Controller.Entities.TV.Series.CreatePresentationUniqueKey
        new("MediaBrowser.Controller", "58dae430-7295-47bb-a62f-b26bf0d89604", 0x0600131F, 72, "18CCA621CB3945C32989BD651619FF170281A81728A00F3098037EC19F48B5AB"), // MediaBrowser.Controller.Entities.TV.Series.GetUserDataKeyInternal
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x060000C5, 79, "0D5BD06D6A36BDD59B282C0C795A6772714599F6FD25A12206913336522DEF89"), // MovieDb.MovieDbBoxSetProvider.EnsureInfo
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x060000DB, 72, "D80FDBD2E3071A5EF68C32DE5C0057845A1B348E16AD683E0D807D0FBB87342A"), // MovieDb.MovieDbImageProvider.GetMovieInfo
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x06000104, 80, "D4F5B2C711F8518DB62E33755466E777D64C659399B645F940C5575804925BAC"), // MovieDb.MovieDbProvider.FetchMainResult
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x0600010D, 88, "CBDE8E47633434201463E9D879B2B9AC4775EDF01A936B87516EB900273FA3EA"), // MovieDb.MovieDbProviderBase.FetchMainResult
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x0600010E, 19, "96D19827F977544067E476CEA07981CFFA199EC897524D972BAE833808F2BEF4"), // MovieDb.MovieDbProviderBase.DownloadEpisodeInfo
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x0600010F, 106, "A094ABE693818D30418F7E4CF0BAD4ACAF9EA0F863543751378E77F11BECB380"), // MovieDb.MovieDbProviderBase.FetchMainResult
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x0600011A, 63, "DB72E5A88550E52527FA386EFCCA930D1C305E69A5BBE9D673FA17FA6B40D422"), // MovieDb.MovieDbProviderBase.GetMovieDbResponse
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x06000120, 130, "262885E3697049E2DEF6F4EC37960A7D81B6250D6846E20994DA55C30B0EF1DF"), // MovieDb.MovieDbProviderBase.GetBackdrops
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x06000136, 71, "A3CB96D6A6B0B3D5DF9EBA7240F3064CF279F852F0483465576B533F8C2EA8A6"), // MovieDb.MovieDbSeasonProvider.GetMetadata
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x06000138, 217, "379CB553038474141CEEDA879657F2C4A14FB042139B3BF0DE9D5BD9888322DB"), // MovieDb.MovieDbSeasonProvider.ImportData
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x0600013F, 71, "8820CA7B7EA1EE52C64024164392B31563E760455FCE5C917F633AAB1E6CE309"), // MovieDb.MovieDbSeriesImageProvider.GetImages
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x0600014C, 71, "B44EB8ABA3ECBEF9A1DE51663B478439A0D05459AC3113EA804DE6FF521DFB2C"), // MovieDb.MovieDbSeriesProvider.GetAllEpisodes
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x06000150, 79, "29FED7F65FA79648FAE8103DD2ED04593E0F3D24D59FBD40B41F2DBCF338BB85"), // MovieDb.MovieDbSeriesProvider.FetchMainResult
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x060001DF, 156, "6140C644701FF46219D905540D2708B179C0E2CF9556D736696E1A1D8E1677CC"), // MovieDb.MovieDbImageProvider+<>c__DisplayClass3_0.<GetImages>b__3
        new("MovieDb", "bf190786-42b7-4923-aea6-1851feea4259", 0x06000375, 156, "7BC844940C79A2013524C4799EB23E439BE863FFD4FD08BE700CF19AE0A632F2"), // MovieDb.MovieDbSeriesImageProvider+<>c__DisplayClass3_0.<GetImages>b__3
        new("OpenSubtitles", "9748e51c-a9ed-4618-8ae7-644ab2bdcf3c", 0x06000026, 79, "0FE862A4753DACD1FDA6438F7B6558439CB8A02FEB6022B56D19527E55DB8DF1"), // OpenSubtitles.OpenSubtitleComDownloader.Search
        new("Tvdb", "7245186e-d86d-4385-8dfb-3582ab8e1c2a", 0x0600035D, 71, "F6CEA5D6A54500EAF8DD845BD6DD0ABBD35C6335EBC3A2377A1E3D289D6FFEB4"), // Tvdb.TvdbMovieImageProvider.GetImages
        new("Tvdb", "7245186e-d86d-4385-8dfb-3582ab8e1c2a", 0x06000393, 71, "01EEF5D7A5845A81FFC4EDD9909855EA7C74E70EB9F53A2CCDFA47E75E1F92EB"), // Tvdb.TvdbSeriesImageProvider.GetImages
    };

    public static void Apply(Version hostVersion)
    {
        lock (Gate)
        {
            try
            {
                if (hostVersion != new Version(4, 10, 2, 0)) { Detail = "Inactive: requires exact host 4.10.2.0."; return; }
                if (Disabler() is null) { Detail = "Inactive: runtime cannot disable inlining."; return; }
                var targets = Targets(out var missing);
                foreach (var target in targets) Mark(target);
                exclusiveMissing = missing;
                if (!subscribed) { AppDomain.CurrentDomain.AssemblyLoad += (_, e) => { lock (Gate) { MarkKnown(e.LoadedAssembly); Describe(); } }; subscribed = true; }
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) MarkKnown(assembly);
                Describe();
            }
            catch (Exception error) { Detail = "Inactive: " + error.GetType().Name + ": " + error.Message; }
        }
    }

    private static bool subscribed;
    private static int failed, exclusive, known;
    private static List<string> exclusiveMissing = new();
    private static readonly HashSet<Target> Marked = new();

    private static void Mark(MethodBase method, bool isKnown = false)
    {
        if (Guarded.Contains(method)) return;
        if (TryDisable(method)) { Guarded.Add(method); if (isKnown) known++; else exclusive++; } else failed++;
    }

    /// <summary>Marks the <see cref="Known"/> entries of one loaded assembly (exact name, MVID, token, IL length and digest).</summary>
    private static void MarkKnown(Assembly assembly)
    {
        try
        {
            if (assembly.IsDynamic) return;
            var name = assembly.GetName().Name;
            foreach (var entry in Known)
            {
                if (entry.Assembly != name || Marked.Contains(entry) || assembly.ManifestModule.ModuleVersionId != Guid.Parse(entry.Mvid)) continue;
                var method = assembly.ManifestModule.ResolveMethod(entry.Token);
                var il = method?.GetMethodBody()?.GetILAsByteArray();
                if (method == null || il?.Length != entry.Length || Convert.ToHexString(SHA256.HashData(il)) != entry.Hash) continue;
                Marked.Add(entry);
                Mark(method, true);
            }
        }
        catch (Exception) { /* An unexpected assembly is simply not guarded. */ }
    }

    private static void Describe() =>
        Detail = $"{exclusive + known}/{Expected} patch targets not inlinable (exclusive {exclusive}/{ExpectedExclusive}, other features {known}/{Known.Length})" +
            (exclusiveMissing.Count > 0 ? "; unverified: " + string.Join(",", exclusiveMissing) : "") + (failed > 0 ? "; failed=" + failed : "") + ".";

    /// <summary>The same targets the exclusive controls patch, each with the control's own pin check.</summary>
    internal static List<MethodInfo> Targets(out List<string> missing)
    {
        var absent = missing = new List<string>();
        var result = new List<MethodInfo>();
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var providers = assemblies.SingleOrDefault(a => a.GetName().Name == "Emby.Providers");
        var encoding = assemblies.SingleOrDefault(a => a.GetName().Name == "Emby.Server.MediaEncoding");
        void Add(string name, Func<IEnumerable<MethodInfo>> source, Func<MethodInfo, bool> verify)
        {
            try
            {
                var found = source().ToArray();
                if (found.Length == 0 || found.Any(m => m == null || !verify(m))) { absent.Add(name); return; }
                result.AddRange(found);
            }
            catch (Exception) { absent.Add(name); }
        }
        if (providers == null) missing.Add("Emby.Providers");
        else
        {
            Add("saver", () => ExclusiveSaverControl.GetTargets(providers).Take(2), ExclusiveSaverControl.Verify);
            Add("remote", () => ExclusiveRemoteFetcherControl.GetTargets(providers), ExclusiveRemoteFetcherControl.Verify);
            Add("chapter", () => ExclusiveChapterImageControl.Target(providers) is { } m ? new[] { m } : Array.Empty<MethodInfo>(), ExclusiveChapterImageControl.Verify);
            Add("ffprobe", () => NativeChangedProbeControl.Target(providers) is { } m ? new[] { m } : Array.Empty<MethodInfo>(), NativeChangedProbeControl.VerifyTarget);
        }
        if (encoding == null) missing.Add("Emby.Server.MediaEncoding");
        else Add("extraction", () => ExclusiveMediaControl.GetTargets(encoding), m =>
        {
            var body = m.GetMethodBody();
            return body != null && ExclusiveMediaControl.VerifyFingerprint(m, body.GetILAsByteArray(), body.MaxStackSize, body.InitLocals,
                body.LocalSignatureMetadataToken, body.ExceptionHandlingClauses.Count);
        });
        return result;
    }

    private const string OwnerPrefix = "StrmAssistantRebuild.";
    /// <summary>Largest IL body .NET 8 may inline at a profiled hot call site (JitExtDefaultPolicyMaxILProf).</summary>
    private const int MaxInlineIl = 0x400;

    /// <summary>M166: methods currently patched by this plugin whose original body a caller compiled before the patch could
    /// still have inlined (no exception handling, small enough, inlining not forbidden) and that were not marked at startup.</summary>
    public static string Coverage()
    {
        try
        {
            var patched = Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m)?.Owners.Any(o => o.StartsWith(OwnerPrefix, StringComparison.Ordinal)) == true).ToList();
            List<MethodBase> open;
            lock (Gate) open = patched.Where(m => !Guarded.Contains(m) && Inlinable(m)).ToList();
            var owners = patched.SelectMany(m => Harmony.GetPatchInfo(m)!.Owners).Where(o => o.StartsWith(OwnerPrefix, StringComparison.Ordinal)).Distinct().Count();
            return $"patched={patched.Count} by {owners} owners; inlinable unguarded={open.Count}" +
                (open.Count > 0 ? " [" + string.Join(", ", open.Select(Key).OrderBy(x => x, StringComparer.Ordinal)) + "]" : "");
        }
        catch (Exception error) { return "coverage unavailable: " + error.GetType().Name; }
    }

    internal static bool Inlinable(MethodBase method)
    {
        if ((method.MethodImplementationFlags & MethodImplAttributes.NoInlining) != 0) return false;
        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        return body != null && il != null && body.ExceptionHandlingClauses.Count == 0 && il.Length <= MaxInlineIl;
    }

    internal static string Key(MethodBase method) => method.Module.Assembly.GetName().Name + ":0x" + method.MetadataToken.ToString("X8") + ":" +
        (method.DeclaringType?.FullName ?? "?") + "." + method.Name;

    /// <summary>The Harmony 2.4.2 build merged into this DLL carries MonoMod.Core; its platform triple sets the runtime's
    /// "not inline" method flag (the same step it takes for every detoured method). Bound by reflection because the merge
    /// internalizes the type.</summary>
    /// <summary>Marks one method not inlinable (not recorded in <see cref="Count"/>); false if the runtime cannot.</summary>
    internal static bool TryDisable(MethodBase method) => Disabler() is { } call && call(method);

    private static Func<MethodBase, bool>? Disabler()
    {
        if (disable != null) return disable;
        var type = typeof(Harmony).Assembly.GetType("MonoMod.Core.Platforms.PlatformTriple", false);
        var current = type?.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        var method = type?.GetMethod("TryDisableInlining", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(MethodBase) }, null);
        if (current == null || method == null || method.ReturnType != typeof(bool)) return null;
        return disable = target => (bool)method.Invoke(current, new object[] { target })!;
    }
}
