using System.Windows.Threading;
using AzurAssistant.Contracts;
using AzurAssistant.Runtime;
using AzurAssistant.UI;

namespace AzurAssistant.Composition;

internal sealed class UidOverlayTask(IGameTask? actions, CapturePreviewService preview, Dispatcher dispatcher) : IGameTask
{
    public string Id => "realtime-assist";
    public string Name => "实时辅助";

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var mask = await dispatcher.InvokeAsync(() => new UidMaskWindow(() => preview.Current), DispatcherPriority.Normal, token);
        try
        {
            context.Log("已开启游戏窗口 UID 遮罩");
            if (actions is not null) return await actions.ExecuteAsync(context, token);
            var after = TimeSpan.Zero;
            while (true)
            {
                var frame = await context.Frames.ReadAfterAsync(after, token);
                after = frame.SystemTimestamp;
                await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(100), token);
            }
        }
        finally { await dispatcher.InvokeAsync(mask.Dispose); }
    }
}
