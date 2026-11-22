namespace AzurAssistant.Runtime;

public sealed record AssistantSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string GameExecutablePath { get; init; } = "";
    public bool AutoConnect { get; init; }
    public bool AutoUpdate { get; init; }
    public bool AutoRunDaily { get; init; }
    public bool LowPerformanceMode { get; init; }
    public bool ShowPreview { get; init; } = true;
    public bool DarkMode { get; init; }
    public string PauseHotkey { get; init; } = "F9";
    public string StopHotkey { get; init; } = "F10";
    public bool LaunchGame { get; init; }
    public bool EnterGame { get; init; } = true;
    public bool ExitGame { get; init; }
    public int ExitGameDelaySeconds { get; init; }
    public bool CloseAssistantAfterExitGame { get; init; }
    public bool ClaimMail { get; init; }
    public bool HomeCenter { get; init; }
    public bool ClaimActivity { get; init; }
    public bool ClaimBattlePass { get; init; }
    public bool DailyCommissions { get; init; }
    public bool WeeklyEchoes { get; init; }
    public bool CrisisRaids { get; init; }
    public bool StoryCommissions { get; init; }
    public string DailyTaskOrder { get; init; } = "";
    public bool AutoSkipDialogue { get; init; }
    public bool AutoPickup { get; init; }
    public bool ScheduleEnabled { get; init; }
    /// <summary>"daily"（每天固定时刻）或 "interval"（每隔若干分钟）。</summary>
    public string ScheduleMode { get; init; } = "daily";
    /// <summary>每天执行的时刻，"HH:mm"。</summary>
    public string ScheduleTime { get; init; } = "08:00";
    public int ScheduleIntervalMinutes { get; init; } = 60;
    /// <summary>"daily" 或 "route:名称" / "action:名称" / "wheelchair:名称"。</summary>
    public string ScheduleTarget { get; init; } = "daily";
    public bool ScheduleSound { get; init; } = true;
    /// <summary>开始提示音预设：chime / soft / deep / double。</summary>
    public string ScheduleStartSound { get; init; } = "chime";
    /// <summary>结束提示音预设：chime / soft / deep / double。</summary>
    public string ScheduleEndSound { get; init; } = "soft";
}
