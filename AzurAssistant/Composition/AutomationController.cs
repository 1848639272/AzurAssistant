using System.IO;
using AzurAssistant.Contracts;
using AzurAssistant.Platform;
using AzurAssistant.Runtime;
using AzurAssistant.Features.Commissions;
using AzurAssistant.Features.NavigationTest;
using AzurAssistant.Game.Navigation;
using AzurAssistant.Game.Ui;
using AzurAssistant.Features.Routes;
using AzurAssistant.Features.StartJourney;
using AzurAssistant.Features.Realtime;

namespace AzurAssistant.Composition;

public sealed class AutomationController(
    CapturePreviewService preview, TaskRunner runner, SettingsStore settings, DailyLogStore log,
    Func<AssistantSettings, IGameTask> createDaily, Func<AssistantSettings, IGameTask> createRealtime,
    Func<string, Action<string>, CancellationToken, Task>? launchGame = null, bool enableStartupActions = true,
    CommissionSettingsStore? commissionSettings = null, WeeklyProgressStore? weeklyProgress = null,
    Func<TaskResult, Task>? dailyCompleted = null,
    Func<NavigationTestRequest, Action<NavigationStatus>, IGameTask>? createNavigation = null,
    Func<RouteDefinition, bool, Action<NavigationStatus>, IGameTask>? createRoute = null,
    Func<Action<NavigationStatus>, IRouteRecordingTask>? createRouteRecording = null,
    IReadOnlyList<NavigationMap>? navigationMaps = null, LauncherPreparation? launcherPreparation = null)
{
    private readonly object _completionGate = new();
    private bool _dailyCompletionPending;
    private bool _initialized;
    private NavigationStatus? _navigationStatus;
    public NavigationStatus? NavigationStatus => Volatile.Read(ref _navigationStatus);
    public IReadOnlyList<NavigationMap> NavigationMaps => navigationMaps ?? [];
    private readonly bool _connectOnStartup = settings.Current.AutoConnect;
    private readonly bool _runDailyOnStartup = settings.Current.AutoRunDaily;
    private string? _orderError;
    private string? _startError;
    public Task<TaskResult?> StartupCompletion { get; private set; } = Task.FromResult<TaskResult?>(null);
    public Task<TaskResult> DailyCompletion { get; private set; } = Task.FromResult(new TaskResult(false, "尚未运行"));
    public bool IsBusy { get { lock (_completionGate) return _dailyCompletionPending || runner.IsRunning; } }
    public TaskRunner Runner => runner;
    public AssistantSettings Settings => settings.Current;
    public CommissionSettingsStore? CommissionSettings => commissionSettings;
    public WeeklyProgressStore? WeeklyProgress => weeklyProgress;
    public bool ResetWeeklyProgress()
    {
        if (IsBusy || weeklyProgress is null) return false;
        try { weeklyProgress.Reset(); log.Write("周本本地记录已重置，下次以游戏剩余次数为准"); return true; }
        catch (Exception ex) { log.Write("周本记录重置失败：" + ex.Message); return false; }
    }
    public string? SettingsError => settings.Error ?? _orderError ?? ValidateOrder(Settings);
    public bool SaveSettings(AssistantSettings value)
    {
        _orderError = ValidateOrder(value);
        if (_orderError is not null) { log.Write(_orderError); return false; }
        var saved = settings.Save(value);
        if (!saved) log.Write(settings.Error ?? "设置保存失败");
        return saved;
    }

    private static string? ValidateOrder(AssistantSettings value)
    {
        try { DailyFeatureOrder.Resolve(value.DailyTaskOrder); return null; }
        catch (InvalidDataException ex) { return ex.Message; }
    }

    public void StartConfigured(bool runDailyFromCommandLine)
    {
        if (_initialized) return;
        _initialized = true;
        if (!enableStartupActions) return;
        if (runDailyFromCommandLine || _runDailyOnStartup) StartupCompletion = RunConfiguredDailyAsync();
        else if (SettingsError is { } error) log.Write(error, LogLevel.Error);
        else if (_connectOnStartup) preview.Start();
    }

    private async Task<TaskResult?> RunConfiguredDailyAsync()
    {
        log.Write("自动运行：开始执行已保存的一条龙配置");
        Task<TaskResult> completion;
        lock (_completionGate)
        {
            if (StartDailyCore()) completion = DailyCompletion;
            else
            {
                var result = new TaskResult(false, _startError ?? "一条龙未能启动");
                log.Write($"最终结果：失败；任务=日常一条龙；未启动：{result.Message}", LogLevel.Error);
                // A rejected startup must not take over another run's completion or exit ownership.
                if (IsBusy) return result;
                _dailyCompletionPending = true;
                completion = DailyCompletion = CompleteDailyAsync(Task.FromResult(result));
            }
        }
        return await completion;
    }

    private bool RejectStart(string message)
    {
        _startError = message;
        log.Write(message, LogLevel.Error);
        return false;
    }

    public bool StartDaily()
    {
        lock (_completionGate) return StartDailyCore();
    }

    private bool StartDailyCore()
    {
        _startError = null;
        if (SettingsError is { } error) return RejectStart(error);
        if (IsBusy) return RejectStart("已有功能正在运行或收尾，请等待完成。");
        var snapshot = Settings;
        if (snapshot.CustomRoutes && snapshot.DailyRoutes.Count == 0) return RejectStart("请先为自定义路线添加至少一条已保存的路线。");
        var requiresGameInput = snapshot.CustomRoutes || snapshot.LaunchGame || snapshot.EnterGame || DailyFeatureOrder.Choices.Any(c => c.Selected(snapshot));
        if (!(requiresGameInput || snapshot.ExitGame))
        return RejectStart("请先在一条龙页面选择功能。");
        if (snapshot.LaunchGame && (string.IsNullOrWhiteSpace(snapshot.GameExecutablePath)
            || !Path.IsPathFullyQualified(snapshot.GameExecutablePath) || !File.Exists(snapshot.GameExecutablePath)
            || !string.Equals(Path.GetExtension(snapshot.GameExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase)))
        return RejectStart("请先在设置中选择有效的游戏启动器（.exe）。");
        var response = ResponseTiming.FromLowPerformanceMode(snapshot.LowPerformanceMode);
        var timeout = response.Scale(TimeSpan.FromMinutes(snapshot.DailyCommissions || snapshot.WeeklyEchoes || snapshot.CrisisRaids || snapshot.StoryCommissions ? 180 : 10))
            + TimeSpan.FromSeconds(snapshot.ExitGame ? snapshot.ExitGameDelaySeconds : 0);
        if (snapshot.CustomRoutes) timeout += TimeSpan.FromMinutes(180d * snapshot.DailyRoutes.Count);
        if (snapshot.EnterGame) timeout += StartJourneyTask.UpdateTimeout + StartJourneyTask.RestartTimeout;
        _dailyCompletionPending = true;
        var started = Start(() => createDaily(snapshot), new TaskRunOptions(timeout,
            RequiresGameInput: requiresGameInput, ResponseTiming: response, RecoverForeground: requiresGameInput,
            RandomInteractionDelay: snapshot.RandomInteractionDelay,
            MinimumClickInterval: response.Scale(DailyUiTiming.MinimumClickInterval),
            PrepareWithExecutionAsync: snapshot.LaunchGame ? (execution, input, token) => launchGame is not null
                ? launchGame(snapshot.GameExecutablePath, log.Write, token)
                : (launcherPreparation ?? throw new InvalidOperationException("游戏启动器尚未配置。"))
                    .RunAsync(snapshot.GameExecutablePath, log.Write, execution, input,
                        response.Scale(TimeSpan.FromMinutes(2)), response.Scale(TimeSpan.FromMilliseconds(250)), token) : null));
        if (!started) { _dailyCompletionPending = false; return false; }
        DailyCompletion = CompleteDailyAsync(runner.Completion);
        return true;
    }

    public bool StartRealtime()
    {
        lock (_completionGate) return StartRealtimeCore();
    }

    private bool StartRealtimeCore()
    {
        if (IsBusy) { log.Write("已有功能正在运行或收尾，请等待完成。"); return false; }
        var snapshot = Settings;
        if (!snapshot.HasRealtimeSelection)
        { log.Write("请先在实时辅助页面选择功能。"); return false; }
        var response = ResponseTiming.FromLowPerformanceMode(snapshot.LowPerformanceMode);
        return Start(() => createRealtime(snapshot), new TaskRunOptions(null, ResponseTiming: response,
            CaptureInterval: snapshot.HasRealtimeActions ? response.Scale(RealtimeTiming.Default.PollInterval) : null));
    }

    private async Task<TaskResult> CompleteDailyAsync(Task<TaskResult> completion)
    {
        // Defer completion handling until the starting call has published this run's task.
        await Task.Yield();
        try
        {
            var result = await completion;
            if (dailyCompleted is not null)
            {
                try { await dailyCompleted(result); }
                catch (Exception ex) { log.Write($"一条龙收尾处理失败，助手保持打开：{ex.Message}", LogLevel.Error); }
            }
            return result;
        }
        finally { lock (_completionGate) _dailyCompletionPending = false; }
    }

    public bool StartNavigation(NavigationTestRequest request)
    {
        lock (_completionGate)
        {
            if (IsBusy) return RejectStart("已有功能正在运行，请先停止当前任务。");
            if (createNavigation is null) return RejectStart("导航功能未装配。");
            Volatile.Write(ref _navigationStatus, new NavigationStatus(null, DateTimeOffset.Now, "正在定位"));
            return Start(() => createNavigation(request, status => Volatile.Write(ref _navigationStatus, status)),
                new TaskRunOptions(request.Mode == NavigationTestMode.Observe ? null
                    : TimeSpan.FromMinutes(request.Mode == NavigationTestMode.Teleport ? 5 : 3),
                    ResponseTiming: ResponseTiming.FromLowPerformanceMode(Settings.LowPerformanceMode)));
        }
    }

    private bool Start(Func<IGameTask> create, TaskRunOptions options)
    {
        if (SettingsError is { } error) return RejectStart(error);
        try { return runner.Start(create(), options); }
        catch (Exception ex) { return RejectStart($"无法启动功能：{ex.Message}"); }
    }

    public Task StopAsync() => runner.StopAsync();

    public bool StartRoute(RouteDefinition route, bool autoPickup = false)
    {
        lock (_completionGate)
        {
            if (IsBusy) return RejectStart("已有功能正在运行，请先停止当前任务。");
            if (createRoute is null) return RejectStart("路线功能未装配。");
            Volatile.Write(ref _navigationStatus, new NavigationStatus(null, DateTimeOffset.Now, "正在启动路线"));
            return Start(() => createRoute(route, autoPickup, status => Volatile.Write(ref _navigationStatus, status)),
                new TaskRunOptions(TimeSpan.FromMinutes(Math.Min(180, route.Points.Sum(p => p.TeleportId is null ? 3 : 5) + 1)),
                    ResponseTiming: ResponseTiming.FromLowPerformanceMode(Settings.LowPerformanceMode)));
        }
    }

    private IRouteRecordingTask? _recorder;
    private Task<TaskResult>? _recordingCompletion;
    public bool IsRecording { get { lock (_completionGate) return _recordingCompletion is { IsCompleted: false }; } }

    public bool StartRecording()
    {
        lock (_completionGate)
        {
            if (IsRecording) return true;
            if (IsBusy || createRouteRecording is null) return RejectStart("当前无法开始路线录制。");
            Volatile.Write(ref _navigationStatus, new(null, DateTimeOffset.Now, "正在定位"));
            var recording = createRouteRecording(status => Volatile.Write(ref _navigationStatus, status));
            if (!Start(() => recording, new TaskRunOptions(null, RecoverForeground: true,
                ResponseTiming: ResponseTiming.FromLowPerformanceMode(Settings.LowPerformanceMode)))) return false;
            _recorder = recording;
            _recordingCompletion = runner.Completion;
            return true;
        }
    }

    public async Task<RouteRecordingResult> RecordPointAsync(string? mapId = null, string? teleportId = null)
    {
        Task<RouteRecordingResult> request;
        Task<TaskResult> completion;
        lock (_completionGate)
        {
            if (!IsRecording || _recorder is null) return new(null, "录制会话已结束。");
            request = _recorder.RequestPointAsync(mapId, teleportId);
            completion = _recordingCompletion!;
        }
        await Task.WhenAny(request, completion);
        return request.IsCompleted ? await request : new(null, (await completion).Message);
    }

    public void FinishRecording()
    {
        lock (_completionGate) _recorder?.Finish();
    }
}
