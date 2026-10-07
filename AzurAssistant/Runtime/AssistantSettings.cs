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
}
