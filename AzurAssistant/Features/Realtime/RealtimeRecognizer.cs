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
public sealed record RealtimeObservation(RealtimePage Page, ClientPoint? SkipPoint = null,
    string DialogueText = "", PickupItem? Pickup = null, bool PickupBlocked = false,
    DialogueAction Action = DialogueAction.SkipArrow);

public interface IRealtimeRecognizer
{
    Task<RealtimeObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token);
    Task<RealtimeObservation> ObserveDialogueControlAsync(FrameSnapshot frame, CancellationToken token)
        => ObserveAsync(frame, token);
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
        var control = FindDialogueControl(frame, token);
        if (control is { Page: RealtimePage.Dialogue, Action: DialogueAction.SkipArrow })
            return control with { DialogueText = JoinText(await _ocr.ReadAsync(frame, token, _assets.DialogueTextRegion, 2)) };
        if (control is not null) return control;
        if (_assets.Find(frame, "world-menu") is null) return new(RealtimePage.Unknown);
        if (_assets.Find(frame, "world-hud") is null && (!_assets.HasHomeHud || _assets.Find(frame, "home-leave-label") is null))
            return new(RealtimePage.Unknown);

        var result = await _pickup.ReadAsync(frame, token);
        return new(RealtimePage.World, Pickup: result.Candidate,
            PickupBlocked: result.Blocked);
    }

    public Task<RealtimeObservation> ObserveDialogueControlAsync(FrameSnapshot frame, CancellationToken token)
        => Task.FromResult(FindDialogueControl(frame, token) ?? new RealtimeObservation(RealtimePage.Unknown));

    private RealtimeObservation? FindDialogueControl(FrameSnapshot frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (frame.Width != _assets.Width || frame.Height != _assets.Height) return new(RealtimePage.Unknown);
        var summary = _assets.Find(frame, "dialogue-summary-title");
        var summaryConfirm = _assets.Find(frame, "dialogue-summary-confirm");
        if (summary is not null)
        {
            var point = summaryConfirm is not null ? summaryConfirm.Center with { X = summaryConfirm.Center.X + 90 } :
                ButtonFromEdge(frame, "dialogue-summary-continue", "dialogue-summary-skip-edge", 878, -9, -112);
            if (point is not null) return new(RealtimePage.Dialogue, point, "剧情梗概", Action: DialogueAction.ConfirmSummary);
            return new(RealtimePage.Unknown);
        }
        var question = _assets.Find(frame, "dialogue-prompt-question");
        var promptConfirm = _assets.Find(frame, "dialogue-prompt-confirm")
            ?? (question is not null && _assets.Version != "1.1" ? _assets.Find(frame, "dialogue-prompt-confirm-hover") : null);
        if (question is not null)
        {
            var point = promptConfirm is not null ? promptConfirm.Center with { X = promptConfirm.Center.X + 68 } :
                ButtonFromEdge(frame, "dialogue-prompt-cancel", "dialogue-prompt-confirm-edge", 422, -12, -59);
            if (point is not null) return new(RealtimePage.Dialogue, point, "是否确认跳过本段剧情", Action: DialogueAction.ConfirmPrompt);
            return new(RealtimePage.Unknown);
        }
        // A confirmation control without its specific story evidence cannot authorize any input.
        if (summaryConfirm is not null || promptConfirm is not null)
            return new(RealtimePage.Unknown);

        var auto = _assets.Find(frame, "dialogue-auto");
        var skip = _assets.Find(frame, "dialogue-skip");
        if (auto is not null && skip is not null) return new(RealtimePage.Dialogue, skip.Center);
        // Partial dialogue evidence blocks world interaction on this frame.
        return auto is not null || skip is not null ? new(RealtimePage.Unknown) : null;
    }

    // A pointer or hover animation may obscure the label. Verify both controls on this dialog before using its right edge.
    private ClientPoint? ButtonFromEdge(FrameSnapshot frame, string otherId, string edgeId, int spacingX, int spacingY, int inset)
    {
        if (!_assets.HasDialogueEdges) return null;
        var other = _assets.Find(frame, otherId)?.Center;
        var edge = _assets.Find(frame, edgeId, allowInvertedContrast: true)?.Center;
        if (other is not { } left || edge is not { } right || Math.Abs(right.X - left.X - spacingX) > 7
            || Math.Abs(right.Y - left.Y - spacingY) > 7) return null;
        return new(right.X + inset, right.Y + 12);
    }

    private static string JoinText(IReadOnlyList<TextRegion> text) => string.Concat(text
        .Where(line => line.Confidence >= 0.7).OrderBy(line => line.Y).ThenBy(line => line.X)
        .Select(line => string.Concat(line.Text.Where(character => !char.IsWhiteSpace(character)))));

}
