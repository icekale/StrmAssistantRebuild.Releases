namespace StrmAssistantRebuild.Common;

/// <summary>
/// M148: the original IsModSuccess = "every supported patch is in its desired state" (decompiled patch tracker check).
/// Each Configure(...) call reports whether its desired state (ON installed / OFF removed) was reached; the latest
/// result per name wins. The main-page banner is shown only while some recorded patch failed.
/// </summary>
public static class PatchHealth
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, bool> States = new(StringComparer.Ordinal);

    public static bool Record(string name, bool reachedDesiredState)
    {
        lock (Gate) States[name] = reachedDesiredState;
        return reachedDesiredState;
    }

    public static IReadOnlyList<string> Failed
    {
        get { lock (Gate) return States.Where(p => !p.Value).Select(p => p.Key).OrderBy(n => n, StringComparer.Ordinal).ToArray(); }
    }

    public static int Recorded { get { lock (Gate) return States.Count; } }

    /// <summary>Test isolation only.</summary>
    internal static void Reset() { lock (Gate) States.Clear(); }
}
