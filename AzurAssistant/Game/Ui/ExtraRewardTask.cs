using System.Diagnostics;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Game.Ui;

/// <summary>Handles item showcases within the active task's input lease, then resumes observation on a newer frame.</summary>
internal sealed class ExtraRewardTask(IGameTask task, TemplateCatalog templates, TimeSpan? poll = null,
    TimeSpan stableDuration = default, TimeSpan afterClick = default) : IGameTask
{
    public string Id => task.Id;
    public string Name => task.Name;

    public Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
        => task.ExecuteAsync(context with { Frames = new RewardFrames(context, templates, poll ?? TimeSpan.FromMilliseconds(160), stableDuration, afterClick) }, token);

    internal static ClientPoint? FindReturn(TemplateCatalog templates, FrameSnapshot frame) => Inspect(templates, frame)?.Point;

    private sealed record Showcase(ClientPoint Point, bool Equip);
    private static Showcase? Inspect(TemplateCatalog templates, FrameSnapshot frame)
    {
        var back = templates.Match(frame, "extra-return");
        if (back is null) return null;
        var change = templates.Match(frame, "extra-change");
        var equip = templates.Match(frame, "extra-equip");
        if ((change is null) == (equip is null)) return null;
        var option = change ?? equip;
        return option is not null && option.Center.X > back.Center.X && Math.Abs(option.Center.Y - back.Center.Y) <= 10
            ? new Showcase(back.Center, equip is not null) : null;
    }

    internal static bool ReturnedAfter(TaskContext context, FrameSnapshot origin) =>
        context.Frames is RewardFrames frames && frames.LastReturn is { } receipt &&
        receipt.ClickedAt > origin.SystemTimestamp && receipt.Window == origin.WindowHandle &&
        receipt.Process == origin.ProcessId && receipt.Generation == context.Execution.Generation;

    private sealed record ReturnReceipt(TimeSpan ClickedAt, nint Window, int Process, int Generation);

    private sealed class RewardFrames(TaskContext context, TemplateCatalog templates, TimeSpan pollInterval,
        TimeSpan stableDuration, TimeSpan afterClick) : IFrameFeed
    {
        public ReturnReceipt? LastReturn { get; private set; }

        public async Task<FrameSnapshot?> ReadForInputCheckAsync(TimeSpan timestamp, CancellationToken token)
        {
            var frame = await context.Frames.ReadForInputCheckAsync(timestamp, token);
            return frame is { Width: 1920, Height: 1080 } && Inspect(templates, frame) is null ? frame : null;
        }

        public async Task<FrameSnapshot> ReadAfterAsync(TimeSpan timestamp, CancellationToken token)
        {
            var first = await context.Frames.ReadAfterAsync(timestamp, token);
            if (first.Width != 1920 || first.Height != 1080 || Inspect(templates, first) is not { } initial) return first;
            context.Input.ReleaseAll();
            using var deadline = context.Execution.CreateDeadline(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5) + afterClick * 2);
            var after = first.SystemTimestamp;
            var previous = first;
            Showcase? lastPanel = Fresh(first) && context.Execution.IsObservationCurrent(first) ? initial : null;
            var sawClear = false;
            var stableSince = first.SystemTimestamp;
            var readyAt = TimeSpan.Zero;
            var generation = context.Execution.Generation;
            ReturnReceipt? clicked = null;
            bool? clickedEquip = null;
            try
            {
                while (true)
                {
                    await context.Execution.DelayAsync(pollInterval, deadline.Token);
                    var frame = await context.Frames.ReadAfterAsync(after, deadline.Token);
                    if (frame.SystemTimestamp <= after || frame.Id <= previous.Id) continue;
                    after = frame.SystemTimestamp;
                    if (frame.WindowHandle != first.WindowHandle || frame.ProcessId != first.ProcessId)
                        throw new InvalidOperationException("特殊奖励展示期间游戏窗口已更换。");
                    var currentGeneration = context.Execution.Generation;
                    var stable = currentGeneration == generation && previous.SameViewport(frame);
                    previous = frame;
                    generation = currentGeneration;
                    if (frame.Width != 1920 || frame.Height != 1080 || !Fresh(frame) || !context.Execution.IsObservationCurrent(frame))
                    {
                        lastPanel = null;
                        sawClear = false;
                        continue;
                    }
                    var next = Inspect(templates, frame);
                    var samePanel = stable && next is not null && lastPanel is not null && next.Equip == lastPanel.Equip
                        && Math.Abs(lastPanel.Point.X - next.Point.X) <= 4 && Math.Abs(lastPanel.Point.Y - next.Point.Y) <= 4;
                    if (!samePanel) stableSince = frame.SystemTimestamp;
                    if (next is null)
                    {
                        if (clicked is null) return frame;
                        if (sawClear && stable && context.Execution.Elapsed >= readyAt)
                        {
                            if (clicked.Window == frame.WindowHandle && clicked.Process == frame.ProcessId && clicked.Generation == generation)
                                LastReturn = clicked;
                            return frame;
                        }
                    }
                    else if (samePanel && clickedEquip != next.Equip && frame.SystemTimestamp - stableSince >= stableDuration
                        && context.Execution.Elapsed >= readyAt)
                    {
                        try
                        {
                            await context.Input.ClickAsync(frame, next.Point, deadline.Token);
                            after = Stopwatch.GetElapsedTime(0);
                            clicked = new(after, frame.WindowHandle, frame.ProcessId, generation);
                            clickedEquip = next.Equip;
                            readyAt = context.Execution.Elapsed + afterClick;
                            context.Log("已点击特殊奖励的返回按钮，等待奖励展示关闭");
                        }
                        catch (ObservationInvalidatedException) { lastPanel = null; continue; }
                    }
                    sawClear = next is null;
                    lastPanel = next;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.Token.IsCancellationRequested)
            { throw new InvalidOperationException("特殊奖励展示未确认关闭，停止本次操作。"); }
        }

        private static bool Fresh(FrameSnapshot frame) => Stopwatch.GetElapsedTime(0) - frame.SystemTimestamp is var age &&
            age >= TimeSpan.Zero && age <= TimeSpan.FromMilliseconds(500);

    }
}
