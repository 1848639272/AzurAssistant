using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace AzurAssistant.Platform;

public static class AssistantRestarter
{
    /// <summary>Starts the local executable; the caller closes the old window only after success.</summary>
    public static bool TryRestart(Action<string> log, string? baseDirectory = null, int? previousProcessId = null,
        Func<ProcessStartInfo, bool>? startProcess = null)
    {
        try
        {
            var directory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
            var executable = Path.Combine(directory, "AzurAssistant.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("找不到当前目录中的 AzurAssistant.exe。", executable);
            var previous = previousProcessId ?? Environment.ProcessId;
            if (previous <= 0) throw new ArgumentOutOfRangeException(nameof(previousProcessId));
            var request = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = directory };
            // One-shot automatic-run arguments must not be replayed during a settings restart.
            request.ArgumentList.Add("--wait-for-process");
            request.ArgumentList.Add(previous.ToString(CultureInfo.InvariantCulture));
            if (!(startProcess ?? Start)(request))
            {
                log("助手重启未完成：系统未创建新进程，当前窗口保持打开。");
                return false;
            }
            log("已启动新助手，等待当前助手退出后加载设置");
            return true;
        }
        catch (Exception ex)
        {
            log($"助手重启未完成：{ex.Message}；当前窗口保持打开。");
            return false;
        }
    }

    private static bool Start(ProcessStartInfo request)
    {
        using var process = Process.Start(request);
        return process is not null;
    }
}
