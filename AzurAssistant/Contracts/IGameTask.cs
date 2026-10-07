namespace AzurAssistant.Contracts;

public interface IGameTask
{
    string Id { get; }
    string Name { get; }
    Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token);
}

/// <summary>Reports task outcome; application exit is a host intent consumed only after run cleanup has completed.</summary>
public sealed record TaskResult(bool Success, string Message, bool FailureReported = false, bool RequestApplicationExit = false);
public sealed class ElevationRequiredException() : InvalidOperationException("游戏以管理员权限运行，请先以管理员身份重启助手，再运行功能。");
/// <summary>An input precondition is unavailable. A continuing hold releases its owned key before this error propagates.</summary>
public class InputUnavailableException(string message) : InvalidOperationException(message);
/// <summary>Resuming invalidated an observation before any input was sent; callers must observe again.</summary>
public sealed class ObservationInvalidatedException() : InputUnavailableException("暂停恢复后原画面已失效，请重新观察。");
public sealed record TaskContext(IFrameFeed Frames, IGameInput Input, Action<string> Log,
    ITaskFailureReporter? Failures = null, TaskExecution? ExecutionControl = null)
{
    public TaskExecution Execution => ExecutionControl ?? TaskExecution.Uncontrolled;
    public string? TaskPath { get; private init; }
    private Action<string>? LogSink { get; init; }

    /// <summary>Names a child operation while sharing the same capture, input owner, execution and failure session.</summary>
    public TaskContext ForTask(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var path = TaskPath is null ? name : $"{TaskPath}-{name}";
        var sink = LogSink ?? Log;
        return this with { TaskPath = path, LogSink = sink, Log = message => sink($"[{path}] {message}") };
    }
}

/// <summary>Records failure evidence in the current shared capture session; never sends game input.</summary>
public interface ITaskFailureReporter
{
    void SetTask(string id, string name);
    Task ReportAsync(string reason, Exception? error = null);
}

public interface IFrameFeed
{
    Task<FrameSnapshot> ReadAfterAsync(TimeSpan timestamp, CancellationToken token);
}

public readonly record struct ClientPoint(int X, int Y);
public enum GameKey { Escape = 0x1B, Interact = 0x46, AutoBattle = 0x70, Slot1 = 0x31, Slot2 = 0x32, Slot3 = 0x33, Q = 0x51, E = 0x45, R = 0x52,
    Map = 0x4D, Forward = 0x57, Left = 0x41, Backward = 0x53, Right = 0x44, MovementMode = 0x11, Jump = 0x20 }

/// <summary>A single held movement key that expires without fresh observations; pause, cancellation and disposal release it.</summary>
public interface IContinuousKeyHold : IDisposable
{
    Task RefreshAsync(FrameSnapshot observation, CancellationToken token);
}

public interface IGameInput
{
    Task ActivateAsync(CancellationToken token);
    Task ClickAsync(FrameSnapshot observation, ClientPoint point, CancellationToken token);
    /// <summary>A bounded left-button drag, cancelled and released when the foreground or viewport changes.</summary>
    Task DragAsync(FrameSnapshot observation, ClientPoint from, ClientPoint to, CancellationToken token)
        => throw new NotSupportedException("当前输入后端不支持拖动。");
    /// <summary>Clicks at the current pointer position without moving the mouse.</summary>
    Task ClickCurrentAsync(FrameSnapshot observation, CancellationToken token)
        => throw new NotSupportedException("当前输入后端不支持原位点击。");
    Task PressKeyAsync(FrameSnapshot observation, GameKey key, CancellationToken token)
        => throw new NotSupportedException("当前输入后端不支持键盘。");
    /// <summary>Holds one movement key for at most 400ms, revalidating foreground/geometry and releasing on every exit.</summary>
    Task HoldKeyAsync(FrameSnapshot observation, GameKey key, TimeSpan duration, CancellationToken token)
        => throw new NotSupportedException("当前输入后端不支持短步移动。");
    /// <summary>A bounded relative pointer movement without pressing a button, guarded by the same observation and lease.</summary>
    Task MovePointerRelativeAsync(FrameSnapshot observation, int horizontal, int vertical, CancellationToken token)
        => throw new NotSupportedException("当前输入后端不支持相对转向。");
    Task<IContinuousKeyHold> BeginKeyHoldAsync(FrameSnapshot observation, GameKey key, CancellationToken token)
        => throw new NotSupportedException("当前输入后端不支持可续期长按。");
    void ReleaseAll();
}
