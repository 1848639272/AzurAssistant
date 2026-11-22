using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzurAssistant.Runtime;

/// <summary>One finished task run, kept for the run report page and CSV export.</summary>
public sealed record RunRecord(string TaskName, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    double DurationSeconds, bool Success, string Message)
{
    public string StartedText => StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string DurationText => DurationSeconds < 60
        ? $"{DurationSeconds:F1} 秒"
        : $"{(int)(DurationSeconds / 60)} 分 {DurationSeconds % 60:F0} 秒";
    public string ResultText => Success ? "成功" : "失败";
}

/// <summary>Append-only JSONL history of task runs. A single writer keeps the file small and easy to export.</summary>
public sealed class RunHistoryStore(string path, int maxRecords = 2000)
{
    private readonly object _gate = new();

    public string Path => path;
    public string? Error { get; private set; }

    public void Append(RunRecord record)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine, Encoding.UTF8);
                Trim();
            }
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "写入运行记录失败：" + ex.Message;
        }
    }

    public IReadOnlyList<RunRecord> ListAll()
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(path)) return [];
                var records = new List<RunRecord>();
                foreach (var line in File.ReadLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<RunRecord>(line) is { } record) records.Add(record);
                    }
                    catch (JsonException) { }
                }
                return records;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "读取运行记录失败：" + ex.Message;
            return [];
        }
    }

    public bool Export(string targetPath, IReadOnlyList<RunRecord> records)
    {
        try
        {
            var builder = new StringBuilder("开始时间,任务,用时(秒),结果,说明\r\n");
            foreach (var record in records)
                builder.AppendLine(string.Join(',',
                    Csv(record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    Csv(record.TaskName), record.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture),
                    Csv(record.ResultText), Csv(record.Message)));
            File.WriteAllText(targetPath, builder.ToString(), new UTF8Encoding(true));
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "导出运行记录失败：" + ex.Message;
            return false;
        }
    }

    public bool Clear()
    {
        try
        {
            lock (_gate) { if (File.Exists(path)) File.Delete(path); }
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "清空运行记录失败：" + ex.Message;
            return false;
        }
    }

    private static string Csv(string value)
    {
        var quoted = value.Replace("\"", "\"\"");
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') ? $"\"{quoted}\"" : quoted;
    }

    private void Trim()
    {
        if (maxRecords <= 0 || !File.Exists(path)) return;
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length <= maxRecords) return;
        File.WriteAllLines(path, lines.Skip(lines.Length - maxRecords), Encoding.UTF8);
    }
}
