using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace AzurAssistant.Runtime;

/// <summary>One material requirement (name + amount).</summary>
public sealed record GrowthItem(string Name, long Quantity);

/// <summary>One ascension step: level cap, its own costs, and the rune costs of that step.</summary>
public sealed record GrowthRank(int Rank, int LevelLimit, IReadOnlyList<GrowthItem> Costs, IReadOnlyList<GrowthItem> RuneCosts);

/// <summary>A hero or soul-essence progression table: per-level EXP plus the rank costs.</summary>
public sealed record GrowthEntry(string Id, string Title, IReadOnlyList<int> LevelExp, IReadOnlyList<GrowthRank> Ranks)
{
    public int MaxLevel => LevelExp.Count;
    public int MaxRank => Ranks.Count;

    /// <summary>Sum of the EXP needed to walk from <paramref name="fromLevel"/> to <paramref name="toLevel"/>.</summary>
    public long ExpBetween(int fromLevel, int toLevel)
    {
        var from = Math.Clamp(fromLevel, 1, MaxLevel);
        var to = Math.Clamp(toLevel, from, MaxLevel);
        long total = 0;
        for (var level = from; level < to; level++) total += LevelExp[level - 1];
        return total;
    }

    public override string ToString() => $"{Title}（{Id}）";
}

/// <summary>The progression planner data published by promilia.wiki, trimmed to heroes and soul essence.</summary>
public sealed class GrowthData
{
    public string SchemaVersion { get; init; } = "";
    public string SourceVersion { get; init; } = "";
    public double GoldPerExp { get; init; } = 0.2;
    public double SoulGoldPerExp { get; init; } = 0.2;
    public IReadOnlyList<GrowthEntry> Heroes { get; init; } = [];
    public IReadOnlyList<GrowthEntry> Souls { get; init; } = [];
    public IReadOnlyList<(string Name, int Experience)> ExpItems { get; init; } = [];

    public string VersionText => string.IsNullOrWhiteSpace(SourceVersion)
        ? SchemaVersion
        : $"{SourceVersion}（数据格式 {SchemaVersion}）";

    public static GrowthData? Load(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { return null; }
    }

    public static GrowthData Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
        double Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0.2;

        var expItems = new List<(string, int)>();
        if (root.TryGetProperty("expItems", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var experience = item.TryGetProperty("experience", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 0;
                if (name.Length > 0 && experience > 0) expItems.Add((name, experience));
            }
        }

        var heroes = Array(root, "hero");
        var souls = Array(root, "soulessence");
        // Older/large exports keep the tables under "catalog".
        if (heroes.Count == 0 && root.TryGetProperty("catalog", out var catalog))
        {
            heroes = Array(catalog, "hero");
            souls = Array(catalog, "soulessence");
        }
        if (heroes.Count == 0 && souls.Count == 0)
            throw new InvalidDataException("数据里没有找到角色或源脉的养成表。");

        expItems.Sort((left, right) => right.Item2.CompareTo(left.Item2));
        return new GrowthData
        {
            SchemaVersion = Text("schemaVersion"),
            SourceVersion = Text("sourceVersion"),
            GoldPerExp = Number("heroGoldPerExp"),
            SoulGoldPerExp = Number("soulGoldPerExp"),
            Heroes = heroes,
            Souls = souls,
            ExpItems = expItems,
        };
    }

    private static List<GrowthEntry> Array(JsonElement parent, string name)
    {
        var list = new List<GrowthEntry>();
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var entry in array.EnumerateArray())
        {
            var id = entry.TryGetProperty("id", out var idValue) ? idValue.ToString() : "";
            var title = entry.TryGetProperty("title", out var titleValue) ? titleValue.GetString() ?? id : id;
            var levels = new List<int>();
            if (entry.TryGetProperty("levels", out var levelArray) && levelArray.ValueKind == JsonValueKind.Array)
                foreach (var level in levelArray.EnumerateArray())
                    levels.Add(level.ValueKind == JsonValueKind.Number ? level.GetInt32() : 0);
            var ranks = new List<GrowthRank>();
            if (entry.TryGetProperty("ranks", out var rankArray) && rankArray.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var rank in rankArray.EnumerateArray())
                {
                    index++;
                    var rankNumber = rank.TryGetProperty("rank", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : index;
                    var limit = rank.TryGetProperty("levelLimit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                    var costs = Costs(rank, "costs");
                    var runeCosts = new List<GrowthItem>();
                    if (rank.TryGetProperty("runes", out var runes) && runes.ValueKind == JsonValueKind.Array)
                        foreach (var rune in runes.EnumerateArray())
                            runeCosts.AddRange(Costs(rune, "costs"));
                    ranks.Add(new GrowthRank(rankNumber, limit, costs, runeCosts));
                }
            }
            if (levels.Count > 0) list.Add(new GrowthEntry(id, title, levels, ranks));
        }
        return list;
    }

    private static List<GrowthItem> Costs(JsonElement parent, string name)
    {
        var list = new List<GrowthItem>();
        if (!parent.TryGetProperty(name, out var costs) || costs.ValueKind != JsonValueKind.Array) return list;
        foreach (var cost in costs.EnumerateArray())
        {
            var itemName = cost.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var quantity = cost.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt64() : 0;
            if (itemName.Length > 0 && quantity > 0) list.Add(new GrowthItem(itemName, quantity));
        }
        return list;
    }
}

/// <summary>Keeps the planner data current: bundled copy, local import, or download from promilia.wiki.</summary>
public sealed class GrowthDataSource(string cachePath)
{
    public const string DefaultUrl = "https://promilia.wiki/content/v2/progression-planner-v2.json.gz";
    private const string BundledRelative = "assets/growth/growth-data.json";

    public string CachePath => cachePath;
    public string? Error { get; private set; }

    /// <summary>Cache first, then the copy shipped with the app.</summary>
    public GrowthData? Load()
    {
        if (GrowthData.Load(cachePath) is { } cached) return cached;
        var bundled = Path.Combine(AppContext.BaseDirectory, BundledRelative);
        return GrowthData.Load(bundled);
    }

    public GrowthData? Import(string jsonPath)
    {
        try
        {
            var text = File.ReadAllText(jsonPath);
            var data = GrowthData.Parse(text);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, text);
            Error = null;
            return data;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            Error = "导入失败：" + ex.Message;
            return null;
        }
    }

    public async Task<GrowthData?> UpdateAsync(string? url, CancellationToken token)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var bytes = await client.GetByteArrayAsync(string.IsNullOrWhiteSpace(url) ? DefaultUrl : url, token);
            byte[] json;
            if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var output = new MemoryStream();
                await input.CopyToAsync(output, token);
                json = output.ToArray();
            }
            else json = bytes;
            var text = System.Text.Encoding.UTF8.GetString(json);
            var data = GrowthData.Parse(text);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(cachePath, text, token);
            Error = null;
            return data;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or TaskCanceledException)
        {
            Error = "更新失败：" + ex.Message;
            return null;
        }
    }
}
