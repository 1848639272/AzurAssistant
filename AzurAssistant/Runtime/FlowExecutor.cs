using System.Diagnostics;

namespace AzurAssistant.Runtime;

/// <summary>Everything a flow needs from the outside world; the real implementation drives the game, tests fake it.</summary>
public interface IFlowHost
{
    Task MoveAsync(double x, double y, CancellationToken token);
    Task ClickAsync(double x, double y, CancellationToken token);
    Task PlayActionAsync(string action, CancellationToken token);
    Task DelayAsync(int milliseconds, CancellationToken token);
    Task<bool> WaitTemplateAsync(string? pack, string template, int timeoutMs, CancellationToken token);
    Task<bool> TemplateVisibleAsync(string? pack, string template, CancellationToken token);
    void Log(string message);
}

public sealed record FlowRunResult(bool Success, string Message, int StepsExecuted, TimeSpan Elapsed);

/// <summary>
/// Executes a nested flow on top of <see cref="IFlowHost"/>. Pure control flow: no game APIs, so it is unit-testable.
/// </summary>
public sealed class FlowExecutor(IFlowHost host, TimeSpan? overallTimeout = null)
{
    /// <summary>Hard cap on loop iterations to keep a bad plan from spinning forever.</summary>
    public const int MaxIterations = 1000;

    public async Task<FlowRunResult> RunAsync(FlowDefinition flow, CancellationToken token)
    {
        flow.Validate();
        var started = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(overallTimeout ?? TimeSpan.FromMinutes(30));
        var executed = 0;
        try
        {
            await RunStepsAsync(flow.Steps, 0, deadline.Token, () => executed++).ConfigureAwait(false);
            host.Log($"流程「{flow.Name}」完成：{executed} 步，用时 {started.Elapsed.TotalSeconds:F1} 秒");
            return new FlowRunResult(true, $"流程「{flow.Name}」完成（{executed} 步）", executed, started.Elapsed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new FlowRunResult(false, $"流程「{flow.Name}」已取消（已执行 {executed} 步）", executed, started.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new FlowRunResult(false, $"流程「{flow.Name}」超时（已执行 {executed} 步）", executed, started.Elapsed);
        }
        catch (FlowStepException ex)
        {
            return new FlowRunResult(false, $"流程「{flow.Name}」失败：{ex.Message}", executed, started.Elapsed);
        }
    }

    private async Task RunStepsAsync(IReadOnlyList<FlowStep> steps, int depth, CancellationToken token, Action count)
    {
        foreach (var step in steps)
        {
            token.ThrowIfCancellationRequested();
            if (depth > FlowDefinition.MaxDepth) throw new FlowStepException("流程嵌套过深。");
            switch (step.Kind)
            {
                case FlowStep.Move:
                    host.Log(step.Describe());
                    await host.MoveAsync(step.X, step.Y, token).ConfigureAwait(false);
                    break;
                case FlowStep.Click:
                    host.Log(step.Describe());
                    await host.ClickAsync(step.X, step.Y, token).ConfigureAwait(false);
                    break;
                case FlowStep.ActionKind:
                    host.Log(step.Describe());
                    await host.PlayActionAsync(step.Action!, token).ConfigureAwait(false);
                    break;
                case FlowStep.Delay:
                    host.Log(step.Describe());
                    await host.DelayAsync(step.DurationMs, token).ConfigureAwait(false);
                    break;
                case FlowStep.Wait:
                    host.Log(step.Describe());
                    if (!await host.WaitTemplateAsync(step.Pack, step.Template!, step.DurationMs, token).ConfigureAwait(false))
                        throw new FlowStepException($"等待超时：找不到 {Describe(step)}");
                    break;
                case FlowStep.Loop:
                    await RunLoopAsync(step, depth, token, count).ConfigureAwait(false);
                    break;
                case FlowStep.If:
                    await RunIfAsync(step, depth, token, count).ConfigureAwait(false);
                    break;
                default:
                    throw new FlowStepException("未知步骤类型：" + step.Kind);
            }
            count();
        }
    }

    private async Task RunLoopAsync(FlowStep step, int depth, CancellationToken token, Action count)
    {
        var body = step.Body ?? [];
        if (step.Template is not null)
        {
            // Repeat until the template is (or is not) visible.
            for (var iteration = 0; ; iteration++)
            {
                if (iteration >= MaxIterations) throw new FlowStepException($"循环“{Describe(step)}”超过 {MaxIterations} 次仍未满足退出条件。");
                token.ThrowIfCancellationRequested();
                var visible = await host.TemplateVisibleAsync(step.Pack, step.Template!, token).ConfigureAwait(false);
                if (visible != step.Negate) break;
                host.Log($"循环第 {iteration + 1} 次（{Describe(step)}）");
                await RunStepsAsync(body, depth + 1, token, count).ConfigureAwait(false);
            }
            return;
        }

        var repeat = Math.Clamp(step.Repeat, 1, FlowDefinition.MaxTotalSteps);
        for (var iteration = 0; iteration < repeat; iteration++)
        {
            token.ThrowIfCancellationRequested();
            host.Log($"循环 {iteration + 1}/{repeat}");
            await RunStepsAsync(body, depth + 1, token, count).ConfigureAwait(false);
        }
    }

    private async Task RunIfAsync(FlowStep step, int depth, CancellationToken token, Action count)
    {
        var visible = await host.TemplateVisibleAsync(step.Pack, step.Template, token).ConfigureAwait(false);
        var matches = visible != step.Negate;
        host.Log($"条件 {Describe(step)} → {(matches ? "成立" : "跳过")}");
        if (!matches) return;
        await RunStepsAsync(step.Body ?? [], depth + 1, token, count).ConfigureAwait(false);
    }

    private static string Describe(FlowStep step) => step.Template is null ? step.Kind : $"{step.Pack}/{step.Template}";
}

public sealed class FlowStepException(string message) : Exception(message);
