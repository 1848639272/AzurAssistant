namespace AzurAssistant.Runtime;

/// <summary>Settings-page values applied once per assistant process; task selections remain live.</summary>
public sealed record ApplicationPreferences(string GameExecutablePath, bool AutoConnect, bool AutoRunDaily,
    bool LowPerformanceMode, bool ShowPreview, bool DarkMode = false, string PauseHotkey = "F9", string StopHotkey = "F10",
    bool AutoUpdate = false)
{
    public static ApplicationPreferences From(AssistantSettings settings) => new(settings.GameExecutablePath,
        settings.AutoConnect, settings.AutoRunDaily, settings.LowPerformanceMode, settings.ShowPreview, settings.DarkMode,
        settings.PauseHotkey, settings.StopHotkey, settings.AutoUpdate);

    public AssistantSettings ApplyTo(AssistantSettings selections) => selections with
    {
        GameExecutablePath = GameExecutablePath, AutoConnect = AutoConnect, AutoRunDaily = AutoRunDaily,
        LowPerformanceMode = LowPerformanceMode, ShowPreview = ShowPreview, DarkMode = DarkMode,
        PauseHotkey = PauseHotkey, StopHotkey = StopHotkey, AutoUpdate = AutoUpdate
    };
}
