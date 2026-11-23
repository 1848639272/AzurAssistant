namespace AzurAssistant.Runtime;

/// <summary>One target the user added to the combined plan (a hero, pet, soul essence or piece of equipment).</summary>
public sealed record CombinedTarget(string Title, IReadOnlyList<GrowthMaterialRow> Rows, string Summary);

/// <summary>Merges several growth plans into one material list, so a batch of upgrades can be farmed in one go.</summary>
public static class GrowthPlanCombiner
{
    public const int MaxTargets = 12;

    public sealed class CombinedMaterial
    {
        public string Name { get; init; } = "";
        public string Key { get; init; } = "";
        public long Need { get; set; }
        public string? IconPath { get; init; }
        public bool HasIcon => !string.IsNullOrWhiteSpace(IconPath);
        public override string ToString() => $"{Name} ×{Need:N0}";
    }

    /// <summary>Sums the needed quantities per material across all targets.</summary>
    public static IReadOnlyList<CombinedMaterial> Combine(IEnumerable<CombinedTarget> targets)
    {
        var totals = new Dictionary<string, CombinedMaterial>(StringComparer.Ordinal);
        foreach (var target in targets)
        foreach (var row in target.Rows)
        {
            if (row.Need <= 0) continue;
            if (totals.TryGetValue(row.Key, out var existing)) { existing.Need += row.Need; continue; }
            totals[row.Key] = new CombinedMaterial { Name = row.Name, Key = row.Key, Need = row.Need, IconPath = row.IconPath };
        }
        return totals.Values.OrderByDescending(material => material.Need).ToList();
    }

    /// <summary>Material rows shaped like the single-plan list, so the shared stamina model can price them.</summary>
    public static IReadOnlyList<GrowthMaterialRow> AsRows(IEnumerable<CombinedMaterial> materials)
        => materials.Select(material =>
        {
            var parts = material.Key.Split(':');
            return new GrowthMaterialRow(material.Name, material.IconPath,
                parts.Length > 1 ? parts[0] : "", parts.Length > 1 ? parts[1] : "") { Need = material.Need };
        }).ToList();
}
