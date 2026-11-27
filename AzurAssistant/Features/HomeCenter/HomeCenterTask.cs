using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;

namespace AzurAssistant.Features.HomeCenter;

public sealed class HomeCenterTask(IDailyUiRecognizer recognizer, IDiningSatietyReader dining, DailyUiTiming? timing = null,
    bool keepMenuOpen = false, bool cakesOnly = false) : IGameTask
{
    public string Id => "home-center";
    public string Name => "家园中枢";

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var delays = timing ?? DailyUiTiming.Default;
        var navigator = new UiNavigator(context, recognizer, delays);
        var pendingClaim = "";
        var diningDiagnostic = "";
        var collectionOutcomes = new List<string>();
        async Task<ObservedUi> CollectAsync(ObservedUi source, string target, string name, int offsetY = 0)
        {
            pendingClaim = name + "收取";
            var attempt = context.Execution.Elapsed;
            Task<bool> AcceptResult(ObservedUi observed, CancellationToken cancellation) => Task.FromResult(
                observed.Ui.Page != source.Ui.Page || (observed.Ui.Has(target)
                    || source.Ui.Page == DailyPage.Ranch && observed.Ui.Has("ranch-basket-empty"))
                && context.Execution.Elapsed - attempt >= delays.ActionSettle + delays.RetryDelay);
            var reward = await navigator.TransitionAsync(source, target, $"收取{name}产出", token, offsetY: offsetY,
                allowRetry: false, accept: AcceptResult, pages: [DailyPage.HomeReward, DailyPage.HomeLevelUp, source.Ui.Page]);
            if (reward.Ui.Page == DailyPage.HomeLevelUp)
                reward = await navigator.DismissHomeLevelUpAsync(reward, token, DailyPage.HomeReward, source.Ui.Page);
            pendingClaim = "";
            if (reward.Ui.Page == source.Ui.Page)
            {
                var outcome = $"{name}已尝试收取，未出现收获弹窗";
                context.Log(outcome + "；已确认原页面稳定，不重复收取，继续后续步骤");
                collectionOutcomes.Add(outcome);
                return reward;
            }
            collectionOutcomes.Add(name + "已确认收获");
            context.Log($"已确认{name}收获物品");
            var returned = await navigator.TransitionAsync(reward, "home-reward-close", $"关闭{name}收获结果", token,
                pages: [source.Ui.Page, DailyPage.HomeLevelUp]);
            if (returned.Ui.Page == DailyPage.HomeLevelUp)
                returned = await navigator.DismissHomeLevelUpAsync(returned, token, source.Ui.Page);
            return returned;
        }
        try
        {
            var menu = await navigator.OpenMenuAsync(token);
            var center = await navigator.TransitionAsync(menu, "menu-home", "进入家园中枢", token, "home-dining", pages: [DailyPage.HomeCenter]);
            DiningSatiety? previous = null;
            DiningSatiety? baseline = null;
            var diningGeneration = context.Execution.Generation;
            async Task<bool> ReadBaseline(ObservedUi observed, CancellationToken cancellation)
            {
                if (diningGeneration != context.Execution.Generation)
                { diningGeneration = context.Execution.Generation; previous = null; }
                var value = await dining.ReadAsync(observed.Frame, cancellation);
                if (!context.Execution.IsObservationCurrent(observed.Frame)) { previous = null; return false; }
                diningDiagnostic = value is null ? "饱腹值未能读取" : $"最后饱腹值 {value.Current}/{value.Capacity}";
                var stable = value is not null && value == previous;
                previous = value;
                if (stable) baseline = value;
                return stable;
            }
            var table = await navigator.TransitionAsync(center, "home-dining", "打开奇波餐桌并确认饱腹值", token,
                "dining-add", accept: ReadBaseline, pages: [DailyPage.DiningTable]);
            var before = baseline!;
            var diningOutcome = "餐桌饱腹值已满，无需补充";
            if (before.Current < before.Capacity && cakesOnly)
            {
                (table, diningOutcome) = await FeedCakesAsync(table, before, navigator, context, token);
                context.Log(diningOutcome);
            }
            else if (before.Current < before.Capacity)
            {
                pendingClaim = "餐桌补充";
                previous = null;
                DiningSatiety? confirmed = null;
                async Task<bool> ConfirmResult(ObservedUi observed, CancellationToken cancellation)
                {
                    if (diningGeneration != context.Execution.Generation)
                    { diningGeneration = context.Execution.Generation; previous = null; }
                    var value = await dining.ReadAsync(observed.Frame, cancellation);
                    if (!context.Execution.IsObservationCurrent(observed.Frame)) { previous = null; return false; }
                    diningDiagnostic = value is null ? "饱腹值未能读取" : $"最后饱腹值 {value.Current}/{value.Capacity}";
                    var stable = value is not null && value == previous && value.Capacity == before.Capacity && value.Current >= before.Current;
                    previous = value;
                    if (stable) confirmed = value;
                    return stable;
                }
                context.Log($"餐桌添加前饱腹值 {before.Current}/{before.Capacity}");
                table = await navigator.TransitionAsync(table, "dining-add", "餐桌一键添加并观察结果", token,
                    "dining-close", allowRetry: false, accept: ConfirmResult, pages: [DailyPage.DiningTable]);
                pendingClaim = "";
                if (confirmed!.Current > before.Current)
                {
                    diningOutcome = "餐桌已补充";
                    context.Log($"已确认餐桌饱腹值增加：{before.Current}/{before.Capacity} → {confirmed.Current}/{confirmed.Capacity}");
                }
                else
                {
                    // An unchanged value permits closing after one attempt, but does not prove an empty inventory or a successful refill.
                    diningOutcome = "餐桌已尝试添加，饱腹值未增加";
                    context.Log($"{diningOutcome}（{confirmed.Current}/{confirmed.Capacity}）；不重复添加，关闭后继续建筑与牧场");
                }
            }
            else context.Log($"餐桌饱腹值已满 {before.Current}/{before.Capacity}，无需补充");
            center = await navigator.TransitionAsync(table, "dining-close", "关闭奇波餐桌", token, "home-buildings", pages: [DailyPage.HomeCenter]);
            diningDiagnostic = "";
            var buildings = await navigator.TransitionAsync(center, "home-buildings", "打开建筑管理", token, "buildings-claim-label", pages: [DailyPage.Buildings]);
            // The fixed claim label sits 98 px above the chest's click area in the supported 1080p layout.
            buildings = await CollectAsync(buildings, "buildings-claim-label", "建筑", offsetY: 98);
            center = await navigator.TransitionAsync(buildings, "buildings-back", "返回家园中枢", token, "home-ranch", pages: [DailyPage.HomeCenter]);
            if (center.Ui.Has("home-ranch-locked"))
            {
                collectionOutcomes.Add("奇波牧场尚未解锁");
                context.Log("奇波牧场入口显示锁标，跳过牧场");
            }
            else
            {
                var ranch = await navigator.TransitionAsync(center, "home-ranch", "打开奇波牧场", token,
                    accept: (observed, _) => Task.FromResult(observed.Ui.Has("ranch-claim") != observed.Ui.Has("ranch-basket-empty")),
                    pages: [DailyPage.Ranch]);
                if (ranch.Ui.Has("ranch-basket-empty"))
                {
                    const string outcome = "牧场篮子为空，无可收获产出";
                    context.Log(outcome + "；跳过收取，继续返回");
                    collectionOutcomes.Add(outcome);
                }
                else await CollectAsync(ranch, "ranch-claim", "牧场");
            }
            var endpoint = await navigator.FinishAtBoundaryAsync(keepMenuOpen, token);
            return new(true, $"{diningOutcome}；{string.Join("；", collectionOutcomes)}；{endpoint}");
        }
        catch (UiStepException error)
        {
            var reason = pendingClaim.Length > 0 ? pendingClaim + "结果未确认；" + error.Message : error.Message;
            if (diningDiagnostic.Length > 0) reason += "；" + diningDiagnostic;
            return new(false, reason);
        }
    }

    private async Task<(ObservedUi Table, string Outcome)> FeedCakesAsync(ObservedUi table, DiningSatiety satiety,
        UiNavigator navigator, TaskContext context, CancellationToken token)
    {
        var added = 0;
        var drags = 0;
        var stationaryDrags = 0;
        while (satiety.Current < satiety.Capacity)
        {
            if (!table.Ui.Has("dining-cake"))
            {
                if (++drags > 10) throw new UiStepException("餐桌食材列表未确认到达右端，停止查找波波饼");
                var previousShelf = table.Frame;
                table = await navigator.DragRowAsync(table, new(1420, 957), new(825, 957), "向右查找波波饼", token);
                stationaryDrags = !table.Ui.Has("dining-cake") && table.Ui.Has("dining-add") && SameShelf(previousShelf, table.Frame)
                    ? stationaryDrags + 1 : 0;
                if (stationaryDrags >= 2) break;
                continue;
            }
            DiningSatiety? previous = null;
            var generation = context.Execution.Generation;
            async Task<bool> ConfirmFeeding(ObservedUi observed, CancellationToken ct)
            {
                if (generation != context.Execution.Generation) { previous = null; generation = context.Execution.Generation; }
                var value = await dining.ReadAsync(observed.Frame, ct);
                if (!context.Execution.IsObservationCurrent(observed.Frame)) { previous = null; return false; }
                var confirmed = value is not null && value == previous && value.Capacity == satiety.Capacity && value.Current > satiety.Current;
                previous = value;
                return confirmed;
            }
            table = await navigator.TransitionAsync(table, "dining-cake", "添加波波饼并确认饱腹值增加", token,
                "dining-close", allowRetry: false, accept: ConfirmFeeding, pages: [DailyPage.DiningTable]);
            satiety = previous!;
            added++;
            stationaryDrags = 0;
        }
        return (table, added == 0 ? "已查至食材列表右端，未发现波波饼" : $"已添加波波饼，饱腹值 {satiety.Current}/{satiety.Capacity}");
    }

    private static bool SameShelf(FrameSnapshot left, FrameSnapshot right)
    {
        if (!left.SameClientSurface(right) || left.Width != 1920 || left.Height != 1080
            || left.Pixels.Length < left.Stride * left.Height || right.Pixels.Length < right.Stride * right.Height) return false;
        var a = left.Pixels.Span;
        var b = right.Pixels.Span;
        var changed = 0;
        var total = 0;
        // Compare card illustrations at fixed positions. Exclude counts, highlights and the moving background.
        for (var y = 924; y <= 973; y += 5)
        for (var x = 355; x <= 1520; x += 5)
        {
            var i = y * left.Stride + x * 4;
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i+1] - b[i+1]) + Math.Abs(a[i+2] - b[i+2]) > 60) changed++;
            total++;
        }
        return changed < total * .04;
    }
}
