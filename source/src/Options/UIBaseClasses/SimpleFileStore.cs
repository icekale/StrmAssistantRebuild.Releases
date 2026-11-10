using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Emby.Web.GenericEdit;
using MediaBrowser.Model.Logging;
using StrmAssistantRebuild.Common;

namespace StrmAssistantRebuild.Options.UIBaseClasses;

public sealed class FileSavingEventArgs : EventArgs
{
    public EditableOptionsBase Options { get; init; } = null!;
    public bool Cancel { get; set; }
}
public sealed class FileSavedEventArgs : EventArgs
{
    public EditableOptionsBase Options { get; init; } = null!;
}

/// <summary>JSON store as observed in the target; transient UI state never crosses the persistence boundary.</summary>
public sealed class SimpleFileStore<T> where T : EditableOptionsBase, new()
{
    private readonly object gate = new();
    private readonly EmbyPathPolicy paths;
    private readonly ILogger logger;
    private T options = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string OptionsFileName { get; }
    public string OptionsFilePath { get; }
    public string? LastError { get; private set; }
    internal EmbyPathPolicy PathPolicy => paths;
    public event EventHandler<FileSavingEventArgs>? FileSaving;
    public event EventHandler<FileSavedEventArgs>? FileSaved;

    public SimpleFileStore(string folder, string name, EmbyPathPolicy paths, ILogger logger)
    {
        this.paths = paths;
        this.logger = logger;
        OptionsFileName = name;
        OptionsFilePath = Path.Combine(folder, name);
        try
        {
            paths.RequireWritablePath(OptionsFilePath);
            if (File.Exists(OptionsFilePath))
            {
                using var stream = File.OpenRead(OptionsFilePath);
                if (stream.Length > 4 * 1024 * 1024) throw new IOException("Configuration exceeds 4 MiB.");
                var document = JsonNode.Parse(stream) as JsonObject ?? throw new JsonException("Expected configuration object.");
                var loaded = new T();
                Populate(loaded, document);
                options = loaded;
            }
        }
        catch (Exception ex) { Report("load", ex); }
    }

    public T GetOptions() { lock (gate) return Copy(options); }
    public static T Copy(T value) { var copy = new T(); Populate(copy, Persisted(value)); return copy; }
    public bool Save(T value)
    {
        T committed;
        lock (gate)
        {
            string? temp = null;
            try
            {
                var saving = new FileSavingEventArgs { Options = value };
                FileSaving?.Invoke(this, saving);
                if (saving.Cancel) return false;
                var destination = paths.RequireWritablePath(OptionsFilePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                temp = paths.RequireWritablePath(destination + "." + Guid.NewGuid().ToString("N") + ".tmp");
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, Persisted(value), JsonOptions);
                    file.Flush(true);
                }
                paths.RequireWritablePath(destination);
                File.Move(temp, destination, true);
                options = Copy(value);
                LastError = null;
                committed = Copy(options);
            }
            catch (Exception ex) { Report("save", ex); return false; }
            finally
            {
                if (temp != null) try { paths.RequireWritablePath(temp); File.Delete(temp); } catch (Exception ex) { logger.Warn("[StrmAssistantRebuild] Temporary file cleanup: {0}", ex.Message); }
            }
        }
        // Subscribers may read sibling stores: never call them while holding this store's lock.
        try { FileSaved?.Invoke(this, new FileSavedEventArgs { Options = committed }); }
        catch (Exception ex) { logger.Warn("[StrmAssistantRebuild] Saved callback failed: {0}", ex.Message); }
        return true;
    }

    private void Report(string operation, Exception ex)
    {
        LastError = ex.Message;
        logger.Warn("[StrmAssistantRebuild] Configuration {0} degraded: {1}", operation, ex.Message);
    }
    private static IEnumerable<PropertyInfo> Properties(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.SetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0 &&
            !p.IsDefined(typeof(IgnoreOnPersistAttribute), true));
    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);
    }
    internal static JsonObject Persisted(EditableOptionsBase value)
    {
        var result = new JsonObject();
        foreach (var p in Properties(value.GetType()))
        {
            var v = p.GetValue(value);
            if (typeof(EditableOptionsBase).IsAssignableFrom(p.PropertyType)) result[p.Name] = v is EditableOptionsBase child ? Persisted(child) : null;
            else if (IsScalar(p.PropertyType)) result[p.Name] = JsonSerializer.SerializeToNode(v, p.PropertyType);
        }
        return result;
    }
    internal static void Populate(EditableOptionsBase value, JsonObject data)
    {
        foreach (var p in Properties(value.GetType()))
        {
            if (!data.TryGetPropertyValue(p.Name, out var node)) continue;
            if (typeof(EditableOptionsBase).IsAssignableFrom(p.PropertyType))
            {
                if (node is JsonObject child && p.GetValue(value) is EditableOptionsBase nested) Populate(nested, child);
            }
            else if (IsScalar(p.PropertyType)) p.SetValue(value, node == null ? null : node.Deserialize(p.PropertyType));
        }
    }
}
