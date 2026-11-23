using System.Runtime.InteropServices;

namespace AzurAssistant.Platform;

/// <summary>Reads the colour of a screen pixel (used by the coordinate picker and the colour debug tool).</summary>
public static class ScreenSampler
{
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint hdc, int x, int y);

    /// <summary>Returns the colour at a screen point, or null when the pixel cannot be read.</summary>
    public static (byte R, byte G, byte B)? Sample(int x, int y)
    {
        var screen = GetDC(0);
        if (screen == 0) return null;
        try
        {
            var color = GetPixel(screen, x, y);
            if (color == 0xFFFFFFFF) return null;
            return ((byte)(color & 0xFF), (byte)((color >> 8) & 0xFF), (byte)((color >> 16) & 0xFF));
        }
        finally { ReleaseDC(0, screen); }
    }
}
