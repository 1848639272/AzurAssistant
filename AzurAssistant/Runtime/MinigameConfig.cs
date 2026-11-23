using System.Globalization;
using System.Text;

namespace AzurAssistant.Runtime;

/// <summary>
/// Tunables for the two mini-games. Stored as compact "key=value;key=value" strings so the settings file stays simple
/// and both parsers can be unit-tested.
/// </summary>
public sealed record MinigameConfig(
    int LaneCount, double LaneLeft, double LaneRight, double JudgeY, double BandHeight,
    int MinPixels, int CooldownMs, int[] Keys, double LadderTop, double LadderTopHeight,
    double LadderTowerY, double LadderTargetX, double LadderTolerance)
{
    public static MinigameConfig Default => new(
        LaneCount: 3, LaneLeft: 700, LaneRight: 1220, JudgeY: 600, BandHeight: 44,
        MinPixels: 40, CooldownMs: 140, Keys: [0x31, 0x32, 0x33],
        LadderTop: 180, LadderTopHeight: 160, LadderTowerY: 760, LadderTargetX: 960, LadderTolerance: 26);

    /// <summary>Parse "lanes=3;left=700;…"; unknown keys and bad values keep the defaults.</summary>
    public static MinigameConfig Parse(string? text, MinigameConfig? fallback = null)
    {
        var config = fallback ?? Default;
        if (string.IsNullOrWhiteSpace(text)) return config;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1) continue;
            values[part[..separator].Trim()] = part[(separator + 1)..].Trim();
        }
        double Number(string key, double current, double min, double max)
            => values.TryGetValue(key, out var raw)
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Clamp(parsed, min, max)
                : current;
        int Integer(string key, int current, int min, int max)
            => (int)Number(key, current, min, max);

        var keys = config.Keys;
        if (values.TryGetValue("keys", out var keyText))
        {
            var parsed = keyText.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vk) ? vk : 0)
                .Where(vk => vk is > 0 and <= 0xFF)
                .Take(8)
                .ToArray();
            if (parsed.Length > 0) keys = parsed;
        }

        return new MinigameConfig(
            LaneCount: Integer("lanes", config.LaneCount, 1, keys.Length),
            LaneLeft: Number("left", config.LaneLeft, 0, 1920),
            LaneRight: Number("right", config.LaneRight, 0, 1920),
            JudgeY: Number("judge", config.JudgeY, 0, 1080),
            BandHeight: Number("band", config.BandHeight, 8, 200),
            MinPixels: Integer("minpixels", config.MinPixels, 4, 5000),
            CooldownMs: Integer("cooldown", config.CooldownMs, 40, 2000),
            Keys: keys,
            LadderTop: Number("top", config.LadderTop, 0, 1080),
            LadderTopHeight: Number("toph", config.LadderTopHeight, 20, 600),
            LadderTowerY: Number("tower", config.LadderTowerY, 0, 1080),
            LadderTargetX: Number("target", config.LadderTargetX, 0, 1920),
            LadderTolerance: Number("tol", config.LadderTolerance, 4, 200));
    }

    public string Serialize()
    {
        var builder = new StringBuilder();
        builder.Append("lanes=").Append(LaneCount)
            .Append(";left=").Append(LaneLeft.ToString("0.##", CultureInfo.InvariantCulture))
            .Append(";right=").Append(LaneRight.ToString("0.##", CultureInfo.InvariantCulture))
            .Append(";judge=").Append(JudgeY.ToString("0.##", CultureInfo.InvariantCulture))
            .Append(";band=").Append(BandHeight.ToString("0.##", CultureInfo.InvariantCulture))
            .Append(";minpixels=").Append(MinPixels)
            .Append(";cooldown=").Append(CooldownMs)
            .Append(";keys=").Append(string.Join(",", Keys))
            .Append(";top=").Append(LadderTop.ToString("0.##", CultureInfo.InvariantCulture))
            .Append(";toph=").Append(LadderTopHeight.ToString("0.##", CultureInfo.InvariantCulture))
            .Append(";tower=").Append(LadderTowerY.ToString("0.##", CultureInfo.InvariantCulture))
            .Append(";target=").Append(LadderTargetX.ToString("0.##", CultureInfo.InvariantCulture))
            .Append(";tol=").Append(LadderTolerance.ToString("0.##", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    /// <summary>Horizontal centre of a lane (0-based) in reference pixels.</summary>
    public double LaneCenter(int lane)
    {
        var count = Math.Max(1, LaneCount);
        var step = (LaneRight - LaneLeft) / count;
        return LaneLeft + step * (Math.Clamp(lane, 0, count - 1) + 0.5);
    }

    public int LaneWidth => (int)Math.Max(1, (LaneRight - LaneLeft) / Math.Max(1, LaneCount));
}
