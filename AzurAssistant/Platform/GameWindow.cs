using System.Diagnostics;

namespace AzurAssistant.Platform;

public sealed class GameWindow(nint handle, int processId)
{
    public nint Handle { get; } = handle;
    public int ProcessId { get; } = processId;

    public static GameWindow Find(string processName)
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process) ids.Add(process.Id);
        }
        var candidates = new List<GameWindow>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (ids.Contains((int)pid) && NativeMethods.IsWindowVisible(hwnd)
                && NativeMethods.GetClientRect(hwnd, out var rect) && rect.Right > 0 && rect.Bottom > 0)
                candidates.Add(new GameWindow(hwnd, (int)pid));
            return true;
        }, 0);
        if (candidates.Count == 0) throw new InvalidOperationException($"未找到 {processName}.exe 的游戏窗口，请打开游戏后重新连接。");
        if (candidates.Count > 1) throw new InvalidOperationException("检测到多个游戏窗口，请保留一个游戏窗口后重新连接。");
        return candidates[0];
    }

    internal WindowGeometry Observe()
    {
        NativeMethods.GetWindowThreadProcessId(Handle, out var currentPid);
        if (!NativeMethods.IsWindow(Handle) || currentPid != ProcessId)
            throw new InvalidOperationException("游戏窗口已关闭，请打开游戏后重新连接。");
        if (NativeMethods.IsIconic(Handle))
            throw new InvalidOperationException("游戏窗口已最小化，请还原游戏后重新连接。");
        if (!NativeMethods.IsWindowVisible(Handle)
            || (NativeMethods.GetCloaked(Handle, 14, out var cloaked, 4) == 0 && cloaked != 0))
            throw new InvalidOperationException("游戏窗口不可见，请还原窗口后重新连接。");
        var origin = new NativeMethods.Point();
        if (!NativeMethods.GetClientRect(Handle, out var client) || !NativeMethods.ClientToScreen(Handle, ref origin)
            || NativeMethods.DwmGetWindowAttribute(Handle, 9, out var bounds, 16) < 0
            || client.Right <= 0 || client.Bottom <= 0)
            throw new InvalidOperationException("无法获取游戏客户区，请检查游戏窗口状态。");
        return new WindowGeometry(origin.X, origin.Y, client.Right, client.Bottom,
            bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }
}

internal readonly record struct WindowGeometry(int X, int Y, int Width, int Height,
    int WindowX, int WindowY, int WindowWidth, int WindowHeight)
{
    public int CropX => X - WindowX;
    public int CropY => Y - WindowY;
}
