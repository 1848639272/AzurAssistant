using System.Diagnostics;
using AzurAssistant.Contracts;

namespace AzurAssistant.Runtime;

public enum PreviewState { Idle, Connecting, Live, Stopping, Faulted }
public sealed record PreviewSnapshot(PreviewState State, string Message, FrameSnapshot? Frame = null);

/// <summary>Owns the only capture loop. UI polls the latest snapshot, so frames never queue on the dispatcher.</summary>
public sealed class CapturePreviewService(Func<IFrameSource> sourceFactory, Action<string>? log = null) : IFrameFeed
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task _run = Task.CompletedTask;
    private PreviewSnapshot _current = new(PreviewState.Idle, "画面预览已停止");
    private FrameSnapshot? _diagnosticFrame;
    public PreviewSnapshot Current { get { lock (_gate) return _current; } }
    /// <summary>Last structurally valid captured frame, including black loading frames; for evidence only, never input.</summary>
    public FrameSnapshot? DiagnosticFrame { get { lock (_gate) return _diagnosticFrame; } }
    public bool IsRunning { get { lock (_gate) return !_run.IsCompleted; } }

    public bool Start()
    {
        lock (_gate)
        {
            if (!_run.IsCompleted) return false;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _current = new(PreviewState.Connecting, "正在连接游戏窗口…");
            _diagnosticFrame = null;
            log?.Invoke(_current.Message);
            _run = Task.Run(() => RunAsync(_cancellation.Token));
            return true;
        }
    }

    public async Task StopAsync()
    {
        Task run;
        lock (_gate)
        {
            if (_run.IsCompleted) { _current = new(PreviewState.Idle, "画面预览已停止"); return; }
            _current = new(PreviewState.Stopping, "正在停止画面预览…");
            _cancellation!.Cancel();
            run = _run;
        }
        await run.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        PreviewSnapshot terminal = new(PreviewState.Idle, "画面预览已停止");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = sourceFactory();
            long lastId = 0;
            TimeSpan lastTimestamp = TimeSpan.MinValue;
            var retryPending = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FrameSnapshot frame;
                try { frame = await source.ReadAsync(cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (retryPending)
                        throw new TimeoutException($"获取游戏画面重试 1/1 后仍未取得有效新帧；{ex.Message} 请检查游戏窗口后重新连接。", ex);
                    retryPending = true;
                    lock (_gate)
                    {
                        _current = new(PreviewState.Connecting, $"{ex.Message} 获取游戏画面重试 1/1，等待新帧…");
                        log?.Invoke(_current.Message);
                    }
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var now = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
                if (frame.Id <= lastId || frame.SystemTimestamp <= lastTimestamp || now - frame.SystemTimestamp > TimeSpan.FromSeconds(1)
                    || frame.SystemTimestamp > now + TimeSpan.FromMilliseconds(50))
                    throw new InvalidOperationException("收到过期或重复画面，请重新连接。");
                if (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels.Length != checked(frame.Width * frame.Height * 4))
                    throw new InvalidOperationException("画面数据尺寸异常，请重新连接。");
                lastId = frame.Id;
                lastTimestamp = frame.SystemTimestamp;
                lock (_gate) _diagnosticFrame = frame;
                if (IsBlack(frame.Pixels.Span))
                {
                    lock (_gate)
                    {
                        const string blackMessage = "画面暂时全黑，等待新画面";
                        if (_current.Message != blackMessage) log?.Invoke(blackMessage);
                        _current = new(PreviewState.Connecting, blackMessage);
                    }
                    // Loading transitions can be black. Publish no usable frame, but keep observing.
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                lock (_gate)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        if (retryPending) log?.Invoke("游戏画面已恢复，继续观察新帧");
                        if (_current.State != PreviewState.Live) log?.Invoke("正在获取游戏画面");
                        _current = new(PreviewState.Live, "正在获取游戏画面", frame);
                    }
                }
                retryPending = false;
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            terminal = new(PreviewState.Faulted, $"{ex.Message}（{ex.GetType().Name}）");
        }
        finally
        {
            lock (_gate)
            {
                _current = cancellationToken.IsCancellationRequested ? new(PreviewState.Idle, "画面预览已停止") : terminal;
                log?.Invoke(_current.Message);
            }
        }
    }

    private static bool IsBlack(ReadOnlySpan<byte> pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 2 || pixels[i + 1] > 2 || pixels[i + 2] > 2) return false;
        return true;
    }

    public async Task<FrameSnapshot> ReadAfterAsync(TimeSpan timestamp, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = Current;
            if (snapshot.State is PreviewState.Faulted or PreviewState.Idle)
                throw new InvalidOperationException(snapshot.Message);
            var now = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
            if (snapshot.Frame is { } frame && frame.SystemTimestamp > timestamp && now - frame.SystemTimestamp <= TimeSpan.FromSeconds(1)) return frame;
            await Task.Delay(30, token).ConfigureAwait(false);
        }
    }
}
