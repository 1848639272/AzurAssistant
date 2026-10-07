using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;

namespace AzurAssistant.Features.ClaimMail;

public sealed class ClaimMailTask(IDailyUiRecognizer recognizer, DailyUiTiming? timing = null) : IGameTask
{
    public string Id => "claim-mail";
    public string Name => "领取邮件";

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var navigator = new UiNavigator(context, recognizer, timing ?? DailyUiTiming.Default);
        var claimed = false;
        var emptyDialogObserved = false;
        try
        {
            var initial = await navigator.WaitAsync("确认邮件任务起点", token, null, DailyPage.World, DailyPage.Menu, DailyPage.MailEmpty);
            if (initial.Ui.Page == DailyPage.MailEmpty) return await CompleteEmptyAsync();
            var menu = await navigator.OpenMenuAsync(token);
            var mail = await navigator.TransitionAsync(menu, "menu-mail", "打开邮箱", token, "mail-claim", pages: [DailyPage.Mail]);
            claimed = true;
            var outcome = await navigator.TransitionAsync(mail, "mail-claim", "领取邮件附件", token, pages: [DailyPage.MailReward, DailyPage.MailEmpty]);
            if (outcome.Ui.Page == DailyPage.MailEmpty) return await CompleteEmptyAsync();
            var reward = await navigator.WaitAsync("确认邮件奖励关闭位置", token, "mail-dismiss", DailyPage.MailReward);
            context.Log("已确认邮件奖励弹窗");
            await navigator.TransitionAsync(reward, "mail-dismiss", "关闭邮件奖励", token, offsetY: 80, pages: [DailyPage.Mail]);
            await navigator.ReturnToWorldAsync(token);
            return new(true, "邮件奖励已领取，已返回大世界");
        }
        catch (UiStepException error)
        {
            var reason = emptyDialogObserved ? "无可领取邮件提示关闭失败；" + error.Message
                : claimed ? "邮件领取流程未完成；" + error.Message : error.Message;
            return new(false, reason);
        }

        async Task<TaskResult> CompleteEmptyAsync()
        {
            emptyDialogObserved = true;
            var empty = await navigator.WaitAsync("确认无可领取邮件提示", token, "mail-empty-confirm", DailyPage.MailEmpty);
            context.Log("所有邮件附件都已被领取，确认提示后退出邮箱");
            await navigator.TransitionAsync(empty, "mail-empty-confirm", "确认无可领取邮件提示", token, pages: [DailyPage.Mail, DailyPage.World]);
            await navigator.ReturnToWorldAsync(token);
            return new(true, "已无可领取邮件，已返回大世界");
        }
    }
}
