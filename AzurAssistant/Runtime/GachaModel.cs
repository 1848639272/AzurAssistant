using System;
using System.Collections.Generic;
using System.Linq;

namespace AzurAssistant.Runtime;

/// <summary>
/// Gacha model reimplemented after GGanalysis' <c>PityBernoulliModel(pity_p, item_p)</c>: each pull uses the
/// probability found at the current pity counter, and a pull that produces an item is the wanted one with
/// probability <see cref="ItemProbability"/>; otherwise the pity counter resets and pulling continues.
/// </summary>
public sealed class GachaModel
{
    public const int MaxPulls = 2000;
    public const int MaxCopies = 7;

    /// <summary>Probability per pull indexed by the pity counter (index 0/1 = first pull after a reset).</summary>
    public IReadOnlyList<double> PityTable { get; }

    /// <summary>Chance that an obtained item is the wanted one (e.g. 0.5 for a 50/50 banner).</summary>
    public double ItemProbability { get; }

    public GachaModel(IReadOnlyList<double> pityTable, double itemProbability)
    {
        if (pityTable is null || pityTable.Count < 2) throw new ArgumentException("保底概率表至少要 2 项。", nameof(pityTable));
        if (itemProbability is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(itemProbability), "出货概率需在 (0,1]。");
        var table = new double[pityTable.Count];
        for (var index = 0; index < pityTable.Count; index++)
            table[index] = Math.Clamp(pityTable[index], 0, 1);
        PityTable = table;
        ItemProbability = itemProbability;
    }

    public int MaxPity => PityTable.Count - 1;

    /// <summary>
    /// Distribution of the number of pulls needed for <paramref name="copies"/> wanted items starting from
    /// <paramref name="startPity"/>. Index n holds the probability of finishing exactly at pull n; the last entry
    /// collects everything that would need more than <paramref name="maxPulls"/> pulls.
    /// </summary>
    public double[] Distribution(int copies, int startPity = 0, int maxPulls = 400)
    {
        copies = Math.Clamp(copies, 1, MaxCopies);
        maxPulls = Math.Clamp(maxPulls, 1, MaxPulls);
        startPity = Math.Clamp(startPity, 0, MaxPity);

        // state[copiesObtained, pity] -> probability
        var state = new double[copies + 1, MaxPity + 1];
        state[0, startPity] = 1;
        var result = new double[maxPulls + 2];
        for (var pull = 1; pull <= maxPulls; pull++)
        {
            var next = new double[copies + 1, MaxPity + 1];
            for (var obtained = 0; obtained < copies; obtained++)
            for (var pity = 0; pity <= MaxPity; pity++)
            {
                var weight = state[obtained, pity];
                if (weight <= 0) continue;
                var p = PityTable[Math.Min(pity + 1, MaxPity)];
                // No item: pity rises.
                if (p < 1)
                {
                    var advanced = Math.Min(pity + 1, MaxPity);
                    next[obtained, advanced] += weight * (1 - p);
                }
                // Item, but not the wanted one.
                if (p > 0 && ItemProbability < 1) next[obtained, 0] += weight * p * (1 - ItemProbability);
                // The wanted item.
                if (p > 0)
                {
                    var hit = weight * p * ItemProbability;
                    if (obtained + 1 >= copies) result[pull] += hit;
                    else next[obtained + 1, 0] += hit;
                }
            }
            state = next;
        }
        var remaining = 0.0;
        for (var obtained = 0; obtained < copies; obtained++)
        for (var pity = 0; pity <= MaxPity; pity++)
            remaining += state[obtained, pity];
        result[^1] = remaining;
        return result;
    }

    /// <summary>Expected number of pulls, ignoring the tail beyond <paramref name="maxPulls"/>.</summary>
    public double Expected(int copies, int startPity = 0, int maxPulls = 400)
    {
        var distribution = Distribution(copies, startPity, maxPulls);
        double total = 0;
        for (var n = 1; n <= maxPulls; n++) total += n * distribution[n];
        return total;
    }

    /// <summary>Probability of finishing within <paramref name="pulls"/> pulls.</summary>
    public double ProbabilityWithin(int pulls, int copies, int startPity = 0, int maxPulls = 400)
    {
        pulls = Math.Clamp(pulls, 0, maxPulls);
        var distribution = Distribution(copies, startPity, maxPulls);
        double total = 0;
        for (var n = 1; n <= pulls; n++) total += distribution[n];
        return total;
    }

    /// <summary>Pulls needed in the worst case (every item at hard pity, losing every 50/50).</summary>
    public int WorstCase(int copies, int startPity = 0)
    {
        copies = Math.Clamp(copies, 1, MaxCopies);
        var pity = Math.Clamp(startPity, 0, MaxPity);
        var pulls = 0;
        // With a guaranteed pull (item probability 100%) a copy costs at most one hard-pity cycle; otherwise the
        // worst case is losing once and then getting the guaranteed copy.
        var cyclesPerCopy = ItemProbability >= 0.999 ? 1 : 2;
        for (var obtained = 0; obtained < copies; obtained++)
        {
            var first = Math.Max(1, MaxPity - pity);
            pity = 0;
            pulls += first + (cyclesPerCopy - 1) * MaxPity;
        }
        return pulls;
    }

    /// <summary>Preset tables matching the shapes used by the common banners.</summary>
    public static GachaModel Preset(string name, double itemProbability = 0.5)
    {
        // 0.6% base with soft pity from the 74th pull (+6% per pull) and a hard pity at 90.
        // 0.8% base; pulls 71-80 add 2 percentage points each, 81-90 add 8 each, hard pity at 90.
        var table = new double[91];
        for (var pull = 1; pull <= 90; pull++)
        {
            if (pull >= 90) table[pull] = 1;
            else if (pull >= 81) table[pull] = Math.Min(1, 0.008 + 10 * 0.02 + (pull - 80) * 0.08);
            else if (pull >= 71) table[pull] = Math.Min(1, 0.008 + (pull - 70) * 0.02);
            else table[pull] = 0.008;
        }
        // 1.6% base, hard pity at 80, no soft ramp (weapon-banner-like shape).
        var weapon = new double[81];
        for (var pull = 1; pull <= 80; pull++) weapon[pull] = pull >= 80 ? 1 : 0.016;
        return name switch
        {
            "weapon" => new GachaModel(weapon, itemProbability),
            _ => new GachaModel(table, itemProbability),
        };
    }

    /// <summary>Parses a table written as "0.006;0.006;…" or "1-73:0.006,74-89:ramp,90:1" style shorthand.</summary>
    public static IReadOnlyList<double> ParseTable(string? text)
    {
        var values = new List<double>();
        if (string.IsNullOrWhiteSpace(text)) return values;
        foreach (var part in text.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)) continue;
            values.Add(value > 1 ? value / 100 : value);   // accept both 0.006 and 0.6
        }
        return values;
    }
}
