using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace StrmAssistantRebuild.Common;

/// <summary>Two pinned native ordering corrections; never substitutes a query, renderer or page order.</summary>
public static class DisplayOrderingControl
{
    private const string Owner = "StrmAssistantRebuild.DisplayOrdering.4.10.2";
    private const string HostName = "Emby.Server.Implementations";
    private const string Comparator = HostName + ".Localization.AlphanumComparator";
    private const string Repository = HostName + ".Data.SqliteItemRepository";
    private static readonly Version Version = new(4, 10, 2, 0);
    private static readonly Guid Mvid = new("2009e43d-fa42-4e57-a902-2784a8306072");
    private static readonly object Gate = new();
    private sealed record Contract(MethodInfo Natural, MethodInfo Collection, bool NaturalEnabled, bool CollectionEnabled);
    private static Contract? active;
    private static Harmony? patcher;
    private static readonly List<MethodInfo> installed = new();
    private static long naturalComparisons, changedNaturalComparisons, changedCollectionSorts;
    public static long NaturalComparisons => Interlocked.Read(ref naturalComparisons);
    public static long ChangedNaturalComparisons => Interlocked.Read(ref changedNaturalComparisons);
    public static long ChangedCollectionSorts => Interlocked.Read(ref changedCollectionSorts);
    public static bool IsEnabled => Volatile.Read(ref active) != null;
    public static string Detail { get; private set; } = "Disabled; native sorting unchanged.";

    public static bool Configure(Version hostVersion, bool optimizeMediaItemOrder, string? preferences, out string detail)
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try
            {
                Remove();
                var selected = (preferences ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var natural = optimizeMediaItemOrder && selected.Contains("NaturalSort", StringComparer.OrdinalIgnoreCase);
                var collection = optimizeMediaItemOrder && selected.Contains("CollectionPerItemDateCreated", StringComparer.OrdinalIgnoreCase);
                if (!natural && !collection)
                { detail = Detail = "Disabled; owned ordering patches removed; native sorting unchanged."; return true; }
                var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == HostName);
                var compare = assembly?.GetType(Comparator)?.GetMethod("CompareValues", new[] { typeof(ReadOnlySpan<char>), typeof(ReadOnlySpan<char>), typeof(StringComparison) });
                var group = assembly?.GetType(Repository)?.GetMethod("GetOrderByBeforeGroupingIntoCollections", BindingFlags.NonPublic | BindingFlags.Static);
                if (hostVersion != Version || compare == null || group == null || !Verify(compare, group))
                { detail = Detail = "Inactive: exact native ordering file/MVID/signature/IL/locals differs; native sorting retained."; return false; }
                var contract = new Contract(compare, group, natural, collection);
                if (ForeignOwner(contract))
                { detail = Detail = "Inactive: foreign ordering patch; native sorting retained."; return false; }
                if (natural) Install(compare, nameof(AfterCompare));
                if (collection) Install(group, nameof(AfterCollectionOrder));
                Volatile.Write(ref active, contract);
                detail = Detail = $"Enabled: NaturalSort={natural}, CollectionPerItemDateCreated={collection}. Original digit-boundary correction; collection date uses reconstructed MAX(desc)/MIN(asc) member aggregate. User sort keys/directions, scope and paging retained.";
                return true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref active, null);
                Detail = "Inactive: ordering installation failed (" + error.GetType().Name + "); native sorting retained.";
                try { Remove(); } catch { Detail += " Owned cleanup pending; guards inactive."; }
                detail = Detail;
                return false;
            }
        }
    }

    private static void Install(MethodInfo target, string postfix)
    {
        installed.Add(target); // Keep removal handle even when Harmony throws partway through installation.
        (patcher ??= new Harmony(Owner)).Patch(target, postfix: new HarmonyMethod(
            typeof(DisplayOrderingControl).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)!));
    }
    private static bool ForeignOwner(Contract contract) =>
        new[] { contract.Natural, contract.Collection }.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any(o => o != Owner) == true);
    private static Contract? ReadActive()
    {
        var contract = Volatile.Read(ref active);
        if (contract == null) return null;
        try { if (!ForeignOwner(contract)) return contract; } catch { }
        Volatile.Write(ref active, null);
        Detail = "Inactive: ordering patch ownership changed; native sorting retained.";
        return null;
    }

    // Exact native span parameters: no object[]/__args boxing and no replacement numeric/string comparator.
    private static void AfterCompare(ReadOnlySpan<char> s1, ReadOnlySpan<char> s2, ref int __result)
    {
        if (ReadActive()?.NaturalEnabled != true) return;
        Interlocked.Increment(ref naturalComparisons);
        if (s1.IsEmpty || s2.IsEmpty || char.IsDigit(s1[0]) || char.IsDigit(s2[0])) return;
        var length = Math.Min(s1.Length, s2.Length);
        var i = 0;
        while (i < length && s1[i] == s2[i]) i++;
        if (i == length || char.IsDigit(s1[i]) == char.IsDigit(s2[i])) return;
        var corrected = char.IsDigit(s1[i]) ? 1 : -1;
        if (__result == corrected) return;
        __result = corrected;
        Interlocked.Increment(ref changedNaturalComparisons);
    }

    private static void AfterCollectionOrder(InternalItemsQuery query, string mediaItemsTableQualifier, List<(string, SortOrder)> __result)
    {
        if (ReadActive()?.CollectionEnabled != true || query.GroupItemsIntoItemLinkType != ItemLinkType.Collections ||
            mediaItemsTableQualifier != "MediaItemsInnerA") return;
        // The pinned caller selects this field before GROUP BY GroupedId and orders/paginates the outer query.
        // Rebuild decision: original encrypted aggregate literals remain unknown; newest DESC / oldest ASC.
        // Emit only a fixed expression for an existing native DateCreated field, never caller-supplied SQL.
        for (var i = 0; i < __result.Count; i++)
        {
            var entry = __result[i];
            if (!string.Equals(entry.Item1, "MediaItemsInnerA.DateCreated", StringComparison.OrdinalIgnoreCase) ||
                entry.Item2 is not (SortOrder.Ascending or SortOrder.Descending)) continue;
            __result[i] = (entry.Item2 == SortOrder.Descending ?
                "MAX(MediaItemsInnerA.DateCreated) AS DateCreated" : "MIN(MediaItemsInnerA.DateCreated) AS DateCreated", entry.Item2);
            Interlocked.Increment(ref changedCollectionSorts);
            return;
        }
    }

    internal static bool Verify(MethodInfo compare, MethodInfo group)
    {
        var assembly = compare.Module.Assembly;
        if (assembly != group.Module.Assembly || assembly.GetName().Name != HostName || assembly.GetName().Version != Version ||
            compare.Module.ModuleVersionId != Mvid || group.Module.ModuleVersionId != Mvid || string.IsNullOrEmpty(assembly.Location)) return false;
        using var file = File.OpenRead(assembly.Location);
        using var sha = SHA256.Create();
        if (Convert.ToHexString(sha.ComputeHash(file)) != "2A119028BE281E3F6AF05C80DD1F047CDAEC4BBE092BDE30781F6989AED3764F") return false;
        var chunk = assembly.GetType(Comparator + "+ChunkType");
        var builder = typeof(MediaBrowser.Model.Text.ValueStringBuilder);
        return compare.DeclaringType is { IsPublic: true, IsSealed: true } && compare.DeclaringType.FullName == Comparator &&
            compare.IsPublic && compare.IsStatic && !compare.IsVirtual && !compare.ContainsGenericParameters && compare.Name == "CompareValues" && compare.ReturnType == typeof(int) &&
            compare.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(ReadOnlySpan<char>), typeof(ReadOnlySpan<char>), typeof(StringComparison) }) &&
            group.DeclaringType is { IsPublic: true, IsSealed: true } && group.DeclaringType.FullName == Repository &&
            group.IsPrivate && group.IsStatic && !group.IsVirtual && !group.ContainsGenericParameters && group.Name == "GetOrderByBeforeGroupingIntoCollections" &&
            group.ReturnType == typeof(List<(string, SortOrder)>) && group.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(InternalItemsQuery), typeof(string) }) &&
            Match(compare, 0x06000977, 381, 3, 0x1100022B, "071008080A0A080803118A64118791118A64118A6403118A64118791118A6408",
                new[] { typeof(int), typeof(int), typeof(long), typeof(long), typeof(int), typeof(int), typeof(char), chunk!, builder, chunk!, chunk!, typeof(char), chunk!, builder, chunk!, typeof(int) },
                "D2355227466BA1AF72F918516FB347A759D1540FA05007BA6315F2B0EED52244") &&
            Match(group, 0x0600102C, 79, 3, 0x1100043F, "0704151280F1011511868D020E1186911D1511868D020E118691081511868D020E118691",
                new[] { typeof(List<(string, SortOrder)>), typeof((string, SortOrder)[]), typeof(int), typeof((string, SortOrder)) },
                "529C190A4E845BD0BC96C21890F2083D1BAE7F93745D1B71180FCB0872FC4E77");
    }
    private static bool Match(MethodInfo method, int token, int length, int stack, int signature, string signatureHex, Type[] locals, string hash)
    {
        var body = method.GetMethodBody(); var il = body?.GetILAsByteArray();
        return method.MetadataToken == token && method.GetMethodImplementationFlags() == MethodImplAttributes.IL &&
            body is { InitLocals: true } && body.MaxStackSize == stack && body.LocalSignatureMetadataToken == signature &&
            Convert.ToHexString(method.Module.ResolveSignature(signature)) == signatureHex &&
            body.LocalVariables.All(v => !v.IsPinned) && body.LocalVariables.Select(v => v.LocalType).SequenceEqual(locals) &&
            body.ExceptionHandlingClauses.Count == 0 && il?.Length == length && Convert.ToHexString(SHA256.HashData(il)) == hash;
    }
    public static void Disable()
    {
        lock (Gate)
        {
            Volatile.Write(ref active, null);
            try { Remove(); Detail = "Disabled; owned ordering patches removed; native sorting restored."; }
            catch { Detail = "Disabled; owned cleanup pending; guards inactive."; }
        }
    }
    private static void Remove()
    {
        for (var i = installed.Count - 1; i >= 0; i--)
        { patcher?.Unpatch(installed[i], HarmonyPatchType.Postfix, Owner); installed.RemoveAt(i); }
    }
}
