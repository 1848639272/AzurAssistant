using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AzurAssistant.Runtime;

/// <summary>One booked line from a settlement screen: what was earned or spent and how much.</summary>
public sealed record LedgerEntry
{
    public DateTimeOffset BookedAt { get; init; } = DateTimeOffset.Now;
    public string ItemName { get; init; } = "";
    public long Quantity { get; init; }
    /// <summary>True for spending (消耗/花费), false for income.</summary>
    public bool Expense { get; init; }
    public string Note { get; init; } = "";

    [JsonIgnore] public long Signed => Expense ? -Quantity : Quantity;
    [JsonIgnore] public string TimeText => BookedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    [JsonIgnore] public string AmountText => (Expense ? "-" : "+") + Quantity.ToString("N0", CultureInfo.InvariantCulture);
    [JsonIgnore] public string KindText => Expense ? "消耗" : "获得";
    public override string ToString() => $"{ItemName} {AmountText}";
}

/// <summary>
/// Reads settlement-screen text (or pasted text) into ledger entries. Deliberately tolerant: OCR output is noisy, so
/// several common shapes are accepted and anything without a number is ignored.
/// </summary>
public static class LedgerParser
{
    public const int MaxEntries = 500;
    private static readonly string[] ExpenseWords = ["消耗", "花费", "扣除", "失去", "支出"];

    private static readonly Regex NameTimesNumber =
        new(@"(?<name>[^\d×xX*+]{2,20}?)\s*[×xX*]\s*(?<number>\d[\d,]{0,11})", RegexOptions.Compiled);

    private static readonly Regex NumberTimesName =
        new(@"(?<number>\d[\d,]{0,11})\s*[×xX*]\s*(?<name>[^\d×xX*+]{2,20})", RegexOptions.Compiled);

    private static readonly Regex NameThenNumber =
        new(@"(?<name>[^\d]{2,20}?)\s+(?<number>\d[\d,]{0,11})\s*$", RegexOptions.Compiled);

    /// <summary>Parses lines into entries; duplicates inside one call are kept (a settlement can list the same item twice).</summary>
    public static IReadOnlyList<LedgerEntry> Parse(IEnumerable<string> lines, DateTimeOffset? bookedAt = null)
    {
        var at = bookedAt ?? DateTimeOffset.Now;
        var entries = new List<LedgerEntry>();
        foreach (var raw in lines)
        {
            if (entries.Count >= MaxEntries) break;
            var line = Clean(raw);
            if (line.Length < 3) continue;
            if (TryParseLine(line, out var name, out var quantity))
                entries.Add(new LedgerEntry
                {
                    BookedAt = at,
                    ItemName = name,
                    Quantity = quantity,
                    Expense = ExpenseWords.Any(word => line.Contains(word, StringComparison.Ordinal)),
                    Note = line.Length > 40 ? line[..40] : line,
                });
        }
        return entries;
    }

    public static bool TryParseLine(string line, out string name, out long quantity)
    {
        name = "";
        quantity = 0;
        foreach (var pattern in new[] { NameTimesNumber, NumberTimesName, NameThenNumber })
        {
            var match = pattern.Match(line);
            if (!match.Success) continue;
            var numberText = match.Groups["number"].Value.Replace(",", "");
            if (!long.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
                continue;
            var candidate = TrimName(match.Groups["name"].Value);
            if (candidate.Length < 1) continue;
            name = candidate;
            quantity = value;
            return true;
        }
        return false;
    }

    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\r' or '\n' or '\t') builder.Append(' ');
            else if (!char.IsWhiteSpace(character) || builder.Length == 0 || builder[^1] != ' ') builder.Append(character);
        }
        return builder.ToString().Trim();
    }

    private static string TrimName(string name)
    {
        var trimmed = name.Trim(' ', '·', '.', ':', '：', '-', '—', '|', '(', ')', '（', '）', '+');
        // "获得的奖励：尤尼" -> "尤尼": everything before the last separator is a label.
        var colon = trimmed.LastIndexOfAny(['：', ':']);
        if (colon >= 0 && colon < trimmed.Length - 1) trimmed = trimmed[(colon + 1)..].Trim();
        foreach (var word in new[] { "的奖励", "获得", "奖励", "掉落", "消耗", "花费" })
            while (trimmed.StartsWith(word, StringComparison.Ordinal) && trimmed.Length > word.Length)
                trimmed = trimmed[word.Length..].Trim();
        return trimmed.Length > 20 ? trimmed[..20] : trimmed;
    }
}

/// <summary>Appends ledger entries to a JSONL file and reports totals.</summary>
public sealed class LedgerStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public string FilePath => Path.GetFullPath(path);

    public bool Append(IEnumerable<LedgerEntry> entries)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var entry in entries) writer.WriteLine(JsonSerializer.Serialize(entry, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public IReadOnlyList<LedgerEntry> Load(int max = 1000)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var entries = new List<LedgerEntry>();
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<LedgerEntry>(line, Json) is { } entry) entries.Add(entry);
                }
                catch (JsonException) { }
            }
            return entries.Count <= max ? entries : entries.Skip(entries.Count - max).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Net quantity per item (income positive, spending negative), largest absolute first.</summary>
    public static IReadOnlyList<(string Name, long Net)> Totals(IEnumerable<LedgerEntry> entries)
        => entries.GroupBy(entry => entry.ItemName, StringComparer.Ordinal)
            .Select(group => (Name: group.Key, Net: group.Sum(entry => entry.Signed)))
            .Where(pair => pair.Net != 0)
            .OrderByDescending(pair => Math.Abs(pair.Net))
            .ToList();

    /// <summary>Totals for one calendar day (local time).</summary>
    public static IReadOnlyList<(string Name, long Net)> DailyTotals(IEnumerable<LedgerEntry> entries, DateTimeOffset day)
        => Totals(entries.Where(entry => entry.BookedAt.ToLocalTime().Date == day.ToLocalTime().Date));

    public bool Clear()
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch (IOException) { return false; }
    }
}
