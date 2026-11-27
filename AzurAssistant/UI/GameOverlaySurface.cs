using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AzurAssistant.Platform;

namespace AzurAssistant.UI;

/// <summary>A click-through popup owned by the game, positioned in physical desktop pixels.</summary>
internal sealed class GameOverlaySurface(string name, Visual visual, Func<WindowGeometry, Int32Rect> boundsFor,
    Action<string>? report = null) : IDisposable
{
    private const int RetryDelayMs = 5000;
    private GameWindow? _game;
    private HwndSource? _source;
    private Int32Rect? _bounds;
    private nint _hook;
    private LocationCallback? _location;
    private long _retryAt;
    private bool _unavailable;
    public nint Handle => _source is { IsDisposed: false } source ? source.Handle : 0;

    public void SetTarget(GameWindow? game)
    {
        if (_game?.Handle != game?.Handle || _game?.ProcessId != game?.ProcessId)
        {
            Dispose();
            _game = game;
        }
        if (game is null || Environment.TickCount64 < _retryAt) return;
        if (!GameWindow.Exists(game.Handle, game.ProcessId))
        {
            ReleaseSurface();
            return;
        }
        if (_source?.IsDisposed == true) ReleaseSurface();
        if (_source is null)
        {
            try
            {
                var p = new HwndSourceParameters(name)
                {
                    ParentWindow = game.Handle, WindowStyle = unchecked((int)0x80000000),
                    ExtendedWindowStyle = 0x08000000 | 0x80 | 0x20, // NOACTIVATE | TOOLWINDOW | TRANSPARENT
                    UsesPerPixelOpacity = true, Width = 1, Height = 1, PositionX = -32000, PositionY = -32000
                };
                _source = new HwndSource(p);
                _source.SizeToContent = SizeToContent.Manual;
                _source.RootVisual = visual;
                _location = (_, _, hwnd, obj, child, _, _) =>
                {
                    if (_game?.Handle == hwnd && obj == 0 && child == 0) Place();
                };
                _hook = SetWinEventHook(0x800B, 0x800B, 0, _location, (uint)game.ProcessId, 0, 0);
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or COMException or ObjectDisposedException)
            {
                Suspend(ex.Message);
                return;
            }
        }
        Place();
    }

    private void Place()
    {
        if (_game is null || Handle == 0) return;
        WindowGeometry geometry;
        try { geometry = _game.Observe(); }
        catch (InvalidOperationException)
        {
            if (_bounds is not null) ShowWindow(Handle, 0);
            _bounds = null;
            return;
        }
        var bounds = boundsFor(geometry);
        if (_bounds == bounds) return;
        const uint noActivate = 0x10, noZOrder = 0x4, noOwnerZOrder = 0x200, show = 0x40;
        if (!SetWindowPos(Handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, noActivate | noZOrder | noOwnerZOrder | show))
        {
            Suspend(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return;
        }
        _bounds = bounds;
        if (_unavailable) report?.Invoke($"{name}已恢复。");
        _unavailable = false;
    }

    private void Suspend(string reason)
    {
        _retryAt = Environment.TickCount64 + RetryDelayMs;
        ReleaseSurface();
        if (!_unavailable) report?.Invoke($"{name}暂时不可用（{reason}），将重试。");
        _unavailable = true;
    }

    public void Dispose()
    {
        _game = null;
        _retryAt = 0;
        _unavailable = false;
        ReleaseSurface();
    }

    private void ReleaseSurface()
    {
        if (_hook != 0) { UnhookWinEvent(_hook); _hook = 0; }
        _location = null;
        var source = _source;
        _source = null;
        _bounds = null;
        if (source is { IsDisposed: false })
        {
            source.RootVisual = null;
            source.Dispose();
        }
    }

    private delegate void LocationCallback(nint hook, uint type, nint window, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, LocationCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);
}
