using MediaBrowser.Model.Entities;
using StrmAssistantRebuild.Options;
using CacheMode = StrmAssistantRebuild.Options.MetadataBuildOptions.MetadataCacheOption;

namespace StrmAssistantRebuild.Common;

/// <summary>Read-only save-boundary checks; never creates cache folders or consumes startup flags.</summary>
public static class MetadataCacheRules
{
    public static bool TryMode(string? text, out CacheMode mode)
    {
        mode = CacheMode.None;
        return Enum.GetNames<CacheMode>().Any(n => string.Equals(n, text?.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            Enum.TryParse(text?.Trim(), true, out mode);
    }

    public static IEnumerable<string> Validate(MetadataBuildOptions options, EmbyPathPolicy? paths = null)
    {
        if (!TryMode(options.MetadataCacheMode, out var mode))
        { yield return "MetadataCacheMode: select a named cache mode."; yield break; }
        if (mode == CacheMode.None) yield break; // Dormant paths/image choices do not prevent disabling caching.
        var root = options.MetadataCacheRootFolder?.Trim();
        if (!string.IsNullOrEmpty(root))
        {
            string? error = null;
            try
            {
                if (!Path.IsPathFullyQualified(root) || root.Any(char.IsControl))
                    error = "MetadataCacheRootFolder: use an absolute directory path.";
                else
                {
                    var full = paths?.RequireWritablePath(root) ?? Path.GetFullPath(root);
                    if (File.Exists(full)) error = "MetadataCacheRootFolder: a file occupies the selected directory.";
                    var parent = Path.GetDirectoryName(full);
                    while (error == null && !string.IsNullOrEmpty(parent))
                    {
                        if (File.Exists(parent)) error = "MetadataCacheRootFolder: a file occupies an ancestor directory.";
                        if (Directory.Exists(parent)) break;
                        parent = Path.GetDirectoryName(parent);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { error = "MetadataCacheRootFolder: directory must stay within Emby roots without symbolic links."; }
            if (error != null) yield return error;
        }
        var allowed = Enum.GetNames<ImageType>();
        if ((options.MetadataCacheOptionalImageTypes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => !allowed.Contains(t, StringComparer.OrdinalIgnoreCase)))
            yield return "MetadataCacheOptionalImageTypes: select named image types.";
    }
}
