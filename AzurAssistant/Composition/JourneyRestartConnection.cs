using AzurAssistant.Contracts;
using AzurAssistant.Platform;
using AzurAssistant.Runtime;

namespace AzurAssistant.Composition;

public sealed class JourneyRestartConnection(CapturePreviewService preview, Func<GameWindow?> findWindow,
    Func<TaskContext, CancellationToken, Task>? relaunch = null, Func<bool>? isGameRunning = null,
    TimeSpan? selfRestartGrace = null)
{
    public async Task<FrameSnapshot> ReconnectAsync(FrameSnapshot previous, TaskContext context, CancellationToken token)
    {
        context.Input.ReleaseAll();
        await preview.StopAsync();
        TimeSpan? absentSince = null;
        var relaunched = false;
        while (true)
        {
            await context.Execution.WaitAsync(token);
            var window = findWindow();
            if (window is not null && (window.ProcessId != previous.ProcessId || window.Handle != previous.WindowHandle))
            {
                await context.Input.PrepareForObservationAsync(token);
                if (!preview.Start()) throw new InputUnavailableException("游戏重启后未能重新开始捕获。");
                var first = await context.Frames.ReadAfterAsync(previous.SystemTimestamp, token);
                if (first.WindowHandle != window.Handle || first.ProcessId != window.ProcessId)
                    throw new InputUnavailableException("游戏重启期间窗口再次变化，请重新运行进入游戏。");
                return first;
            }
            if (relaunch is not null && !relaunched && window is null && !(isGameRunning ?? GameLauncher.IsGameRunning)())
            {
                absentSince ??= context.Execution.Elapsed;
                if (context.Execution.Elapsed - absentSince.Value >= (selfRestartGrace ?? TimeSpan.FromSeconds(8)))
                {
                    token.ThrowIfCancellationRequested();
                    relaunched = true;
                    await relaunch(context, token);
                    continue;
                }
            }
            else absentSince = null;
            await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(250), token);
        }
    }
}
