using AzurAssistant.Contracts;
using AzurAssistant.Game.Navigation;
using AzurAssistant.Platform;
using AzurAssistant.Runtime;

namespace AzurAssistant.Features.Wheelchair;

/// <summary>
/// Runs a wheelchair plan: walk to each coordinate with the existing navigation mover, then play the recorded action of
/// that step. Actions are looked up by name so a shared plan only needs the same action recordings imported locally.
/// </summary>
public sealed class WheelchairTask(INavigationRecognizer recognizer, WheelchairPlan plan,
    IReadOnlyDictionary<string, ActionRecording> actions, Action<NavigationStatus> report) : IGameTask
{
    public string Id => "wheelchair-run";
    public string Name => "轮椅功能·" + plan.Name;

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var map = plan.Validate(recognizer.Maps);
        var missing = plan.ActionNames.Where(name => !actions.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
            return new(false, "缺少动作：" + string.Join("、", missing) + "。请先在“动作录制”页导入这些动作。");
        var session = new NavigationSession(context, recognizer,
            observation => report(new(observation.Position, observation.Frame.CapturedAt, observation.Diagnostic, observation.FacingDegrees)));
        try
        {
            var start = await session.PrepareRouteAsync(token, map.Id);
            if (!map.Contains(start.Position!)) throw new NavigationException("请先让角色进入方案所属地图：" + map.Name);
            var mover = new CoordinateMover(context, session);
            var player = new ActionPlayer(log: context.Log);
            for (var index = 0; index < plan.Steps.Count; index++)
            {
                var step = plan.Steps[index];
                context.Log($"轮椅 {index + 1}/{plan.Steps.Count}：前往“{step.Name}”（{step.X:0.##}, {step.Y:0.##}）");
                using (var deadline = context.Execution.CreateDeadline(token))
                {
                    deadline.CancelAfter(TimeSpan.FromMinutes(3));
                    await mover.MoveAsync(map.Position(step.X, step.Y), deadline.Token, maximumDistance: double.PositiveInfinity);
                }
                await session.ConfirmArrivalAsync(map.Position(step.X, step.Y), token);
                if (step.DelayMs > 0)
                {
                    context.Log($"等待 {step.DelayMs / 1000.0:0.#} 秒");
                    await Task.Delay(step.DelayMs, token);
                }
                if (string.IsNullOrWhiteSpace(step.Action)) continue;
                if (!actions.TryGetValue(step.Action, out var recording))
                    throw new NavigationException($"步骤“{step.Name}”缺少动作“{step.Action}”。");
                context.Log($"执行动作“{step.Action}”：{recording.Summary}");
                var frame = await context.Frames.ReadAfterAsync(TimeSpan.Zero, token).ConfigureAwait(false);
                var paused = context.Execution as TaskPauseController;
                player.Play(recording, frame.WindowHandle, null, paused is null ? null : () => paused.IsPaused, token);
            }
            return new(true, $"轮椅方案“{plan.Name}”的 {plan.Steps.Count} 个步骤已完成");
        }
        catch (NavigationException ex) { return new(false, ex.Message); }
        finally { context.Input.ReleaseAll(); }
    }
}
