using System.IO;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>每日重置提醒与体力提醒（靠一个 30 秒的计时器驱动，窗口里负责弹通知）。</summary>
public sealed partial class MainViewModel
{
    private ReminderStateStore? _reminderState;
    private DateTimeOffset? _lastDailyResetReminder;
    private DateTimeOffset? _lastStaminaReminder;
    private string _reminderStatus = "";

    private string ReminderStatePath => Path.Combine(
        Path.GetDirectoryName(SoundsDirectory) ?? AppContext.BaseDirectory, "reminder-state.txt");

    private ReminderStateStore ReminderState => _reminderState ??= new ReminderStateStore(ReminderStatePath);

    public bool ReminderDailyReset
    {
        get => Settings.ReminderDailyReset;
        set
        {
            if (value == Settings.ReminderDailyReset) return;
            if (SaveSettings(Settings with { ReminderDailyReset = value })) { Notify(); NotifyReminderStatus(); }
        }
    }

    public int ReminderStaminaMinutes
    {
        get => Settings.ReminderStaminaMinutes;
        set
        {
            var clamped = Math.Clamp(value, 0, ReminderScheduler.MaxStaminaMinutes);
            if (clamped == Settings.ReminderStaminaMinutes) return;
            if (SaveSettings(Settings with { ReminderStaminaMinutes = clamped })) { Notify(); NotifyReminderStatus(); }
        }
    }

    public string ReminderStatus { get => _reminderStatus; private set { _reminderStatus = value; Notify(); } }

    /// <summary>Loads the last-fired timestamps so a restart does not repeat an already delivered reminder.</summary>
    public void LoadReminderState()
    {
        var (dailyReset, stamina) = ReminderState.Load();
        _lastDailyResetReminder = dailyReset;
        _lastStaminaReminder = stamina;
        NotifyReminderStatus();
    }

    /// <summary>Returns the reminders that are due right now and marks them as delivered.</summary>
    public IReadOnlyList<ReminderEvent> TakeDueReminders()
    {
        var now = DateTimeOffset.Now;
        var scheduler = new ReminderScheduler(_growth?.DailyResetHour ?? 4, Settings.ReminderStaminaMinutes);
        var due = scheduler.Due(now, _lastDailyResetReminder, _lastStaminaReminder)
            .Where(item => item.Kind != ReminderKind.DailyReset || Settings.ReminderDailyReset)
            .ToList();
        foreach (var reminder in due)
        {
            if (reminder.Kind == ReminderKind.DailyReset) _lastDailyResetReminder = now;
            else _lastStaminaReminder = now;
        }
        if (due.Count > 0) ReminderState.Save(_lastDailyResetReminder, _lastStaminaReminder);
        NotifyReminderStatus();
        return due;
    }

    private void NotifyReminderStatus()
    {
        var now = DateTimeOffset.Now;
        var scheduler = new ReminderScheduler(_growth?.DailyResetHour ?? 4, Settings.ReminderStaminaMinutes);
        var reset = scheduler.NextDailyReset(now);
        var parts = new List<string>();
        parts.Add(Settings.ReminderDailyReset
            ? $"每日重置提醒：{reset:MM-dd HH:mm}（{ReminderScheduler.Format(reset - now)}后）"
            : "每日重置提醒：关闭");
        parts.Add(Settings.ReminderStaminaMinutes > 0
            ? $"体力提醒：每 {Settings.ReminderStaminaMinutes} 分钟"
            : "体力提醒：关闭");
        ReminderStatus = string.Join(" · ", parts);
    }
}
