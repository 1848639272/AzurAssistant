using System.Diagnostics;

namespace AzurAssistant.Contracts;

public enum PauseToggleResult { None, Paused, Resumed }

/// <summary>One task session's active clock and cooperative execution gates; standalone tasks use real time.</summary>
public class TaskExecution
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private static readonly IDisposable NoAction = new EmptyAction();
    public static TaskExecution Uncontrolled { get; } = new();
    public virtual TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);
    public virtual int Generation => 0;
    public virtual bool IsObservationCurrent(FrameSnapshot observation) => true;
    public virtual Task WaitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
    public virtual Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
    public virtual TaskExecutionDeadline CreateDeadline(CancellationToken token) => new(this, token);
    public virtual Task<IDisposable> EnterActionAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(NoAction);
    }
    private sealed class EmptyAction : IDisposable { public void Dispose() { } }
}

/// <summary>A resettable cancellation deadline measured by its session's active clock.</summary>
public sealed class TaskExecutionDeadline(TaskExecution execution, CancellationToken token) : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = CancellationTokenSource.CreateLinkedTokenSource(token);
    private CancellationTokenSource? _timer;
    private bool _disposed;
    public CancellationToken Token => _source.Token;

    public void CancelAfter(TimeSpan delay)
    {
        if (delay != Timeout.InfiniteTimeSpan && (delay < TimeSpan.Zero || delay.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(delay));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timer?.Cancel();
            _timer?.Dispose();
            _timer = null;
            if (_source.IsCancellationRequested || delay == Timeout.InfiniteTimeSpan) return;
            _timer = new CancellationTokenSource();
            _ = ExpireAsync(_timer, delay);
        }
    }

    private async Task ExpireAsync(CancellationTokenSource timer, TimeSpan delay)
    {
        try
        {
            await execution.DelayAsync(delay, timer.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_disposed && ReferenceEquals(_timer, timer) && !timer.IsCancellationRequested) _source.Cancel();
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Cancel();
            _timer?.Dispose();
            _source.Dispose();
        }
    }
}
