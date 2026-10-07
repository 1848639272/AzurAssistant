using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using AzurAssistant.Contracts;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace AzurAssistant.Platform;

/// <summary>Windows window capture, never a desktop screen crop. No game input.</summary>
public sealed class WindowsGraphicsFrameSource : IFrameSource
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private readonly GameWindow _window;
    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _size;
    private WindowGeometry? _geometry;
    private long _viewportVersion;
    private long _sequence;
    private bool _recreate;

    public WindowsGraphicsFrameSource(GameWindow window)
    {
        _window = window;
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("当前系统不支持 Windows 窗口捕获。");
            window.Observe();
            var item = NativeMethods.CreateCaptureItem(window.Handle);
            _size = item.Size;
            _device = NativeMethods.CreateDevice();
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            _session.StartCapture();
        }
        catch { Dispose(); throw; }
    }

    public async Task<FrameSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_pool is null, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        var received = 0;
        var stale = 0;
        var geometryChanges = 0;
        var stage = "等待帧池新帧";
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                stage = "检查游戏窗口";
                var before = _window.Observe();
                if (_recreate)
                {
                    _pool.Recreate(_device!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                    _recreate = false;
                }
                stage = "等待帧池新帧";
                using var frame = TakeLatestFrame();
                if (frame is null) { await Task.Delay(15, timeout.Token); continue; }
                received++;
                var content = frame.ContentSize;
                if (content.Width <= 0 || content.Height <= 0) continue;
                if (content.Width != _size.Width || content.Height != _size.Height)
                {
                    _size = content;
                    // Recreate next iteration, after the using scope releases this frame.
                    _recreate = true;
                    _geometry = null;
                    geometryChanges++;
                    continue;
                }
                var timestamp = frame.SystemRelativeTime;
                if (SystemNow - timestamp > TimeSpan.FromSeconds(1)) { stale++; continue; }
                stage = "复制捕获表面";
                using var software = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore).AsTask(timeout.Token);
                var after = _window.Observe();
                if (before != after) { geometryChanges++; continue; }
                var x = before.CropX;
                var y = before.CropY;
                if (x < 0 || y < 0 || x + before.Width > content.Width || y + before.Height > content.Height)
                {
                    geometryChanges++;
                    continue; // Window geometry and captured surface have not converged yet.
                }
                var full = new byte[checked(software.PixelWidth * software.PixelHeight * 4)];
                software.CopyToBuffer(full.AsBuffer());
                var pixels = new byte[checked(before.Width * before.Height * 4)];
                for (var row = 0; row < before.Height; row++)
                    Buffer.BlockCopy(full, ((row + y) * software.PixelWidth + x) * 4, pixels, row * before.Width * 4, before.Width * 4);
                if (SystemNow - timestamp > TimeSpan.FromSeconds(1)) { stale++; continue; }
                if (_geometry != before) { _viewportVersion++; _geometry = before; }
                return new FrameSnapshot(++_sequence, timestamp, DateTimeOffset.Now - (SystemNow - timestamp), before.Width, before.Height,
                    _viewportVersion, _window.Handle, pixels, before.X, before.Y, _window.ProcessId);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Observe again so an invalid window remains a hard failure rather than a retryable timeout.
            _window.Observe();
            // The failed read has released all native frames. The next read resets the same pool,
            // preserving source identity, frame sequence and unchanged viewport geometry.
            _recreate = true;
            var foreground = NativeMethods.GetForegroundWindow() == _window.Handle ? "是" : "否";
            throw new TimeoutException($"{ReadTimeout.TotalSeconds:0} 秒内未取得新的游戏画面（阶段：{stage}，收到帧 {received}，过期帧 {stale}，几何不一致 {geometryChanges}，游戏前台：{foreground}）。");
        }
    }

    internal static TimeSpan SystemNow => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);

    private Direct3D11CaptureFrame? TakeLatestFrame()
    {
        Direct3D11CaptureFrame? latest = null;
        try
        {
            // Bounded by the two-frame pool: discard queued older frames without an unbounded drain loop.
            for (var i = 0; i < 2; i++)
            {
                var next = _pool!.TryGetNextFrame();
                if (next is null) break;
                latest?.Dispose();
                latest = next;
            }
            return latest;
        }
        catch { latest?.Dispose(); throw; }
    }

    public void Dispose()
    {
        try { _session?.Dispose(); }
        finally
        {
            _session = null;
            try { _pool?.Dispose(); }
            finally { _pool = null; _device?.Dispose(); _device = null; }
        }
    }
}
