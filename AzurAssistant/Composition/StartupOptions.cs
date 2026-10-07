namespace AzurAssistant.Composition;

public sealed record StartupOptions(bool RunDaily = false, int? WaitForProcess = null, bool ExitAfterDaily = false,
    bool SkipStartupActions = false)
{
    public static StartupOptions Parse(string[] args)
    {
        var runDaily = false;
        var exitAfterDaily = false;
        var skipStartupActions = false;
        int? previous = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--run-daily": runDaily = true; break;
                case "--exit-after-daily": exitAfterDaily = true; break;
                case "--skip-startup-actions": skipStartupActions = true; break;
                case "--wait-for-process" when i + 1 < args.Length && int.TryParse(args[i + 1], out var pid) && pid > 0:
                    previous = pid; i++; break;
                default: throw new ArgumentException($"未知或无效的启动参数：{args[i]}。自动运行一条龙请使用 --run-daily。");
            }
        }
        if (exitAfterDaily && !runDaily) throw new ArgumentException("--exit-after-daily 必须与 --run-daily 一起使用。");
        if (skipStartupActions && runDaily) throw new ArgumentException("更新后启动不能同时自动运行一条龙。");
        return new(runDaily, previous, exitAfterDaily, skipStartupActions);
    }
}
