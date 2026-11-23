using System.Windows;

namespace AzurAssistant.Runtime;

/// <summary>Where the overlay window goes and where the cover rectangle sits inside it, all in DIPs.</summary>
public readonly record struct UidCoverPlacement(double WindowLeft, double WindowTop, double WindowWidth, double WindowHeight,
    double CoverLeft, double CoverTop, double CoverWidth, double CoverHeight)
{
    public bool IsValid => WindowWidth > 0 && WindowHeight > 0 && CoverWidth >= 1 && CoverHeight >= 1;

    public Rect CoverRect => new(CoverLeft, CoverTop, CoverWidth, CoverHeight);
}

/// <summary>
/// Pure geometry for the UID cover. The configured rectangle is expressed in the same 1920×1080 reference space the
/// recognition assets use, so one setting works at every resolution; conversion to DIPs happens here.
/// </summary>
public static class UidCoverGeometry
{
    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;

    /// <summary>Default cover rectangle in reference space: the "UID:xxxxx" text at the bottom-left.</summary>
    public const int DefaultX = 78;
    public const int DefaultY = 1026;
    public const int DefaultWidth = 132;
    public const int DefaultHeight = 26;

    /// <summary>
    /// Builds the overlay placement from the target client area (physical pixels), its DPI scale and the configured
    /// rectangle in reference space.
    /// </summary>
    public static UidCoverPlacement Compute(int clientX, int clientY, int clientWidth, int clientHeight, double dpiScale,
        int x, int y, int width, int height)
    {
        if (dpiScale <= 0 || double.IsNaN(dpiScale)) dpiScale = 1;
        if (clientWidth <= 0 || clientHeight <= 0) return default;

        var perReferenceX = clientWidth / (double)ReferenceWidth;
        var perReferenceY = clientHeight / (double)ReferenceHeight;
        var windowWidth = clientWidth / dpiScale;
        var windowHeight = clientHeight / dpiScale;

        var left = Math.Clamp(x, 0, ReferenceWidth - 1) * perReferenceX / dpiScale;
        var top = Math.Clamp(y, 0, ReferenceHeight - 1) * perReferenceY / dpiScale;
        var w = Math.Max(1, width) * perReferenceX / dpiScale;
        var h = Math.Max(1, height) * perReferenceY / dpiScale;
        // Keep the box inside the window even if the caller passed something silly.
        if (left + w > windowWidth) w = Math.Max(1, windowWidth - left);
        if (top + h > windowHeight) h = Math.Max(1, windowHeight - top);

        return new UidCoverPlacement(clientX / dpiScale, clientY / dpiScale, windowWidth, windowHeight, left, top, w, h);
    }

    /// <summary>Converts a dragged DIP rectangle back into reference-space pixels for persistence.</summary>
    public static (int X, int Y, int Width, int Height) ToReference(double dipX, double dipY, double dipWidth, double dipHeight,
        int clientWidth, int clientHeight, double dpiScale)
    {
        if (dpiScale <= 0 || double.IsNaN(dpiScale)) dpiScale = 1;
        if (clientWidth <= 0 || clientHeight <= 0) return (DefaultX, DefaultY, DefaultWidth, DefaultHeight);
        var perReferenceX = clientWidth / (double)ReferenceWidth;
        var perReferenceY = clientHeight / (double)ReferenceHeight;
        return (
            Math.Max(0, (int)Math.Round(dipX * dpiScale / perReferenceX, MidpointRounding.AwayFromZero)),
            Math.Max(0, (int)Math.Round(dipY * dpiScale / perReferenceY, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(dipWidth * dpiScale / perReferenceX, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(dipHeight * dpiScale / perReferenceY, MidpointRounding.AwayFromZero)));
    }
}
