using System.Diagnostics;
using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;

namespace AzurAssistant.Features.StartJourney;

public sealed class StartJourneyTask(IJourneyRecognizer recognizer, TimeSpan? recognitionTimeout = null, TimeSpan? entryTimeout = null,
    DailyUiTiming? timing = null, TimeSpan? updateTimeout = null,
    Func<FrameSnapshot, TaskContext, CancellationToken, Task<FrameSnapshot>>? reconnect = null,
    Func<FrameSnapshot, bool>? windowClosed = null) : IGameTask
{
    public static readonly TimeSpan InitialPageTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RestartTimeout = TimeSpan.FromMinutes(2);
    public TimeSpan EntryWorldStableDuration { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan GiftClaimStableDuration { get; init; } = TimeSpan.FromSeconds(1);
    public string Id => "start-journey";
    public string Name => "进入游戏";

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var recognitionBudget = recognitionTimeout ?? InitialPageTimeout;
        var entryBudget = entryTimeout ?? TimeSpan.FromSeconds(60);
        var updateBudget = updateTimeout ?? UpdateTimeout;
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
        var waitingUpdate = false;
        var restartConfirmed = false;
        var observations = 0;
        var initialHits = 0;
        double? bestInitialScore = null;
        var lastPage = JourneyPage.Unknown;
        var startClicked = false;
        int? selectedCard = null;
        FrameSnapshot? giftPanel = null;
        TimeSpan? cardVerifiedAt = null;
        var giftDismissed = false;
        var blankTried = false;
        var dismissedCurrentPopup = false;
        var loginClosed = false;
        var monthlyObserved = false;
        var monthlyRewardObserved = false;
        var rewardClosed = false;
        var menuDismissed = false;

        async Task ReconnectAsync(FrameSnapshot oldWindow)
        {
            deadline.Token.ThrowIfCancellationRequested();
            restartConfirmed = waitingUpdate = true;
            canRetry = false;
            deadline.CancelAfter(RestartTimeout);
            viewport = await reconnect!(oldWindow, context, deadline.Token);
            after = viewport.SystemTimestamp;
            lastId = viewport.Id;
            previous = actionSource = null;
        }

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
                    if (viewport is not null && !viewport.SameClientSurface(frame))
                        throw new InputUnavailableException("进入游戏期间窗口身份或客户区尺寸发生变化。");
                    if (viewport is not null && !viewport.SameViewport(frame))
                    {
                        previous = null;
                        canRetry = false;
                    }
                    var waitingForMonthlyReward = monthlyObserved && !monthlyRewardObserved;
                    if (viewport is not null && ExtraRewardTask.ObservedAfter(context, viewport))
                    { previous = null; canRetry = false; }
                    // A return receipt belongs to the showcase handled between these two observations.
                    if (!waitingForMonthlyReward && giftPanel is not null && viewport is not null && ExtraRewardTask.ReturnedAfter(context, viewport))
                    {
                        giftDismissed = true;
                        loginClosed = canRetry = false;
                        previous = null;
                        readyAt = context.Execution.Elapsed + pace.ActionSettle;
                        context.Log("已确认登录领取后的特殊奖励展示返回");
                    }
                    viewport = frame;
                    var observed = await recognizer.ObserveAsync(frame, deadline.Token, blankTried || waitingForMonthlyReward ? null : giftPanel);
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
                    var stableFor = frame.SystemTimestamp - stableSince;
                    if (stableFor < pace.StableDuration || (observed is { Page: JourneyPage.LoginReward, ClaimPoint: not null }
                        && stableFor < GiftClaimStableDuration)) continue;
                    if (observed.Page is JourneyPage.UpdatePrompt or JourneyPage.Updating or JourneyPage.RestartPrompt)
                    {
                        if (entering || restartConfirmed) continue;
                        if (observed.Page == JourneyPage.RestartPrompt)
                        {
                            if (observed.ConfirmPoint is not { } restartPoint) continue;
                            if (reconnect is null) throw new InputUnavailableException("重启后无法重新连接游戏窗口。");
                            await ClickAsync(restartPoint, "确认资源更新并重启游戏");
                            context.Log("等待游戏自行重启并重新连接窗口");
                            await ReconnectAsync(frame);
                            continue;
                        }
                        if (waitingUpdate) continue;
                        if (observed.Page == JourneyPage.UpdatePrompt)
                        {
                            if (observed.ConfirmPoint is not { } confirmPoint) continue;
                            await ClickAsync(confirmPoint, "确认游戏资源更新");
                            canRetry = false;
                        }
                        waitingUpdate = true;
                        deadline.CancelAfter(updateBudget);
                        context.Log($"等待资源更新完成，最长 {updateBudget.TotalMinutes:0.#} 分钟");
                        continue;
                    }
                    if (waitingUpdate && observed.Page != JourneyPage.Start) continue;
                    if (!entering)
                    {
                        entering = true;
                        waitingUpdate = false;
                        deadline.CancelAfter(entryBudget);
                    }
    
                    if (canRetry && context.Execution.Elapsed - actionAt >= pace.RetryDelay)
                    {
                        context.Log($"{actionName}：页面仍未切换，重试 1/1");
                        await ClickAsync(observed.ClaimPoint ?? actionPoint, actionName, retry: true);
                        continue;
                    }
    
                    if (observed.Page != JourneyPage.Reward) dismissedCurrentPopup = false;
                    if (observed.Page != JourneyPage.Menu) menuDismissed = false;
    
                    switch (observed.Page)
                    {
                        case JourneyPage.InGame:
                        case JourneyPage.Menu:
                            if (selectedCard is not null && (cardVerifiedAt is null || !giftDismissed)) continue;
                            if (waitingForMonthlyReward) continue;
                            giftPanel = null;
                            if (observed.Page == JourneyPage.Menu)
                            {
                                if (!menuDismissed)
                                {
                                    await context.Input.PressKeyAsync(frame, GameKey.Escape, deadline.Token);
                                    menuDismissed = true;
                                    after = Now;
                                    previous = null;
                                    readyAt = context.Execution.Elapsed + pace.ActionSettle;
                                    context.Log("已确认主菜单，返回大世界观察登录奖励");
                                }
                                continue;
                            }
                            if (frame.SystemTimestamp - stableSince < EntryWorldStableDuration) continue;
                            return new(true, $"大世界已连续稳定 {EntryWorldStableDuration.TotalSeconds:0.##} 秒，进入游戏完成");
                        case JourneyPage.Start:
                            if (startClicked || selectedCard is not null || loginClosed || monthlyObserved || rewardClosed) break;
                            if (observed.StartPoint is not { } startPoint) break;
                            await ClickAsync(startPoint, "开始旅程");
                            startClicked = true;
                            context.Log("已点击开始旅程，等待游戏加载");
                            break;
                        case JourneyPage.LoginReward:
                            if (loginClosed) break;
                            var sameCardChecked = selectedCard is int cardIndex && observed.ClaimedCards?.Contains(cardIndex) == true;
                            if (sameCardChecked && cardVerifiedAt is null)
                            {
                                cardVerifiedAt = context.Execution.Elapsed;
                                context.Log($"已确认第 {selectedCard + 1} 张登录奖励卡片的领取勾选");
                            }
                            if ((sameCardChecked && giftDismissed && cardVerifiedAt is { } checkedAt
                                 && context.Execution.Elapsed - checkedAt >= pace.ActionSettle)
                                || (selectedCard is null && observed.LoginRewardComplete && observed.ClaimPoint is null))
                            {
                                if (observed.ClosePoint is not { } close) break;
                                await ClickAsync(close, "关闭已确认领取的登录奖励面板");
                                loginClosed = true;
                            }
                            else if (selectedCard is null && observed.ClaimPoint is { } claim && observed.ClaimCardIndex is { } card && card >= 0
                                && observed.ClaimedCards?.Contains(card) != true)
                            {
                                await ClickAsync(claim, $"领取第 {card + 1} 张登录奖励卡片");
                                selectedCard = card;
                                giftPanel = frame;
                                context.Log("已点击登录奖励卡片，等待奖励展示与同卡勾选");
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
                            if (dismissedCurrentPopup || observed.ClosePoint is not { } dismiss) break;
                            await ClickAsync(dismiss, "关闭已确认的获得弹窗");
                            dismissedCurrentPopup = rewardClosed = true;
                            if (monthlyObserved && !monthlyRewardObserved) monthlyRewardObserved = true;
                            else
                            {
                                if (selectedCard is not null) giftDismissed = true;
                                loginClosed = false;
                            }
                            break;
                        case JourneyPage.PossibleGiftReward:
                            if (giftPanel is null || waitingForMonthlyReward || blankTried || observed.ClosePoint is not { } blank) break;
                            await ClickAsync(blank, "登录领取后出现未知奖励样式，尝试点击空白处关闭");
                            blankTried = giftDismissed = true;
                            loginClosed = canRetry = false;
                            break;
                    }
    
                    async Task ClickAsync(ClientPoint point, string action, bool retry = false)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if (!Fresh(frame)) throw new InputUnavailableException("识别帧已过期，未发送点击。");
                        if (point.X < 0 || point.Y < 0 || point.X >= frame.Width || point.Y >= frame.Height)
                            throw new InvalidOperationException("进入游戏识别目标超出客户区，未发送点击。");
                        await context.Input.ClickAsync(frame, point, deadline.Token);
                        context.Log(action);
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
                catch (InvalidOperationException) when (waitingUpdate && !restartConfirmed && viewport is not null
                    && reconnect is not null && windowClosed?.Invoke(viewport) == true)
                {
                    context.Log("资源更新期间原游戏窗口已关闭，等待新游戏窗口");
                    await ReconnectAsync(viewport);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            context.Log($"进入游戏识别诊断：新帧 {observations}，初始页命中 {initialHits}，适龄标识最高分 {bestInitialScore?.ToString("F3") ?? "无"}，最后页面 {lastPage}");
            var reason = waitingUpdate && restartConfirmed ? "重启后未确认新的游戏窗口和开始旅程页面"
                : waitingUpdate ? "资源更新后未确认开始旅程页面"
                : selectedCard is not null && cardVerifiedAt is null ? "登录奖励未确认同一卡片勾选"
                : selectedCard is not null && !giftDismissed ? "已确认卡片勾选，未等到登录奖励展示，已保留当前面板"
                : selectedCard is not null && !loginClosed ? "登录奖励关闭后未确认礼赠面板或游戏界面"
                : monthlyObserved && !monthlyRewardObserved ? "未确认月卡奖励到账"
                : !entering ? "未稳定识别到初始页、登录奖励或游戏界面，未发送点击"
                : "未确认进入游戏，任务已停止";
            var budget = waitingUpdate ? (restartConfirmed ? RestartTimeout : updateBudget) : entering ? entryBudget : recognitionBudget;
            return new(false, $"{budget.TotalSeconds:0.#} 秒等待超时；{reason}") { AllowPageRecovery = false };
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

    private static bool Fresh(FrameSnapshot frame) => Now - frame.SystemTimestamp is var age
        && age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(1);
    private static TimeSpan Now => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
}
