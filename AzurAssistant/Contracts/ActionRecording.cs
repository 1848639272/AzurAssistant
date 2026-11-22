using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzurAssistant.Contracts;

/// <summary>One recorded player input. Times are microseconds from the first recorded event.</summary>
public enum ActionEventKind
{
    KeyDown, KeyUp,
    Move,
    LeftDown, LeftUp,
    RightDown, RightUp,
    MiddleDown, MiddleUp,
    Wheel,
}

/// <summary>
/// A single captured input event. Positions are native client-area pixels of the window at record time, and relative
/// mouse movement keeps the exact deltas a camera turn was made of, so a replay reproduces the same motion even when
/// the pointer is clipped by the game.
/// </summary>
public sealed record ActionEvent
{
    public long Us { get; init; }
    public ActionEventKind Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Vk { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Scan { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Ext { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int X { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Y { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Dx { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Dy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Wheel { get; init; }
}

/// <summary>A complete player-input recording: every key press with its hold time, every click and every mouse move.</summary>
public sealed record ActionRecording(int SchemaVersion, string Name, DateTimeOffset CreatedAt,
    int ClientWidth, int ClientHeight, long DurationUs, IReadOnlyList<ActionEvent> Events)
{
    public const int CurrentSchema = 1;
    public const int MaximumEvents = 1_000_000;
    public const int MaximumClientSide = 32_768;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string DurationText => DurationUs >= 60_000_000
        ? $"{DurationUs / 60_000_000} 分 {(DurationUs / 1_000_000) % 60} 秒"
        : $"{DurationUs / 1_000_000.0:0.0} 秒";

    public int KeyPresses => Events.Count(e => e.Kind is ActionEventKind.KeyDown);
    public int Clicks => Events.Count(e => e.Kind is ActionEventKind.LeftDown or ActionEventKind.RightDown or ActionEventKind.MiddleDown);

    public string Summary => $"{DurationText} · {KeyPresses} 次按键 · {Clicks} 次点击 · {Events.Count} 个事件";

    public void Validate()
    {
        if (SchemaVersion != CurrentSchema) throw new InvalidDataException($"动作文件版本不受支持：{SchemaVersion}");
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("动作名称为空。");
        if (Name.Length > 80) throw new InvalidDataException("动作名称过长。");
        if (ClientWidth is <= 0 or > MaximumClientSide || ClientHeight is <= 0 or > MaximumClientSide)
            throw new InvalidDataException($"动作记录的客户区尺寸无效：{ClientWidth}×{ClientHeight}");
        if (Events is null || Events.Count == 0) throw new InvalidDataException("动作文件没有任何输入事件。");
        if (Events.Count > MaximumEvents) throw new InvalidDataException($"动作文件事件过多：{Events.Count}");
        var previous = -1L;
        foreach (var item in Events)
        {
            if (item.Us < 0 || item.Us < previous) throw new InvalidDataException("动作文件时间戳未按顺序排列。");
            previous = item.Us;
            if (!Enum.IsDefined(item.Kind)) throw new InvalidDataException($"动作文件事件类型无效：{item.Kind}");
        }
        if (DurationUs < 0) throw new InvalidDataException("动作文件时长为负。");
    }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static ActionRecording FromJson(string json)
    {
        var recording = JsonSerializer.Deserialize<ActionRecording>(json, SerializerOptions)
            ?? throw new InvalidDataException("动作文件为空。");
        recording.Validate();
        return recording;
    }
}
