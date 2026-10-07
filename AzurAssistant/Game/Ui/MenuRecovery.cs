using AzurAssistant.Contracts;

namespace AzurAssistant.Game.Ui;

/// <summary>Verifies a daily-task boundary, using at most three observed Escape presses to dismiss other pages.</summary>
public sealed class MenuRecovery(IDailyUiRecognizer recognizer, DailyUiTiming? timing = null)
{
    public async Task<TaskResult> EnsureAsync(TaskContext context, CancellationToken token)
    {
        var pace = timing ?? DailyUiTiming.Default with { StepTimeout = TimeSpan.FromSeconds(5) };
        var endpoint = new EndpointRecognizer(recognizer);
        var presses = 0;
        context.Input.ReleaseAll();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Each attempt starts after the previous input and settling delay; no old observation is reused.
                var navigator = new UiNavigator(context, endpoint, pace);
                var current = await navigator.WaitAsync("确认大世界或主菜单", token, null,
                    DailyPage.World, DailyPage.Menu, DailyPage.Unknown);
                token.ThrowIfCancellationRequested();
                if (current.Ui.Page is DailyPage.World or DailyPage.Menu)
                {
                    var page = current.Ui.Page == DailyPage.World ? "大世界" : "主菜单";
                    var message = presses == 0 ? $"已确认{page}" : $"已恢复到{page}（Esc {presses}/3）";
                    if (presses > 0) context.Log(message);
                    return new(true, message);
                }
                if (presses == 3)
                    return new(false, $"恢复失败：已尝试 Esc 3/3，仍未识别到大世界或主菜单；{current.Ui.Diagnostic}");
                if (presses == 0) context.Log("识别到未知画面，尝试恢复到主菜单");
                context.Log($"恢复到主菜单：Esc {presses + 1}/3");
                try { await context.Input.PressKeyAsync(current.Frame, GameKey.Escape, token); presses++; }
                catch (ObservationInvalidatedException) { continue; }
                finally { context.Input.ReleaseAll(); }
                await context.Execution.DelayAsync(pace.ActionSettle, token);
            }
        }
        catch (UiStepException ex) { return new(false, $"恢复未完成（Esc {presses}/3）：{ex.Message}"); }
        finally { context.Input.ReleaseAll(); }
    }

    private sealed class EndpointRecognizer(IDailyUiRecognizer inner) : IDailyUiRecognizer
    {
        private FrameSnapshot? _initial;
        private long _lastId;
        private TimeSpan _lastTimestamp;
        public async Task<DailyUiObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (frame.Id <= _lastId || frame.SystemTimestamp <= _lastTimestamp)
                throw new InputUnavailableException("恢复收到重复或回退画面，停止发送 Esc。");
            _lastId = frame.Id;
            _lastTimestamp = frame.SystemTimestamp;
            if (_initial is not null && (_initial.WindowHandle != frame.WindowHandle || _initial.ProcessId != frame.ProcessId
                || _initial.ViewportVersion != frame.ViewportVersion || _initial.Width != frame.Width || _initial.Height != frame.Height
                || _initial.ClientLeft != frame.ClientLeft || _initial.ClientTop != frame.ClientTop))
                throw new InputUnavailableException("恢复期间游戏窗口或视口发生变化，停止发送 Esc。");
            _initial ??= frame;
            var observed = await inner.ObserveAsync(frame, token);
            token.ThrowIfCancellationRequested();
            // Only endpoints authorize handoff. Other recognized popups are unknown to this boundary,
            // regardless of their changing reward details; no popup-specific coordinates are used.
            return observed.Page is DailyPage.World or DailyPage.Menu ? observed
                : new(DailyPage.Unknown, new Dictionary<string, ClientPoint>())
                    { Diagnostic = $"当前页面 {observed.Page}；{observed.Diagnostic}" };
        }
    }
}
