using System.IO;
using System.Text.Json;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Features.StartJourney;

public sealed class JourneyRewardRecognizer
{
    private readonly TemplateCatalog _templates;
    private readonly RewardLayout _layout;

    public JourneyRewardRecognizer(string directory)
    {
        _templates = new TemplateCatalog(directory, "rewards.json");
        _layout = JsonSerializer.Deserialize<RewardLayout>(File.ReadAllText(Path.Combine(directory, "reward-layout.json")))
            ?? throw new InvalidDataException("登录奖励布局为空。");
        if (_layout.SchemaVersion != 1 || _layout.CardCenters is not { Length: 7 }
            || _layout.CardCenters.Distinct().Count() != 7 || _layout.CardClickY is < 0 or >= 1080
            || _layout.MaxArrowOffset is < 1 or > 20 || _layout.DismissOffsetY is < 0 or > 150)
            throw new InvalidDataException("登录奖励布局版本或参数无效。");
        foreach (var center in _layout.CardCenters)
            foreach (var region in new[] { _layout.TopArrow, _layout.BottomArrow, _layout.Check, _layout.Edge })
                At(center, region).Validate(1920, 1080);
    }

    public JourneyObservation? Observe(FrameSnapshot frame)
    {
        var title = _templates.Match(frame, "login-title");
        var close = _templates.Match(frame, "login-close");
        if (title is not null && close is not null) return ObserveLoginReward(frame, close.Center);
        if (title is not null) return new(JourneyPage.Unknown);

        var monthly = _templates.Match(frame, "monthly-label");
        var monthlyPrompt = _templates.Match(frame, "monthly-prompt");
        if (monthly is not null && monthlyPrompt is not null) return new(JourneyPage.MonthlyPending);
        if (monthly is not null || monthlyPrompt is not null) return new(JourneyPage.Unknown);

        var reward = _templates.Match(frame, "reward-title");
        var dismiss = _templates.Match(frame, "reward-dismiss");
        if (reward is not null && dismiss is not null)
            return new(JourneyPage.Reward, ClosePoint: dismiss.Center with { Y = dismiss.Center.Y + _layout.DismissOffsetY });
        if (reward is not null || dismiss is not null) return new(JourneyPage.Unknown);
        return null;
    }

    private JourneyObservation ObserveLoginReward(FrameSnapshot frame, ClientPoint close)
    {
        var claimed = new List<int>();
        var available = new List<int>();
        var idleCards = 0;
        var ambiguous = false;
        for (var index = 0; index < _layout.CardCenters.Length; index++)
        {
            var center = _layout.CardCenters[index];
            var check = _templates.Match(frame, "claimed-check", At(center, _layout.Check));
            var top = _templates.Match(frame, "login-arrow-top", At(center, _layout.TopArrow));
            var bottom = _templates.Match(frame, "login-arrow-bottom", At(center, _layout.BottomArrow));
            var highlight = _templates.Match(frame, "login-highlight", At(center, _layout.Edge));
            var paired = top is not null && bottom is not null
                && Math.Abs(top.Center.X - bottom.Center.X) <= _layout.MaxArrowOffset
                && Math.Abs(top.Center.X - center) <= _layout.MaxArrowOffset;
            if (paired && highlight is not null && check is null) available.Add(index);
            if (check is not null && top is null && bottom is null && highlight is null)
            { claimed.Add(index); idleCards++; }
            else if (check is null && top is null && bottom is null && highlight is null
                && new[] { "future-card-edge", "future-card-edge-2", "future-card-edge-3" }
                    .Any(id => _templates.Match(frame, id, At(center, _layout.Edge)) is not null)) idleCards++;
            else if ((top is not null || bottom is not null || highlight is not null || check is not null)
                && (!paired || highlight is null || check is not null)) ambiguous = true;
        }
        if (available.Count == 1 && !ambiguous)
        {
            var index = available[0];
            return new(JourneyPage.LoginReward, ClaimPoint: new ClientPoint(_layout.CardCenters[index], _layout.CardClickY),
                ClaimCardIndex: index, ClaimedCards: claimed, ClosePoint: close);
        }
        // A completed row requires positive evidence for every claimed or future card.
        return new(JourneyPage.LoginReward, ClaimedCards: claimed,
            LoginRewardComplete: available.Count == 0 && !ambiguous && idleCards == 7 && claimed.Count > 0,
            ClosePoint: close);
    }

    private static PixelRegion At(int center, PixelRegion region) => region with { X = center + region.X };

    private sealed record RewardLayout
    {
        public int SchemaVersion { get; init; }
        public int[] CardCenters { get; init; } = [];
        public int CardClickY { get; init; }
        public int MaxArrowOffset { get; init; }
        public int DismissOffsetY { get; init; }
        public PixelRegion TopArrow { get; init; }
        public PixelRegion BottomArrow { get; init; }
        public PixelRegion Check { get; init; }
        public PixelRegion Edge { get; init; }
    }
}
