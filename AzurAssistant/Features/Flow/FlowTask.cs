using System.Collections.Concurrent;
using System.IO;
using AzurAssistant.Contracts;
using AzurAssistant.Game.Navigation;
using AzurAssistant.Platform;
using AzurAssistant.Runtime;
using AzurAssistant.Vision;

namespace AzurAssistant.Features.Flow;

/// <summary>
/// Runs a flow (moves, clicks, actions, waits, loops and conditions). Template checks read a fresh frame and use the
/// same catalogs as the rest of the assistant, so a flow only names a pack and a template id.
/// </summary>
public sealed class FlowTask(INavigationRecognizer recognizer, FlowDefinition flow,
    IReadOnlyDictionary<string, ActionRecording> actions, Action<NavigationStatus> report,
    Action<int, int>? progress = null, int startIndex = 0) : IGameTask
{
    public string Id => "flow-run";
    public string Name => "流程·" + flow.Name;

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        flow.Validate();
        var host = new GameFlowHost(context, recognizer, actions, report);
        var executor = new FlowExecutor(host);
        var result = await executor.RunAsync(flow, token, startIndex, index => progress?.Invoke(index, flow.Steps.Count))
            .ConfigureAwait(false);
        return new(result.Success, result.Message);
    }

    /// <summary>Bridges the pure flow executor to the running game session.</summary>
    private sealed class GameFlowHost(TaskContext context, INavigationRecognizer recognizer,
        IReadOnlyDictionary<string, ActionRecording> actions, Action<NavigationStatus> report) : IFlowHost, IDisposable
    {
        private readonly Dictionary<string, TemplateCatalog> _catalogs = new(StringComparer.OrdinalIgnoreCase);
        private NavigationSession? _session;
        private NavigationObservation? _start;
        private NavigationMap? _map;
        private CoordinateMover? _mover;
        private ActionPlayer? _player;

        public void Log(string message) => context.Log(message);

        public async Task MoveAsync(double x, double y, CancellationToken token)
        {
            if (recognizer.Maps.Count == 0) throw new FlowStepException("没有可用的导航地图，无法执行移动。");
            if (_session is null)
            {
                _session = new NavigationSession(context, recognizer,
                    observation => report(new(observation.Position, observation.Frame.CapturedAt, observation.Diagnostic, observation.FacingDegrees)));
                _mover = new CoordinateMover(context, _session);
                _start = await _session.PrepareRouteAsync(token).ConfigureAwait(false);
                _map = recognizer.Maps.FirstOrDefault(candidate => _start.Position is { } position && candidate.Contains(position));
                if (_map is null) throw new FlowStepException("无法确定当前所在地图，流程里的移动步骤无法执行。");
            }
            var map = _map ?? throw new FlowStepException("无法确定当前所在地图。");
            var target = map.Position(x, y);
            using var deadline = context.Execution.CreateDeadline(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            await _mover!.MoveAsync(target, deadline.Token, maximumDistance: double.PositiveInfinity).ConfigureAwait(false);
            await _session.ConfirmArrivalAsync(target, token).ConfigureAwait(false);
        }

        public async Task ClickAsync(double x, double y, CancellationToken token)
        {
            var frame = await FreshAsync(token).ConfigureAwait(false);
            await context.Input.ClickAsync(frame, new ClientPoint((int)Math.Round(x), (int)Math.Round(y)), token).ConfigureAwait(false);
        }

        public async Task PlayActionAsync(string action, CancellationToken token)
        {
            if (!actions.TryGetValue(action, out var recording))
                throw new FlowStepException($"缺少动作“{action}”，请先在动作录制页导入。");
            var frame = await FreshAsync(token).ConfigureAwait(false);
            _player ??= new ActionPlayer(log: context.Log);
            var paused = context.Execution as TaskPauseController;
            _player.Play(recording, frame.WindowHandle, null, paused is null ? null : () => paused.IsPaused, token);
        }

        public async Task PressKeyAsync(string key, CancellationToken token)
        {
            if (!Enum.TryParse<GameKey>(key, ignoreCase: true, out var gameKey))
                throw new FlowStepException($"不认识的按键“{key}”。可用：{string.Join(" / ", Enum.GetNames<GameKey>())}");
            var frame = await FreshAsync(token).ConfigureAwait(false);
            await context.Input.PressKeyAsync(frame, gameKey, token).ConfigureAwait(false);
        }

        public Task DelayAsync(int milliseconds, CancellationToken token) => Task.Delay(milliseconds, token);

        public async Task<bool> WaitTemplateAsync(string? pack, string template, int timeoutMs, CancellationToken token)
        {
            var deadline = DateTimeOffset.Now.AddMilliseconds(Math.Max(100, timeoutMs));
            while (DateTimeOffset.Now < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (await VisibleAsync(pack, template, token).ConfigureAwait(false)) return true;
                await Task.Delay(150, token).ConfigureAwait(false);
            }
            return await VisibleAsync(pack, template, token).ConfigureAwait(false);
        }

        public async Task<bool> TemplateVisibleAsync(string? pack, string template, CancellationToken token)
            => await VisibleAsync(pack, template, token).ConfigureAwait(false);

        private async Task<bool> VisibleAsync(string? pack, string template, CancellationToken token)
        {
            var catalog = Catalog(pack);
            var frame = await FreshAsync(token).ConfigureAwait(false);
            catalog.Match(frame, template, out var score);
            return score >= catalog.ThresholdFor(template);
        }

        private TemplateCatalog Catalog(string? pack)
        {
            var name = string.IsNullOrWhiteSpace(pack) ? "daily" : pack!;
            if (_catalogs.TryGetValue(name, out var cached)) return cached;
            var directory = Path.Combine(AppContext.BaseDirectory, "assets", name);
            if (!File.Exists(Path.Combine(directory, "recognition.json")))
                throw new FlowStepException($"找不到资产包「{name}」（assets\\{name}\\recognition.json）。");
            var catalog = new TemplateCatalog(directory);
            _catalogs[name] = catalog;
            return catalog;
        }

        private Task<FrameSnapshot> FreshAsync(CancellationToken token)
            => context.Frames.ReadAfterAsync(TimeSpan.Zero, token);

        public void Dispose() => _catalogs.Clear();
    }
}
