namespace AzurAssistant.Contracts;

/// <summary>Owned BGRA32 pixels, independent of the native frame. Consumers must not mutate Pixels.</summary>
/// <remarks>
/// <see cref="Width"/> and <see cref="Height"/> are the reference client area every recognition asset and hit point is
/// calibrated in (currently 1920×1080). <see cref="NativeWidth"/> and <see cref="NativeHeight"/> keep the real physical
/// client size so screen input can be mapped back onto the actual window.
/// </remarks>
public sealed record FrameSnapshot(long Id, TimeSpan SystemTimestamp, DateTimeOffset CapturedAt,
    int Width, int Height, long ViewportVersion, nint WindowHandle, ReadOnlyMemory<byte> Pixels,
    int ClientLeft = 0, int ClientTop = 0, int ProcessId = 0, int NativeWidth = 0, int NativeHeight = 0)
{
    public bool SameClientSurface(FrameSnapshot other) => WindowHandle == other.WindowHandle && ProcessId == other.ProcessId &&
        Width == other.Width && Height == other.Height && ContentWidth == other.ContentWidth && ContentHeight == other.ContentHeight;

    public bool SameViewport(FrameSnapshot other) => SameClientSurface(other) && ViewportVersion == other.ViewportVersion &&
        ClientLeft == other.ClientLeft && ClientTop == other.ClientTop;

    public int Stride => checked(Width * 4);

    /// <summary>Client width in physical pixels; equals <see cref="Width"/> when the frame was not normalized.</summary>
    public int ContentWidth => NativeWidth > 0 ? NativeWidth : Width;

    /// <summary>Client height in physical pixels; equals <see cref="Height"/> when the frame was not normalized.</summary>
    public int ContentHeight => NativeHeight > 0 ? NativeHeight : Height;

    /// <summary>True when the captured client area was rescaled to the reference client area.</summary>
    public bool IsNormalized => Width != ContentWidth || Height != ContentHeight;
}
