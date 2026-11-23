using System.IO;
// 祖赞卡之梯识别：移植自 OK-AzurPromilia 的 src/image/jenga_detector.py（AGPL-3.0）。
// 原实现：https://github.com/longer-sausage/OK-AzurPromilia —— 本项目同样以 AGPL-3.0 发布，见 LICENSE。
// 移植范围：资源加载与模板变体、灰度高通细节、画面归一化、HUD 激活判定、鸟检测、金字塔模板匹配定位悬挂件。
// 未移植：原实现里的手持/塔顶跟踪与身份投票状态机（依赖任务层时序），承接面位置改由亮度重心给出。

using OpenCvSharp;

namespace AzurAssistant.Runtime;

/// <summary>一个部件在 1920×1080 坐标里的位置。</summary>
public sealed record JengaPiece(double X, double Top, double Bottom, double Width, double Confidence = 1.0,
    string Kind = "");

/// <summary>当前观测：悬挂件（鸟下方匹配到的部件）、承接面位置、以及鸟本身。</summary>
public sealed record JengaObservation(JengaPiece? Carried, double TowerX, JengaPiece? Bird, string Note);

/// <summary>
/// Port of the reference jenga detector's visual part: high-pass detail, template pyramid matching for the piece
/// hanging under the bird, plus the HUD gate. Everything is reported in 1920×1080 coordinates.
/// </summary>
public sealed class JengaDetector
{
    /// <summary>Half-scale templates are compared at half resolution, like the original.</summary>
    private const double TemplateScale = 0.5;
    private const int PyramidFactor = 4;

    private readonly List<(Mat Detail, string Kind)> _templates = [];
    private readonly List<Mat> _birds = [];
    private Mat? _controls;
    private bool _loaded;

    public int TemplateCount => _templates.Count;
    public int BirdCount => _birds.Count;
    public string? LoadError { get; private set; }

    public static string AssetDirectory => Path.Combine(AppContext.BaseDirectory, "assets", "jenga");

    /// <summary>Loads and prepares the reference template pack (27 PNGs ship with the app).</summary>
    public void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!Directory.Exists(AssetDirectory)) { LoadError = "找不到 assets\\jenga 资源目录。"; return; }
            foreach (var path in Directory.GetFiles(AssetDirectory, "*.png").OrderBy(name => name, StringComparer.Ordinal))
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                using var gray = Cv2.ImRead(path, ImreadModes.Grayscale);
                if (gray.Empty()) continue;
                if (stem.StartsWith("bird", StringComparison.Ordinal))
                {
                    var small = new Mat();
                    Cv2.Resize(gray, small, new Size(0, 0), TemplateScale, TemplateScale);
                    _birds.Add(Detail(small));
                    continue;
                }
                if (stem == "controls") { _controls = gray.Clone(); continue; }
                if (stem == "score") continue;
                var kind = stem.StartsWith("rabbit", StringComparison.Ordinal) ? "rabbit"
                    : stem.StartsWith("owl", StringComparison.Ordinal) ? "owl"
                    : stem.StartsWith("stone", StringComparison.Ordinal) ? "monkey" : "";
                foreach (var variant in Variants(gray)) _templates.Add((variant, kind));
            }
            if (_templates.Count == 0) LoadError = "jenga 模板为空。";
        }
        catch (Exception ex) when (ex is IOException or OpenCVException or UnauthorizedAccessException)
        {
            LoadError = "加载 jenga 资源失败：" + ex.Message;
        }
    }

    /// <summary>High-pass detail: removes day/night lighting and blurry backgrounds, clamps the moving sparkles.</summary>
    public static Mat Detail(Mat gray)
    {
        using var blur = new Mat();
        Cv2.GaussianBlur(gray, blur, new Size(0, 0), 3);
        using var diff = new Mat();
        Cv2.Subtract(gray, blur, diff, null, MatType.CV_32F);
        using var clamped = new Mat();
        Cv2.Min(diff, new Scalar(15), clamped);
        Cv2.Max(clamped, new Scalar(-15), clamped);
        var result = new Mat();
        clamped.ConvertTo(result, MatType.CV_32F);
        return result;
    }

    /// <summary>0.9/1.0/1.1 scale × -8/0/8 degrees, each at half size — the original's variant set.</summary>
    private static IEnumerable<Mat> Variants(Mat gray)
    {
        foreach (var scale in new[] { 0.9, 1.0, 1.1 })
        {
            using var small = new Mat();
            Cv2.Resize(gray, small, new Size(0, 0), TemplateScale * scale, TemplateScale * scale);
            foreach (var angle in new[] { -8.0, 0.0, 8.0 })
            {
                using var rotation = Cv2.GetRotationMatrix2D(new Point2f(small.Width / 2f, small.Height / 2f), angle, 1);
                var rotated = new Mat();
                Cv2.WarpAffine(small, rotated, rotation, new Size(small.Width, small.Height),
                    InterpolationFlags.Linear, BorderTypes.Reflect101);
                yield return Detail(rotated);
                rotated.Dispose();
            }
        }
    }

    /// <summary>Normalises any 16:9 capture to exactly 1920×1080; returns null for other aspect ratios.</summary>
    public static Mat? Normalize(byte[] bgra, int width, int height)
    {
        if (width < 120 || height < 120 || bgra.Length < width * height * 4) return null;
        if (Math.Abs(width / (double)height - 16 / 9.0) > 0.04) return null;
        using var source = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra);
        using var bgr = new Mat();
        Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        if (width == 1920 && height == 1080) return bgr.Clone();
        var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(1920, 1080));
        return resized;
    }

    /// <summary>HUD gate: the two fixed glyphs plus the gold/white star are present only in the mini-game.</summary>
    public bool IsActive(Mat frame)
    {
        if (_controls is null || frame.Width != 1920 || frame.Height != 1080) return false;
        using var roi = new Mat(frame, new Rect(826, 48, 94, 44));
        using var gray = new Mat();
        Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        using var scores = new Mat();
        Cv2.MatchTemplate(gray, _controls, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out double _, out double confidence, out Point _, out Point _);
        if (confidence < 0.75) return false;

        using var star = new Mat(frame, new Rect(1550, 932, 90, 80));
        using var hsv = new Mat();
        Cv2.CvtColor(star, hsv, ColorConversionCodes.BGR2HSV);
        using var gold = new Mat();
        Cv2.InRange(hsv, new Scalar(15, 100, 180), new Scalar(40, 255, 255), gold);
        using var white = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 0, 220), new Scalar(179, 80, 255), white);
        return Cv2.Mean(gold).Val0 / 255.0 > 0.20 && Cv2.Mean(white).Val0 / 255.0 > 0.025;
    }

    /// <summary>Birds sit in a yellow box; their presence means a piece is hanging and can be released.</summary>
    public IReadOnlyList<JengaPiece> FindBirds(Mat frame)
    {
        if (frame.Width != 1920 || frame.Height != 1080) return [];
        using var roi = new Mat(frame, new Rect(520, 0, 920, 450));
        using var hsv = new Mat();
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);
        foreach (var (saturation, minimumArea, minimumHeight) in new[]
                 { (80, 750, 35), (60, 750, 35), (60, 300, 20) })
        {
            using var yellow = new Mat();
            Cv2.InRange(hsv, new Scalar(15, saturation, 140), new Scalar(42, 255, 255), yellow);
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            var count = Cv2.ConnectedComponentsWithStats(yellow, labels, stats, centroids);
            var birds = new List<JengaPiece>();
            for (var index = 1; index < count; index++)
            {
                int X(int column) => stats.At<int>(index, column);
                var (x, y, w, h, area) = (X(0), X(1), X(2), X(3), X(4));
                if (area <= minimumArea || area >= 1600) continue;
                if (w is < 27 or > 45 || h < minimumHeight || h > 67 || y + h >= 420) continue;
                if (x < 8 || x + w + 8 > roi.Width) continue;
                var top = y + h / 2;
                var bottom = Math.Min(roi.Height, y + h + 30);
                if (bottom - top < 2) continue;
                using var patch = new Mat(hsv, new Rect(x - 8, top, w + 16, bottom - top));
                using var white = new Mat();
                Cv2.InRange(patch, new Scalar(0, 0, 155), new Scalar(179, 125, 255), white);
                if (Cv2.CountNonZero(white) <= 250) continue;
                birds.Add(new JengaPiece(520 + x + w / 2.0, y, y + h + 12, w));
            }
            if (birds.Count > 0) return birds;
        }
        return [];
    }

    /// <summary>
    /// Finds the piece hanging under a bird by matching the template pack in the window below it, then reports the
    /// tower column from the bright-pixel centroid (the reference tracks it with its own state machine instead).
    /// </summary>
    public JengaObservation Detect(Mat frame, double towerTolerance = 26)
    {
        Load();
        if (LoadError is not null) return new JengaObservation(null, double.NaN, null, LoadError);
        if (!IsActive(frame)) return new JengaObservation(null, double.NaN, null, "不在小游戏界面（HUD 校验未通过）。");
        var birds = FindBirds(frame);
        if (birds.Count == 0) return new JengaObservation(null, double.NaN, null, "没有检测到鸟（悬挂件可能还没出现）。");

        var bird = birds[0];
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        using var full = Detail(gray);
        // 原实现在 4 倍金字塔上重捕、缓存后再细化；这里保持同样的 1/4 坐标。
        using var half = new Mat();
        Cv2.Resize(full, half, new Size(0, 0), 1.0 / PyramidFactor, 1.0 / PyramidFactor, InterpolationFlags.Area);
        var centreX = bird.X / PyramidFactor;
        var topY = bird.Bottom / PyramidFactor;
        var window = new Rect(
            (int)Math.Clamp(centreX - 90, 0, Math.Max(1, half.Width - 2)),
            (int)Math.Clamp(topY, 0, Math.Max(1, half.Height - 2)),
            (int)Math.Clamp(180, 1, Math.Max(1, half.Width - (int)centreX + 90)),
            (int)Math.Clamp(120, 1, Math.Max(1, half.Height - (int)topY)));
        if (window.Right > half.Width) window.Width = half.Width - window.X;
        if (window.Bottom > half.Height) window.Height = half.Height - window.Y;
        if (window.Width < 8 || window.Height < 8) return new JengaObservation(null, double.NaN, bird, "鸟下方区域太小。");

        using var roi = new Mat(half, window);
        JengaPiece? carried = null;
        foreach (var (template, kind) in _templates)
        {
            if (template.Width >= roi.Width || template.Height >= roi.Height) continue;
            using var scores = new Mat();
            Cv2.MatchTemplate(roi, template, scores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(scores, out double _, out double score, out Point _, out Point location);
            if (score < 0.62) continue;
            var width = template.Width * PyramidFactor;
            var height = template.Height * PyramidFactor;
            var x = (window.X + location.X) * PyramidFactor + width / 2.0;
            var y = (window.Y + location.Y) * PyramidFactor;
            if (carried is not null && carried.Confidence >= score) continue;
            carried = new JengaPiece(x, y, y + height, width, score, kind);
        }

        var towerX = TowerColumn(frame);
        var note = carried is null
            ? "鸟下方没有匹配到模板（换造型后可能需要采集新模板）。"
            : $"悬挂件 {carried.Kind} x≈{carried.X:0}（置信 {carried.Confidence:0.00}）";
        return new JengaObservation(carried, towerX, bird, note);
    }

    /// <summary>Horizontal centre of the bright pixels in the lower band — a stand-in for the tracked tower piece.</summary>
    public static double TowerColumn(Mat frame)
    {
        using var roi = new Mat(frame, new Rect(0, Math.Max(0, 700), frame.Width, Math.Max(1, Math.Min(120, frame.Height - 700))));
        using var hsv = new Mat();
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);
        using var bright = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 0, 120), new Scalar(179, 255, 255), bright);
        using var points = new Mat();
        Cv2.FindNonZero(bright, points);
        if (points.Empty() || points.Rows < 60) return double.NaN;
        var moments = Cv2.Moments(bright, true);
        return moments.M00 <= 0 ? double.NaN : moments.M10 / moments.M00;
    }

    public static string Describe(JengaObservation observation)
    {
        if (observation.Carried is not { } piece) return "参考识别：" + observation.Note;
        var offset = double.IsNaN(observation.TowerX) ? double.NaN : piece.X - observation.TowerX;
        var tail = double.IsNaN(offset) ? "承接面未知" : $"偏差 {offset:+0;-0;0}";
        return $"参考识别：{observation.Note}；承接面 x≈{observation.TowerX:0}；{tail}";
    }
}
