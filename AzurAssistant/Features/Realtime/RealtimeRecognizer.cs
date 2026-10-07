using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;
using AzurAssistant.Game.Interaction;

namespace AzurAssistant.Features.Realtime;

public enum RealtimePage { Unknown, World, Dialogue }
public enum DialogueAction { SkipArrow, ConfirmSummary, ConfirmPrompt }
public sealed record PickupCandidate(ClientPoint Icon, string Name, int VisibleRows);
public sealed record RealtimeObservation(RealtimePage Page, ClientPoint? SkipPoint = null,
    string DialogueText = "", PickupCandidate? Pickup = null, bool PickupBlocked = false,
    DialogueAction Action = DialogueAction.SkipArrow);

public interface IRealtimeRecognizer
{
    Task<RealtimeObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token);
}

public sealed class RealtimeRecognizer : IRealtimeRecognizer
{
    private readonly PpOcrReader _ocr;
    private readonly InteractionAssets _assets;
    private readonly PickupRecognizer _pickup;
    private RealtimeRecognizer(PpOcrReader ocr, InteractionAssets assets) { _ocr = ocr; _assets = assets; _pickup = new(ocr, assets); }

    public static RealtimeRecognizer Load(PpOcrReader ocr, string? directory = null) =>
        new(ocr, InteractionAssets.Load(directory ?? Path.Combine(AppContext.BaseDirectory, "assets", "realtime")));

    public async Task<RealtimeObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (frame.Width != _assets.Width || frame.Height != _assets.Height)
            return new(RealtimePage.Unknown);

        var summary = _assets.Find(frame, "dialogue-summary-title");
        var summaryConfirm = _assets.Find(frame, "dialogue-summary-confirm");
        if (summary is not null && summaryConfirm is not null)
            return new(RealtimePage.Dialogue, summaryConfirm.Center, "剧情梗概", Action: DialogueAction.ConfirmSummary);
        var question = _assets.Find(frame, "dialogue-prompt-question");
        var promptConfirm = _assets.Find(frame, "dialogue-prompt-confirm");
        if (question is not null && promptConfirm is not null)
            return new(RealtimePage.Dialogue, promptConfirm.Center, "是否确认跳过本段剧情", Action: DialogueAction.ConfirmPrompt);
        // A confirmation control without its specific story evidence cannot authorize any input.
        if (summary is not null || summaryConfirm is not null || question is not null || promptConfirm is not null)
            return new(RealtimePage.Unknown);

        var auto = _assets.Find(frame, "dialogue-auto");
        var skip = _assets.Find(frame, "dialogue-skip");
        if (auto is not null && skip is not null)
        {
            var lines = await _ocr.ReadAsync(frame, token, _assets.DialogueTextRegion, 2);
            return new(RealtimePage.Dialogue, skip.Center, JoinText(lines));
        }
        // A partial dialogue marker is ambiguous; it never grants an interaction key.
        if (auto is not null || skip is not null) return new(RealtimePage.Unknown);
        if (_assets.Find(frame, "world-menu") is null || _assets.Find(frame, "world-hud") is null)
            return new(RealtimePage.Unknown);

        var result = await _pickup.ReadAsync(frame, token);
        return new(RealtimePage.World, Pickup: result.Candidate is { } item ? new(item.Icon, item.Name, item.VisibleRows) : null,
            PickupBlocked: result.Blocked);
    }

    private static string JoinText(IReadOnlyList<TextRegion> text) => string.Concat(text
        .Where(line => line.Confidence >= 0.7).OrderBy(line => line.Y).ThenBy(line => line.X)
        .Select(line => string.Concat(line.Text.Where(character => !char.IsWhiteSpace(character)))));

}
