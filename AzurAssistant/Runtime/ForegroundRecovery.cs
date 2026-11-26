using System.Diagnostics;
using AzurAssistant.Contracts;

namespace AzurAssistant.Runtime;

/// <summary>Tracks foreground requests for a pending input, serialised by the task's input lease.</summary>
internal sealed class ForegroundRecovery(IGameWindowFocus window, IGameInput input, TaskPauseController execution,
    Action<string> log, Func<TimeSpan>? clock = null)
{
    private static readonly TimeSpan LossConfirmation = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan InterruptionWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeniedRetryInterval = TimeSpan.FromSeconds(5);
    private sealed record Request(nint Window, int Process, TimeSpan Started, uint? InputTick, bool Granted);
    private Request? _pending;
    private readonly Queue<TimeSpan> _interruptions = new();

    public Task PrepareForObservationAsync(CancellationToken token) => window.PrepareForObservationAsync(token);

    public void InputCompleted()
    {
        _pending = null;
        _interruptions.Clear();
    }

    public async Task<bool> ReobserveIfChangedAsync(FrameSnapshot frame, CancellationToken token, bool lostBeforeDispatch = false)
    {
        if (window.IsTargetForeground)
        {
            try { if (!lostBeforeDispatch && !window.HasMovedSince(frame)) return false; }
            catch (InputUnavailableException) when (!window.IsTargetForeground) { }
        }

        input.ReleaseAll();
        try
        {
            if (!window.IsTargetForeground)
                await RecoverBeforeInputAsync(frame, token);
        }
        finally { execution.InvalidateObservations(); }
        return true;
    }

    private async Task RecoverBeforeInputAsync(FrameSnapshot frame, CancellationToken token)
    {
        // A single mismatched HWND, including an activation/animation transition, is not a confirmed loss.
        var since = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(since) < LossConfirmation)
        {
            await Task.Delay(35, token);
            if (window.IsTargetForeground) return;
        }

        var now = clock?.Invoke() ?? Stopwatch.GetElapsedTime(0);
        if (_pending is { } prior)
        {
            if (prior.Window != frame.WindowHandle || prior.Process != frame.ProcessId || now - prior.Started >= InterruptionWindow)
                InputCompleted();
            else if (prior.Granted && prior.InputTick is { } before && window.IsOtherProcessForeground &&
                     window.LastInputTick is { } after && unchecked((int)(after - before)) > 0)
            {
                while (_interruptions.TryPeek(out var first) && now - first >= InterruptionWindow)
                    _interruptions.Dequeue();
                _interruptions.Enqueue(now);
                if (_interruptions.Count >= 3)
                    throw new InvalidOperationException("30 秒内连续三次置前被其他操作打断，一条龙已停止。");
                log($"输入前置前被其他操作打断（{_interruptions.Count}/3），重新确认前台。");
            }
            else if (!prior.Granted && now - prior.Started < DeniedRetryInterval)
                return;
            else
                _interruptions.Clear();
        }

        token.ThrowIfCancellationRequested();
        var tick = window.LastInputTick;
        log("操作前确认持续失焦，正在尝试置前。");
        execution.SetFocusRecovery(true);
        bool granted;
        try { granted = await window.TryActivateOnceAsync(token); }
        finally { execution.SetFocusRecovery(false); }
        _pending = new(frame.WindowHandle, frame.ProcessId, now, tick, granted);
        log(granted ? "已置前，重新观察操作目标。" : "尚未取得前台，等待新画面再确认操作。");
    }
}
