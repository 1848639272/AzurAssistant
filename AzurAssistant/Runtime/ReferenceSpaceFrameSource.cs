using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Runtime;

/// <summary>
/// Adapts a frame source onto the reference client area the recognition assets were captured in, so a game window that
/// is not 1920×1080 client pixels keeps working instead of being rejected by the per-feature size guards.
/// The native client size is preserved on every frame so mouse input maps back onto the real window.
/// </summary>
public sealed class ReferenceSpaceFrameSource(IFrameSource inner) : IFrameSource
{
    public async Task<FrameSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var frame = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (frame.Width == FrameNormalizer.ReferenceWidth && frame.Height == FrameNormalizer.ReferenceHeight) return frame;
        var pixels = FrameNormalizer.ToReference(frame.Pixels.Span, frame.Width, frame.Height);
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
