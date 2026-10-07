using System.Runtime.InteropServices;
using System.IO;
using AzurAssistant.Contracts;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace AzurAssistant.Vision;

/// <summary>A similarity transform from client pixels to immutable reference image pixels.</summary>
public sealed record MapImageMatch(double A, double B, double Tx, double Ty, int Inliers, int Matches,
    double MedianError, double Coverage)
{
    public double Scale => Math.Sqrt(A * A + B * B);
    public Point2d Project(double x, double y) => new(A * x - B * y + Tx, B * x + A * y + Ty);
    public Point2d Unproject(double x, double y)
    {
        var divisor = A * A + B * B;
        return new((A * (x - Tx) + B * (y - Ty)) / divisor, (-B * (x - Tx) + A * (y - Ty)) / divisor);
    }
}

/// <summary>Cached reference SIFT features, ratio filtering and RANSAC reject UI overlays and unrelated images.</summary>
public sealed class MapImageMatcher : IDisposable
{
    private readonly SIFT _sift = SIFT.Create(12000, 3, .02);
    private readonly Mat _descriptors = new();
    private readonly KeyPoint[] _keypoints;
    private readonly BFMatcher _matcher = new(NormTypes.L2);
    private readonly FlannBasedMatcher? _index;
    private readonly double _referenceFactor = 1;
    public MapImageMatcher(string imagePath, string? featurePath = null)
    {
        if (featurePath is not null)
        {
            using var reader = new BinaryReader(File.OpenRead(featurePath));
            if (new string(reader.ReadChars(4)) != "AZMF" || reader.ReadInt32() != 1)
                throw new InvalidDataException("地图特征索引版本无效。");
            var count = reader.ReadInt32(); _referenceFactor = reader.ReadDouble();
            if (count is < 100 or > 500000 || _referenceFactor is < 1 or > 4
                || reader.BaseStream.Length != 20L + count * (8L + 128 * 4)) throw new InvalidDataException("地图特征索引尺寸无效。");
            _keypoints = Enumerable.Range(0, count).Select(_ => new KeyPoint(reader.ReadSingle(), reader.ReadSingle(), 1)).ToArray();
            _descriptors.Create(count, 128, MatType.CV_32FC1);
            var bytes = reader.ReadBytes(count * 128 * 4); Marshal.Copy(bytes, 0, _descriptors.Data, bytes.Length);
            _index = new(new OpenCvSharp.Flann.KDTreeIndexParams(4), new OpenCvSharp.Flann.SearchParams(96));
            _index.Add([_descriptors]); _index.Train();
            return;
        }
        using var image = Cv2.ImDecode(File.ReadAllBytes(imagePath), ImreadModes.Grayscale);
        if (image.Empty()) throw new InvalidDataException("地图底图无法读取。");
        _sift.DetectAndCompute(image, null, out _keypoints, _descriptors);
        if (_keypoints.Length < 100) throw new InvalidDataException("地图底图特征不足。");
    }

    public MapImageMatch? Match(FrameSnapshot frame, PixelRegion region, double resize, bool circular,
        int minimumInliers, CancellationToken token, ClientPoint? excludedCenter = null, int excludedRadius = 0,
        Point2d? nearReference = null, double referenceRadius = 650)
    {
        using var features = Extract(frame, region, resize, circular, token, excludedCenter, excludedRadius);
        return Match(features, minimumInliers, token, nearReference, referenceRadius);
    }

    public static MapQueryFeatures Extract(FrameSnapshot frame, PixelRegion region, double resize, bool circular,
        CancellationToken token, ClientPoint? excludedCenter = null, int excludedRadius = 0)
    {
        token.ThrowIfCancellationRequested();
        region.Validate(frame.Width, frame.Height);
        using var image = ReadRegion(frame, region);
        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGRA2GRAY);
        using var query = new Mat();
        Cv2.Resize(gray, query, new Size(), resize, resize, InterpolationFlags.Area);
        using var mask = new Mat(query.Size(), MatType.CV_8UC1, Scalar.All(255));
        if (circular)
        {
            mask.SetTo(Scalar.All(0));
            var center = new Point(query.Width / 2, query.Height / 2 - 1);
            Cv2.Circle(mask, center, (int)(67 * resize), Scalar.All(255), -1);
            Cv2.Circle(mask, center, (int)(14 * resize), Scalar.All(0), -1);
        }
        if (excludedCenter is { } excluded && excludedRadius > 0)
            Cv2.Circle(mask, new Point((int)((excluded.X - region.X) * resize), (int)((excluded.Y - region.Y) * resize)),
                (int)Math.Ceiling(excludedRadius * resize), Scalar.All(0), -1);
        var descriptors = new Mat();
        try
        {
            using var sift = SIFT.Create(12000, 3, .02);
            sift.DetectAndCompute(query, mask, out var points, descriptors);
            token.ThrowIfCancellationRequested();
            return new(region, resize, circular, query.Width, query.Height, points, descriptors);
        }
        catch { descriptors.Dispose(); throw; }
    }

    public MapImageMatch? Match(MapQueryFeatures query, int minimumInliers, CancellationToken token,
        Point2d? nearReference = null, double referenceRadius = 650)
    {
        token.ThrowIfCancellationRequested();
        var descriptors = query.Descriptors;
        var points = query.Points;
        if (descriptors.Rows < minimumInliers) return null;
        var indexes = nearReference is { } near ? Enumerable.Range(0, _keypoints.Length)
            .Where(i => Math.Abs(_keypoints[i].Pt.X * _referenceFactor - near.X) < referenceRadius
                && Math.Abs(_keypoints[i].Pt.Y * _referenceFactor - near.Y) < referenceRadius).ToArray() : null;
        using var local = new Mat();
        DMatch[][] pairs;
        if (indexes is not null)
        {
            if (indexes.Length < minimumInliers) return null;
            local.Create(indexes.Length, 128, MatType.CV_32FC1);
            unsafe
            {
                for (var i = 0; i < indexes.Length; i++)
                    new ReadOnlySpan<byte>((void*)_descriptors.Ptr(indexes[i]), 512).CopyTo(new Span<byte>((void*)local.Ptr(i), 512));
            }
            pairs = _matcher.KnnMatch(descriptors, local, 2);
        }
        else pairs = _index is null ? _matcher.KnnMatch(descriptors, _descriptors, 2) : _index.KnnMatch(descriptors, 2);
        var matches = pairs
            .Where(pair => pair.Length == 2 && pair[0].Distance < .72 * pair[1].Distance)
            .Select(pair => pair[0]).GroupBy(pair => pair.TrainIdx).Select(group => group.MinBy(pair => pair.Distance)).ToArray();
        if (matches.Length < minimumInliers) return null;
        var source = matches.Select(m => points[m.QueryIdx].Pt).ToArray();
        var target = matches.Select(m => _keypoints[indexes is null ? m.TrainIdx : indexes[m.TrainIdx]].Pt).ToArray();
        using var input = InputArray.Create(source);
        using var reference = InputArray.Create(target);
        using var inliers = new Mat();
        using var affine = Cv2.EstimateAffinePartial2D(input, reference, inliers, RobustEstimationAlgorithms.RANSAC, 3);
        token.ThrowIfCancellationRequested();
        if (affine is null || affine.Empty()) return null;
        var a = affine.At<double>(0, 0); var b = affine.At<double>(1, 0);
        var tx = affine.At<double>(0, 2); var ty = affine.At<double>(1, 2);
        var accepted = Enumerable.Range(0, matches.Length).Where(i => inliers.At<byte>(i) != 0).ToArray();
        if (accepted.Length < minimumInliers || accepted.Length < matches.Length * .60) return null;
        var spanX = accepted.Max(i => source[i].X) - accepted.Min(i => source[i].X);
        var spanY = accepted.Max(i => source[i].Y) - accepted.Min(i => source[i].Y);
        var coverage = Math.Min(spanX / query.Width, spanY / query.Height);
        if (coverage < (query.Circular ? .28 : .35)) return null;
        var cells = accepted.Select(i => (Math.Min(2, (int)(source[i].X * 3 / query.Width)),
            Math.Min(2, (int)(source[i].Y * 3 / query.Height)))).Distinct().Count();
        if (cells < 3) return null;
        var errors = accepted.Select(i => Math.Sqrt(Math.Pow(a * source[i].X - b * source[i].Y + tx - target[i].X, 2)
            + Math.Pow(b * source[i].X + a * source[i].Y + ty - target[i].Y, 2))).Order().ToArray();
        if (errors[errors.Length / 2] > 1.7) return null;
        // Resize uses pixel-center alignment; restore the transform to full client coordinates.
        var resize = query.Resize;
        var region = query.Region;
        var offset = (resize - 1) / 2;
        tx += a * (offset - resize * region.X) - b * (offset - resize * region.Y);
        ty += b * (offset - resize * region.X) + a * (offset - resize * region.Y);
        return new(a * resize * _referenceFactor, b * resize * _referenceFactor,
            tx * _referenceFactor + (_referenceFactor - 1) / 2, ty * _referenceFactor + (_referenceFactor - 1) / 2,
            accepted.Length, matches.Length, errors[errors.Length / 2] * _referenceFactor, coverage);
    }

    public static Mat ReadRegion(FrameSnapshot frame, PixelRegion region)
    {
        region.Validate(frame.Width, frame.Height);
        var pixels = new byte[checked(region.Width * region.Height * 4)];
        for (var row = 0; row < region.Height; row++)
            frame.Pixels.Span.Slice((region.Y + row) * frame.Stride + region.X * 4, region.Width * 4)
                .CopyTo(pixels.AsSpan(row * region.Width * 4));
        var image = new Mat(region.Height, region.Width, MatType.CV_8UC4);
        Marshal.Copy(pixels, 0, image.Data, pixels.Length);
        return image;
    }
    public void Dispose() { _index?.Dispose(); _matcher.Dispose(); _descriptors.Dispose(); _sift.Dispose(); }
}

/// <summary>One frame's feature extraction shared across reference maps and nearby/global searches.</summary>
public sealed class MapQueryFeatures(PixelRegion region, double resize, bool circular, int width, int height,
    KeyPoint[] points, Mat descriptors) : IDisposable
{
    internal PixelRegion Region { get; } = region;
    internal double Resize { get; } = resize;
    internal bool Circular { get; } = circular;
    internal int Width { get; } = width;
    internal int Height { get; } = height;
    internal KeyPoint[] Points { get; } = points;
    internal Mat Descriptors { get; } = descriptors;
    public void Dispose() => Descriptors.Dispose();
}
