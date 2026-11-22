using AzurAssistant.Contracts;
using AzurAssistant.Platform;
using AzurAssistant.Runtime;

namespace AzurAssistant.Features.Actions;

/// <summary>Replays a recorded player input stream as a task, so pause hotkeys, stop, logging and failure capture apply.</summary>
public sealed class PlayActionTask(ActionRecording recording) : IGameTask
{
    public string Id => "action-play";
    public string Name => $"动作回放·{recording.Name}";

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var frame = await context.Frames.ReadAfterAsync(TimeSpan.Zero, token).ConfigureAwait(false);
        context.Log($"开始回放「{recording.Name}」：{recording.Summary}");
        var player = new ActionPlayer(log: context.Log);
        var lastReport = -1L;
        var paused = context.Execution as TaskPauseController;
        player.Play(recording, frame.WindowHandle, (elapsed, total) =>
        {
            // Report at most every 10 seconds so long recordings do not flood the log.
            if (elapsed - lastReport < 10_000_000) return;
            lastReport = elapsed;
            context.Log($"回放进度：{elapsed / 1_000_000.0:0.0} / {total / 1_000_000.0:0.0} 秒");
        }, paused is null ? null : () => paused.IsPaused, token);
        return new TaskResult(true, $"动作回放完成：{recording.Name}");
    }
}
