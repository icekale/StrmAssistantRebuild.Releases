using System.Threading.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

/// <summary>4.10.2 uses IUserManager.UserSettings, not the removed IDisplayPreferencesRepository.</summary>
public sealed class UserPreferenceService : IDisposable
{
    private readonly IUserManager users;
    private readonly IUserViewManager views;
    private readonly ISessionManager sessions;
    private readonly ILogger logger;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<Request> queue = Channel.CreateBounded<Request>(new BoundedChannelOptions(16)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private sealed record Request(int Generation, CancellationToken Token, TaskCompletionSource<bool>? Done = null);
    private bool libraryOrder, seriesDisplay;
    private int generation, disposed;
    private long dropped;
    public Task Completion { get; }
    public long DroppedEventCount => Interlocked.Read(ref dropped);

    public UserPreferenceService(IUserManager users, IUserViewManager views, ISessionManager sessions, ILogger logger)
    {
        this.users = users; this.views = views; this.sessions = sessions; this.logger = logger;
        Completion = Task.Run(RunAsync);
        users.UserCreated += OnUserChanged;
        users.UserConfigurationUpdated += OnUserChanged;
        users.UserPolicyUpdated += OnUserChanged;
        sessions.SessionStarted += OnSessionStarted;
    }

    public void Configure(UIFunctionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (gate)
        {
            if (disposed != 0) return;
            generation++;
            var scope = Selected(options.EnforceUserPreferencesScope);
            libraryOrder = options.EnforceUserPreferences && (scope.Count == 0 || scope.Contains("LibraryOrder") || scope.Contains("0"));
            seriesDisplay = options.EnforceUserPreferences && (scope.Count == 0 || scope.Contains("SeriesDisplay") || scope.Contains("1"));
            Enqueue();
        }
    }

    internal static HashSet<string> Selected(string? text) => (text ?? "").Split(new[] { ',', ';' },
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private void OnUserChanged(object? sender, GenericEventArgs<User> e) { lock (gate) Enqueue(); }
    private void OnSessionStarted(object? sender, SessionEventArgs e) { lock (gate) Enqueue(); }
    private void Enqueue()
    {
        if (disposed != 0 || !(libraryOrder || seriesDisplay)) return;
        if (!queue.Writer.TryWrite(new(generation, default))) Interlocked.Increment(ref dropped);
    }

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        int version;
        lock (gate)
        {
            if (disposed != 0) throw new ObjectDisposedException(nameof(UserPreferenceService));
            version = generation;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, cancellationToken);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new(version, cancellationToken, done), linked.Token).ConfigureAwait(false);
        await done.Task.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var request in queue.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, request.Token);
                    Apply(request.Generation, linked.Token);
                    request.Done?.TrySetResult(true);
                }
                catch (OperationCanceledException) { request.Done?.TrySetCanceled(); }
                catch (Exception error)
                {
                    logger.Warn("[StrmAssistantRebuild] User display preference copy failed: {0}", error.Message);
                    request.Done?.TrySetException(error);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        finally { while (queue.Reader.TryRead(out var request)) request.Done?.TrySetCanceled(); }
    }

    private void Apply(int version, CancellationToken token)
    {
        bool order, display;
        lock (gate)
        {
            if (disposed != 0 || generation != version) return;
            order = libraryOrder; display = seriesDisplay;
        }
        if (!order && !display) return;
        token.ThrowIfCancellationRequested();
        var enabled = users.GetUserList(new UserQuery { IsDisabled = false }).Where(u => !u.Policy.IsDisabled).ToArray();
        var admin = enabled.Where(u => u.Policy.IsAdministrator).OrderBy(u => u.DateCreated).ThenBy(u => u.InternalId).FirstOrDefault();
        if (admin == null) return;
        var sourceOrder = (users.GetUserConfiguration(admin).OrderedViews ?? Array.Empty<string>()).ToArray();
        // Actual 4.10.2 dashboard usersettingsbuilder: get("seriesDisplay") || "episodes".
        var sourceDisplay = display && users.GetUserSettings(admin.InternalId, token).TryGetValue("seriesDisplay", out var selected)
            && !string.IsNullOrWhiteSpace(selected) ? selected : "episodes";
        foreach (var user in enabled.Where(u => u.InternalId != admin.InternalId))
        {
            token.ThrowIfCancellationRequested();
            try
            {
            if (order)
            {
                var target = users.GetUserConfiguration(user);
                var visible = views.GetUserViews(new UserViewQuery { UserId = user.InternalId });
                var ordered = VisibleOrder(sourceOrder, target.OrderedViews ?? Array.Empty<string>(), visible);
                if (!(target.OrderedViews ?? Array.Empty<string>()).SequenceEqual(ordered, StringComparer.OrdinalIgnoreCase))
                {
                    var copy = Copy(target); copy.OrderedViews = ordered;
                    lock (gate)
                    {
                        if (disposed != 0 || generation != version) return;
                        token.ThrowIfCancellationRequested();
                        users.UpdateConfiguration(user, copy);
                    }
                }
            }
            if (display)
            {
                var target = users.GetUserSettings(user.InternalId, token);
                if (target.TryGetValue("seriesDisplay", out var existing) && existing == sourceDisplay) continue;
                lock (gate)
                {
                    if (disposed != 0 || generation != version) return;
                    token.ThrowIfCancellationRequested();
                    users.UpdatePartialUserSettings(user.InternalId, new Dictionary<string, string> { ["seriesDisplay"] = sourceDisplay });
                }
            }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { logger.Warn("[StrmAssistantRebuild] User {0} preference copy failed: {1}", user.InternalId, error.Message); }
        }
    }

    internal static string[] VisibleOrder(string[] source, string[] target, Folder[] visible)
    {
        var result = new List<string>(); var used = new HashSet<long>();
        foreach (var id in source.Concat(target))
        {
            var folder = visible.FirstOrDefault(f => id == f.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                Guid.TryParse(id, out var guid) && guid == f.Id);
            if (folder != null && used.Add(folder.InternalId)) result.Add(id);
        }
        // ponytail: linear lookup over a small library list; use an alias map only if library counts warrant it.
        foreach (var folder in visible)
            if (used.Add(folder.InternalId)) result.Add(folder.Id.ToString("N"));
        return result.ToArray();
    }

    // Retain the target's legacy field on the validated 4.10.2 public configuration contract.
#pragma warning disable CS0612
    private static UserConfiguration Copy(UserConfiguration value) => new()
    {
        AudioLanguagePreference = value.AudioLanguagePreference, PlayDefaultAudioTrack = value.PlayDefaultAudioTrack,
        SubtitleLanguagePreference = value.SubtitleLanguagePreference, ProfilePin = value.ProfilePin,
        DisplayMissingEpisodes = value.DisplayMissingEpisodes, SubtitleMode = value.SubtitleMode,
        OrderedViews = value.OrderedViews?.ToArray(), LatestItemsExcludes = value.LatestItemsExcludes?.ToArray(),
        MyMediaExcludes = value.MyMediaExcludes?.ToArray(), HidePlayedInLatest = value.HidePlayedInLatest,
        HidePlayedInMoreLikeThis = value.HidePlayedInMoreLikeThis, HidePlayedInSuggestions = value.HidePlayedInSuggestions,
        RememberAudioSelections = value.RememberAudioSelections, RememberSubtitleSelections = value.RememberSubtitleSelections,
        EnableNextEpisodeAutoPlay = value.EnableNextEpisodeAutoPlay, ResumeRewindSeconds = value.ResumeRewindSeconds,
        IntroSkipMode = value.IntroSkipMode, EnableLocalPassword = value.EnableLocalPassword
    };
#pragma warning restore CS0612

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed != 0) return;
            disposed = 1; libraryOrder = seriesDisplay = false; generation++;
            users.UserCreated -= OnUserChanged; users.UserConfigurationUpdated -= OnUserChanged;
            users.UserPolicyUpdated -= OnUserChanged; sessions.SessionStarted -= OnSessionStarted;
            queue.Writer.TryComplete(); shutdown.Cancel();
        }
    }
}
