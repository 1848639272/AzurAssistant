namespace AzurAssistant.Runtime;

public sealed record GrowthRequest(GrowthEntry Entry, int FromLevel, long OwnedExp, int ToLevel, int FromRank, int ToRank, bool IncludeRunes);

/// <summary>One material row: how much is needed, owned (user input) and therefore missing.</summary>
public sealed class GrowthMaterialRow(string name) : System.ComponentModel.INotifyPropertyChanged
{
    private long _owned;

    public string Name { get; } = name;
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
        }
    }

    public long Missing => Math.Max(0, Need - Owned);
    public string Suggestion => Missing == 0 ? "已足够" : $"还差 {Missing:N0}";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed record GrowthCrystal(string Name, int Experience, int Count);

public sealed record GrowthPlan(long Exp, long Gold, IReadOnlyList<GrowthCrystal> Crystals, int CrystalRemainder,
    IReadOnlyList<GrowthMaterialRow> Materials)
{
    public string Summary
    {
        get
        {
            var crystals = Crystals.Count == 0
                ? "不需要结晶"
                : string.Join(" + ", Crystals.Select(crystal => $"{crystal.Name}×{crystal.Count:N0}"));
            var leftover = CrystalRemainder > 0 ? $"（另需 {CrystalRemainder} 点零头，可取整）" : "";
            return $"需要经验 {Exp:N0}，尤尼 {Gold:N0}；星脉结晶：{crystals}{leftover}";
        }
    }
}

/// <summary>Pure computation of a hero / soul-essence growth plan from the planner tables.</summary>
public static class GrowthCalculator
{
    /// <summary>Currency name used by the planner for gold ("尤尼").</summary>
    public const string GoldName = "尤尼";

    public static GrowthPlan Compute(GrowthRequest request, double goldPerExp, IReadOnlyList<(string Name, int Experience)> expItems)
    {
        var entry = request.Entry;
        var exp = Math.Max(0, entry.ExpBetween(request.FromLevel, request.ToLevel) - Math.Max(0, request.OwnedExp));
        var gold = (long)Math.Round(exp * goldPerExp, MidpointRounding.AwayFromZero);

        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        void Add(GrowthItem item) => totals[item.Name] = totals.GetValueOrDefault(item.Name) + item.Quantity;

        // Gold from EXP is the same currency the rank steps require.
        if (gold > 0) totals[GoldName] = gold;
        var fromRank = Math.Clamp(request.FromRank, 1, Math.Max(1, entry.MaxRank));
        var toRank = Math.Clamp(request.ToRank, fromRank, Math.Max(1, entry.MaxRank));
        for (var rank = fromRank; rank < toRank; rank++)
        {
            var step = entry.Ranks.FirstOrDefault(candidate => candidate.Rank == rank);
            if (step is null) continue;
            foreach (var cost in step.Costs) Add(cost);
            if (request.IncludeRunes)
                foreach (var cost in step.RuneCosts) Add(cost);
        }

        var rows = totals.OrderByDescending(pair => pair.Value)
            .Select(pair => new GrowthMaterialRow(pair.Key) { Need = pair.Value })
            .ToList();
        var (crystals, remainder) = SplitCrystals(exp, expItems);
        return new GrowthPlan(exp, gold, crystals, remainder, rows);
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
