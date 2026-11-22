using System.Runtime.InteropServices;

namespace AzurAssistant.Platform;

/// <summary>
/// One physical monitor. Bounds are physical pixels; Dip* values are the same rectangle in WPF device-independent
/// units for that monitor's DPI, which is what Window.Left/Top expect on a per-monitor-DPI-aware app.
/// </summary>
public sealed record DisplayMonitor(int Index, string Label, int X, int Y, int Width, int Height, bool Primary,
    double Scale = 1.0, int WorkX = 0, int WorkY = 0, int WorkWidth = 0, int WorkHeight = 0)
{
    /// <summary>Work area (screen minus taskbar) in DIPs; falls back to the full monitor when unknown.</summary>
    public double DipWorkX => (WorkWidth > 0 ? WorkX : X) / Scale;
    public double DipWorkY => (WorkHeight > 0 ? WorkY : Y) / Scale;
    public double DipWorkWidth => (WorkWidth > 0 ? WorkWidth : Width) / Scale;
    public double DipWorkHeight => (WorkHeight > 0 ? WorkHeight : Height) / Scale;

    public override string ToString() => Label;
}

/// <summary>Enumerates monitors through the Win32 API and reports their DPI so overlays scale on every screen.</summary>
public static class DisplayMonitors
{
    public static IReadOnlyList<DisplayMonitor> List()
    {
        var handles = new List<IntPtr>();
        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (monitor, _, _, _) => { handles.Add(monitor); return true; }, IntPtr.Zero);
        }
        catch (EntryPointNotFoundException) { return []; }
        var list = new List<DisplayMonitor>();
        var index = 0;
        foreach (var handle in handles)
        {
            var info = NativeMethods.MONITORINFO.Create();
            if (!NativeMethods.GetMonitorInfo(handle, ref info)) continue;
            index++;
            var bounds = info.rcMonitor;
            var work = info.rcWork;
            var primary = info.dwFlags == 1;
            var dpi = EffectiveDpi(handle);
            var scale = dpi / 96.0;
            var label = $"显示器 {index}：{bounds.Right - bounds.Left}×{bounds.Bottom - bounds.Top}"
                        + (Math.Abs(scale - 1.0) > 0.01 ? $" @{scale * 100:F0}%" : "")
                        + (primary ? "（主）" : "");
            list.Add(new DisplayMonitor(index - 1, label, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, primary, scale,
                work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top));
        }
        return list;
    }

    /// <summary>Resolves the monitor at <paramref name="index"/>, falling back to the primary one.</summary>
    public static DisplayMonitor? Resolve(int index)
    {
        var monitors = List();
        if (monitors.Count == 0) return null;
        return monitors.FirstOrDefault(monitor => monitor.Index == index)
               ?? monitors.FirstOrDefault(monitor => monitor.Primary)
               ?? monitors[0];
    }

    private static uint EffectiveDpi(IntPtr monitor)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX;
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        return 96;
    }
}
