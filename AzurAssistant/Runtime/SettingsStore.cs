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
        if (!HotkeyBinding.TryParsePair(value.PauseHotkey, value.StopHotkey, out _, out _, out var error))
            throw new InvalidDataException(error);
    }

    private static AssistantSettings NormalizeHotkeys(AssistantSettings value)
    {
        HotkeyBinding.TryParsePair(value.PauseHotkey, value.StopHotkey, out var pause, out var stop, out _);
        return value with { PauseHotkey = pause.Text, StopHotkey = stop.Text };
    }
}
