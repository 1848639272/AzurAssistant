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
        log.Write("蔚蓝助手 v0.1.0 beta 已就绪");
        var preview = new CapturePreviewService(() => new WindowsGraphicsFrameSource(GameWindow.Find("AzurPromilia")), log.Write);
        var nativeInput = new WindowsGameInput(() => GameWindow.Find("AzurPromilia"));
        var input = new InputCoordinator(nativeInput, timing => nativeInput.SetActivationTiming(
            timing.Scale(TimeSpan.FromSeconds(3)), timing.Scale(TimeSpan.FromMilliseconds(200)), timing.Scale(TimeSpan.FromMilliseconds(40))));
        var runner = new TaskRunner(preview, input, log);
        var settings = new SettingsStore(settingsPath ?? Path.Combine(DataRoot(), "settings.json"));
        var dataDirectory = Path.GetDirectoryName(settingsPath ?? Path.Combine(DataRoot(), "settings.json"))!;
        var commissionSettings = new CommissionSettingsStore(Path.Combine(dataDirectory, "commissions.json"));
        var weekly = new WeeklyProgressStore(Path.Combine(dataDirectory, "weekly-progress.json"));
        var features = new FeatureCatalog(commissionSettings, weekly, async (timeout, token) =>
        {
            await preview.StopAsync();
            await GameCloser.CloseAsync(log.Write, timeout, token);
        });
        MainWindow? window = null;
        var commandLineExit = exitAfterDaily && runDailyFromCommandLine && enableStartupActions;
        var automation = new AutomationController(preview, runner, settings, log,
            features.CreateDaily, features.CreateRealtime, enableStartupActions: enableStartupActions,
            commissionSettings: commissionSettings, weeklyProgress: weekly, createNavigation: features.CreateNavigation,
            createRoute: features.CreateRoute, createRouteCapture: features.CreateRouteCapture,
            navigationMaps: Game.Navigation.NavigationMapCatalog.Load(verifyFiles: false).Maps,
            dailyCompleted: async result =>
            {
                var activeWindow = window;
                if (activeWindow is null) return;
                await activeWindow.Dispatcher.InvokeAsync(() => CompleteAutomaticExitAsync(Task.FromResult<TaskResult?>(result),
                    () => activeWindow.ViewModel.RestartRequested, activeWindow.ViewModel.StopAsync,
                    code => System.Windows.Application.Current.Shutdown(code), log.Write,
                    onlyIfRequested: !commandLineExit)).Task.Unwrap();
            });
        window = new MainWindow(new MainViewModel(preview, log, runner, null, () =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = true, Verb = "runas", Arguments = $"--wait-for-process {Environment.ProcessId}", WorkingDirectory = AppContext.BaseDirectory });
                System.Windows.Application.Current.MainWindow?.Close();
            }
            catch (System.ComponentModel.Win32Exception ex) { log.Write($"管理员重启未完成：{ex.Message}"); }
        }, automation, runDailyFromCommandLine, restartAssistant: () => AssistantRestarter.TryRestart(log.Write)));
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
        TaskHotkeys? hotkeys = null;
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
                automation.EffectiveSettings.PauseHotkey, automation.EffectiveSettings.StopHotkey,
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
        // A requested settings restart retains closing ownership even if launching the new process fails.
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
