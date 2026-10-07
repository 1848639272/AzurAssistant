using System.Diagnostics;
using System.IO;
using AzurAssistant.Contracts;

namespace AzurAssistant.Platform;

public static class GameLauncher
{
    public static Task EnsureRunningAsync(string executablePath, Action<string> log, CancellationToken token)
        => EnsureRunningAsync(executablePath, log, token, TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(250));

    public static async Task EnsureRunningAsync(string executablePath, Action<string> log, CancellationToken token,
        TimeSpan timeout, TimeSpan pollInterval, TaskExecution? execution = null)
    {
        execution ??= TaskExecution.Uncontrolled;
        await execution.WaitAsync(token);
        var processes = Process.GetProcessesByName("AzurPromilia");
        var running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        if (!running)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath)
                || !File.Exists(executablePath) || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请先在设置中选择有效的游戏启动文件（.exe）。");
            token.ThrowIfCancellationRequested();
            using (await execution.EnterActionAsync(token))
            {
                using var process = Process.Start(new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(executablePath)!
                }) ?? throw new InvalidOperationException("游戏启动失败。");
            }
            log("已启动游戏程序，等待 AzurPromilia.exe 的游戏窗口");
        }
        else log("游戏进程已经运行，等待可用窗口");
        using var waiting = execution.CreateDeadline(token);
        waiting.CancelAfter(timeout);
        try
        {
            while (true)
            {
                await execution.WaitAsync(waiting.Token);
                try { _ = GameWindow.Find("AzurPromilia"); log("游戏窗口已就绪"); return; }
                catch (InvalidOperationException) { }
                await execution.DelayAsync(pollInterval, waiting.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException($"{timeout.TotalSeconds:0}秒内未找到游戏窗口，请检查启动器、登录或更新提示；未重复启动游戏。"); }
    }
}
