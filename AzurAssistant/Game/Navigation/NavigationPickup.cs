using AzurAssistant.Contracts;
using AzurAssistant.Game.Interaction;

namespace AzurAssistant.Game.Navigation;

/// <summary>Pauses movement for a confirmed collectible; shares the route's input lease and fresh frame source.</summary>
public sealed class NavigationPickup(TaskContext context, NavigationSession session, IPickupRecognizer recognizer)
{
    private TimeSpan _nextCheck;
    private string? _pending;
    public bool ShouldPause(FrameSnapshot frame)
    {
        return context.Execution.Elapsed >= _nextCheck && recognizer.IsPromptVisible(frame);
    }
    public async Task CollectAsync(CancellationToken token)
    {
        string? previous = null;
        var emptyFrames = 0;
        var generation = context.Execution.Generation;
        var confirmedAt = TimeSpan.Zero;
        using var deadline = context.Execution.CreateDeadline(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            for (var read = 0; read < 20; read++)
            {
                var current = await session.LocateMinimapAsync("拾取前确认当前位置", deadline.Token);
                if (generation != context.Execution.Generation)
                { generation = context.Execution.Generation; previous = null; emptyFrames = 0; }
                var observed = await recognizer.ReadAsync(current.Frame, deadline.Token);
                if (!context.Execution.IsObservationCurrent(current.Frame)) throw new ObservationInvalidatedException();
                if (!NavigationSession.Fresh(current.Frame)) { previous = null; continue; }
                if (observed.Blocked) break;
                if (observed.Candidate is not { } item)
                {
                    previous = null;
                    if (++emptyFrames >= 2) { _pending = null; break; }
                    continue;
                }
                emptyFrames = 0;
                if (item.Key == _pending) break;
                if (previous != item.Key) { previous = item.Key; confirmedAt = current.Frame.SystemTimestamp; }
                else if (current.Frame.SystemTimestamp - confirmedAt >= TimeSpan.FromMilliseconds(150))
                {
                    await context.Input.PressKeyAsync(current.Frame, GameKey.Interact, deadline.Token);
                    context.Log("路线拾取：" + (item.Name.Length == 0 ? "当前选中采集物" : item.Name));
                    _pending = item.Key; previous = null;
                    await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(500), deadline.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { context.Log("本次拾取观察达到时限，继续路线；不重复未确认结果的交互。"); }
        finally { _nextCheck = context.Execution.Elapsed + TimeSpan.FromSeconds(2); }
    }
}
