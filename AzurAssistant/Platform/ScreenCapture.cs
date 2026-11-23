using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzurAssistant.Platform;

/// <summary>
/// Captures a screen region through GDI and returns a pixelated (mosaic) bitmap. The overlay window marks itself as
/// excluded from capture, so what is read back is the game underneath rather than the cover itself.
/// </summary>
public static class ScreenCapture
{
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines,
        byte[] bits, ref BitmapInfo info, uint usage);

    /// <summary>Marks a window so screen captures see through it.</summary>
    public static bool ExcludeFromCapture(nint hwnd) =>
        hwnd != 0 && SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);

    /// <summary>
    /// Captures the region and averages it into blocks of <paramref name="block"/> pixels. Returns null when the
    /// region is empty or the capture failed.
    /// </summary>
    public static BitmapSource? CaptureMosaic(int x, int y, int width, int height, int block)
    {
        if (width < 1 || height < 1) return null;
        block = Math.Clamp(block, 2, 64);
        var screen = GetDC(0);
        if (screen == 0) return null;
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (memory == 0 || bitmap == 0) return null;
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, SrcCopy | CaptureBlt)) return null;

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height,
                    Planes = 1, BitCount = 32, Compression = 0,
                },
            };
            var pixels = new byte[width * height * 4];
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) == 0) return null;

            var smallWidth = Math.Max(1, width / block);
            var smallHeight = Math.Max(1, height / block);
            var output = new byte[smallWidth * smallHeight * 4];
            for (var by = 0; by < smallHeight; by++)
            for (var bx = 0; bx < smallWidth; bx++)
            {
                long b = 0, g = 0, r = 0;
                var count = 0;
                for (var py = by * block; py < Math.Min(height, (by + 1) * block); py++)
                for (var px = bx * block; px < Math.Min(width, (bx + 1) * block); px++)
                {
                    var index = (py * width + px) * 4;
                    b += pixels[index];
                    g += pixels[index + 1];
                    r += pixels[index + 2];
                    count++;
                }
                if (count == 0) count = 1;
                var target = (by * smallWidth + bx) * 4;
                output[target] = (byte)(b / count);
                output[target + 1] = (byte)(g / count);
                output[target + 2] = (byte)(r / count);
                output[target + 3] = 255;
            }

            var source = BitmapSource.Create(smallWidth, smallHeight, 96, 96, PixelFormats.Bgra32, null, output, smallWidth * 4);
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is OverflowException or OutOfMemoryException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }
}
