using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;

namespace AzurAssistant.Features.ClaimMail;

public sealed class ClaimMailTask(IDailyUiRecognizer recognizer, DailyUiTiming? timing = null, bool keepMenuOpen = false) : IGameTask
{
    public string Id => "claim-mail";
    public string Name => "领取邮件";

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var navigator = new UiNavigator(context, new InboxResultRecognizer(recognizer, context.Execution), timing ?? DailyUiTiming.Default);
        var claimed = false;
        var emptyDialogObserved = false;
        try
        {
            var initial = await navigator.WaitAsync("确认邮件任务起点", token, null, DailyPage.World, DailyPage.Menu, DailyPage.MailEmpty);
            if (initial.Ui.Page == DailyPage.MailEmpty) return await CompleteEmptyAsync(initial);
            var menu = await navigator.OpenMenuAsync(token);
            var mail = await navigator.TransitionAsync(menu, "menu-mail", "打开邮箱", token,
                accept: (next, _) => Task.FromResult(next.Ui.Page == DailyPage.MailEmpty || next.Ui.Has("mail-claim")),
                pages: [DailyPage.Mail, DailyPage.MailEmpty]);
            if (mail.Ui.Page == DailyPage.MailEmpty) return await CompleteEmptyAsync(mail);
            claimed = true;
            var outcome = await navigator.TransitionAsync(mail, "mail-claim", "领取邮件附件", token, allowRetry: false,
                accept: (next, _) => Task.FromResult(next.Ui.Page != DailyPage.Mail || ExtraRewardTask.ReturnedAfter(context, mail.Frame)),
                pages: [DailyPage.MailReward, DailyPage.MailEmpty, DailyPage.Mail]);
            if (outcome.Ui.Page == DailyPage.MailEmpty) return await CompleteEmptyAsync(outcome);
            if (outcome.Ui.Page == DailyPage.MailReward)
            {
                var reward = await navigator.WaitAsync("确认邮件奖励关闭位置", token, "mail-dismiss", DailyPage.MailReward);
                context.Log("已确认邮件奖励弹窗");
                var inbox = await navigator.TransitionAsync(reward, "mail-dismiss", "关闭邮件奖励", token, offsetY: 80,
                    pages: [DailyPage.Mail, DailyPage.MailEmpty]);
                if (inbox.Ui.Page == DailyPage.MailEmpty) return await CompleteEmptyAsync(inbox);
            }
            var endpoint = await navigator.FinishAtBoundaryAsync(keepMenuOpen, token);
            return new(true, $"邮件奖励已领取，{endpoint}");
        }
        catch (UiStepException error)
        {
            var reason = emptyDialogObserved ? "无可领取邮件提示关闭失败；" + error.Message
                : claimed ? "邮件领取流程未完成；" + error.Message : error.Message;
            return new(false, reason);
        }

        async Task<TaskResult> CompleteEmptyAsync(ObservedUi observed)
        {
            if (observed.Ui.EvidenceKey == InboxResultRecognizer.EmptyReceipt)
            {
                await navigator.TransitionAsync(observed, "mail-close", "关闭已领完附件的邮箱", token, pages: [DailyPage.Menu, DailyPage.World]);
            }
            else
            {
                emptyDialogObserved = true;
                var empty = await navigator.WaitAsync("确认无可领取邮件提示", token, "mail-empty-confirm", DailyPage.MailEmpty);
                context.Log("所有邮件附件都已被领取，确认提示后退出邮箱");
                await navigator.TransitionAsync(empty, "mail-empty-confirm", "确认无可领取邮件提示", token, pages: [DailyPage.Mail, DailyPage.World]);
            }
            var endpoint = await navigator.FinishAtBoundaryAsync(keepMenuOpen, token);
            return new(true, $"已无可领取邮件，{endpoint}");
        }
    }

    private sealed class InboxResultRecognizer(IDailyUiRecognizer inner, TaskExecution execution) : IDailyUiRecognizer
    {
        public const string EmptyReceipt = "mail-attachments-empty";
        private FrameSnapshot? _receipt;
        private int _generation;

        public async Task<DailyUiObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token)
        {
            var generation = execution.Generation;
            var ui = await inner.ObserveAsync(frame, token);
            if (!execution.IsObservationCurrent(frame) || generation != execution.Generation) return ui;
            if (_generation != generation || _receipt?.SameClientSurface(frame) != true)
                _receipt = null;
            _generation = generation;
            if (ui.Page == DailyPage.MailEmpty && ui.Has("mail-empty-banner") && ui.Has("mail-close"))
                _receipt = frame;
            else if (ui.Page != DailyPage.Mail)
                _receipt = null;

            // The banner may vanish during settling. Only the current inbox may supply the close position.
            if (_receipt is null || !ui.Targets.TryGetValue("mail-close", out var close)) return ui;
            return ui with { Page = DailyPage.MailEmpty, EvidenceKey = EmptyReceipt,
                Targets = new Dictionary<string, ClientPoint> { ["mail-close"] = close } };
        }
    }
}
