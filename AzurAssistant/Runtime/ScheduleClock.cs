using System.Globalization;
using System.IO;
using System.Media;

namespace AzurAssistant.Runtime;

public enum ScheduleModeKind { Daily, Interval }

/// <summary>Pure scheduling math: when should the next scheduled run happen?</summary>
public static class ScheduleClock
{
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (!TimeSpan.TryParseExact(trimmed, ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var parsed)) return false;
        if (parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1)) return false;
        time = parsed;
        return true;
    }

    public static bool TryParseMode(string? text, out ScheduleModeKind mode)
    {
        mode = ScheduleModeKind.Daily;
        if (string.Equals(text, "interval", StringComparison.OrdinalIgnoreCase)) { mode = ScheduleModeKind.Interval; return true; }
        return string.Equals(text, "daily", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Next fire time for one task, or null when it is disabled or misconfigured.</summary>
    public static DateTimeOffset? Next(ScheduleTask task, DateTimeOffset now, DateTimeOffset? lastRun)
    {
        if (!task.Enabled) return null;
        if (!TryParseMode(task.Mode, out var mode)) return null;
        if (mode == ScheduleModeKind.Interval)
        {
            var interval = TimeSpan.FromMinutes(Math.Clamp(task.IntervalMinutes, 1, 1440));
            return (lastRun is { } previous && previous <= now ? previous : now) + interval;
        }
        if (!TryParseTime(task.Time, out var time)) return null;
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, time.Hours, time.Minutes, 0, now.Offset);
        // A run already happened at or after today's slot: wait for tomorrow's slot.
        if (lastRun is { } last && last.Date >= now.Date && candidate <= last) candidate = candidate.AddDays(1);
        while (candidate <= now) candidate = candidate.AddDays(1);
        return candidate;
    }

    public static string Describe(ScheduleTask task)
    {
        if (!task.Enabled) return "已停用";
        if (!TryParseMode(task.Mode, out var mode)) return "配置无效";
        if (mode == ScheduleModeKind.Interval)
        {
            var minutes = Math.Clamp(task.IntervalMinutes, 1, 1440);
            return minutes % 60 == 0 ? $"每 {minutes / 60} 小时" : $"每 {minutes} 分钟";
        }
        return TryParseTime(task.Time, out var time) ? $"每天 {time.Hours:00}:{time.Minutes:00}" : "时刻无效";
    }

    /// <summary>Startup chime presets, selectable separately for start and finish.</summary>
    public static IReadOnlyList<string> SoundPresets { get; } = ["chime", "soft", "deep", "double"];

    public static string SoundLabel(string? preset) => preset switch
    {
        "soft" => "柔和",
        "deep" => "低沉",
        "double" => "双响",
        _ => "清脆",
    };

    public static string NormalizeSound(string? preset) => SoundPresets.Contains(preset) ? preset! : "chime";
}
