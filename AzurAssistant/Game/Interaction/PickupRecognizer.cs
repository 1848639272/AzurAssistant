using System.IO;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Game.Interaction;

public sealed record PickupItem(ClientPoint Icon, string Name, int VisibleRows)
{
    public string Key => $"{Name}:{VisibleRows}:{Icon.Y / 10}";
}
public sealed record PickupObservation(PickupItem? Candidate = null, bool Blocked = false);
public interface IPickupRecognizer
{
    bool IsPromptVisible(FrameSnapshot frame);
    Task<PickupObservation> ReadAsync(FrameSnapshot frame, CancellationToken token);
}

/// <summary>Recognizes the selected collectible row only. The caller establishes its world-page boundary.</summary>
public sealed class PickupRecognizer : IPickupRecognizer
{
    private readonly PpOcrReader _ocr;
    private readonly InteractionAssets _assets;
    internal PickupRecognizer(PpOcrReader ocr, InteractionAssets assets) { _ocr = ocr; _assets = assets; }
    public static PickupRecognizer Load(PpOcrReader ocr, string? directory = null) => new(ocr,
        InteractionAssets.Load(directory ?? Path.Combine(AppContext.BaseDirectory, "assets", "realtime")));
    public bool IsPromptVisible(FrameSnapshot frame)
    {
        if (frame.Width != _assets.Width || frame.Height != _assets.Height || _assets.Find(frame, "interact-key") is not { } key) return false;
        var row = new PixelRegion(_assets.IconSearchX, key.Center.Y - _assets.RowHeight / 2, _assets.IconSearchWidth, _assets.RowHeight);
        return _assets.Find(frame, "talk-icon", row) is null && _assets.Find(frame, "pickup-icon", row) is not null;
    }
    public async Task<PickupObservation> ReadAsync(FrameSnapshot frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (frame.Width != _assets.Width || frame.Height != _assets.Height) return new(Blocked: true);
        var key = _assets.Find(frame, "interact-key");
        if (key is null) return new();
        var row = new PixelRegion(_assets.IconSearchX, key.Center.Y - _assets.RowHeight / 2, _assets.IconSearchWidth, _assets.RowHeight);
        if (_assets.Find(frame, "talk-icon", row) is not null) return new(Blocked: true);
        var icon = _assets.Find(frame, "pickup-icon", row);
        if (icon is null || Math.Abs(icon.Center.Y - key.Center.Y) > _assets.MaxRowDistance) return new(Blocked: true);
        var text = await _ocr.ReadAsync(frame, token, new(icon.Center.X + _assets.NameOffsetX,
            icon.Center.Y - _assets.RowHeight / 2, _assets.NameWidth, _assets.RowHeight), 2);
        var name = string.Concat(text.Where(t => t.Confidence >= .7).OrderBy(t => t.Y).ThenBy(t => t.X)
            .Select(t => string.Concat(t.Text.Where(c => !char.IsWhiteSpace(c)))));
        var count = 0;
        for (var i = 0; i < _assets.MaxVisibleRows; i++)
        {
            token.ThrowIfCancellationRequested();
            if (_assets.Find(frame, "pickup-icon", new(_assets.IconSearchX,
                _assets.FirstRowY + i * _assets.RowSpacing - _assets.RowHeight / 2, _assets.IconSearchWidth, _assets.RowHeight)) is not null) count++;
        }
        return new(new(icon.Center, name, Math.Max(1, count)));
    }
}
