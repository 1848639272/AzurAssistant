using AzurAssistant.Contracts;
using AzurAssistant.Game.Navigation;
using AzurAssistant.Game.Interaction;

namespace AzurAssistant.Features.Routes;

public sealed class RouteCaptureTask(INavigationRecognizer recognizer, Action<NavigationStatus> report,
    Action<MapPosition> captured) : IGameTask
{
    public string Id => "route-capture";
    public string Name => "记录路线点位";
    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var session = new NavigationSession(context, recognizer, o => report(new(o.Position, o.Frame.CapturedAt, o.Diagnostic, o.FacingDegrees)));
        try
        {
            var observation = await session.WaitAsync(o => o.Page == NavigationPage.World && o.Position is not null,
                "记录当前世界坐标", token, 12, 350, NavigationStability.Stationary);
            captured(observation.Position!);
            return new(true, "已取得当前点位，可以命名后继续录制");
        }
        catch (NavigationException ex) { return new(false, ex.Message); }
        finally { context.Input.ReleaseAll(); }
    }
}

public sealed class RouteTask(INavigationRecognizer recognizer, RouteDefinition route,
    Action<NavigationStatus> report, IPickupRecognizer? pickup = null) : IGameTask
{
    public string Id => "route-run";
    public string Name => "运行路线";
    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var map = route.Validate(recognizer.Maps);
        var session = new NavigationSession(context, recognizer, o => report(new(o.Position, o.Frame.CapturedAt, o.Diagnostic, o.FacingDegrees)));
        try
        {
            if (route.AutoPickup && pickup is null) throw new NavigationException("自动拾取能力未装配，路线未启动。");
            var start = await session.PrepareRouteAsync(token, map.Id);
            if (!map.Contains(start.Position!)) throw new NavigationException("请先让角色进入路线所属地图：" + map.Name);
            var collector = route.AutoPickup ? new NavigationPickup(context, session, pickup!) : null;
            var mover = new CoordinateMover(context, session, collector);
            for (var i = 0; i < route.Points.Count; i++)
            {
                var point = route.Points[i];
                context.Log($"路线 {route.Name}：{i + 1}/{route.Points.Count}，{point.Name}");
                using var deadline = context.Execution.CreateDeadline(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                await mover.MoveAsync(map.Position(point.X, point.Y), deadline.Token, maximumDistance: double.PositiveInfinity);
                if (collector is not null) await collector.CollectAsync(token);
            }
            var last = route.Points[^1];
            await session.ConfirmArrivalAsync(map.Position(last.X, last.Y), token);
            return new(true, $"路线“{route.Name}”的 {route.Points.Count} 个点位已完成");
        }
        catch (NavigationException ex) { return new(false, ex.Message); }
        finally { context.Input.ReleaseAll(); }
    }
}
