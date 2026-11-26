using System.Diagnostics;
using AzurAssistant.Contracts;
using AzurAssistant.Game.Interaction;

namespace AzurAssistant.Game.Navigation;

/// <summary>Handles the selected collectible while stationary at a verified waypoint.</summary>
public sealed class NavigationPickup(TaskContext context, NavigationSession session, IPickupRecognizer recognizer)
{
    private static readonly TimeSpan ToolActionWait = TimeSpan.FromSeconds(10);

    public async Task CollectAsync(MapPosition target, CancellationToken token)
    {
        string? previous = null, pending = null;
        var previousCheckClear = false;
        var gatherInterval = TimeSpan.FromMilliseconds(250);
        var nextCheck = context.Execution.Elapsed;
        var confirmedAt = TimeSpan.Zero;
        var generation = context.Execution.Generation;
        using var deadline = context.Execution.CreateDeadline(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            while (true)
            {
                var remaining = nextCheck - context.Execution.Elapsed;
                if (remaining > TimeSpan.Zero) await context.Execution.DelayAsync(remaining, deadline.Token);
                nextCheck = context.Execution.Elapsed + gatherInterval;
                var observeAfter = Stopwatch.GetElapsedTime(0);
                var current = await session.WaitAsync(o => o.Frame.SystemTimestamp > observeAfter, "到点采集观察", deadline.Token,
                    timeoutSeconds: 30, stableMilliseconds: 0, minimapOnly: true);
                if (generation != context.Execution.Generation)
                {
                    generation = context.Execution.Generation;
                    previous = null;
                    previousCheckClear = false;
                }
                var at = current.Frame.SystemTimestamp;
                if (current.Position is not { } position)
                {
                    previous = null;
                    previousCheckClear = false;
                    continue;
                }
                if (!position.SameMap(target) || position.DistanceTo(target) > 4)
                    throw new NavigationException("已离开路线采集点，停止本次采集。");
                var observed = await recognizer.ReadAsync(current.Frame, deadline.Token, verifyAbsence: true);
                if (!context.Execution.IsObservationCurrent(current.Frame)) throw new ObservationInvalidatedException();
                if (observed.Blocked || !NavigationSession.Fresh(current.Frame))
                {
                    previous = null;
                    previousCheckClear = false;
                    continue;
                }
                if (observed.Candidate is not { } item)
                {
                    previous = null;
                    if (previousCheckClear)
                    {
                        context.Log("到点采集结束：连续两次检查均确认大世界操作提示可见，F 和交互行已消失");
                        return;
                    }
                    previousCheckClear = true;
                    continue;
                }
                previousCheckClear = false;
                if (pending == item.Key) { previous = null; continue; }
                if (previous != item.Key) { previous = item.Key; confirmedAt = at; continue; }
                if (at - confirmedAt < TimeSpan.FromMilliseconds(150)) continue;
                await context.Input.PressKeyAsync(current.Frame, GameKey.Interact, deadline.Token);
                if (item.Kind is PickupKind.Logging or PickupKind.Mining)
                {
                    var action = item.Kind == PickupKind.Logging ? "砍树" : "挖矿";
                    context.Log($"到点{action}：已对「{item.Name}」发送 F，原地等待 {ToolActionWait.TotalSeconds} 秒");
                    // The prompt disappears when the animation starts. Only the route token bounds this wait.
                    await context.Execution.DelayAsync(ToolActionWait, token);
                    context.Log($"到点{action}：已等满 {ToolActionWait.TotalSeconds} 秒，继续路线");
                    return;
                }
                pending = item.Key;
                previous = null;
                context.Log($"到点拾取：已对「{item.Name}」发送 F，每 250 毫秒检查一次交互界面");
                nextCheck = context.Execution.Elapsed + gatherInterval;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new NavigationException(pending is null ? "到点采集未能确认交互状态，路线已停止。" : "到点采集后未能确认 F 提示消失，路线已停止。");
        }
    }
}
