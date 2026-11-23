using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>养成计算：角色 / 奇波 / 灵子 / 装备 的经验、金币与材料缺口。</summary>
public sealed partial class MainViewModel
{
    private GrowthDataSource? _growthSource;
    private GrowthData? _growth;
    private GrowthStamina? _stamina;
    private GrowthInventoryStore? _inventory;
    private readonly Dictionary<string, long> _owned = new(StringComparer.Ordinal);
    private System.Windows.Threading.DispatcherTimer? _inventorySave;
    private string _growthStaminaText = "";
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

    private static readonly (string Label, string Kind)[] GrowthKindMap =
    [
        ("角色", GrowthData.Hero),
        ("奇波", GrowthData.Pet),
        ("灵子", GrowthData.Soul),
        ("装备", GrowthData.Equipment),
    ];

    public ObservableCollection<string> GrowthModules { get; } = ["角色", "奇波", "灵子", "装备"];
    public ObservableCollection<GrowthEntry> GrowthEntries { get; } = [];
    public ObservableCollection<GrowthMaterialRow> GrowthMaterials { get; } = [];

    private string GrowthDataPath => Path.Combine(
        Path.GetDirectoryName(SoundsDirectory) ?? AppContext.BaseDirectory, "growth-data.json");

    private static string IconsDirectory => Path.Combine(AppContext.BaseDirectory, "assets", "growth", "icons");

    private static string InventoryPath => Path.Combine(AppContext.BaseDirectory, "growth-inventory.json");

    private string CurrentKind => GrowthKindMap.FirstOrDefault(entry => entry.Label == _growthModule).Kind ?? GrowthData.Hero;

    public string GrowthVersionText => _growth is null
        ? "尚未加载数据"
        : $"数据版本 {_growth.VersionText} · 角色 {_growth.Heroes.Count} / 奇波 {_growth.Pets.Count} / 灵子 {_growth.Souls.Count} / 装备 {_growth.Equipments.Count}";

    public string GrowthStaminaText { get => _growthStaminaText; private set { _growthStaminaText = value; Notify(); } }

    public string GrowthStatus { get => _growthStatus; private set { _growthStatus = value; Notify(); } }
    public string GrowthSummary { get => _growthSummary; private set { _growthSummary = value; Notify(); } }
    public string GrowthCrystalText { get => _growthCrystalText; private set { _growthCrystalText = value; Notify(); } }

    /// <summary>Shows which stage index currently means what, so the from/to boxes are understandable.</summary>
    /// <summary>False for equipment (no level EXP in the data), so the level boxes are disabled.</summary>
    public bool GrowthHasLevels => _selectedGrowthEntry?.HasLevelExp ?? false;

    public string GrowthStageHint
    {
        get
        {
            if (_selectedGrowthEntry is not { } entry) return "";
            if (entry.MaxStage <= 1) return "该对象没有阶段（只算等级）。";
            var from = entry.StageLabel(Math.Clamp(_growthFromRank, 1, entry.MaxStage));
            var to = entry.StageLabel(Math.Clamp(_growthToRank, 1, entry.MaxStageTarget));
            return $"阶段共 {entry.MaxStage} 个（目标填 {entry.MaxStageTarget} 表示全部完成）：当前 = {_growthFromRank}「{from}」，目标 = {_growthToRank}「{to}」";
        }
    }

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
                _growthToLevel = Math.Clamp(_growthToLevel, 1, Math.Max(1, value.MaxLevel));
                _growthFromLevel = Math.Clamp(_growthFromLevel, 1, _growthToLevel);
                _growthToRank = Math.Clamp(_growthToRank, 1, Math.Max(1, value.MaxStageTarget));
                _growthFromRank = Math.Clamp(_growthFromRank, 1, _growthToRank);
                Notify(nameof(GrowthFromLevel));
                Notify(nameof(GrowthToLevel));
                Notify(nameof(GrowthFromRank));
                Notify(nameof(GrowthToRank));
            }
            Notify(nameof(GrowthStageHint));
            Notify(nameof(GrowthHasLevels));
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
            var max = Math.Max(1, _selectedGrowthEntry?.MaxStage ?? 1);
            var clamped = Math.Clamp(value, 1, max);
            if (clamped == _growthFromRank) return;
            _growthFromRank = clamped;
            if (_growthToRank < clamped) _growthToRank = clamped;
            Notify();
            Notify(nameof(GrowthToRank));
            Notify(nameof(GrowthStageHint));
            Notify(nameof(GrowthHasLevels));
            ComputeGrowth();
        }
    }

    public int GrowthToRank
    {
        get => _growthToRank;
        set
        {
            var max = Math.Max(1, _selectedGrowthEntry?.MaxStageTarget ?? 1);
            var clamped = Math.Clamp(value, _growthFromRank, max);
            if (clamped == _growthToRank) return;
            _growthToRank = clamped;
            Notify();
            Notify(nameof(GrowthStageHint));
            Notify(nameof(GrowthHasLevels));
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
        var max = Math.Max(1, _selectedGrowthEntry?.MaxLevel ?? 100);
        var clamped = Math.Clamp(value, 1, max);
        if (field == clamped) return false;
        field = clamped;
        return true;
    }

    public void LoadGrowthData()
    {
        _growthSource ??= new GrowthDataSource(GrowthDataPath);
        _growth = _growthSource.Load();
        if (_growth is not null) _stamina ??= new GrowthStamina(_growth);
        if (_inventory is null)
        {
            _inventory = new GrowthInventoryStore(InventoryPath);
            foreach (var pair in _inventory.Load()) _owned[pair.Key] = pair.Value;
        }
        Notify(nameof(GrowthVersionText));
        if (_growth is null)
        {
            GrowthStatus = "没有养成数据。点「更新数据」联网拉取，或「导入本地 JSON」。";
            GrowthEntries.Clear();
            GrowthMaterials.Clear();
            GrowthSummary = "";
            return;
        }
        GrowthStatus = $"已加载：角色 {_growth.Heroes.Count}、奇波 {_growth.Pets.Count}、灵子 {_growth.Souls.Count}、装备 {_growth.Equipments.Count}，{_growth.ExpItems.Count} 种星脉结晶，{_growth.Icons.Count} 个材料图标。";
        RefreshGrowthEntries();
    }

    public void RefreshGrowthEntries()
    {
        GrowthEntries.Clear();
        var source = _growth?.For(CurrentKind);
        if (source is not null)
            foreach (var entry in source) GrowthEntries.Add(entry);
        _selectedGrowthEntry = GrowthEntries.FirstOrDefault();
        _growthToLevel = _selectedGrowthEntry?.MaxLevel ?? 100;
        _growthFromLevel = 1;
        _growthToRank = _selectedGrowthEntry?.MaxStageTarget ?? 1;
        _growthFromRank = 1;
        RefreshPotentials();
        Notify(nameof(SelectedGrowthEntry));
        Notify(nameof(GrowthFromLevel));
        Notify(nameof(GrowthToLevel));
        Notify(nameof(GrowthFromRank));
        Notify(nameof(GrowthToRank));
        Notify(nameof(GrowthStageHint));
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
        var plan = GrowthCalculator.Compute(
            new GrowthRequest(_selectedGrowthEntry, _growthFromLevel, _growthOwnedExp, _growthToLevel,
                _growthFromRank, _growthToRank, _growthIncludeRunes, _selectedPotential, _potentialFromLevel, _potentialToLevel),
            _growth.GoldPerExpFor(CurrentKind), _growth.ExpItems, _growth.Icons, IconsDirectory);
        GrowthSummary = plan.Summary;
        GrowthCrystalText = plan.Crystals.Count == 0
            ? ""
            : string.Join("　", plan.Crystals.Select(crystal => $"{crystal.Name} × {crystal.Count:N0}（{crystal.Experience} 经验/个）"))
              + (plan.CrystalRemainder > 0 ? $"　零头 {plan.CrystalRemainder}" : "");
        foreach (var row in plan.Materials)
        {
            if (_owned.TryGetValue(row.Key, out var owned)) row.Owned = owned;
            if (_stamina is not null) row.StaminaText = _stamina.Describe(row);
            row.PropertyChanged += OnGrowthRowChanged;
            GrowthMaterials.Add(row);
        }
        UpdateStaminaTotal();
        _lastPlan = plan;
        GrowthPotentialText = PotentialText();
    }

    /// <summary>Persists the owned counts the user typed (debounced) and refreshes the totals.</summary>
    private void OnGrowthRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GrowthMaterialRow.Owned) || sender is not GrowthMaterialRow row) return;
        _owned[row.Key] = row.Owned;
        _inventorySave ??= CreateInventoryTimer();
        _inventorySave.Stop();
        _inventorySave.Start();
        UpdateStaminaTotal();
    }

    private System.Windows.Threading.DispatcherTimer CreateInventoryTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _inventory?.Save(_owned);
        };
        return timer;
    }

    private void UpdateStaminaTotal()
    {
        if (_stamina is null || GrowthMaterials.Count == 0)
        {
            GrowthStaminaText = "";
            return;
        }
        var (stamina, runs, unavailable) = _stamina.Estimate(GrowthMaterials);
        var missing = GrowthMaterials.Where(row => row.Missing > 0).ToList();
        if (missing.Count == 0)
        {
            GrowthStaminaText = "材料都够了，不需要刷。";
            return;
        }
        var text = $"剩余材料预计需要 ≈{stamina:N0} 体力，约 {runs} 次副本（按最省体力的副本估，多材料副本的实际消耗更低）";
        if (unavailable.Count > 0) text += $"；以下材料没有稳定来源：{string.Join("、", unavailable.Take(6))}";
        GrowthStaminaText = text;
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
        GrowthStatus = $"已更新：角色 {data.Heroes.Count}、奇波 {data.Pets.Count}、灵子 {data.Souls.Count}、装备 {data.Equipments.Count}（{data.VersionText}）。";
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
        GrowthStatus = $"已导入：角色 {data.Heroes.Count}、奇波 {data.Pets.Count}、灵子 {data.Souls.Count}、装备 {data.Equipments.Count}（{data.VersionText}）。";
        RefreshGrowthEntries();
    }

    /// <summary>Material icon as a cached, frozen bitmap so the list does not reload PNGs while scrolling.</summary>
    public static ImageSource? LoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }
    // ---- 潜能（奇波）与导出 ----

    private GrowthPotential? _selectedPotential;
    private int _potentialFromLevel = 1;
    private int _potentialToLevel;
    private GrowthPlan? _lastPlan;
    private string _growthPotentialText = "";

    public ObservableCollection<GrowthPotential> GrowthPotentials { get; } = [];
    public bool GrowthHasPotentials => GrowthPotentials.Count > 0;
    public string GrowthPotentialText { get => _growthPotentialText; private set { _growthPotentialText = value; Notify(); } }

    public GrowthPotential? SelectedGrowthPotential
    {
        get => _selectedPotential;
        set
        {
            if (ReferenceEquals(_selectedPotential, value)) return;
            _selectedPotential = value;
            if (value is not null)
            {
                _potentialToLevel = value.MaxLevel;
                _potentialFromLevel = 1;
                Notify(nameof(GrowthPotentialFrom));
                Notify(nameof(GrowthPotentialTo));
            }
            Notify();
            ComputeGrowth();
        }
    }

    public int GrowthPotentialFrom
    {
        get => _potentialFromLevel;
        set
        {
            var max = Math.Max(1, _selectedPotential?.MaxLevel ?? 1);
            var clamped = Math.Clamp(value, 1, max);
            if (clamped == _potentialFromLevel) return;
            _potentialFromLevel = clamped;
            if (_potentialToLevel < clamped) _potentialToLevel = clamped;
            Notify();
            Notify(nameof(GrowthPotentialTo));
            ComputeGrowth();
        }
    }

    public int GrowthPotentialTo
    {
        get => _potentialToLevel;
        set
        {
            var max = Math.Max(1, _selectedPotential?.MaxLevel ?? 1);
            var clamped = Math.Clamp(value, _potentialFromLevel, max);
            if (clamped == _potentialToLevel) return;
            _potentialToLevel = clamped;
            Notify();
            ComputeGrowth();
        }
    }

    private void RefreshPotentials()
    {
        GrowthPotentials.Clear();
        var potentials = _selectedGrowthEntry?.Potentials;
        if (potentials is not null)
            foreach (var potential in potentials) GrowthPotentials.Add(potential);
        _selectedPotential = GrowthPotentials.FirstOrDefault();
        _potentialFromLevel = 1;
        _potentialToLevel = _selectedPotential?.MaxLevel ?? 0;
        Notify(nameof(GrowthHasPotentials));
        Notify(nameof(SelectedGrowthPotential));
        Notify(nameof(GrowthPotentialFrom));
        Notify(nameof(GrowthPotentialTo));
    }

    private string PotentialText()
    {
        if (_selectedPotential is not { } potential || GrowthMaterials.Count == 0) return "";
        var items = GrowthMaterials.FirstOrDefault(row => row.Kind == potential.ItemKind && row.Id == potential.ItemId);
        var exp = potential.ExpBetween(_potentialFromLevel, _potentialToLevel);
        if (items is null || items.Need == 0) return $"潜能「{potential.Name}」{_potentialFromLevel}→{_potentialToLevel}：不需要{potential.ItemName}";
        return $"潜能「{potential.Name}」{_potentialFromLevel}→{_potentialToLevel}：需 {potential.ItemName} ×{items.Need:N0}"
               + $"（每个 {potential.ItemExperience} 经验，共 {exp:N0} 经验）";
    }

    public GrowthPlanExport.PlanSnapshot BuildSnapshot()
    {
        var title = _selectedGrowthEntry is null
            ? _growthModule
            : $"{_growthModule} · {_selectedGrowthEntry.Title}（{_selectedGrowthEntry.Id}）";
        var levels = _selectedGrowthEntry is { HasLevelExp: true }
            ? $"等级 {_growthFromLevel} → {_growthToLevel}；"
            : "";
        var stages = _selectedGrowthEntry is { } entry
            ? $"阶段 {_growthFromRank}「{entry.StageLabel(_growthFromRank)}」→ {_growthToRank}「{entry.StageLabel(_growthToRank)}」"
            : "";
        return new GrowthPlanExport.PlanSnapshot(title, GrowthSummary, GrowthCrystalText, GrowthStaminaText,
            GrowthMaterials.ToList(), GrowthPotentialText,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    public string ExportGrowthCsv(string path)
    {
        GrowthPlanExport.ToCsvFile(path, BuildSnapshot());
        GrowthStatus = "已导出 CSV：" + path;
        return path;
    }

    public string ExportGrowthPng(string path)
    {
        GrowthPlanExport.ToPngFile(path, BuildSnapshot());
        GrowthStatus = "已导出 PNG：" + path;
        return path;
    }
}
