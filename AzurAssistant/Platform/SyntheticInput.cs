using System.Runtime.InteropServices;

namespace AzurAssistant.Platform;

/// <summary>Minimal SendInput wrapper used by the mini-game helpers (key down/up with a virtual-key code).</summary>
public static class SyntheticInput
{
    private const int InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint ScanCode = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>True when the foreground window belongs to the given process.</summary>
    public static bool IsForegroundOf(int processId)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return false;
        GetWindowThreadProcessId(foreground, out var owner);
        return (int)owner == processId;
    }

    /// <summary>Presses and releases one virtual key. Returns false when SendInput was rejected.</summary>
    public static bool Tap(ushort virtualKey, int holdMilliseconds = 30)
    {
        var scan = (ushort)MapVirtualKey(virtualKey, 0);
        var down = new Input { Type = InputKeyboard, VirtualKey = virtualKey, Scan = scan, Flags = scan != 0 ? ScanCode : 0 };
        var up = down;
        up.Flags |= KeyUp;
        if (SendInput(1, [down], Marshal.SizeOf<Input>()) != 1) return false;
        if (holdMilliseconds > 0) Thread.Sleep(holdMilliseconds);
        return SendInput(1, [up], Marshal.SizeOf<Input>()) == 1;
    }
}