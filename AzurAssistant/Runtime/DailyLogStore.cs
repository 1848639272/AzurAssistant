using System.Globalization;
using System.IO;
using System.Text;

namespace AzurAssistant.Runtime;

public enum LogLevel { Info, Warning, Error }

public sealed record LogEntry(long Id, DateTimeOffset Time, string Message, LogLevel Level = LogLevel.Info)
{
    public string LevelText => Level switch { LogLevel.Warning => "WARN", LogLevel.Error => "ERROR", _ => "INFO" };
    public override string ToString() => $"{Time:HH:mm:ss} [{LevelText}]   {Message}";
}

/// <summary>Small local log sink: daily UTF-8 files, bounded UI history, calendar-day retention.</summary>
public sealed class DailyLogStore
{
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _recent = new();
    private readonly TimeProvider _clock;
    private readonly StandardOutputLog _stdout;
    private DateOnly? _lastCleanup;
    private long _sequence;
    public string DirectoryPath { get; }
    public int RetentionDays { get; }
    public string? StorageError { get; private set; }

    public DailyLogStore(string directory, int retentionDays = 7, TimeProvider? clock = null, StandardOutputLog? stdout = null)
    {
        if (retentionDays < 1) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        DirectoryPath = Path.GetFullPath(directory);
        RetentionDays = retentionDays;
        _clock = clock ?? TimeProvider.System;
        _stdout = stdout ?? StandardOutputLog.Shared;
    }

    public IReadOnlyList<LogEntry> ReadAfter(long id)
    {
        lock (_gate) return _recent.Where(entry => entry.Id > id).ToArray();
    }

    public void Write(string message) => Write(message, LogLevel.Info);

    public void Write(string message, LogLevel level)
    {
        lock (_gate)
        {
            var now = _clock.GetLocalNow();
            var entry = new LogEntry(++_sequence, now, message.Replace('\r', ' ').Replace('\n', ' '), level);
            _recent.Enqueue(entry);
            while (_recent.Count > 200) _recent.Dequeue();
            var line = $"{now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} [PID {Environment.ProcessId}] [{entry.LevelText}] {entry.Message}";
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var today = DateOnly.FromDateTime(now.DateTime);
                var path = Path.Combine(DirectoryPath, $"AzurAssistant-{today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(line);
                writer.Flush();
                StorageError = null;
                if (_lastCleanup != today) { _lastCleanup = today; Cleanup(today); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StorageError = $"日志写入失败：{ex.Message}";
            }
            _stdout.WriteLine(line);
        }
    }

    public void Maintain()
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
            if (_lastCleanup == today) return;
            try { Directory.CreateDirectory(DirectoryPath); _lastCleanup = today; Cleanup(today); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StorageError = $"日志清理失败：{ex.Message}"; }
        }
    }

    private void Cleanup(DateOnly today)
    {
        var oldestKept = today.AddDays(1 - RetentionDays);
        // Only files owned by this sink, directly inside its directory; never recurse or touch arbitrary logs.
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "AzurAssistant-????-??-??.log", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != 24 || !DateOnly.TryParseExact(name[14..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            if (date >= oldestKept || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException($"无法清理 {Path.GetFileName(path)}", ex); }
        }
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, FailureScreenshotStore.FilePrefix + "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            var dateStart = FailureScreenshotStore.FilePrefix.Length;
            if (!(name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".png.tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json.tmp", StringComparison.OrdinalIgnoreCase))
                || name.Length < dateStart + 11 || name[dateStart + 10] != '-'
                || !DateOnly.TryParseExact(name.Substring(dateStart, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date >= oldestKept || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException($"无法清理 {name}", ex); }
        }
    }
}
