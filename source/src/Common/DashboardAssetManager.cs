using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Model.Logging;

namespace StrmAssistantRebuild.Common;

/// <summary>Version-bound overlays. Route and disk use one byte snapshot; originals are never overwritten without a backup.</summary>
public sealed class DashboardAssetManager
{
    public static readonly string[] AssetPaths =
    {
        "components/strmassistant/strmassistant.js", "modules/shortcuts.js", "modules/mediainfo/mediainfo.js",
        "modules/refreshdialog/refreshdialog.js", "modules/tabbedview/listcontroller.js", "modules/indicators/indicators.js",
        "modules/filtermenu/filtermenu.template.html", "item/item.js", "item/linkeditems.js", "list/list.js",
        "components/libraryoptionseditor/libraryoptionseditor.js"
    };
    private readonly object gate = new();
    private readonly string webRoot;
    private readonly string backupRoot;
    private readonly EmbyPathPolicy policy;
    private readonly ILogger logger;
    private readonly Func<string, byte[]?> resource;
    private readonly bool compatible;
    private readonly Dictionary<string, byte[]> originals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssetRecord> records = new(StringComparer.Ordinal);
    public Dictionary<string, string> Diagnostics { get; } = new(StringComparer.Ordinal);
    public DashboardAssetManager(string webRoot, string backupRoot, Version version, EmbyPathPolicy policy, ILogger logger, Func<string, byte[]?>? resource = null)
    {
        this.webRoot = Path.GetFullPath(webRoot);
        this.backupRoot = Path.GetFullPath(backupRoot);
        this.policy = policy;
        this.logger = logger;
        this.resource = resource ?? ReadResource;
        compatible = version.Major == 4 && version.Minor == 10 && version.Build == 2;
        logger.Info("[StrmAssistantRebuild] Dashboard root: {0}", this.webRoot);
        foreach (var path in AssetPaths) LoadOriginal(path);
    }
    private static byte[]? ReadResource(string name)
    {
        using var stream = typeof(DashboardAssetManager).Assembly.GetManifestResourceStream(name);
        if (stream == null) return null;
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
    private void LoadOriginal(string path)
    {
        try
        {
            var disk = Path.Combine(webRoot, path);
            var recordPath = Path.Combine(backupRoot, path + ".json");
            var backup = Path.Combine(backupRoot, path + ".original");
            if (File.Exists(recordPath) && File.Exists(backup))
            {
                var record = JsonSerializer.Deserialize<AssetRecord>(File.ReadAllText(recordPath)) ?? throw new IOException("Invalid asset record.");
                var bytes = File.ReadAllBytes(backup);
                if (Hash(bytes) != record.OriginalHash) throw new IOException("Original backup checksum mismatch.");
                originals[path] = bytes;
                records[path] = record;
            }
            else if (File.Exists(disk)) originals[path] = File.ReadAllBytes(disk);
            else if (path != AssetPaths[0] && resource("native/" + path) is byte[] baseline) originals[path] = baseline;
            else originals[path] = Array.Empty<byte>();
            snapshots[path] = originals[path];
        }
        catch (Exception ex)
        {
            Record(path, "Original recovery degraded: " + ex.Message);
            // A corrupted backup does not permit overwriting a live file.
            try { snapshots[path] = File.ReadAllBytes(Path.Combine(webRoot, path)); } catch { snapshots[path] = resource("native/" + path) ?? Array.Empty<byte>(); }
        }
    }
    public void Refresh(IReadOnlyDictionary<string, bool> enabled, object publicOptions)
    {
        lock (gate)
        {
            var prefix = Encoding.UTF8.GetBytes("/* StrmAssistantRebuild options: public UI fields only */\nwindow.StrmAssistantRebuildOptions=" + JsonSerializer.Serialize(publicOptions) + ";\n");
            foreach (var path in AssetPaths)
            {
                try
                {
                    if (!originals.TryGetValue(path, out var original)) continue;
                    var native = resource("native/" + path);
                    var isNew = path == AssetPaths[0];
                    var selected = original;
                    if (enabled.GetValueOrDefault(path) && compatible && (isNew || native != null && original.AsSpan().SequenceEqual(native)))
                    {
                        var overlay = resource("dashboard/" + path);
                        if (overlay != null)
                        {
                            selected = path.EndsWith(".js", StringComparison.Ordinal) && !isNew ? prefix.Concat(overlay).ToArray() : overlay;
                        }
                        else Record(path, "Overlay missing; native asset retained.");
                    }
                    else if (enabled.GetValueOrDefault(path)) Record(path, "Host version or native digest differs; native asset retained.");
                    var destination = Path.Combine(webRoot, path);
                    if (File.Exists(destination))
                    {
                        var current = File.ReadAllBytes(destination);
                        var isOurs = records.TryGetValue(path, out var record) && (Hash(current) == record.AppliedHash || Hash(current) == record.PreviousAppliedHash);
                        if (!current.AsSpan().SequenceEqual(original) && !isOurs)
                        {
                            snapshots[path] = current;
                            Record(path, "Another writer changed this asset; preserving its bytes.");
                            continue;
                        }
                    }
                    snapshots[path] = selected;
                    TryWrite(path, original, selected);
                }
                catch (Exception ex) { Record(path, "Asset refresh degraded: " + ex.Message); }
            }
        }
    }
    public ReadOnlyMemory<byte> Get(string path)
    {
        lock (gate)
            return snapshots.TryGetValue(path, out var bytes) ? bytes : throw new FileNotFoundException("Unknown dashboard asset.");
    }
    public void Restore()
    {
        lock (gate)
        {
            foreach (var path in AssetPaths)
            {
                if (!originals.TryGetValue(path, out var original)) continue;
                try
                {
                    var destination = Path.Combine(webRoot, path);
                    if (File.Exists(destination) && records.TryGetValue(path, out var record) && Hash(File.ReadAllBytes(destination)) == record.AppliedHash)
                    {
                        if (path == AssetPaths[0] && original.Length == 0) File.Delete(policy.RequireWritablePath(destination));
                        else AtomicWrite(destination, original);
                    }
                    snapshots[path] = original;
                }
                catch (Exception ex) { Record(path, "Restore degraded: " + ex.Message); }
            }
        }
    }
    private void TryWrite(string path, byte[] original, byte[] selected)
    {
        var destination = Path.Combine(webRoot, path);
        try
        {
            if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(selected)) return;
            var backup = Path.Combine(backupRoot, path + ".original");
            if (!File.Exists(backup)) AtomicWrite(backup, original);
            else if (!File.ReadAllBytes(backup).AsSpan().SequenceEqual(original)) throw new IOException("Existing backup differs; keeping it unchanged.");
            var record = new AssetRecord { OriginalHash = Hash(original), AppliedHash = Hash(selected), PreviousAppliedHash = records.TryGetValue(path, out var previous) ? previous.AppliedHash : Hash(original) };
            // Write-ahead record also recovers an interruption before/after replacing the live asset.
            AtomicWrite(Path.Combine(backupRoot, path + ".json"), JsonSerializer.SerializeToUtf8Bytes(record));
            records[path] = record;
            AtomicWrite(destination, selected);
            Diagnostics[path] = "disk-and-route";
        }
        catch (Exception ex) { Record(path, "route-fallback: " + ex.Message); }
    }
    private void AtomicWrite(string path, byte[] bytes)
    {
        path = policy.RequireWritablePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = policy.RequireWritablePath(path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            policy.RequireWritablePath(path);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(policy.RequireWritablePath(temp)); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private void Record(string path, string message) { Diagnostics[path] = message; logger.Warn("[StrmAssistantRebuild] {0}: {1}", path, message); }
    public sealed class AssetRecord
    {
        public string OriginalHash { get; set; } = string.Empty;
        public string AppliedHash { get; set; } = string.Empty;
        public string PreviousAppliedHash { get; set; } = string.Empty;
    }
}
