using System.Security;
using System.Diagnostics;
using System.Globalization;
using Emby.Web.GenericEdit;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace StrmAssistantRebuild.Options.UIBaseClasses;

public abstract class ControllerBase : IPluginUIPageController, IPluginPageSecurity
{
    protected ControllerBase(string pluginId) => PluginId = pluginId;
    public string PluginId { get; }
    public abstract PluginPageInfo PageInfo { get; }
    public Task Initialize(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public abstract Task<IPluginUIView> CreateDefaultPageView();
    public Task CheckIsUserAuthorised(UserDto user, IPluginUIView requestedView)
    {
        if (user?.Policy?.IsAdministrator != true) throw new SecurityException("Administrator access required.");
        return Task.CompletedTask;
    }
}

public sealed class OptionsController<T> : ControllerBase, IHasTabbedUIPages where T : EditableOptionsBase, new()
{
    private readonly SimpleFileStore<T> store;
    private readonly CancellationToken lifetime;
    private readonly Action<T> prepare;
    private readonly Func<T, string, CancellationToken, Task<string>>? commandHandler;
    private readonly Func<OptionsView<T>, string, CancellationToken, Task<IPluginUIView?>>? viewCommandHandler;
    public override PluginPageInfo PageInfo { get; }
    public IReadOnlyList<IPluginUIPageController> TabPageControllers { get; set; } = Array.Empty<IPluginUIPageController>();
    public OptionsController(string pluginId, SimpleFileStore<T> store, Action<T> prepare, bool main = false, Func<T, string, CancellationToken, Task<string>>? commandHandler = null,
        Func<OptionsView<T>, string, CancellationToken, Task<IPluginUIView?>>? viewCommandHandler = null, CancellationToken lifetime = default) : base(pluginId)
    {
        this.store = store; this.lifetime = lifetime;
        this.prepare = prepare;
        this.commandHandler = commandHandler;
        this.viewCommandHandler = viewCommandHandler;
        PageInfo = new PluginPageInfo { Name = "StrmAssistantRebuild." + typeof(T).Name, DisplayName = new T().EditorTitle, IsMainConfigPage = main };
    }
    public override Task<IPluginUIView> CreateDefaultPageView()
    {
        var model = store.GetOptions();
        prepare(model);
        return Task.FromResult<IPluginUIView>(new OptionsView<T>(PluginId, model, store.Save, prepare, commandHandler, viewCommandHandler, store.PathPolicy, lifetime));
    }
}

public sealed class OptionsView<T> : IPluginPageView, IPluginViewWithOptions where T : EditableOptionsBase, new()
{
    private readonly Func<T, bool> save;
    private readonly Action<T> prepare;
    private string message = string.Empty;
    private readonly Func<T, string, CancellationToken, Task<string>>? commandHandler;
    private readonly Func<OptionsView<T>, string, CancellationToken, Task<IPluginUIView?>>? viewCommandHandler;
    private readonly Common.EmbyPathPolicy? paths;
    private CancellationTokenSource stopping = new();
    private readonly CancellationToken lifetime;
    private readonly object refreshGate = new();
    private CancellationTokenSource? refreshStop;
    private EventHandler<GenericEventArgs<IPluginUIView>>? changed;
    private UserDto user = null!;
    internal TimeSpan RefreshPeriod { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan RefreshLease { get; set; } = TimeSpan.FromMinutes(5);
    internal Task RefreshCompletion { get; private set; } = Task.CompletedTask;
    public OptionsView(string pluginId, T content, Func<T, bool> save, Action<T> prepare, Func<T, string, CancellationToken, Task<string>>? commandHandler = null,
        Func<OptionsView<T>, string, CancellationToken, Task<IPluginUIView?>>? viewCommandHandler = null, Common.EmbyPathPolicy? paths = null, CancellationToken lifetime = default)
    { PluginId = pluginId; ContentData = content; this.save = save; this.prepare = prepare; this.commandHandler = commandHandler; this.viewCommandHandler = viewCommandHandler; this.paths = paths; this.lifetime = lifetime; }
    public string Caption => ((T)ContentData).EditorTitle;
    public string SubCaption => message;
    public string PluginId { get; }
    public IEditableObject ContentData { get; set; }
    public UserDto User
    {
        get => user;
        set { lock (refreshGate) { user = value; if (user?.Policy?.IsAdministrator != true) StopRefresh(); else StartRefresh(); } }
    }
    public string RedirectViewUrl { get; set; } = string.Empty;
    public bool ShowSave { get; set; } = true;
    public bool ShowBack { get; set; } = false;
    private bool allowSave = true, allowBack = true;
    public bool AllowSave { get => allowSave && User?.Policy?.IsAdministrator == true; set => allowSave = value; }
    public bool AllowBack { get => allowBack && User?.Policy?.IsAdministrator == true; set => allowBack = value; }
    public PluginViewOptions ViewOptions { get; } = new();
    public event EventHandler<GenericEventArgs<IPluginUIView>>? UIViewInfoChanged
    {
        add { lock (refreshGate) { changed += value; StartRefresh(); } }
        remove { lock (refreshGate) { changed -= value; if (changed == null) StopRefresh(); } }
    }
    private void StartRefresh()
    {
        if (typeof(T) != typeof(DashboardOptions) || changed == null || refreshStop != null ||
            user?.Policy?.IsAdministrator != true || stopping.IsCancellationRequested || lifetime.IsCancellationRequested) return;
        var source = refreshStop = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, lifetime);
        var culture = CultureInfo.CurrentUICulture;
        RefreshCompletion = RefreshDashboard(source, culture);
    }
    private void StopRefresh() { refreshStop?.Cancel(); refreshStop = null; }
    private async Task RefreshDashboard(CancellationTokenSource source, CultureInfo culture)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                await Task.Delay(RefreshPeriod, source.Token).ConfigureAwait(false);
                var paused = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency >= RefreshLease.TotalSeconds;
                var prior = CultureInfo.CurrentUICulture;
                try
                {
                    CultureInfo.CurrentUICulture = culture;
                    T model;
                    lock (refreshGate)
                    {
                        if (!ReferenceEquals(refreshStop, source) || user?.Policy?.IsAdministrator != true || changed == null) return;
                        model = SimpleFileStore<T>.Copy((T)ContentData);
                    }
                    prepare(model); // Build separately; never mutate a list being serialized by another native request.
                    if (model is DashboardOptions dashboard)
                    {
                        var row = dashboard.SystemInfoItemList.FirstOrDefault(r => r.Button1?.CommandId == "RefreshActivity");
                        if (row != null) row.SecondaryText = RefreshText(paused);
                    }
                    EventHandler<GenericEventArgs<IPluginUIView>>? notify;
                    lock (refreshGate)
                    {
                        if (!ReferenceEquals(refreshStop, source) || source.IsCancellationRequested || user?.Policy?.IsAdministrator != true) return;
                        ContentData = model; notify = changed;
                    }
                    notify?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
                }
                finally { CultureInfo.CurrentUICulture = prior; }
                if (paused) return; // Native tab departure has no reliable server Cancel; never retain an infinite page timer.
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch
        {
            // Keep the last snapshot, but do not leave an inaccurate promise of ongoing updates.
            try
            {
                CultureInfo.CurrentUICulture = culture;
                EventHandler<GenericEventArgs<IPluginUIView>>? notify;
                lock (refreshGate)
                {
                    if (!ReferenceEquals(refreshStop, source) || source.IsCancellationRequested || user?.Policy?.IsAdministrator != true) return;
                    var previous = (DashboardOptions)ContentData;
                    var snapshot = SimpleFileStore<DashboardOptions>.Copy(previous);
                    foreach (var row in previous.SystemInfoItemList)
                        if (row.Button1?.CommandId == "RefreshActivity") snapshot.SystemInfoItemList.Add(new() { PrimaryText = row.PrimaryText, SecondaryText = RefreshText(true), Button1 = row.Button1 });
                        else snapshot.SystemInfoItemList.Add(row);
                    ContentData = snapshot; notify = changed;
                }
                notify?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
            }
            catch { /* Native notification failed too; no unobserved background fault or automatic retry loop. */ }
        }
        finally { lock (refreshGate) { if (ReferenceEquals(refreshStop, source)) refreshStop = null; } source.Dispose(); }
    }
    private static string RefreshText(bool paused)
    {
        var name = CultureInfo.CurrentUICulture.Name;
        var traditional = name.Contains("Hant", StringComparison.OrdinalIgnoreCase) || name.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) || name.Equals("zh-HK", StringComparison.OrdinalIgnoreCase);
        return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? traditional ? (paused ? "自動更新已暫停；按重新整理活動可重新啟動。" : "每 5 秒自動更新，持續 5 分鐘；亦可手動重新整理。")
                          : (paused ? "自动更新已暂停；点击刷新活动可重新启动。" : "每 5 秒自动更新，持续 5 分钟；也可手动刷新。")
            : paused ? "Automatic updates paused; Refresh activity to resume." : "Updates every 5 seconds for 5 minutes; manual refresh remains available.";
    }
    public bool IsCommandAllowed(string command) => (command == "PageSave" || commandHandler != null || viewCommandHandler != null) && User?.Policy?.IsAdministrator == true;
    public async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
    {
        if (commandId == "PageSave") return await OnSaveCommand(itemId, commandId, data).ConfigureAwait(false);
        if (User?.Policy?.IsAdministrator != true) throw new SecurityException("Administrator access required.");
        if (viewCommandHandler != null)
        {
            var view = await viewCommandHandler(this, commandId, stopping.Token).ConfigureAwait(false);
            if (view != null) { view.User = User; return view; }
        }
        if (commandHandler == null) return this;
        try { message = await commandHandler((T)ContentData, commandId, stopping.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { message = "Operation cancelled."; }
        catch (Exception ex) { message = "Operation failed: " + ex.Message; }
        prepare((T)ContentData); // Re-evaluate runtime status/buttons after a completed or failed operation.
        return new OptionsView<T>(PluginId, (T)ContentData, save, prepare, commandHandler, viewCommandHandler, paths, lifetime) { User = User, message = message };
    }
    public Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
    {
        if (User?.Policy?.IsAdministrator != true) throw new SecurityException("Administrator access required.");
        var model = (T)ContentData;
        prepare(model); // Validation uses current host-owned capabilities/catalogs, never client-supplied transient bits.
        var errors = OptionsValidator.Validate(model, paths);
        if (errors.Count == 0)
        {
            // Source GeneralSave normalizes discovery to authority, once after validation, never during view preparation.
            OptionsValidator.NormalizeDiscovery(model);
        }
        message = errors.Count > 0 ? string.Join("\n", errors) : save(model) ? string.Empty : "Settings remain active in the editor; storage is read-only or unavailable. Changes have not been saved.";
        prepare(model);
        // The host snapshots Caption/SubCaption when creating its view wrapper.
        var next = new OptionsView<T>(PluginId, model, save, prepare, commandHandler, viewCommandHandler, paths, lifetime) { User = User, message = message };
        changed?.Invoke(this, new GenericEventArgs<IPluginUIView>(next));
        return Task.FromResult<IPluginUIView>(next);
    }
    public void NotifyChanged() => changed?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
    public IPluginUIView Refreshed(string status)
    {
        prepare((T)ContentData);
        return new OptionsView<T>(PluginId, (T)ContentData, save, prepare, commandHandler, viewCommandHandler, paths, lifetime) { User = User, message = status };
    }
    public Task Cancel()
    {
        lock (refreshGate) { stopping.Cancel(); StopRefresh(); return RefreshCompletion; }
    }
    public void OnDialogResult(IPluginUIView dialog, bool success, object result)
    {
        // GenericUI cancels this page when entering a dialog, then reuses it on close.
        if (stopping.IsCancellationRequested) { stopping.Dispose(); stopping = new CancellationTokenSource(); }
        prepare((T)ContentData);
        lock (refreshGate) StartRefresh();
    }
}
