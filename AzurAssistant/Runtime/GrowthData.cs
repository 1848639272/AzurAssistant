using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace AzurAssistant.Runtime;

/// <summary>One material requirement (name + amount) with the optional icon shipped under assets/growth/icons.</summary>
public sealed record GrowthCost(string Kind, string Id, string Name, long Quantity)
{
    public string Key => $"{Kind}:{Id}";
}

/// <summary>One ascension-like step (hero rank, soul rank, equipment refine, pet evolution or pulse node).</summary>
public sealed record GrowthStage(string Label, IReadOnlyList<GrowthCost> Costs, IReadOnlyList<GrowthCost> RuneCosts, int Copies,
    string Kind = "")
{
    public bool HasRuneCosts => RuneCosts.Count > 0;
}

/// <summary>One repeatable stage that yields materials for a stamina cost.</summary>
public sealed record GrowthFarm(string Id, string Name, int Stamina, IReadOnlyList<KeyValuePair<string, double>> Yields);

/// <summary>A pet potential track: feeding the item raises levels, each level costs experience.</summary>
public sealed record GrowthPotential(string Id, string Name, string ItemKind, string ItemId, string ItemName,
    int ItemExperience, IReadOnlyList<int> LevelExp)
{
    public int MaxLevel => LevelExp.Count;

    /// <summary>Experience needed to walk from <paramref name="fromLevel"/> to <paramref name="toLevel"/>.</summary>
    public long ExpBetween(int fromLevel, int toLevel)
    {
        var from = Math.Clamp(fromLevel, 1, Math.Max(1, MaxLevel));
        var to = Math.Clamp(toLevel, from, Math.Max(1, MaxLevel));
        long total = 0;
        for (var level = from; level < to; level++) total += LevelExp[level - 1];
        return total;
    }

    /// <summary>Number of feed items needed (each gives ItemExperience).</summary>
    public long ItemsBetween(int fromLevel, int toLevel)
    {
        if (ItemExperience <= 0) return 0;
        var exp = ExpBetween(fromLevel, toLevel);
        return (exp + ItemExperience - 1) / ItemExperience;
    }

    public override string ToString() => Name;
}

/// <summary>A crafting recipe: costs in, one unit of the keyed product out.</summary>
public sealed record GrowthRecipe(IReadOnlyList<GrowthCost> Costs);

/// <summary>A hero, pet, soul essence or equipment progression table.</summary>
public sealed record GrowthEntry(string Id, string Title, IReadOnlyList<int> LevelExp, IReadOnlyList<GrowthStage> Stages,
    IReadOnlyList<GrowthPotential> Potentials)
{
    public int MaxLevel => LevelExp.Count;
    /// <summary>Equipment tables list levels but no EXP, so the level inputs are meaningless there.</summary>
    public bool HasLevelExp => LevelExp.Any(value => value > 0);
    public int MaxStage => Math.Max(1, Stages.Count);

    /// <summary>Target index that means "complete every stage" (one past the last).</summary>
    public int MaxStageTarget => Stages.Count + 1;

    /// <summary>Sum of the EXP needed to walk from <paramref name="fromLevel"/> to <paramref name="toLevel"/>.</summary>
    public long ExpBetween(int fromLevel, int toLevel)
    {
        var from = Math.Clamp(fromLevel, 1, Math.Max(1, MaxLevel));
        var to = Math.Clamp(toLevel, from, Math.Max(1, MaxLevel));
        long total = 0;
        for (var level = from; level < to; level++) total += LevelExp[level - 1];
        return total;
    }

    /// <summary>Label of a 1-based stage index, for the UI hints.</summary>
    public string StageLabel(int index) => index >= 1 && index <= Stages.Count
        ? Stages[index - 1].Label
        : index == Stages.Count + 1 ? "全部完成" : $"阶段 {index}";

    public override string ToString() => $"{Title}（{Id}）";
}

/// <summary>The progression planner data published by promilia.wiki, trimmed to hero / pet / soul essence / equipment.</summary>
public sealed class GrowthData
{
    public const string Hero = "hero";
    public const string Pet = "pet";
    public const string Soul = "soulessence";
    public const string Equipment = "equipment";

    public string SchemaVersion { get; init; } = "";
    public string SourceVersion { get; init; } = "";
    public double HeroGoldPerExp { get; init; } = 0.2;
    public double SoulGoldPerExp { get; init; } = 0.2;
    public IReadOnlyList<GrowthEntry> Heroes { get; init; } = [];
    public IReadOnlyList<GrowthEntry> Pets { get; init; } = [];
    public IReadOnlyList<GrowthEntry> Souls { get; init; } = [];
    public IReadOnlyList<GrowthEntry> Equipments { get; init; } = [];
    public IReadOnlyDictionary<string, string> Icons { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<(string Name, int Experience)> ExpItems { get; init; } = [];
    public IReadOnlyList<GrowthFarm> Farms { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, GrowthRecipe>> Recipes { get; init; } = [];
    public int StaminaSeconds { get; init; } = 360;
    public int DailyResetHour { get; init; } = 4;
    /// <summary>Materials that only come from shop exchanges / home production, not from farms.</summary>
    public IReadOnlySet<string> ShopKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public string VersionText => string.IsNullOrWhiteSpace(SourceVersion) ? SchemaVersion : $"{SourceVersion}（数据格式 {SchemaVersion}）";

    public IReadOnlyList<GrowthEntry> For(string kind) => kind switch
    {
        Pet => Pets,
        Soul => Souls,
        Equipment => Equipments,
        _ => Heroes,
    };

    /// <summary>Gold per EXP for a kind; pet and equipment have no published coefficient, so 0 = not shown.</summary>
    public double GoldPerExpFor(string kind) => kind switch
    {
        Soul => SoulGoldPerExp,
        Hero => HeroGoldPerExp,
        _ => 0,
    };

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

        var icons = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in resources.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                if (property.Value.TryGetProperty("icon", out var icon) && icon.GetString() is { Length: > 0 } file)
                    icons[property.Name] = file;
            }
        }

        var heroes = Entries(root, "hero");
        var pets = Entries(root, "pet");
        var souls = Entries(root, "soulessence");
        var equipments = Entries(root, "equipment");
        // Older exports keep the tables under "catalog".
        if (heroes.Count + pets.Count + souls.Count + equipments.Count == 0 && root.TryGetProperty("catalog", out var catalog))
        {
            heroes = Entries(catalog, "hero");
            pets = Entries(catalog, "pet");
            souls = Entries(catalog, "soulessence");
            equipments = Entries(catalog, "equipment");
        }
        if (heroes.Count + pets.Count + souls.Count + equipments.Count == 0)
            throw new InvalidDataException("数据里没有找到角色、奇波、灵子或装备的养成表。");

        expItems.Sort((left, right) => right.Item2.CompareTo(left.Item2));
        var farms = new List<GrowthFarm>();
        if (root.TryGetProperty("farms", out var farmArray) && farmArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var farm in farmArray.EnumerateArray())
            {
                var id = farm.TryGetProperty("id", out var fid) ? fid.GetString() ?? "" : "";
                var farmName = farm.TryGetProperty("name", out var fname) ? fname.GetString() ?? id : id;
                var stamina = farm.TryGetProperty("stamina", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0;
                var yields = new List<KeyValuePair<string, double>>();
                if (farm.TryGetProperty("yields", out var yieldsElement) && yieldsElement.ValueKind == JsonValueKind.Object)
                    foreach (var property in yieldsElement.EnumerateObject())
                        if (property.Value.ValueKind == JsonValueKind.Number)
                            yields.Add(new KeyValuePair<string, double>(property.Name, property.Value.GetDouble()));
                if (id.Length > 0 && stamina > 0 && yields.Count > 0)
                    farms.Add(new GrowthFarm(id, farmName, stamina, yields));
            }
        }

        var shopKeys = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("shopKeys", out var shopArray) && shopArray.ValueKind == JsonValueKind.Array)
            foreach (var entry in shopArray.EnumerateArray())
                if (entry.GetString() is { Length: > 0 } shopKey) shopKeys.Add(shopKey);

        var recipes = new List<KeyValuePair<string, GrowthRecipe>>();
        if (root.TryGetProperty("recipes", out var recipeObject) && recipeObject.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in recipeObject.EnumerateObject())
            {
                var costs = new List<GrowthCost>();
                if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("costs", out var costArray))
                    costs.AddRange(Costs(property.Value, "costs"));
                recipes.Add(new KeyValuePair<string, GrowthRecipe>(property.Name, new GrowthRecipe(costs)));
            }
        }
        return new GrowthData
        {
            SchemaVersion = Text("schemaVersion"),
            SourceVersion = Text("sourceVersion"),
            HeroGoldPerExp = Number("heroGoldPerExp"),
            SoulGoldPerExp = Number("soulGoldPerExp"),
            Heroes = heroes,
            Pets = pets,
            Souls = souls,
            Equipments = equipments,
            Icons = icons,
            ExpItems = expItems,
            Farms = farms,
            Recipes = recipes,
            ShopKeys = shopKeys,
        };
    }

    private static List<GrowthEntry> Entries(JsonElement parent, string name)
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

            var stages = new List<GrowthStage>();
            if (entry.TryGetProperty("stages", out var stageArray) && stageArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var stage in stageArray.EnumerateArray())
                {
                    var label = stage.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                    var copies = stage.TryGetProperty("copies", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                    var kind = stage.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                    stages.Add(new GrowthStage(label, Costs(stage, "costs"), Costs(stage, "runeCosts"), copies, kind));
                }
            }
            // Older/raw shapes: ranks/upgrades trees are folded into stages by the data builder, but accept them too.
            if (stages.Count == 0) stages.AddRange(Ranks(entry));
            // Equipment has an empty level table: its progress is 强化 stages plus 升星 stars only.
            var potentials = new List<GrowthPotential>();
            if (entry.TryGetProperty("potentials", out var potentialArray) && potentialArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var potential in potentialArray.EnumerateArray())
                {
                    var pId = potential.TryGetProperty("id", out var pid) ? pid.ToString() : "";
                    var pName = potential.TryGetProperty("name", out var pname) ? pname.GetString() ?? "" : "";
                    var itemId = potential.TryGetProperty("itemId", out var iid) ? iid.ToString() : "";
                    var itemName = potential.TryGetProperty("itemName", out var iname) ? iname.GetString() ?? "" : "";
                    var itemExp = potential.TryGetProperty("itemExperience", out var iexp) && iexp.ValueKind == JsonValueKind.Number
                        ? iexp.GetInt32() : 0;
                    var levelExp = new List<int>();
                    if (potential.TryGetProperty("levels", out var potentialLevels) && potentialLevels.ValueKind == JsonValueKind.Array)
                        foreach (var value in potentialLevels.EnumerateArray())
                            levelExp.Add(value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0);
                    if (pName.Length > 0 && itemName.Length > 0 && levelExp.Count > 0)
                        potentials.Add(new GrowthPotential(pId, pName, "3", itemId, itemName, itemExp, levelExp));
                }
            }
            if (levels.Count > 0 || stages.Count > 0) list.Add(new GrowthEntry(id, title, levels, stages, potentials));
        }
        return list;
    }

    private static IEnumerable<GrowthStage> Ranks(JsonElement entry)
    {
        foreach (var key in new[] { "ranks", "upgrades" })
        {
            if (!entry.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array) continue;
            foreach (var rank in array.EnumerateArray())
            {
                var number = rank.TryGetProperty("rank", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
                var star = rank.TryGetProperty("star", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
                var label = number > 0 ? $"阶级 {number}" : star > 0 ? $"精炼 {star}" : "阶段";
                var runeCosts = new List<GrowthCost>();
                if (rank.TryGetProperty("runes", out var runes) && runes.ValueKind == JsonValueKind.Array)
                    foreach (var rune in runes.EnumerateArray())
                        runeCosts.AddRange(Costs(rune, "costs"));
                yield return new GrowthStage(label, Costs(rank, "costs"), runeCosts, 0);
            }
        }
    }

    private static List<GrowthCost> Costs(JsonElement parent, string name)
    {
        var list = new List<GrowthCost>();
        if (!parent.TryGetProperty(name, out var costs) || costs.ValueKind != JsonValueKind.Array) return list;
        foreach (var cost in costs.EnumerateArray())
        {
            var itemName = cost.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var quantity = cost.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt64() : 0;
            var id = cost.TryGetProperty("id", out var i) ? i.ToString() : "";
            var kind = cost.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "3";
            if (itemName.Length > 0 && quantity > 0) list.Add(new GrowthCost(kind, id, itemName, quantity));
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

    public GrowthData? Load()
    {
        if (GrowthData.Load(cachePath) is { } cached) return cached;
        return GrowthData.Load(Path.Combine(AppContext.BaseDirectory, BundledRelative));
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
