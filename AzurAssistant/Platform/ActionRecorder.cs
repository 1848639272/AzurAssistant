using System.Diagnostics;
using System.Runtime.InteropServices;
using AzurAssistant.Contracts;

namespace AzurAssistant.Platform;

/// <summary>
/// Records the player's own keyboard and mouse input with microsecond timestamps while the game window is in front.
/// Uses low-level hooks so the game keeps receiving its input untouched; injected input and the assistant's own
/// hotkeys are ignored so a replay never feeds back into the recorder or the assistant.
/// </summary>
public sealed class ActionRecorder : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A;
    private const int WM_TIMER = 0x0113, WM_QUIT = 0x0012;
    private const uint LLKHF_INJECTED = 0x10, LLKHF_EXTENDED = 0x01, LLMHF_INJECTED = 0x01;
    private const uint TimerId = 1;
    private const int WatchdogIntervalMs = 200;
    private const long ForegroundGraceMs = 1200;

    private delegate nint HookProc(int code, nint wParam, nint lParam);
    private static ActionRecorder? _active;
    private static HookProc? _proc;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG
    { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT
    { public uint vkCode, scanCode, flags, time; public nint extraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT
    { public POINT pt; public uint mouseData, flags, time; public nint extraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int idHook, HookProc proc, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref POINT pt);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, nint hwnd, uint filterMin, uint filterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint SetTimer(nint hwnd, uint id, uint elapse, nint proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd, uint id);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private readonly nint _window;
    private readonly HashSet<int> _excluded;
    private readonly int _moveIntervalUs;
    private readonly TaskCompletionSource<ActionRecording> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ActionEvent> _events = [];
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private POINT _lastScreen;
    private readonly string _name;
    private Thread? _thread;
    private uint _threadId;
    private nint _keyboardHook, _mouseHook;
    private bool _clockStarted;
    private long _lastFlushUs;
    private int _pendingDx, _pendingDy;
    private long _awaySinceMs = -1;
    private int _clientWidth, _clientHeight;
    private volatile bool _stopped;

    public ActionRecorder(nint window, string name, int clientWidth, int clientHeight,
        IEnumerable<int>? excludedVirtualKeys = null, int moveIntervalMs = 4)
    {
        _window = window;
        _name = name;
        _clientWidth = clientWidth;
        _clientHeight = clientHeight;
        _excluded = excludedVirtualKeys is null ? [] : [.. excludedVirtualKeys];
        _moveIntervalUs = Math.Max(1, moveIntervalMs) * 1000;
        GetCursorPos(out _lastScreen);
    }

    /// <summary>Completes when the recording stops, by request, by the game losing the foreground, or by a hook failure.</summary>
    public Task<ActionRecording> Completion => _completion.Task;

    /// <summary>Test-only: also capture synthetic (SendInput) events so a hook round trip can be verified without hardware.</summary>
    internal bool IncludeInjectedForTests { get; set; }
    public int EventCount { get { lock (_gate) return _events.Count; } }
    public TimeSpan Elapsed => _clockStarted ? _clock.Elapsed : TimeSpan.Zero;
    public bool IsRunning => !_stopped;

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("录制已经开始。");
        _active = this;
        _proc = HookCallback;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "ActionRecorder" };
        _thread.Start();
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        var id = _threadId;
        if (id != 0) PostThreadMessage(id, WM_QUIT, 0, 0);
    }

    private void ThreadMain()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            var module = GetModuleHandle(null);
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc!, module, 0);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _proc!, module, 0);
            if (_keyboardHook == 0 || _mouseHook == 0)
                throw new InvalidOperationException($"无法安装键鼠钩子（错误 {Marshal.GetLastWin32Error()}），请检查助手权限。");
            SetTimer(0, TimerId, WatchdogIntervalMs, 0);
            while (!_stopped && GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.message == WM_QUIT || _stopped) break;
                if (message.message == WM_TIMER) { Watchdog(); continue; }
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex) { _completion.TrySetException(ex); return; }
        finally { Finish(); }
    }

    private void Finish()
    {
        _stopped = true;
        if (_threadId != 0) KillTimer(0, TimerId);
        if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
        if (ReferenceEquals(_active, this)) _active = null;
        lock (_gate)
        {
            FlushMove(NowUs());
            var duration = _events.Count == 0 ? 0 : _events[^1].Us;
            if (_events.Count == 0)
            {
                _completion.TrySetException(new InvalidOperationException("没有录到任何游戏输入：请先把游戏切到前台再操作。"));
                return;
            }
            var recording = new ActionRecording(ActionRecording.CurrentSchema, _name, DateTimeOffset.Now, _clientWidth, _clientHeight, duration, _events.ToArray());
            _completion.TrySetResult(recording);
        }
    }

    private void Watchdog()
    {
        if (!IsWindow(_window)) { Stop(); return; }
        if (!_clockStarted) return; // still waiting for the player to focus the game
        if (GetForegroundWindow() == _window) { _awaySinceMs = -1; return; }
        var now = Environment.TickCount64;
        if (_awaySinceMs < 0) { _awaySinceMs = now; return; }
        if (now - _awaySinceMs >= ForegroundGraceMs) Stop();
    }

    private static nint HookCallback(int code, nint wParam, nint lParam)
    {
        var recorder = _active;
        if (code >= 0 && recorder is not null)
        {
            try { recorder.OnHook((int)wParam, lParam); }
            catch { /* a hook must never throw into the input pipeline */ }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private void OnHook(int message, nint lParam)
    {
        if (_stopped || GetForegroundWindow() != _window) return;
        switch (message)
        {
            case WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP:
            {
                var keyboard = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (!IncludeInjectedForTests && (keyboard.flags & LLKHF_INJECTED) != 0) return;
                if (_excluded.Contains((int)keyboard.vkCode)) return;
                var up = message is WM_KEYUP or WM_SYSKEYUP;
                var us = NowUs();
                lock (_gate)
                {
                    FlushMove(us);
                    Add(new ActionEvent
                    {
                        Us = us,
                        Kind = up ? ActionEventKind.KeyUp : ActionEventKind.KeyDown,
                        Vk = (int)keyboard.vkCode,
                        Scan = (int)keyboard.scanCode,
                        Ext = (keyboard.flags & LLKHF_EXTENDED) != 0,
                    });
                }
                break;
            }
            case WM_MOUSEMOVE:
            {
                var mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (!IncludeInjectedForTests && (mouse.flags & LLMHF_INJECTED) != 0) return;
                lock (_gate) OnMove(mouse.pt);
                break;
            }
            case WM_LBUTTONDOWN or WM_LBUTTONUP or WM_RBUTTONDOWN or WM_RBUTTONUP or WM_MBUTTONDOWN or WM_MBUTTONUP:
            {
                var mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (!IncludeInjectedForTests && (mouse.flags & LLMHF_INJECTED) != 0) return;
                var kind = message switch
                {
                    WM_LBUTTONDOWN => ActionEventKind.LeftDown,
                    WM_LBUTTONUP => ActionEventKind.LeftUp,
                    WM_RBUTTONDOWN => ActionEventKind.RightDown,
                    WM_RBUTTONUP => ActionEventKind.RightUp,
                    WM_MBUTTONDOWN => ActionEventKind.MiddleDown,
                    _ => ActionEventKind.MiddleUp,
                };
                var us = NowUs();
                lock (_gate)
                {
                    FlushMove(us);
                    var client = ToClient(mouse.pt);
                    Add(new ActionEvent { Us = us, Kind = kind, X = client.X, Y = client.Y });
                }
                break;
            }
            case WM_MOUSEWHEEL:
            {
                var mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (!IncludeInjectedForTests && (mouse.flags & LLMHF_INJECTED) != 0) return;
                var us = NowUs();
                lock (_gate)
                {
                    FlushMove(us);
                    var client = ToClient(mouse.pt);
                    Add(new ActionEvent { Us = us, Kind = ActionEventKind.Wheel, X = client.X, Y = client.Y, Wheel = (short)(mouse.mouseData >> 16) });
                }
                break;
            }
        }
    }

    private void OnMove(POINT screen)
    {
        var dx = screen.X - _lastScreen.X;
        var dy = screen.Y - _lastScreen.Y;
        if (dx == 0 && dy == 0) return;
        _lastScreen.X = screen.X;
        _lastScreen.Y = screen.Y;
        _pendingDx += dx;
        _pendingDy += dy;
        var us = NowUs();
        if (us - _lastFlushUs >= _moveIntervalUs) FlushMove(us);
    }

    /// <summary>Emits the accumulated relative movement, keeping total motion intact while bounding event count.</summary>
    private void FlushMove(long us)
    {
        if (_pendingDx == 0 && _pendingDy == 0) return;
        var client = ToClient(_lastScreen);
        Add(new ActionEvent { Us = us, Kind = ActionEventKind.Move, X = client.X, Y = client.Y, Dx = _pendingDx, Dy = _pendingDy });
        _pendingDx = 0;
        _pendingDy = 0;
        _lastFlushUs = us;
    }

    private void Add(ActionEvent item)
    {
        if (_events.Count >= ActionRecording.MaximumEvents) { Stop(); return; }
        _events.Add(item);
    }

    private long NowUs()
    {
        if (!_clockStarted) { _clockStarted = true; _clock.Start(); return 0; }
        return _clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
    }

    private POINT ToClient(POINT screen)
    {
        var point = screen;
        return ScreenToClient(_window, ref point) ? point : screen;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);

    public void Dispose()
    {
        Stop();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
