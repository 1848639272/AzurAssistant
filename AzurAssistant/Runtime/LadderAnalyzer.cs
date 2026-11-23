using System;

namespace AzurAssistant.Runtime;

/// <summary>What the ladder detector currently sees: where the hanging piece is and how far it is from the target.</summary>
public readonly record struct LadderObservation(double PieceX, double TowerX, double Offset, bool Aligned);

/// <summary>
/// Independent detector for the stacking mini-game: the hanging piece is found in the upper band (brightest saturated
/// column cluster) and the tower top in a band around the configured height; the piece is "aligned" when its centre is
/// within the tolerance of the target column. No code copied from elsewhere — the bands are parameters.
/// </summary>
public static class LadderAnalyzer
{
    public const int SampleStep = 2;

    public static LadderObservation Detect(ReadOnlySpan<byte> bgra, int width, int height, MinigameConfig config)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return new LadderObservation(0, 0, double.NaN, false);
        var scaleX = width / 1920.0;
        var scaleY = height / 1080.0;

        var piece = ColumnCluster(bgra, width, height, scaleX, scaleY,
            config.LadderTop, config.LadderTop + config.LadderTopHeight);
        var tower = ColumnCluster(bgra, width, height, scaleX, scaleY,
            config.LadderTowerY - 40, config.LadderTowerY + 40);
        var target = config.LadderTargetX;
        var reference = tower ?? target;
        var offset = piece is { } pieceX ? pieceX - reference : double.NaN;
        var aligned = piece is not null && !double.IsNaN(offset) && Math.Abs(offset) <= config.LadderTolerance;
        return new LadderObservation(piece ?? double.NaN, reference, offset, aligned);
    }

    /// <summary>Horizontal centre of the coloured pixels inside a reference-space band, or null when nothing is found.</summary>
    private static double? ColumnCluster(ReadOnlySpan<byte> bgra, int width, int height, double scaleX, double scaleY,
        double top, double bottom)
    {
        var from = (int)Math.Clamp(top * scaleY, 0, height - 1);
        var to = (int)Math.Clamp(bottom * scaleY, from + 1, height);
        double sum = 0;
        var count = 0;
        for (var y = from; y < to; y += SampleStep)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x += SampleStep)
            {
                var index = row + x * 4;
                var b = bgra[index];
                var g = bgra[index + 1];
                var r = bgra[index + 2];
                // Contrast gate: the piece and tower are brighter/more colourful than the sky behind them.
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                // Bright shapes (the hanging piece / the stack) or strongly coloured ones count; the flat background does not.
                if (max < 120 || (max - min < 40 && max < 200)) continue;
                sum += x / scaleX;
                count++;
            }
        }
        if (count < 60) return null;
        return sum / count;
    }

    public static string Describe(LadderObservation observation)
    {
        if (double.IsNaN(observation.PieceX)) return "悬挂件区域没有找到目标。";
        var direction = observation.Offset > 0 ? "偏右" : observation.Offset < 0 ? "偏左" : "正中";
        return $"悬挂件 x≈{observation.PieceX:0}　承接面 x≈{observation.TowerX:0}　偏差 {observation.Offset:+0;-0;0}（{direction}）"
               + (observation.Aligned ? "　可以松手" : "");
    }
}
