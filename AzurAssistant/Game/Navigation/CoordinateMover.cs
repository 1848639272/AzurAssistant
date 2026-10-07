using AzurAssistant.Contracts;

namespace AzurAssistant.Game.Navigation;

/// <summary>Point movement inside an already confirmed route. Only the caller owns normal world-page boundaries.</summary>
public sealed class CoordinateMover(TaskContext context, NavigationSession session, NavigationPickup? pickup = null)
{
    public async Task MoveAsync(MapPosition target, CancellationToken token, double maximumDistance = 250)
    {
        var current = await LocateAsync("点位移动起点定位", token);
        if (current.Position!.DistanceTo(target) > maximumDistance)
            throw new NavigationException("本轮只支持起点250参考像素内的短距离移动；远距离及绕障尚未接入。");
        var generation = context.Execution.Generation;
        ForwardMotion? motion = null;
        double? turnGain = null;
        var turns = 0;
        for (var step = 0; step < 60; step++)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                await context.Execution.WaitAsync(token);
                if (generation != context.Execution.Generation)
                {
                    throw new ObservationInvalidatedException();
                }
                if (current.Position!.DistanceTo(target) <= 4)
                {
                    current = await LocateAsync("停止后确认点位", token, 350);
                    if (current.Position!.DistanceTo(target) <= 4) return;
                }
                if (motion is null)
                {
                    current = await session.SetMovementModeAsync(MovementMode.Walk, token);
                    context.Log("短按W同步角色朝向，随后确认稳定箭头方向");
                    (current, motion) = await ForwardAsync(current, token);
                    continue;
                }
                var bearing = CameraSteering.Bearing(current.Position, target);
                var error = CameraSteering.Difference(bearing, motion.Heading);
                if (Math.Abs(error) > 12)
                {
                    if (++turns > 12) throw new NavigationException("转向12次仍未对准目标；已停止，请检查镜头设置或障碍。");
                    current = await session.SetMovementModeAsync(MovementMode.Walk, token);
                    var dx = turnGain is null ? 80 : CameraSteering.MouseDelta(error, turnGain.Value);
                    var previousHeading = motion.Heading;
                    var remaining = dx;
                    while (remaining != 0)
                    {
                        var chunk = Math.Clamp(remaining, -240, 240);
                        current = await FreshAsync(current, token);
                        await context.Input.MovePointerRelativeAsync(current.Frame, chunk, 0, token);
                        await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(150), token);
                        current = await LocateAsync("转向后重新定位", token);
                        if (generation != context.Execution.Generation) break;
                        remaining -= chunk;
                    }
                    if (generation != context.Execution.Generation) continue;
                    (current, motion) = await ForwardAsync(current, token, motion);
                    if (generation != context.Execution.Generation) continue;
                    var measured = CameraSteering.MeasureGain(previousHeading, motion.Heading, dx);
                    if (turnGain is { } prior && Math.Sign(prior) != Math.Sign(measured))
                        throw new NavigationException("镜头转向响应方向改变，已停止并要求重新校准。");
                    turnGain = measured;
                    context.Log($"转向后前进方向 {motion.Heading:F1}°，目标方向 {CameraSteering.Bearing(current.Position!, target):F1}°");
                    continue;
                }
                turns = 0;
                var mode = current.Position!.DistanceTo(target) > 16 ? MovementMode.Run : MovementMode.Walk;
                current = await session.SetMovementModeAsync(mode, token);
                (current, motion) = await TravelAsync(current, target, motion, turnGain, mode, token);
            }
            catch (ObservationInvalidatedException)
            {
                context.Input.ReleaseAll();
                motion = null; turnGain = null; turns = 0;
                // A resumed session is a new route boundary, not a regular intermediate waypoint.
                current = await session.PrepareRouteAsync(token, target.MapId);
                generation = context.Execution.Generation;
            }
        }
        throw new NavigationException("移动调整次数达到上限，尚未到达目标。");
    }

    private Task<NavigationObservation> LocateAsync(string step, CancellationToken token, double stable = 0) =>
        session.LocateMinimapAsync(step, token, stable);

    private async Task<NavigationObservation> FreshAsync(NavigationObservation current, CancellationToken token)
    {
        if (!context.Execution.IsObservationCurrent(current.Frame)) throw new ObservationInvalidatedException();
        if (NavigationSession.Fresh(current.Frame)) return current;
        var fresh = await LocateAsync("行动前重新定位", token);
        if (current.Position!.DistanceTo(fresh.Position!) > 2) throw new ObservationInvalidatedException();
        return fresh;
    }

    private async Task<(NavigationObservation, ForwardMotion)> ForwardAsync(NavigationObservation current, CancellationToken token,
        ForwardMotion? previousMotion = null)
    {
        current = await FreshAsync(current, token);
        var origin = current;
        var generation = context.Execution.Generation;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            current = await FreshAsync(current, token);
            await context.Input.HoldKeyAsync(current.Frame, GameKey.Forward, TimeSpan.FromMilliseconds(120), token);
            await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(120), token);
            current = await LocateAsync("短步后等待位置稳定", token, 200);
            NavigationObservation? facing = null;
            using (var deadline = context.Execution.CreateDeadline(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(1.2));
                try
                {
                    facing = await session.WaitAsync(o => o.Position is not null && o.FacingDegrees is not null,
                        "确认短步后的稳定朝向", deadline.Token, 2, 200, NavigationStability.Facing, minimapOnly: true);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            if (generation != context.Execution.Generation) throw new ObservationInvalidatedException();
            current = facing ?? await LocateAsync("朝向暂未确认，重新定位", token);
            var displacement = origin.Position!.DistanceTo(current.Position!);
            if (displacement > 35) throw new NavigationException("短步期间坐标发生不可信跳变，已停止。");
            if (facing?.FacingDegrees is { } heading)
            {
                // Heading confirmation does not require travel. Speed is updated from actual continuous motion later.
                var motion = new ForwardMotion(CameraSteering.Difference(heading - 90, 0), previousMotion?.PixelsPerMillisecond ?? .015);
                context.Log($"短步朝向确认：累计位移 {displacement:F2}px，前进方向 {motion.Heading:F1}°");
                return (current, motion);
            }
            if (displacement >= 1.2)
            {
                var measured = ForwardMotion.Measure(origin.Position!, current.Position!, (attempt + 1) * 120);
                context.Log($"箭头暂未确认，使用累计步行位移 {displacement:F2}px 确认方向 {measured.Heading:F1}°");
                return (current, measured);
            }
            if (attempt == 0) context.Log("箭头持续不清晰且位移不足以判断方向，重新定位后仅补一次短步同步");
        }
        throw new NavigationException("两次短步后箭头方向仍不清晰，累计位移也不足以确认方向；已停止，未判定为障碍。");
    }

    private async Task<(NavigationObservation, ForwardMotion)> TravelAsync(NavigationObservation current, MapPosition target,
        ForwardMotion motion, double? gain, MovementMode mode, CancellationToken token)
    {
        current = await FreshAsync(current, token);
        var generation = context.Execution.Generation;
        var checkpoint = current;
        var progressAt = current.Frame.SystemTimestamp;
        var bestDistance = current.Position!.DistanceTo(target);
        var previous = current;
        var lastTurn = current.Frame.SystemTimestamp;
        var loggedAt = current.Frame.SystemTimestamp;
        var speed = mode == MovementMode.Walk ? Math.Min(15, motion.PixelsPerMillisecond * 1000) : motion.PixelsPerMillisecond * 1000;
        using (var hold = await context.Input.BeginKeyHoldAsync(current.Frame, GameKey.Forward, token))
        {
            while (true)
            {
                await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(70), token);
                current = await session.WaitAsync(_ => true, "持续前进定位", token, 1, 0, minimapOnly: true);
                if (generation != context.Execution.Generation) throw new ObservationInvalidatedException();
                if (current.Position is null) throw new NavigationException("行进小地图定位丢失，已松开W；请重新定位后继续。");
                var dt = (current.Frame.SystemTimestamp - previous.Frame.SystemTimestamp).TotalSeconds;
                if (dt <= 0 || current.Position.DistanceTo(previous.Position!) > Math.Max(8, dt * 180))
                    throw new NavigationException("行进坐标发生不可信跳变，已停止。");
                var distance = current.Position.DistanceTo(target);
                if (distance < bestDistance - .8) { bestDistance = distance; progressAt = current.Frame.SystemTimestamp; }
                if (current.Frame.SystemTimestamp - progressAt > TimeSpan.FromSeconds(1.8))
                    throw new NavigationException("持续前进未接近目标，可能被障碍阻挡，已松开W。");
                var travelled = checkpoint.Position!.DistanceTo(current.Position);
                var elapsed = (current.Frame.SystemTimestamp - checkpoint.Frame.SystemTimestamp).TotalSeconds;
                if (travelled >= 2.5 && elapsed >= .2)
                {
                    speed = travelled / elapsed;
                    motion = new(CameraSteering.Bearing(checkpoint.Position, current.Position), speed / 1000);
                    checkpoint = current;
                }
                // Release once for the final walking approach; normal observation never lifts and presses W again.
                var observationLag = Math.Max(0, NavigationSession.Age(current.Frame).TotalSeconds);
                if (distance <= (mode == MovementMode.Run ? Math.Max(12, speed * (.25 + observationLag))
                    : Math.Max(2.5, speed * (.12 + observationLag)))) break;
                if (pickup?.ShouldPause(current.Frame) == true) break;
                await hold.RefreshAsync(current.Frame, token);
                var error = CameraSteering.Difference(CameraSteering.Bearing(current.Position, target), motion.Heading);
                if (Math.Abs(error) > 40) break;
                if (gain is { } response && Math.Abs(error) > 5 && current.Frame.SystemTimestamp - lastTurn >= TimeSpan.FromMilliseconds(400))
                {
                    var dx = Math.Clamp((int)Math.Round(Math.Clamp(error, -15, 15) / response * .5), -120, 120);
                    if (dx != 0)
                    {
                        await context.Input.MovePointerRelativeAsync(current.Frame, dx, 0, token);
                        lastTurn = current.Frame.SystemTimestamp;
                        checkpoint = current;
                    }
                }
                if (current.Frame.SystemTimestamp - loggedAt >= TimeSpan.FromSeconds(1))
                {
                    context.Log($"持续前进：({current.Position.X:F1}, {current.Position.Y:F1})，距目标 {distance:F1}px");
                    loggedAt = current.Frame.SystemTimestamp;
                }
                previous = current;
            }
        }
        if (pickup?.ShouldPause(current.Frame) == true) await pickup.CollectAsync(token);
        current = await LocateAsync("松开W后重新定位", token, 250);
        return (current, motion);
    }
}

public sealed record ForwardMotion(double Heading, double PixelsPerMillisecond)
{
    public static ForwardMotion Measure(MapPosition before, MapPosition after, int milliseconds)
    {
        var distance = before.DistanceTo(after);
        if (distance is < 1.2 or > 35)
            throw new NavigationException("W前进未得到可靠位移，可能被阻挡或发生位置跳变；已停止。");
        return new(CameraSteering.Bearing(before, after), distance / milliseconds);
    }
}

public static class CameraSteering
{
    public static double Difference(double target, double current) => (target - current + 540) % 360 - 180;
    public static double Bearing(MapPosition from, MapPosition to)
    { _ = from.DistanceTo(to); return Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI; }
    public static double MeasureGain(double before, double after, int delta)
    {
        var change = Difference(after, before); var gain = change / delta;
        if (!double.IsFinite(gain) || Math.Abs(change) < 3 || Math.Abs(change) > 120 || Math.Abs(gain) is < .03 or > 2)
            throw new NavigationException("未测得可靠镜头转向响应；已停止，不继续盲转。");
        return gain;
    }
    public static int MouseDelta(double error, double gain)
    {
        // One measured turn covers at most 90 degrees; the caller splits it into fresh, bounded platform inputs.
        var value = Math.Clamp(error, -90, 90) / gain * .85;
        return Math.Sign(value) * Math.Clamp((int)Math.Round(Math.Abs(value)), 8, 2400);
    }
}
