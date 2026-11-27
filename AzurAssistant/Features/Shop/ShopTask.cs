using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;

namespace AzurAssistant.Features.Shop;

public sealed class ShopTask(IDailyUiRecognizer recognizer, DailyUiTiming timing, bool keepMenuOpen) : IGameTask
{
    public string Id => "shop";
    public string Name => "商城";

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var nav = new UiNavigator(context, recognizer, timing);
        try
        {
            var menu = await nav.OpenMenuAsync(token);
            if (!menu.Ui.Has("menu-shop"))
                menu = await nav.WaitAsync("确认主菜单商城入口", token, "menu-shop", DailyPage.Menu);
            var page = await nav.TransitionAsync(menu, "menu-shop", "打开商城", token,
                pages: [DailyPage.Shop, DailyPage.ShopFeatured, DailyPage.ShopLimited]);
            if (page.Ui.Page == DailyPage.Shop)
            {
                page = await FindTabAsync(page, "featured-tab", "甄选星品", new(1590, 1033), new(510, 1033),
                    DailyPage.ShopFeatured, DailyPage.ShopLimited);
                if (page.Ui.Page == DailyPage.Shop)
                    page = await nav.TransitionAsync(page, "featured-tab", "选择甄选星品", token,
                        pages: [DailyPage.ShopFeatured, DailyPage.ShopLimited]);
            }
            if (page.Ui.Page == DailyPage.ShopFeatured)
            {
                page = await FindTabAsync(page, "limited-tab", "限时珍藏", new(1770, 149), new(803, 149), DailyPage.ShopLimited);
                if (page.Ui.Page == DailyPage.ShopFeatured)
                    page = await nav.TransitionAsync(page, "limited-tab", "选择限时珍藏", token, pages: [DailyPage.ShopLimited]);
            }
            page = await nav.WaitAsync("确认限时珍藏第一格", token, "first-slot-ready", DailyPage.ShopLimited);
            // A rendered price proves that a paid item occupies the first slot. Empty/loading tiles prove nothing.
            var claimed = false;
            if (!page.Ui.Has("price-yuan"))
            {
                var detail = await nav.TransitionAsync(page, "gift-free", "打开每日惊喜盒子", token, "detail-free",
                    pages: [DailyPage.ShopGiftDetail]);
                var reward = await nav.TransitionAsync(detail, "detail-free", "确认免费领取", token, allowRetry: false,
                    accept: (seen, _) => Task.FromResult(seen.Ui.Page == DailyPage.ShopReward || seen.Ui.Has("price-yuan")),
                    pages: [DailyPage.ShopReward, DailyPage.ShopLimited]);
                if (reward.Ui.Page == DailyPage.ShopReward)
                    page = await nav.TransitionAsync(reward, "reward-dismiss", "关闭商城奖励", token,
                        pages: [DailyPage.Shop, DailyPage.ShopFeatured, DailyPage.ShopLimited]);
                else page = reward;
                claimed = true;
            }
            await nav.TransitionAsync(page, "close", "关闭商城", token, pages: [DailyPage.Menu, DailyPage.World]);
            var endpoint = await nav.FinishAtBoundaryAsync(keepMenuOpen, token);
            return new(true, (claimed ? "已领取每日惊喜盒子，" : "今日商城免费奖励已领取，") + endpoint);
        }
        catch (UiStepException error) { return new(false, error.Message); }

        async Task<ObservedUi> FindTabAsync(ObservedUi page, string target, string label, ClientPoint from, ClientPoint to,
            params DailyPage[] selectedPages)
        {
            foreach (var (start, end) in new[] { (from, to), (to, from) })
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (page.Ui.Has(target) || selectedPages.Contains(page.Ui.Page)) return page;
                    page = await nav.DragRowAsync(page, start, end, $"滑动查找{label}", token, selectedPages);
                }
            if (page.Ui.Has(target) || selectedPages.Contains(page.Ui.Page)) return page;
            throw new UiStepException($"滑动后仍未找到{label}");
        }
    }
}
