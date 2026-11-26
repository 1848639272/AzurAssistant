using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzurAssistant.Runtime;

public sealed record DailyRouteChoice(string File, string Name, bool AutoPickup);

[JsonConverter(typeof(DailyRouteListConverter))]
public sealed class DailyRouteList(IEnumerable<DailyRouteChoice> entries) : IReadOnlyList<DailyRouteChoice>, IEquatable<DailyRouteList>
{
    private readonly DailyRouteChoice[] _entries = entries.ToArray();
    public int Count => _entries.Length;
    public DailyRouteChoice this[int index] => _entries[index];
    public IEnumerator<DailyRouteChoice> GetEnumerator() => ((IEnumerable<DailyRouteChoice>)_entries).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Equals(DailyRouteList? other) => other is not null && _entries.SequenceEqual(other._entries);
    public override bool Equals(object? obj) => obj is DailyRouteList list && Equals(list);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in _entries) hash.Add(entry);
        return hash.ToHashCode();
    }
}

public sealed class DailyRouteListConverter : JsonConverter<DailyRouteList>
{
    public override DailyRouteList Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<DailyRouteChoice[]>(ref reader, options) ?? throw new JsonException("路线列表为空值。"));
    public override void Write(Utf8JsonWriter writer, DailyRouteList value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}

public sealed record AssistantSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string GameExecutablePath { get; init; } = "";
    public bool AutoConnect { get; init; }
    public bool AutoUpdate { get; init; }
    public bool AutoRunDaily { get; init; }
    public bool RandomInteractionDelay { get; init; }
    public bool LowPerformanceMode { get; init; }
    public bool ShowPreview { get; init; } = true;
    public bool DarkMode { get; init; }
    public string PauseHotkey { get; init; } = "F9";
    public string StopHotkey { get; init; } = "F10";
    public bool LaunchGame { get; init; }
    public bool EnterGame { get; init; } = true;
    public bool CustomRoutes { get; init; }
    public DailyRouteList DailyRoutes { get; init; } = new([]);
    public bool ExitGame { get; init; }
    public int ExitGameDelaySeconds { get; init; }
    public bool CloseAssistantAfterExitGame { get; init; }
    public bool ClaimMail { get; init; }
    public bool HomeCenter { get; init; }
    public bool ClaimActivity { get; init; }
    public bool ClaimBattlePass { get; init; }
    public bool DailyCommissions { get; init; }
    public bool WeeklyEchoes { get; init; }
    public bool CrisisRaids { get; init; }
    public bool StoryCommissions { get; init; }
    public string DailyTaskOrder { get; init; } = "";
    public bool AutoSkipDialogue { get; init; }
    public bool AutoPickup { get; init; }
    public bool AutoMining { get; init; }
    public bool AutoLogging { get; init; }
    public bool HideUid { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRealtimeSelection => HasRealtimeActions || HideUid;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRealtimeActions => AutoSkipDialogue || AutoPickup || AutoMining || AutoLogging;
}
