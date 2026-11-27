using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Features.StartJourney;

public enum JourneyPage { Unknown, Start, InGame, LoginReward, MonthlyPending, Reward, Menu, UpdatePrompt, Updating, RestartPrompt, PossibleGiftReward }
public sealed record JourneyObservation(JourneyPage Page, ClientPoint? StartPoint = null,
    ClientPoint? ClaimPoint = null, int? ClaimCardIndex = null, IReadOnlyList<int>? ClaimedCards = null,
    bool LoginRewardComplete = false, ClientPoint? ClosePoint = null, ClientPoint? ConfirmPoint = null,
    double? InitialPageScore = null);
public interface IJourneyRecognizer
{
    Task<JourneyObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token, FrameSnapshot? giftPanel = null);
}

public sealed class StartJourneyRecognizer(ImageTemplate ageRating, JourneySettings settings,
    Func<FrameSnapshot, CancellationToken, Task<JourneyPage>> readGamePage, JourneyRewardRecognizer? rewards = null,
    JourneyUpdateRecognizer? updates = null) : IJourneyRecognizer, IDisposable
{
    public void Dispose() { }
    public async Task<JourneyObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token, FrameSnapshot? giftPanel = null)
    {
        if (frame.Width != settings.ReferenceWidth || frame.Height != settings.ReferenceHeight)
            throw new InvalidOperationException("进入游戏功能当前仅支持 1920×1080 客户区。");
        token.ThrowIfCancellationRequested();
        if (updates?.Observe(frame) is { } update) return update;
        var reward = rewards?.Observe(frame);
        if (reward is not null && (giftPanel is null || reward.Page != JourneyPage.Unknown)) return reward;
        var roi = settings.AgeSearch;
        var match = OpenCvTemplateMatcher.Match(frame, ageRating, new PixelRegion(roi[0], roi[1], roi[2], roi[3]));
        if (match.Score >= settings.AgeThreshold)
            return reward ?? new JourneyObservation(JourneyPage.Start,
                new ClientPoint(settings.StartClick[0], settings.StartClick[1]), InitialPageScore: match.Score);
        var gamePage = await readGamePage(frame, token);
        if (gamePage is JourneyPage.InGame or JourneyPage.Menu) return reward ?? new(gamePage, InitialPageScore: match.Score);
        if (giftPanel is not null && rewards?.FindSpecialGiftBlank(frame, giftPanel) is { } blank)
            return new(JourneyPage.PossibleGiftReward, ClosePoint: blank);
        return reward ?? new(JourneyPage.Unknown, InitialPageScore: match.Score);
    }
}
