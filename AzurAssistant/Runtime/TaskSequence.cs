using AzurAssistant.Contracts;

namespace AzurAssistant.Runtime;

public sealed class TaskSequence(IReadOnlyList<IGameTask> tasks,
    Func<IGameTask, TaskContext, CancellationToken, Task<TaskResult>>? beforeTask = null,
    Func<TaskContext, CancellationToken, Task<TaskResult>>? recoverFailure = null,
    Func<TaskContext, CancellationToken, Task<TaskResult>>? afterTasks = null,
    string? afterTaskName = null) : IGameTask
{
    public string Id => "daily-sequence";
    public string Name => "日常一条龙";
    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var incomplete = new List<string>();
        var requestApplicationExit = false;
        foreach (var task in tasks)
        {
            var child = context.ForTask(task.Name);
            await context.Execution.WaitAsync(token);
            context.Failures?.SetTask(task.Id, task.Name);
            context.Input.ReleaseAll();
            TaskResult result;
            try
            {
                try
                {
                    await context.Input.ActivateAsync(token);
                    child.Log("开始执行");
                    if (beforeTask is not null)
                    {
                        var prepared = await beforeTask(task, child, token);
                        await context.Execution.WaitAsync(token);
                        if (!prepared.Success)
                        {
                            context.Input.ReleaseAll();
                            await ReportAsync(prepared);
                            return new(false, $"一条龙停止于{task.Name}的起点检查：{prepared.Message}{PriorFailures()}", context.Failures is not null);
                        }
                    }
                    result = await task.ExecuteAsync(child, token);
                    await context.Execution.WaitAsync(token);
                }
                finally { context.Input.ReleaseAll(); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (context.Failures is { } reporter) await reporter.ReportAsync(ex.Message, ex);
                throw;
            }
            child.Log($"{(result.Success ? "完成" : "未完成")}：{result.Message}");
            if (result.Success) continue;
            await ReportAsync(result);
            incomplete.Add(task.Name);
            if (recoverFailure is null) return new(false, $"一条龙停止于{task.Name}：{result.Message}{PriorFailures()}", context.Failures is not null);
            child.Log("先恢复页面；不会重新执行该任务或重复领取");
            TaskResult recovery;
            try
            {
                try { recovery = await recoverFailure(child, token); }
                finally { context.Input.ReleaseAll(); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (context.Failures is { } reporter) await reporter.ReportAsync($"恢复页面失败：{ex.Message}", ex);
                throw;
            }
            await context.Execution.WaitAsync(token);
            child.Log(recovery.Message);
            if (!recovery.Success)
            {
                await ReportAsync(recovery);
                return new(false, $"一条龙停止于{task.Name}：{result.Message}；{recovery.Message}{PriorFailures()}", context.Failures is not null);
            }
            child.Log("已恢复，跳过此项并继续后续任务");
        }
        if (afterTasks is not null)
        {
            var finalContext = context.ForTask(afterTaskName ?? "收尾");
            await context.Execution.WaitAsync(token);
            context.Failures?.SetTask(Id + "-after-tasks", Name + "收尾");
            context.Input.ReleaseAll();
            TaskResult final;
            try
            {
                try { final = await afterTasks(finalContext, token); }
                finally { context.Input.ReleaseAll(); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (context.Failures is { } reporter) await reporter.ReportAsync(ex.Message, ex);
                throw;
            }
            await context.Execution.WaitAsync(token);
            finalContext.Log(final.Message);
            if (!final.Success)
            {
                await ReportAsync(final);
                return new(false, $"一条龙收尾失败：{final.Message}{PriorFailures()}", context.Failures is not null);
            }
            requestApplicationExit = final.RequestApplicationExit;
        }
        await context.Execution.WaitAsync(token);
        return incomplete.Count == 0 ? new(true, "日常一条龙完成", RequestApplicationExit: requestApplicationExit)
            : new(false, $"日常一条龙执行结束；未完成并跳过：{string.Join("、", incomplete)}；其余任务已执行完成", context.Failures is not null, requestApplicationExit);

        string PriorFailures() => incomplete.Count == 0 ? "" : $"；未完成项：{string.Join("、", incomplete)}";
        Task ReportAsync(TaskResult failure) => !failure.FailureReported && context.Failures is { } reporter
            ? reporter.ReportAsync(failure.Message) : Task.CompletedTask;
    }
}
