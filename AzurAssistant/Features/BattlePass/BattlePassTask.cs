using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;

namespace AzurAssistant.Features.BattlePass;

public sealed class BattlePassTask(IDailyUiRecognizer recognizer, DailyUiTiming? timing = null, bool keepMenuOpen = false) : IGameTask
{
    public string Id => "claim-battle-pass";
    public string Name => "通行证领取";

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var nav = new UiNavigator(context, recognizer, timing ?? DailyUiTiming.Default);
        try
        {
            var menu = await nav.OpenMenuAsync(token);
            var page = await nav.TransitionAsync(menu, "menu-entry", "打开通行证", token, pages: [DailyPage.PassTasks, DailyPage.PassRewards]);
            if (page.Ui.Page != DailyPage.PassTasks)
                page = await nav.TransitionAsync(page, "tasks-label", "切换通行证任务栏", token, pages: [DailyPage.PassTasks]);
            if (page.Ui.Has("claim-all"))
            {
                page = await nav.TransitionAsync(page, "claim-all", "一键领取通行证任务经验", token, allowRetry: false,
                    accept: (next, _) => Task.FromResult(next.Ui.Page == DailyPage.PassLevelUp || next.Ui.Has("empty-unlocked")),
                    pages: [DailyPage.PassTasks, DailyPage.PassLevelUp]);
                if (page.Ui.Page == DailyPage.PassLevelUp)
                {
                    page = await nav.TransitionAsync(page, "level-title", "点击空白处关闭通行证升级", token,
                        offsetY: 215, allowRetry: false, accept: TaskResultVisible,
                        pages: [DailyPage.PassTasks, DailyPage.PassRewards, DailyPage.PassReward]);
                    if (page.Ui.Page == DailyPage.PassReward)
                        page = await nav.TransitionAsync(page, "reward-dismiss", "关闭升级获得的奖励", token,
                            offsetY: 80, allowRetry: false, accept: TaskResultVisible, pages: [DailyPage.PassTasks, DailyPage.PassRewards]);
                    if (page.Ui.Page == DailyPage.PassRewards)
                        page = await nav.TransitionAsync(page, "tasks-label", "确认通行证任务经验已领完", token,
                            resultTarget: "empty-unlocked", pages: [DailyPage.PassTasks]);
                }
                context.Log("已确认通行证任务经验领取后的完成布局");
            }
            else if (!page.Ui.Has("empty-unlocked")) throw new UiStepException("通行证任务经验状态不明");
            page = await nav.TransitionAsync(page, "rewards-label", "切换通行证奖励栏", token, pages: [DailyPage.PassRewards]);
            if (page.Ui.Has("claim-all"))
            {
                var claimFrame = page.Frame;
                page = await nav.TransitionAsync(page, "claim-all", "一键领取通行证奖励", token, allowRetry: false,
                    accept: (next, _) => Task.FromResult(next.Ui.Page == DailyPage.PassReward ||
                        next.Ui.Has("empty-unlocked") && ExtraRewardTask.ReturnedAfter(context, claimFrame)),
                    pages: [DailyPage.PassReward, DailyPage.PassRewards]);
                if (page.Ui.Page == DailyPage.PassReward)
                    page = await nav.TransitionAsync(page, "reward-dismiss", "关闭通行证奖励弹窗", token, offsetY: 80, allowRetry: false,
                        pages: [DailyPage.PassRewards, DailyPage.PassSpecialReward]);
                if (page.Ui.Page == DailyPage.PassSpecialReward)
                    page = await nav.TransitionAsync(page, "special-dismiss", "关闭通行证第二次奖励弹窗", token,
                        offsetY: -100, allowRetry: false, pages: [DailyPage.PassRewards]);
                if (!page.Ui.Has("empty-unlocked")) throw new UiStepException("奖励弹窗已关闭，但未确认通行证奖励领完");
                context.Log("已确认通行证奖励弹窗及领取后的完成布局");
            }
            else if (!page.Ui.Has("empty-unlocked")) throw new UiStepException("通行证奖励状态不明");
            await nav.TransitionAsync(page, "close", "关闭通行证并返回主菜单", token, pages: [DailyPage.Menu]);
            var endpoint = await nav.FinishAtBoundaryAsync(keepMenuOpen, token);
            return new(true, $"通行证当前可领取经验与奖励处理完成，{endpoint}");
        }
        catch (UiStepException ex) { return new(false, "通行证领取未完成；" + ex.Message); }

        static Task<bool> TaskResultVisible(ObservedUi next, CancellationToken _) =>
            Task.FromResult(next.Ui.Page != DailyPage.PassTasks || next.Ui.Has("empty-unlocked"));
    }
}
