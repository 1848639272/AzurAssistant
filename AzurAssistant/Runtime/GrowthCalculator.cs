using System.IO;

namespace AzurAssistant.Runtime;

public sealed record GrowthRequest(GrowthEntry Entry, int FromLevel, long OwnedExp, int ToLevel, int FromStage, int ToStage,
    bool IncludeRunes);

/// <summary>One material row: how much is needed, owned (user input) and therefore missing, plus its icon.</summary>
public sealed class GrowthMaterialRow(string name, string? iconPath, string kind = "", string id = "") : System.ComponentModel.INotifyPropertyChanged
{
    private long _owned;

    public string Name { get; } = name;
    public string Kind { get; } = kind;
    public string Id { get; } = id;
    /// <summary>"kind:id" — the key used by the inventory store and the stamina model.</summary>
    public string Key => kind.Length > 0 ? $"{kind}:{id}" : name;

    /// <summary>Per-row farming estimate, filled in by the view model.</summary>
    public string StaminaText
    {
        get => _staminaText;
        set { if (_staminaText == value) return; _staminaText = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StaminaText))); }
    }
    private string _staminaText = "";
    public string? IconPath { get; } = iconPath;
    public bool HasIcon => !string.IsNullOrWhiteSpace(IconPath);
    public long Need { get; set; }

    public long Owned
    {
        get => _owned;
        set
        {
            if (_owned == value) return;
            _owned = Math.Max(0, value);
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Owned)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Missing)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Suggestion)));
        }
    }

    public long Missing => Math.Max(0, Need - Owned);
    public string Suggestion => Missing == 0 ? "已足够" : $"还差 {Missing:N0}";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed record GrowthCrystal(string Name, int Experience, int Count);

public sealed record GrowthPlan(long Exp, long Gold, IReadOnlyList<GrowthCrystal> Crystals, int CrystalRemainder,
    IReadOnlyList<GrowthMaterialRow> Materials, int StagesIncluded, string StageBreakdown = "")
{
    public string Summary
    {
        get
        {
            var crystals = Crystals.Count == 0
                ? "不需要结晶"
                : string.Join(" + ", Crystals.Select(crystal => $"{crystal.Name}×{crystal.Count:N0}"));
            var leftover = CrystalRemainder > 0 ? $"（另需 {CrystalRemainder} 点零头，可取整）" : "";
            var gold = Gold > 0 ? $"，尤尼 {Gold:N0}" : "";
            var stages = StagesIncluded > 0
                ? $"，跨 {StagesIncluded} 个阶段" + (StageBreakdown.Length > 0 ? $"（{StageBreakdown}）" : "")
                : "";
            if (Exp == 0)
                return StagesIncluded > 0 ? $"不需要经验{gold}{stages}（该对象按阶段计材料）" : "没有需要计算的内容。";
            return $"需要经验 {Exp:N0}{gold}{stages}；星脉结晶：{crystals}{leftover}";
        }
    }
}

/// <summary>Pure computation of a growth plan from the planner tables (hero / pet / soul essence / equipment).</summary>
public static class GrowthCalculator
{
    /// <summary>Currency name used by the planner for gold ("尤尼").</summary>
    public const string GoldName = "尤尼";

    public static GrowthPlan Compute(GrowthRequest request, double goldPerExp, IReadOnlyList<(string Name, int Experience)> expItems,
        IReadOnlyDictionary<string, string>? icons = null, string iconDirectory = "")
    {
        var entry = request.Entry;
        var exp = Math.Max(0, entry.ExpBetween(request.FromLevel, request.ToLevel) - Math.Max(0, request.OwnedExp));
        var gold = goldPerExp > 0 ? (long)Math.Round(exp * goldPerExp, MidpointRounding.AwayFromZero) : 0;

        // Aggregate by "kind:id" so the same material from different steps merges, and the icon can be resolved.
        var totals = new Dictionary<string, (string Name, string Key, long Quantity)>(StringComparer.Ordinal);
        void Add(GrowthCost cost)
        {
            if (totals.TryGetValue(cost.Key, out var existing))
                totals[cost.Key] = (existing.Name, cost.Key, existing.Quantity + cost.Quantity);
            else
                totals[cost.Key] = (cost.Name, cost.Key, cost.Quantity);
        }

        // Gold from EXP is the same currency the steps require.
        if (gold > 0) totals["10:2"] = (GoldName, "10:2", gold);

        var from = Math.Clamp(request.FromStage, 1, entry.MaxStage);
        var to = Math.Clamp(request.ToStage, from, entry.Stages.Count + 1);
        var included = 0;
        var kinds = new List<string>();
        for (var stage = from; stage < to; stage++)
        {
            if (stage - 1 >= entry.Stages.Count) break;
            var step = entry.Stages[stage - 1];
            included++;
            kinds.Add(step.Kind);
            foreach (var cost in step.Costs) Add(cost);
            if (request.IncludeRunes)
                foreach (var cost in step.RuneCosts) Add(cost);
            // 升星 also consumes copies of the item itself.
            if (step.Copies > 0) Add(new GrowthCost("copy", entry.Id, "本体（同款装备）", step.Copies));
        }

        var rows = totals.Values.OrderByDescending(entry2 => entry2.Quantity)
            .Select(entry2 =>
            {
                var icon = ResolveIcon(entry2.Key, icons, iconDirectory);
                var parts = entry2.Key.Split(':');
                return new GrowthMaterialRow(entry2.Name, icon, parts.Length > 1 ? parts[0] : "", parts.Length > 1 ? parts[1] : "")
                    { Need = entry2.Quantity };
            })
            .ToList();
        var (crystals, remainder) = SplitCrystals(exp, expItems);
        return new GrowthPlan(exp, gold, crystals, remainder, rows, included, Breakdown(kinds));
    }

    private static string Breakdown(IReadOnlyList<string> kinds)
    {
        if (kinds.Count == 0) return "";
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enhance"] = "强化", ["star"] = "升星", ["rank"] = "阶级",
            ["evolve"] = "进化", ["pulse"] = "源脉",
        };
        return string.Join("、", kinds.GroupBy(kind => kind, StringComparer.Ordinal)
            .Select(group => $"{(names.TryGetValue(group.Key, out var label) ? label : group.Key)} {group.Count()} 段"));
    }

    private static string? ResolveIcon(string key, IReadOnlyDictionary<string, string>? icons, string iconDirectory)
    {
        if (icons is null || iconDirectory.Length == 0) return null;
        if (!icons.TryGetValue(key, out var file) || string.IsNullOrWhiteSpace(file)) return null;
        var path = Path.Combine(iconDirectory, file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Greedy split of the EXP requirement into the largest available crystals first.</summary>
    public static (List<GrowthCrystal> Crystals, int Remainder) SplitCrystals(long exp, IReadOnlyList<(string Name, int Experience)> expItems)
    {
        var list = new List<GrowthCrystal>();
        var rest = exp;
        foreach (var (name, experience) in expItems.OrderByDescending(item => item.Experience))
        {
            if (experience <= 0 || rest < experience) continue;
            var count = (int)(rest / experience);
            list.Add(new GrowthCrystal(name, experience, count));
            rest -= (long)count * experience;
        }
        return (list, (int)rest);
    }
}
