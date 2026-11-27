using AzurAssistant.Contracts;

namespace AzurAssistant.Runtime;

/// <summary>Only an active task lease can call the underlying input adapter.</summary>
public sealed class InputCoordinator(IGameInput backend, Action<ResponseTiming>? configureTiming = null)
{
    private readonly SemaphoreSlim _owner = new(1, 1);
    public Lease Acquire(ResponseTiming? timing = null, TaskExecution? execution = null)
    {
        if (!_owner.Wait(0)) throw new InvalidOperationException("已有功能占用游戏输入。");
        try
        {
            configureTiming?.Invoke(timing ?? ResponseTiming.FromLowPerformanceMode(false));
            return new Lease(backend, _owner, execution);
        }
        catch { _owner.Release(); throw; }
    }

    public sealed class Lease(IGameInput backend, SemaphoreSlim owner, TaskExecution? execution = null) : IGameInput, IDisposable
    {
        private readonly TaskExecution _execution = execution ?? TaskExecution.Uncontrolled;
        private ForegroundRecovery? _focus;
        private TimeSpan _clickInterval, _nextClickAt;
        private readonly SemaphoreSlim _clickGate = new(1, 1);
        private TaskPauseController? _clickControl;

        private Func<int>? _interactionDelay;
        internal void SetInteractionDelay(Func<int> milliseconds) => _interactionDelay = milliseconds;

        internal void SetClickInterval(TaskPauseController control, TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            _clickControl = control;
            _clickInterval = interval;
        }
        internal void EnableForegroundRecovery(TaskPauseController control, Action<string> log)
        {
            if (backend is IGameWindowFocus window) _focus = new(window, backend, control, log);
        }


        private bool _disposed;
        public async Task ActivateAsync(CancellationToken token)
        {
            using var action = await _execution.EnterActionAsync(token);
            ObjectDisposedException.ThrowIf(_disposed, this);
            await backend.ActivateAsync(token);
        }
        public async Task<bool> TryActivateWindowAsync(nint window, int processId, CancellationToken token)
        {
            using var action = await _execution.EnterActionAsync(token);
            ObjectDisposedException.ThrowIf(_disposed, this);
            backend.ReleaseAll();
            try
            {
                return _focus is null ? await backend.TryActivateWindowAsync(window, processId, token)
                    : await _focus.RequestAsync(window, processId, token);
            }
            finally { if (_execution is TaskPauseController control) control.InvalidateObservations(); }
        }
        public async Task PrepareForObservationAsync(CancellationToken token)
        {
            using var action = await _execution.EnterActionAsync(token);
            await PrepareForResumeAsync(token);
        }
        internal Task PrepareForResumeAsync(CancellationToken token)
        { ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); return _focus?.PrepareForObservationAsync(token) ?? backend.ActivateAsync(token); }
        public Task ClickAsync(FrameSnapshot frame, ClientPoint point, CancellationToken token)
            => ClickWhenReadyAsync(frame, () => backend.ClickAsync(frame, point, token), token);
        public Task ClickCurrentAsync(FrameSnapshot frame, CancellationToken token)
            => ClickWhenReadyAsync(frame, () => backend.ClickCurrentAsync(frame, token), token);

        private async Task ClickWhenReadyAsync(FrameSnapshot frame, Func<Task> click, CancellationToken token)
        {
            if (_clickControl is null) { await ActAsync(frame, click, token); return; }
            await _clickGate.WaitAsync(token);
            try
            {
                var remaining = _nextClickAt - _execution.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await _execution.DelayAsync(remaining, token);
                    _clickControl.InvalidateObservations(changeGeneration: false);
                    throw new ObservationInvalidatedException();
                }
                await ActAsync(frame, async () =>
                {
                    var started = _execution.Elapsed;
                    await click();
                    _nextClickAt = started + _clickInterval;
                }, token);
            }
            finally { _clickGate.Release(); }
        }
        public Task DragAsync(FrameSnapshot frame, ClientPoint from, ClientPoint to, CancellationToken token)
            => ActAsync(frame, async () =>
            {
                try { await backend.DragAsync(frame, from, to, token); }
                catch (InterruptedDragException error) when (_focus is not null)
                {
                    // The mouse was already down. Let the caller observe the resulting list without replaying the gesture.
                    if (!await _focus.ReobserveIfChangedAsync(frame, token, error.InnerException is ForegroundLostException)) throw;
                }
            }, token);
        public Task PressKeyAsync(FrameSnapshot frame, GameKey key, CancellationToken token)
            => ActAsync(frame, () => backend.PressKeyAsync(frame, key, token), token);
        public Task HoldKeyAsync(FrameSnapshot frame, GameKey key, TimeSpan duration, CancellationToken token)
            => ActAsync(frame, () => backend.HoldKeyAsync(frame, key, duration, token), token);
        public Task MovePointerRelativeAsync(FrameSnapshot frame, int horizontal, int vertical, CancellationToken token)
            => ActAsync(frame, () => backend.MovePointerRelativeAsync(frame, horizontal, vertical, token), token);
        public async Task<IContinuousKeyHold> BeginKeyHoldAsync(FrameSnapshot frame, GameKey key, CancellationToken token)
        {
            IContinuousKeyHold? hold = null;
            await ActAsync(frame, async () => hold = await backend.BeginKeyHoldAsync(frame, key, token), token);
            return new ContinuousLease(this, hold!);
        }
        private sealed class ContinuousLease(Lease owner, IContinuousKeyHold backendHold) : IContinuousKeyHold
        {
            private bool _disposed;
            public Task RefreshAsync(FrameSnapshot frame, CancellationToken token)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return owner.ActAsync(frame, () => backendHold.RefreshAsync(frame, token), token, newInteraction: false);
            }
            public void Dispose() { if (_disposed) return; _disposed = true; backendHold.Dispose(); }
        }
        private async Task ActAsync(FrameSnapshot frame, Func<Task> action, CancellationToken token, bool newInteraction = true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var generation = _execution.Generation;
            if (newInteraction && _interactionDelay is not null)
            {
                await _execution.DelayAsync(TimeSpan.FromMilliseconds(_interactionDelay()), token);
                var age = System.Diagnostics.Stopwatch.GetElapsedTime(0) - frame.SystemTimestamp;
                if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(1)) throw new ObservationInvalidatedException();
            }
            using var entered = await _execution.EnterActionAsync(token);
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (generation != _execution.Generation || !_execution.IsObservationCurrent(frame))
                throw new ObservationInvalidatedException();
            if (_focus is not null && await _focus.ReobserveIfChangedAsync(frame, token)) throw new ObservationInvalidatedException();
            try { await action(); _focus?.InputCompleted(); }
            catch (InputUnavailableException error) when (_focus is not null)
            {
                if (await _focus.ReobserveIfChangedAsync(frame, token, error is ForegroundLostException)) throw new ObservationInvalidatedException();
                throw;
            }
        }
        public void ReleaseAll() { if (!_disposed) backend.ReleaseAll(); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { backend.ReleaseAll(); } finally { owner.Release(); }
        }
    }
}
