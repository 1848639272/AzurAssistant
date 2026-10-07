using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Game.Navigation;

/// <summary>Selects a map, moves its viewport, verifies the named detail and validates the destination.</summary>
public sealed class MapTeleporter(TaskContext context, INavigationRecognizer recognizer, NavigationSession session)
{
    private Task<NavigationObservation> WaitAsync(Func<NavigationObservation, bool> accept, string step,
        CancellationToken token, double timeoutSeconds = 12, double stableMilliseconds = 500,
        NavigationStability stability = NavigationStability.Target) =>
        session.WaitAsync(accept, step, token, timeoutSeconds, stableMilliseconds, stability, teleportOnly: true);

    private Task ActAsync(NavigationObservation source, Func<NavigationObservation, bool> accept,
        Func<NavigationObservation, CancellationToken, Task> action, string step, CancellationToken token,
        NavigationStability stability = NavigationStability.Target) =>
        session.ActAsync(source, accept, action, step, token, stability, teleportOnly: true, stableMilliseconds: 500);

    public async Task RunAsync(string mapId, string teleportId, CancellationToken token)
    {
        var map = recognizer.Maps.Single(m => m.Id == mapId);
        var target = map.Teleports.Single(t => t.Id == teleportId);
        var destination = recognizer.Maps.Single(m => m.Id == target.ArrivalMapId);
        recognizer.SelectTeleport(mapId, teleportId);
        string? Displayed(NavigationObservation o) => o.DisplayedMapId ?? o.Position?.MapId
            ?? (recognizer.Maps.Count == 1 && o.Page != NavigationPage.WorldMap ? recognizer.Layout.MapId : null);
        var current = await WaitAsync(o => o.Page != NavigationPage.Unknown, "确认传送起点", token,
            stability: NavigationStability.Target);
        for (var transition = 0; transition < 6; transition++)
        {
            if (Displayed(current) == mapId && current.Page is NavigationPage.LocalMap or NavigationPage.TeleportDetail or NavigationPage.OverlapChoices) break;
            var previousMap = Displayed(current);
            var expectWorldMap = false;
            var expectedLocalMap = previousMap;
            if (current.Page == NavigationPage.World)
            {
                await ActAsync(current, o => o.Page == NavigationPage.World,
                    (o, ct) => context.Input.PressKeyAsync(o.Frame, GameKey.Map, ct), "打开当前地图", token);
            }
            else if (current.Page == NavigationPage.WorldMap)
            {
                expectedLocalMap = mapId;
                await ActAsync(current, o => o.Page == NavigationPage.WorldMap && o.WorldRegion is not null,
                    (o, ct) => context.Input.ClickAsync(o.Frame, o.WorldRegion!.Value, ct), "切换至" + map.Name, token);
            }
            else if (current.WorldMapButton is not null)
            {
                expectWorldMap = true;
                await ActAsync(current, o => o.WorldMapButton is not null,
                    (o, ct) => context.Input.ClickAsync(o.Frame, o.WorldMapButton!.Value, ct), "打开世界地图", token);
            }
            else if (current.Page is NavigationPage.TeleportDetail or NavigationPage.OverlapChoices)
            {
                await ActAsync(current, o => o.Page is NavigationPage.TeleportDetail or NavigationPage.OverlapChoices,
                    (o, ct) => context.Input.PressKeyAsync(o.Frame, GameKey.Escape, ct), "关闭原目标详情", token);
            }
            else throw new NavigationException("当前页面没有可确认的地图切换入口。");
            // A changed page alone can be an unfinished transition. Require the requested destination state.
            current = await WaitAsync(o => expectWorldMap
                    ? o.Page == NavigationPage.WorldMap && o.WorldRegion is not null
                    : (o.Page is NavigationPage.LocalMap or NavigationPage.TeleportDetail or NavigationPage.OverlapChoices)
                        && Displayed(o) is { } shown && (expectedLocalMap is null || shown == expectedLocalMap),
                "确认地图切换结果", token, stability: NavigationStability.Target);
        }
        if (Displayed(current) != mapId) throw new NavigationException("未进入指定地图，已停止。");
        if (current.Page is NavigationPage.TeleportDetail or NavigationPage.OverlapChoices && current.SelectedName != target.Name)
        {
            await ActAsync(current, o => Displayed(o) == mapId && o.Page is NavigationPage.TeleportDetail or NavigationPage.OverlapChoices,
                (o, ct) => context.Input.PressKeyAsync(o.Frame, GameKey.Escape, ct), "关闭其他目标详情", token);
            current = await WaitAsync(o => o.Page == NavigationPage.LocalMap && Displayed(o) == mapId,
                "返回指定地图", token, stability: NavigationStability.Target);
        }
        current = await FindTargetAsync(current, target, o => Displayed(o) == mapId, token);
        if (current.Page == NavigationPage.LocalMap)
        {
            await ActAsync(current, o => o.Page == NavigationPage.LocalMap && Displayed(o) == mapId && o.TeleportIcon is not null,
                (o, ct) => context.Input.ClickAsync(o.Frame, o.TeleportIcon!.Value, ct), "选择" + target.Name, token);
            current = await WaitAsync(o => o.Page is NavigationPage.TeleportDetail or NavigationPage.OverlapChoices,
                "等待目标详情或重叠列表", token, stability: NavigationStability.Target);
        }
        if (current.Page == NavigationPage.OverlapChoices)
        {
            if (current.SelectedName != target.Name || current.OverlapChoice is null)
                throw new NavigationException("重叠列表中未确认指定传送点名称。");
            await ActAsync(current, o => o.Page == NavigationPage.OverlapChoices && o.SelectedName == target.Name && o.OverlapChoice is not null,
                (o, ct) => context.Input.ClickAsync(o.Frame, o.OverlapChoice!.Value, ct), "选择重叠目标", token);
            current = await WaitAsync(o => o.Page == NavigationPage.TeleportDetail, "确认传送详情", token,
                stability: NavigationStability.Target);
        }
        if (current.Page != NavigationPage.TeleportDetail || Displayed(current) != mapId || current.SelectedName != target.Name || current.TeleportButton is null)
            throw new NavigationException("详情页未确认指定传送点，未点击传送。");
        context.Log("已确认" + target.Name + "，执行一次传送");
        await ActAsync(current, o => o.Page == NavigationPage.TeleportDetail && Displayed(o) == mapId && o.SelectedName == target.Name && o.TeleportButton is not null,
            (o, ct) => context.Input.ClickAsync(o.Frame, o.TeleportButton!.Value, ct), "传送", token);
        await context.Execution.DelayAsync(TimeSpan.FromSeconds(1), token);
        var landing = target.LandingX is { } lx && target.LandingY is { } ly ? destination.Position(lx, ly)
            : target.ArrivalMapId == map.Id ? map.Position(target.X, target.Y) : null;
        var arrived = await WaitAsync(o => o.Page == NavigationPage.World && o.Position is { } p && destination.Contains(p)
            && (landing is null || p.DistanceTo(landing) <= target.ArrivalRadius), "验证传送后地图与落点", token, 60, 700,
            NavigationStability.Stationary);
        context.Log($"传送到达 {destination.Name} ({arrived.Position!.X:F1}, {arrived.Position.Y:F1})"
            + (landing is null ? "；该跨区入口尚无精确出生点标定" : ""));
    }

    private async Task<NavigationObservation> FindTargetAsync(NavigationObservation current, NavigationTeleport target,
        Func<NavigationObservation, bool> correctMap, CancellationToken token)
    {
        // Count only active time, across all successful drags and alternate starts. Progress never renews the deadline.
        using var deadline = context.Execution.CreateDeadline(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try { return await DragToTargetAsync(current, target, correctMap, deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.Token.IsCancellationRequested)
        { throw new NavigationException($"寻找{target.Name}已用满3分钟，尚未确认目标图标；已停止拖动。"); }
    }

    private async Task<NavigationObservation> DragToTargetAsync(NavigationObservation current, NavigationTeleport target,
        Func<NavigationObservation, bool> correctMap, CancellationToken token)
    {
        var origin = 0;
        var originsTried = 1;
        var unmoved = 0;
        var noProgress = 0;
        for (var drag = 0; current.Page == NavigationPage.LocalMap && current.TeleportIcon is null; drag++)
        {
            if (current.Registration is not { } transform) throw new NavigationException("缺少当前地图坐标变换，不能拖动。");
            var gesture = MapDragPlanner.Plan(transform, target.X, target.Y, origin);
            if (gesture is null) throw new NavigationException("目标区域已居中，但未识别到目标传送图标；未按推算坐标盲点。" + current.Diagnostic);
            MapImageMatch? beforeDrag = null;
            var generation = context.Execution.Generation;
            await ActAsync(current, o => o.Page == NavigationPage.LocalMap && correctMap(o) && o.Registration is not null,
                (o, ct) =>
                {
                    var fresh = MapDragPlanner.Plan(o.Registration!, target.X, target.Y, origin)
                        ?? throw new NavigationException("拖动前目标位置已变化，请重新确认。");
                    beforeDrag = o.Registration;
                    context.Log($"寻找{target.Name}：第 {drag + 1} 次拖动，起点 {origin + 1} ({fresh.From.X},{fresh.From.Y}) → ({fresh.To.X},{fresh.To.Y})");
                    return context.Input.DragAsync(o.Frame, fresh.From, fresh.To, ct);
                }, "向目标移动地图视口", token, NavigationStability.Registration);
            await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(300), token);
            current = await WaitAsync(o => o.Page == NavigationPage.LocalMap && correctMap(o) && o.Registration is not null,
                "拖动后重新定位地图", token, stability: NavigationStability.Registration);
            if (current.TeleportIcon is not null) break;
            if (generation != context.Execution.Generation)
            { unmoved = noProgress = 0; originsTried = 1; continue; }
            var displacement = MapDragPlanner.Displacement(beforeDrag!, current.Registration!);
            var remaining = MapDragPlanner.TargetDistance(current.Registration!, target.X, target.Y);
            var progress = MapDragPlanner.TargetDistance(beforeDrag!, target.X, target.Y) - remaining;
            context.Log($"拖动反馈：底图移动 {displacement:F1} 像素，接近目标 {progress:F1} 像素，目标距视口中心 {remaining:F1} 像素");
            if (displacement < 4)
            {
                noProgress = 0;
                if (++unmoved < 2) continue;
                if (originsTried == MapDragPlanner.OriginCount)
                    throw new NavigationException("已尝试3个拖动起点（每处连续2次），地图仍未移动；可能已到边界或交互遮挡。");
                origin = (origin + 1) % MapDragPlanner.OriginCount;
                originsTried++;
                unmoved = 0;
                context.Log($"连续两次拖动后底图未移动，更换为起点 {origin + 1}，保持朝目标的拖动方向");
            }
            else
            {
                unmoved = 0;
                originsTried = 1;
                noProgress = progress < 8 ? noProgress + 1 : 0;
                if (noProgress >= 2)
                    throw new NavigationException("底图已移动，但连续两次未接近目标；请检查地图边界或缩放状态。");
            }
        }
        return current;
    }
}

public static class MapDragPlanner
{
    private static readonly ClientPoint[] Origins = [new(760, 520), new(1040, 380), new(520, 680)];
    public static int OriginCount => Origins.Length;

    public static (ClientPoint From, ClientPoint To)? Plan(MapImageMatch transform, double x, double y, int origin = 0)
    {
        if (origin < 0 || origin >= OriginCount) throw new ArgumentOutOfRangeException(nameof(origin));
        var target = transform.Unproject(x, y);
        var dx = 760 - target.X; var dy = 520 - target.Y;
        if (Math.Abs(dx) < 55 && Math.Abs(dy) < 55) return null;
        var factor = Math.Min(1, Math.Min(640 / Math.Max(1, Math.Abs(dx)), 420 / Math.Max(1, Math.Abs(dy))));
        var shiftX = (int)Math.Round(dx * factor); var shiftY = (int)Math.Round(dy * factor);
        // Translate the entire gesture inside the map interior, preserving its direction and length.
        var from = new ClientPoint(Math.Clamp(Origins[origin].X, 120 - Math.Min(0, shiftX), 1420 - Math.Max(0, shiftX)),
            Math.Clamp(Origins[origin].Y, 170 - Math.Min(0, shiftY), 880 - Math.Max(0, shiftY)));
        return (from, new(from.X + shiftX, from.Y + shiftY));
    }

    public static double TargetDistance(MapImageMatch transform, double x, double y)
    {
        var target = transform.Unproject(x, y);
        return Math.Sqrt(Math.Pow(target.X - 760, 2) + Math.Pow(target.Y - 520, 2));
    }

    public static double Displacement(MapImageMatch before, MapImageMatch after)
    {
        var distance = 0d;
        foreach (var point in new[] { new ClientPoint(160, 180), new ClientPoint(1250, 180), new ClientPoint(760, 850) })
        {
            var reference = before.Project(point.X, point.Y);
            var moved = after.Unproject(reference.X, reference.Y);
            distance = Math.Max(distance, Math.Sqrt(Math.Pow(moved.X - point.X, 2) + Math.Pow(moved.Y - point.Y, 2)));
        }
        return distance;
    }
}
