using System.Diagnostics;
using System.Runtime.InteropServices;
using AzurAssistant.Contracts;

namespace AzurAssistant.Platform;

/// <summary>Foreground-only Windows keyboard and mouse input, revalidated against the source frame.</summary>
public sealed class WindowsGameInput(Func<GameWindow> findWindow) : IGameInput
{
    private sealed record ActivationTiming(TimeSpan Timeout, TimeSpan RetryInterval, TimeSpan PollInterval);
    private ActivationTiming _activationTiming = new(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(40));
    private GameWindow? _target;
    private bool _leftHeld;
    private readonly HashSet<ushort> _heldScans = [];
    private readonly object _holdGate = new();
    private ContinuousHold? _continuous;
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Value; }
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClipCursor(out NativeMethods.Rect rectangle);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativeMethods.Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativeMethods.Point point);

    public void SetActivationTiming(TimeSpan timeout, TimeSpan retryInterval, TimeSpan pollInterval)
    {
        if (timeout <= TimeSpan.Zero || retryInterval <= TimeSpan.Zero || pollInterval <= TimeSpan.Zero
            || pollInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout), "窗口置前等待、重试和观察间隔必须为有效的正时长。");
        _activationTiming = new(timeout, retryInterval, pollInterval);
    }

    public async Task ActivateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var timing = _activationTiming;
        ReleaseAll();
        _target = findWindow();
        if (ProcessPrivileges.IsElevated(_target.ProcessId) && !ProcessPrivileges.IsElevated(Environment.ProcessId))
            throw new ElevationRequiredException();
        if (NativeMethods.IsIconic(_target.Handle)) ShowWindowAsync(_target.Handle, 9);
        SetForegroundWindow(_target.Handle);
        var timer = Stopwatch.StartNew();
        var lastAttempt = -1L;
        while (timer.Elapsed < timing.Timeout)
        {
            token.ThrowIfCancellationRequested();
            var attempt = timer.Elapsed.Ticks / timing.RetryInterval.Ticks;
            if (attempt != lastAttempt)
            {
                if (NativeMethods.IsIconic(_target.Handle)) ShowWindowAsync(_target.Handle, 9);
                SetForegroundWindow(_target.Handle);
                lastAttempt = attempt;
            }
            if (NativeMethods.GetForegroundWindow() == _target.Handle && !NativeMethods.IsIconic(_target.Handle))
            { _target.Observe(); return; }
            await Task.Delay(timing.PollInterval, token);
        }
        throw new InvalidOperationException("无法将游戏置于前台，请点击游戏窗口后重新开始。");
    }

    public async Task ClickAsync(FrameSnapshot frame, ClientPoint point, CancellationToken token)
    {
        RequireNoContinuousHold();
        token.ThrowIfCancellationRequested();
        var target = _target ?? throw new InvalidOperationException("必须先激活游戏窗口。");
        var geometry = target.Observe();
        var age = WindowsGraphicsFrameSource.SystemNow - frame.SystemTimestamp;
        if (frame.WindowHandle != target.Handle || frame.ProcessId != target.ProcessId)
            throw new InvalidOperationException("观察帧来自其他游戏窗口，已停止输入。");
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(1)
            || geometry.X != frame.ClientLeft || geometry.Y != frame.ClientTop || geometry.Width != frame.Width || geometry.Height != frame.Height)
            throw new InputUnavailableException("观察帧或窗口位置已变化，等待重新观察。");
        if (point.X < 0 || point.Y < 0 || point.X >= frame.Width || point.Y >= frame.Height)
            throw new InvalidOperationException("点击位置超出游戏客户区。");
        if (NativeMethods.GetForegroundWindow() != target.Handle)
            throw new InputUnavailableException("游戏不在前台，等待切回游戏。");
        var desktop = new NativeMethods.Point { X = geometry.X + point.X, Y = geometry.Y + point.Y };
        if (GetAncestor(WindowFromPoint(desktop), 2) != target.Handle)
            throw new InputUnavailableException("目标位置被其他窗口遮挡，等待恢复。");
        if (!GetClipCursor(out var clip))
            throw new InvalidOperationException("无法检查鼠标可移动范围，未点击。");
        if (desktop.X < clip.Left || desktop.X >= clip.Right || desktop.Y < clip.Top || desktop.Y >= clip.Bottom)
            throw new InputUnavailableException("游戏暂时限制鼠标移动范围，等待目标可点击。");
        token.ThrowIfCancellationRequested();
        if (NativeMethods.GetForegroundWindow() != target.Handle || target.Observe() != geometry)
            throw new InputUnavailableException("点击前窗口状态变化，等待重新观察。");
        var move = MouseEvent(0x8000 | 0x4000 | 0x0001);
        move.Value.Mouse.X = (int)((long)(desktop.X - GetSystemMetrics(76)) * 65535 / Math.Max(1, GetSystemMetrics(78) - 1));
        move.Value.Mouse.Y = (int)((long)(desktop.Y - GetSystemMetrics(77)) * 65535 / Math.Max(1, GetSystemMetrics(79) - 1));
        token.ThrowIfCancellationRequested();
        if (!GetClipCursor(out clip))
            throw new InvalidOperationException("无法检查鼠标可移动范围，未点击。");
        if (NativeMethods.GetForegroundWindow() != target.Handle || target.Observe() != geometry
            || GetAncestor(WindowFromPoint(desktop), 2) != target.Handle
            || desktop.X < clip.Left || desktop.X >= clip.Right || desktop.Y < clip.Top || desktop.Y >= clip.Bottom
            || WindowsGraphicsFrameSource.SystemNow - frame.SystemTimestamp > TimeSpan.FromSeconds(1))
            throw new InputUnavailableException("按下鼠标前窗口或观察已失效，等待重新观察。");
        try
        {
            // A single SendInput batch keeps physical mouse movement out of the move/down gap.
            var sent = SendInput(2, [move, MouseEvent(0x0002)], Marshal.SizeOf<Input>());
            _leftHeld = sent == 2;
            if (!_leftHeld) throw new InvalidOperationException($"Windows 拒绝鼠标输入（错误 {Marshal.GetLastWin32Error()}），请检查助手与游戏的运行权限。");
            // Let the game observe a press on at least one update; cancellation still releases in finally.
            await Task.Delay(60, token);
        }
        finally { ReleaseAll(); }
    }

    public async Task ClickCurrentAsync(FrameSnapshot frame, CancellationToken token)
    {
        RequireNoContinuousHold();
        token.ThrowIfCancellationRequested();
        var target = ValidateObservation(frame);
        if (!GetCursorPos(out var pointer)) throw new InvalidOperationException("无法读取鼠标当前位置，未点击。");
        if (pointer.X < frame.ClientLeft || pointer.X >= frame.ClientLeft + frame.Width
            || pointer.Y < frame.ClientTop || pointer.Y >= frame.ClientTop + frame.Height
            || GetAncestor(WindowFromPoint(pointer), 2) != target.Handle)
            throw new InputUnavailableException("鼠标不在游戏客户区或被遮挡，已停止输入。");
        token.ThrowIfCancellationRequested();
        ValidateObservation(frame);
        try
        {
            _leftHeld = SendInput(1, [MouseEvent(0x0002)], Marshal.SizeOf<Input>()) == 1;
            if (!_leftHeld) throw new InvalidOperationException("Windows 拒绝鼠标输入，请检查运行权限。");
            await Task.Delay(60, token);
        }
        finally { ReleaseAll(); }
    }

    private GameWindow ValidateObservation(FrameSnapshot frame)
    {
        var target = _target ?? throw new InvalidOperationException("必须先激活游戏窗口。");
        var geometry = target.Observe();
        var age = WindowsGraphicsFrameSource.SystemNow - frame.SystemTimestamp;
        if (frame.WindowHandle != target.Handle || frame.ProcessId != target.ProcessId)
            throw new InvalidOperationException("观察帧来自其他游戏窗口，已停止输入。");
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(1)
            || geometry.X != frame.ClientLeft || geometry.Y != frame.ClientTop || geometry.Width != frame.Width || geometry.Height != frame.Height
            || NativeMethods.GetForegroundWindow() != target.Handle)
            throw new InputUnavailableException("输入前观察帧或游戏前台已失效，请重新开始。");
        return target;
    }

    public async Task DragAsync(FrameSnapshot frame, ClientPoint from, ClientPoint to, CancellationToken token)
    {
        RequireNoContinuousHold();
        token.ThrowIfCancellationRequested();
        Input Move(ClientPoint point)
        {
            var target = ValidateObservation(frame);
            if (point.X < 0 || point.Y < 0 || point.X >= frame.Width || point.Y >= frame.Height)
                throw new ArgumentOutOfRangeException(nameof(point));
            var desktop = new NativeMethods.Point { X = frame.ClientLeft + point.X, Y = frame.ClientTop + point.Y };
            if (!GetClipCursor(out var clip)) throw new InvalidOperationException("无法检查拖动范围。");
            if (GetAncestor(WindowFromPoint(desktop), 2) != target.Handle || desktop.X < clip.Left
                || desktop.X >= clip.Right || desktop.Y < clip.Top || desktop.Y >= clip.Bottom)
                throw new InputUnavailableException("拖动路径被遮挡或超出鼠标范围。");
            var move = MouseEvent(0x8000 | 0x4000 | 0x0001);
            move.Value.Mouse.X = (int)((long)(desktop.X - GetSystemMetrics(76)) * 65535 / Math.Max(1, GetSystemMetrics(78) - 1));
            move.Value.Mouse.Y = (int)((long)(desktop.Y - GetSystemMetrics(77)) * 65535 / Math.Max(1, GetSystemMetrics(79) - 1));
            return move;
        }
        Move(to);
        var first = Move(from);
        token.ThrowIfCancellationRequested();
        ValidateObservation(frame);
        try
        {
            _leftHeld = SendInput(2, [first, MouseEvent(0x0002)], Marshal.SizeOf<Input>()) == 2;
            if (!_leftHeld) throw new InvalidOperationException("Windows 拒绝拖动输入。");
            for (var i = 1; i <= 8; i++)
            {
                await Task.Delay(40, token);
                var progress = i / 8d;
                var eased = 1 - Math.Pow(1 - progress, 3);
                var point = new ClientPoint(from.X + (int)Math.Round((to.X - from.X) * eased),
                    from.Y + (int)Math.Round((to.Y - from.Y) * eased));
                if (SendInput(1, [Move(point)], Marshal.SizeOf<Input>()) != 1)
                    throw new InvalidOperationException("Windows 拒绝拖动移动。");
            }
            // Slow down before release, then hold still so inertial lists do not skip the next visible items.
            for (var hold = 0; hold < 3; hold++)
            {
                await Task.Delay(40, token);
                Move(to);
            }
        }
        catch (InputUnavailableException error) when (_leftHeld)
        { throw new InvalidOperationException("拖动中窗口或观察失效，已停止后续移动。", error); }
        finally { ReleaseAll(); }
    }

    public async Task PressKeyAsync(FrameSnapshot frame, GameKey key, CancellationToken token)
    {
        RequireNoContinuousHold();
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(key)) throw new ArgumentOutOfRangeException(nameof(key));
        var target = _target ?? throw new InvalidOperationException("必须先激活游戏窗口。");
        var geometry = target.Observe();
        var age = WindowsGraphicsFrameSource.SystemNow - frame.SystemTimestamp;
        if (frame.WindowHandle != target.Handle || frame.ProcessId != target.ProcessId)
            throw new InvalidOperationException("观察帧来自其他游戏窗口，已停止输入。");
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(1)
            || geometry.X != frame.ClientLeft || geometry.Y != frame.ClientTop || geometry.Width != frame.Width || geometry.Height != frame.Height
            || NativeMethods.GetForegroundWindow() != target.Handle)
            throw new InputUnavailableException("按键前观察帧或游戏前台已失效，等待重新观察。");
        var scan = checked((ushort)MapVirtualKey((uint)key, 0));
        if (scan == 0) throw new InvalidOperationException("无法映射游戏按键。");
        token.ThrowIfCancellationRequested();
        if (NativeMethods.GetForegroundWindow() != target.Handle || target.Observe() != geometry
            || WindowsGraphicsFrameSource.SystemNow - frame.SystemTimestamp > TimeSpan.FromSeconds(1))
            throw new InputUnavailableException("按键前窗口或观察已变化，等待重新观察。");
        try
        {
            if (SendInput(1, [KeyboardEvent(scan, false)], Marshal.SizeOf<Input>()) != 1)
                throw new InvalidOperationException("Windows 拒绝键盘输入，请检查运行权限。");
            _heldScans.Add(scan);
            await Task.Delay(60, token);
        }
        finally { ReleaseAll(); }
    }

    public async Task HoldKeyAsync(FrameSnapshot frame, GameKey key, TimeSpan duration, CancellationToken token)
    {
        RequireNoContinuousHold();
        if (key is not (GameKey.Forward or GameKey.Left or GameKey.Backward or GameKey.Right)
            || duration < TimeSpan.FromMilliseconds(40) || duration > TimeSpan.FromMilliseconds(400))
            throw new ArgumentOutOfRangeException(nameof(duration), "短步只允许 WASD，时长 40–400ms。");
        token.ThrowIfCancellationRequested();
        ValidateObservation(frame);
        if (WindowsGraphicsFrameSource.SystemNow - frame.SystemTimestamp > TimeSpan.FromMilliseconds(500))
            throw new InputUnavailableException("短步移动需要 500ms 内的新观察。");
        var scan = checked((ushort)MapVirtualKey((uint)key, 0));
        if (scan == 0) throw new InvalidOperationException("无法映射移动按键。");
        try
        {
            if (SendInput(1, [KeyboardEvent(scan, false)], Marshal.SizeOf<Input>()) != 1)
                throw new InvalidOperationException("Windows 拒绝移动按键。");
            _heldScans.Add(scan);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < duration)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp((duration - clock.Elapsed).TotalMilliseconds, 1, 25)), token);
                ValidateObservation(frame);
            }
        }
        catch (InputUnavailableException ex) when (_heldScans.Count > 0)
        { throw new InvalidOperationException("移动期间前台或观察失效，已释放按键。", ex); }
        finally { ReleaseAll(); }
    }

    public Task MovePointerRelativeAsync(FrameSnapshot frame, int horizontal, int vertical, CancellationToken token)
    {
        if (horizontal is < -240 or > 240 || vertical is < -240 or > 240 || (horizontal == 0 && vertical == 0))
            throw new ArgumentOutOfRangeException(nameof(horizontal), "单次相对鼠标移动必须在240单位内且非零。");
        token.ThrowIfCancellationRequested();
        ValidateObservation(frame);
        if (WindowsGraphicsFrameSource.SystemNow - frame.SystemTimestamp > TimeSpan.FromMilliseconds(500))
            throw new InputUnavailableException("转向需要500ms内的新观察。");
        var movement = MouseEvent(0x0001); movement.Value.Mouse.X = horizontal; movement.Value.Mouse.Y = vertical;
        token.ThrowIfCancellationRequested();
        ValidateObservation(frame);
        if (SendInput(1, [movement], Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException("Windows拒绝相对鼠标移动。");
        return Task.CompletedTask;
    }

    public Task<IContinuousKeyHold> BeginKeyHoldAsync(FrameSnapshot frame, GameKey key, CancellationToken token)
    {
        if (key != GameKey.Forward) throw new ArgumentOutOfRangeException(nameof(key), "持续移动当前只允许W。");
        token.ThrowIfCancellationRequested();
        lock (_holdGate)
        {
            RequireNoContinuousHold();
            ValidateHoldFrame(frame);
            if (_heldScans.Count != 0 || _leftHeld) throw new InvalidOperationException("开始长按前必须释放原有输入。");
            var scan = checked((ushort)MapVirtualKey((uint)key, 0));
            if (scan == 0 || SendInput(1, [KeyboardEvent(scan, false)], Marshal.SizeOf<Input>()) != 1)
                throw new InvalidOperationException("Windows拒绝持续移动按键。");
            _heldScans.Add(scan);
            try
            {
                var hold = new ContinuousHold(this, frame, token);
                _continuous = hold;
                hold.Start();
                return Task.FromResult<IContinuousKeyHold>(hold);
            }
            catch { ReleaseAll(); throw; }
        }
    }
    private void ValidateHoldFrame(FrameSnapshot frame)
    {
        ValidateObservation(frame);
        if (WindowsGraphicsFrameSource.SystemNow - frame.SystemTimestamp > TimeSpan.FromMilliseconds(500))
            throw new InputUnavailableException("长按续期需要500ms内的新定位画面。");
    }
    private void RequireNoContinuousHold()
    {
        lock (_holdGate)
            if (_continuous is not null) throw new InvalidOperationException("其他按键或点击前必须结束持续移动。");
    }
    private sealed class ContinuousHold(WindowsGameInput owner, FrameSnapshot frame, CancellationToken token) : IContinuousKeyHold
    {
        private FrameSnapshot _frame = frame;
        private bool _stopped;
        private Exception? _fault;
        public void Start() => _ = Task.Run(WatchAsync);
        public Task RefreshAsync(FrameSnapshot observation, CancellationToken cancellation)
        {
            lock (owner._holdGate)
            {
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    token.ThrowIfCancellationRequested();
                    if (_fault is not null) throw new InputUnavailableException(_fault.Message);
                    if (_stopped || owner._continuous != this) throw new ObservationInvalidatedException();
                    owner.ValidateHoldFrame(observation);
                    if (observation.WindowHandle != _frame.WindowHandle || observation.ProcessId != _frame.ProcessId
                        || observation.ViewportVersion != _frame.ViewportVersion || observation.SystemTimestamp <= _frame.SystemTimestamp)
                        throw new InputUnavailableException("持续移动必须由同一视口的新定位帧续期。");
                    _frame = observation;
                    return Task.CompletedTask;
                }
                catch { if (owner._continuous == this) owner.ReleaseAll(); throw; }
            }
        }
        private async Task WatchAsync()
        {
            while (true)
            {
                await Task.Delay(20).ConfigureAwait(false);
                lock (owner._holdGate)
                {
                    if (_stopped || owner._continuous != this) return;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        owner.ValidateObservation(_frame);
                        if (WindowsGraphicsFrameSource.SystemNow - _frame.SystemTimestamp > TimeSpan.FromMilliseconds(800))
                            throw new InputUnavailableException("持续移动未收到新定位，已自动松开W。");
                    }
                    catch (Exception error)
                    {
                        _fault = error;
                        try { owner.ReleaseAll(); } catch (Exception releaseError) { _fault = releaseError; }
                        return;
                    }
                }
            }
        }
        public void Stop() => _stopped = true;
        public void Dispose() { lock (owner._holdGate) { if (owner._continuous == this) owner.ReleaseAll(); } }
    }
    public void ReleaseAll()
    {
        lock (_holdGate)
        {
            _continuous?.Stop();
            _continuous = null;
            ReleasePressedInputs();
        }
    }
    private void ReleasePressedInputs()
    {
        var releases = new List<Input>();
        if (_leftHeld) releases.Add(MouseEvent(0x0004));
        releases.AddRange(_heldScans.Select(scan => KeyboardEvent(scan, true)));
        if (releases.Count == 0) return;
        if (SendInput((uint)releases.Count, releases.ToArray(), Marshal.SizeOf<Input>()) != releases.Count)
            throw new InvalidOperationException("键鼠释放失败，请手动松开按键并停止功能。");
        _leftHeld = false;
        _heldScans.Clear();
    }
    private static Input KeyboardEvent(ushort scan, bool release) => new()
    { Type = 1, Value = new InputUnion { Keyboard = new KeyboardInput { Scan = scan, Flags = 0x0008u | (release ? 0x0002u : 0) } } };
    private static Input MouseEvent(uint flags) => new() { Type = 0, Value = new InputUnion { Mouse = new MouseInput { Flags = flags } } };
}
