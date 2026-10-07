using System.Runtime.InteropServices;
using AzurAssistant.Contracts;
using OpenCvSharp;

namespace AzurAssistant.Vision;

public enum TemplateChannel { Gray, RedMinusBlue }
public sealed record ImageTemplate(int Width, int Height, byte[] ChannelPixels, TemplateChannel Channel = TemplateChannel.Gray);
public sealed record TemplateMatch(double Score, ClientPoint Center);

public static class OpenCvTemplateMatcher
{
    public static ImageTemplate Create(int width, int height, ReadOnlySpan<byte> bgra, TemplateChannel channel = TemplateChannel.Gray)
    {
        using var color = new Mat(height, width, MatType.CV_8UC4);
        if (bgra.Length != checked(width * height * 4)) throw new ArgumentException("模板像素尺寸无效。");
        Marshal.Copy(bgra.ToArray(), 0, color.Data, bgra.Length);
        using var gray = new Mat();
        ConvertChannel(color, gray, channel);
        Cv2.MeanStdDev(gray, out _, out var deviation);
        if (deviation.Val0 < 1) throw new ArgumentException("模板缺少可匹配的图像特征。");
        var pixels = new byte[width * height];
        Marshal.Copy(gray.Data, pixels, 0, pixels.Length);
        return new(width, height, pixels, channel);
    }

    public static TemplateMatch Match(FrameSnapshot frame, ImageTemplate template, PixelRegion search)
    {
        search.Validate(frame.Width, frame.Height);
        if (template.Width > search.Width || template.Height > search.Height)
            throw new ArgumentException("模板超出搜索区域。");
        var pixels = new byte[checked(search.Width * search.Height * 4)];
        for (var y = 0; y < search.Height; y++)
            frame.Pixels.Span.Slice(((search.Y + y) * frame.Width + search.X) * 4, search.Width * 4)
                .CopyTo(pixels.AsSpan(y * search.Width * 4));
        using var color = new Mat(search.Height, search.Width, MatType.CV_8UC4);
        Marshal.Copy(pixels, 0, color.Data, pixels.Length);
        using var gray = new Mat();
        ConvertChannel(color, gray, template.Channel);
        Cv2.MeanStdDev(gray, out _, out var deviation);
        if (deviation.Val0 < 1) return new(0, default);
        using var target = new Mat(template.Height, template.Width, MatType.CV_8UC1);
        Marshal.Copy(template.ChannelPixels, 0, target.Data, template.ChannelPixels.Length);
        using var scores = new Mat();
        Cv2.MatchTemplate(gray, target, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out _, out var best, out _, out var point);
        return new(double.IsFinite(best) ? best : 0,
            new ClientPoint(search.X + point.X + template.Width / 2, search.Y + point.Y + template.Height / 2));
    }

    private static void ConvertChannel(Mat source, Mat target, TemplateChannel channel)
    {
        if (channel == TemplateChannel.Gray) { Cv2.CvtColor(source, target, ColorConversionCodes.BGRA2GRAY); return; }
        if (channel != TemplateChannel.RedMinusBlue) throw new ArgumentOutOfRangeException(nameof(channel));
        // Opponent color suppresses neutral lighting while retaining warm/cool shape contrast.
        using var red = new Mat();
        using var blue = new Mat();
        Cv2.ExtractChannel(source, red, 2);
        Cv2.ExtractChannel(source, blue, 0);
        Cv2.AddWeighted(red, 0.5, blue, -0.5, 127.5, target);
    }
}
