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
                    throw new InvalidOperationException($"30 秒内连续三次置前被其他操作打断，一条龙已停止。{window.DescribeForeground()}");
                log($"输入前置前被其他操作打断（{_interruptions.Count}/3），重新确认前台。{window.DescribeForeground()}");
            }
            else if (!prior.Granted && now - prior.Started < DeniedRetryInterval)
                return;
            else
                _interruptions.Clear();
        }

        log("操作前确认持续失焦，正在尝试置前。");
        await RequestAsync(frame.WindowHandle, frame.ProcessId, token);
    }

    internal async Task<bool> RequestAsync(nint handle, int processId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = clock?.Invoke() ?? Stopwatch.GetElapsedTime(0);
        var alreadyFocused = window.HasInputFocus(handle, processId);
        execution.SetFocusRecovery(true);
        bool granted;
        try { granted = await input.TryActivateWindowAsync(handle, processId, token); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log($"置前请求失败。{window.DescribeForeground()}");
            throw;
        }
        finally { execution.SetFocusRecovery(false); }
        // Only input after the confirmed transfer can interrupt it. Denied requests are throttled from their start.
        _pending = granted && alreadyFocused ? null : new(handle, processId,
            granted ? clock?.Invoke() ?? Stopwatch.GetElapsedTime(0) : now, granted ? window.LastInputTick : null, granted);
        log(granted ? "已确认目标窗口在前台并持有输入焦点，重新观察操作目标。"
            : $"置前未确认，等待新画面后自动重试。{window.DescribeForeground()}");
        return granted;
    }
}
