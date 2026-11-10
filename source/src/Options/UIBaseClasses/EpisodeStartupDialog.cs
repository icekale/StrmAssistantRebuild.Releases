using System.ComponentModel;
using System.Globalization;
using System.Security;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Plugins.UI.Views.Enums;
using StrmAssistantRebuild.Common;
using IEditableObject = MediaBrowser.Model.GenericEdit.IEditableObject;

namespace StrmAssistantRebuild.Options.UIBaseClasses;

/// <summary>Native administrator editor for existing journal startup intent, never a live migration control.</summary>
public sealed class EpisodeStartupDialog : IPluginDialogView, IPluginViewWithOptions
{
    private readonly EpisodeScopeJournal? journal;
    private readonly ILibraryManager library;
    private readonly IUserManager users;
    private readonly CancellationToken lifetime;
    private readonly CancellationTokenSource stopping;
    private int closed;
    private readonly Guid ownerUserId;
    private readonly long ownerInternalId;
    private readonly EpisodeScopeJournal.StartupSettingsSnapshot? baseline;
    private readonly Dictionary<long, Choice> choices = new();
    private EpisodeStartupContent content = new();
    private sealed record Choice(long Id, Guid Guid, string Name, string State);

    public EpisodeStartupDialog(IPluginUIView parent, EpisodeScopeJournal? journal, ILibraryManager library, IUserManager users, CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(parent); ArgumentNullException.ThrowIfNull(library); ArgumentNullException.ThrowIfNull(users);
        this.journal = journal; this.library = library; this.users = users;
        PluginId = parent.PluginId; User = parent.User;
        if (!Guid.TryParse(User?.Id, out ownerUserId) || ownerUserId == Guid.Empty) throw new SecurityException(EpisodeStartupContent.Text("Owner"));
        ownerInternalId = CurrentOwner().InternalId;
        RequireOwner(); lifetime.ThrowIfCancellationRequested();
        baseline = journal?.ReadStartupSettings(lifetime);
        var stored = baseline?.Bindings.ToDictionary(b => b.LibraryId) ?? new();
        foreach (var id in (baseline?.JournalLibraryIds ?? Array.Empty<long>()).Concat(stored.Keys).Distinct().OrderBy(id => id))
        {
            lifetime.ThrowIfCancellationRequested();
            var folder = library.GetItemById(id) as CollectionFolder;
            var valid = folder != null && folder.InternalId == id && folder.Id != Guid.Empty;
            stored.TryGetValue(id, out var binding);
            // A stored GUID is an identity, never a hint to rebind the same numeric ID.
            var guid = binding?.LibraryGuid ?? (valid ? folder!.Id : Guid.Empty);
            var state = !valid ? "Missing" : binding != null && guid != folder!.Id ? "Stale" :
                baseline!.JournalLibraryIds.Contains(id) ? "" : "Archived";
            choices.Add(id, new(id, guid, valid ? folder!.Name ?? "" : "", state));
        }
        content.ResumeOnStartup = baseline?.ResumeOnStartup ?? false;
        content.LibraryScope = string.Join(",", stored.Keys.OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture)));
        PrepareContent(); RequireOwner();
        stopping = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        this.lifetime = stopping.Token;
    }
    public string Caption => EpisodeStartupContent.Text("Title");
    public string SubCaption => "";
    public string PluginId { get; }
    public IEditableObject ContentData
    {
        get { PrepareContent(); return content; }
        set { content = value as EpisodeStartupContent ?? throw new ArgumentException(EpisodeStartupContent.Text("InvalidContent")); PrepareContent(); }
    }
    public UserDto User { get; set; } = null!;
    public string RedirectViewUrl { get; set; } = "";
    public bool AllowOk { get => !lifetime.IsCancellationRequested && IsOwner; set { } }
    public bool AllowCancel { get => IsOwner; set { } }
    public bool ShowDialogFullScreen => false;
    public PluginViewOptions ViewOptions => new() { OKButtonCaption = EpisodeStartupContent.Text("Save"), DialogSize = DialogSize.Small, PrimaryDialogAction = DialogAction.AddOrEdit };
    public event EventHandler<GenericEventArgs<IPluginUIView>>? UIViewInfoChanged { add { } remove { } }
    private bool MatchesOwnerDto => User?.Policy?.IsAdministrator == true && !User.Policy.IsDisabled && Guid.TryParse(User.Id, out var id) && id == ownerUserId;
    private User CurrentOwner()
    {
        if (!MatchesOwnerDto) throw new SecurityException(EpisodeStartupContent.Text("Owner"));
        var current = users.GetUserById(ownerUserId);
        if (!MatchesOwnerDto || current?.Id != ownerUserId || current.InternalId <= 0 || current.Policy?.IsAdministrator != true || current.Policy.IsDisabled)
            throw new SecurityException(EpisodeStartupContent.Text("Owner"));
        return current;
    }
    private bool IsOwner { get { try { RequireOwner(); return true; } catch { return false; } } }
    private void RequireOwner() { if (CurrentOwner().InternalId != ownerInternalId) throw new SecurityException(EpisodeStartupContent.Text("Owner")); }
    public bool IsCommandAllowed(string command) => command == "DialogOk" && AllowOk || command == "DialogCancel" && AllowCancel;
    public Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
    {
        RequireOwner();
        if (!IsCommandAllowed(commandId)) throw new ArgumentException(EpisodeStartupContent.Text("Command"));
        // Null lets the native host invoke OnOk/OnCancel and return to the original page.
        return Task.FromResult<IPluginUIView>(null!);
    }
    public Task OnOkCommand(string itemId, string commandId, string data)
    {
        RequireOwner();
        if (commandId != "DialogOk") throw new ArgumentException(EpisodeStartupContent.Text("Command"));
        lifetime.ThrowIfCancellationRequested();
        var resume = content.ResumeOnStartup;
        var raw = content.LibraryScope ?? "";
        if (raw.Length > 4096) throw new ArgumentException(EpisodeStartupContent.Text("Budget"));
        var parts = raw.Length == 0 ? Array.Empty<string>() : raw.Split(',');
        if (parts.Length > 64) throw new ArgumentException(EpisodeStartupContent.Text("Budget"));
        var selected = new List<EpisodeScopeJournal.LibraryBinding>(); var ids = new HashSet<long>();
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || !ids.Add(id) ||
                !choices.TryGetValue(id, out var choice) || choice.Guid == Guid.Empty)
                throw new ArgumentException(EpisodeStartupContent.Text("Selection"));
            selected.Add(new(id, choice.Guid));
        }
        selected.Sort((a, b) => a.LibraryId.CompareTo(b.LibraryId));
        if (resume && (baseline?.EntryCount ?? 0) == 0) throw new InvalidOperationException(EpisodeStartupContent.Text("EmptyError"));
        var changed = baseline != null && !baseline.Bindings.SequenceEqual(selected);
        if (changed && selected.Count != 0 && !resume) throw new InvalidOperationException(EpisodeStartupContent.Text("EnableFirst"));
        if (resume)
        {
            if (selected.Any(b => !baseline!.JournalLibraryIds.Contains(b.LibraryId))) throw new InvalidOperationException(EpisodeStartupContent.Text("Selection"));
            ValidateCurrent(selected);
        }
        RequireOwner();
        if (journal != null)
        {
            try { journal.SaveIfUnchanged(baseline!, resume, selected, lifetime, bindings => { if (resume) ValidateCurrent(bindings); else RequireOwner(); }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new IOException(EpisodeStartupContent.Text("Storage"), error); }
            catch (InvalidOperationException error) { throw new InvalidOperationException(EpisodeStartupContent.Text("SaveError"), error); }
        }
        Close();
        return Task.CompletedTask;
    }
    private void ValidateCurrent(IReadOnlyList<EpisodeScopeJournal.LibraryBinding> selected)
    {
        RequireOwner(); lifetime.ThrowIfCancellationRequested();
        foreach (var binding in selected)
        {
            if (library.GetItemById(binding.LibraryId) is not CollectionFolder folder || folder.InternalId != binding.LibraryId || folder.Id != binding.LibraryGuid)
                throw new InvalidOperationException(EpisodeStartupContent.Text("Identity"));
            lifetime.ThrowIfCancellationRequested();
        }
        RequireOwner();
        // Fresh native metadata is not a complete database snapshot or a writer-drain barrier.
    }
    private void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        try { stopping.Cancel(); } finally { stopping.Dispose(); }
    }
    public Task OnCancelCommand() { RequireOwner(); Close(); return Task.CompletedTask; }
    public Task Cancel() { RequireOwner(); Close(); return Task.CompletedTask; }
    public void OnDialogResult(IPluginUIView dialog, bool success, object result) { }
    private void PrepareContent()
    {
        content.HasJournalEntries = (baseline?.EntryCount ?? 0) != 0;
        content.LibraryList = choices.Values.Select(c => new EditorSelectOption
        {
            Value = c.Id.ToString(CultureInfo.InvariantCulture),
            Name = (string.IsNullOrWhiteSpace(c.Name) ? EpisodeStartupContent.Text("Library") : c.Name) + " [" + c.Id.ToString(CultureInfo.InvariantCulture) + "]" +
                (c.State.Length == 0 ? "" : " — " + EpisodeStartupContent.Text(c.State)),
            // Kept selectable so a stale/archived stored choice can be removed. Save validates identities.
            IsEnabled = c.Guid != Guid.Empty
        }).ToList();
        content.Status = new StatusItem { Caption = EpisodeStartupContent.Text("Status"), Status = ItemStatus.None,
            StatusText = ((baseline?.EntryCount ?? 0) == 0 ? EpisodeStartupContent.Text("Empty") :
                EpisodeStartupContent.Text("Count") + (baseline!.EntryCount).ToString(CultureInfo.CurrentUICulture) + ". ") + EpisodeStartupContent.Text("Boundary") };
    }
}

public sealed class EpisodeStartupContent : CultureAwareOptionsBase
{
    [Browsable(false)] public override string EditorTitle => Text("Title");
    [Browsable(false)] public bool HasJournalEntries { get; set; }
    [Browsable(false)] public List<EditorSelectOption> LibraryList { get; set; } = new();
    public bool ResumeOnStartup { get; set; }
    [EditMultilSelect, SelectItemsSource(nameof(LibraryList))]
    public string LibraryScope { get; set; } = "";
    [DisplayName(""), ReadOnly(true)] public StatusItem Status { get; set; } = new();
    public override IEditObjectContainer CreateEditContainer()
    {
        var container = (EditObjectContainer)base.CreateEditContainer();
        foreach (var editor in container.EditorRoot.EditorItems)
        {
            if (editor.Name == nameof(ResumeOnStartup)) { editor.DisplayName = Text("Resume"); editor.Description = Text("ResumeHelp"); editor.IsReadOnly = !HasJournalEntries && !ResumeOnStartup; }
            if (editor.Name == nameof(LibraryScope)) { editor.DisplayName = Text("Libraries"); editor.Description = Text("LibraryHelp"); editor.IsReadOnly = !HasJournalEntries && LibraryList.Count == 0; }
        }
        return container;
    }
    internal static string Text(string key)
    {
        var culture = CultureInfo.CurrentUICulture.Name;
        var zh = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var tw = culture.Contains("Hant", StringComparison.OrdinalIgnoreCase) || culture.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) || culture.Equals("zh-HK", StringComparison.OrdinalIgnoreCase);
        var words = key switch
        {
            "Title" => ("Episode startup settings", "剧集启动设置", "劇集啟動設定"),
            "Save" => ("Save startup settings", "保存启动设置", "儲存啟動設定"),
            "Cancel" => ("Cancel", "取消", "取消"),
            "Resume" => ("Resume existing journal on startup", "启动时恢复现有日志", "啟動時恢復現有日誌"),
            "ResumeHelp" => ("Reconcile existing journal records at a future server startup. This does not start a migration now.", "在服务器后续启动时核对现有日志记录。此操作不会立即开始迁移。", "在伺服器後續啟動時核對現有日誌記錄。此操作不會立即開始遷移。"),
            "Libraries" => ("Startup catch-up libraries", "启动后补齐的媒体库", "啟動後補齊的媒體庫"),
            "LibraryHelp" => ("Only libraries already recorded in this journal. Blank means no startup catch-up. Remove missing or identity-changed choices before enabling. Up to 64 libraries.", "仅列出现有日志中的媒体库。留空表示启动后不补齐。启用前请移除缺失或身份已变更的选项，最多选择 64 个媒体库。", "僅列出現有日誌中的媒體庫。留空表示啟動後不補齊。啟用前請移除缺失或身分已變更的選項，最多選擇 64 個媒體庫。"),
            "Status" => ("Startup scope", "启动范围", "啟動範圍"),
            "Empty" => ("No existing journal entries. Startup recovery stays unavailable until an entry exists. ", "现有日志中没有记录；记录存在后才可启用启动恢复。", "現有日誌中沒有記錄；記錄存在後才可啟用啟動恢復。"),
            "Count" => ("Existing journal records: ", "现有日志记录：", "現有日誌記錄："),
            "Boundary" => ("Future startup only. Saving or disarming does not turn OFF the current runtime. No migration of existing items or database remapping occurs here. The global LibraryScopeSupport flag remains false. Library metadata is not a complete database snapshot.", "仅影响后续启动。保存或停用不会关闭当前运行状态（OFF）。此处不迁移现有项目、不重新映射数据库；全局 LibraryScopeSupport 标志保持 false。媒体库元数据不代表完整数据库快照。", "僅影響後續啟動。儲存或停用不會關閉目前執行狀態（OFF）。此處不遷移現有項目、不重新映射資料庫；全域 LibraryScopeSupport 旗標保持 false。媒體庫中繼資料不代表完整資料庫快照。"),
            "Library" => ("Library", "媒体库", "媒體庫"),
            "Missing" => ("missing; remove to enable", "已缺失；启用前移除", "已缺失；啟用前移除"),
            "Stale" => ("identity changed; remove to enable", "身份已变更；启用前移除", "身分已變更；啟用前移除"),
            "Archived" => ("no current journal entry; remove to enable", "当前日志中无记录；启用前移除", "目前日誌中無記錄；啟用前移除"),
            "Owner" => ("The administrator who opened this dialog must save or cancel it.", "请由打开此对话框的管理员保存或取消。", "請由開啟此對話框的管理員儲存或取消。"),
            "Command" => ("Unknown dialog command. Use Save startup settings or Cancel.", "未知对话框命令。请使用保存启动设置或取消。", "未知對話框命令。請使用儲存啟動設定或取消。"),
            "InvalidContent" => ("Expected startup settings content.", "需要启动设置内容。", "需要啟動設定內容。"),
            "Budget" => ("Select at most 64 libraries; selection text must be at most 4096 characters.", "最多选择 64 个媒体库；选择文本上限为 4096 个字符。", "最多選擇 64 個媒體庫；選擇文字上限為 4096 個字元。"),
            "Selection" => ("Invalid, duplicate or unavailable library selection. Remove it and select an existing journal library.", "媒体库选项无效、重复或已失效。请移除后选择现有日志中的媒体库。", "媒體庫選項無效、重複或已失效。請移除後選擇現有日誌中的媒體庫。"),
            "EmptyError" => ("No journal intent exists. Close this dialog; startup recovery remains disabled.", "尚无日志记录。请关闭对话框；启动恢复保持停用。", "尚無日誌記錄。請關閉對話框；啟動恢復保持停用。"),
            "EnableFirst" => ("A new nonempty library selection requires startup recovery. Enable it, clear the selection, or keep the saved selection while disarming.", "新的非空媒体库选择需要启动恢复。请启用恢复、清空选择，或保留原有选择并停用恢复。", "新的非空媒體庫選擇需要啟動恢復。請啟用恢復、清空選擇，或保留原有選擇並停用恢復。"),
            "Identity" => ("A selected library is missing or its identity changed. Remove that choice, or disarm without changing the saved selection.", "所选媒体库已缺失或身份已变更。请移除该选项，或保留已保存的选择并停用恢复。", "所選媒體庫已缺失或身分已變更。請移除該選項，或保留已儲存的選擇並停用恢復。"),
            "Storage" => ("Startup settings were not saved. Check journal storage, then retry Save or Cancel.", "启动设置尚未保存。请检查日志存储后重试保存，或取消。", "啟動設定尚未儲存。請檢查日誌儲存後重試儲存，或取消。"),
            "SaveError" => ("Startup settings or library identities changed, or the journal has no matching intent. Close and reopen this dialog, then review the selection before saving.", "启动设置或媒体库身份已变更，或日志中已无对应记录。请关闭并重新打开对话框，核对选择后保存。", "啟動設定或媒體庫身分已變更，或日誌中已無對應記錄。請關閉並重新開啟對話框，核對選擇後儲存。"),
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        return zh ? tw ? words.Item3 : words.Item2 : words.Item1;
    }
}
