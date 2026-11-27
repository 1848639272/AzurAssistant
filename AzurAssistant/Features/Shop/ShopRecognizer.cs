using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;
using AzurAssistant.Vision;

namespace AzurAssistant.Features.Shop;

public sealed class ShopRecognizer(IDailyUiRecognizer daily, TemplateCatalog templates) : IDailyUiRecognizer
{
    public async Task<DailyUiObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var targets = new Dictionary<string, ClientPoint>();
        bool Match(string id, PixelRegion? region = null)
        {
            if (templates.Match(frame, id, region) is not { } hit) return false;
            targets[id] = hit.Center;
            return true;
        }
        DailyUiObservation Result(DailyPage page) => new(page, targets)
            { Diagnostic = string.Join(", ", targets.Keys) };

        if (Match("detail-title"))
        {
            if (Match("detail-free")) return Result(DailyPage.ShopGiftDetail);
            return Result(DailyPage.Unknown);
        }
        if (Match("reward-title") && Match("reward-dismiss")) return Result(DailyPage.ShopReward);
        targets.Clear();
        if (Match("title") && Match("close"))
        {
            if (!Match("featured-tab-selected"))
            {
                Match("featured-tab");
                return Result(DailyPage.Shop);
            }
            if (!Match("limited-tab")) return Result(DailyPage.ShopFeatured);
            var tab = targets["limited-tab"];
            if (!Match("selected-marker", new PixelRegion(tab.X - 21, 160, 43, 27)))
                return Result(DailyPage.ShopFeatured);
            var title = Match("gift-title");
            var free = Match("gift-free");
            if (title && free) targets["first-slot-ready"] = targets["gift-free"];
            else if (!title && !free && Match("price-yuan")) targets["first-slot-ready"] = targets["price-yuan"];
            return Result(DailyPage.ShopLimited);
        }
        var other = await daily.ObserveAsync(frame, token);
        if (other.Page != DailyPage.Menu || templates.Match(frame, "menu-shop") is not { } entrance) return other;
        return other with { Targets = new Dictionary<string, ClientPoint>(other.Targets) { ["menu-shop"] = entrance.Center } };
    }
}
