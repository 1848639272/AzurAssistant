using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzurAssistant.Runtime;

/// <summary>One node of a flow. Blocks (loop/if) carry their children in <see cref="Body"/>.</summary>
public sealed record FlowStep(
    [property: JsonRequired] string Kind,
    double X = 0,
    double Y = 0,
    string? Action = null,
    string? Pack = null,
    string? Template = null,
    int DurationMs = 0,
    int Repeat = 1,
    bool Negate = false,
    IReadOnlyList<FlowStep>? Body = null)
{
    public const string Move = "move";
    public const string Click = "click";
    public const string ActionKind = "action";
    public const string Delay = "delay";
    public const string Wait = "wait";
    public const string Loop = "loop";
    public const string If = "if";

    public static readonly string[] Kinds = [Move, Click, ActionKind, Delay, Wait, Loop, If];

    public bool IsBlock => Kind is Loop or If;

    public string Describe() => Kind switch
    {
        Move => $"移动到 ({X:0.##}, {Y:0.##})",
        Click => $"点击 ({X:0.##}, {Y:0.##})",
        ActionKind => $"执行动作「{Action}」",
        Delay => $"等待 {DurationMs} ms",
        Wait => $"等待识别 {Pack}/{Template}（超时 {DurationMs} ms）",
        Loop => Template is null ? $"循环 {Repeat} 次（{Body?.Count ?? 0} 步）" : $"循环直到{(Negate ? "看不到" : "看到")} {Pack}/{Template}",
        If => $"如果{(Negate ? "看不到" : "看到")} {Pack}/{Template}（{Body?.Count ?? 0} 步）",
        _ => Kind,
    };
}

/// <summary>An ordered, nestable automation flow: moves, clicks, actions, delays, waits, loops and conditions.</summary>
public sealed record FlowDefinition([property: JsonRequired] int SchemaVersion, [property: JsonRequired] string Name,
    [property: JsonRequired] IReadOnlyList<FlowStep> Steps)
{
    public const int CurrentSchema = 1;
    public const int MaxDepth = 4;
    public const int MaxTotalSteps = 500;

    public int TotalSteps => Count(Steps);

    public string Summary => $"{TotalSteps} 个步骤";

    public void Validate()
    {
        if (SchemaVersion != CurrentSchema) throw new InvalidDataException("不支持的流程版本。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80) throw new InvalidDataException("流程名称需要 1–80 字。");
        if (Steps is not { Count: > 0 }) throw new InvalidDataException("流程至少要有一个步骤。");
        if (TotalSteps > MaxTotalSteps) throw new InvalidDataException($"流程步骤过多（上限 {MaxTotalSteps}）。");
        Validate(Steps, 1);
    }

    private static void Validate(IReadOnlyList<FlowStep> steps, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException($"流程嵌套超过 {MaxDepth} 层。");
        foreach (var step in steps)
        {
            if (step is null) throw new InvalidDataException("流程里存在空步骤。");
            if (!FlowStep.Kinds.Contains(step.Kind)) throw new InvalidDataException($"未知步骤类型：{step.Kind}");
            switch (step.Kind)
            {
                case FlowStep.Move:
                case FlowStep.Click:
                    if (!double.IsFinite(step.X) || !double.IsFinite(step.Y)) throw new InvalidDataException("坐标无效。");
                    break;
                case FlowStep.ActionKind:
                    if (string.IsNullOrWhiteSpace(step.Action)) throw new InvalidDataException("动作步骤缺少动作名。");
                    break;
                case FlowStep.Delay:
                    if (step.DurationMs is < 0 or > 600_000) throw new InvalidDataException("等待时间应在 0–600000 毫秒。");
                    break;
                case FlowStep.Wait:
                    if (string.IsNullOrWhiteSpace(step.Template)) throw new InvalidDataException("等待步骤缺少模板名。");
                    if (step.DurationMs is < 100 or > 600_000) throw new InvalidDataException("等待超时应在 100–600000 毫秒。");
                    break;
                case FlowStep.Loop:
                    if (step.Template is null && step.Repeat is < 1 or > 200) throw new InvalidDataException("循环次数应在 1–200。");
                    if (step.Template is not null && string.IsNullOrWhiteSpace(step.Template)) throw new InvalidDataException("循环的退出条件缺少模板名。");
                    if (step.Body is not { Count: > 0 }) throw new InvalidDataException("循环需要有步骤。");
                    Validate(step.Body, depth + 1);
                    break;
                case FlowStep.If:
                    if (string.IsNullOrWhiteSpace(step.Template)) throw new InvalidDataException("条件步骤缺少模板名。");
                    if (step.Body is not { Count: > 0 }) throw new InvalidDataException("条件步骤需要有步骤。");
                    Validate(step.Body, depth + 1);
                    break;
            }
        }
    }

    private static int Count(IReadOnlyList<FlowStep> steps) =>
        steps.Sum(step => 1 + (step.Body is null ? 0 : Count(step.Body)));
}

public sealed record SavedFlow(string Path, FlowDefinition Flow)
{
    public string Name => Flow.Name;
    public string Summary => Flow.Summary;
}

/// <summary>Reads and writes flow files; importing never executes anything.</summary>
public sealed class FlowStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 16,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string DirectoryPath => Path.GetFullPath(directory);

    public FlowDefinition Read(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("流程文件超过 2MB。");
        var flow = JsonSerializer.Deserialize<FlowDefinition>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("流程文件为空。");
        flow.Validate();
        return flow;
    }

    public IReadOnlyList<SavedFlow> List()
    {
        if (!Directory.Exists(directory)) return [];
        var flows = new List<SavedFlow>();
        foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(path => path))
        {
            try { flows.Add(new SavedFlow(path, Read(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or ArgumentException or NotSupportedException) { }
        }
        return flows;
    }

    public string Save(FlowDefinition flow)
    {
        flow.Validate();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName(flow.Name) + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        Write(path, flow);
        return path;
    }

    public void Export(FlowDefinition flow, string path)
    {
        flow.Validate();
        Write(path, flow);
    }

    public string Import(string path) => Save(Read(path));

    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Write(string path, FlowDefinition flow)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(flow, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string FileName(string name)
    {
        var cleaned = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "流程" : cleaned;
    }
}
