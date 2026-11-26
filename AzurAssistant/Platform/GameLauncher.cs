using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AzurAssistant.Platform;

public static class GameLauncher
{
    public static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("AzurPromilia");
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    public static void Start(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        }) ?? throw new InvalidOperationException("启动文件未能打开。");
    }

    public static GameWindow? FindLauncher(string executablePath)
    {
        GameWindow? found = null;
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath));
        try
        {
            foreach (var process in processes)
            {
                if (process.MainWindowHandle == 0 || process.MainWindowTitle != "蓝色星原：旅谣") continue;
                using var handle = OpenProcess(0x1000, false, process.Id);
                var path = new StringBuilder(32768);
                var length = path.Capacity;
                if (handle.IsInvalid || !QueryFullProcessImageName(handle, 0, path, ref length)
                    || !string.Equals(path.ToString(), executablePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (found is not null) throw new InvalidOperationException("存在多个匹配的启动器主窗口，请保留一个。");
                found = new GameWindow(process.MainWindowHandle, process.Id);
            }
            return found;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int length);
}
