using AzurAssistant.Game.Ui;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>识别与容错设置：单步超时、重试间隔、动作后等待、失败自动恢复、卡死检测。</summary>
public sealed partial class MainViewModel
{
    private string? _lastRunStatus;
    private DateTimeOffset _lastRunStatusAt = DateTimeOffset.Now;
    private bool _stuckReported;

    public int StepTimeoutSeconds
    {
        get => Settings.StepTimeoutSeconds;
        set
        {
            var clamped = Math.Clamp(value, 3, 300);
            if (clamped == Settings.StepTimeoutSeconds) return;
            if (SaveSettings(Settings with { StepTimeoutSeconds = clamped })) Notify();
        }
    }

    public int StepRetrySeconds
    {
        get => Settings.StepRetrySeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, 60);
            if (clamped == Settings.StepRetrySeconds) return;
            if (SaveSettings(Settings with { StepRetrySeconds = clamped })) Notify();
        }
    }

    public int ActionSettleMs
    {
        get => Settings.ActionSettleMs;
        set
        {
            var clamped = Math.Clamp(value, 200, 10000);
            if (clamped == Settings.ActionSettleMs) return;
            if (SaveSettings(Settings with { ActionSettleMs = clamped })) Notify();
        }
    }

    public int StuckWarningSeconds
    {
        get => Settings.StuckWarningSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, 3600);
            if (clamped == Settings.StuckWarningSeconds) return;
            if (SaveSettings(Settings with { StuckWarningSeconds = clamped })) Notify();
        }
    }

    public bool AutoRecover
    {
        get => Settings.AutoRecover;
        set
        {
            if (value == Settings.AutoRecover) return;
            if (SaveSettings(Settings with { AutoRecover = value })) Notify();
        }
    }

    /// <summary>Pushes the recognition timing into the navigators (called after every settings save).</summary>
    internal static void ApplyRecognitionTiming(AssistantSettings settings)
        => DailyUiTiming.Configure(settings.StepTimeoutSeconds, settings.StepRetrySeconds, settings.ActionSettleMs);

    /// <summary>Logs once when a running task has not changed its status for the configured number of seconds.</summary>
    private void DetectStuck()
    {
        var runner = Runner;
        if (runner is null || !runner.IsRunning)
        {
            _lastRunStatus = null;
            _stuckReported = false;
            return;
        }
        var status = runner.Status;
        if (!string.Equals(status, _lastRunStatus, StringComparison.Ordinal))
        {
            _lastRunStatus = status;
            _lastRunStatusAt = DateTimeOffset.Now;
            _stuckReported = false;
            return;
        }
        var limit = Settings.StuckWarningSeconds;
        if (limit <= 0 || _stuckReported) return;
        if ((DateTimeOffset.Now - _lastRunStatusAt).TotalSeconds < limit) return;
        _stuckReported = true;
        Report($"疑似卡死：{limit} 秒没有进展（当前步骤：{status}）。可按停止快捷键结束，或检查游戏画面是否被遮挡。");
    }
}
