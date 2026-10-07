using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;

namespace AzurAssistant.Features.ActivityClaims;

public sealed class ActivityClaimsTask(IDailyUiRecognizer recognizer, DailyUiTiming? timing = null) : IGameTask
{
    public string Id => "claim-activity";
    public string Name => "日常周常领取";

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var nav = new UiNavigator(context, recognizer, timing ?? DailyUiTiming.Default);
        try
        {
            var menu = await nav.OpenMenuAsync(token);
            var page = await nav.TransitionAsync(menu, "menu-entry", "打开日常周常", token,
                pages: [DailyPage.ActivityDay, DailyPage.ActivityWeek]);
            if (page.Ui.Page != DailyPage.ActivityDay)
                page = await nav.TransitionAsync(page, "day-label", "切换日常活跃", token, pages: [DailyPage.ActivityDay]);
            page = await ClaimPageAsync(page);
            page = await nav.TransitionAsync(page, "week-label", "切换周常活跃", token, pages: [DailyPage.ActivityWeek]);
            page = await ClaimPageAsync(page);
            await nav.TransitionAsync(page, "close", "关闭日常周常", token, pages: [DailyPage.Menu]);
            await nav.ReturnToWorldAsync(token);
            return new(true, "日常与周常当前可领取奖励处理完成，已返回大世界");
        }
        catch (UiStepException ex) { return new(false, $"日常周常领取未完成；{ex.Message}"); }

        async Task<ObservedUi> ClaimPageAsync(ObservedUi current)
        {
            var expected = current.Ui.Page;
            var label = expected == DailyPage.ActivityDay ? "日常" : "周常";
            if (current.Ui is not ActivityObservation initial) throw new UiStepException($"{label}缺少活跃度证据");
            if (initial.Has("claim-0"))
            {
                if (initial.Activity is null || string.IsNullOrWhiteSpace(initial.FirstTask))
                    throw new UiStepException($"{label}领取前活跃度或任务名称无法确认");
                bool TasksClaimed(ObservedUi next) => next.Ui is ActivityObservation after
                    && after.TasksSettled && after.Activity is not null
                    && (after.Activity > initial.Activity || initial.Activity == 100 && after.Activity == 100);
                current = await nav.TransitionAsync(current, "claim-0", $"点击首项批量领取{label}任务活跃度：{initial.FirstTask}", token,
                    allowRetry: false, accept: (next, _) => Task.FromResult(next.Ui.Page == DailyPage.MailReward || TasksClaimed(next)),
                    pages: [expected, DailyPage.MailReward]);
                if (current.Ui.Page == DailyPage.MailReward)
                    current = await nav.TransitionAsync(current, "mail-dismiss", $"点击空白处关闭{label}任务奖励", token, offsetY: 80,
                        allowRetry: false, accept: (next, _) => Task.FromResult(TasksClaimed(next)), pages: [expected]);
                context.Log($"已确认{label}任务批量领取后的列表与活跃度");
            }
            else if (!initial.TasksSettled) throw new UiStepException($"{label}任务列表未确认完整状态，停止领取");
            var beforeChests = (ActivityObservation)current.Ui;
            if (beforeChests.Chests.Length != 5 || beforeChests.Chests.Any(c => c == ActivityChest.Unknown))
                throw new UiStepException($"{label}宝箱状态未全部确认，停止领取");
            for (var slot = 4; slot >= 0; slot--)
            {
                if (beforeChests.Chests[slot] == ActivityChest.Unknown) throw new UiStepException($"{label}{(slot + 1) * 20}档宝箱状态不明");
                if (beforeChests.Chests[slot] != ActivityChest.Ready) continue;
                bool Claimed(ObservedUi next) => next.Ui is ActivityObservation after
                    && beforeChests.Chests.Select((chest, i) => chest != ActivityChest.Ready || after.Chests[i] == ActivityChest.Claimed).All(x => x);
                current = await nav.TransitionAsync(current, $"chest-{slot}", $"点击{label}{(slot + 1) * 20}档批量领取活跃度奖励", token,
                    allowRetry: false, accept: (next, _) => Task.FromResult(next.Ui.Page == DailyPage.MailReward
                        || Claimed(next)),
                    pages: [expected, DailyPage.MailReward]);
                if (current.Ui.Page == DailyPage.MailReward)
                    current = await nav.TransitionAsync(current, "mail-dismiss", $"关闭{label}活跃度奖励并确认宝箱已领取", token, offsetY: 80,
                        allowRetry: false, accept: (next, _) => Task.FromResult(Claimed(next)), pages: [expected]);
                context.Log($"已确认{label}本次可领取宝箱全部打开");
                break;
            }
            return current;
        }
    }
}
