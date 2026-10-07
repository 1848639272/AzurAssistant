namespace AzurAssistant.Runtime;

/// <summary>Immutable response-speed choice captured at the beginning of a run.</summary>
public readonly record struct ResponseTiming
{
    public bool LowPerformanceMode { get; }
    public double Factor => LowPerformanceMode ? 1.5 : 1;

    private ResponseTiming(bool lowPerformanceMode) => LowPerformanceMode = lowPerformanceMode;

    public static ResponseTiming FromLowPerformanceMode(bool enabled) => new(enabled);

    // Input validity and physical key/button hold durations are deliberately not scaled.
    public TimeSpan Scale(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        return duration * Factor;
    }
}
