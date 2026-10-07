using System.Runtime.ExceptionServices;
using AzurAssistant.Contracts;

namespace AzurAssistant.Runtime;

public sealed record TaskRunOptions(TimeSpan? Timeout, Func<CancellationToken, Task>? PrepareAsync = null,
    bool RequiresGameInput = true, ResponseTiming? ResponseTiming = null,
    Func<TaskExecution, CancellationToken, Task>? PrepareWithExecutionAsync = null);

public sealed class TaskRunner(CapturePreviewService preview, InputCoordinator input, DailyLogStore log,
    IFailureScreenshotStore? failureScreenshots = null)
{
    private readonly object _gate = new();
    private readonly IFailureScreenshotStore _failureScreenshots = failureScreenshots ?? new FailureScreenshotStore(log);
    private RunSession? _session;
    private Task<TaskResult> _run = Task.FromResult(new TaskResult(false, "尚未运行"));
    private bool _running;
    public bool IsRunning { get { lock (_gate) return _running; } }
    public bool IsPaused { get { lock (_gate) return _running && _session?.Execution.IsPaused == true; } }
    public string Status { get; private set; } = "等待开始";
    public TaskResult? LastResult { get; private set; }
    public bool RequiresElevation { get; private set; }
    public Task<TaskResult> Completion { get { lock (_gate) return _run; } }

    public bool Start(IGameTask task, TaskRunOptions? options = null)
    {
        lock (_gate)
        {
            if (_running) return false;
            options ??= new TaskRunOptions(TimeSpan.FromSeconds(90));
            if (options.Timeout is { } requestedTimeout
                && (requestedTimeout < TimeSpan.Zero || requestedTimeout.TotalMilliseconds > uint.MaxValue - 1))
                throw new ArgumentOutOfRangeException(nameof(options), "运行时限必须是有效的非负时长，或为空表示不限时。");
            if (options.PrepareAsync is not null && options.PrepareWithExecutionAsync is not null)
                throw new ArgumentException("运行准备只能配置一种回调。", nameof(options));
            _session?.Cancellation.Dispose();
            var session = _session = new RunSession();
            _running = true;
            LastResult = null;
            RequiresElevation = false;
            _run = RunAsync(task, options, session);
            return true;
        }
    }

    public async Task StopAsync()
    {
        Task run;
        lock (_gate)
        {
            if (!_running) return;
            Status = "正在取消…";
            _session!.Cancellation.Cancel();
            run = _run;
        }
        await run.ConfigureAwait(false);
    }

    public async Task<PauseToggleResult> TogglePauseAsync()
    {
        RunSession session;
        CancellationToken runToken;
        lock (_gate)
        {
            if (!_running || _session is null || _session.RunToken.IsCancellationRequested) return PauseToggleResult.None;
            session = _session;
            runToken = session.RunToken;
        }
        try
        {
            var result = await session.Execution.ToggleAsync(
                () => session.Lease?.ReleaseAll(),
                token => session.HasActivated && (!session.HasStartedPreview || preview.IsRunning) && session.Lease is { } lease
                    ? lease.ActivateForResumeAsync(token) : Task.CompletedTask,
                runToken);
            lock (_gate)
            {
                if (!ReferenceEquals(_session, session) || !_running || runToken.IsCancellationRequested)
                    return PauseToggleResult.None;
                if (result == PauseToggleResult.Paused && session.Execution.IsPaused) SetStatus("功能已暂停");
                else if (result == PauseToggleResult.Resumed && !session.Execution.IsPaused) SetStatus("功能已继续，重新观察游戏画面");
            }
            return result;
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested) { return PauseToggleResult.None; }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_session, session) || !_running || runToken.IsCancellationRequested)
                    return PauseToggleResult.None;
                session.PauseFailure = ex;
                session.Cancellation.Cancel();
                log.Write($"暂停/继续失败，已停止当前功能：{ex.Message}", LogLevel.Error);
            }
            return PauseToggleResult.None;
        }
    }

    private async Task<TaskResult> RunAsync(IGameTask task, TaskRunOptions options, RunSession session)
    {
        var execution = session.Execution;
        using var deadline = execution.CreateDeadline(CancellationToken.None);
        if (options.Timeout is { } timeout) deadline.CancelAfter(timeout);
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token, deadline.Token);
        var token = runCancellation.Token;
        session.RunToken = token;
        var failures = new FailureDiagnosticSession(preview, _failureScreenshots, log, task.Id, task.Name);
        TaskResult? taskResult = null;
        Exception? taskError = null;
        TaskResult completedResult;
        try
        {
            try
            {
                session.Lease = input.Acquire(options.ResponseTiming, execution);
                await preview.StopAsync();
                await execution.WaitAsync(token);
                if (options.PrepareWithExecutionAsync is { } controlledPrepare)
                {
                    SetStatus($"{task.Name}：正在准备");
                    await controlledPrepare(execution, token);
                }
                else if (options.PrepareAsync is { } prepare)
                {
                    SetStatus($"{task.Name}：正在准备");
                    await prepare(token);
                }
                await execution.WaitAsync(token);
                if (options.RequiresGameInput)
                {
                    SetStatus($"{task.Name}：正在将游戏置于前台");
                    // Initial activation stays on the caller's foreground UI thread where possible.
                    await session.Lease.ActivateAsync(token);
                    session.HasActivated = true;
                    await execution.WaitAsync(token);
                    preview.Start();
                    session.HasStartedPreview = true;
                    SetStatus($"{task.Name}：正在观察游戏画面");
                }
                await execution.WaitAsync(token);
                var frames = new ExecutionFrameFeed(preview, execution);
                taskResult = await Task.Run(() => task.ExecuteAsync(new TaskContext(frames, session.Lease,
                    message => log.Write(message), failures, execution).ForTask(task.Name), token), token);
            }
            catch (Exception ex) { taskError = ex; }

            // A completed observation or an error cannot advance finalization while the user has paused.
            await execution.CompleteAsync(token);
            token.ThrowIfCancellationRequested();
            if (taskError is not null) ExceptionDispatchInfo.Capture(taskError).Throw();
            LastResult = taskResult ?? new(false, "未取得任务结果");
            await ReleaseBeforeDiagnosticsAsync();
            if (!LastResult.Success && !LastResult.FailureReported)
                await failures.ReportAsync(LastResult.Message);
            SetStatus(LastResult.Message, LastResult.Success ? LogLevel.Info : LogLevel.Error);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LastResult = CancellationResult();
            await ReleaseBeforeDiagnosticsAsync();
            SetStatus(LastResult.Message, session.PauseFailure is not null || deadline.Token.IsCancellationRequested ? LogLevel.Error : LogLevel.Warning);
            if (session.PauseFailure is { } pauseFailure)
            {
                RequiresElevation = pauseFailure is ElevationRequiredException;
                log.Write($"异常详情：{pauseFailure}", LogLevel.Error);
                await failures.ReportAsync(LastResult.Message, pauseFailure);
            }
            else if (deadline.Token.IsCancellationRequested) await failures.ReportAsync(LastResult.Message);
        }
        catch (Exception ex)
        {
            RequiresElevation = ex is ElevationRequiredException;
            LastResult = new(false, $"功能停止：{ex.Message}");
            await ReleaseBeforeDiagnosticsAsync();
            SetStatus(LastResult.Message, LogLevel.Error);
            log.Write($"异常详情：{ex}", LogLevel.Error);
            await failures.ReportAsync(LastResult.Message, ex);
        }
        finally
        {
            execution.End();
            // Resume activation runs outside the feature body; finish its bounded action before returning input ownership.
            await execution.DrainActionsAsync();
            try { session.Lease?.Dispose(); }
            catch (Exception ex)
            {
                if (LastResult?.Success != false) LastResult = new(false, $"功能停止：输入清理失败：{ex.Message}");
                log.Write($"输入清理异常：{ex}", LogLevel.Error);
            }
            lock (_gate)
            {
                if (token.IsCancellationRequested) LastResult = CancellationResult();
                completedResult = LastResult ?? new TaskResult(false, "未取得任务结果");
                log.Write($"最终结果：{(completedResult.Success ? "成功" : "失败")}；任务={task.Name} ({task.Id})；{completedResult.Message}",
                    completedResult.Success ? LogLevel.Info : LogLevel.Error);
                _running = false;
            }
        }
        return completedResult;

        TaskResult CancellationResult() => new(false, session.PauseFailure is { } pauseFailure
            ? $"功能停止：暂停/继续失败：{pauseFailure.Message}"
            : deadline.Token.IsCancellationRequested ? "功能已达到运行时限" : "功能已取消");

        async Task ReleaseBeforeDiagnosticsAsync()
        {
            execution.End();
            await execution.DrainActionsAsync();
            try { session.Lease?.ReleaseAll(); }
            catch (Exception ex)
            {
                if (LastResult?.Success != false) LastResult = new(false, $"功能停止：输入清理失败：{ex.Message}");
                log.Write($"输入清理异常：{ex}", LogLevel.Error);
            }
        }
    }

    private void SetStatus(string message, LogLevel level = LogLevel.Info) { Status = message; log.Write(message, level); }

    private sealed class RunSession
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly TaskPauseController Execution = new();
        public volatile InputCoordinator.Lease? Lease;
        public volatile bool HasActivated;
        public volatile bool HasStartedPreview;
        public CancellationToken RunToken;
        public Exception? PauseFailure;
    }

    private sealed class ExecutionFrameFeed(IFrameFeed frames, TaskExecution execution) : IFrameFeed
    {
        public async Task<FrameSnapshot> ReadAfterAsync(TimeSpan timestamp, CancellationToken token)
        {
            while (true)
            {
                await execution.WaitAsync(token);
                var generation = execution.Generation;
                FrameSnapshot observation;
                try { observation = await frames.ReadAfterAsync(timestamp, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { await execution.WaitAsync(token); throw; }
                await execution.WaitAsync(token);
                if (generation == execution.Generation && execution.IsObservationCurrent(observation)) return observation;
                if (observation.SystemTimestamp > timestamp) timestamp = observation.SystemTimestamp;
            }
        }
    }
}
