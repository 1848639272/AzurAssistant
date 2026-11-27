using AzurAssistant.Platform;
using AzurAssistant.Runtime;
using AzurAssistant.UI;
using AzurAssistant.Features.StartJourney;
using AzurAssistant.Vision;
using System.IO;
using AzurAssistant.Features.Commissions;
using AzurAssistant.Contracts;
using System.Net.Http;
using System.Text.Json;

namespace AzurAssistant.Composition;

public static class Bootstrapper
{
    public static MainWindow CreateWindow(bool runDailyFromCommandLine = false, bool enableStartupActions = true, string? settingsPath = null,
        bool exitAfterDaily = false, DailyLogStore? startupLog = null, bool enableGlobalHotkeys = true)
    {
        var log = startupLog ?? new DailyLogStore(Path.Combine(DataRoot(), "logs"));
        log.Write("蔚蓝助手 v1.0.0_rc2_20261127 已就绪");
        var preview = new CapturePreviewService(
            () => new ReferenceSpaceFrameSource(new WindowsGraphicsFrameSource(GameWindow.Find("AzurPromilia"))), log.Write);
        var launcher = new LauncherPreparation();
        var nativeInput = new WindowsGameInput(launcher.InputWindow);
        var input = new InputCoordinator(nativeInput, timing => nativeInput.SetActivationTiming(
            timing.Scale(TimeSpan.FromSeconds(3)), timing.Scale(TimeSpan.FromMilliseconds(200)), timing.Scale(TimeSpan.FromMilliseconds(40))));
        var runner = new TaskRunner(preview, input, log);
        var settings = new SettingsStore(settingsPath ?? Path.Combine(DataRoot(), "settings.json"));
        var dataDirectory = Path.GetDirectoryName(settingsPath ?? Path.Combine(DataRoot(), "settings.json"))!;
        var commissionSettings = new CommissionSettingsStore(Path.Combine(dataDirectory, "commissions.json"));
        var weekly = new WeeklyProgressStore(Path.Combine(dataDirectory, "weekly-progress.json"));
        var routesDirectory = Path.Combine(AppContext.BaseDirectory, "routes");
        var features = new FeatureCatalog(commissionSettings, weekly, async (timeout, token) =>
        {
            await preview.StopAsync();
            await GameCloser.CloseAsync(log.Write, timeout, token);
        }, options => new JourneyRestartConnection(preview, () => GameWindow.FindIfPresent("AzurPromilia"), async (context, token) =>
        {
            var path = options.GameExecutablePath;
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)
                || !string.Equals(Path.GetFileName(path), "launcher.exe", StringComparison.OrdinalIgnoreCase))
            {
                context.Log("未配置有效的官方启动器，请手动启动游戏；仍在等待新游戏窗口");
                return;
            }
            context.Log("更新后游戏进程已退出，正在通过官方启动器重新启动一次");
            var timing = ResponseTiming.FromLowPerformanceMode(options.LowPerformanceMode);
            await launcher.RunAsync(path, context.Log, context.Execution, context.Input, StartJourneyTask.RestartTimeout,
                timing.Scale(TimeSpan.FromMilliseconds(250)), token);
        }).ReconnectAsync, routesDirectory);
        MainWindow? window = null;
        var commandLineExit = exitAfterDaily && runDailyFromCommandLine && enableStartupActions;
        var automation = new AutomationController(preview, runner, settings, log,
            snapshot => snapshot.CustomRoutes || snapshot.EnterGame || DailyFeatureOrder.Choices.Any(choice => choice.Selected(snapshot))
                ? features.WithExtraRewards(features.CreateDaily(snapshot), snapshot, daily: true) : features.CreateDaily(snapshot),
            snapshot =>
            {
                var actions = snapshot.HasRealtimeActions ? features.WithExtraRewards(features.CreateRealtime(snapshot), snapshot) : null;
                return snapshot.HideUid ? new UidOverlayTask(actions, preview, System.Windows.Application.Current.Dispatcher) : actions!;
            }, enableStartupActions: enableStartupActions,
            commissionSettings: commissionSettings, weeklyProgress: weekly, createNavigation: features.CreateNavigation,
            createRoute: (route, pickup, report) => pickup ? features.WithExtraRewards(features.CreateRoute(route, true, report)) : features.CreateRoute(route, false, report), createRouteRecording: features.CreateRouteRecording,
            navigationMaps: Game.Navigation.NavigationMapCatalog.Load(verifyFiles: false, availableOnly: true).Maps, launcherPreparation: launcher,
            dailyCompleted: async result =>
            {
                var activeWindow = window;
                if (activeWindow is null) return;
                await activeWindow.Dispatcher.InvokeAsync(() => CompleteAutomaticExitAsync(Task.FromResult<TaskResult?>(result),
                    () => activeWindow.ViewModel.RestartRequested, activeWindow.ViewModel.StopAsync,
                    code => System.Windows.Application.Current.Shutdown(code), log.Write,
                    onlyIfRequested: !commandLineExit)).Task.Unwrap();
            });
        TaskHotkeys? hotkeys = null;
        window = new MainWindow(new MainViewModel(preview, log, runner, null, () =>
        {
            if (AssistantRestarter.TryRestart(log.Write, elevated: true)) System.Windows.Application.Current.MainWindow?.Close();
        }, automation, runDailyFromCommandLine, applyHotkeys: (pause, stop) => hotkeys?.TryRebind(pause, stop), routesDirectory: routesDirectory));
        window.AttachGamePresentation(() => GameWindow.FindIfPresent("AzurPromilia"), enableStartupActions && !runDailyFromCommandLine);
        try
        {
            var source = JsonSerializer.Deserialize<UpdateSource>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "update-source.json")))!;
            var release = JsonSerializer.Deserialize<AssistantRelease>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "release.json")))!;
            source.Validate(); release.Validate();
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var client = new GitHubReleaseClient(http, source);
            var updates = new AssistantUpdate(source, release, client.CheckAsync, Path.Combine(dataDirectory, "update-state.json"), log.Write);
            window.AttachUpdates(updates, client, http, source.IsConfigured
                ? $"GitHub · 当前构建 {release.BuildNumber}" : "GitHub 更新仓库尚未配置");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException or NullReferenceException)
        { log.Write($"更新配置读取失败：{ex.Message}"); window.ViewModel.SetUpdateStatus(false, "更新配置读取失败，请重新安装完整包。"); }
        var pendingHotkeyErrors = new List<string>();
        void ReportHotkeyFailure(string message)
        {
            log.Write(message);
            if (!window.IsLoaded) { pendingHotkeyErrors.Add(message); return; }
            // Registration can run during keyboard focus routing; show diagnostics after that route settles.
            window.Dispatcher.BeginInvoke(() => window.ShowHotkeyFailure(message));
        }
        window.SourceInitialized += (_, _) =>
        {
            if (!enableGlobalHotkeys) return;
            hotkeys = new TaskHotkeys(new System.Windows.Interop.WindowInteropHelper(window).Handle,
                automation.Settings.PauseHotkey, automation.Settings.StopHotkey,
                window.TogglePauseFromHotkeyAsync, window.ViewModel.StopTaskAsync, ReportHotkeyFailure);
        };
        window.HotkeyCaptureChanged += suspended => hotkeys?.SetSuspended(suspended);
        window.Loaded += (_, _) =>
        {
            if (pendingHotkeyErrors.Count == 0) return;
            var message = string.Join(Environment.NewLine, pendingHotkeyErrors);
            pendingHotkeyErrors.Clear();
            window.Dispatcher.BeginInvoke(() => window.ShowHotkeyFailure(message));
        };
        window.Closed += (_, _) => { hotkeys?.Dispose(); features.Dispose(); };
        return window;
    }

    public static async Task CompleteAutomaticExitAsync(Task<TaskResult?> completion, Func<bool> restartRequested,
        Func<Task> stop, Action<int> exit, Action<string> log, bool onlyIfRequested = false)
    {
        // An installer handoff retains closing ownership even if launching the new process fails.
        var result = await completion;
        if (onlyIfRequested && result?.RequestApplicationExit != true) return;
        if (restartRequested()) return;
        await stop();
        if (restartRequested()) return;
        var exitCode = result?.Success == true ? 0 : 1;
        log($"自动运行收尾：任务和捕获已停止，助手即将退出；退出码={exitCode}；任务结果以最终结果日志为准");
        exit(exitCode);
    }

    internal static string DataRoot()
    {
        // A packaged application can be extracted inside a checkout; its data still belongs beside its executable.
        try
        {
            var releasePath = Path.Combine(AppContext.BaseDirectory, "release.json");
            if (File.Exists(releasePath) && JsonSerializer.Deserialize<AssistantRelease>(File.ReadAllText(releasePath))?.BuildNumber > 0)
                return AppContext.BaseDirectory;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AzurAssistant.sln"))) return directory.FullName;
        return AppContext.BaseDirectory;
    }
}
