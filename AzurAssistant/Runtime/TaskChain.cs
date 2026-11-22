using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzurAssistant.Contracts;

namespace AzurAssistant.Runtime;

/// <summary>One entry of a task chain: a job kind plus the name of a saved flow or wheelchair plan.</summary>
public sealed record ChainItem([property: JsonRequired] string Kind, string? Ref = null, int BudgetMinutes = 0)
{
    public const string Daily = "daily";
    public const string Realtime = "realtime";
    public const string Flow = "flow";
    public const string Wheelchair = "wheelchair";

    public static readonly string[] Kinds = [Daily, Realtime, Flow, Wheelchair];

    public string Describe() => Kind switch
    {
        Daily => "一条龙",
        Realtime => "实时辅助",
        Flow => "流程：" + (Ref ?? "?"),
        Wheelchair => "轮椅：" + (Ref ?? "?"),
        _ => Kind,
    };
}

/// <summary>An ordered queue of jobs that run one after another.</summary>
public sealed record TaskChain([property: JsonRequired] int SchemaVersion, [property: JsonRequired] string Name,
    [property: JsonRequired] IReadOnlyList<ChainItem> Items, bool StopOnFailure = true)
{
    public const int CurrentSchema = 1;
    public const int MaxItems = 20;

    public string Summary => $"{Items.Count} 项：" + string.Join(" → ", Items.Select(item => item.Describe()));

    public void Validate()
    {
        if (SchemaVersion != CurrentSchema) throw new InvalidDataException("不支持的任务队列版本。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80) throw new InvalidDataException("队列名称需要 1–80 字。");
        if (Items is not { Count: > 0 and <= MaxItems }) throw new InvalidDataException($"队列需要 1–{MaxItems} 项。");
        foreach (var item in Items)
        {
            if (item is null) throw new InvalidDataException("队列里存在空项。");
            if (!ChainItem.Kinds.Contains(item.Kind)) throw new InvalidDataException($"未知任务类型：{item.Kind}");
            if (item.Kind is ChainItem.Flow or ChainItem.Wheelchair && string.IsNullOrWhiteSpace(item.Ref))
                throw new InvalidDataException($"{item.Describe()} 缺少名称。");
            if (item.BudgetMinutes is < 0 or > 600) throw new InvalidDataException("单项时间预算应在 0–600 分钟。");
        }
    }
}

public sealed record ChainStepResult(string Name, bool Success, string Message, double Seconds, bool OverBudget);

public sealed record ChainOutcome(bool Success, string Message, IReadOnlyList<ChainStepResult> Steps)
{
    public string Report => string.Join("；", Steps.Select(step =>
        $"{step.Name} {(step.Success ? "成功" : "失败")} {step.Seconds:F0}s{(step.OverBudget ? "（超预算）" : "")}"));
}

/// <summary>
/// Runs a chain by awaiting a caller-supplied starter. Pure sequencing logic, so it can be unit-tested with a fake.
/// </summary>
public static class ChainRunner
{
    public static async Task<ChainOutcome> RunAsync(TaskChain chain, Func<ChainItem, CancellationToken, Task<TaskResult>> start,
        CancellationToken token)
    {
        chain.Validate();
        var steps = new List<ChainStepResult>();
        try
        {
        foreach (var item in chain.Items)
        {
            token.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            TaskResult result;
            try
            {
                result = await start(item, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new ChainOutcome(false, $"队列「{chain.Name}」已取消", steps);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new TaskResult(false, ex.Message);
            }
            watch.Stop();
            var seconds = watch.Elapsed.TotalSeconds;
            var overBudget = item.BudgetMinutes > 0 && seconds > item.BudgetMinutes * 60;
            steps.Add(new ChainStepResult(item.Describe(), result.Success, result.Message, seconds, overBudget));
            if (!result.Success && chain.StopOnFailure)
                return new ChainOutcome(false, $"队列「{chain.Name}」在「{item.Describe()}」失败：{result.Message}", steps);
            if (overBudget)
                return new ChainOutcome(false, $"队列「{chain.Name}」在「{item.Describe()}」超出 {item.BudgetMinutes} 分钟预算", steps);
        }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new ChainOutcome(false, $"队列「{chain.Name}」已取消（已完成 {steps.Count} 项）", steps);
        }
        return new ChainOutcome(true, $"队列「{chain.Name}」全部 {steps.Count} 项完成", steps);
    }


}

/// <summary>Reads and writes task chain files.</summary>
public sealed class ChainStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 8,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string DirectoryPath => Path.GetFullPath(directory);

    public TaskChain Read(string path)
    {
        if (new FileInfo(path).Length > 1 * 1024 * 1024) throw new InvalidDataException("队列文件超过 1MB。");
        var chain = JsonSerializer.Deserialize<TaskChain>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("队列文件为空。");
        chain.Validate();
        return chain;
    }

    public IReadOnlyList<(string Path, TaskChain Chain)> List()
    {
        if (!Directory.Exists(directory)) return [];
        var list = new List<(string, TaskChain)>();
        foreach (var path in Directory.GetFiles(directory, "*.json").OrderBy(path => path))
        {
            try { list.Add((path, Read(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                or JsonException or ArgumentException or NotSupportedException) { }
        }
        return list;
    }

    public string Save(TaskChain chain)
    {
        chain.Validate();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName(chain.Name) + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        Write(path, chain);
        return path;
    }

    public void Export(TaskChain chain, string path)
    {
        chain.Validate();
        Write(path, chain);
    }

    public string Import(string path) => Save(Read(path));

    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Write(string path, TaskChain chain)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(chain, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string FileName(string name)
    {
        var cleaned = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "任务队列" : cleaned;
    }
}
