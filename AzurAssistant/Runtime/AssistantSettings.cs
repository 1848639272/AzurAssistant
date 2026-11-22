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
    /// <summary>界面元素（边框、分隔线、强调色）透明度百分比 0–100。</summary>
    public int UiOpacity { get; init; } = 100;
    /// <summary>文字透明度百分比 0–100。</summary>
    public int TextOpacity { get; init; } = 100;
    /// <summary>背景表面（卡片、输入框、侧栏）透明度百分比 0–100。</summary>
    public int BackgroundOpacity { get; init; } = 100;
    /// <summary>整个窗口（含外框）不透明度百分比 0–100。</summary>
    public int WindowOpacity { get; init; } = 100;
    /// <summary>自定义背景图（绝对路径），空表示不使用。</summary>
    public string BackgroundImagePath { get; init; } = "";
    /// <summary>"stretch"=拉伸铺满；"fit"=保持比例，留白。</summary>
    public string BackgroundImageMode { get; init; } = "stretch";
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
    /// <summary>提示音音量百分比（0–100）。</summary>
    public int ScheduleSoundVolume { get; init; } = 70;
}
