namespace StrmAssistantRebuild.Common;

/// <summary>Merge, split and version deletion share one order: version gate, then per-item work lease.</summary>
internal static class VersionMutationGate
{
    internal static readonly SemaphoreSlim Shared = new(1, 1);
}
