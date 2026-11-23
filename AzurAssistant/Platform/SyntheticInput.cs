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

    /// <summary>Presses several keys at the same time (all downs, hold, then all ups) — used for purple notes.</summary>
    public static bool TapCombo(IReadOnlyList<ushort> keys, int holdMilliseconds = 30)
    {
        if (keys.Count == 0) return false;
        if (keys.Count == 1) return Tap(keys[0], holdMilliseconds);
        var downs = new Input[keys.Count];
        var ups = new Input[keys.Count];
        for (var index = 0; index < keys.Count; index++)
        {
            var scan = (ushort)MapVirtualKey(keys[index], 0);
            downs[index] = new Input
            {
                Type = InputKeyboard, VirtualKey = keys[index], Scan = scan, Flags = scan != 0 ? ScanCode : 0,
            };
            ups[index] = downs[index];
            ups[index].Flags |= KeyUp;
        }
        if (SendInput((uint)downs.Length, downs, Marshal.SizeOf<Input>()) != (uint)downs.Length) return false;
        if (holdMilliseconds > 0) Thread.Sleep(holdMilliseconds);
        return SendInput((uint)ups.Length, ups, Marshal.SizeOf<Input>()) == (uint)ups.Length;
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