using System.Threading.Channels;
using AzurAssistant.Contracts;
using AzurAssistant.Game.Navigation;

namespace AzurAssistant.Features.Routes;

public sealed record RouteRecordingResult(RoutePoint? Point, string Message);

public interface IRouteRecordingTask : IGameTask
{
    void Finish();
    Task<RouteRecordingResult> RequestPointAsync(string? mapId = null, string? teleportId = null);
}

/// <summary>Serializes observation, point capture and teleportation in the recording session.</summary>
public sealed class RouteRecordingTask(INavigationRecognizer recognizer, Action<NavigationStatus> report) : IRouteRecordingTask
{
    private sealed record Request(string? MapId, string? TeleportId, TaskCompletionSource<RouteRecordingResult> Reply);
    private readonly Channel<Request> _requests = Channel.CreateBounded<Request>(1);
    public string Id => "route-recording";
    public string Name => "路线录制";
    public void Finish() => _requests.Writer.TryComplete();

    public Task<RouteRecordingResult> RequestPointAsync(string? mapId = null, string? teleportId = null)
    {
        var reply = new TaskCompletionSource<RouteRecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.Writer.TryWrite(new(mapId, teleportId, reply))) reply.TrySetResult(new(null, "录制忙或已经结束。"));
        return reply.Task;
    }

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        var session = new NavigationSession(context, recognizer,
            o => report(new(o.Position, o.Frame.CapturedAt, o.Diagnostic, o.FacingDegrees)));
        try
        {
            while (!_requests.Reader.Completion.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (!_requests.Reader.TryRead(out var request))
                {
                    await session.WaitAsync(_ => true, "定位当前角色", token, stableMilliseconds: 0, minimapOnly: true);
                    await context.Execution.DelayAsync(TimeSpan.FromMilliseconds(100), token);
                    continue;
                }
                try
                {
                    RoutePoint point;
                    if (request.TeleportId is { } id)
                    {
                        var targetMap = recognizer.Maps.Single(m => m.Id == request.MapId);
                        var node = targetMap.Teleports.Single(t => t.Id == id);
                        await session.TeleportAsync(targetMap.Id, node.Id, token);
                        point = new(node.Name, node.X, node.Y, new(targetMap.Id, targetMap.Floor, targetMap.CoordinateVersion), TeleportId: node.Id);
                    }
                    else
                    {
                        var current = await session.WaitAsync(o => o.Page == NavigationPage.World && o.Position is not null && o.FacingDegrees is not null,
                            "记录当前地图、坐标与朝向", token, 12, 350, NavigationStability.Facing);
                        var p = current.Position!;
                        point = new("", Math.Round(p.X, 2), Math.Round(p.Y, 2), new(p.MapId, p.Floor, p.CoordinateVersion), current.FacingDegrees);
                    }
                    request.Reply.TrySetResult(new(point, point.TeleportId is null ? "已记录当前点。" : "已确认传送落地，传送步骤已记录。"));
                }
                catch (NavigationException ex) { request.Reply.TrySetResult(new(null, ex.Message)); }
                finally { request.Reply.TrySetResult(new(null, "录制操作已中止。")); }
            }
            return new(true, "路线录制已结束");
        }
        finally
        {
            Finish();
            while (_requests.Reader.TryRead(out var pending)) pending.Reply.TrySetResult(new(null, "路线录制已结束。"));
            context.Input.ReleaseAll();
        }
    }
}
