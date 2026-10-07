using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Features.HomeCenter;

public sealed record DiningSatiety(int Current, int Capacity);

public interface IDiningSatietyReader
{
    Task<DiningSatiety?> ReadAsync(FrameSnapshot frame, CancellationToken token);
}

public sealed class DiningSatietyReader : IDiningSatietyReader
{
    private readonly PpOcrReader _ocr;
    private readonly Settings _settings;
    private sealed record Settings(int SchemaVersion, int Width, int Height, PixelRegion SatietyRegion, int Scale, double MinimumConfidence);

    public DiningSatietyReader(PpOcrReader ocr, string? directory = null)
    {
        _ocr = ocr;
        _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(
            directory ?? Path.Combine(AppContext.BaseDirectory, "assets", "daily"), "dining.json")))
            ?? throw new InvalidDataException("餐桌识别配置为空。");
        if (_settings.SchemaVersion != 1 || _settings.Width != 1920 || _settings.Height != 1080
            || _settings.Scale is < 1 or > 3 || !double.IsFinite(_settings.MinimumConfidence)
            || _settings.MinimumConfidence is <= 0 or > 1)
            throw new InvalidDataException("餐桌识别配置版本或参数不受支持。");
        _settings.SatietyRegion.Validate(_settings.Width, _settings.Height);
    }

    public async Task<DiningSatiety?> ReadAsync(FrameSnapshot frame, CancellationToken token)
    {
        if (frame.Width != _settings.Width || frame.Height != _settings.Height)
            throw new InvalidOperationException("餐桌补充仅支持1920×1080客户区。");
        var text = await _ocr.ReadAsync(frame, token, _settings.SatietyRegion, _settings.Scale);
        var candidates = text.Where(item => item.Confidence >= _settings.MinimumConfidence)
            .Select(item => Regex.Replace(item.Text, @"\s+", "").Replace('／', '/'))
            .Select(value => Regex.Match(value, @"^(\d{1,6})/(\d{1,6})$"))
            .Where(match => match.Success).ToArray();
        if (candidates.Length != 1 || !int.TryParse(candidates[0].Groups[1].Value, out var current)
            || !int.TryParse(candidates[0].Groups[2].Value, out var capacity)
            || capacity <= 0 || current > capacity) return null;
        return new(current, capacity);
    }
}
