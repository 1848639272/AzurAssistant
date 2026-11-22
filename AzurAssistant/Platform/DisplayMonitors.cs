using System.Runtime.InteropServices;

namespace AzurAssistant.Platform;

/// <summary>One physical monitor, used to place the HUD and notifications.</summary>
public sealed record DisplayMonitor(int Index, string Label, int X, int Y, int Width, int Height, bool Primary)
{
    public override string ToString() => Label;
}

/// <summary>Enumerates monitors through the Win32 API so overlays can follow the chosen screen.</summary>
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
            var primary = info.dwFlags == 1;
            var label = $"显示器 {index}：{bounds.Right - bounds.Left}×{bounds.Bottom - bounds.Top}"
                        + (primary ? "（主）" : "");
            list.Add(new DisplayMonitor(index - 1, label, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, primary));
        }
        return list;
    }

    /// <summary>Resolves the monitor at <paramref name="index"/>, falling back to the primary one.</summary>
    public static DisplayMonitor? Resolve(int index)
    {
        var monitors = List();
        if (monitors.Count == 0) return null;
        return monitors.FirstOrDefault(monitor => monitor.Index == index) ?? monitors.FirstOrDefault(monitor => monitor.Primary) ?? monitors[0];
    }
}
