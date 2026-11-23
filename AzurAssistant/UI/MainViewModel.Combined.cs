using System.Collections.ObjectModel;
using System.IO;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>多目标合并：把几个养成目标加进同一张材料清单，一次看清总共要刷多少。</summary>
public sealed partial class MainViewModel
{
    private string _combinedSummary = "还没有加入目标。点上面的「加入合并清单」把当前方案加进来。";
    private string _combinedStatus = "";

    public ObservableCollection<CombinedTarget> CombinedTargets { get; } = [];

    public ObservableCollection<GrowthPlanCombiner.CombinedMaterial> CombinedMaterials { get; } = [];

    public string CombinedSummary { get => _combinedSummary; private set { _combinedSummary = value; Notify(); } }

    public string CombinedStatus { get => _combinedStatus; private set { _combinedStatus = value; Notify(); } }

    /// <summary>Adds the plan currently shown on the page to the combined list.</summary>
    public void AddCurrentPlanToCombined()
    {
        if (_lastPlan is null || _selectedGrowthEntry is null)
        {
            CombinedStatus = "先选一个对象并把方案算出来，再加入合并清单。";
            return;
        }
        if (CombinedTargets.Count >= GrowthPlanCombiner.MaxTargets)
        {
            CombinedStatus = $"最多同时合并 {GrowthPlanCombiner.MaxTargets} 个目标。";
            return;
        }
        var title = $"{_growthModule} · {_selectedGrowthEntry.Title}";
        var rows = GrowthMaterials.ToList();
        if (rows.Count == 0)
        {
            CombinedStatus = "当前方案没有材料，无法加入。";
            return;
        }
        CombinedTargets.Add(new CombinedTarget(title, rows, GrowthSummary));
        RecalculateCombined();
        CombinedStatus = $"已加入「{title}」，当前合并 {CombinedTargets.Count} 个目标。";
    }

    public void RemoveLastCombined()
    {
        if (CombinedTargets.Count == 0) return;
        CombinedTargets.RemoveAt(CombinedTargets.Count - 1);
        RecalculateCombined();
        CombinedStatus = CombinedTargets.Count == 0 ? "合并清单已清空。" : $"已移除最后一个目标，剩 {CombinedTargets.Count} 个。";
    }

    public void ClearCombined()
    {
        CombinedTargets.Clear();
        RecalculateCombined();
        CombinedStatus = "合并清单已清空。";
    }

    private void RecalculateCombined()
    {
        CombinedMaterials.Clear();
        if (CombinedTargets.Count == 0)
        {
            CombinedSummary = "还没有加入目标。点上面的「加入合并清单」把当前方案加进来。";
            return;
        }
        var merged = GrowthPlanCombiner.Combine(CombinedTargets);
        foreach (var material in merged) CombinedMaterials.Add(material);

        var names = string.Join("、", CombinedTargets.Select(target => target.Title));
        var rows = GrowthPlanCombiner.AsRows(merged);
        if (_stamina is not null)
        {
            var (stamina, runs, unavailable) = _stamina.Estimate(rows);
            CombinedSummary = $"{CombinedTargets.Count} 个目标（{names}）：共 {merged.Count} 种材料，"
                + $"预计 ≈{stamina:N0} 体力 / {runs} 次副本"
                + (unavailable.Count > 0 ? $"（{unavailable.Count} 种没有稳定来源）" : "");
            return;
        }
        CombinedSummary = $"{CombinedTargets.Count} 个目标（{names}）：共 {merged.Count} 种材料";
    }

    public bool ExportCombined(string csvPath)
    {
        try
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("目标," + string.Join(",", CombinedTargets.Select(target => "\"" + target.Title.Replace("\"", "\"\"") + "\"")));
            builder.AppendLine("材料,合计数量");
            foreach (var material in CombinedMaterials)
                builder.AppendLine($"\"{material.Name}\",{material.Need}");
            File.WriteAllText(csvPath, builder.ToString(), new System.Text.UTF8Encoding(true));
            CombinedStatus = "已导出：" + csvPath;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CombinedStatus = "导出失败：" + ex.Message;
            return false;
        }
    }
}
