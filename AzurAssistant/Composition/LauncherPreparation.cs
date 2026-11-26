using System.IO;
using AzurAssistant.Contracts;
using AzurAssistant.Features.LaunchGame;
using AzurAssistant.Platform;
using AzurAssistant.Vision;

namespace AzurAssistant.Composition;

public sealed class LauncherPreparation
{
    private GameWindow? _launcher;
    public GameWindow InputWindow() => _launcher ?? GameWindow.Find("AzurPromilia");

    public async Task RunAsync(string path, Action<string> log, TaskExecution execution, IGameInput input,
        TimeSpan budget, TimeSpan poll, CancellationToken token)
    {
        using var deadline = execution.CreateDeadline(token);
        deadline.CancelAfter(budget);
        var runToken = deadline.Token;
        var stage = "等待游戏窗口";
        try
        {
            await execution.WaitAsync(runToken);
            if (!GameLauncher.IsGameRunning())
            {
                path = Path.GetFullPath(path);
                var filename = Path.GetFileName(path);
                if (filename.Equals("AzurPromilia.exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("请在设置中选择官方启动器 launcher.exe，公测客户端需要从启动器进入。");
                var official = filename.Equals("launcher.exe", StringComparison.OrdinalIgnoreCase);
                if (!official || GameLauncher.FindLauncher(path) is null)
                {
                    using var action = await execution.EnterActionAsync(runToken);
                    if (!GameLauncher.IsGameRunning() && (!official || GameLauncher.FindLauncher(path) is null))
                    {
                        GameLauncher.Start(path);
                        log("已打开设置中的启动文件");
                    }
                }
                if (official)
                {
                    stage = "等待启动器主窗口";
                    while (!GameLauncher.IsGameRunning())
                    {
                        await execution.WaitAsync(runToken);
                        _launcher = GameLauncher.FindLauncher(path);
                        if (_launcher is not null)
                        {
                            stage = "识别启动器的“启动游戏”按钮";
                            await ClickStartAsync(_launcher, execution, input, poll, log, runToken);
                            break;
                        }
                        await execution.DelayAsync(poll, runToken);
                    }
                }
            }
            _launcher = null;
            stage = "等待 AzurPromilia.exe 的游戏窗口";
            while (true)
            {
                await execution.WaitAsync(runToken);
                if (GameWindow.FindIfPresent("AzurPromilia") is not null) { log("游戏窗口已就绪"); return; }
                await execution.DelayAsync(poll, runToken);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException($"启动游戏超时（{stage}），请检查启动器、游戏更新或登录提示。"); }
        finally { _launcher = null; }
    }

    private static async Task ClickStartAsync(GameWindow window, TaskExecution execution, IGameInput input,
        TimeSpan poll, Action<string> log, CancellationToken token)
    {
        await input.PrepareForObservationAsync(token);
        log("已找到蓝色星原启动器，正在识别“启动游戏”");
        using var source = new WindowsGraphicsFrameSource(window);
        using var reader = new PpOcrReader();
        var recognizer = new LauncherButtonRecognizer(reader);
        ClientPoint? previous = null;
        long viewport = 0;
        TimeSpan last = TimeSpan.Zero;
        var generation = execution.Generation;
        while (!GameLauncher.IsGameRunning())
        {
            await execution.WaitAsync(token);
            if (generation != execution.Generation)
            {
                previous = null;
                await input.PrepareForObservationAsync(token);
                generation = execution.Generation;
            }
            FrameSnapshot frame;
            try { frame = await source.ReadAsync(token); }
            catch (TimeoutException) { previous = null; continue; }
            var point = await recognizer.FindAsync(frame, token);
            if (!execution.IsObservationCurrent(frame) || WindowsGraphicsFrameSource.SystemNow - frame.SystemTimestamp >= TimeSpan.FromSeconds(1))
            { previous = null; continue; }
            if (point is { } current && previous is { } prior && frame.ViewportVersion == viewport
                && frame.SystemTimestamp > last && frame.SystemTimestamp - last <= TimeSpan.FromSeconds(1)
                && Math.Abs(current.X - prior.X) <= 6 && Math.Abs(current.Y - prior.Y) <= 6)
            {
                if (GameLauncher.IsGameRunning()) return;
                try { await input.ClickAsync(frame, current, token); }
                catch (ObservationInvalidatedException) { previous = null; continue; }
                log("已点击启动器的“启动游戏”，等待游戏窗口");
                return;
            }
            previous = point;
            viewport = frame.ViewportVersion;
            last = frame.SystemTimestamp;
            await execution.DelayAsync(poll, token);
        }
    }
}
