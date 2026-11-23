namespace AzurAssistant.Runtime;

/// <summary>
/// Estimates how long a task will take from the run history, and turns that into a progress fraction and a
/// remaining-time label. Pure functions so the behaviour can be unit-tested.
/// </summary>
public static class TaskDurationEstimator
{
    /// <summary>Records used for the average; older runs are ignored so the estimate follows recent speed.</summary>
    public const int SampleSize = 10;

    /// <summary>Average duration in seconds for the given task name, or null when there is no usable history.</summary>
    public static double? AverageSeconds(IReadOnlyList<RunRecord> records, string taskName)
    {
        var samples = records.Where(record => string.Equals(record.TaskName, taskName, StringComparison.Ordinal)
                && record.DurationSeconds > 0)
            .OrderByDescending(record => record.StartedAt)
            .Take(SampleSize)
            .ToList();
        if (samples.Count == 0) return null;
        return samples.Average(record => record.DurationSeconds);
    }

    /// <summary>
    /// Progress fraction for a running task. Stays below 1 while running so the bar never completes early; returns 1
    /// once the task is no longer running.
    /// </summary>
    public static double Progress(double elapsedSeconds, double? estimatedSeconds, bool running)
    {
        if (!running) return 0;
        if (estimatedSeconds is not { } estimate || estimate <= 0) return 0;
        var fraction = elapsedSeconds / estimate;
        return Math.Clamp(Math.Min(fraction, 0.97), 0, 0.97);
    }

    /// <summary>Remaining time label, or an empty string when there is nothing to show.</summary>
    public static string Remaining(double elapsedSeconds, double? estimatedSeconds)
    {
        if (estimatedSeconds is not { } estimate || estimate <= 0) return "";
        var left = Math.Max(0, estimate - elapsedSeconds);
        if (left < 1) return "即将结束";
        if (left < 60) return $"预计还剩 {(int)left} 秒";
        var minutes = (int)(left / 60);
        var seconds = (int)(left % 60);
        return seconds == 0 ? $"预计还剩 {minutes} 分" : $"预计还剩 {minutes} 分 {seconds} 秒";
    }
}

/// <summary>Writes a diagnostic bundle when the assistant hits an unhandled exception.</summary>
public static class CrashReporter
{
    public static string? LastBundlePath { get; private set; }
    public static string? LastError { get; private set; }

    /// <summary>Builds a bundle under <paramref name="directory"/> and appends the stack trace next to it.</summary>
    public static string? Report(string directory, string source, Exception exception, Func<string, string> createBundle)
    {
        LastError = source + "：" + exception.GetType().Name + "：" + exception.Message;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var tracePath = System.IO.Path.Combine(directory, $"crash-{stamp}.txt");
            System.IO.File.WriteAllText(tracePath,
                $"{LastError}{Environment.NewLine}{DateTimeOffset.Now:O}{Environment.NewLine}{source}"
                + $"{Environment.NewLine}{exception}{Environment.NewLine}");
            var bundle = createBundle(System.IO.Path.Combine(directory, $"crash-bundle-{stamp}.zip"));
            LastBundlePath = bundle;
            return bundle;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
