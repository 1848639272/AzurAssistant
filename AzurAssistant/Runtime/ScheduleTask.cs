using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzurAssistant.Runtime;

/// <summary>One named scheduled job: what to run, when, and when it last ran.</summary>
public sealed record ScheduleTask
{
    public string Name { get; init; } = "定时任务";
    public bool Enabled { get; init; } = true;
    /// <summary>"daily"（每天固定时刻）或 "interval"（每隔若干分钟）。</summary>
    public string Mode { get; init; } = "daily";
    public string Time { get; init; } = "08:00";
    public int IntervalMinutes { get; init; } = 60;
    /// <summary>"daily" 或 "route:名称" / "action:名称" / "wheelchair:名称"。</summary>
    public string Target { get; init; } = "daily";
    /// <summary>该任务自己的开始提示音；null 表示跟随全局设置。</summary>
    public string? StartSound { get; init; }
    /// <summary>该任务自己的结束提示音；null 表示跟随全局设置。</summary>
    public string? EndSound { get; init; }
    /// <summary>上次触发时间（Unix 毫秒），重启后用于计算间隔任务的下一轮。</summary>
    public long LastRunUnixMs { get; init; }

    public DateTimeOffset? LastRun => LastRunUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(LastRunUnixMs) : null;
}

public sealed record ScheduleDefinition(int SchemaVersion, IReadOnlyList<ScheduleTask> Tasks)
{
    public const int CurrentSchema = 1;
    public const int MaximumTasks = 20;

    public static ScheduleDefinition Empty => new(CurrentSchema, []);

    public void Validate()
    {
        if (SchemaVersion != CurrentSchema) throw new InvalidDataException("定时任务文件版本不受支持。");
        if (Tasks is null) throw new InvalidDataException("定时任务列表为空值。");
        if (Tasks.Count > MaximumTasks) throw new InvalidDataException($"定时任务最多 {MaximumTasks} 条。");
        foreach (var task in Tasks)
        {
            if (task is null || string.IsNullOrWhiteSpace(task.Name) || task.Name.Length > 40)
                throw new InvalidDataException("每条定时任务需要 1–40 字的名称。");
            if (task.Mode is not ("daily" or "interval")) throw new InvalidDataException($"任务“{task.Name}”的触发方式无效。");
            if (!ScheduleClock.TryParseTime(task.Time, out _)) throw new InvalidDataException($"任务“{task.Name}”的时刻无效，格式为 HH:mm。");
            if (task.IntervalMinutes is < 1 or > 1440) throw new InvalidDataException($"任务“{task.Name}”的间隔应为 1–1440 分钟。");
            if (task.Target is null || task.Target.Length > 300) throw new InvalidDataException($"任务“{task.Name}”的执行内容无效。");
            if (task.LastRunUnixMs < 0) throw new InvalidDataException($"任务“{task.Name}”的上次执行时间无效。");
        }
    }
}

/// <summary>Reads and writes the scheduled job list; a missing file simply means "no scheduled jobs".</summary>
public sealed class ScheduleStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 8,
    };

    public string FilePath => Path.GetFullPath(path);
    public string? Error { get; private set; }

    public ScheduleDefinition Load()
    {
        Error = null;
        try
        {
            if (!File.Exists(path)) return ScheduleDefinition.Empty;
            var definition = JsonSerializer.Deserialize<ScheduleDefinition>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("定时任务文件为空。");
            definition.Validate();
            return definition;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            Error = "定时任务读取失败，已按未配置处理：" + ex.Message;
            return ScheduleDefinition.Empty;
        }
    }

    public bool Save(ScheduleDefinition definition)
    {
        try
        {
            definition.Validate();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(definition, Json));
            File.Move(temporary, path, overwrite: true);
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Error = "定时任务保存失败：" + ex.Message;
            return false;
        }
    }
}
