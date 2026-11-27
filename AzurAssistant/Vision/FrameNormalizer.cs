using System.Runtime.InteropServices;
using OpenCvSharp;

namespace AzurAssistant.Vision;

/// <summary>
/// Rescales a captured client area onto the fixed reference client area every recognition asset was captured in.
/// The frame source passes native 1080p through and sends other supported client sizes here.
/// </summary>
internal static class FrameNormalizer
{
    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;
    private static readonly Size Reference = new(ReferenceWidth, ReferenceHeight);

    /// <summary>Returns BGRA32 pixels rescaled to the reference client area.</summary>
    public static unsafe byte[] ToReference(ReadOnlySpan<byte> source, int width, int height)
    {
        if (width <= 0 || height <= 0 || source.Length != checked(width * height * 4))
            throw new InvalidOperationException($"画面数据尺寸异常：{width}×{height} / {source.Length} 字节。");
        fixed (byte* origin = source)
        {
            // Wrap the caller's buffer instead of copying it: the pool frame is already an owned BGRA32 block.
            using var image = Mat.FromPixelData(height, width, MatType.CV_8UC4, (nint)origin);
            using var target = new Mat();
            Cv2.Resize(image, target, Reference, 0, 0,
                width < ReferenceWidth ? InterpolationFlags.Linear : InterpolationFlags.Area);
            var resized = new byte[checked(ReferenceWidth * ReferenceHeight * 4)];
            Marshal.Copy(target.Data, resized, 0, resized.Length);
            return resized;
        }
    }
}
