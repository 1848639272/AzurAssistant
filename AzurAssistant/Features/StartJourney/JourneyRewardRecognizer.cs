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
        if (_layout.SchemaVersion != 1 || _layout.FutureEdges is not { Length: > 0 } || _layout.FutureEdges.Any(string.IsNullOrWhiteSpace)
            || _layout.CardCenters is not { Length: 7 }
            || _layout.CardCenters.Distinct().Count() != 7 || _layout.CardClickY is < 0 or >= 1080
            || _layout.MaxArrowOffset is < 1 or > 20 || _layout.DismissOffsetY is < 0 or > 150)
            throw new InvalidDataException("登录奖励布局版本或参数无效。");
        foreach (var center in _layout.CardCenters)
            foreach (var region in new[] { _layout.TopArrow, _layout.BottomArrow, _layout.Check, _layout.Edge })
                At(center, region).Validate(1920, 1080);
    }

    public JourneyObservation? Observe(FrameSnapshot frame)
    {
        var reward = _templates.Match(frame, "reward-title");
        var dismiss = _templates.Match(frame, "reward-dismiss") ?? (reward is null ? null : _templates.Match(frame, "reward-dismiss-faint"));
        if (reward is not null && dismiss is not null)
            return new(JourneyPage.Reward, ClosePoint: dismiss.Center with { Y = dismiss.Center.Y + _layout.DismissOffsetY });
        if (reward is not null) return new(JourneyPage.Unknown);

        var title = _templates.Match(frame, "login-title");
        var close = _templates.Match(frame, "login-close");
        if (title is not null && close is not null) return ObserveLoginReward(frame, close.Center);
        if (title is not null) return new(JourneyPage.Unknown);

        var monthly = _templates.Match(frame, "monthly-label");
        var monthlyPrompt = _templates.Match(frame, "monthly-prompt");
        if (monthly is not null && monthlyPrompt is not null) return new(JourneyPage.MonthlyPending);
        if (monthly is not null || monthlyPrompt is not null) return new(JourneyPage.Unknown);

        if (dismiss is not null) return new(JourneyPage.Unknown);
        return null;
    }

    internal ClientPoint? FindSpecialGiftBlank(FrameSnapshot frame, FrameSnapshot giftPanel)
    {
        if (!frame.SameClientSurface(giftPanel) || _templates.Match(frame, "login-close") is not null) return null;
        var pixels = frame.Pixels.Span;
        var panel = giftPanel.Pixels.Span;
        // Require visible content on a changed screen. A blank load screen or a missing corner icon alone is insufficient.
        int changed = 0, detail = 0;
        for (var y = 200; y < 860; y += 48)
        for (var x = 360; x < 1560; x += 48)
        {
            var i = y * frame.Stride + x * 4;
            if (ColorDistance(pixels[i..], panel[i..]) > 30) changed++;
            if (ColorDistance(pixels[i..], pixels[(i + 48 * 4)..]) > 20) detail++;
        }
        if (changed < 30 || detail < 12) return null;

        // Prefer undecorated side margins away from the cards, the close button and any central reward controls.
        foreach (var y in new[] { 780, 540, 660, 420 })
        foreach (var x in new[] { 240, 1680, 360, 1560 })
        {
            var center = pixels[(y * frame.Stride + x * 4)..];
            var clear = true;
            for (var dy = -16; dy <= 16 && clear; dy++)
            for (var dx = -16; dx <= 16; dx++)
                if (ColorDistance(center, pixels[((y + dy) * frame.Stride + (x + dx) * 4)..]) > 24)
                { clear = false; break; }
            if (clear) return new(x, y);
        }
        return null;
    }

    private static int ColorDistance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        Math.Max(Math.Abs(a[0] - b[0]), Math.Max(Math.Abs(a[1] - b[1]), Math.Abs(a[2] - b[2])));

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
            var triangle = FindBrightTriangle(frame, center);
            var marker = triangle ?? top?.Center;
            var active = marker is { } arrow && Math.Abs(arrow.X - center) <= _layout.MaxArrowOffset &&
                (bottom is null ? highlight is not null || triangle is not null
                    : Math.Abs(arrow.X - bottom.Center.X) <= _layout.MaxArrowOffset);
            if (active && check is null) available.Add(index);
            if (check is not null && marker is null && bottom is null && highlight is null)
            { claimed.Add(index); idleCards++; }
            else if (check is null && marker is null && bottom is null && highlight is null
                && _layout.FutureEdges
                    .Any(id => _templates.Match(frame, id, At(center, _layout.Edge)) is not null)) idleCards++;
            else if ((marker is not null || bottom is not null || highlight is not null || check is not null)
                && (!active || check is not null)) ambiguous = true;
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

    private ClientPoint? FindBrightTriangle(FrameSnapshot frame, int center)
    {
        // The marker has a bright horizontal shoulder tapering to a tip. Compare it to nearby pixels
        // in the same frame so character art behind the translucent glow can vary across columns.
        var region = At(center, _layout.TopArrow);
        int Light(int x, int y)
        {
            var pixel = frame.Pixels.Span.Slice(y * frame.Stride + x * 4, 3);
            return (pixel[0] + 2 * pixel[1] + pixel[2]) / 4;
        }
        var firstX = Math.Max(center - 4, region.X + 5);
        var lastX = Math.Min(center + 4, region.X + region.Width - 6);
        for (var y = region.Y + 5; y < region.Y + region.Height - 8; y++)
        for (var x = firstX; x <= lastX; x++)
        {
            var left = Light(x - 5, y);
            var right = Light(x + 5, y);
            var tip = Light(x, y + 4);
            if (Math.Min(Math.Min(left, right), Math.Min(tip, Light(x, y))) < 233) continue;
            if (left - Light(x - 5, y + 5) >= 18 && right - Light(x + 5, y + 5) >= 18 &&
                tip - Light(x, y + 8) >= 18 && Light(x, y) - Light(x, y - 5) >= 12)
                return new(x, y);
        }
        return null;
    }

    private static PixelRegion At(int center, PixelRegion region) => region with { X = center + region.X };

    private sealed record RewardLayout
    {
        public int SchemaVersion { get; init; }
        public int[] CardCenters { get; init; } = [];
        public string[] FutureEdges { get; init; } = ["future-card-edge", "future-card-edge-2", "future-card-edge-3"];
        public int CardClickY { get; init; }
        public int MaxArrowOffset { get; init; }
        public int DismissOffsetY { get; init; }
        public PixelRegion TopArrow { get; init; }
        public PixelRegion BottomArrow { get; init; }
        public PixelRegion Check { get; init; }
        public PixelRegion Edge { get; init; }
    }
}
