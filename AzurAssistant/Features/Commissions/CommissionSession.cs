using System.Diagnostics;
using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;

namespace AzurAssistant.Features.Commissions;

internal sealed record CommissionFrame(FrameSnapshot Frame, CommissionObservation Ui, CommissionRead Read = CommissionRead.Details);
internal sealed class CommissionStepException(string message) : Exception(message);

/// <summary>Bounded observations and actions within the runner's existing frame feed and input lease.</summary>
internal sealed class CommissionSession(TaskContext context, ICommissionRecognizer recognizer, CommissionLayout layout, DailyUiTiming timing)
{
    private TimeSpan _after = Now;
    private long _lastId;
    private FrameSnapshot? _identity;
    private static TimeSpan Now => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    private static bool Fresh(FrameSnapshot frame, TimeSpan? maximumAge = null) => Now - frame.SystemTimestamp is var age
        && age >= TimeSpan.Zero && age < (maximumAge ?? TimeSpan.FromSeconds(1));
    public void Log(string message) => context.Log(message);

    public async Task<CommissionFrame> WaitAsync(string step, Func<CommissionObservation, bool> accept, CancellationToken token,
        CommissionRead read = CommissionRead.Details, TimeSpan? timeout = null, TimeSpan? maxFrameAge = null,
        TimeSpan? autoBattlePollInterval = null)
    {
        using var deadline = context.Execution.CreateDeadline(token);
        deadline.CancelAfter(timeout ?? timing.StepTimeout);
        CommissionFrame? previous = null;
        var stableAt = TimeSpan.Zero;
        var last = "尚无有效新帧";
        var generation = context.Execution.Generation;
        try
        {
            while (true)
            {
                var frame = await context.Frames.ReadAfterAsync(_after, deadline.Token);
                await context.Execution.WaitAsync(deadline.Token);
                if (generation != context.Execution.Generation)
                { generation = context.Execution.Generation; previous = null; }
                if (frame.Id <= _lastId || frame.SystemTimestamp <= _after || !Fresh(frame) || !context.Execution.IsObservationCurrent(frame))
                { previous = null; await context.Execution.DelayAsync(timing.PollInterval, deadline.Token); continue; }
                CheckIdentity(frame);
                _after = frame.SystemTimestamp; _lastId = frame.Id;
                var recognitionStarted = Now;
                var ui = await recognizer.ObserveAsync(frame, read, deadline.Token);
                await context.Execution.WaitAsync(deadline.Token);
                if (!context.Execution.IsObservationCurrent(frame)) { previous = null; continue; }
                last = $"{ui.Page}；识别耗时 {(Now - recognitionStarted).TotalMilliseconds:F0}ms；帧龄 {(Now - frame.SystemTimestamp).TotalMilliseconds:F0}ms；{ui.EvidenceKey}；{ui.Diagnostic}";
                var current = new CommissionFrame(frame, ui, read);
                if (Fresh(frame, maxFrameAge) && ui.Page != CommissionPage.Unknown && accept(ui))
                {
                    if (previous is null || !Same(previous.Ui, ui)) { previous = current; stableAt = frame.SystemTimestamp; }
                    else if (frame.SystemTimestamp - stableAt >= timing.StableDuration) return current;
                }
                else previous = null;
                var poll = autoBattlePollInterval is { } battlePoll && Fresh(frame, maxFrameAge)
                    && ui.Page == CommissionPage.Battle && ui.AutoRunning ? battlePoll : timing.PollInterval;
                await PollAsync(poll, frame, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CommissionStepException($"{step}超时，停止该项；最后观察：{last}"); }
    }

    private async Task PollAsync(TimeSpan delay, FrameSnapshot source, CancellationToken token)
    {
        if (delay <= timing.PollInterval)
        { await context.Execution.DelayAsync(delay, token); return; }
        var generation = context.Execution.Generation;
        var due = context.Execution.Elapsed + delay;
        // The slower recognition cadence does not delay pause/stop; resume discards this old page's remaining wait.
        var slice = timing.PollInterval > TimeSpan.Zero ? timing.PollInterval : TimeSpan.FromMilliseconds(200);
        while (due - context.Execution.Elapsed is var remaining && remaining > TimeSpan.Zero)
        {
            await context.Execution.DelayAsync(remaining < slice ? remaining : slice, token);
            if (generation != context.Execution.Generation || !context.Execution.IsObservationCurrent(source)) return;
        }
    }

    public Task<CommissionFrame> PageAsync(string step, CancellationToken token, params CommissionPage[] pages)
        => WaitAsync(step, u => pages.Contains(u.Page), token);

    public async Task<CommissionFrame> DelayAndObserveAsync(string step, TimeSpan delay,
        Func<CommissionObservation, bool> accept, CancellationToken token)
    {
        await context.Execution.DelayAsync(delay, token);
        _after = Now;
        return await WaitAsync(step, accept, token);
    }

    public async Task ClickAsync(CommissionFrame source, string target, string step, CancellationToken token, TimeSpan? settleDelay = null)
    {
        await PerformAsync(source, step, token, async current =>
        {
            if (!current.Ui.Targets.TryGetValue(target, out var point)) throw new CommissionStepException(step + "：按钮缺失");
            await context.Input.ClickAsync(current.Frame, point, token);
        });
        await SettleAsync(token, settleDelay);
    }
    public async Task KeyAsync(CommissionFrame source, GameKey key, string step, CancellationToken token)
    {
        await PerformAsync(source, step, token, current => context.Input.PressKeyAsync(current.Frame, key, token));
        await SettleAsync(token);
    }
    public async Task DragAsync(CommissionFrame source, string path, string step, CancellationToken token)
    {
        var page = source.Ui.Page;
        var supported = page switch
        {
            CommissionPage.Daily => path is "list-left" or "list-right",
            CommissionPage.Story => path is "story-left" or "story-right",
            CommissionPage.Teams => path is "team-up" or "team-down",
            CommissionPage.Crisis => path is "crisis-up" or "crisis-down" or "tier-up" or "tier-down",
            _ => false
        };
        if (!supported) throw new CommissionStepException(step + "：页面不支持该拖动路径");
        // Fixed navigation paths need fresh page anchors, not the previous OCR list's coordinates.
        // Reserve the observation's lifetime for the whole gesture; the caller reads the new list afterwards.
        var dragFrameAge = TimeSpan.FromMilliseconds(500);
        source = await WaitAsync(step + "：拖动前重新确认页面", ui => ui.Page == page, token,
            CommissionRead.Navigation, maxFrameAge: dragFrameAge);
        await PerformAsync(source, step, token,
            current => context.Input.DragAsync(current.Frame, layout.Points[path + "-from"], layout.Points[path + "-to"], token), dragFrameAge);
        await SettleAsync(token);
    }
    private async Task PerformAsync(CommissionFrame source, string step, CancellationToken token, Func<CommissionFrame, Task> action,
        TimeSpan? maximumAge = null)
    {
        while (true)
        {
            await context.Execution.WaitAsync(token);
            if (!context.Execution.IsObservationCurrent(source.Frame))
            {
                var expected = source.Ui;
                source = await WaitAsync(step + "：继续前复核副本、资源和按钮", ui => Same(expected, ui), token, source.Read,
                    maxFrameAge: maximumAge);
            }
            CheckIdentity(source.Frame);
            if (!Fresh(source.Frame, maximumAge)) throw new CommissionStepException(step + "：观察过期");
            try { context.Log(step); await action(source); return; }
            catch (ObservationInvalidatedException) { /* Re-observe only when the input gate confirms nothing was sent. */ }
        }
    }
    private async Task SettleAsync(CancellationToken token, TimeSpan? delay = null)
    { await context.Execution.DelayAsync(delay ?? timing.ActionSettle, token); _after = Now; }
    private void CheckIdentity(FrameSnapshot frame)
    {
        _identity ??= frame;
        if (_identity.WindowHandle != frame.WindowHandle || _identity.ProcessId != frame.ProcessId
            || _identity.ViewportVersion != frame.ViewportVersion || _identity.ClientLeft != frame.ClientLeft || _identity.ClientTop != frame.ClientTop
            || _identity.Width != frame.Width || _identity.Height != frame.Height)
            throw new InputUnavailableException("委托执行中窗口或视口已变化，请重新开始。");
    }
    private static bool Same(CommissionObservation a, CommissionObservation b) => a.EvidenceKey == b.EvidenceKey
        && a.Targets.Count == b.Targets.Count && a.Targets.All(t => b.Targets.TryGetValue(t.Key, out var p)
            && Math.Abs(t.Value.X - p.X) <= 6 && Math.Abs(t.Value.Y - p.Y) <= 6);
}
