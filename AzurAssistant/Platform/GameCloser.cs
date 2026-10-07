using System.Diagnostics;

namespace AzurAssistant.Platform;

/// <summary>A retained process identity, separated from selection and exit policy for safe verification.</summary>
public interface IGameProcessHandle : IDisposable
{
    string Name { get; }
    bool HasExited { get; }
    void Terminate();
    Task WaitForExitAsync(CancellationToken token);
}

public static class GameCloser
{
    public static async Task CloseAsync(Action<string> log, TimeSpan timeout, CancellationToken token,
        Func<IReadOnlyList<IGameProcessHandle>>? findProcesses = null)
    {
        token.ThrowIfCancellationRequested();
        var processes = (findProcesses ?? FindProcesses)();
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(timeout);
            var closed = 0;
            foreach (var process in processes)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (!string.Equals(process.Name, "AzurPromilia", StringComparison.OrdinalIgnoreCase) || process.HasExited) continue;
                // Never terminate child processes or broaden the target after an exit request.
                process.Terminate();
                await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
                if (!process.HasExited) throw new InvalidOperationException("退出请求后游戏进程仍在运行。");
                closed++;
            }
            log(closed == 0 ? "AzurPromilia.exe 已关闭，无需再次退出" : $"已确认关闭 AzurPromilia.exe（{closed} 个进程）");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("等待 AzurPromilia.exe 退出超时，未重复发送关闭请求。"); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static IReadOnlyList<IGameProcessHandle> FindProcesses()
    {
        var processes = Process.GetProcessesByName("AzurPromilia");
        var handles = new List<IGameProcessHandle>();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    // Retain a handle before reading the name, so a reused PID cannot change the target.
                    _ = process.SafeHandle;
                    if (!process.HasExited) handles.Add(new ProcessHandle(process, process.ProcessName));
                    else process.Dispose();
                }
                catch (InvalidOperationException) { process.Dispose(); }
            }
            return handles;
        }
        catch
        {
            foreach (var process in processes) process.Dispose();
            throw;
        }
    }

    private sealed class ProcessHandle(Process process, string name) : IGameProcessHandle
    {
        public string Name => name;
        public bool HasExited => process.HasExited;
        public void Terminate() { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
        public void Dispose() => process.Dispose();
    }
}
