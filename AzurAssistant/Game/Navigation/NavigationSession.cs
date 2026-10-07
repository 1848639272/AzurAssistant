using System.Diagnostics;
using AzurAssistant.Contracts;

namespace AzurAssistant.Game.Navigation;

public enum NavigationStability { Page, Position, Stationary, Facing, Target, Registration }

/// <summary>Fresh/stable map observations and guarded actions in the caller's existing task session.</summary>
public sealed class NavigationSession(TaskContext context, INavigationRecognizer recognizer,
    Action<NavigationObservation>? report = null)
{
    private TimeSpan _after = Now;
    private long _lastId;
    private FrameSnapshot? _identity;
    private MovementMode _movementMode;
    private long _modeGeneration = -1;
    private TimeSpan? _modeConfirmedAt;
    private static TimeSpan Now => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    public static TimeSpan Age(FrameSnapshot frame) => Now - frame.SystemTimestamp;
    public static bool Fresh(FrameSnapshot frame, double milliseconds = 500) => Age(frame) is var age
        && age >= TimeSpan.Zero && age < TimeSpan.FromMilliseconds(milliseconds);

    public async Task<NavigationObservation> WaitAsync(Func<NavigationObservation, bool> accept, string step,
        CancellationToken token, double timeoutSeconds = 12, double stableMilliseconds = 300,
        NavigationStability stability = NavigationStability.Position, bool minimapOnly = false, bool teleportOnly = false)
    {
        using var deadline = context.Execution.CreateDeadline(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        NavigationObservation? previous = null;
        var stableAt = TimeSpan.Zero;
        var generation = context.Execution.Generation;
        var diagnostic = "尚无新帧";
        try
        {
            while (true)
            {
                await context.Execution.WaitAsync(deadline.Token);
                if (generation != context.Execution.Generation)
                { previous = null; _after = Now; generation = context.Execution.Generation; }
                var frame = await context.Frames.ReadAfterAsync(_after, deadline.Token);
                if (!AcceptFrame(frame))
                { previous = null; await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(30), deadline.Token); continue; }
                var observation = minimapOnly ? await recognizer.ObserveMinimapAsync(frame, deadline.Token)
                    : teleportOnly ? await recognizer.ObserveTeleportAsync(frame, deadline.Token)
                    : await recognizer.ObserveAsync(frame, deadline.Token);
                await context.Execution.WaitAsync(deadline.Token);
                if (!context.Execution.IsObservationCurrent(frame) || !Fresh(frame))
                { diagnostic = $"识别后画面过期，帧龄 {(Now - frame.SystemTimestamp).TotalMilliseconds:F0}ms"; previous = null; continue; }
                report?.Invoke(observation);
                diagnostic = $"{observation.Page}；{observation.Diagnostic}；位置{(observation.Position is null ? "未知" : "有效")}；节点{(observation.TeleportIcon is null ? "未见" : "可见")}";
                if (!accept(observation)) { previous = null; continue; }
                if (stableMilliseconds <= 0) return observation;
                if (previous is null || !Same(previous, observation, stability))
                { previous = observation; stableAt = frame.SystemTimestamp; }
                else if (frame.SystemTimestamp - stableAt >= TimeSpan.FromMilliseconds(stableMilliseconds)) return observation;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new NavigationException($"{step}：{timeoutSeconds:0.#} 秒内未确认（{diagnostic}）"); }
    }

    private bool AcceptFrame(FrameSnapshot frame)
    {
        if (frame.Id <= _lastId || frame.SystemTimestamp <= _after || !Fresh(frame, 850)
            || !context.Execution.IsObservationCurrent(frame)) return false;
        _identity ??= frame;
        if ((_identity.WindowHandle, _identity.ProcessId, _identity.ViewportVersion, _identity.ClientLeft, _identity.ClientTop,
            _identity.Width, _identity.Height) != (frame.WindowHandle, frame.ProcessId, frame.ViewportVersion, frame.ClientLeft,
            frame.ClientTop, frame.Width, frame.Height)) throw new InputUnavailableException("导航期间窗口或视口改变，请重新开始定位。");
        _after = frame.SystemTimestamp; _lastId = frame.Id;
        return true;
    }

    public async Task ActAsync(NavigationObservation source, Func<NavigationObservation, bool> accept,
        Func<NavigationObservation, CancellationToken, Task> action, string step, CancellationToken token,
        NavigationStability stability = NavigationStability.Target, bool teleportOnly = false, double stableMilliseconds = 300)
    {
        await context.Execution.WaitAsync(token);
        if (!Fresh(source.Frame) || !context.Execution.IsObservationCurrent(source.Frame))
            source = await WaitAsync(accept, step + "：重新确认", token, stableMilliseconds: stableMilliseconds, stability: stability, teleportOnly: teleportOnly);
        if (!accept(source)) throw new NavigationException(step + "：观察条件已变化");
        try { await action(source, token); }
        catch (ObservationInvalidatedException)
        {
            source = await WaitAsync(accept, step + "：暂停后重新确认", token, stableMilliseconds: stableMilliseconds, stability: stability, teleportOnly: teleportOnly);
            await action(source, token);
        }
        // The next decision must come from a frame captured after this action has released its input.
        _after = Now;
    }

    public async Task<NavigationObservation> EnsureWorldAsync(CancellationToken token)
    {
        var current = await WaitAsync(o => o.Page != NavigationPage.Unknown, "确认导航起点", token);
        if (current.Page == NavigationPage.World && current.Position is not null) return current;
        if (current.Page == NavigationPage.LocalMap && current.Position is not null)
        {
            await ActAsync(current, o => o.Page == NavigationPage.LocalMap && o.Position is not null,
                (o, ct) => context.Input.PressKeyAsync(o.Frame, GameKey.Escape, ct), "关闭地图", token);
            return await WaitAsync(o => o.Page == NavigationPage.World && o.Position is not null, "确认世界坐标", token);
        }
        throw new NavigationException("请让角色处于已支持地图的世界画面，或打开当前角色所在的本地地图。");
    }

    public Task<NavigationObservation> LocateMinimapAsync(string step, CancellationToken token, double stableMilliseconds = 0) =>
        WaitAsync(o => o.Position is not null, step, token, 5, stableMilliseconds, NavigationStability.Stationary, minimapOnly: true);

    public async Task<NavigationObservation> PrepareRouteAsync(CancellationToken token, string? expectedMapId = null)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await context.Execution.WaitAsync(token);
                var generation = context.Execution.Generation;
                var current = await EnsureWorldAsync(token);
                if (expectedMapId is not null && current.Position!.MapId != expectedMapId)
                    throw new NavigationException("角色当前不在路线或目标坐标所属地图，请先传送到对应地图。");
                context.Log("路线启动：空格结束待机动作，等待3秒及坐标稳定后切换移动模式");
                await context.Input.PressKeyAsync(current.Frame, GameKey.Jump, token);
                _after = Now;
                await context.Execution.DelayAsync(TimeSpan.FromSeconds(3), token);
                current = await LocateMinimapAsync("跳跃落地后确认坐标稳定", token, 350);
                if (generation != context.Execution.Generation) throw new ObservationInvalidatedException();
                return current;
            }
            catch (ObservationInvalidatedException) { context.Input.ReleaseAll(); }
        }
        throw new NavigationException("路线准备连续被暂停恢复打断，请重新开始。");
    }

    public async Task<NavigationObservation> SetMovementModeAsync(MovementMode desired, CancellationToken token)
    {
        if (desired == MovementMode.Unknown) throw new ArgumentOutOfRangeException(nameof(desired));
        var generation = context.Execution.Generation;
        if (_modeGeneration != generation) { _movementMode = MovementMode.Unknown; _modeGeneration = generation; _modeConfirmedAt = null; }
        var current = await LocateMinimapAsync("切换移动状态前定位", token);
        if (_movementMode == desired) return current;
        if (_movementMode == MovementMode.Unknown)
        {
            _movementMode = await recognizer.ReadMovementModeAsync(current.Frame, token);
            if (_movementMode != MovementMode.Unknown) _modeConfirmedAt = context.Execution.Elapsed;
        }
        for (var attempt = 0; _movementMode != desired && attempt < 2; attempt++)
        {
            if (_modeConfirmedAt is { } confirmed)
            {
                var remaining = TimeSpan.FromSeconds(1) - (context.Execution.Elapsed - confirmed);
                if (remaining > TimeSpan.Zero) await context.Execution.DelayAsync(remaining, token);
            }
            if (!Fresh(current.Frame)) current = await LocateMinimapAsync("切换移动状态前更新定位", token);
            if (generation != context.Execution.Generation) throw new ObservationInvalidatedException();
            var before = _movementMode;
            context.Log("发送Ctrl切换移动状态，等待新文字结果");
            await context.Input.PressKeyAsync(current.Frame, GameKey.MovementMode, token);
            _movementMode = MovementMode.Unknown;
            _after = Now;
            await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(300), token);
            using var deadline = context.Execution.CreateDeadline(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            MovementMode previous = MovementMode.Unknown;
            var firstAt = TimeSpan.Zero;
            try
            {
                while (true)
                {
                    await context.Execution.WaitAsync(deadline.Token);
                    if (generation != context.Execution.Generation) throw new ObservationInvalidatedException();
                    // Stationary toast confirmation needs a fresh text frame, not another expensive map registration.
                    var frame = await context.Frames.ReadAfterAsync(_after, deadline.Token);
                    if (!AcceptFrame(frame))
                    { previous = MovementMode.Unknown; await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(30), deadline.Token); continue; }
                    var observed = await recognizer.ReadMovementModeAsync(frame, deadline.Token);
                    if (generation != context.Execution.Generation) throw new ObservationInvalidatedException();
                    if (!context.Execution.IsObservationCurrent(frame) || !Fresh(frame) || observed == MovementMode.Unknown || observed == before)
                    { previous = MovementMode.Unknown; continue; }
                    if (previous != observed) { previous = observed; firstAt = frame.SystemTimestamp; }
                    else if (frame.SystemTimestamp - firstAt >= TimeSpan.FromMilliseconds(150))
                    { _movementMode = observed; _modeConfirmedAt = context.Execution.Elapsed; break; }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new NavigationException("Ctrl后未确认步行/奔跑状态文字；已停止，不盲目再次切换。"); }
        }
        if (generation != context.Execution.Generation) throw new ObservationInvalidatedException();
        if (_movementMode != desired) throw new NavigationException("未确认所需移动状态，已停止。");
        context.Log(desired == MovementMode.Walk ? "文字确认步行状态，用于转向及近点校准" : "文字确认奔跑状态，开始持续前进");
        return await LocateMinimapAsync("移动状态确认后定位", token);
    }

    public async Task ConfirmArrivalAsync(MapPosition target, CancellationToken token)
    {
        var confirmed = await WaitAsync(o => o.Page == NavigationPage.World && o.Position is { } p && p.SameMap(target) && p.DistanceTo(target) <= 4,
            "路线结束：确认世界画面和到达坐标", token, 5, 650, NavigationStability.Stationary);
        context.Log($"已到达 ({confirmed.Position!.X:F1}, {confirmed.Position.Y:F1})；目标 ({target.X:F1}, {target.Y:F1})");
    }

    public IReadOnlyList<NavigationMap> Maps => recognizer.Maps;
    public Task TeleportAsync(CancellationToken token) => TeleportAsync(recognizer.Layout.MapId, recognizer.Layout.Teleport.Id, token);
    public Task TeleportAsync(string mapId, string teleportId, CancellationToken token) =>
        new MapTeleporter(context, recognizer, this).RunAsync(mapId, teleportId, token);

    private static bool Same(NavigationObservation a, NavigationObservation b, NavigationStability stability) => a.Page == b.Page && a.DisplayedMapId == b.DisplayedMapId && (stability switch
    {
        NavigationStability.Page => true,
        NavigationStability.Registration => a.Registration is not null && b.Registration is not null && SameViewport(a, b),
        NavigationStability.Target => a.SelectedName == b.SelectedName && Near(a.TeleportIcon, b.TeleportIcon)
            && Near(a.TeleportButton, b.TeleportButton) && Near(a.OverlapChoice, b.OverlapChoice)
            && Near(a.WorldRegion, b.WorldRegion) && Near(a.WorldMapButton, b.WorldMapButton) && SameViewport(a, b),
        NavigationStability.Stationary => a.Position is not null && b.Position is not null && a.Position.SameMap(b.Position) && a.Position.DistanceTo(b.Position) < .65,
        NavigationStability.Facing => a.Position is not null && b.Position is not null && a.Position.SameMap(b.Position) && a.Position.DistanceTo(b.Position) < .65
            && a.FacingDegrees is { } x && b.FacingDegrees is { } y && Math.Abs(CameraSteering.Difference(x, y)) <= 6,
        _ => (a.Position is null && b.Position is null) || (a.Position is not null && b.Position is not null && a.Position.SameMap(b.Position) && a.Position.DistanceTo(b.Position) < 2.5)
    });
    private static bool SameViewport(NavigationObservation a, NavigationObservation b)
    {
        if (a.Registration is null || b.Registration is null) return a.Registration is null && b.Registration is null;
        // Three separated client points detect zoom/rotation about a stationary center as well as panning.
        foreach (var point in new[] { new ClientPoint(160, 180), new ClientPoint(1250, 180), new ClientPoint(760, 850) })
        {
            var reference = a.Registration.Project(point.X, point.Y);
            var screen = b.Registration.Unproject(reference.X, reference.Y);
            if (Math.Abs(screen.X - point.X) >= 2 || Math.Abs(screen.Y - point.Y) >= 2) return false;
        }
        return true;
    }
    private static bool Near(ClientPoint? a, ClientPoint? b) => (a is null && b is null) || (a is { } x && b is { } y
        && Math.Abs(x.X - y.X) <= 3 && Math.Abs(x.Y - y.Y) <= 3);
}

public sealed class NavigationException(string message) : Exception(message);
