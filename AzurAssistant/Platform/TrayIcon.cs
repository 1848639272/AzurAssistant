using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AzurAssistant.Platform;

/// <summary>Minimal Shell_NotifyIcon wrapper: an icon with a context menu and double-click activation.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_TRAYICON = 0x8000 + 1;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x0010, LR_DEFAULTSIZE = 0x0040;
    private const int WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_LBUTTONUP = 0x0202;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private readonly Window _owner;
    private readonly uint _callback;
    private HwndSource? _source;
    private IntPtr _icon;
    private bool _added;

    public TrayIcon(Window owner, string tooltip)
    {
        _owner = owner;
        Tooltip = tooltip;
        _callback = WM_TRAYICON;
    }

    public string Tooltip { get; }
    public event Action? Activated;
    public event Action? MenuRequested;

    public bool IsVisible => _added;

    public void Show()
    {
        if (_added) return;
        var handle = new WindowInteropHelper(_owner).Handle;
        if (handle == IntPtr.Zero) return;
        _source ??= HwndSource.FromHwnd(handle);
        if (_source is null) return;
        _source.RemoveHook(OnWindowMessage);
        _source.AddHook(OnWindowMessage);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "branding", "app.ico");
        if (File.Exists(iconPath)) _icon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);

        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = _callback,
            hIcon = _icon,
            szTip = Truncate(Tooltip, 127),
            szInfo = "",
            szInfoTitle = "",
        };
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (!_added && _icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }

    public void Hide()
    {
        if (!_added) return;
        var handle = new WindowInteropHelper(_owner).Handle;
        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = handle,
            uID = 1,
            szTip = "",
            szInfo = "",
            szInfoTitle = "",
        };
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
        _source?.RemoveHook(OnWindowMessage);
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != _callback) return IntPtr.Zero;
        var mouse = lParam.ToInt64() & 0xFFFF;
        switch (mouse)
        {
            case WM_LBUTTONDBLCLK:
            case WM_LBUTTONUP:
                handled = true;
                Activated?.Invoke();
                break;
            case WM_RBUTTONUP:
                handled = true;
                MenuRequested?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    public void Dispose() => Hide();
}
