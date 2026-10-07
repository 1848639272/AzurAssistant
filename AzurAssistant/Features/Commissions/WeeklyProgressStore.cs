using System.IO;
using System.Text.Json;

namespace AzurAssistant.Features.Commissions;

public sealed record WeeklyProgress(int SchemaVersion, string Week, int Claimed);

/// <summary>Local cache only; a readable in-game remaining count takes precedence over it.</summary>
public sealed class WeeklyProgressStore(string path, Func<DateTimeOffset>? clock = null)
{
    private readonly object _gate = new();
    private DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();
    public static string WeekKey(DateTimeOffset now)
    {
        var date = now.ToOffset(TimeSpan.FromHours(8)).AddHours(-4).Date;
        return date.AddDays(-((int)date.DayOfWeek + 6) % 7).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }
    public int Claimed
    {
        get
        {
            lock (_gate)
            {
                if (!File.Exists(path)) return 0;
                try
                {
                    var value = JsonSerializer.Deserialize<WeeklyProgress>(File.ReadAllText(path));
                    return value is { SchemaVersion: 1, Claimed: >= 0 and <= 3 } && value.Week == WeekKey(Now) ? value.Claimed : 0;
                }
                catch (JsonException) { return 0; }
            }
        }
    }
    public void ObserveRemaining(int remaining)
    {
        if (remaining is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(remaining));
        lock (_gate) CommissionSettingsStore.SaveJson(Path.GetFullPath(path), new WeeklyProgress(1, WeekKey(Now), 3 - remaining));
    }
    public void Reset() => ObserveRemaining(3);
}
