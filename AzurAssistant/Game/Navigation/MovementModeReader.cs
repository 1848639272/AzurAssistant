using System.IO;
using System.Text.Json;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Game.Navigation;

/// <summary>Reads only the status text, never the translucent toast background or the alternate-mode instruction.</summary>
public sealed class MovementModeReader
{
    private readonly PpOcrReader _ocr;
    private readonly Settings _settings;
    private sealed record Settings(int SchemaVersion, PixelRegion Region, int Scale, double MinimumConfidence);
    public MovementModeReader(PpOcrReader ocr, string directory)
    {
        _ocr = ocr;
        _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(directory, "movement-ocr.json")))
            ?? throw new InvalidDataException("移动状态OCR配置为空。");
        if (_settings.SchemaVersion != 1 || _settings.Scale is < 1 or > 3 || !double.IsFinite(_settings.MinimumConfidence) || _settings.MinimumConfidence is < .8 or > 1)
            throw new InvalidDataException("移动状态OCR配置不受支持。");
        _settings.Region.Validate(1920, 1080);
    }
    public async Task<MovementMode> ReadAsync(FrameSnapshot frame, CancellationToken token)
    {
        foreach (var preprocessing in new[] { OcrPreprocessing.Original, OcrPreprocessing.WhiteText })
        foreach (var scale in Enumerable.Range(_settings.Scale, 4 - _settings.Scale))
        {
            var text = await _ocr.ReadAsync(frame, token, _settings.Region, scale, preprocessing);
            var mode = ReadEvidence(text, _settings.MinimumConfidence);
            if (mode != MovementMode.Unknown) return mode;
        }
        return MovementMode.Unknown;
    }
    public static MovementMode ReadEvidence(IReadOnlyList<TextRegion> regions, double minimumConfidence)
    {
        var characters = regions.OrderBy(r => r.X).SelectMany(r => r.Text.Select((letter, i) =>
            (Letter: letter, Confidence: r.CharacterConfidences is { } scores && scores.Count == r.Text.Length ? scores[i] : r.Confidence)))
            .Where(c => !char.IsWhiteSpace(c.Letter)).ToArray();
        var mode = Parse(new string(characters.Select(c => c.Letter).ToArray()));
        // Both current-state clauses contain eight characters. Confidence of the alternate-mode instruction is irrelevant.
        return mode != MovementMode.Unknown && characters.Take(8).Average(c => c.Confidence) >= minimumConfidence
            ? mode : MovementMode.Unknown;
    }
    public static MovementMode Parse(string text)
    {
        text = text.Replace(" ", "").Trim();
        if (text.StartsWith("已切换至步行状态", StringComparison.Ordinal)) return MovementMode.Walk;
        if (text.StartsWith("已切换至奔跑状态", StringComparison.Ordinal)) return MovementMode.Run;
        return MovementMode.Unknown;
    }
}
