using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzurAssistant.Features.Routes;
using AzurAssistant.Game.Navigation;

namespace AzurAssistant.Features.Wheelchair;

/// <summary>One stop of a wheelchair plan: walk to a map coordinate, then (optionally) play a recorded action.</summary>
public sealed record WheelchairStep([property: JsonRequired] string Name, [property: JsonRequired] double X,
    [property: JsonRequired] double Y, string? Action = null, int DelayMs = 0);

/// <summary>
/// A wheelchair plan chains navigation and recorded actions: walk to a coordinate, do something, walk to the next one.
/// Actions are referenced by name so a plan keeps working after the same recording is imported on another machine.
/// </summary>
public sealed record WheelchairPlan([property: JsonRequired] int SchemaVersion, [property: JsonRequired] string Name,
    [property: JsonRequired] RouteMap Map, [property: JsonRequired] IReadOnlyList<WheelchairStep> Steps)
{
    public const int CurrentSchema = 1;

    public IReadOnlyList<string> ActionNames => [.. Steps.Select(step => step.Action)
        .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).Distinct(StringComparer.Ordinal)];

    public string Summary => $"{Steps.Count} 个步骤 · {ActionNames.Count} 个不同动作";

    public NavigationMap Validate(IReadOnlyList<NavigationMap> maps)
    {
        if (SchemaVersion != CurrentSchema) throw new InvalidDataException("不支持的轮椅方案版本，请使用蔚蓝助手方案 JSON。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Map is null || Steps is not { Count: > 0 and <= 2000 })
            throw new InvalidDataException("方案名称、地图或步骤数量无效（1–2000 步）。");
        var map = maps.SingleOrDefault(candidate => (candidate.Id, candidate.Floor, candidate.CoordinateVersion) == (Map.Id, Map.Floor, Map.CoordinateVersion))
            ?? throw new InvalidDataException("方案所属地图、层级或坐标版本不受支持。");
        foreach (var step in Steps)
        {
            if (step is null || string.IsNullOrWhiteSpace(step.Name) || step.Name.Length > 80)
                throw new InvalidDataException("每个步骤需要 1–80 字的名称。");
            if (step.Action is { Length: > 80 }) throw new InvalidDataException("步骤“" + step.Name + "”的动作名称过长。");
            if (step.DelayMs is < 0 or > 600_000) throw new InvalidDataException("步骤“" + step.Name + "”的等待时间必须在 0–600 秒之间。");
            try { map.Position(step.X, step.Y); }
            catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("步骤“" + step.Name + "”的" + ex.Message, ex); }
        }
        return map;
    }
}

public sealed record SavedWheelchairPlan(string Path, WheelchairPlan Plan)
{
    public string Name => Plan.Name;
    public string Summary => Plan.Summary;
}

/// <summary>Reads and writes wheelchair plans; importing a plan never executes it and never overwrites its source.</summary>
public sealed class WheelchairPlanStore(string directory, IReadOnlyList<NavigationMap> maps)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 12,
    };

    public string DirectoryPath => Path.GetFullPath(directory);

    public WheelchairPlan Read(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("方案文件超过 4MB。");
        var plan = JsonSerializer.Deserialize<WheelchairPlan>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("方案文件为空。");
        plan.Validate(maps);
        return plan;
    }

    public IReadOnlyList<SavedWheelchairPlan> List()
    {
        if (!Directory.Exists(directory)) return [];
        var plans = new List<SavedWheelchairPlan>();
        foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(path => path))
        {
            try { plans.Add(new SavedWheelchairPlan(path, Read(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or ArgumentException or NotSupportedException) { }
        }
        return plans;
    }

    public string Save(WheelchairPlan plan)
    {
        plan.Validate(maps);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName(plan.Name) + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        Write(path, plan);
        return path;
    }

    public void Export(WheelchairPlan plan, string path)
    {
        plan.Validate(maps);
        Write(path, plan);
    }

    /// <summary>Validates a plan shared by another player and stores a copy; the source file is left untouched.</summary>
    public string Import(string path)
    {
        var plan = Read(path);
        return Save(plan);
    }

    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Write(string path, WheelchairPlan plan)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(plan, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string FileName(string name)
    {
        var cleaned = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "轮椅方案" : cleaned;
    }
}
