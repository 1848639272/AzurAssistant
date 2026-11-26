using System.Diagnostics;
using AzurAssistant.Contracts;
using AzurAssistant.Game.Interaction;

namespace AzurAssistant.Features.Realtime;

public sealed record RealtimeTiming(TimeSpan PollInterval, TimeSpan DialogueCooldown,
    TimeSpan PickupCooldown, TimeSpan ResultTimeout)
{
    public static TimeSpan DoubleClickGap { get; } = TimeSpan.FromMilliseconds(30);
    public TimeSpan ConfirmationSettle { get; init; } = TimeSpan.FromMilliseconds(500);
    public static RealtimeTiming Default { get; } = new(TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(8));
}

public sealed class RealtimeTask(IRealtimeRecognizer recognizer, bool skipDialogue, bool autoPickup,
    RealtimeTiming? timing = null, bool autoMining = false, bool autoLogging = false) : IGameTask
{
    public string Id => "realtime-assist";
    public string Name => "实时辅助";

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        if (!skipDialogue && !autoPickup && !autoMining && !autoLogging) return new(false, "请至少选择一项实时辅助功能");
        var delays = timing ?? RealtimeTiming.Default;
        if (delays.PollInterval <= TimeSpan.Zero || delays.DialogueCooldown <= TimeSpan.Zero
            || delays.PickupCooldown <= TimeSpan.Zero || delays.ResultTimeout <= TimeSpan.Zero
            || delays.ConfirmationSettle <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timing));
        var after = Now;
        var nextAction = TimeSpan.Zero;
        var previousKey = "";
        var stable = 0;
        var absent = 0;
        string? pending = null;
        TimeSpan pendingAt = default;
        var timeoutReported = false;
        var dialogueRetryReported = false;
        var viewportNotice = false;
        string? inputWaitReason = null;
        FrameSnapshot? previousFrame = null;
        var generation = context.Execution.Generation;
        context.Log("实时辅助已启动，等待剧情或当前选中的采集物；可使用已设置的快捷键暂停、继续或停止");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var frame = await context.Frames.ReadAfterAsync(after, token);
            await context.Execution.WaitAsync(token);
            if (generation != context.Execution.Generation)
            { generation = context.Execution.Generation; ResetObservation(); }
            if (frame.SystemTimestamp <= after || (previousFrame is not null && frame.Id <= previousFrame.Id) || !Fresh(frame) || !context.Execution.IsObservationCurrent(frame))
            { ResetObservation(); await context.Execution.DelayAsync(delays.PollInterval, token); continue; }
            after = frame.SystemTimestamp;
            if (previousFrame is not null)
            {
                if (previousFrame.WindowHandle != frame.WindowHandle || previousFrame.ProcessId != frame.ProcessId)
                    return new(false, "游戏窗口身份已变化，实时辅助已停止，请重新连接");
                if (previousFrame.ViewportVersion != frame.ViewportVersion || previousFrame.ClientLeft != frame.ClientLeft
                    || previousFrame.ClientTop != frame.ClientTop || previousFrame.Width != frame.Width || previousFrame.Height != frame.Height)
                {
                    ResetObservation();
                    if (!viewportNotice) context.Log("游戏窗口位置或尺寸变化，等待新画面稳定后继续");
                    viewportNotice = true;
                }
            }
            previousFrame = frame;
            var observation = await recognizer.ObserveAsync(frame, token);
            await context.Execution.WaitAsync(token);
            if (!Fresh(frame) || !context.Execution.IsObservationCurrent(frame))
            { ResetObservation(); await context.Execution.DelayAsync(delays.PollInterval, token); continue; }

            var key = ActionKey(observation);
            stable = key.Length > 0 && SamePrompt(key, previousKey) ? stable + 1 : key.Length > 0 ? 1 : 0;
            previousKey = key;
            if (stable >= 2) viewportNotice = false;
            if (pending is not null)
            {
                absent = observation.Page != RealtimePage.Unknown && !observation.PickupBlocked && key.Length == 0 ? absent + 1 : 0;
                if (absent >= 2 || stable >= 2 && !SamePrompt(key, pending))
                {
                    context.Log(pending.StartsWith("dialogue:", StringComparison.Ordinal)
                        ? "已观察到剧情画面变化" : "采集提示已变化，继续观察附近采集物");
                    pending = null;
                    timeoutReported = false;
                }
                else if (!timeoutReported && context.Execution.Elapsed - pendingAt >= delays.ResultTimeout)
                {
                    context.Log(pending.StartsWith("dialogue:", StringComparison.Ordinal)
                        ? "剧情按钮仍未变化，将在重新确认可见且冷却结束后继续尝试"
                        : "等待操作后的提示更新");
                    timeoutReported = true;
                }
            }
            // Visible story controls may precede their game-side click handlers becoming ready.
            var retryDialogue = observation.Page == RealtimePage.Dialogue && pending is not null
                && pending.StartsWith("dialogue:", StringComparison.Ordinal) && SamePrompt(key, pending);
            if ((pending is null || retryDialogue) && stable >= 2 && context.Execution.Elapsed >= nextAction)
            {
                try
                {
                    if (observation.Page == RealtimePage.Dialogue && skipDialogue && observation.SkipPoint is { } point)
                    {
                        await context.Input.ClickAsync(frame, point, token);
                        if (!retryDialogue) context.Log(observation.Action switch
                        {
                            DialogueAction.ConfirmSummary => "已点击剧情梗概的跳过按钮，等待新画面",
                            DialogueAction.ConfirmPrompt => "已点击本段剧情的确认按钮，等待新画面",
                            _ => "已点击剧情跳过箭头，等待新画面"
                        });
                        else if (!dialogueRetryReported)
                        {
                            context.Log("剧情按钮仍可见，已按设定间隔再次点击");
                            dialogueRetryReported = true;
                        }
                        pending ??= key;
                        await TrySecondClickAsync(frame, point, observation.Action, generation);
                        if (observation.Action is DialogueAction.ConfirmSummary or DialogueAction.ConfirmPrompt)
                            await context.Execution.DelayAsync(delays.ConfirmationSettle, token);
                        nextAction = context.Execution.Elapsed + delays.DialogueCooldown;
                        after = Now;
                        ResetObservation();
                    }
                    else if (SelectedPickup(observation) is { } pickup)
                    {
                        await context.Input.PressKeyAsync(frame, GameKey.Interact, token);
                        context.Log($"尝试拾取：{(string.IsNullOrWhiteSpace(pickup.Name) ? "名称未识别" : pickup.Name)}");
                        nextAction = context.Execution.Elapsed + delays.PickupCooldown;
                        pending = key;
                    }
                    if (pending is not null)
                    {
                        if (!retryDialogue)
                        {
                            pendingAt = context.Execution.Elapsed; absent = 0; timeoutReported = false;
                            dialogueRetryReported = false;
                        }
                        if (inputWaitReason is not null) context.Log("游戏输入条件已恢复，继续实时辅助");
                        inputWaitReason = null;
                    }
                }
                catch (InputUnavailableException error)
                {
                    // This exception guarantees that no button or key was pressed.
                    ResetObservation();
                    if (inputWaitReason != error.Message) context.Log("实时辅助等待：" + error.Message);
                    inputWaitReason = error.Message;
                }
            }
            await context.Execution.DelayAsync(delays.PollInterval, token);
        }

        async Task TrySecondClickAsync(FrameSnapshot first, ClientPoint point, DialogueAction action, int firstGeneration)
        {
            var released = Now;
            using var check = CancellationTokenSource.CreateLinkedTokenSource(token);
            // A late frame belongs to the next normal observation, not to this double-click.
            check.CancelAfter(TimeSpan.FromMilliseconds(180));
            try
            {
                var fresh = await context.Frames.ReadForInputCheckAsync(released, check.Token);
                if (fresh is null || fresh.Id <= first.Id || fresh.SystemTimestamp <= released || !Fresh(fresh)
                    || !first.SameViewport(fresh) || !context.Execution.IsObservationCurrent(fresh)) return;
                var next = await recognizer.ObserveDialogueControlAsync(fresh, check.Token);
                if (next.Page != RealtimePage.Dialogue || next.Action != action || next.SkipPoint is not { } button
                    || Math.Abs(button.X - point.X) > 6 || Math.Abs(button.Y - point.Y) > 6) return;
                var wait = RealtimeTiming.DoubleClickGap - (Now - released);
                if (wait > TimeSpan.Zero) await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(Math.Ceiling(wait.TotalMilliseconds)), check.Token);
                check.Token.ThrowIfCancellationRequested();
                if (context.Execution.Generation != firstGeneration || !context.Execution.IsObservationCurrent(fresh)) return;
                // The verification deadline only limits the start. Once pressed, use the normal key-up and cancellation rules.
                await context.Input.ClickAsync(fresh, button, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && check.IsCancellationRequested) { }
            catch (InputUnavailableException error)
            {
                if (inputWaitReason != error.Message) context.Log("剧情双击的第二下未发送：" + error.Message);
            }
        }

        void ResetObservation() { stable = 0; previousKey = ""; absent = 0; }
    }

    private string ActionKey(RealtimeObservation observation) => observation.Page switch
    {
        RealtimePage.Dialogue when skipDialogue && observation.SkipPoint is not null => $"dialogue:{observation.Action}:" + observation.DialogueText,
        RealtimePage.World when SelectedPickup(observation) is { } pickup => $"pickup:{pickup.Key}",
        _ => ""
    };

    private PickupItem? SelectedPickup(RealtimeObservation observation)
    {
        if (observation.Page != RealtimePage.World || observation.PickupBlocked || observation.Pickup is not { } item) return null;
        return item.Kind switch
        {
            PickupKind.Gather when autoPickup => item,
            PickupKind.Mining when autoMining => item,
            PickupKind.Logging when autoLogging => item,
            _ => null
        };
    }

    private static bool SamePrompt(string first, string second)
    {
        if (first == second) return true;
        const string prefix = "dialogue:";
        if (!first.StartsWith(prefix, StringComparison.Ordinal) || !second.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var separator = first.IndexOf(':', prefix.Length);
        var otherSeparator = second.IndexOf(':', prefix.Length);
        if (separator < 0 || otherSeparator < 0 || first[..separator] != second[..otherSeparator]) return false;
        var left = string.Concat(first[(separator + 1)..].Where(char.IsLetterOrDigit));
        var right = string.Concat(second[(otherSeparator + 1)..].Where(char.IsLetterOrDigit));
        if (left == right) return true;
        if (Math.Min(left.Length, right.Length) < 8) return false;
        var allowance = Math.Max(left.Length, right.Length) / 10;
        if (Math.Abs(left.Length - right.Length) > allowance) return false;
        var distances = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var row = 1; row <= left.Length; row++)
        {
            var diagonal = distances[0];
            distances[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var above = distances[column];
                distances[column] = Math.Min(Math.Min(above + 1, distances[column - 1] + 1),
                    diagonal + (left[row - 1] == right[column - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return distances[right.Length] <= allowance;
    }

    private static bool Fresh(FrameSnapshot frame) => Now - frame.SystemTimestamp is var age
        && age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(1);
    private static TimeSpan Now => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
}
