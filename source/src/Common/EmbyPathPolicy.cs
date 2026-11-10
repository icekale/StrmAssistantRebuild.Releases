namespace StrmAssistantRebuild.Common;

/// <summary>Writes stay inside host-provided Emby installation/data roots, never through symlinks.</summary>
public sealed class EmbyPathPolicy
{
    private readonly string[] roots;
    public EmbyPathPolicy(params string[] roots) => this.roots = roots.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).ToArray();
    public string RequireWritablePath(string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!roots.Any(root => full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison)))
            throw new IOException("Write path is outside Emby roots.");
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full.Substring(current.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget != null || new DirectoryInfo(current).LinkTarget != null ||
                ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Write path traverses a symbolic link.");
        }
        return full;
    }
}
