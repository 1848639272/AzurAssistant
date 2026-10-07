using System.IO;
using System.Text.Json;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Game.Ui;

public sealed record WorldHudEvidence(ClientPoint? Point, string Diagnostic);

public sealed class WorldHudReader
{
    private readonly PpOcrReader _ocr;
    private readonly Settings _settings;
    private sealed record Label(PixelRegion Region, int[] Scales, double MinimumConfidence);
    private sealed record Settings(int SchemaVersion, int Width, int Height, Label Esc, Label Enter);

    public WorldHudReader(PpOcrReader ocr, string? directory = null)
    {
        _ocr = ocr;
        _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(
            directory ?? Path.Combine(AppContext.BaseDirectory, "assets", "daily"), "world-ocr.json")))
            ?? throw new InvalidDataException("大世界OCR配置为空。");
        if (_settings.SchemaVersion != 2 || _settings.Width != 1920 || _settings.Height != 1080
            || _settings.Esc is null || _settings.Enter is null)
            throw new InvalidDataException("大世界OCR配置版本或参数不受支持。");
        foreach (var label in new[] { _settings.Esc, _settings.Enter })
        {
            if (label.Scales is null || label.Scales.Length is < 1 or > 2 || label.Scales.Any(s => s is < 1 or > 3)
                || label.Scales.Distinct().Count() != label.Scales.Length || !double.IsFinite(label.MinimumConfidence) || label.MinimumConfidence is <= 0 or > 1)
                throw new InvalidDataException("大世界OCR标签参数不受支持。");
            label.Region.Validate(_settings.Width, _settings.Height);
        }
    }

    public Task<WorldHudEvidence> ReadEscAsync(FrameSnapshot frame, CancellationToken token) => ReadAsync(frame, _settings.Esc, "Esc", token);
    public Task<WorldHudEvidence> ReadEnterAsync(FrameSnapshot frame, CancellationToken token) => ReadAsync(frame, _settings.Enter, "Enter", token);
    private async Task<WorldHudEvidence> ReadAsync(FrameSnapshot frame, Label label, string expected, CancellationToken token)
    {
        if (frame.Width != _settings.Width || frame.Height != _settings.Height)
            throw new InvalidOperationException("大世界识别当前仅支持1920×1080客户区。");
        foreach (var preprocessing in new[] { OcrPreprocessing.Original, OcrPreprocessing.WhiteText })
        foreach (var scale in label.Scales)
        {
            var text = await _ocr.ReadAsync(frame, token, label.Region, scale, preprocessing);
            var matches = text.Where(item => item.Confidence >= label.MinimumConfidence
                && string.Equals(item.Text.Trim(), expected, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) continue;
            var match = matches[0];
            return new(new((int)Math.Round(match.X + match.Width / 2), (int)Math.Round(match.Y + match.Height / 2)),
                $"world-{expected.ToLowerInvariant()}-ocr={expected}命中，置信度{match.Confidence:F3}，倍率{scale}，{preprocessing}");
        }
        return new(null, $"world-{expected.ToLowerInvariant()}-ocr=未确认完整{expected}文字");
    }
}
