using System.IO;
using System.Text.Json;

namespace AzurAssistant.Runtime;

/// <summary>Estimated cost of a material: stamina per unit plus the farm that gives the best rate.</summary>
public sealed record StaminaRate(double PerUnit, string FarmName, int Stamina, double YieldPerRun, bool IsEstimate);

/// <summary>
/// Estimates farming stamina for the growth material list. Materials are matched to the cheapest farm that yields
/// them (farms give several items at once, so the total is an upper bound), falling back to crafting recipes.
/// </summary>
public sealed class GrowthStamina
{
    private readonly Dictionary<string, StaminaRate?> _rates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, string FarmName, int Stamina, double Yield)> _best = new(StringComparer.Ordinal);

    private readonly IReadOnlySet<string> _shopKeys;

    public GrowthStamina(GrowthData data)
    {
        _shopKeys = data.ShopKeys;
        var farmYields = new Dictionary<string, List<(string Name, int Stamina, double Yield)>>(StringComparer.Ordinal);
        foreach (var farm in data.Farms)
        {
            foreach (var (key, quantity) in farm.Yields)
            {
                if (quantity <= 0) continue;
                if (!farmYields.TryGetValue(key, out var list))
                {
                    list = [];
                    farmYields[key] = list;
                }
                list.Add((farm.Name, farm.Stamina, quantity));
            }
        }
        foreach (var (key, candidates) in farmYields)
        {
            // Prefer the lowest stamina per unit; ties go to the bigger yield per run.
            var best = candidates.OrderBy(candidate => candidate.Stamina / candidate.Yield)
                .ThenByDescending(candidate => candidate.Yield)
                .First();
            _best[key] = (key, best.Name, best.Stamina, best.Yield);
            _rates[key] = new StaminaRate(best.Stamina / best.Yield, best.Name, best.Stamina, best.Yield, false);
        }
        // Recipe chains only fill materials no farm produces.
        foreach (var (key, recipe) in data.Recipes)
        {
            if (_rates.ContainsKey(key)) continue;
            var per = RecipeRate(recipe, data, 0);
            if (per is { } rate) _rates[key] = new StaminaRate(rate, "合成", 0, 0, true);
        }
    }

    private double? RecipeRate(GrowthRecipe recipe, GrowthData data, int depth)
    {
        if (depth > 3 || recipe.Costs.Count == 0) return null;
        double total = 0;
        foreach (var cost in recipe.Costs)
        {
            var rate = Find(cost.Kind, cost.Id, data, depth + 1);
            if (rate is null) return null;
            total += rate.Value * cost.Quantity;
        }
        return total;
    }

    private double? Find(string kind, string id, GrowthData data, int depth)
    {
        var key = $"{kind}:{id}";
        if (_rates.TryGetValue(key, out var known)) return known?.PerUnit;
        if (depth > 3) return null;
        var recipe = data.Recipes.FirstOrDefault(entry => entry.Key == key).Value;
        return recipe is null ? null : RecipeRate(recipe, data, depth);
    }

    public StaminaRate? For(string kind, string id) =>
        _rates.TryGetValue($"{kind}:{id}", out var rate) ? rate : null;

    /// <summary>Totals for the given material rows: stamina sum plus the number of runs of the dominant farms.</summary>
    public (long Stamina, int Runs, IReadOnlyList<string> Unavailable) Estimate(IReadOnlyList<GrowthMaterialRow> rows)
    {
        double stamina = 0;
        var runs = 0;
        var unavailable = new List<string>();
        foreach (var row in rows)
        {
            if (row.Missing <= 0) continue;
            var rate = For(row.Kind, row.Id);
            if (rate is null)
            {
                unavailable.Add(_shopKeys.Contains($"{row.Kind}:{row.Id}") ? $"{row.Name}（商店/家园）" : row.Name);
                continue;
            }
            stamina += row.Missing * rate.PerUnit;
            if (rate.YieldPerRun > 0)
                runs += (int)Math.Ceiling(row.Missing / rate.YieldPerRun);
        }
        return ((long)Math.Ceiling(stamina), runs, unavailable);
    }

    /// <summary>Per-row estimate text, e.g. "≈72 体力 / 3 次".</summary>
    public string Describe(GrowthMaterialRow row)
    {
        var rate = For(row.Kind, row.Id);
        if (rate is null) return _shopKeys.Contains($"{row.Kind}:{row.Id}") ? "商店/家园产出" : "无稳定来源";
        if (row.Missing <= 0) return "—";
        var stamina = Math.Ceiling(row.Missing * rate.PerUnit);
        var runs = rate.YieldPerRun > 0 ? (int)Math.Ceiling(row.Missing / rate.YieldPerRun) : 0;
        return runs > 0 ? $"≈{stamina:N0} 体力 / {runs} 次" : $"≈{stamina:N0} 体力";
    }

    public string DescribeFarm(string kind, string id) =>
        _best.TryGetValue($"{kind}:{id}", out var best) ? $"{best.FarmName}（{best.Stamina} 体力/次，产出 {best.Yield:0.##}）" : "—";
}

/// <summary>Keeps the "owned" counts the user typed, so the gap list survives restarts.</summary>
public sealed class GrowthInventoryStore(string path)
{
    public string Path => path;

    public Dictionary<string, long> Load()
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, long>(StringComparer.Ordinal);
            var map = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path));
            return map is null ? new(StringComparer.Ordinal) : new Dictionary<string, long>(map, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(StringComparer.Ordinal); }
    }

    public bool Save(IReadOnlyDictionary<string, long> owned)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);
            var filtered = owned.Where(pair => pair.Value > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            File.WriteAllText(path, JsonSerializer.Serialize(filtered, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
