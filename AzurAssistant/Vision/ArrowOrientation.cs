using OpenCvSharp;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AzurAssistant.Vision;

/// <summary>Fits the whole arrow silhouette, including the tail notch. Image axes: east zero, south positive.</summary>
public static class ArrowOrientation
{
    private const int Size = 48;
    private const double Area = 220;
    private static readonly Lazy<Shape[]> Shapes = new(BuildShapes);
    private sealed record Shape(double Angle, ulong[] Bits, int Count);

    public static double? Read(Mat binary)
    {
        Cv2.FindContours(binary, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var contour = contours.MaxBy(c => Cv2.ContourArea(c));
        if (contour is null || Cv2.ContourArea(contour) < 15) return null;
        var bounds = Cv2.BoundingRect(contour);
        if (bounds.X == 0 || bounds.Y == 0 || bounds.Right >= binary.Cols || bounds.Bottom >= binary.Rows) return null;
        using var component = new Mat(binary.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.DrawContours(component, [contour], 0, Scalar.All(255), -1);
        Cv2.BitwiseAnd(component, binary, component);
        var moments = Cv2.Moments(component, true);
        var scale = Math.Sqrt(Area / moments.M00);
        using var transform = new Mat(2, 3, MatType.CV_64FC1, Scalar.All(0));
        transform.Set(0, 0, scale); transform.Set(1, 1, scale);
        transform.Set(0, 2, (Size - 1) / 2d - scale * moments.M10 / moments.M00);
        transform.Set(1, 2, (Size - 1) / 2d - scale * moments.M01 / moments.M00);
        using var normalized = new Mat();
        Cv2.WarpAffine(component, normalized, transform, new(Size, Size), InterpolationFlags.Nearest);
        var bits = Pack(normalized); var count = bits.Sum(b => BitOperations.PopCount(b));
        var scores = new double[180];
        foreach (var shape in Shapes.Value)
        {
            var intersection = 0;
            for (var i = 0; i < bits.Length; i++) intersection += BitOperations.PopCount(bits[i] & shape.Bits[i]);
            var score = (double)intersection / (count + shape.Count - intersection);
            var index = (int)shape.Angle / 2;
            scores[index] = Math.Max(scores[index], score);
        }
        var best = Array.IndexOf(scores, scores.Max());
        var alternative = scores.Where((_, i) => Math.Min(Math.Abs(i - best), 180 - Math.Abs(i - best)) > 30).Max();
        // A competing direction means the silhouette cannot distinguish its head from a wing or background.
        if (scores[best] < .68 || scores[best] - alternative < .03) return null;
        return best * 2 - 90;
    }

    private static Shape[] BuildShapes()
    {
        var shapes = new List<Shape>();
        for (var angle = 0; angle < 360; angle += 2)
        foreach (var ratio in new[] { .45f, .55f, .65f, .75f, .85f, .95f })
        foreach (var notch in new[] { 0f, .15f, .3f })
        {
            Point2f[] polygon = [new(0, -.65f), new(-ratio / 2, .35f), new(0, .35f - notch), new(ratio / 2, .35f)];
            var moments = Cv2.Moments(polygon);
            var scale = Math.Sqrt(Area / moments.M00);
            var radians = angle * Math.PI / 180;
            var points = polygon.Select(p =>
            {
                var x = (p.X - moments.M10 / moments.M00) * scale;
                var y = (p.Y - moments.M01 / moments.M00) * scale;
                return new Point((int)Math.Round((x * Math.Cos(radians) - y * Math.Sin(radians) + (Size - 1) / 2d) * 16),
                    (int)Math.Round((x * Math.Sin(radians) + y * Math.Cos(radians) + (Size - 1) / 2d) * 16));
            }).ToArray();
            using var mask = new Mat(Size, Size, MatType.CV_8UC1, Scalar.All(0));
            Cv2.FillPoly(mask, [points], Scalar.All(255), shift: 4);
            var bits = Pack(mask);
            shapes.Add(new(angle, bits, bits.Sum(b => BitOperations.PopCount(b))));
        }
        return shapes.ToArray();
    }

    private static ulong[] Pack(Mat mask)
    {
        var pixels = new byte[Size * Size]; Marshal.Copy(mask.Data, pixels, 0, pixels.Length);
        var bits = new ulong[pixels.Length / 64];
        for (var i = 0; i < pixels.Length; i++) if (pixels[i] != 0) bits[i / 64] |= 1UL << (i % 64);
        return bits;
    }
    public static double NorthClockwise(double imageDegrees) => (imageDegrees + 450) % 360;
}
