using System.Collections.ObjectModel;
using System.IO;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>养成计算：从 promilia.wiki 的养成数据算出经验、金币与材料缺口。</summary>
public sealed partial class MainViewModel
{
    private GrowthDataSource? _growthSource;
    private GrowthData? _growth;
    private string _growthStatus = "";
    private string _growthSummary = "";
    private string _growthCrystalText = "";
    private string _growthModule = "角色";
    private GrowthEntry? _selectedGrowthEntry;
    private int _growthFromLevel = 1;
    private int _growthToLevel = 100;
    private int _growthFromRank = 1;
    private int _growthToRank = 1;
    private long _growthOwnedExp;
    private bool _growthIncludeRunes = true;

    public ObservableCollection<string> GrowthModules { get; } = ["角色", "源脉"];
    public ObservableCollection<GrowthEntry> GrowthEntries { get; } = [];
    public ObservableCollection<GrowthMaterialRow> GrowthMaterials { get; } = [];

    private string GrowthDataPath => Path.Combine(
        Path.GetDirectoryName(SoundsDirectory) ?? AppContext.BaseDirectory, "growth-data.json");

    public string GrowthVersionText => _growth is null ? "尚未加载数据" : $"数据版本 {_growth.VersionText}";

    public string GrowthStatus { get => _growthStatus; private set { _growthStatus = value; Notify(); } }
    public string GrowthSummary { get => _growthSummary; private set { _growthSummary = value; Notify(); } }
    public string GrowthCrystalText { get => _growthCrystalText; private set { _growthCrystalText = value; Notify(); } }

    public string SelectedGrowthModule
    {
        get => _growthModule;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _growthModule) return;
            _growthModule = value;
            Notify();
            RefreshGrowthEntries();
        }
    }

    public GrowthEntry? SelectedGrowthEntry
    {
        get => _selectedGrowthEntry;
        set
        {
            if (ReferenceEquals(_selectedGrowthEntry, value)) return;
            _selectedGrowthEntry = value;
            Notify();
            if (value is not null)
            {
                _growthToLevel = Math.Clamp(_growthToLevel, 1, value.MaxLevel);
                _growthFromLevel = Math.Clamp(_growthFromLevel, 1, _growthToLevel);
                _growthToRank = Math.Clamp(_growthToRank, 1, Math.Max(1, value.MaxRank));
                _growthFromRank = Math.Clamp(_growthFromRank, 1, _growthToRank);
                Notify(nameof(GrowthFromLevel));
                Notify(nameof(GrowthToLevel));
                Notify(nameof(GrowthFromRank));
                Notify(nameof(GrowthToRank));
            }
            ComputeGrowth();
        }
    }

    public int GrowthFromLevel
    {
        get => _growthFromLevel;
        set { if (SetLevel(ref _growthFromLevel, value)) { Notify(); ComputeGrowth(); } }
    }

    public int GrowthToLevel
    {
        get => _growthToLevel;
        set { if (SetLevel(ref _growthToLevel, value)) { Notify(); ComputeGrowth(); } }
    }

    public int GrowthFromRank
    {
        get => _growthFromRank;
        set
        {
            var max = Math.Max(1, _selectedGrowthEntry?.MaxRank ?? 1);
            var clamped = Math.Clamp(value, 1, max);
            if (clamped == _growthFromRank) return;
            _growthFromRank = clamped;
            if (_growthToRank < clamped) _growthToRank = clamped;
            Notify();
            Notify(nameof(GrowthToRank));
            ComputeGrowth();
        }
    }

    public int GrowthToRank
    {
        get => _growthToRank;
        set
        {
            var max = Math.Max(1, _selectedGrowthEntry?.MaxRank ?? 1);
            var clamped = Math.Clamp(value, _growthFromRank, max);
            if (clamped == _growthToRank) return;
            _growthToRank = clamped;
            Notify();
            ComputeGrowth();
        }
    }

    public long GrowthOwnedExp
    {
        get => _growthOwnedExp;
        set
        {
            var clamped = Math.Max(0, value);
            if (clamped == _growthOwnedExp) return;
            _growthOwnedExp = clamped;
            Notify();
            ComputeGrowth();
        }
    }

    public bool GrowthIncludeRunes
    {
        get => _growthIncludeRunes;
        set
        {
            if (value == _growthIncludeRunes) return;
            _growthIncludeRunes = value;
            Notify();
            ComputeGrowth();
        }
    }

    private bool SetLevel(ref int field, int value)
    {
        var max = _selectedGrowthEntry?.MaxLevel ?? 100;
        var clamped = Math.Clamp(value, 1, max);
        if (field == clamped) return false;
        field = clamped;
        return true;
    }

    public void LoadGrowthData()
    {
        _growthSource ??= new GrowthDataSource(GrowthDataPath);
        _growth = _growthSource.Load();
        Notify(nameof(GrowthVersionText));
        if (_growth is null)
        {
            GrowthStatus = "没有养成数据。点「更新数据」联网拉取，或「导入本地 JSON」。";
            GrowthEntries.Clear();
            GrowthMaterials.Clear();
            GrowthSummary = "";
            return;
        }
        GrowthStatus = $"已加载：{_growth.Heroes.Count} 个角色、{_growth.Souls.Count} 个源脉、{_growth.ExpItems.Count} 种星脉结晶。";
        RefreshGrowthEntries();
    }

    public void RefreshGrowthEntries()
    {
        GrowthEntries.Clear();
        var source = _growthModule == "源脉" ? _growth?.Souls : _growth?.Heroes;
        if (source is not null)
            foreach (var entry in source) GrowthEntries.Add(entry);
        _selectedGrowthEntry = GrowthEntries.FirstOrDefault();
        _growthToLevel = _selectedGrowthEntry?.MaxLevel ?? 100;
        _growthFromLevel = Math.Min(1, _growthToLevel);
        _growthToRank = _selectedGrowthEntry?.MaxRank ?? 1;
        _growthFromRank = 1;
        Notify(nameof(SelectedGrowthEntry));
        Notify(nameof(GrowthFromLevel));
        Notify(nameof(GrowthToLevel));
        Notify(nameof(GrowthFromRank));
        Notify(nameof(GrowthToRank));
        ComputeGrowth();
    }

    public void ComputeGrowth()
    {
        GrowthMaterials.Clear();
        if (_selectedGrowthEntry is null || _growth is null)
        {
            GrowthSummary = "";
            GrowthCrystalText = "";
            return;
        }
        var goldPerExp = _growthModule == "源脉" ? _growth.SoulGoldPerExp : _growth.GoldPerExp;
        var plan = GrowthCalculator.Compute(new GrowthRequest(_selectedGrowthEntry, _growthFromLevel, _growthOwnedExp,
            _growthToLevel, _growthFromRank, _growthToRank, _growthIncludeRunes), goldPerExp, _growth.ExpItems);
        GrowthSummary = plan.Summary;
        GrowthCrystalText = plan.Crystals.Count == 0
            ? ""
            : string.Join("　", plan.Crystals.Select(crystal => $"{crystal.Name} × {crystal.Count:N0}（{crystal.Experience} 经验/个）"))
              + (plan.CrystalRemainder > 0 ? $"　零头 {plan.CrystalRemainder}" : "");
        foreach (var row in plan.Materials) GrowthMaterials.Add(row);
    }

    public async Task UpdateGrowthDataAsync(string? url)
    {
        _growthSource ??= new GrowthDataSource(GrowthDataPath);
        GrowthStatus = "正在从 promilia.wiki 更新养成数据…";
        var data = await _growthSource.UpdateAsync(url, CancellationToken.None);
        if (data is null)
        {
            GrowthStatus = _growthSource.Error ?? "更新失败。";
            return;
        }
        _growth = data;
        Notify(nameof(GrowthVersionText));
        GrowthStatus = $"已更新：{data.Heroes.Count} 个角色、{data.Souls.Count} 个源脉（{data.VersionText}）。";
        Report(GrowthStatus);
        RefreshGrowthEntries();
    }

    public void ImportGrowthData(string jsonPath)
    {
        _growthSource ??= new GrowthDataSource(GrowthDataPath);
        var data = _growthSource.Import(jsonPath);
        if (data is null)
        {
            GrowthStatus = _growthSource.Error ?? "导入失败。";
            return;
        }
        _growth = data;
        Notify(nameof(GrowthVersionText));
        GrowthStatus = $"已导入：{data.Heroes.Count} 个角色、{data.Souls.Count} 个源脉（{data.VersionText}）。";
        RefreshGrowthEntries();
    }
}
