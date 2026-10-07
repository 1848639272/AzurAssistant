using System.Diagnostics;
using System.IO;
using System.Globalization;

namespace AzurAssistant.Platform;

public static class AssistantInstaller
{
    public static bool TryStart(string installer, string installDirectory, Action<string> log,
        Func<ProcessStartInfo, bool>? startProcess = null)
    {
        try
        {
            if (!File.Exists(installer) || !Path.IsPathFullyQualified(installer) || !Path.IsPathFullyQualified(installDirectory))
                throw new IOException("安装包或安装目录无效。");
            var request = new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer)! };
            request.ArgumentList.Add("/DIR=" + Path.GetFullPath(installDirectory));
            request.ArgumentList.Add("/WAITPID=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            request.ArgumentList.Add("/FROMUPDATE=1");
            var started = startProcess?.Invoke(request) ?? Process.Start(request) is not null;
            if (!started) log("安装程序未启动，当前窗口保持打开。");
            return started;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { log($"安装程序启动失败：{ex.Message}；当前窗口保持打开。"); return false; }
    }
}
