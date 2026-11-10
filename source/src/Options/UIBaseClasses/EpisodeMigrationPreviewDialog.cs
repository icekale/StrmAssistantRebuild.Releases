using System.ComponentModel;
using System.Globalization;
using System.Security;
using System.Text;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Plugins.UI.Views.Enums;
using StrmAssistantRebuild.Common;
using IEditableObject = MediaBrowser.Model.GenericEdit.IEditableObject;
using Row = StrmAssistantRebuild.Common.EpisodeLibraryScopePlan.Row;

namespace StrmAssistantRebuild.Options.UIBaseClasses;

/// <summary>Bounded read preview. An optional batch enables a separate, explicitly confirmed initial migration.</summary>
public sealed class EpisodeMigrationPreviewDialog : IPluginDialogView, IPluginViewWithOptions
{
    public const int MaxCatalogEntries = 256, MaxDetails = 30;
    private readonly IPluginUIView parent;
    private readonly Version version;
    private readonly ILibraryManager library;
    private readonly IUserDataManager data;
    private readonly IUserManager users;
    private readonly CancellationTokenSource stopping;
    private readonly CancellationToken lifetime;
    private readonly CancellationToken pluginLifetime;
    private readonly EpisodeScopeBatch? initialBatch;
    private sealed record Observation(EpisodeMigrationPreviewResultContent Summary, Row[] Rows, EpisodeScopeJournal.LibraryBinding[] Libraries);
    private readonly Guid ownerUserId;
    private readonly long ownerInternalId;
    private readonly object gate = new(); // Only state publication; never held across host callbacks.
    private readonly Dictionary<long, Choice> choices = new();
    private EpisodeMigrationPreviewContent content = new();
    private string status = "";
    private int busy, closed, generation;
    private sealed record Choice(long Id, Guid Guid, string Name);
    private sealed record Identity(long Id, Guid Guid, long Library, string Path, string EpisodeKey, string SeriesKey, long Series, int? Season, int? Number, int? End);

    public EpisodeMigrationPreviewDialog(IPluginUIView parent, Version version, ILibraryManager library, IUserDataManager data, IUserManager users, CancellationToken lifetime = default, EpisodeScopeBatch? initialBatch = null)
    {
        ArgumentNullException.ThrowIfNull(parent); ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(library); ArgumentNullException.ThrowIfNull(data); ArgumentNullException.ThrowIfNull(users);
        this.parent = parent; this.version = version; this.users = users; pluginLifetime = lifetime;
        lifetime.ThrowIfCancellationRequested();
        PluginId = parent.PluginId; User = parent.User;
        if (!Guid.TryParse(User?.Id, out ownerUserId) || ownerUserId == Guid.Empty) throw new SecurityException(Text("Owner"));
        ownerInternalId = CurrentOwner().InternalId;
        stopping = CancellationTokenSource.CreateLinkedTokenSource(lifetime); this.lifetime = stopping.Token;
        this.library = library; this.data = data; this.initialBatch = initialBatch;
        try
        {
            CheckActive();
            // Mirrors native UserRootFolder's CollectionFolder catalog, without materializing VirtualFolderInfo for every library.
            // Limit bounds returned IDs plus one overflow sentinel; native SQL/root-cache work is not a database snapshot or a hard IO bound.
            var query = new InternalItemsQuery { Recursive = true, IncludeItemTypes = new[] { nameof(CollectionFolder) }, GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false, Limit = MaxCatalogEntries + 1 };
            var ids = Read(() => this.library.GetInternalItemIds(query, this.lifetime)) ?? throw new InvalidOperationException(Text("CatalogIdentity"));
            if (ids.Length > MaxCatalogEntries) throw new InvalidOperationException(Text("CatalogBudget"));
            if (query.Limit != MaxCatalogEntries + 1 || !query.Recursive || !query.IncludeItemTypes.SequenceEqual(new[] { nameof(CollectionFolder) }) || query.GroupByPresentationUniqueKey != false || query.EnableTotalRecordCount ||
                query.User != null || query.IsFavorite != null || query.GroupBy.Length != 0 || query.EnableGroupByMetadataKey || query.GroupBySeriesId || query.GroupBySeriesPresentationUniqueKey || query.GroupByAlbumId || query.GroupByParentPresentationUniqueKey || query.GroupByPrimaryImagePath || query.GroupProgramsBySeries || query.IsNextUpQuery || query.GroupItemsIntoItemLinkType.HasValue || query.ProjectToMedia)
                throw new InvalidOperationException(Text("CatalogIdentity"));
            foreach (var id in ids.ToArray()) // Snapshot before callbacks; raw bound applies before deduplication.
            {
                CheckActive();
                if (id <= 0 || Read(() => this.library.GetItemById(id)) is not CollectionFolder current || current.InternalId != id || current.Id == Guid.Empty)
                    throw new InvalidOperationException(Text("CatalogIdentity"));
                if (choices.TryGetValue(id, out var prior) && prior.Guid != current.Id) throw new InvalidOperationException(Text("Identity"));
                choices[id] = new(id, current.Id, current.Name ?? "");
            }
            CheckActive();
        }
        catch { Close(); throw; }
    }
    public string Caption => Text("Title");
    public string SubCaption => "";
    public string PluginId { get; }
    public UserDto User { get; set; } = null!;
    public string RedirectViewUrl { get; set; } = "";
    public IEditableObject ContentData
    {
        get { lock (gate) { PrepareContent(); return content; } }
        set
        {
            if (value is not EpisodeMigrationPreviewContent incoming) throw new ArgumentException(Text("Content"));
            lock (gate)
            {
                // The only submitted field is CSV. Client lists/status/extra properties are never authority.
                content = new() { LibraryScope = incoming.LibraryScope ?? "" };
                if (busy == 0) status = "";
                PrepareContent();
            }
        }
    }
    public bool AllowOk { get => Volatile.Read(ref busy) == 0 && Volatile.Read(ref closed) == 0 && !lifetime.IsCancellationRequested && IsOwner; set { } }
    public bool AllowCancel { get => Volatile.Read(ref closed) == 0 && IsOwner; set { } }
    public bool ShowDialogFullScreen => false;
    public PluginViewOptions ViewOptions => new() { OKButtonCaption = Text("Preview"), DialogSize = DialogSize.Small, PrimaryDialogAction = DialogAction.AddOrEdit };
    public event EventHandler<GenericEventArgs<IPluginUIView>>? UIViewInfoChanged { add { } remove { } }
    public bool IsCommandAllowed(string command) => command == "DialogOk" && AllowOk || command == "DialogCancel" && AllowCancel;

    public async Task<IPluginUIView> RunCommand(string itemId, string commandId, string commandData)
    {
        RequireOwner();
        if (commandId == "DialogCancel") { return null!; } // Native OnCancel -> original root host.
        if (commandId != "DialogOk") throw new ArgumentException(Text("Command"));
        lifetime.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new InvalidOperationException(Text("Busy"));
        int operation;
        try
        {
            string raw;
            lock (gate) { operation = generation; raw = content.LibraryScope ?? ""; status = Text("Working"); }
            CheckActive();
            var selected = ParseSelection(raw);
            // Actual synchronous native selector/planner work stays off the request continuation, so native Cancel can run while a manager blocks.
            var summary = await Task.Run(() => Observe(selected), lifetime).ConfigureAwait(false);
            CheckActive();
            var result = new PreviewResult(this, summary);
            CheckActive();
            lock (gate)
            {
                if (closed != 0 || operation != generation || lifetime.IsCancellationRequested) throw new OperationCanceledException(lifetime);
                closed = 1; generation++;
            }
            Stop();
            return result;
        }
        catch
        {
            lock (gate) { if (closed == 0) status = Text("Failed"); }
            throw; // Native GenericUI keeps the selection/input open; never a null OK success.
        }
        finally { Volatile.Write(ref busy, 0); }
    }
    public Task OnOkCommand(string itemId, string commandId, string commandData)
    {
        RequireOwner(); lifetime.ThrowIfCancellationRequested();
        throw new InvalidOperationException(Text("Command")); // Preview must be intercepted in RunCommand, not the close-success fallback.
    }
    public Task OnCancelCommand() { RequireOwner(); Close(); return Task.CompletedTask; }
    public Task Cancel() { RequireOwner(); Close(); return Task.CompletedTask; }
    public void OnDialogResult(IPluginUIView dialog, bool success, object result) { }

    private long[] ParseSelection(string raw)
    {
        if (raw.Length == 0) throw new ArgumentException(Text("Empty"));
        if (raw.Length > 4096) throw new ArgumentException(Text("Budget"));
        var parts = raw.Split(',');
        if (parts.Length > EpisodeLibrarySelection.MaxLibraries) throw new ArgumentException(Text("Budget"));
        var ids = new HashSet<long>(); var selected = new List<long>();
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || !ids.Add(id) || !choices.ContainsKey(id))
                throw new ArgumentException(Text("Selection"));
            selected.Add(id);
        }
        return selected.ToArray();
    }
    private Observation Observe(long[] selected)
    {
        ValidateLibraries(selected);
        var selection = EpisodeLibrarySelection.Select(version, library, selected, lifetime, new Checkpoint(CheckActive));
        CheckActive(); ValidateLibraries(selected);
        if (selection.ProgressErrors != 0) throw new SecurityException(Text("Owner")); // The only observer is our owner/cancellation checkpoint.
        if (!selection.Complete)
        {
            // Library state is diagnostic only. No Ready item totals/planner rows for an incomplete selector observation.
            var detail = string.Join("\n", selection.Libraries.Take(MaxDetails).Select(r => "[" + r.LibraryId.ToString(CultureInfo.InvariantCulture) + "] " + r.State));
            throw new InvalidOperationException(Text("SelectionFailed") + "\n" + detail + (selection.Libraries.Count > MaxDetails ? "\n" + Text("FailedDetailsTruncated") : ""));
        }
        var identities = selection.ItemIds.ToDictionary(id => id, id => ReadIdentity(id, selected));
        ValidateLibraries(selected); CheckActive();
        var rows = EpisodeLibraryScopePlan.Build(version, library, data, selection.ItemIds, ownerInternalId, lifetime, new Checkpoint(CheckActive));
        CheckActive(); ValidateLibraries(selected);
        foreach (var row in rows)
        {
            var now = ReadIdentity(row.ItemId, selected, row);
            if (!identities.TryGetValue(row.ItemId, out var prior) || prior != now || (row.ItemGuid != Guid.Empty && row.ItemGuid != now.Guid) || (row.LibraryId != 0 && row.LibraryId != now.Library) ||
                (row.ItemGuid != Guid.Empty && (row.EpisodeKey != now.EpisodeKey || row.SeriesKey != now.SeriesKey)))
                throw new InvalidOperationException(Text("Identity"));
        }
        ValidateLibraries(selected); CheckActive();
        var summary = new EpisodeMigrationPreviewResultContent(Format(selection, rows), selection.Libraries.Count, selection.Libraries.Sum(r => (long)r.Candidates), rows.Count(r => r.Status == "Ready"), rows.Count(r => r.Status == "AlreadyScoped"), rows.Count(r => r.Status == "Blocked"), rows.Length > MaxDetails);
        // Only these private server copies can reach ApplyInitialAsync. Nothing executable is in ContentData.
        return new(summary, rows.Select(r => r with { Issues = r.Issues.ToArray() }).ToArray(), selected.Select(id => new EpisodeScopeJournal.LibraryBinding(id, choices[id].Guid)).ToArray());
    }
    private void ValidateLibraries(IEnumerable<long> selected)
    {
        CheckActive();
        foreach (var id in selected)
            if (Read(() => library.GetItemById(id)) is not CollectionFolder folder || folder.InternalId != id || folder.Id != choices[id].Guid)
                throw new InvalidOperationException(Text("Identity"));
        CheckActive();
    }
    private Identity ReadIdentity(long id, long[] selected, Row? plan = null)
    {
        if (Read(() => library.GetItemById(id)) is not Episode item || item.InternalId != id || item.Id == Guid.Empty || item.IsVirtualItem || !item.IsFileProtocol ||
            string.IsNullOrWhiteSpace(item.Path) || !string.IsNullOrEmpty(item.ExternalId)) throw new InvalidOperationException(Text("Identity"));
        var before = Capture(item, 0);
        var folders = Read(() => library.GetCollectionFolders(item, lifetime));
        if (folders == null || folders.Length == 0 || folders.Any(f => f is not CollectionFolder || !selected.Contains(f.InternalId) || !choices.TryGetValue(f.InternalId, out var choice) || f.Id != choice.Guid) ||
            folders.Select(f => f.InternalId).Distinct().Count() != 1) throw new InvalidOperationException(Text("Identity"));
        if (plan?.Status is "Ready" or "AlreadyScoped")
            if (Read(() => ScopedEpisodeUserDataKey.ReadNativeKey(item)) != plan.NativeKey) throw new InvalidOperationException(Text("Identity"));
        CheckActive();
        if (before != Capture(item, 0) || item.IsVirtualItem || !item.IsFileProtocol || !string.IsNullOrEmpty(item.ExternalId))
            throw new InvalidOperationException(Text("Identity"));
        return before with { Library = folders[0].InternalId };
    }
    private static Identity Capture(Episode item, long libraryId) => new(item.InternalId, item.Id, libraryId, item.Path, item.PresentationUniqueKey ?? "", item.SeriesPresentationUniqueKey ?? "", item.SeriesId, item.ParentIndexNumber, item.IndexNumber, item.IndexNumberEnd);
    private static string Format(EpisodeLibrarySelection.Selection selection, Row[] rows)
    {
        var text = new StringBuilder(Text("Boundary")).AppendLine().AppendLine(Text("Window"));
        text.Append(Text("Totals")).Append(": ").Append(selection.Libraries.Count).Append(" / ").Append(selection.Libraries.Sum(r => (long)r.Candidates)).Append(" / ").Append(rows.Length)
            .Append("; Ready=").Append(rows.Count(r => r.Status == "Ready")).Append("; AlreadyScoped=").Append(rows.Count(r => r.Status == "AlreadyScoped")).Append("; Blocked=").Append(rows.Count(r => r.Status == "Blocked")).AppendLine();
        text.AppendLine(Text("Details"));
        foreach (var row in rows.Take(MaxDetails))
            text.Append('[').Append(row.ItemId).Append("] ").Append(Text("Library")).Append(' ').Append(row.LibraryId).Append(" — ").Append(row.Status)
                .Append(row.Issues.Length == 0 ? "" : ": " + string.Join(", ", row.Issues)).AppendLine();
        if (rows.Length > MaxDetails) text.AppendLine(Text("Truncated"));
        if (rows.Length == 0) text.AppendLine(Text("NoCandidates"));
        return text.ToString();
    }
    private bool MatchesOwnerDto(UserDto? dto) => dto?.Policy?.IsAdministrator == true && !dto.Policy.IsDisabled && Guid.TryParse(dto.Id, out var id) && id == ownerUserId;
    private User CurrentOwner()
    {
        if (!MatchesOwnerDto(User)) throw new SecurityException(Text("Owner"));
        User current;
        try { current = users.GetUserById(ownerUserId); }
        catch (OperationCanceledException error) when (!pluginLifetime.IsCancellationRequested) { throw new InvalidOperationException(Text("ReadFailed"), error); }
        if (!MatchesOwnerDto(User) || current?.Id != ownerUserId || current.InternalId <= 0 || current.Policy?.IsAdministrator != true || current.Policy.IsDisabled)
            throw new SecurityException(Text("Owner"));
        return current;
    }
    private void RequireOwner() { if (CurrentOwner().InternalId != ownerInternalId) throw new SecurityException(Text("Owner")); }
    private bool IsOwner { get { try { RequireOwner(); return true; } catch { return false; } } }
    private void CheckActive() { lifetime.ThrowIfCancellationRequested(); try { RequireOwner(); } finally { lifetime.ThrowIfCancellationRequested(); } }
    private void Close()
    {
        lock (gate) { if (closed != 0) return; closed = 1; generation++; }
        Stop();
    }
    private void Stop() { try { stopping.Cancel(); } finally { stopping.Dispose(); } }
    private void PrepareContent()
    {
        content.LibraryList = choices.Values.OrderBy(c => c.Id).Select(c => new EditorSelectOption { Value = c.Id.ToString(CultureInfo.InvariantCulture), Name = (string.IsNullOrWhiteSpace(c.Name) ? Text("Library") : c.Name) + " [" + c.Id.ToString(CultureInfo.InvariantCulture) + "]", IsEnabled = true }).ToList();
        content.Status = new StatusItem { Caption = Text("Status"), Status = ItemStatus.None, StatusText = (status.Length == 0 ? choices.Count == 0 ? Text("NoLibraries") : Text("Choose") : status) + "\n" + Text("Boundary") + "\n" + Text("Window") };
    }
    internal static string Text(string key) => EpisodeMigrationPreviewContent.Text(key);

    private T Read<T>(Func<T> read)
    {
        CheckActive();
        try { return read(); }
        catch (OperationCanceledException error) when (!lifetime.IsCancellationRequested) { throw new InvalidOperationException(Text("ReadFailed"), error); }
        finally { CheckActive(); }
    }
    private sealed class Checkpoint : IProgress<double>
    {
        private readonly Action check;
        public Checkpoint(Action check) => this.check = check;
        public void Report(double value) => check();
    }
    // GenericUI wraps a returned dialog with the input dialog as OriginalUIView, NOT the constructor's parent.
    // Therefore result Close explicitly returns the root plugin view (and restores its lifecycle), rather than null -> orphaned input.
    private sealed class PreviewResult : IPluginDialogView, IPluginViewWithOptions
    {
        private readonly EpisodeMigrationPreviewDialog owner;
        private readonly EpisodeMigrationPreviewResultContent summary;
        private readonly Row[] previewRows;
        private readonly EpisodeScopeJournal.LibraryBinding[] libraries;
        private readonly long probeUserId;
        private readonly bool eligible;
        private readonly object stateGate = new();
        private CancellationTokenSource? operationStop;
        private CancellationToken operationToken;
        private Task operationTask = Task.CompletedTask; // Owned and fully observed by Execute; never async void.
        private EpisodeScopeBatch.Report? report;
        private string failure = "";
        private double percentage;
        private long notifiedAt;
        private string notifiedPhase = "";
        private bool confirm;
        private int closed, started, finished, operationIdentity, notificationErrors;
        public PreviewResult(EpisodeMigrationPreviewDialog owner, Observation observation)
        {
            this.owner = owner; summary = observation.Summary;
            previewRows = observation.Rows; libraries = observation.Libraries; probeUserId = owner.ownerInternalId;
            eligible = owner.initialBatch != null && previewRows.Length != 0 && previewRows.All(r => r.Status == "Ready" && r.Issues.Length == 0);
            User = owner.User;
        }
        public string Caption => Text("Title");
        public string SubCaption => "";
        public string PluginId => owner.PluginId;
        public UserDto User { get; set; }
        public string RedirectViewUrl { get; set; } = "";
        public IEditableObject ContentData
        {
            get
            {
                lock (stateGate)
                {
                    var copy = summary.Copy(report, started != 0 && finished == 0);
                    copy.MigrationAvailable = eligible; copy.ConfirmInitialMigration = confirm;
                    if (started != 0) { copy.Report.Caption = Text("CapturedPreview"); copy.Report.StatusText = Text("CapturedPreview") + copy.Report.StatusText[copy.Report.StatusText.IndexOf('\n')..]; }
                    copy.ConfirmationEditable = started == 0 && closed == 0;
                    copy.ApplyInitialMigration = new() { Caption = Text("ApplyInitial"), CommandId = "ApplyInitialMigration", IsEnabled = started == 0 && closed == 0 };
                    copy.CancelInitialMigration = new() { Caption = Text("CancelMigration"), CommandId = "CancelInitialMigration", IsEnabled = started != 0 && finished == 0 && closed == 0 && !operationToken.IsCancellationRequested };
                    copy.Progress = new() { MaxValue = 100, CurrentValue = (int)Math.Clamp(percentage, 0, 100), ProgressText = started == 0 ? Text("NotStarted") : finished == 0 ? operationToken.IsCancellationRequested ? Text("Cancelling") : Text("Running") : Text("Finished") };
                    copy.Execution = new() { Caption = Text("InitialTitle"), Status = ItemStatus.None, StatusText = FormatExecution() };
                    return copy;
                }
            }
            set
            {
                // JSON report/counts/catalog/IDs/rows and detached model mutation are never input authority.
                if (value is EpisodeMigrationPreviewResultContent incoming)
                    lock (stateGate) { if (started == 0 && closed == 0 && eligible) confirm = incoming.ConfirmInitialMigration; }
            }
        }
        public bool AllowOk { get => Volatile.Read(ref closed) == 0 && Valid; set { } }
        public bool AllowCancel { get => Volatile.Read(ref closed) == 0 && Valid; set { } }
        public bool ShowDialogFullScreen => false;
        public PluginViewOptions ViewOptions => new() { OKButtonCaption = Text("Close"), DialogSize = DialogSize.Small, PrimaryDialogAction = DialogAction.AddOrEdit };
        public event EventHandler<GenericEventArgs<IPluginUIView>>? UIViewInfoChanged;
        private void Guard() { owner.RequireOwner(); if (!owner.MatchesOwnerDto(User) || !owner.MatchesOwnerDto(owner.parent.User)) throw new SecurityException(Text("Owner")); }
        private bool Valid { get { try { Guard(); return true; } catch { return false; } } }
        public bool IsCommandAllowed(string command) => command switch
        {
            "DialogOk" or "DialogCancel" => AllowOk,
            // GenericUI checks this BEFORE posting ContentData. Do not gate admission on the old confirm value.
            "ApplyInitialMigration" => eligible && Volatile.Read(ref started) == 0 && AllowOk,
            "CancelInitialMigration" => Volatile.Read(ref started) != 0 && Volatile.Read(ref finished) == 0 && AllowCancel,
            _ => false
        };
        public Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            Guard();
            if (Volatile.Read(ref closed) != 0) throw new OperationCanceledException(Text("Closed"));
            if (commandId == "ApplyInitialMigration")
            {
                lock (stateGate)
                {
                    owner.pluginLifetime.ThrowIfCancellationRequested();
                    if (closed != 0) throw new OperationCanceledException(Text("Closed"));
                    if (!eligible || started != 0) throw new InvalidOperationException(Text("InitialUnavailable"));
                    if (!confirm) throw new InvalidOperationException(Text("ConfirmRequired"));
                    operationStop = CancellationTokenSource.CreateLinkedTokenSource(owner.pluginLifetime);
                    operationToken = operationStop.Token; started = 1; var identity = ++operationIdentity;
                    operationTask = Task.Run(() => Execute(identity));
                }
                // Same native view/host: no outgoing Cancel on start and no request-long loading overlay.
                return Task.FromResult<IPluginUIView>(this);
            }
            if (commandId == "CancelInitialMigration")
            {
                RequestCancel(); Notify(); return Task.FromResult<IPluginUIView>(this);
            }
            if (commandId is not ("DialogOk" or "DialogCancel")) throw new ArgumentException(Text("Command"));
            if (!Close()) throw new OperationCanceledException(Text("Closed"));
            owner.parent.OnDialogResult(this, false, null!); Guard();
            return Task.FromResult(owner.parent);
        }
        // Pure writer callback: no locks, native manager IO, journal reads, notification or synchronous wait.
        private void ValidateCommit(int identity)
        {
            operationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref closed) != 0 || Volatile.Read(ref operationIdentity) != identity) throw new OperationCanceledException(Text("Closed"), operationToken);
            if (!owner.MatchesOwnerDto(User) || !owner.MatchesOwnerDto(owner.User) || !owner.MatchesOwnerDto(owner.parent.User)) throw new SecurityException(Text("Owner"));
        }
        private async Task Execute(int identity)
        {
            try
            {
                ValidateCommit(identity); Guard(); ValidateCommit(identity);
                var actual = await owner.initialBatch!.ApplyInitialAsync(previewRows, libraries, probeUserId,
                    new MigrationProgress(value => PublishProgress(value)), operationToken,
                    () => { ValidateCommit(identity); Guard(); ValidateCommit(identity); }, () => ValidateCommit(identity)).ConfigureAwait(false);
                lock (stateGate) { if (closed == 0) report = actual; }
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested || Volatile.Read(ref closed) != 0)
            { lock (stateGate) { if (closed == 0) failure = Text("CancelledBeforeReport"); } }
            catch (Exception error)
            { lock (stateGate) { if (closed == 0) failure = error.GetType().Name + ": " + error.Message; } }
            finally
            {
                lock (stateGate) { finished = 1; }
                Notify(); operationStop!.Dispose();
            }
        }
        private void PublishProgress(double value)
        {
            // Batch publishes LastReport immediately before this synchronous callback, under its single admission.
            // Keep every snapshot, but bound native websocket metadata updates to 4/s plus phase changes and the final report.
            bool notify;
            lock (stateGate)
            {
                if (closed != 0) return;
                percentage = value; report = owner.initialBatch!.LastReport;
                var now = Environment.TickCount64; var phase = report?.Phase ?? "";
                notify = phase != notifiedPhase || now - notifiedAt >= 250;
                if (notify) { notifiedAt = now; notifiedPhase = phase; }
            }
            if (notify) Notify();
        }
        private sealed class MigrationProgress : IProgress<double>
        {
            private readonly Action<double> publish;
            public MigrationProgress(Action<double> publish) => this.publish = publish;
            public void Report(double value) => publish(value);
        }
        private void Notify()
        {
            if (Volatile.Read(ref closed) != 0) return;
            try { UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this)); }
            catch { Interlocked.Increment(ref notificationErrors); } // Host notification failures never fault the owned operation or reinterpret a commit.
        }
        private void RequestCancel()
        {
            try { operationStop?.Cancel(); } catch (ObjectDisposedException) { } // Completion may race a native close.
        }
        private bool Close()
        {
            lock (stateGate) { if (closed != 0) return false; closed = 1; operationIdentity++; UIViewInfoChanged = null; }
            RequestCancel(); return true; // Never join our own operation from a native callback.
        }
        private string FormatExecution()
        {
            var text = new StringBuilder(Text("InitialBoundary")).AppendLine().AppendLine(Text("CancelBoundary"));
            if (started == 0) return text.Append(Text("InitialEligibility")).ToString();
            if (failure.Length != 0) text.AppendLine(failure);
            if (report != null)
            {
                text.Append("Phase=").Append(report.Phase).Append("; Cancelled=").Append(report.Cancelled).Append("; Retained=").Append(report.Retained)
                    .Append("; RetainedCountAuthoritative=").Append(report.RetainedCountAuthoritative).Append("; ProgressErrors=").Append(report.ProgressErrors).AppendLine();
                text.Append("Applied=").Append(report.Items.Count(r => r.State == "Applied")).Append(" / ").Append(report.Items.Count)
                    .Append("; CopiedUsers=").Append(report.Items.Sum(r => (long)r.CopiedUsers)).Append("; MappedItems=").Append(report.Items.Sum(r => (long)r.MappedItems)).AppendLine();
                foreach (var item in report.Items.Take(MaxDetails))
                    text.Append('[').Append(item.ItemId).Append("] ").Append(item.State).Append("; CopiedUsers=").Append(item.CopiedUsers).Append("; MappedItems=").Append(item.MappedItems).Append(" — ").Append(item.Detail).AppendLine();
                if (report.Items.Count > MaxDetails) text.AppendLine(Text("ExecutionTruncated"));
                text.AppendLine(report.Reconciliation);
            }
            if (notificationErrors != 0) text.Append("NotificationErrors=").Append(notificationErrors).AppendLine();
            if (finished != 0 && (failure.Length != 0 || report?.Phase != "Completed" || report.Cancelled)) text.AppendLine(Text("InspectRecovery"));
            return text.ToString();
        }
        public Task OnOkCommand(string itemId, string commandId, string data) { Guard(); throw new InvalidOperationException(Text("Command")); }
        public Task OnCancelCommand() { try { Guard(); } finally { Close(); } return Task.CompletedTask; }
        public Task Cancel() { try { Guard(); } finally { Close(); } return Task.CompletedTask; }
        public void OnDialogResult(IPluginUIView dialog, bool success, object result) { }
    }
}

public sealed class EpisodeMigrationPreviewContent : CultureAwareOptionsBase
{
    [Browsable(false)] public override string EditorTitle => "";
    [Browsable(false)] public List<EditorSelectOption> LibraryList { get; set; } = new();
    [EditMultilSelect, SelectItemsSource(nameof(LibraryList))] public string LibraryScope { get; set; } = "";
    [DisplayName(""), ReadOnly(true)] public StatusItem Status { get; set; } = new();
    public override IEditObjectContainer CreateEditContainer()
    {
        var container = (EditObjectContainer)base.CreateEditContainer();
        foreach (var editor in container.EditorRoot.EditorItems)
            if (editor.Name == nameof(LibraryScope)) { editor.DisplayName = Text("Libraries"); editor.Description = Text("LibraryHelp"); editor.IsReadOnly = LibraryList.Count == 0; }
        return container;
    }
    internal static string Text(string key)
    {
        var name = CultureInfo.CurrentUICulture.Name;
        var zh = name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var tw = name.Contains("Hant", StringComparison.OrdinalIgnoreCase) || name.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) || name.Equals("zh-HK", StringComparison.OrdinalIgnoreCase);
        var words = key switch
        {
            "Title" => ("Episode migration preview", "剧集迁移预览", "劇集遷移預覽"),
            "Preview" => ("Preview", "预览", "預覽"),
            "Close" => ("Close", "关闭", "關閉"),
            "Libraries" => ("Libraries to preview", "预览媒体库", "預覽媒體庫"),
            "Library" => ("Library", "媒体库", "媒體庫"),
            "LibraryHelp" => ("Select libraries explicitly; blank never means all. Up to 64 selected libraries and 4096 raw Episode candidates across them. The native catalog query requests at most 257 IDs to detect overflow; up to 256 raw library entries are accepted. Larger catalogs produce an error, not a partial list.", "请明确选择媒体库；留空不表示全部。最多选择 64 个库，合计最多读取 4096 个原始剧集候选。原生目录查询最多请求 257 个 ID 用于检测超限，最多接受 256 个原始媒体库条目。超限会报错，而非显示部分列表。", "請明確選擇媒體庫；留空不表示全部。最多選擇 64 個媒體庫，合計最多讀取 4096 個原始劇集候選。原生目錄查詢最多請求 257 個 ID 用於偵測超限，最多接受 256 個原始媒體庫項目。超限會報錯，而非顯示部分清單。"),
            "Status" => ("Preview only", "仅预览", "僅預覽"),
            "Boundary" => ("A preview only; nothing migrated or saved. No database, journal or runtime policy changes. Use existing startup settings only after enrollment. LibraryScopeSupport remains false.", "仅供预览；尚未迁移或保存任何内容。不更改数据库、日志或运行策略。登记完成后才使用现有启动设置。LibraryScopeSupport 保持 false。", "僅供預覽；尚未遷移或儲存任何內容。不變更資料庫、日誌或執行策略。登記完成後才使用現有啟動設定。LibraryScopeSupport 保持 false。"),
            "Window" => ("Counts and identities cover this selected metadata observation window, not a database snapshot or a global collision scan.", "计数与身份核对仅覆盖本次所选元数据观察窗口，并非数据库快照或全局冲突扫描。", "計數與身分核對僅涵蓋本次所選中繼資料觀察時段，並非資料庫快照或全域衝突掃描。"),
            "Choose" => ("Choose one or more libraries, then Preview.", "选择一个或多个媒体库，然后预览。", "選擇一個或多個媒體庫，然後預覽。"),
            "Working" => ("Reading selected libraries. Cancel closes this preview and discards pending results.", "正在读取所选媒体库。取消会关闭预览并丢弃待返回结果。", "正在讀取所選媒體庫。取消會關閉預覽並捨棄待傳回結果。"),
            "Failed" => ("Preview not completed. Your selection is retained; review the error, then retry or close.", "预览尚未完成，选择已保留。请检查错误后重试或关闭。", "預覽尚未完成，選擇已保留。請檢查錯誤後重試或關閉。"),
            "NoLibraries" => ("No native collection-folder libraries are available. Close and reopen after adding a library.", "尚无原生媒体库可供选择。添加媒体库后重新打开。", "尚無原生媒體庫可供選擇。新增媒體庫後重新開啟。"),
            "Empty" => ("Select at least one library to preview. Blank does not select all libraries.", "请至少选择一个预览媒体库。留空不表示选择全部库。", "請至少選擇一個預覽媒體庫。留空不表示選擇全部媒體庫。"),
            "Budget" => ("Select at most 64 libraries; selection text is limited to 4096 characters.", "最多选择 64 个媒体库；选择文本上限为 4096 个字符。", "最多選擇 64 個媒體庫；選擇文字上限為 4096 個字元。"),
            "Selection" => ("Invalid, duplicate or unknown library ID. Select from the server list and retry.", "媒体库 ID 无效、重复或未知。请从服务器列表中选择后重试。", "媒體庫 ID 無效、重複或未知。請從伺服器清單中選擇後重試。"),
            "CatalogBudget" => ("More than 256 raw library entries. Preview catalog is not loaded; no truncated selection is offered.", "原始媒体库条目超过 256 个，预览目录未加载；不会提供截断的选择列表。", "原始媒體庫項目超過 256 個，預覽目錄未載入；不會提供截斷的選擇清單。"),
            "CatalogIdentity" => ("A native catalog entry has no valid CollectionFolder ID/GUID. Close and reopen after refreshing the library catalog.", "原生目录条目缺少有效 CollectionFolder ID/GUID。请刷新媒体库目录后重新打开。", "原生目錄項目缺少有效 CollectionFolder ID/GUID。請重新整理媒體庫目錄後重新開啟。"),
            "Identity" => ("A selected library or Episode changed identity or membership during the preview. No complete result is available; close, reopen and retry.", "预览期间所选媒体库或剧集的身份或归属已变更，尚无完整结果。请关闭并重新打开后重试。", "預覽期間所選媒體庫或劇集的身分或歸屬已變更，尚無完整結果。請關閉並重新開啟後重試。"),
            "SelectionFailed" => ("Library selection was incomplete (read, membership or 4096-candidate limit). No executable summary was produced. Choose a smaller or refreshed scope and retry.", "媒体库选择未完整完成（读取、归属或 4096 候选上限）。未生成可执行摘要。请选择更小或已刷新的范围后重试。", "媒體庫選擇未完整完成（讀取、歸屬或 4096 候選上限）。未產生可執行摘要。請選擇較小或已重新整理的範圍後重試。"),
            "Totals" => ("Libraries seen / raw candidates / unique planned Episodes", "已观察媒体库 / 原始候选 / 已计划的唯一剧集", "已觀察媒體庫 / 原始候選 / 已計畫的唯一劇集"),
            "Details" => ("Episode IDs, library IDs and planner status/reasons (first 30):", "剧集 ID、媒体库 ID 及计划器状态/原因（前 30 条）：", "劇集 ID、媒體庫 ID 及計畫器狀態/原因（前 30 筆）："),
            "FailedDetailsTruncated" => ("Library diagnostics truncated to the first 30; selection is incomplete and no item totals are available.", "媒体库诊断仅显示前 30 条；选择尚未完整完成，暂无项目总数。", "媒體庫診斷僅顯示前 30 筆；選擇尚未完整完成，暫無項目總數。"),
            "Truncated" => ("Details truncated; totals above include the complete selected observation.", "详情已截断；上方计数包含本次所选观察的全部结果。", "詳情已截斷；上方計數包含本次所選觀察的全部結果。"),
            "NoCandidates" => ("No existing Episode candidates in the selected libraries.", "所选媒体库中没有现有剧集候选。", "所選媒體庫中沒有現有劇集候選。"),
            "Busy" => ("A preview is already running in this dialog. Wait for it or Cancel; no second scan was started.", "此对话框中已有预览正在进行。请等待或取消；未启动第二次扫描。", "此對話框中已有預覽正在進行。請等待或取消；未啟動第二次掃描。"),
            "Owner" => ("The current enabled administrator who opened this dialog must preview or close it.", "请由打开此对话框且当前仍已启用的管理员预览或关闭。", "請由開啟此對話框且目前仍已啟用的管理員預覽或關閉。"),
            "Command" => ("Unsupported command. Use the native buttons shown in this dialog.", "未识别的命令，请使用此对话框显示的原生按钮。", "未識別的命令，請使用此對話框顯示的原生按鈕。"),
            "ReadFailed" => ("A native metadata read did not complete. Retry the preview or close this dialog.", "原生元数据读取尚未完成。请重试预览或关闭对话框。", "原生中繼資料讀取尚未完成。請重試預覽或關閉對話框。"),
            "Content" => ("Expected migration preview selection.", "需要迁移预览选择内容。", "需要遷移預覽選擇內容。"),
            "Closed" => ("This preview is closed.", "此预览已关闭。", "此預覽已關閉。"),
            "ExecutionTruncated" => ("Showing the first 30 item results. Totals include all requested items, including pending or unconfirmed items.", "仅显示前 30 条项目结果。总数包含全部请求项目，包括待处理或未确认的项目。", "僅顯示前 30 筆項目結果。總數包含全部請求項目，包括待處理或未確認的項目。"),
            "InitialTitle" => ("Initial migration", "首次迁移", "首次遷移"),
            "ApplyInitial" => ("Apply initial migration", "执行首次迁移", "執行首次遷移"),
            "ConfirmInitial" => ("I confirm migration of exactly these preview items", "我确认仅迁移本次预览的这些项目", "我確認僅遷移本次預覽的這些項目"),
            "ConfirmRequired" => ("Select the confirmation checkbox, then use Apply initial migration. Nothing was started.", "请勾选确认框，再点击执行首次迁移。尚未启动操作。", "請勾選確認框，再點選執行首次遷移。尚未啟動操作。"),
            "InitialBoundary" => ("Copies missing native user-history rows and remaps only these preview items to library-scoped keys. Successful completion retains these mappings for this runtime. Does not enable future startup recovery, automatic catch-up, or the full global flag; LibraryScopeSupport remains false.", "复制缺失的原生用户历史记录，并且只将本次预览项目重新映射到媒体库范围键。成功完成后在当前运行期保留这些映射。不启用未来启动恢复、自动补录或全局完整支持开关；LibraryScopeSupport 保持 false。", "複製缺少的原生使用者歷史紀錄，並且只將本次預覽項目重新對應到媒體庫範圍鍵。成功完成後在目前執行期間保留這些對應。不啟用未來啟動復原、自動補錄或全域完整支援開關；LibraryScopeSupport 保持 false。"),
            "InitialEligibility" => ("All preview rows are Ready. This is eligibility, not execution approval: the backend rechecks the exact identities, enabled administrator, inactive policy and empty disabled journal at execution. Preview and Close never apply changes.", "全部预览行均为 Ready，这仅表示候选资格，而非保证可执行：后端执行时再次核对准确身份、已启用管理员、未激活策略以及空且未启用的日志。预览和关闭从不启动迁移。", "全部預覽列均為 Ready，這僅表示候選資格，而非保證可執行：後端執行時再次核對準確身分、已啟用管理員、未啟用策略以及空且未啟用的日誌。預覽和關閉從不啟動遷移。"),
            "CancelBoundary" => ("Cancel migration requests cancellation and keeps this report open. Close requests cancellation and returns to the original page. Committed items and journal intents remain; cancellation does not roll them back.", "取消迁移会请求停止并保留此报告。关闭会请求停止并返回原页面。已提交项目和日志意图仍保留；取消不会回滚它们。", "取消遷移會請求停止並保留此報告。關閉會請求停止並返回原頁面。已提交項目與日誌意圖仍保留；取消不會回復它們。"),
            "CancelMigration" => ("Cancel migration", "取消迁移", "取消遷移"),
            "NotStarted" => ("Not started", "尚未启动", "尚未啟動"),
            "Running" => ("Migration running or queued", "迁移正在执行或排队", "遷移正在執行或排隊"),
            "Cancelling" => ("Cancellation requested; waiting for the current operation to settle", "已请求取消；正在等待当前操作结束", "已請求取消；正在等待目前操作結束"),
            "Finished" => ("Operation settled — see actual report below", "操作已结束 — 请查看下方实际报告", "操作已結束 — 請查看下方實際報告"),
            "InitialUnavailable" => ("Initial migration is unavailable or has already been attempted in this dialog. No duplicate operation was started.", "此处尚未满足首次迁移条件，或此对话框已经尝试过迁移。未启动重复操作。", "此處尚未符合首次遷移條件，或此對話框已經嘗試過遷移。未啟動重複操作。"),
            "CancelledBeforeReport" => ("Cancellation observed before the API returned a final report; no rollback is inferred.", "API 返回最终报告前已观察到取消；这不代表已回滚。", "API 傳回最終報告前已觀察到取消；這不代表已回復。"),
            "InspectRecovery" => ("Inspect the journal and current mappings for explicit recovery. Initial retry is not offered here; existing partial intent is preserved, not cleared or automatically retried.", "请检查日志及当前映射，再进行显式恢复。此处不提供首次迁移重试；现有部分意图会保留，不会清除或自动重试。", "請檢查日誌及目前對應，再進行明確復原。此處不提供首次遷移重試；現有部分意圖會保留，不會清除或自動重試。"),
            "CapturedPreview" => ("Captured pre-migration observation (not current execution counts)", "迁移前捕获的观察结果（不是当前执行计数）", "遷移前擷取的觀察結果（不是目前執行計數）"),
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        return zh ? tw ? words.Item3 : words.Item2 : words.Item1;
    }
}

/// <summary>Server-owned detached observation counts; hidden from the native edit tree, available to host diagnostics.</summary>
public sealed class EpisodeMigrationPreviewResultContent : CultureAwareOptionsBase
{
    public EpisodeMigrationPreviewResultContent() { }
    internal EpisodeMigrationPreviewResultContent(string report, int libraries, long candidates, int ready, int scoped, int blocked, bool truncated, EpisodeScopeBatch.Report? executionReport = null, bool executionRunning = false)
    {
        Report = new StatusItem { Caption = EpisodeMigrationPreviewContent.Text("Status"), Status = ItemStatus.None, StatusText = report };
        SelectedLibraries = libraries; Candidates = candidates; Ready = ready; AlreadyScoped = scoped; Blocked = blocked;
        SelectionComplete = true; DetailsTruncated = truncated;
        ExecutionReport = executionReport == null ? null : executionReport with { Items = Array.AsReadOnly(executionReport.Items.ToArray()) };
        ExecutionRunning = executionRunning;
    }
    [Browsable(false)] public override string EditorTitle => "";
    [DisplayName(""), ReadOnly(true)] public StatusItem Report { get; set; } = new();
    [Browsable(false)] public int SelectedLibraries { get; }
    [Browsable(false)] public long Candidates { get; }
    [Browsable(false)] public int Ready { get; }
    [Browsable(false)] public int AlreadyScoped { get; }
    [Browsable(false)] public int Blocked { get; }
    [Browsable(false)] public bool SelectionComplete { get; }
    [Browsable(false)] public bool DetailsTruncated { get; }
    [Browsable(false)] public EpisodeScopeBatch.Report? ExecutionReport { get; }
    [Browsable(false)] public bool ExecutionRunning { get; }
    [Browsable(false)] public bool ShowProgress => ExecutionRunning || ExecutionReport != null;
    // Display-only state is rebuilt by PreviewResult on every native request/notification.
    [Browsable(false)] public bool MigrationAvailable { get; internal set; }
    [Browsable(false)] public bool ConfirmationEditable { get; internal set; }
    public bool ConfirmInitialMigration { get; set; }
    public ButtonItem ApplyInitialMigration { get; set; } = new();
    // Keep the editor in the native tree: same-view updates reveal it without rebuilding the dialog DOM.
    [DisplayName(""), ReadOnly(true), VisibleCondition(nameof(ShowProgress), SimpleCondition.IsTrue)] public ProgressItem Progress { get; set; } = new();
    [DisplayName(""), ReadOnly(true)] public StatusItem Execution { get; set; } = new();
    public ButtonItem CancelInitialMigration { get; set; } = new();
    public override IEditObjectContainer CreateEditContainer()
    {
        var container = (EditObjectContainer)base.CreateEditContainer();
        if (!MigrationAvailable)
        {
            container.EditorRoot.EditorItems = container.EditorRoot.EditorItems.Where(e => e.Name == nameof(Report)).ToArray();
            container.EditorRoot.PropertyConditions = Array.Empty<Emby.Web.GenericEdit.Conditions.PropertyCondition>();
        }
        foreach (var editor in container.EditorRoot.EditorItems)
            if (editor.Name == nameof(ConfirmInitialMigration)) { editor.DisplayName = EpisodeMigrationPreviewContent.Text("ConfirmInitial"); editor.IsReadOnly = !ConfirmationEditable; }
        return container;
    }
    internal EpisodeMigrationPreviewResultContent Copy(EpisodeScopeBatch.Report? executionReport = null, bool running = false) => new(Report.StatusText, SelectedLibraries, Candidates, Ready, AlreadyScoped, Blocked, DetailsTruncated, executionReport, running);
}
