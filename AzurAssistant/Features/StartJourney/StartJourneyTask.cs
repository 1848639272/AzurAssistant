using System.Diagnostics;
using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;

namespace AzurAssistant.Features.StartJourney;

public sealed class StartJourneyTask(IJourneyRecognizer recognizer, TimeSpan? recognitionTimeout = null, TimeSpan? entryTimeout = null,
    DailyUiTiming? timing = null, TimeSpan? updateTimeout = null) : IGameTask
{
    public string Id => "start-journey";
    public string Name => "进入游戏";

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var recognitionBudget = recognitionTimeout ?? TimeSpan.FromSeconds(15);
        var entryBudget = entryTimeout ?? TimeSpan.FromSeconds(60);
        var updateBudget = updateTimeout ?? TimeSpan.FromMinutes(5);
        var pace = timing ?? DailyUiTiming.Default;
        if (recognitionBudget <= TimeSpan.Zero || entryBudget <= TimeSpan.Zero || updateBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(recognitionTimeout), "进入游戏的等待时限必须大于零。");
        using var deadline = context.Execution.CreateDeadline(token);
        deadline.CancelAfter(recognitionBudget);
        var after = Now;
        long lastId = 0;
        FrameSnapshot? viewport = null;
        JourneyObservation? previous = null;
        JourneyObservation? actionSource = null;
        var stableSince = TimeSpan.Zero;
        var readyAt = context.Execution.Elapsed;
        var actionAt = context.Execution.Elapsed;
        var generation = context.Execution.Generation;
        var canRetry = false;
        ClientPoint actionPoint = default;
        var actionName = "";
        var entering = false;
        var updateConfirmed = false;
        var waitingUpdate = false;
        var observations = 0;
        var initialHits = 0;
        double? bestInitialScore = null;
        var lastPage = JourneyPage.Unknown;
        var startClicked = false;
        var claimClicked = false;
        int? pendingCard = null;
        int? verifiedCard = null;
        var loginClosed = false;
        var monthlyObserved = false;
        var monthlyRewardObserved = false;
        var rewardClosed = false;
        context.Log($"观察游戏入口和登录奖励，起点最长等待 {recognitionBudget.TotalSeconds:0.#} 秒");
        try
        {
            while (true)
            {
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await context.Execution.DelayAsync(pace.PollInterval, deadline.Token);
                    var frame = await context.Frames.ReadAfterAsync(after, deadline.Token);
                    await context.Execution.WaitAsync(deadline.Token);
                    if (generation != context.Execution.Generation)
                    { generation = context.Execution.Generation; previous = null; canRetry = false; }
                    if (frame.SystemTimestamp <= after || frame.Id <= lastId || !Fresh(frame) || !context.Execution.IsObservationCurrent(frame))
                    {
                        previous = null;
                        canRetry = false;
                        await context.Execution.DelayAsync(pace.InvalidFrameDelay, deadline.Token);
                        continue;
                    }
                    if (frame.SystemTimestamp - after > TimeSpan.FromSeconds(1)) { canRetry = false; previous = null; }
                    after = frame.SystemTimestamp;
                    lastId = frame.Id;
                    if (viewport is not null && !SameViewport(viewport, frame))
                        throw new InputUnavailableException("进入游戏期间窗口或视口发生变化，已停止；请重新开始观察");
                    viewport = frame;
                    var observed = await recognizer.ObserveAsync(frame, deadline.Token);
                    await context.Execution.WaitAsync(deadline.Token);
                    observations++;
                    lastPage = observed.Page;
                    if (observed.Page == JourneyPage.Start) initialHits++;
                    if (observed.InitialPageScore is { } score)
                        bestInitialScore = Math.Max(bestInitialScore ?? 0, score);
                    if (actionSource is not null && !SameObservation(actionSource, observed)) canRetry = false;
                    if (!Fresh(frame) || !context.Execution.IsObservationCurrent(frame)) { previous = null; canRetry = false; continue; }
                    if (observed.Page == JourneyPage.Unknown || context.Execution.Elapsed < readyAt) { previous = null; continue; }
                    var stable = previous is not null && SameObservation(previous, observed);
                    previous = observed;
                    if (!stable) { stableSince = frame.SystemTimestamp; continue; }
                    if (frame.SystemTimestamp - stableSince < pace.StableDuration) continue;
                    if (waitingUpdate && observed.Page != JourneyPage.Start) continue;
                    if (!entering && observed.Page != JourneyPage.UpdatePrompt)
                    {
                        entering = true;
                        waitingUpdate = false;
                        deadline.CancelAfter(entryBudget);
                    }
    
                    if (canRetry && context.Execution.Elapsed - actionAt >= pace.RetryDelay)
                    {
                        context.Log($"{actionName}：页面仍未切换，重试 1/1");
                        await ClickAsync(actionPoint, actionName, retry: true);
                        continue;
                    }
    
                    if (pendingCard is { } awaiting)
                    {
                        if (observed.Page == JourneyPage.LoginReward && observed.ClaimedCards?.Contains(awaiting) == true)
                        {
                            verifiedCard = awaiting;
                            pendingCard = null;
                            context.Log($"已确认第 {awaiting + 1} 张登录奖励卡片的领取勾选");
                        }
                        else if (observed.Page is JourneyPage.InGame or JourneyPage.Menu)
                            return new(false, "登录奖励领取后未确认同一张卡片的勾选");
                        else continue;
                    }
    
                    switch (observed.Page)
                    {
                        case JourneyPage.UpdatePrompt:
                            if (updateConfirmed || entering || observed.ConfirmPoint is not { } confirmPoint) break;
                            await ClickAsync(confirmPoint, "确认游戏资源更新");
                            canRetry = false;
                            updateConfirmed = true;
                            waitingUpdate = true;
                            deadline.CancelAfter(updateBudget);
                            context.Log($"等待资源更新完成，最长 {updateBudget.TotalMinutes:0.#} 分钟");
                            break;
                        case JourneyPage.InGame:
                        case JourneyPage.Menu:
                            if (monthlyObserved && !monthlyRewardObserved)
                                return new(false, "月卡展示后未确认自动发放的获得弹窗，结果不明，已停止");
                            return new(true, startClicked || loginClosed || rewardClosed
                                ? "进入游戏完成，已处理确认的登录奖励并确认游戏界面"
                                : "已经进入游戏，无需点击开始旅程");
                        case JourneyPage.Start:
                            if (startClicked || claimClicked || loginClosed || monthlyObserved || rewardClosed) break;
                            if (observed.StartPoint is not { } startPoint) break;
                            await ClickAsync(startPoint, "开始旅程");
                            startClicked = true;
                            context.Log("已点击开始旅程，等待游戏加载");
                            break;
                        case JourneyPage.LoginReward:
                            if (loginClosed) break;
                            if ((verifiedCard is { } verified && observed.ClaimedCards?.Contains(verified) == true)
                                || (!claimClicked && observed.LoginRewardComplete && observed.ClaimPoint is null))
                            {
                                if (observed.ClosePoint is not { } close) break;
                                await ClickAsync(close, "关闭已确认领取的登录奖励面板");
                                loginClosed = true;
                            }
                            else if (!claimClicked && observed.ClaimPoint is { } claim && observed.ClaimCardIndex is { } card && card >= 0
                                && observed.ClaimedCards?.Contains(card) != true)
                            {
                                await ClickAsync(claim, $"领取第 {card + 1} 张登录奖励卡片");
                                claimClicked = true;
                                pendingCard = card;
                                context.Log("已点击登录奖励卡片，等待领取完成");
                            }
                            break;
                        case JourneyPage.MonthlyPending:
                            if (!monthlyObserved)
                            {
                                monthlyRewardObserved = false;
                                context.Log("已识别月卡展示，等待奖励自动发放");
                            }
                            monthlyObserved = true;
                            break;
                        case JourneyPage.Reward:
                            monthlyRewardObserved = true;
                            if (rewardClosed || observed.ClosePoint is not { } dismiss) break;
                            await ClickAsync(dismiss, "关闭已确认的获得弹窗");
                            rewardClosed = true;
                            break;
                    }
    
                    async Task ClickAsync(ClientPoint point, string action, bool retry = false)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (!Fresh(frame)) throw new InputUnavailableException("识别帧已过期，未发送点击。");
                        if (point.X < 0 || point.Y < 0 || point.X >= frame.Width || point.Y >= frame.Height)
                            throw new InvalidOperationException("进入游戏识别目标超出客户区，未发送点击。");
                        context.Log(action);
                        await context.Input.ClickAsync(frame, point, deadline.Token);
                        deadline.Token.ThrowIfCancellationRequested();
                        after = Now;
                        previous = null;
                        actionAt = context.Execution.Elapsed;
                        readyAt = actionAt + pace.ActionSettle;
                        actionSource = observed;
                        actionPoint = point;
                        actionName = action;
                        canRetry = !retry;
                    }
                }
                catch (ObservationInvalidatedException) { previous = null; canRetry = false; }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            context.Log($"进入游戏识别诊断：新帧 {observations}，初始页命中 {initialHits}，适龄标识最高分 {bestInitialScore?.ToString("F3") ?? "无"}，最后页面 {lastPage}");
            var reason = waitingUpdate ? "资源更新后未确认开始旅程页面"
                : pendingCard is not null ? "登录奖励未确认同一卡片勾选"
                : monthlyObserved && !monthlyRewardObserved ? "未确认月卡奖励到账"
                : !entering ? "未稳定识别到初始页、登录奖励或游戏界面，未发送点击"
                : "未确认进入游戏，任务已停止";
            return new(false, $"{(waitingUpdate ? updateBudget : entering ? entryBudget : recognitionBudget).TotalSeconds:0.#} 秒等待超时；{reason}");
        }
    }

    private static bool SameObservation(JourneyObservation left, JourneyObservation right) => left.Page == right.Page
        && Near(left.StartPoint, right.StartPoint) && Near(left.ClaimPoint, right.ClaimPoint)
        && Near(left.ClosePoint, right.ClosePoint) && left.ClaimCardIndex == right.ClaimCardIndex
        && Near(left.ConfirmPoint, right.ConfirmPoint)
        && left.LoginRewardComplete == right.LoginRewardComplete
        && (left.ClaimedCards ?? []).Order().SequenceEqual((right.ClaimedCards ?? []).Order());

    private static bool Near(ClientPoint? left, ClientPoint? right) => left is null || right is null
        ? left == right : Math.Abs(left.Value.X - right.Value.X) <= 6 && Math.Abs(left.Value.Y - right.Value.Y) <= 6;

    private static bool SameViewport(FrameSnapshot left, FrameSnapshot right) => left.ViewportVersion == right.ViewportVersion
        && left.WindowHandle == right.WindowHandle && left.ProcessId == right.ProcessId && left.Width == right.Width
        && left.Height == right.Height && left.ClientLeft == right.ClientLeft && left.ClientTop == right.ClientTop;

    private static bool Fresh(FrameSnapshot frame) => Now - frame.SystemTimestamp is var age
        && age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(1);
    private static TimeSpan Now => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
}
