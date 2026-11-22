using System.Diagnostics;
using System.Runtime.InteropServices;
using AzurAssistant.Contracts;

namespace AzurAssistant.Platform;

/// <summary>One client-area geometry sample of the replay target.</summary>
public readonly record struct ClientGeometry(int Left, int Top, int Width, int Height);

/// <summary>Low-level injection seam, so replay can be verified without touching the real input queue.</summary>
public interface IActionSink
{
    void Key(ushort scan, bool extended, bool up);
    void Move(int dx, int dy);
    void Point(int screenX, int screenY);
    void Button(ActionEventKind kind, bool down);
    void Wheel(int delta);
}

/// <summary>Injects through SendInput with the same scan codes and absolute coordinates the assistant already uses.</summary>
public sealed class SendInputSink : IActionSink
{
    private const uint MouseMove = 0x0001, MouseAbsolute = 0x8000, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004,
        MouseRightDown = 0x0008, MouseRightUp = 0x0010, MouseMiddleDown = 0x0020, MouseMiddleUp = 0x0040, MouseWheelFlag = 0x0800;
    private const uint KeyExtended = 0x0001, KeyUpFlag = 0x0002, KeyScanCode = 0x0008;

    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION
    { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    public void Key(ushort scan, bool extended, bool up)
        => Send(new INPUT
        {
            type = 1,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT { wScan = scan, dwFlags = KeyScanCode | (extended ? KeyExtended : 0) | (up ? KeyUpFlag : 0) },
            },
        });

    public void Move(int dx, int dy)
        => Send(new INPUT { type = 0, u = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MouseMove } } });

    public void Point(int screenX, int screenY)
    {
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = Math.Max(1, GetSystemMetrics(78) - 1);
        var height = Math.Max(1, GetSystemMetrics(79) - 1);
        Send(new INPUT
        {
            type = 0,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)((long)(screenX - left) * 65535 / width),
                    dy = (int)((long)(screenY - top) * 65535 / height),
                    dwFlags = MouseMove | MouseAbsolute,
                },
            },
        });
    }

    public void Button(ActionEventKind kind, bool down)
        => Send(new INPUT
        {
            type = 0,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = kind switch
                    {
                        ActionEventKind.LeftDown => MouseLeftDown,
                        ActionEventKind.LeftUp => MouseLeftUp,
                        ActionEventKind.RightDown => MouseRightDown,
                        ActionEventKind.RightUp => MouseRightUp,
                        ActionEventKind.MiddleDown => MouseMiddleDown,
                        _ => MouseMiddleUp,
                    },
                },
            },
        });

    public void Wheel(int delta)
        => Send(new INPUT
        {
            type = 0,
            u = new INPUTUNION { mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MouseWheelFlag } },
        });

    private static void Send(INPUT input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException($"Windows 拒绝输入注入（错误 {Marshal.GetLastWin32Error()}），请检查助手与游戏的运行权限。");
    }
}

/// <summary>
/// Replays a recorded player input stream onto the game window with the original timing: key presses keep their exact
/// hold time, camera turns replay as the same relative deltas, and clicks replay at the same client-area position.
/// </summary>
public sealed class ActionPlayer(IActionSink? sink = null, Action<string>? log = null)
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref POINT point);

    private readonly IActionSink _sink = sink ?? new SendInputSink();

    /// <summary>Test-only: replay into a sink without owning the real foreground, so no input reaches the desktop.</summary>
    internal bool IgnoreForegroundForTests { get; set; }

    public static ClientGeometry GeometryOf(nint window)
    {
        GetClientRect(window, out var rect);
        var origin = new POINT();
        ClientToScreen(window, ref origin);
        return new ClientGeometry(origin.X, origin.Y, rect.Right, rect.Bottom);
    }

    /// <summary>Plays every event; returns once the last event was sent. Cancellation releases every held key and button.</summary>
    public void Play(ActionRecording recording, nint window, Action<long, long>? progress, Func<bool>? isPaused, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var geometry = GeometryOf(window);
        if (geometry.Width <= 0 || geometry.Height <= 0)
            throw new InvalidOperationException("游戏窗口客户区无效，无法回放动作。");
        var scaleX = (double)geometry.Width / recording.ClientWidth;
        var scaleY = (double)geometry.Height / recording.ClientHeight;
        if (Math.Abs(scaleX - 1) > 0.02 || Math.Abs(scaleY - 1) > 0.02)
            log?.Invoke($"客户区与录制时不同（{recording.ClientWidth}×{recording.ClientHeight} → {geometry.Width}×{geometry.Height}），坐标将按比例缩放。");
        var heldKeys = new List<ActionEvent>();
        var leftHeld = false;
        var rightHeld = false;
        var middleHeld = false;
        var clock = Stopwatch.StartNew();
        var lastCheck = 0L;
        var lastProgress = -1L;
        try
        {
            foreach (var item in recording.Events)
            {
                token.ThrowIfCancellationRequested();
                if (isPaused?.Invoke() == true) throw new InvalidOperationException("动作回放不支持暂停，请使用停止。");
                WaitUntil(item.Us, clock, token);
                if (item.Us - lastCheck >= 250_000)
                {
                    lastCheck = item.Us;
                    if (!IgnoreForegroundForTests) RequireForeground(window);
                    geometry = GeometryOf(window);
                }
                switch (item.Kind)
                {
                    case ActionEventKind.KeyDown:
                        _sink.Key((ushort)item.Scan, item.Ext, up: false);
                        heldKeys.Add(item);
                        break;
                    case ActionEventKind.KeyUp:
                        _sink.Key((ushort)item.Scan, item.Ext, up: true);
                        heldKeys.RemoveAll(held => held.Scan == item.Scan && held.Ext == item.Ext);
                        break;
                    case ActionEventKind.Move:
                        _sink.Move(item.Dx, item.Dy);
                        break;
                    case ActionEventKind.LeftDown or ActionEventKind.RightDown or ActionEventKind.MiddleDown:
                        var point = ToScreen(item, geometry, scaleX, scaleY);
                        _sink.Point(point.X, point.Y);
                        _sink.Button(item.Kind, down: true);
                        if (item.Kind == ActionEventKind.LeftDown) leftHeld = true;
                        else if (item.Kind == ActionEventKind.RightDown) rightHeld = true;
                        else middleHeld = true;
                        break;
                    case ActionEventKind.LeftUp:
                        _sink.Button(item.Kind, down: false); leftHeld = false; break;
                    case ActionEventKind.RightUp:
                        _sink.Button(item.Kind, down: false); rightHeld = false; break;
                    case ActionEventKind.MiddleUp:
                        _sink.Button(item.Kind, down: false); middleHeld = false; break;
                    case ActionEventKind.Wheel:
                        var wheelPoint = ToScreen(item, geometry, scaleX, scaleY);
                        _sink.Point(wheelPoint.X, wheelPoint.Y);
                        _sink.Wheel(item.Wheel);
                        break;
                }
                if (progress is not null && item.Us - lastProgress >= 200_000)
                {
                    lastProgress = item.Us;
                    progress(item.Us, recording.DurationUs);
                }
            }
            progress?.Invoke(recording.DurationUs, recording.DurationUs);
        }
        finally
        {
            // Never leave a key or button pressed when the replay stops or fails.
            foreach (var held in heldKeys) _sink.Key((ushort)held.Scan, held.Ext, up: true);
            if (leftHeld) _sink.Button(ActionEventKind.LeftUp, down: false);
            if (rightHeld) _sink.Button(ActionEventKind.RightUp, down: false);
            if (middleHeld) _sink.Button(ActionEventKind.MiddleUp, down: false);
            clock.Stop();
        }
    }

    private static void RequireForeground(nint window)
    {
        if (GetForegroundWindow() != window) throw new InputUnavailableException("游戏不在前台，动作回放已停止。");
    }

    private static POINT ToScreen(ActionEvent item, ClientGeometry geometry, double scaleX, double scaleY)
        => new() { X = geometry.Left + (int)Math.Round(item.X * scaleX), Y = geometry.Top + (int)Math.Round(item.Y * scaleY) };

    private static void WaitUntil(long targetUs, Stopwatch clock, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var remaining = targetUs - clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
            if (remaining <= 0) return;
            if (remaining > 3000) Thread.Sleep((int)(remaining / 1000) - 1);
            else if (remaining > 400) Thread.Yield();
            else Thread.SpinWait(80);
        }
    }
}
