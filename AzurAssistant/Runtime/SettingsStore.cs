using System.IO;
using System.Text.Json;

namespace AzurAssistant.Runtime;

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    public AssistantSettings Current { get; private set; } = new();
    public string? Error { get; private set; }

    public SettingsStore(string path)
    {
        _path = Path.GetFullPath(path);
        try
        {
            if (!File.Exists(_path)) return;
            var value = JsonSerializer.Deserialize<AssistantSettings>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("配置内容为空。");
            Validate(value);
            Current = NormalizeHotkeys(value);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Error = $"设置读取失败，已使用默认值且未自动运行：{ex.Message}"; }
    }

    public bool Save(AssistantSettings settings)
    {
        lock (_gate)
        {
            var temporary = _path + ".tmp";
            try
            {
                Validate(settings);
                settings = NormalizeHotkeys(settings);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, _path, true);
                Current = settings;
                Error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { Error = $"设置保存失败：{ex.Message}"; return false; }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void Validate(AssistantSettings value)
    {
        if (value.SchemaVersion != 1) throw new InvalidDataException("设置版本不受支持。");
        if (value.GameExecutablePath is null) throw new InvalidDataException("游戏路径不能为空值。");
        if (value.DailyTaskOrder is null || value.DailyTaskOrder.Length > 2048)
            throw new InvalidDataException("一条龙任务顺序格式无效。");
        if (value.GameExecutablePath.Length > 32767) throw new ArgumentException("游戏路径过长。");
        if (value.ExitGameDelaySeconds is < 0 or > 3600) throw new InvalidDataException("关闭游戏前的等待时间应为 0 至 3600 秒。");
        if (value.ScheduleMode is not ("daily" or "interval")) throw new InvalidDataException("定时执行的触发方式无效。");
        if (!Runtime.ScheduleClock.TryParseTime(value.ScheduleTime, out _)) throw new InvalidDataException("定时执行的时刻无效，格式为 HH:mm。");
        if (value.ScheduleIntervalMinutes is < 1 or > 1440) throw new InvalidDataException("定时执行的间隔应为 1–1440 分钟。");
        if (value.ScheduleTarget is null || value.ScheduleTarget.Length > 300) throw new InvalidDataException("定时执行的内容无效。");
        if (value.ScheduleStartSound is null || value.ScheduleStartSound.Length > 260) throw new InvalidDataException("开始提示音设置无效。");
        if (value.ScheduleEndSound is null || value.ScheduleEndSound.Length > 260) throw new InvalidDataException("结束提示音设置无效。");
        if (value.ScheduleSoundVolume is < 0 or > 100) throw new InvalidDataException("提示音音量应在 0–100 之间。");
        foreach (var (name, level) in new[] { ("界面", value.UiOpacity), ("文字", value.TextOpacity), ("背景", value.BackgroundOpacity) })
            if (level is < 0 or > 100) throw new InvalidDataException($"{name}透明度应在 0–100 之间。");
        if (value.BackgroundImagePath is null || value.BackgroundImagePath.Length > 32767) throw new InvalidDataException("背景图路径无效。");
        if (value.OverlayMonitor is < 0 or > 15) throw new InvalidDataException("显示器编号应在 0–15 之间。");
        if (value.CloseAction is not ("ask" or "exit" or "tray")) throw new InvalidDataException("关闭行为设置无效。");
        if (value.StepTimeoutSeconds is < 3 or > 300) throw new InvalidDataException("单步超时应在 3–300 秒之间。");
        if (value.StepRetrySeconds is < 0 or > 60) throw new InvalidDataException("重试间隔应在 0–60 秒之间。");
        if (value.ActionSettleMs is < 200 or > 10000) throw new InvalidDataException("操作后等待应在 200–10000 毫秒之间。");
        if (value.StuckWarningSeconds is < 0 or > 3600) throw new InvalidDataException("卡死检测阈值应在 0–3600 秒之间。");
        if (value.AccentColor is null || (value.AccentColor.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(value.AccentColor, "^#[0-9A-Fa-f]{6}$")))
            throw new InvalidDataException("界面颜色格式无效（应形如 #1E90FF）。");
        if (value.BackgroundImageMode is not ("stretch" or "fit")) throw new InvalidDataException("背景图处理方式无效。");
        if (!HotkeyBinding.TryParsePair(value.PauseHotkey, value.StopHotkey, out _, out _, out var error))
            throw new InvalidDataException(error);
    }

    private static AssistantSettings NormalizeHotkeys(AssistantSettings value)
    {
        HotkeyBinding.TryParsePair(value.PauseHotkey, value.StopHotkey, out var pause, out var stop, out _);
        return value with { PauseHotkey = pause.Text, StopHotkey = stop.Text };
    }
}
