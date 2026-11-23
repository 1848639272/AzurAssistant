using System.Globalization;

namespace AzurAssistant.Runtime;

/// <summary>One reminder the assistant should fire: a daily reset or a stamina top-up nudge.</summary>
public enum ReminderKind
{
    DailyReset,
    Stamina,
}

public sealed record ReminderEvent(ReminderKind Kind, string Title, string Message, DateTimeOffset Due);

/// <summary>
/// Decides when the periodic reminders are due. Pure time arithmetic so it can be unit-tested without a timer.
/// </summary>
public sealed class ReminderScheduler(int dailyResetHour, int staminaMinutes, TimeSpan? staminaFullEvery = null)
{
    public const int MaxStaminaMinutes = 1440;

    /// <summary>Next daily reset strictly after <paramref name="now"/>.</summary>
    public DateTimeOffset NextDailyReset(DateTimeOffset now)
    {
        var hour = Math.Clamp(dailyResetHour, 0, 23);
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, now.Offset);
        return today > now ? today : today.AddDays(1);
    }

    /// <summary>True when a daily-reset reminder is due (within the last minute) and was not fired for that reset yet.</summary>
    public bool IsDailyResetDue(DateTimeOffset now, DateTimeOffset? lastFired)
    {
        var reset = NextDailyReset(now.AddMinutes(-1));
        if (now < reset) return false;
        return lastFired is null || lastFired < reset;
    }

    /// <summary>True when the stamina nudge is due; the interval is clamped to 1–1440 minutes.</summary>
    public bool IsStaminaDue(DateTimeOffset now, DateTimeOffset? lastFired)
    {
        if (staminaMinutes <= 0) return false;
        if (lastFired is null) return true;
        return now - lastFired.Value >= TimeSpan.FromMinutes(Math.Clamp(staminaMinutes, 1, MaxStaminaMinutes));
    }

    public IReadOnlyList<ReminderEvent> Due(DateTimeOffset now, DateTimeOffset? lastDailyReset, DateTimeOffset? lastStamina)
    {
        var events = new List<ReminderEvent>();
        if (IsDailyResetDue(now, lastDailyReset))
            events.Add(new ReminderEvent(ReminderKind.DailyReset, "每日重置",
                $"{dailyResetHour:00}:00 每日重置：体力、委托、周常都刷新了。", NextDailyReset(now.AddMinutes(-1))));
        if (IsStaminaDue(now, lastStamina))
            events.Add(new ReminderEvent(ReminderKind.Stamina, "体力提醒",
                $"已经过了 {staminaMinutes} 分钟，记得清一下体力。", now));
        return events;
    }

    public static string Format(TimeSpan span) =>
        span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} 小时 {span.Minutes} 分")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, span.Minutes)} 分钟");
}

/// <summary>Remembers when each reminder last fired so restarts do not re-notify.</summary>
public sealed class ReminderStateStore(string path)
{
    public (DateTimeOffset? DailyReset, DateTimeOffset? Stamina) Load()
    {
        try
        {
            if (!System.IO.File.Exists(path)) return (null, null);
            var parts = System.IO.File.ReadAllLines(path);
            DateTimeOffset? Parse(int index) => parts.Length > index && long.TryParse(parts[index], out var value) && value > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(value) : null;
            return (Parse(0), Parse(1));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return (null, null); }
    }

    public void Save(DateTimeOffset? dailyReset, DateTimeOffset? stamina)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (directory is { Length: > 0 }) System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllLines(path,
            [
                (dailyReset?.ToUnixTimeMilliseconds() ?? 0).ToString(CultureInfo.InvariantCulture),
                (stamina?.ToUnixTimeMilliseconds() ?? 0).ToString(CultureInfo.InvariantCulture),
            ]);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Screen pixel colour sampling for the coordinate picker.</summary>
public static class PixelColor
{
    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    public static bool TryParse(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (hex is null) return false;
        var text = hex.Trim().TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;
        r = (byte)((value >> 16) & 0xFF);
        g = (byte)((value >> 8) & 0xFF);
        b = (byte)(value & 0xFF);
        return true;
    }
}
