using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Features.StartJourney;

public enum JourneyPage { Unknown, Start, InGame, LoginReward, MonthlyPending, Reward, Menu, UpdatePrompt }
public sealed record JourneyObservation(JourneyPage Page, ClientPoint? StartPoint = null,
    ClientPoint? ClaimPoint = null, int? ClaimCardIndex = null, IReadOnlyList<int>? ClaimedCards = null,
    bool LoginRewardComplete = false, ClientPoint? ClosePoint = null, ClientPoint? ConfirmPoint = null,
    double? InitialPageScore = null);
public interface IJourneyRecognizer
{
    Task<JourneyObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token);
}

public sealed class StartJourneyRecognizer(ImageTemplate ageRating, JourneySettings settings,
    Func<FrameSnapshot, CancellationToken, Task<bool>> isWorld, JourneyRewardRecognizer? rewards = null,
    JourneyUpdateRecognizer? updates = null) : IJourneyRecognizer, IDisposable
{
    public void Dispose() { }
    public async Task<JourneyObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token)
    {
        if (frame.Width != settings.ReferenceWidth || frame.Height != settings.ReferenceHeight)
            throw new InvalidOperationException("进入游戏功能当前仅支持 1920×1080 客户区。");
        token.ThrowIfCancellationRequested();
        if (updates?.Observe(frame) is { } update) return update;
        if (rewards?.Observe(frame) is { } reward) return reward;
        var roi = settings.AgeSearch;
        var match = OpenCvTemplateMatcher.Match(frame, ageRating, new PixelRegion(roi[0], roi[1], roi[2], roi[3]));
        if (match.Score >= settings.AgeThreshold)
            return new JourneyObservation(JourneyPage.Start,
                new ClientPoint(settings.StartClick[0], settings.StartClick[1]), InitialPageScore: match.Score);
        return new JourneyObservation(await isWorld(frame, token) ? JourneyPage.InGame : JourneyPage.Unknown,
            InitialPageScore: match.Score);
    }
}
