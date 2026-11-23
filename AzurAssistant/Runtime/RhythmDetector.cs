// 花瓣音游识别：移植自 OK-AzurPromilia 的 src/image/rhythm_detector.py（AGPL-3.0）。
// 原实现：https://github.com/longer-sausage/OK-AzurPromilia —— 本项目同样以 AGPL-3.0 发布，见 LICENSE。
// 移植范围：布局校准（图例优先 + 搜索回退）、颜色掩膜、圆形/长条音符检测、轨道带。
// 尚未移植：_covered_body / _colored_hold / _tail_cap / _glyph_heads 这些处理命中特效遮挡的细节。

using OpenCvSharp;

namespace AzurAssistant.Runtime;

/// <summary>One note found on the track: head/tail are reference-space x, y is the note centre height.</summary>
public sealed record RhythmNote(string Color, double Head, double Tail, double Y, bool Long, bool Clipped = false)
{
    /// <summary>Keys this note needs (game mapping: red = E, blue = Q, purple = both).</summary>
    public IReadOnlyList<string> Keys => Color switch
    {
        "blue" => ["q"],
        "red" => ["e"],
        _ => ["q", "e"],
    };
}

/// <summary>The UI layout: uniform scale plus the offsets of the 1920×1080 reference frame inside the capture.</summary>
public sealed record RhythmLayout(double Scale, double XOffset, double YOffset, IReadOnlyList<(double X, double Y)> Legend)
{
    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;

    public static RhythmLayout ForFrame(int width, int height)
    {
        var scale = Math.Min(width / (double)ReferenceWidth, height / (double)ReferenceHeight);
        return new RhythmLayout(scale, (width - ReferenceWidth * scale) / 2, (height - ReferenceHeight * scale) / 2, []);
    }

    public RhythmLayout WithLegend(int width)
    {
        var centres = new List<(double, double)>();
        foreach (var y in RhythmDetector.LegendY) centres.Add((width - 60 * Scale, YOffset + y * Scale));
        return this with { Legend = centres };
    }

    /// <summary>Copies a reference-space ROI out of a BGRA frame, undoing the layout transform.</summary>
    public Mat Crop(byte[] bgra, int width, int height, int x1, int y1, int x2, int y2)
    {
        using var source = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra);
        using var bgr = new Mat();
        Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        var transform = new double[,]
        {
            { 1 / Scale, 0, -XOffset / Scale - x1 },
            { 0, 1 / Scale, -YOffset / Scale - y1 },
        };
        using var matrix = Mat.FromArray(transform);
        var roi = new Mat();
        Cv2.WarpAffine(bgr, roi, matrix, new Size(x2 - x1, y2 - y1));
        return roi;
    }
}

/// <summary>
/// Port of the reference rhythm detector: it calibrates the UI from the three colour legend icons on the right, then
/// looks for note heads and hold tails along the track band and reports them in reference space.
/// </summary>
public sealed class RhythmDetector
{
    /// <summary>HSV ranges from the reference implementation (OpenCV hue 0–179).</summary>
    public static readonly IReadOnlyDictionary<string, (Scalar Lower, Scalar Upper)> Colors =
        new Dictionary<string, (Scalar, Scalar)>
        {
            ["blue"] = (new Scalar(85, 45, 150), new Scalar(112, 220, 255)),
            ["red"] = (new Scalar(155, 45, 150), new Scalar(179, 220, 255)),
            ["purple"] = (new Scalar(113, 45, 150), new Scalar(145, 220, 255)),
        };

    public const double JudgeX = 587.0;
    public static readonly int[] LegendY = [678, 726, 774];

    private RhythmLayout? _layout;

    public bool IsActive(byte[] bgra, int width, int height)
    {
        if (width < 120 || height < 120 || bgra.Length < width * height * 4) { _layout = null; return false; }
        if (_layout is not null && LegendVisible(bgra, width, height, _layout)) return true;
        _layout = FindLayout(bgra, width, height);
        return _layout is not null;
    }

    /// <summary>Legend icons sit at a fixed spot on the right; they confirm the calibration and the scale.</summary>
    private static bool LegendVisible(byte[] bgra, int width, int height, RhythmLayout layout)
    {
        if (layout.Legend.Count < 3) return false;
        var radius = Math.Max(2, (int)Math.Round(14 * layout.Scale));
        using var source = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra);
        using var bgr = new Mat();
        Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        foreach (var color in new[] { "blue", "red", "purple" })
        {
            var (x, y) = ((int)Math.Round(layout.Legend[0].X), (int)Math.Round(layout.Legend[0].Y));
            var index = color switch { "blue" => 0, "red" => 1, _ => 2 };
            x = (int)Math.Round(layout.Legend[index].X);
            y = (int)Math.Round(layout.Legend[index].Y);
            if (radius <= x && x < width - radius && radius <= y && y < height - radius)
            {
                var patch = new Mat(bgr, new Rect(x - radius, y - radius, radius * 2, radius * 2)).Clone();
                using (patch)
                using (var small = new Mat())
                {
                    Cv2.Resize(patch, small, new Size(28, 28));
                    if (LegendPatchMatches(small, color)) continue;
                }
            }
            return false;
        }
        return true;
    }

    private static bool LegendPatchMatches(Mat smallBgr, string color)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(smallBgr, hsv, ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        var (lower, upper) = Colors[color];
        Cv2.InRange(hsv, lower, upper, mask);
        using var white = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 0, 225), new Scalar(179, 45, 255), white);
        var coloured = Cv2.Mean(mask).Val0 / 255.0;
        var whiteRatio = Cv2.Mean(white).Val0 / 255.0;
        return coloured >= 0.30 && whiteRatio >= 0.025;
    }

    /// <summary>Falls back to searching the right-hand strip for the three legend icons.</summary>
    private RhythmLayout? FindLayout(byte[] bgra, int width, int height)
    {
        var baseLayout = RhythmLayout.ForFrame(width, height).WithLegend(width);
        if (LegendVisible(bgra, width, height, baseLayout)) return baseLayout;

        var x0 = (int)Math.Round(width * 0.65);
        var y0 = (int)Math.Round(height * 0.35);
        var y1 = (int)Math.Round(height * 0.88);
        if (x0 >= width - 10 || y1 <= y0) return null;

        using var source = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra);
        using var bgr = new Mat();
        Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        using var strip = new Mat(bgr, new Rect(x0, y0, width - x0, y1 - y0)).Clone();
        var resizeScale = Math.Min(1.0, 1080.0 / height);
        using var scaled = new Mat();
        Cv2.Resize(strip, scaled, new Size(0, 0), resizeScale, resizeScale);
        using var hsv = new Mat();
        Cv2.CvtColor(scaled, hsv, ColorConversionCodes.BGR2HSV);

        var candidates = new Dictionary<string, List<(double X, double Y, double Radius)>>();
        foreach (var color in Colors.Keys)
        {
            using var mask = new Mat();
            Cv2.InRange(hsv, Colors[color].Lower, Colors[color].Upper, mask);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
            Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            var list = new List<(double, double, double)>();
            foreach (var contour in contours)
            {
                var rect = Cv2.BoundingRect(contour);
                var area = Cv2.ContourArea(contour);
                if (rect.Width is < 5 or > 48 || rect.Height is < 5 or > 48) continue;
                if (rect.Width / (double)rect.Height is < 0.65 or > 1.5) continue;
                if (area / (rect.Width * (double)rect.Height) < 0.35) continue;
                using var white = new Mat();
                using var patch = new Mat(hsv, rect);
                Cv2.InRange(patch, new Scalar(0, 0, 225), new Scalar(179, 45, 255), white);
                if (Cv2.Mean(white).Val0 / 255.0 < 0.025) continue;
                Cv2.MinEnclosingCircle(contour, out var centre, out var radius);
                list.Add((centre.X, centre.Y, radius));
            }
            candidates[color] = list;
        }

        RhythmLayout? best = null;
        var bestError = double.MaxValue;
        var names = Colors.Keys.ToArray();
        for (var first = 0; first < names.Length; first++)
        for (var second = first + 1; second < names.Length; second++)
        {
            foreach (var a in candidates[names[first]])
            foreach (var b in candidates[names[second]])
            {
                var radius = (a.Radius + b.Radius) / 2;
                var gap = (b.Y - a.Y) / (second - first);
                if (!(radius * 2 < gap && gap < radius * 3.8)) continue;
                if (Math.Abs(a.X - b.X) > radius * 0.4 || Math.Abs(a.Radius - b.Radius) > radius * 0.3) continue;
                var scale = gap / resizeScale / 48;
                var cx = (a.X + b.X) / 2 / resizeScale + x0;
                var yOffset = a.Y / resizeScale + y0 - LegendY[first] * scale;
                var layout = new RhythmLayout(scale, (width - 1920 * scale) / 2, yOffset, [])
                    .WithLegend(width);
                if (!LegendVisible(bgra, width, height, layout)) continue;
                var error = Math.Abs(a.X - b.X) / radius + Math.Abs(a.Radius - b.Radius) / radius;
                if (error >= bestError) continue;
                bestError = error;
                best = layout;
            }
        }
        return best;
    }

    /// <summary>Detects notes in the track band; returns them sorted by head position in reference space.</summary>
    public IReadOnlyList<RhythmNote> Detect(byte[] bgra, int width, int height)
    {
        if (width < 120 || height < 120 || bgra.Length < width * height * 4) return [];
        _layout ??= FindLayout(bgra, width, height);
        var layout = _layout ?? RhythmLayout.ForFrame(width, height);
        using var roi = layout.Crop(bgra, width, height, 515, 475, 1900, 600);
        using var hsv = new Mat();
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);
        var height2 = roi.Height;

        var notes = new List<RhythmNote>();
        foreach (var (color, bounds) in Colors)
        {
            using var mask = new Mat();
            Cv2.InRange(hsv, bounds.Lower, bounds.Upper, mask);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
            var stats = Components(mask, out _);

            using var bright = new Mat();
            Cv2.InRange(hsv, new Scalar(bounds.Lower.Val0, bounds.Lower.Val1, 240), bounds.Upper, bright);
            Cv2.MorphologyEx(bright, bright, MorphTypes.Close, kernel);
            var brightStats = Components(bright, out _);
            var circles = new List<Rect>();
            foreach (var (rect, area) in brightStats)
            {
                if (rect.Width is < 50 or > 80 || rect.Height is < 45 or > 85) continue;
                if (rect.Width / (double)rect.Height is < 0.8 or > 1.25) continue;
                if (area < 1200) continue;
                circles.Add(rect);
            }

            foreach (var (rect, area) in stats)
            {
                var isLong = rect.Width > 100;
                var maxHeight = isLong ? 105 : 85;
                if (rect.Height < 45 || rect.Height > maxHeight || rect.Width < 43 || area < 1200) continue;
                if (rect.Y <= 0) continue;
                var density = area / (rect.Width * (double)rect.Height);
                if (rect.Y + rect.Height >= height2)
                {
                    if (!isLong || rect.Y > 55 || density < 0.50) continue;
                }
                else if (density < 0.38) continue;

                var radius = isLong ? 33.5 : rect.Width / 2.0;
                var head = 515 + rect.X + radius;
                var tail = 515 + rect.X + rect.Width - radius;
                if (isLong)
                {
                    var following = circles
                        .Where(circle => circle.X - rect.X > 30 && circle.X + circle.Width <= rect.X + rect.Width + 2
                                         && Math.Abs(circle.Y - rect.Y) < 15)
                        .OrderBy(circle => circle.X)
                        .ToList();
                    foreach (var circle in following)
                    {
                        var centre = 515 + circle.X + circle.Width / 2.0;
                        notes.Add(new RhythmNote(color, centre, centre, 475 + circle.Y + circle.Height / 2.0, false));
                    }
                    if (following.Count > 0) tail = 515 + following[0].X - 10.5;
                }
                if (head > 1500) continue;
                var clipped = rect.X + rect.Width >= roi.Width - 2;
                notes.Add(new RhythmNote(color, head, tail, 475 + rect.Y + rect.Height / 2.0, isLong, clipped));
            }
        }

        // 同一位置可能被长条与圆形重复报告，保留头部更靠前的那个。
        return notes
            .Where(note => note.Head <= 1500)
            .OrderBy(note => note.Head)
            .ToList();
    }

    /// <summary>Connected components with their bounding boxes and areas (first entry is the background).</summary>
    private static List<(Rect Rect, int Area)> Components(Mat mask, out int count)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        var list = new List<(Rect, int)>();
        for (var index = 1; index < count; index++)
        {
            var rect = new Rect(stats.At<int>(index, 0), stats.At<int>(index, 1),
                stats.At<int>(index, 2), stats.At<int>(index, 3));
            list.Add((rect, stats.At<int>(index, 4)));
        }
        return list;
    }

    /// <summary>Compact description used by the mini-game page.</summary>
    public static string Describe(IReadOnlyList<RhythmNote> notes)
    {
        if (notes.Count == 0) return "参考识别：轨道上没有音符。";
        var head = notes[0];
        return $"参考识别：{notes.Count} 个音符，最近 {head.Color}（head x≈{head.Head:0}{(head.Long ? "，长条" : "")}）"
               + (head.Clipped ? "，尾部被截断" : "");
    }
}
