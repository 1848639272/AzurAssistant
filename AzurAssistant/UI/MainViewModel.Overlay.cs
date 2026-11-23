using AzurAssistant.Platform;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>悬浮窗 / 托盘 / 通知 / 显示器选择。</summary>
public sealed partial class MainViewModel
{
    private IReadOnlyList<DisplayMonitor> _monitors = [];

    public sealed record CloseChoice(string Key, string Label);

    /// <summary>Options for the 关闭窗口时 setting.</summary>
    public IReadOnlyList<CloseChoice> CloseChoices { get; } =
    [
        new("ask", "每次询问"),
        new("exit", "总是完全关闭"),
        new("tray", "总是收到系统托盘"),
    ];

    public string CloseAction => Settings.CloseAction;

    public CloseChoice? SelectedCloseChoice
    {
        get => CloseChoices.FirstOrDefault(choice => choice.Key == Settings.CloseAction) ?? CloseChoices[0];
        set
        {
            if (value is null || value.Key == Settings.CloseAction) return;
            if (SaveSettings(Settings with { CloseAction = value.Key })) { Notify(); Notify(nameof(SelectedCloseChoice)); }
        }
    }

    /// <summary>Stores the choice made in the close dialog ("exit" or "tray").</summary>
    public void RememberCloseAction(string action)
    {
        if (action is not ("exit" or "tray" or "ask")) return;
        if (action == Settings.CloseAction) return;
        if (SaveSettings(Settings with { CloseAction = action })) { Notify(nameof(SelectedCloseChoice)); Notify(nameof(CloseAction)); }
    }

    /// <summary>The active runner, so the window can subscribe to run completion for notifications.</summary>
    internal TaskRunner? TaskRunnerInternal => Runner;

    public IReadOnlyList<DisplayMonitor> Monitors => _monitors;

    public void RefreshMonitors()
    {
        _monitors = DisplayMonitors.List();
        Notify(nameof(Monitors));
        Notify(nameof(OverlayMonitor));
    }

    public bool ShowHud
    {
        get => Settings.ShowHud;
        set
        {
            if (value == Settings.ShowHud) return;
            if (SaveSettings(Settings with { ShowHud = value })) Notify();
        }
    }

    public bool ShowTrayIcon
    {
        get => Settings.ShowTrayIcon;
        set
        {
            if (value == Settings.ShowTrayIcon) return;
            if (SaveSettings(Settings with { ShowTrayIcon = value })) Notify();
        }
    }

    public bool MinimizeToTray
    {
        get => Settings.MinimizeToTray;
        set
        {
            if (value == Settings.MinimizeToTray) return;
            if (SaveSettings(Settings with { MinimizeToTray = value })) Notify();
        }
    }

    public bool NotifyOnTaskEnd
    {
        get => Settings.NotifyOnTaskEnd;
        set
        {
            if (value == Settings.NotifyOnTaskEnd) return;
            if (SaveSettings(Settings with { NotifyOnTaskEnd = value })) Notify();
        }
    }

    public int OverlayMonitor
    {
        get => Settings.OverlayMonitor;
        set
        {
            var clamped = Math.Clamp(value, 0, 15);
            if (clamped == Settings.OverlayMonitor) return;
            if (SaveSettings(Settings with { OverlayMonitor = clamped })) { Notify(); Notify(nameof(OverlayMonitorLabel)); }
        }
    }

    public string OverlayMonitorLabel => DisplayMonitors.Resolve(Settings.OverlayMonitor)?.Label ?? "未检测到显示器";

    /// <summary>Second line of the floating panel: connection state plus run state.</summary>
    private string _hudRemaining = "";

    /// <summary>Shows the estimated remaining time on the floating panel.</summary>
    public void SetHudDetail(string text)
    {
        if (_hudRemaining == text) return;
        _hudRemaining = text;
        Notify(nameof(HudDetail));
    }

    public string HudDetail
    {
        get
        {
            var state = IsPaused ? "已暂停" : (Runner?.IsRunning == true ? "运行中" : "空闲");
            return _hudRemaining.Length > 0 ? $"{StateText} · {state} · {_hudRemaining}" : $"{StateText} · {state}";
        }
    }
}
