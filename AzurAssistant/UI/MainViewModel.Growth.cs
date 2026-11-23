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

    private string CurrentKind => GrowthKindMap.FirstOrDefault(entry => entry.Label == _growthModule).Kind ?? GrowthData.Hero;

    public string GrowthVersionText => _growth is null
        ? "尚未加载数据"
        : $"数据版本 {_growth.VersionText} · 角色 {_growth.Heroes.Count} / 奇波 {_growth.Pets.Count} / 灵子 {_growth.Souls.Count} / 装备 {_growth.Equipments.Count}";

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
                _growthFromRank, _growthToRank, _growthIncludeRunes),
            _growth.GoldPerExpFor(CurrentKind), _growth.ExpItems, _growth.Icons, IconsDirectory);
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
}
