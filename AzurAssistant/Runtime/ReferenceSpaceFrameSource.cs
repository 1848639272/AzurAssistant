using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Runtime;

/// <summary>
/// Publishes 1080p game frames in reference coordinates. Supported larger clients keep their native size for input.
/// </summary>
public sealed class ReferenceSpaceFrameSource(IFrameSource inner) : IFrameSource
{
    public async Task<FrameSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var frame = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Width == FrameNormalizer.ReferenceWidth && frame.Height == FrameNormalizer.ReferenceHeight) return frame;
        if ((frame.Width, frame.Height) is not ((2560, 1440) or (3840, 2160)))
            throw new InvalidOperationException($"游戏客户区为 {frame.Width}×{frame.Height}。请设为 1920×1080、2560×1440 或 3840×2160，然后重新连接。");
        var pixels = FrameNormalizer.ToReference(frame.Pixels.Span, frame.Width, frame.Height);
        cancellationToken.ThrowIfCancellationRequested();
        return frame with
        {
            Width = FrameNormalizer.ReferenceWidth,
            Height = FrameNormalizer.ReferenceHeight,
            Pixels = pixels,
            NativeWidth = frame.Width,
            NativeHeight = frame.Height,
        };
    }

    public void Dispose() => inner.Dispose();
}
