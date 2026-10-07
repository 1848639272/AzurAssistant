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
        private bool _disposed;
        public async Task ActivateAsync(CancellationToken token)
        {
            using var action = await _execution.EnterActionAsync(token);
            await ActivateForResumeAsync(token);
        }
        internal Task ActivateForResumeAsync(CancellationToken token)
        { ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); return backend.ActivateAsync(token); }
        public Task ClickAsync(FrameSnapshot frame, ClientPoint point, CancellationToken token)
            => ActAsync(frame, () => backend.ClickAsync(frame, point, token), token);
        public Task ClickCurrentAsync(FrameSnapshot frame, CancellationToken token)
            => ActAsync(frame, () => backend.ClickCurrentAsync(frame, token), token);
        public Task DragAsync(FrameSnapshot frame, ClientPoint from, ClientPoint to, CancellationToken token)
            => ActAsync(frame, () => backend.DragAsync(frame, from, to, token), token);
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
                return owner.ActAsync(frame, () => backendHold.RefreshAsync(frame, token), token);
            }
            public void Dispose() { if (_disposed) return; _disposed = true; backendHold.Dispose(); }
        }
        private async Task ActAsync(FrameSnapshot frame, Func<Task> action, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var generation = _execution.Generation;
            using var entered = await _execution.EnterActionAsync(token);
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (generation != _execution.Generation || !_execution.IsObservationCurrent(frame))
                throw new ObservationInvalidatedException();
            await action();
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
