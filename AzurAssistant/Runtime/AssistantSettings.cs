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
    /// <summary>是否启用 UID 遮挡（扩展功能）。</summary>
    public bool CoverUidEnabled { get; init; }
    /// <summary>UID 遮挡矩形相对游戏客户区左上角的像素偏移与尺寸。</summary>
    public int CoverUidX { get; init; } = 78;
    public int CoverUidY { get; init; } = 1026;
    public int CoverUidWidth { get; init; } = 132;
    public int CoverUidHeight { get; init; } = 26;
    /// <summary>遮挡样式：solid（纯色）或 mosaic（马赛克）。</summary>
    public string CoverUidStyle { get; init; } = "solid";
    /// <summary>纯色遮挡的颜色，格式 #RRGGBB。</summary>
    public string CoverUidColor { get; init; } = "#FFFFFF";
    /// <summary>遮挡框列表，格式 "x,y,w,h;x,y,w,h"（1920×1080 参考空间）；为空时用上面的单框。</summary>
    public string CoverUidBoxes { get; init; } = "";
    /// <summary>没有游戏窗口时，把遮挡层贴在本程序窗口上预览。</summary>
    public bool CoverUidPreviewOnSelf { get; init; }
    /// <summary>每日重置提醒（04:00 体力/委托刷新）。</summary>
    public bool ReminderDailyReset { get; init; }
    /// <summary>体力提醒间隔（分钟）；0 = 关闭。</summary>
    public int ReminderStaminaMinutes { get; init; }
    /// <summary>在线仓库的索引地址（http/https），空表示未配置。</summary>
    public string RepositoryUrl { get; init; } = "";
    /// <summary>司凤小游戏的识别参数（紧凑 key=value 串）。</summary>
    public string MinigameConfigText { get; init; } = "";
    public bool MinigameRhythmAuto { get; init; }
    public bool MinigameLadderAuto { get; init; }
    /// <summary>Use the ported reference detector (layout calibration + note heads) instead of the simple colour bands.</summary>
    public bool MinigameReferenceRhythm { get; init; }
    /// <summary>Press when the note head is within this many reference pixels of the judge line.</summary>
    public int MinigameTriggerWindow { get; init; } = 120;
    /// <summary>Use the ported jenga detector (HUD gate + bird + template matching) instead of the brightness centroid.</summary>
    public bool MinigameReferenceLadder { get; init; }
    /// <summary>关闭窗口时的行为：ask=每次询问，exit=总是完全关闭，tray=总是收到托盘。</summary>
    public string CloseAction { get; init; } = "ask";
    /// <summary>是否显示悬浮窗（HUD）。</summary>
    public bool ShowHud { get; init; }
    /// <summary>是否显示托盘图标。</summary>
    public bool ShowTrayIcon { get; init; } = true;
    /// <summary>最小化时收进托盘。</summary>
    public bool MinimizeToTray { get; init; } = true;
    /// <summary>任务结束（成功/失败）时弹出通知。</summary>
    public bool NotifyOnTaskEnd { get; init; } = true;
    /// <summary>悬浮窗与通知显示在哪个显示器（0 = 第一个）。</summary>
    public int OverlayMonitor { get; init; }
    /// <summary>单步识别等待上限（秒）。</summary>
    public int StepTimeoutSeconds { get; init; } = 15;
    /// <summary>识别失败后重试间隔（秒）。</summary>
    public int StepRetrySeconds { get; init; } = 3;
    /// <summary>每次操作后等待界面稳定的时间（毫秒）。</summary>
    public int ActionSettleMs { get; init; } = 1500;
    /// <summary>任务失败后是否自动尝试按 Esc 回到主界面。</summary>
    public bool AutoRecover { get; init; } = true;
    /// <summary>运行中多少秒没有进展就判定疑似卡死（0 表示不检测）。</summary>
    public int StuckWarningSeconds { get; init; } = 90;
    /// <summary>界面强调色（#RRGGBB），空表示使用主题默认色。</summary>
    public string AccentColor { get; init; } = "";
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
