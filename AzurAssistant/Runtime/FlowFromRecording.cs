using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzurAssistant.Contracts;

namespace AzurAssistant.Runtime;

/// <summary>Turns a saved input recording into flow steps (clicks, keys and delays in 1920×1080 reference space).</summary>
public static class FlowFromRecording
{
    /// <summary>Mapping of captured virtual keys to <see cref="GameKey"/> names understood by flows.</summary>
    private static readonly Dictionary<int, string> KeyNames = BuildKeyNames();

    private static Dictionary<int, string> BuildKeyNames()
    {
        var map = new Dictionary<int, string>();
        foreach (var name in Enum.GetNames<GameKey>())
        {
            var value = (int)Enum.Parse<GameKey>(name);
            map[value] = name;
        }
        return map;
    }

    public sealed record Conversion(FlowDefinition Flow, int Clicks, int Keys, int SkippedKeys, int Delays, bool Truncated);

    public static Conversion Convert(ActionRecording recording, string name, int maxSteps = 400)
    {
        if (recording.ClientWidth <= 0 || recording.ClientHeight <= 0)
            throw new InvalidDataException("录制文件缺少客户区尺寸，无法换算坐标。");
        var scaleX = FlowCoordinateReference.Width / (double)recording.ClientWidth;
        var scaleY = FlowCoordinateReference.Height / (double)recording.ClientHeight;

        var steps = new List<FlowStep>();
        int clicks = 0, keys = 0, skipped = 0, delays = 0;
        var truncated = false;
        long lastUs = 0;
        foreach (var input in recording.Events)
        {
            if (steps.Count >= maxSteps) { truncated = true; break; }
            var gapMs = (int)((input.Us - lastUs) / 1000);
            if (gapMs >= 40 && steps.Count > 0)
            {
                steps.Add(new FlowStep(FlowStep.Delay, DurationMs: Math.Min(gapMs, 5000)));
                delays++;
            }
            lastUs = input.Us;

            switch (input.Kind)
            {
                case ActionEventKind.LeftDown:
                    steps.Add(new FlowStep(FlowStep.Click,
                        X: Math.Round(input.X * scaleX, 1), Y: Math.Round(input.Y * scaleY, 1)));
                    clicks++;
                    break;
                case ActionEventKind.KeyDown:
                    if (KeyNames.TryGetValue(input.Vk, out var keyName))
                    {
                        steps.Add(new FlowStep(FlowStep.Key, Action: keyName));
                        keys++;
                    }
                    else skipped++;
                    break;
                case ActionEventKind.RightDown or ActionEventKind.MiddleDown or ActionEventKind.Wheel:
                    skipped++;
                    break;
            }
        }

        var flow = new FlowDefinition(FlowDefinition.CurrentSchema, name, steps);
        flow.Validate();
        return new Conversion(flow, clicks, keys, skipped, delays, truncated);
    }
}

/// <summary>The coordinate space flows use: the same 1920×1080 reference the recognition assets are authored in.</summary>
public static class FlowCoordinateReference
{
    public const int Width = 1920;
    public const int Height = 1080;
}

/// <summary>Where a flow stopped, so the next run can continue from that step.</summary>
public sealed record FlowProgress
{
    public string FlowName { get; init; } = "";
    /// <summary>1-based index of the step to start from next time.</summary>
    public int NextStep { get; init; } = 1;
    public int TotalSteps { get; init; }
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.Now;

    public string Describe => $"上次中断在第 {NextStep} 步（共 {TotalSteps} 步，{SavedAt:MM-dd HH:mm}）";
}

/// <summary>Stores the last interrupted flow position (one file, one flow).</summary>
public sealed class FlowProgressStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string FilePath => Path.GetFullPath(path);

    public FlowProgress? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<FlowProgress>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public bool Save(FlowProgress progress)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(progress, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Clear()
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }
}
