using AzurAssistant.Contracts;
using AzurAssistant.Game.Navigation;

namespace AzurAssistant.Features.NavigationTest;

public enum NavigationTestMode { Observe, Teleport, Move }
public sealed record NavigationTestRequest(NavigationTestMode Mode, double X = 0, double Y = 0, string? MapId = null, string? TeleportId = null);

public sealed class NavigationTestTask(INavigationRecognizer recognizer, NavigationTestRequest request,
    Action<NavigationStatus> report) : IGameTask
{
    public string Id => "navigation-test";
    public string Name => "导航测试";
    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        void Observe(NavigationObservation observation) => report(new(observation.Position, observation.Frame.CapturedAt,
            observation.Diagnostic, observation.FacingDegrees));
        var session = new NavigationSession(context, recognizer, Observe);
        try
        {
            switch (request.Mode)
            {
                case NavigationTestMode.Observe:
                    context.Log("持续识别当前地图与角色坐标；坐标单位为该地图底图像素");
                    while (true)
                    {
                        await session.WaitAsync(_ => true, "观察地图坐标", token, stableMilliseconds: 0);
                        await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(100), token);
                    }
                case NavigationTestMode.Teleport:
                    await session.TeleportAsync(request.MapId ?? recognizer.Layout.MapId, request.TeleportId ?? recognizer.Layout.Teleport.Id, token);
                    return new(true, "传送后的地图与落点已确认");
                case NavigationTestMode.Move:
                    var start = await session.PrepareRouteAsync(token, request.MapId);
                    var map = recognizer.Maps.Single(m => m.Id == start.Position!.MapId);
                    if (request.MapId is not null && request.MapId != map.Id) return new(false, "角色所在地图已变化，请重新确认目标坐标所属地图。");
                    var target = map.Position(request.X, request.Y);
                    await new CoordinateMover(context, session).MoveAsync(target, token);
                    await session.ConfirmArrivalAsync(target, token);
                    return new(true, "指定坐标已稳定到达");
                default: throw new ArgumentOutOfRangeException(nameof(request));
            }
        }
        catch (NavigationException ex) { return new(false, ex.Message); }
        finally { context.Input.ReleaseAll(); }
    }
}
