using System.Diagnostics;
using AzurAssistant.Contracts;

namespace AzurAssistant.Game.Ui;

public sealed record DailyUiTiming(TimeSpan StepTimeout, TimeSpan PollInterval, TimeSpan ActionSettle)
{
    public TimeSpan StableDuration { get; init; } = TimeSpan.FromMilliseconds(600);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan InvalidFrameDelay { get; init; } = TimeSpan.FromMilliseconds(10);
    public static DailyUiTiming Default { get; } = new(TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(1500));
}

internal sealed record ObservedUi(FrameSnapshot Frame, DailyUiObservation Ui);
internal sealed class UiStepException(string message) : Exception(message);

internal sealed class UiNavigator(TaskContext context, IDailyUiRecognizer recognizer, DailyUiTiming timing)
{
    private TimeSpan _after = Now;
    private long _lastId;
    private readonly HashSet<(DailyPage Page, string? Target)> _failedActions = [];
    private static TimeSpan Now => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    private static bool Fresh(FrameSnapshot frame) => Now - frame.SystemTimestamp is var age && age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(1);

    public async Task<ObservedUi> WaitAsync(string step, CancellationToken token, string? target = null, params DailyPage[] pages)
        => await WaitCoreAsync(step, token, target, pages);

    public async Task<ObservedUi> TransitionAsync(ObservedUi source, string? actionTarget, string step,
        CancellationToken token, string? resultTarget = null, int offsetY = 0, bool allowRetry = true,
        Func<ObservedUi, CancellationToken, Task<bool>>? accept = null, params DailyPage[] pages)
    {
        if (_failedActions.Contains((source.Ui.Page, actionTarget))) throw new UiStepException($"{step}：此前操作未完成");
        source = await SendAsync(source, actionTarget, step, token, offsetY);
        try { return await WaitCoreAsync(step, token, resultTarget, pages, source, actionTarget, offsetY, allowRetry, accept); }
        catch (UiStepException) { _failedActions.Add((source.Ui.Page, actionTarget)); throw; }
    }

    private async Task<ObservedUi> WaitCoreAsync(string step, CancellationToken token, string? target, DailyPage[] pages,
        ObservedUi? origin = null, string? actionTarget = null, int offsetY = 0, bool allowRetry = true,
        Func<ObservedUi, CancellationToken, Task<bool>>? accept = null)
    {
        using var deadline = context.Execution.CreateDeadline(token);
        deadline.CancelAfter(timing.StepTimeout);
        ObservedUi? previous = null;
        var stableSince = TimeSpan.Zero;
        var actionAt = context.Execution.Elapsed;
        var readyAt = origin is null ? actionAt : actionAt + timing.ActionSettle;
        var generation = context.Execution.Generation;
        var canRetry = origin is not null && allowRetry;
        var retryReason = origin is null ? "只观察，未发送动作" : allowRetry ? "尚未满足原页持续稳定条件" : "本动作禁止重复执行";
        var lastDiagnostic = "无有效观察";
        var lastPage = DailyPage.Unknown;
        try
        {
            while (true)
            {
                var frame = await context.Frames.ReadAfterAsync(_after, deadline.Token);
                await context.Execution.WaitAsync(deadline.Token);
                if (generation != context.Execution.Generation)
                {
                    generation = context.Execution.Generation;
                    previous = null;
                    canRetry = false;
                    retryReason = "暂停后重新观察，不补发暂停前的动作";
                }
                if (frame.SystemTimestamp <= _after || frame.Id <= _lastId || !Fresh(frame) || !context.Execution.IsObservationCurrent(frame))
                {
                    previous = null;
                    canRetry = false;
                    retryReason = "出现过期或重复帧，不补点";
                    await context.Execution.DelayAsync(timing.PollInterval, deadline.Token);
                    continue;
                }
                if (frame.SystemTimestamp - _after > TimeSpan.FromSeconds(1)) { canRetry = false; previous = null; retryReason = "取帧中断超过1秒，不补点"; }
                _after = frame.SystemTimestamp;
                _lastId = frame.Id;
                var ui = await recognizer.ObserveAsync(frame, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                await context.Execution.WaitAsync(deadline.Token);
                if (!context.Execution.IsObservationCurrent(frame)) { previous = null; canRetry = false; continue; }
                var current = new ObservedUi(frame, ui);
                lastPage = ui.Page;
                lastDiagnostic = ui.Diagnostic;
                var unchanged = origin is not null && Same(origin, current);
                if (canRetry && (!unchanged || !Fresh(frame)))
                {
                    canRetry = false;
                    retryReason = $"页面/锚点/视口改变或识别过期（{ui.Page}），不沿用原坐标补点";
                }
                if (Fresh(frame) && (ui.Page != DailyPage.Unknown || pages.Contains(DailyPage.Unknown)) && context.Execution.Elapsed >= readyAt)
                {
                    if (previous is null || !(pages.Contains(ui.Page) ? SameResult(previous, current, target) : Same(previous, current)))
                    {
                        previous = current;
                        stableSince = frame.SystemTimestamp;
                    }
                    else if (frame.SystemTimestamp - stableSince >= timing.StableDuration)
                    {
                        if (pages.Contains(ui.Page) && (target is null || ui.Has(target)))
                        {
                            var accepted = accept is null || await accept(current, deadline.Token);
                            deadline.Token.ThrowIfCancellationRequested();
                            await context.Execution.WaitAsync(deadline.Token);
                            if (accepted && Fresh(frame) && context.Execution.IsObservationCurrent(frame)) return current;
                        }
                        if (canRetry && unchanged && context.Execution.Elapsed - actionAt >= timing.RetryDelay)
                        {
                            canRetry = false;
                            retryReason = "已重试1/1，次数用尽";
                            context.Log($"{step}：页面仍未切换，重试 1/1");
                            try { await SendAsync(current, actionTarget, step, deadline.Token, offsetY, permitRefresh: false); }
                            catch (ObservationInvalidatedException) { retryReason = "暂停打断补点，取消重试并重新观察结果"; }
                            readyAt = context.Execution.Elapsed + timing.ActionSettle;
                            previous = null;
                        }
                    }
                }
                else previous = null;
                await context.Execution.DelayAsync(timing.PollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new UiStepException($"{step}：等待 {timing.StepTimeout.TotalSeconds:0.#} 秒后未确认结果（当前页面：{lastPage}；{retryReason}；识别：{lastDiagnostic}）");
        }
    }

    private async Task<ObservedUi> SendAsync(ObservedUi source, string? target, string action, CancellationToken token, int offsetY,
        bool permitRefresh = true)
    {
        while (true)
        {
            await context.Execution.WaitAsync(token);
            if (!context.Execution.IsObservationCurrent(source.Frame))
            {
                if (!permitRefresh) throw new ObservationInvalidatedException();
                var expected = source;
                source = await WaitCoreAsync(action + "：继续前重新确认", token, target, [source.Ui.Page],
                    accept: (current, _) => Task.FromResult(Same(expected, current)));
            }
            token.ThrowIfCancellationRequested();
            if (!Fresh(source.Frame)) throw new UiStepException("识别帧已过期，请重新观察页面");
            try
            {
                if (target is null)
                {
                    context.Log(action);
                    await context.Input.PressKeyAsync(source.Frame, GameKey.Escape, token);
                }
                else
                {
                    if (!source.Ui.Targets.TryGetValue(target, out var point))
                        throw new UiStepException("识别帧已过期或按钮不存在，未发送点击");
                    point = point with { Y = point.Y + offsetY };
                    context.Log(action);
                    await context.Input.ClickAsync(source.Frame, point, token);
                }
                _after = Now;
                return source;
            }
            catch (ObservationInvalidatedException) when (permitRefresh) { /* No input was sent; reacquire matching evidence. */ }
        }
    }

    public async Task<ObservedUi> OpenMenuAsync(CancellationToken token)
    {
        var current = await WaitAsync("确认大世界或主菜单", token, null, DailyPage.World, DailyPage.Menu);
        if (current.Ui.Page == DailyPage.Menu) return current;
        return await TransitionAsync(current, null, "打开主菜单", token, pages: [DailyPage.Menu]);
    }

    // The supported 1080p level-up panel has blank space 480 px below its title center.
    public Task<ObservedUi> DismissHomeLevelUpAsync(ObservedUi source, CancellationToken token, params DailyPage[] pages)
        => TransitionAsync(source, "home-level-title", "点击空白处关闭家园等级提升", token, offsetY: 480, pages: pages);

    public async Task ReturnToWorldAsync(CancellationToken token)
    {
        // MailEmpty belongs to the mail feature's acknowledgement flow.
        var pages = new[] { DailyPage.World, DailyPage.Menu, DailyPage.Mail, DailyPage.MailReward,
            DailyPage.HomeCenter, DailyPage.Buildings, DailyPage.Ranch, DailyPage.HomeReward, DailyPage.DiningTable, DailyPage.HomeLevelUp };
        for (var transitions = 0; transitions < 5; transitions++)
        {
            var current = await WaitAsync("确认返回页面", token, null, pages);
            switch (current.Ui.Page)
            {
                case DailyPage.World: return;
                case DailyPage.Menu:
                    await TransitionAsync(current, null, "关闭主菜单", token, pages: [DailyPage.World]);
                    return;
                case DailyPage.Mail:
                    await TransitionAsync(current, "mail-close", "关闭邮箱", token, pages: [DailyPage.Menu]);
                    break;
                case DailyPage.MailReward:
                    await TransitionAsync(current, "mail-dismiss", "关闭邮件奖励", token, offsetY: 80, pages: [DailyPage.Mail]);
                    break;
                case DailyPage.Buildings:
                    await TransitionAsync(current, "buildings-title", "返回家园中枢", token, pages: [DailyPage.HomeCenter]);
                    break;
                case DailyPage.DiningTable:
                    await TransitionAsync(current, "dining-close", "关闭奇波餐桌", token, pages: [DailyPage.HomeCenter]);
                    break;
                case DailyPage.Ranch:
                    await TransitionAsync(current, "ranch-title", "返回家园中枢", token, pages: [DailyPage.HomeCenter]);
                    break;
                case DailyPage.HomeCenter:
                    await TransitionAsync(current, "home-title", "返回主菜单", token, pages: [DailyPage.Menu]);
                    break;
                case DailyPage.HomeReward:
                    await TransitionAsync(current, "home-reward-close", "关闭家园收获结果", token, pages: [DailyPage.Buildings, DailyPage.Ranch, DailyPage.HomeLevelUp]);
                    break;
                case DailyPage.HomeLevelUp:
                    await DismissHomeLevelUpAsync(current, token, DailyPage.Buildings, DailyPage.Ranch, DailyPage.HomeReward);
                    break;
            }
        }
        throw new UiStepException("返回大世界超过已知页面路径上限，停止操作");
    }

    private static bool Near(ClientPoint left, ClientPoint right) => Math.Abs(left.X - right.X) <= 6 && Math.Abs(left.Y - right.Y) <= 6;
    private static bool SameWindow(ObservedUi left, ObservedUi right) => left.Ui.Page == right.Ui.Page
        && left.Frame.ViewportVersion == right.Frame.ViewportVersion && left.Frame.WindowHandle == right.Frame.WindowHandle
        && left.Frame.ProcessId == right.Frame.ProcessId && left.Frame.Width == right.Frame.Width && left.Frame.Height == right.Frame.Height
        && left.Frame.ClientLeft == right.Frame.ClientLeft && left.Frame.ClientTop == right.Frame.ClientTop;

    // Center entrances are independent; only the title and next target must remain stable for arrival.
    // Retry evidence stays stricter and compares every observed target.
    private static bool SameResult(ObservedUi left, ObservedUi right, string? target) =>
        left.Ui.Page != DailyPage.HomeCenter ? Same(left, right) : SameWindow(left, right)
        && TargetNear(left, right, "home-title") && (target is null || TargetNear(left, right, target));
    private static bool TargetNear(ObservedUi left, ObservedUi right, string target) =>
        left.Ui.Targets.TryGetValue(target, out var point) && right.Ui.Targets.TryGetValue(target, out var other) && Near(point, other);
    private static bool Same(ObservedUi left, ObservedUi right) => SameWindow(left, right)
        && left.Ui.EvidenceKey == right.Ui.EvidenceKey
        && left.Ui.Targets.Count == right.Ui.Targets.Count
        && left.Ui.Targets.All(pair => right.Ui.Targets.TryGetValue(pair.Key, out var point) && Near(pair.Value, point));
}
