using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using AzurAssistant.Runtime;

namespace AzurAssistant.Platform;

/// <summary>Replaceable OS registration boundary; a nonzero result is a Win32 error code.</summary>
public interface IGlobalHotkeyApi
{
    int Register(nint handle, int id, uint modifiers, uint virtualKey);
    void Unregister(nint handle, int id);
}

/// <summary>Owns both task hotkeys for one window. Editing suspends them without changing the startup bindings.</summary>
public sealed class TaskHotkeys : IDisposable
{
    private const uint NoRepeat = 0x4000;
    private readonly nint _handle;
    private readonly HwndSource _source;
    private readonly IGlobalHotkeyApi _api;
    private readonly Action<string> _reportFailure;
    private readonly Entry[] _entries;
    private bool _suspended;
    private bool _disposed;

    private sealed class Entry(int id, string name, HotkeyBinding binding, Func<Task> action)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public HotkeyBinding Binding { get; } = binding;
        public Func<Task> Action { get; } = action;
        public bool Registered { get; set; }
    }

    public TaskHotkeys(nint handle, string pauseHotkey, string stopHotkey, Func<Task> pause, Func<Task> stop,
        Action<string> reportFailure, IGlobalHotkeyApi? api = null)
    {
        if (!HotkeyBinding.TryParsePair(pauseHotkey, stopHotkey, out var pauseBinding, out var stopBinding, out var error))
            throw new ArgumentException(error);
        _handle = handle;
        _source = HwndSource.FromHwnd(handle) ?? throw new ArgumentException("快捷键需要已创建的助手窗口。", nameof(handle));
        _api = api ?? new NativeHotkeyApi();
        _reportFailure = reportFailure;
        _entries = [new(0x415A, "暂停 / 继续", pauseBinding, pause), new(0x415B, "停止所有任务", stopBinding, stop)];
        _source.AddHook(OnMessage);
        RegisterAll();
    }

    public void SetSuspended(bool suspended)
    {
        if (_disposed || _suspended == suspended) return;
        _suspended = suspended;
        if (suspended) UnregisterAll(); else RegisterAll();
    }

    private void RegisterAll()
    {
        var failures = new List<string>();
        foreach (var entry in _entries)
        {
            if (entry.Registered) continue;
            var error = _api.Register(_handle, entry.Id, entry.Binding.Modifiers | NoRepeat, entry.Binding.VirtualKey);
            entry.Registered = error == 0;
            if (error != 0) failures.Add($"{entry.Name}快捷键 {entry.Binding.Text} 注册失败：{new Win32Exception(error).Message}（错误码 {error}）。");
        }
        if (failures.Count > 0) _reportFailure(string.Join(Environment.NewLine, failures)
            + Environment.NewLine + "快捷键可能已被占用，请在设置中更换并重启助手。仍可点击页面中的“停止任务”。");
    }

    private void UnregisterAll()
    {
        foreach (var entry in _entries)
        {
            if (!entry.Registered) continue;
            _api.Unregister(_handle, entry.Id);
            entry.Registered = false;
        }
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed || _suspended || message != 0x0312) return 0;
        var entry = _entries.FirstOrDefault(value => value.Id == wParam && value.Registered);
        if (entry is null) return 0;
        handled = true;
        _ = InvokeAsync(entry);
        return 0;
    }

    private async Task InvokeAsync(Entry entry)
    {
        try { await entry.Action(); }
        catch (Exception ex) { _reportFailure($"{entry.Name}快捷键处理失败：{ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _source.RemoveHook(OnMessage);
    }

    private sealed class NativeHotkeyApi : IGlobalHotkeyApi
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(nint handle, int id);
        public int Register(nint handle, int id, uint modifiers, uint virtualKey)
        {
            if (RegisterHotKey(handle, id, modifiers, virtualKey)) return 0;
            var error = Marshal.GetLastWin32Error();
            return error == 0 ? 31 : error;
        }
        public void Unregister(nint handle, int id) => UnregisterHotKey(handle, id);
    }
}
