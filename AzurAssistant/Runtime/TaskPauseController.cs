using System.Diagnostics;
using AzurAssistant.Contracts;

namespace AzurAssistant.Runtime;

/// <summary>Coordinates one run's pause clock, short actions and resume observation boundary.</summary>
public sealed class TaskPauseController : TaskExecution, IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _action = new(1, 1);
    private readonly SemaphoreSlim _transition = new(1, 1);
    private TaskCompletionSource _changed = NewSignal();
    private TimeSpan _activeTime;
    private long _activeSince = Stopwatch.GetTimestamp();
    private TimeSpan _observationBoundary = TimeSpan.MinValue;
    private bool _pauseRequested, _paused, _finishing, _ended;
    private int _generation;

    public bool IsPaused { get { lock (_gate) return _paused && !_ended; } }
    public override TimeSpan Elapsed { get { lock (_gate) return ElapsedLocked(); } }
    public override int Generation { get { lock (_gate) return _generation; } }
    public override bool IsObservationCurrent(FrameSnapshot observation)
    { lock (_gate) return !_ended && observation.SystemTimestamp > _observationBoundary; }

    public override async Task WaitAsync(CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfEnded(token);
                if (!_pauseRequested) return;
                changed = _changed.Task;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public override async Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        if (delay == Timeout.InfiniteTimeSpan) { await Task.Delay(delay, token).ConfigureAwait(false); return; }
        if (delay < TimeSpan.Zero || delay.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(delay));
        var due = Elapsed + delay;
        while (true)
        {
            await WaitAsync(token).ConfigureAwait(false);
            TimeSpan remaining;
            Task changed;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfEnded(token);
                if (_pauseRequested) continue;
                remaining = due - ElapsedLocked();
                if (remaining <= TimeSpan.Zero) return;
                changed = _changed.Task;
            }
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                var delayTask = Task.Delay(remaining, timer.Token);
                var completed = await Task.WhenAny(delayTask, changed).WaitAsync(token).ConfigureAwait(false);
                if (ReferenceEquals(completed, delayTask)) await delayTask.ConfigureAwait(false);
            }
            finally { timer.Cancel(); }
        }
    }

    public override async Task<IDisposable> EnterActionAsync(CancellationToken token)
    {
        while (true)
        {
            await WaitAsync(token).ConfigureAwait(false);
            await _action.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    ThrowIfEnded(token);
                    if (!_pauseRequested && !_finishing) return new ActionLease(_action);
                    if (_finishing) throw new OperationCanceledException("当前功能已进入收尾，不能发送新动作。", token);
                }
            }
            catch { _action.Release(); throw; }
            _action.Release();
        }
    }

    internal async Task<PauseToggleResult> ToggleAsync(Action releaseInput,
        Func<CancellationToken, Task> activateOnResume, CancellationToken token)
    {
        await _transition.WaitAsync(token).ConfigureAwait(false);
        try
        {
            bool resume;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_ended || _finishing) return PauseToggleResult.None;
                resume = _pauseRequested;
                if (!resume)
                {
                    _activeTime = ElapsedLocked();
                    _pauseRequested = true;
                    PulseLocked();
                }
            }
            await _action.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!resume)
                {
                    releaseInput();
                    lock (_gate)
                    {
                        if (_ended || _finishing) return PauseToggleResult.None;
                        _paused = true;
                    }
                    return PauseToggleResult.Paused;
                }
                await activateOnResume(token).ConfigureAwait(false);
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (_ended || _finishing) return PauseToggleResult.None;
                    _observationBoundary = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
                    _generation++;
                    _activeSince = Stopwatch.GetTimestamp();
                    _pauseRequested = _paused = false;
                    PulseLocked();
                }
                return PauseToggleResult.Resumed;
            }
            finally { _action.Release(); }
        }
        finally { _transition.Release(); }
    }

    internal async Task CompleteAsync(CancellationToken token)
    {
        while (true)
        {
            await WaitAsync(token).ConfigureAwait(false);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_pauseRequested) continue;
                _finishing = true;
                return;
            }
        }
    }

    internal void End()
    {
        lock (_gate)
        {
            if (_ended) return;
            _activeTime = ElapsedLocked();
            _ended = true;
            PulseLocked();
        }
    }

    internal async Task DrainActionsAsync()
    {
        await _action.WaitAsync().ConfigureAwait(false);
        _action.Release();
    }

    private TimeSpan ElapsedLocked() => _pauseRequested || _ended
        ? _activeTime : _activeTime + Stopwatch.GetElapsedTime(_activeSince);
    private void ThrowIfEnded(CancellationToken token)
    { if (_ended) throw new OperationCanceledException("当前功能已结束。", token); }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void PulseLocked() { var old = _changed; _changed = NewSignal(); old.TrySetResult(); }
    public void Dispose() => End();
    private sealed class ActionLease(SemaphoreSlim action) : IDisposable
    {
        private SemaphoreSlim? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Release();
    }
}
