using System;

namespace AzurAssistant.Runtime;

/// <summary>A coloured note found in one lane at the judge line.</summary>
public readonly record struct RhythmHit(int Lane, string Color, int Pixels, double CenterX);

/// <summary>
/// Independent note detector for the petal rhythm mini-game: it looks at a horizontal band around the judge line and
/// counts saturated pixels per lane, bucketed into red / blue / purple by hue. Nothing is copied from another
/// project; the geometry and thresholds are the ones exposed on the mini-game page.
/// </summary>
public static class RhythmAnalyzer
{
    /// <summary>Notes are found when a lane has at least this many coloured pixels inside the band.</summary>
    public const int SampleStep = 2;

    /// <summary>
    /// Analyses one BGRA frame (any size; coordinates are scaled to 1920×1080) and returns the lanes that currently
    /// hold a note.
    /// </summary>
    public static IReadOnlyList<RhythmHit> Detect(ReadOnlySpan<byte> bgra, int width, int height, MinigameConfig config)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return [];
        var scaleX = width / 1920.0;
        var scaleY = height / 1080.0;
        var bandTop = (int)Math.Clamp((config.JudgeY - config.BandHeight / 2) * scaleY, 0, height - 1);
        var bandBottom = (int)Math.Clamp((config.JudgeY + config.BandHeight / 2) * scaleY, bandTop + 1, height);
        var laneWidth = config.LaneWidth;
        var counts = new int[Math.Max(1, config.LaneCount)];
        var sums = new double[counts.Length];
        var colors = new int[counts.Length, 3];

        for (var y = bandTop; y < bandBottom; y += SampleStep)
        {
            var row = y * width * 4;
            for (var x = (int)Math.Clamp(config.LaneLeft * scaleX, 0, width - 1);
                 x < (int)Math.Clamp(config.LaneRight * scaleX, 1, width); x += SampleStep)
            {
                var index = row + x * 4;
                var b = bgra[index];
                var g = bgra[index + 1];
                var r = bgra[index + 2];
                if (!TryClassify(r, g, b, out var color)) continue;
                var lane = (int)((x / scaleX - config.LaneLeft) / Math.Max(1, laneWidth));
                if (lane < 0 || lane >= counts.Length) continue;
                counts[lane]++;
                sums[lane] += x / scaleX;
                colors[lane, color]++;
            }
        }

        var hits = new List<RhythmHit>();
        for (var lane = 0; lane < counts.Length; lane++)
        {
            if (counts[lane] < config.MinPixels) continue;
            var color = "?";
            var best = 0;
            for (var candidate = 0; candidate < 3; candidate++)
            {
                if (colors[lane, candidate] <= best) continue;
                best = colors[lane, candidate];
                color = candidate switch { 0 => "red", 1 => "blue", _ => "purple" };
            }
            hits.Add(new RhythmHit(lane, color, counts[lane], sums[lane] / counts[lane]));
        }
        return hits;
    }

    /// <summary>Classifies a pixel into red / blue / purple using saturation and value gates.</summary>
    public static bool TryClassify(byte r, byte g, byte b, out int color)
    {
        color = 0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max < 90) return false;              // too dark (background / shadow)
        var chroma = max - min;
        if (chroma < 60) return false;           // too grey (white text, UI)
        double hue;
        if (max == r) hue = 60.0 * ((g - b) / (double)chroma);
        else if (max == g) hue = 60.0 * (2 + (b - r) / (double)chroma);
        else hue = 60.0 * (4 + (r - g) / (double)chroma);
        if (hue < 0) hue += 360;
        if (hue >= 330 || hue < 25) { color = 0; return true; }   // red
        if (hue >= 190 && hue < 260) { color = 1; return true; }  // blue
        if (hue >= 260 && hue < 330) { color = 2; return true; }  // purple
        return false;
    }

    /// <summary>Builds a small diagnostic overlay: which lanes are hot right now.</summary>
    public static string Describe(IReadOnlyList<RhythmHit> hits)
    {
        if (hits.Count == 0) return "判定线上没有检测到音符。";
        var parts = new List<string>();
        foreach (var hit in hits)
            parts.Add($"轨道 {hit.Lane + 1}（{hit.Color}，x≈{hit.CenterX:0}）");
        return "检测到：" + string.Join("、", parts);
    }
}
