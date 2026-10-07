using System.Windows;
using AzurAssistant.Composition;
using AzurAssistant.Platform;
using AzurAssistant.Runtime;
using System.IO;

namespace AzurAssistant;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsInstance;
    private DailyLogStore? _log;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _log = new DailyLogStore(Path.Combine(Bootstrapper.DataRoot(), "logs"));
        _log.Write("助手进程启动：AzurAssistant.exe；目标游戏进程：AzurPromilia.exe");
        var automatic = e.Args.Contains("--run-daily") || e.Args.Contains("--exit-after-daily");
        void FailStartup(string message)
        {
            _log.Write($"最终结果：失败；启动未完成，未执行任务：{message}", LogLevel.Error);
            if (!automatic) MessageBox.Show(message, "蔚蓝助手");
            Shutdown(2);
        }
        StartupOptions options;
        try { options = StartupOptions.Parse(e.Args); }
        catch (ArgumentException ex) { FailStartup(ex.Message); return; }
        if (!ProcessPrivileges.IsElevated(Environment.ProcessId))
        {
            FailStartup("请从桌面快捷方式或 AzurAssistant.exe 启动，并允许管理员权限请求。外部重定向调用需先以管理员身份运行调用者。");
            return;
        }
        if (options.WaitForProcess is { } previous)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(previous);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { }
            catch (OperationCanceledException) { FailStartup("旧助手仍未退出，请关闭旧窗口后重启。"); return; }
        }
        try
        {
            _instance = new Mutex(false, "Local\\AzurAssistant.DesktopInstance");
            try { _ownsInstance = _instance.WaitOne(0); } catch (AbandonedMutexException) { _ownsInstance = true; }
        }
        catch (UnauthorizedAccessException) { _ownsInstance = false; }
        if (!_ownsInstance) { FailStartup("蔚蓝助手已经运行，本次未执行任务；请先关闭已有窗口。"); return; }
        try
        {
            MainWindow = Bootstrapper.CreateWindow(options.RunDaily, enableStartupActions: !options.SkipStartupActions,
                exitAfterDaily: options.ExitAfterDaily, startupLog: _log);
            MainWindow.Show();
        }
        catch (Exception ex) { FailStartup($"初始化失败：{ex}"); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Write($"助手进程退出；退出码={e.ApplicationExitCode}；进程结束不代表任务成功，请核对最终结果日志");
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
