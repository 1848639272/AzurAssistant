using System.IO;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Game.Interaction;

public enum PickupKind { Gather, Mining, Logging }

public sealed record PickupItem(ClientPoint Icon, string Name, int VisibleRows, PickupKind Kind = PickupKind.Gather)
{
    public string Key => $"{Kind}:{Name}:{VisibleRows}:{Icon.Y / 10}";
}
public enum PickupBlockReason { None, Excluded, Unrecognized }

public sealed record PickupObservation(PickupItem? Candidate = null, PickupBlockReason BlockReason = PickupBlockReason.None)
{
    public bool Blocked => BlockReason != PickupBlockReason.None;
}
public interface IPickupRecognizer
{
    bool IsPromptVisible(FrameSnapshot frame);
    Task<PickupObservation> ReadAsync(FrameSnapshot frame, CancellationToken token, bool verifyAbsence = false);
}

/// <summary>Recognizes the selected collectible row only. The caller establishes its world-page boundary.</summary>
public sealed class PickupRecognizer : IPickupRecognizer
{
    private readonly PpOcrReader _ocr;
    private readonly InteractionAssets _assets;
    internal PickupRecognizer(PpOcrReader ocr, InteractionAssets assets) { _ocr = ocr; _assets = assets; }
    public static PickupRecognizer Load(PpOcrReader ocr, string? directory = null) => new(ocr,
        InteractionAssets.Load(directory ?? Path.Combine(AppContext.BaseDirectory, "assets", "realtime")));
    private (ClientPoint Center, PickupKind Kind)? Collectible(FrameSnapshot frame, PixelRegion row)
    {
        var gather = _assets.Find(frame, "pickup-icon", row);
        var mining = _assets.Version != "1.1" ? _assets.Find(frame, "mining-icon", row) : null;
        var logging = _assets.Version != "1.1" ? _assets.Find(frame, "logging-icon", row) : null;
        return (gather, mining, logging) switch
        {
            ({ } icon, null, null) => (icon.Center, PickupKind.Gather),
            (null, { } icon, null) => (icon.Center, PickupKind.Mining),
            (null, null, { } icon) => (icon.Center, PickupKind.Logging),
            _ => null
        };
    }

    public bool IsPromptVisible(FrameSnapshot frame)
    {
        if (frame.Width != _assets.Width || frame.Height != _assets.Height || _assets.Find(frame, "interact-key") is not { } key) return false;
        var row = new PixelRegion(_assets.IconSearchX, key.Center.Y - _assets.RowHeight / 2, _assets.IconSearchWidth, _assets.RowHeight);
        return _assets.Find(frame, "talk-icon", row) is null && Collectible(frame, row) is not null;
    }
    public async Task<PickupObservation> ReadAsync(FrameSnapshot frame, CancellationToken token, bool verifyAbsence = false)
    {
        token.ThrowIfCancellationRequested();
        if (frame.Width != _assets.Width || frame.Height != _assets.Height) return new(BlockReason: PickupBlockReason.Unrecognized);
        var key = _assets.Find(frame, "interact-key");
        if (key is null)
            return verifyAbsence && !HasClearInteractionArea(frame) ? new(BlockReason: PickupBlockReason.Unrecognized) : new();
        var row = new PixelRegion(_assets.IconSearchX, key.Center.Y - _assets.RowHeight / 2, _assets.IconSearchWidth, _assets.RowHeight);
        if (_assets.Find(frame, "talk-icon", row) is not null) return new(BlockReason: PickupBlockReason.Excluded);
        if (Collectible(frame, row) is not { } icon || Math.Abs(icon.Center.Y - key.Center.Y) > _assets.MaxRowDistance)
            return new(BlockReason: PickupBlockReason.Unrecognized);
        var nameRegion = new PixelRegion(icon.Center.X + _assets.NameOffsetX,
            icon.Center.Y - _assets.RowHeight / 2, _assets.NameWidth, _assets.RowHeight);
        if (_assets.BlockedNameTemplates.Values.Any(id => _assets.Find(frame, id, nameRegion) is not null))
            return new(BlockReason: PickupBlockReason.Excluded);
        var text = await _ocr.ReadAsync(frame, token, nameRegion, 2);
        if (text.Count == 0 || text.Any(t => t.Confidence < .70)) return new(BlockReason: PickupBlockReason.Unrecognized);
        var name = string.Concat(text.OrderBy(t => t.Y).ThenBy(t => t.X)
            .Select(t => string.Concat(t.Text.Where(c => !char.IsWhiteSpace(c)))));
        if (name.Length == 0) return new(BlockReason: PickupBlockReason.Unrecognized);
        if (_assets.BlockedNameTemplates.ContainsKey(name)) return new(BlockReason: PickupBlockReason.Excluded);
        var count = 0;
        for (var i = 0; i < _assets.MaxVisibleRows; i++)
        {
            token.ThrowIfCancellationRequested();
            if (Collectible(frame, new(_assets.IconSearchX,
                _assets.FirstRowY + i * _assets.RowSpacing - _assets.RowHeight / 2, _assets.IconSearchWidth, _assets.RowHeight)) is not null) count++;
        }
        return new(new(icon.Center, name, Math.Max(1, count), icon.Kind));
    }

    private bool HasClearInteractionArea(FrameSnapshot frame)
    {
        // An animation can hide the F glyph while the row or the world controls are still transitioning.
        if (_assets.Find(frame, "world-menu") is null || _assets.Find(frame, "world-hud") is null) return false;
        var rows = new PixelRegion(_assets.IconSearchX, _assets.FirstRowY - _assets.RowHeight / 2,
            _assets.IconSearchWidth, _assets.RowHeight + (_assets.MaxVisibleRows - 1) * _assets.RowSpacing);
        return _assets.Find(frame, "talk-icon", rows) is null && _assets.Find(frame, "pickup-icon", rows) is null
            && (_assets.Version == "1.1" || _assets.Find(frame, "mining-icon", rows) is null
                && _assets.Find(frame, "logging-icon", rows) is null);
    }
}
