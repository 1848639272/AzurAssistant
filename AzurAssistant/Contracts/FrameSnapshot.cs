namespace AzurAssistant.Contracts;

/// <summary>Owned BGRA32 pixels, independent of the native frame. Consumers must not mutate Pixels.</summary>
public sealed record FrameSnapshot(long Id, TimeSpan SystemTimestamp, DateTimeOffset CapturedAt,
    int Width, int Height, long ViewportVersion, nint WindowHandle, ReadOnlyMemory<byte> Pixels,
    int ClientLeft = 0, int ClientTop = 0, int ProcessId = 0)
{
    public int Stride => checked(Width * 4);
}
