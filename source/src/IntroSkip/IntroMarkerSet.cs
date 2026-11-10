namespace StrmAssistantRebuild.IntroSkip;

public sealed record IntroMarkerSet(long? IntroStartTicks = null, long? IntroEndTicks = null, long? CreditsStartTicks = null)
{
    public bool HasIntro => IntroStartTicks.HasValue && IntroEndTicks.HasValue;
    public bool HasCredits => CreditsStartTicks.HasValue;
    public void Validate(long? runtime)
    {
        if (IntroStartTicks.HasValue != IntroEndTicks.HasValue || !HasIntro && !HasCredits)
            throw new ArgumentException("An intro needs both endpoints; at least one marker segment is required.");
        if (HasIntro && (IntroStartTicks < 0 || IntroEndTicks <= IntroStartTicks))
            throw new ArgumentException("Intro endpoints must be non-negative and increasing.");
        if (HasCredits && CreditsStartTicks < 0) throw new ArgumentException("Credits start must be non-negative.");
        if (runtime is > 0 && (IntroEndTicks >= runtime || CreditsStartTicks >= runtime))
            throw new ArgumentException("Markers must lie within the media runtime.");
    }
}
