using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AzurAssistant.Platform;

internal static class ProcessPrivileges
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int kind, out int elevation, int length, out int needed);

    internal static bool IsElevated(int processId)
    {
        var process = OpenProcess(0x1000, false, processId);
        if (process == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint token = 0;
        try
        {
            if (!OpenProcessToken(process, 8, out token) || !GetTokenInformation(token, 20, out var elevated, 4, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return elevated != 0;
        }
        finally { if (token != 0) CloseHandle(token); CloseHandle(process); }
    }
}
