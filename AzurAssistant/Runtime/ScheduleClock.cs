using System.Globalization;

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
        if (!TimeSpan.TryParseExact(trimmed.Length <= 4 ? trimmed : trimmed, ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var parsed))
            return false;
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

    /// <summary>Next fire time, or null when the schedule is disabled or its configuration is invalid.</summary>
    public static DateTimeOffset? Next(AssistantSettings settings, DateTimeOffset now, DateTimeOffset? lastRun)
    {
        if (!settings.ScheduleEnabled) return null;
        if (!TryParseMode(settings.ScheduleMode, out var mode)) return null;
        if (mode == ScheduleModeKind.Interval)
        {
            var interval = TimeSpan.FromMinutes(Math.Clamp(settings.ScheduleIntervalMinutes, 1, 1440));
            return (lastRun is { } previous && previous <= now ? previous : now) + interval;
        }
        if (!TryParseTime(settings.ScheduleTime, out var time)) return null;
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, time.Hours, time.Minutes, 0, now.Offset);
        // A run already happened at or after today's slot: wait for tomorrow's slot.
        if (lastRun is { } last && last.Date >= now.Date && candidate <= last) candidate = candidate.AddDays(1);
        while (candidate <= now) candidate = candidate.AddDays(1);
        return candidate;
    }

    public static string Describe(AssistantSettings settings)
    {
        if (!settings.ScheduleEnabled) return "未启用";
        if (!TryParseMode(settings.ScheduleMode, out var mode)) return "配置无效";
        if (mode == ScheduleModeKind.Interval)
        {
            var minutes = Math.Clamp(settings.ScheduleIntervalMinutes, 1, 1440);
            return minutes % 60 == 0 ? $"每 {minutes / 60} 小时一次" : $"每 {minutes} 分钟一次";
        }
        return TryParseTime(settings.ScheduleTime, out var time) ? $"每天 {time.Hours:00}:{time.Minutes:00}" : "时刻无效";
    }
}
