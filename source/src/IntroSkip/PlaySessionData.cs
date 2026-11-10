using MediaBrowser.Model.Session;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.IntroSkip;

/// <summary>Playback observations only: never sends seek/playstate commands to a client.</summary>
public sealed class PlaySessionData
{
    public IntroMarkerSet Markers { get; set; }
    public long PlaybackStartTicks { get; }
    public long PreviousPositionTicks { get; private set; }
    public DateTime PreviousEventTime { get; private set; }
    public long? FirstJumpPositionTicks { get; private set; }
    public long? LastJumpPositionTicks { get; private set; }
    public long MaxIntroDurationTicks { get; private set; }
    public long MaxCreditsDurationTicks { get; }
    public long MinOpeningPlotDurationTicks { get; }
    public DateTime? LastPauseEventTime { get; private set; }
    public DateTime? LastPlaybackRateChangeEventTime { get; private set; }
    public bool NoDetectionButReset { get; }

    public PlaySessionData(IntroSkipOptions options, IntroMarkerSet markers, long positionTicks, DateTime eventTime)
    {
        Markers = markers;
        PlaybackStartTicks = PreviousPositionTicks = positionTicks;
        PreviousEventTime = eventTime;
        MaxIntroDurationTicks = Math.Max(0L, options.MaxIntroDurationSeconds) * TimeSpan.TicksPerSecond;
        MaxCreditsDurationTicks = Math.Max(0L, options.MaxCreditsDurationSeconds) * TimeSpan.TicksPerSecond;
        MinOpeningPlotDurationTicks = Math.Max(0L, options.MinOpeningPlotDurationSeconds) * TimeSpan.TicksPerSecond;
        NoDetectionButReset = HasPreference(options, IntroSkipOptions.IntroSkipPreference.NoDetectionButReset);
    }

    public static bool HasPreference(IntroSkipOptions options, IntroSkipOptions.IntroSkipPreference preference) =>
        (options.IntroSkipPreferences ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => Enum.TryParse<IntroSkipOptions.IntroSkipPreference>(value, true, out var parsed) && parsed == preference);

    /// <summary>OSS Monitor:178–307; Pro additionally gates reset/detection on max duration > 0.</summary>
    public IntroMarkerSet? Progress(ProgressEvent kind, long position, long? runtime, DateTime at)
    {
        if (position <= 0 || at < PreviousEventTime) return null;
        if (kind != ProgressEvent.TimeUpdate && kind != ProgressEvent.Pause &&
            kind != ProgressEvent.Unpause && kind != ProgressEvent.PlaybackRateChange) return null;

        long? start = null, end = null, credits = null;
        if (!NoDetectionButReset && kind == ProgressEvent.TimeUpdate && !Markers.IntroEndTicks.HasValue)
        {
            var elapsed = (at - PreviousEventTime).TotalSeconds;
            var delta = TimeSpan.FromTicks(position - PreviousPositionTicks).TotalSeconds;
            // There is no Seek event in the Emby 4.10.2 contract. Both directions are inferred here.
            if (Math.Abs(delta) - elapsed > 5 && position < MaxIntroDurationTicks)
            {
                if (!FirstJumpPositionTicks.HasValue && PlaybackStartTicks < 5 * TimeSpan.TicksPerSecond && delta > 0)
                {
                    FirstJumpPositionTicks = PreviousPositionTicks;
                    if (PreviousPositionTicks > MinOpeningPlotDurationTicks)
                        MaxIntroDurationTicks += PreviousPositionTicks;
                }
                LastJumpPositionTicks = position;
            }
            if (MaxIntroDurationTicks > 0 && position >= MaxIntroDurationTicks && LastJumpPositionTicks.HasValue)
            {
                start = FirstJumpPositionTicks > MinOpeningPlotDurationTicks ? FirstJumpPositionTicks : 0;
                end = LastJumpPositionTicks;
            }
            PreviousPositionTicks = position;
            PreviousEventTime = at;
        }

        if (kind == ProgressEvent.Pause) { LastPauseEventTime = at; return null; }
        if (kind == ProgressEvent.PlaybackRateChange) { LastPlaybackRateChangeEventTime = at; return null; }
        if (kind == ProgressEvent.Unpause && LastPauseEventTime.HasValue)
        {
            var pauseMs = (at - LastPauseEventTime.Value).TotalMilliseconds;
            if (pauseMs < (LastPlaybackRateChangeEventTime.HasValue ? 1500 : 500))
            {
                LastPauseEventTime = null;
                return null;
            }
            if (pauseMs > 500 && pauseMs < 5000)
            {
                var changedEnd = !Markers.IntroEndTicks.HasValue ||
                    Math.Abs(TimeSpan.FromTicks(position - Markers.IntroEndTicks.Value).TotalMilliseconds) >
                    (LastPlaybackRateChangeEventTime.HasValue ? 500 : 0);
                if (MaxIntroDurationTicks > 0 && changedEnd)
                {
                    if (!NoDetectionButReset && Markers.IntroStartTicks.HasValue &&
                        Markers.IntroStartTicks < position && Markers.IntroEndTicks.HasValue &&
                        position < Math.Max(MaxIntroDurationTicks, Markers.IntroEndTicks.Value))
                    {
                        start = Markers.IntroStartTicks;
                        end = position;
                    }
                    else if (NoDetectionButReset && position < MaxIntroDurationTicks)
                    {
                        start = 0;
                        end = position;
                    }
                }
                if (MaxCreditsDurationTicks > 0 && runtime.HasValue && position > runtime - MaxCreditsDurationTicks &&
                    position < runtime && (Markers.CreditsStartTicks.HasValue || NoDetectionButReset)) credits = position;
            }
        }
        return end.HasValue || credits.HasValue ? new IntroMarkerSet(start, end, credits) : null;
    }

    /// <summary>OSS Monitor:309–327 and Pro:81159–81322: reset-only still permits first credits on stop.</summary>
    public IntroMarkerSet? Stop(long position, long? runtime) =>
        !Markers.CreditsStartTicks.HasValue && MaxCreditsDurationTicks > 0 && runtime.HasValue && position > 0 &&
        position > runtime - MaxCreditsDurationTicks && position < runtime
            ? new IntroMarkerSet(CreditsStartTicks: position) : null;
}
