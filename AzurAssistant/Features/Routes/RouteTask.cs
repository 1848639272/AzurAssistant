using AzurAssistant.Contracts;
using AzurAssistant.Game.Navigation;
using AzurAssistant.Game.Interaction;

namespace AzurAssistant.Features.Routes;

public sealed class RouteTask(INavigationRecognizer recognizer, RouteDefinition route,
    Action<NavigationStatus> report, IPickupRecognizer? pickup = null, bool autoPickup = false) : IGameTask
{
    public string Id => "route-run";
    public string Name => "运行路线 · " + route.Name;
    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var firstMap = route.Validate(recognizer.Maps);
        var session = new NavigationSession(context, recognizer, o => report(new(o.Position, o.Frame.CapturedAt, o.Diagnostic, o.FacingDegrees)));
        try
        {
            if (autoPickup && pickup is null) throw new NavigationException("自动拾取能力未装配，路线未启动。");
            var collector = autoPickup ? new NavigationPickup(context, session, pickup!) : null;
            CoordinateMover? mover = null;
            for (var i = 0; i < route.Points.Count; i++)
            {
                var point = route.Points[i];
                context.Log($"路线 {route.Name}：{i + 1}/{route.Points.Count}，{point.Name}");
                var map = point.Map is { } owner ? recognizer.Maps.Single(m => m.Id == owner.Id) : firstMap;
                if (point.TeleportId is { } id)
                {
                    await session.TeleportAsync(map.Id, id, token);
                    mover = null;
                    if (collector is not null)
                    {
                        var landed = await session.LocateMinimapAsync("确认采集点落地位置", token);
                        await collector.CollectAsync(landed.Position!, token);
                    }
                    continue;
                }
                if (mover is null)
                {
                    await session.PrepareRouteAsync(token, map.Id);
                    mover = new CoordinateMover(context, session);
                }
                using var deadline = context.Execution.CreateDeadline(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                var target = map.Position(point.X, point.Y);
                await mover.MoveAsync(target, deadline.Token, maximumDistance: double.PositiveInfinity);
                if (collector is not null) await collector.CollectAsync(target, deadline.Token);
            }
            var last = route.Points[^1];
            if (last.TeleportId is null)
            {
                var lastMap = last.Map is { } owner ? recognizer.Maps.Single(m => m.Id == owner.Id) : firstMap;
                await session.ConfirmArrivalAsync(lastMap.Position(last.X, last.Y), token);
            }
            return new(true, $"路线“{route.Name}”的 {route.Points.Count} 个点位已完成");
        }
        catch (NavigationException ex) { return new(false, ex.Message); }
        finally { context.Input.ReleaseAll(); }
    }
}
