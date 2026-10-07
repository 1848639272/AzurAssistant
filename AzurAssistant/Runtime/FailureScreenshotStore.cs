using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzurAssistant.Contracts;

namespace AzurAssistant.Runtime;

public sealed record FailureScreenshot(string TaskId, string TaskName, string Reason,
    DateTimeOffset FailedAt, FrameSnapshot Frame, double FrameAgeMilliseconds,
    bool Stale, string CaptureStatus);

public interface IFailureScreenshotStore
{
    Task<string> SaveAsync(FailureScreenshot screenshot, CancellationToken token);
}

/// <summary>Saves only owned game frames. A single writer prevents a slow disk from queuing image copies.</summary>
public sealed class FailureScreenshotStore(DailyLogStore log) : IFailureScreenshotStore
{
    internal const string FilePrefix = "AzurAssistant-failure-";
    private const int MaximumPixels = 16_777_216;
    private readonly SemaphoreSlim _writer = new(1, 1);

    public Task<string> SaveAsync(FailureScreenshot screenshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_writer.Wait(0)) throw new IOException("上一张异常截图仍在保存，本次不排队。");
        return Task.Run(() =>
        {
            string? temporaryImage = null, temporaryMetadata = null, imagePath = null;
            var completed = false;
            try
            {
                var frame = screenshot.Frame;
                var pixels = checked((long)frame.Width * frame.Height);
                if (frame.Width <= 0 || frame.Height <= 0 || pixels > MaximumPixels || frame.Pixels.Length != pixels * 4)
                    throw new InvalidDataException("异常截图的像素尺寸无效或超过 1677 万像素上限。");
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(log.DirectoryPath);
                var id = string.Concat(screenshot.TaskId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(48));
                if (id.Length == 0) id = "task";
                var timestamp = screenshot.FailedAt.ToString("yyyy-MM-dd-HHmmss-fff", CultureInfo.InvariantCulture);
                var name = $"{FilePrefix}{timestamp}-{id}-f{frame.Id}-{Guid.NewGuid():N}";
                imagePath = Path.Combine(log.DirectoryPath, name + ".png");
                var metadataPath = Path.Combine(log.DirectoryPath, name + ".json");
                temporaryImage = imagePath + ".tmp";
                temporaryMetadata = metadataPath + ".tmp";
                var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32,
                    null, frame.Pixels.ToArray(), frame.Stride);
                bitmap.Freeze();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = new FileStream(temporaryImage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    encoder.Save(output);
                token.ThrowIfCancellationRequested();
                File.WriteAllText(temporaryMetadata, JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1, screenshot.TaskId, screenshot.TaskName, screenshot.Reason,
                    screenshot.FailedAt, frame.CapturedAt, frame.Id, frame.ProcessId,
                    WindowHandle = (long)frame.WindowHandle, frame.ViewportVersion, frame.Width, frame.Height,
                    screenshot.FrameAgeMilliseconds, screenshot.Stale, screenshot.CaptureStatus
                }, new JsonSerializerOptions { WriteIndented = true }));
                token.ThrowIfCancellationRequested();
                File.Move(temporaryImage, imagePath);
                File.Move(temporaryMetadata, metadataPath);
                completed = true;
                log.Maintain();
                return imagePath;
            }
            finally
            {
                TryDelete(temporaryImage);
                TryDelete(temporaryMetadata);
                if (!completed) TryDelete(imagePath);
                _writer.Release();
            }
        }, CancellationToken.None);
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Captures evidence before recovery changes the screen; screenshot errors never replace the task failure.</summary>
internal sealed class FailureDiagnosticSession(CapturePreviewService preview, IFailureScreenshotStore store,
    DailyLogStore log, string taskId, string taskName) : ITaskFailureReporter
{
    private readonly HashSet<Exception> _reportedErrors = new(ReferenceEqualityComparer.Instance);
    private string _taskId = taskId;
    private string _taskName = taskName;
    internal static TimeSpan SaveTimeout { get; } = TimeSpan.FromSeconds(2);

    public void SetTask(string id, string name) { _taskId = id; _taskName = name; }

    public async Task ReportAsync(string reason, Exception? error = null)
    {
        if (error is not null && !_reportedErrors.Add(error)) return;
        try
        {
            var state = preview.Current;
            var frame = preview.DiagnosticFrame;
            if (frame is null)
            {
                log.Write($"异常截图未保存（{_taskName}）：本次捕获会话尚无有效游戏帧；{state.Message}；原因：{reason}");
                return;
            }
            var now = DateTimeOffset.Now;
            var timestamp = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
            var age = (timestamp - frame.SystemTimestamp).TotalMilliseconds;
            var stale = state.State is PreviewState.Faulted or PreviewState.Idle or PreviewState.Stopping || age < -50 || age > 1000;
            var screenshot = new FailureScreenshot(_taskId, _taskName, reason, now, frame, age, stale, state.Message);
            using var budget = new CancellationTokenSource(SaveTimeout);
            var save = store.SaveAsync(screenshot, budget.Token);
            string path;
            try { path = await save.WaitAsync(budget.Token).ConfigureAwait(false); }
            catch
            {
                // A non-cooperative encoder can finish later; observe its result without holding input or the runner.
                _ = save.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            var freshness = stale ? "最后可得画面（陈旧或捕获已中断，并非故障时的新截图）" : "故障时最近的有效游戏画面";
            log.Write($"异常截图已保存（{_taskName}）：{path}；{freshness}，采集时间 {frame.CapturedAt:yyyy-MM-dd HH:mm:ss.fff zzz}；原因：{reason}");
        }
        catch (Exception ex)
        {
            log.Write($"异常截图保存失败（{_taskName}），原任务错误保留：{(ex is OperationCanceledException ? "保存超过 2 秒预算" : ex.Message)}；原因：{reason}");
        }
    }
}
