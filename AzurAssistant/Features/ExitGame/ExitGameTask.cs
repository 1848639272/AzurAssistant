using AzurAssistant.Contracts;

namespace AzurAssistant.Features.ExitGame;

/// <summary>Runs only at the sequence tail; process and capture lifecycle are supplied by Composition.</summary>
public sealed class ExitGameTask(Func<CancellationToken, Task> closeGame, int delaySeconds = 0,
    bool closeAssistantAfterExitGame = false) : IGameTask
{
    public string Id => "exit-game";
    public string Name => "关闭游戏";

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (delaySeconds is < 0 or > 3600) throw new ArgumentOutOfRangeException(nameof(delaySeconds), "关闭游戏前的等待时间应为 0 至 3600 秒。");
        context.Failures?.SetTask(Id, Name);
        context.Input.ReleaseAll();
        context.Log("开始：关闭游戏");
        if (delaySeconds > 0)
        {
            context.Log($"等待 {delaySeconds} 秒后关闭游戏");
            await context.Execution.DelayAsync(TimeSpan.FromSeconds(delaySeconds), token);
        }
        token.ThrowIfCancellationRequested();
        using (await context.Execution.EnterActionAsync(token)) await closeGame(token);
        token.ThrowIfCancellationRequested();
        return new(true, "关闭游戏完成：已确认 AzurPromilia.exe 关闭", RequestApplicationExit: closeAssistantAfterExitGame);
    }
}
