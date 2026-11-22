using System.Collections.ObjectModel;
using System.IO;
using AzurAssistant.Features.Routes;
using AzurAssistant.Features.Wheelchair;
using AzurAssistant.Game.Navigation;

namespace AzurAssistant.UI;

/// <summary>One editable wheelchair step: a coordinate plus the action to play after arriving.</summary>
public sealed class WheelchairStepDraft(string name, double x, double y)
{
    public string Name { get; set; } = name;
    public double X { get; } = x;
    public double Y { get; } = y;
    public string Action { get; set; } = "";
    public string DelayText { get; set; } = "0";
    public string PositionText => $"({X:0.##}, {Y:0.##})";
}

/// <summary>轮椅功能：坐标 → 动作 的流水线编排、导入导出与运行。</summary>
public sealed partial class MainViewModel
{
    private WheelchairPlanStore? _wheelchairStore;
    private WheelchairPlanStore WheelchairPlans => _wheelchairStore ??=
        new(wheelchairDirectory ?? Path.Combine(AppContext.BaseDirectory, "wheelchair"), NavigationMaps);
    private RouteMap? _wheelchairMap;
    private string? _wheelchairEditingPath;
    private string _wheelchairName = "";
    private string _wheelchairStatus = "先点“新建方案”，再逐个记录坐标并给每一步选择动作。";
    private SavedWheelchairPlan? _selectedWheelchairPlan;
    private bool _isWheelchairDrafting;
    private bool _capturingWheelchairStep;

    public ObservableCollection<WheelchairStepDraft> WheelchairSteps { get; } = [];
    public ObservableCollection<SavedWheelchairPlan> SavedWheelchairPlans { get; } = [];
    public ObservableCollection<string> ActionChoices { get; } = [];

    public bool IsWheelchairDrafting
    {
        get => _isWheelchairDrafting;
        private set
        {
            if (_isWheelchairDrafting == value) return;
            _isWheelchairDrafting = value;
            Notify();
            RefreshWheelchairState();
        }
    }

    public string WheelchairStatus { get => _wheelchairStatus; private set { _wheelchairStatus = value; Notify(); } }
    public string WheelchairName { get => _wheelchairName; set { _wheelchairName = value; Notify(); Notify(nameof(CanSaveWheelchairPlan)); } }

    public SavedWheelchairPlan? SelectedWheelchairPlan
    {
        get => _selectedWheelchairPlan;
        set
        {
            _selectedWheelchairPlan = value;
            Notify();
            Notify(nameof(SelectedWheelchairPlanSummary));
            Notify(nameof(CanRunWheelchairPlan));
        }
    }

    public string SelectedWheelchairPlanSummary => SelectedWheelchairPlan is { } selected
        ? $"{selected.Summary} · 地图 {MapName(selected.Plan.Map.Id)}"
        : "尚未选择方案";

    public bool CanBeginWheelchair => CanConfigure && !IsWheelchairDrafting;
    public bool CanCaptureWheelchairStep => CanConfigure && IsWheelchairDrafting && !_capturingWheelchairStep;
    public bool CanSaveWheelchairPlan => IsWheelchairDrafting && WheelchairSteps.Count > 0 && !string.IsNullOrWhiteSpace(WheelchairName);
    public bool CanRunWheelchairPlan => CanConfigure && !IsWheelchairDrafting && SelectedWheelchairPlan is not null;

    public void LoadWheelchairPlans()
    {
        if (IsWheelchairDrafting) return;
        var selected = SelectedWheelchairPlan?.Path;
        SavedWheelchairPlans.Clear();
        try { foreach (var plan in WheelchairPlans.List()) SavedWheelchairPlans.Add(plan); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetWheelchairStatus("无法读取方案目录：" + ex.Message); return; }
        SelectedWheelchairPlan = SavedWheelchairPlans.FirstOrDefault(plan => plan.Path == selected) ?? SavedWheelchairPlans.FirstOrDefault();
        RefreshActionChoices();
    }

    public void BeginWheelchairDraft()
    {
        if (!CanBeginWheelchair) return;
        WheelchairSteps.Clear();
        _wheelchairMap = null;
        _wheelchairEditingPath = null;
        WheelchairName = "";
        IsWheelchairDrafting = true;
        SetWheelchairStatus("新建方案：让角色站在第一个点，点“记录当前坐标”，再给该步选择动作。");
    }

    public void CancelWheelchairDraft()
    {
        if (!IsWheelchairDrafting) return;
        IsWheelchairDrafting = false;
        WheelchairSteps.Clear();
        _wheelchairMap = null;
        SetWheelchairStatus("已放弃当前草稿。");
    }

    public async Task CaptureWheelchairStepAsync()
    {
        if (!CanCaptureWheelchairStep || automation is null) return;
        _capturingWheelchairStep = true;
        RefreshWheelchairState();
        SetWheelchairStatus("正在读取当前坐标…");
        try
        {
            var position = await automation.CaptureRoutePointAsync();
            if (position is null) { SetWheelchairStatus("未取得有效坐标，请确认角色在世界中并查看日志。"); return; }
            var map = new RouteMap(position.MapId, position.Floor, position.CoordinateVersion);
            if (_wheelchairMap is not null && map != _wheelchairMap)
            { SetWheelchairStatus("角色已切换地图：一个方案只能包含同一张地图的坐标。"); return; }
            _wheelchairMap = map;
            WheelchairSteps.Add(new WheelchairStepDraft($"步骤 {WheelchairSteps.Count + 1}", position.X, position.Y));
            SetWheelchairStatus($"已记录第 {WheelchairSteps.Count} 个坐标，选择动作（或留空只走位）。");
        }
        finally
        {
            _capturingWheelchairStep = false;
            RefreshWheelchairState();
        }
    }

    public void RemoveWheelchairStep(WheelchairStepDraft? draft)
    {
        if (draft is null || !IsWheelchairDrafting) return;
        WheelchairSteps.Remove(draft);
        RefreshWheelchairState();
        SetWheelchairStatus($"已移除一步，当前 {WheelchairSteps.Count} 步。");
    }

    public void SaveWheelchairPlan()
    {
        if (!CanSaveWheelchairPlan || _wheelchairMap is null) return;
        var steps = new List<WheelchairStep>();
        foreach (var draft in WheelchairSteps)
        {
            if (!int.TryParse(draft.DelayText, out var delay) || delay is < 0 or > 600)
            { SetWheelchairStatus($"步骤“{draft.Name}”的等待秒数无效（0–600）。"); return; }
            steps.Add(new WheelchairStep(draft.Name.Trim(), draft.X, draft.Y,
                string.IsNullOrWhiteSpace(draft.Action) ? null : draft.Action.Trim(), delay * 1000));
        }
        var plan = new WheelchairPlan(WheelchairPlan.CurrentSchema, WheelchairName.Trim(), _wheelchairMap, steps);
        try
        {
            // Editing an existing plan overwrites that file; a new draft gets a fresh one.
            var path = _wheelchairEditingPath is { } editing ? Overwrite(plan, editing) : WheelchairPlans.Save(plan);
            IsWheelchairDrafting = false;
            LoadWheelchairPlans();
            SelectedWheelchairPlan = SavedWheelchairPlans.FirstOrDefault(saved => saved.Path == path) ?? SelectedWheelchairPlan;
            SetWheelchairStatus($"已保存「{plan.Name}」：{plan.Summary}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { SetWheelchairStatus("保存失败：" + ex.Message); }
    }

    /// <summary>Loads a saved plan into the draft editor so its steps, actions and delays can be changed.</summary>
    public void EditSelectedWheelchairPlan()
    {
        if (SelectedWheelchairPlan is not { } selected || IsWheelchairDrafting) return;
        WheelchairSteps.Clear();
        _wheelchairMap = selected.Plan.Map;
        _wheelchairEditingPath = selected.Path;
        WheelchairName = selected.Plan.Name;
        foreach (var step in selected.Plan.Steps)
        {
            WheelchairSteps.Add(new WheelchairStepDraft(step.Name, step.X, step.Y)
            {
                Action = step.Action ?? "",
                DelayText = (step.DelayMs / 1000).ToString(),
            });
        }
        IsWheelchairDrafting = true;
        SetWheelchairStatus($"正在编辑「{selected.Plan.Name}」：改完点“保存方案”会覆盖原文件。");
    }

    private string Overwrite(WheelchairPlan plan, string path)
    {
        WheelchairPlans.Export(plan, path);
        return path;
    }

    public void RunWheelchairPlan()
    {
        if (!CanRunWheelchairPlan || automation is null || SelectedWheelchairPlan is null) return;
        var actions = SavedActions.GroupBy(action => action.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Recording, StringComparer.Ordinal);
        var missing = SelectedWheelchairPlan.Plan.ActionNames.Where(name => !actions.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
        { SetWheelchairStatus("缺少动作：" + string.Join("、", missing) + "。请先在“动作录制”页导入这些动作。"); return; }
        if (automation.StartWheelchair(SelectedWheelchairPlan.Plan, actions))
            SetWheelchairStatus($"开始运行「{SelectedWheelchairPlan.Name}」，按 F10 停止。");
        else SetWheelchairStatus("无法开始运行，请查看日志。");
    }

    public void DeleteSelectedWheelchairPlan()
    {
        if (SelectedWheelchairPlan is not { } selected) return;
        try
        {
            WheelchairPlans.Delete(selected.Path);
            SetWheelchairStatus($"已删除「{selected.Name}」。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetWheelchairStatus("删除失败：" + ex.Message); }
        LoadWheelchairPlans();
    }

    public void ExportWheelchairPlan(string path)
    {
        if (SelectedWheelchairPlan is not { } selected) return;
        try
        {
            WheelchairPlans.Export(selected.Plan, path);
            SetWheelchairStatus($"已导出到 {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { SetWheelchairStatus("导出失败：" + ex.Message); }
    }

    public void ImportWheelchairPlan(string path)
    {
        try
        {
            var stored = WheelchairPlans.Import(path);
            LoadWheelchairPlans();
            SelectedWheelchairPlan = SavedWheelchairPlans.FirstOrDefault(saved => saved.Path == stored) ?? SelectedWheelchairPlan;
            SetWheelchairStatus($"已导入方案「{SelectedWheelchairPlan?.Name}」。若提示缺少动作，请先导入对应动作文件。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        { SetWheelchairStatus("导入失败：" + ex.Message); }
    }

    private void RefreshActionChoices()
    {
        ActionChoices.Clear();
        ActionChoices.Add("");
        foreach (var name in SavedActions.Select(action => action.Name).Distinct(StringComparer.Ordinal).OrderBy(name => name))
            ActionChoices.Add(name);
    }

    private void RefreshWheelchairState()
    {
        Notify(nameof(CanBeginWheelchair));
        Notify(nameof(CanCaptureWheelchairStep));
        Notify(nameof(CanSaveWheelchairPlan));
        Notify(nameof(CanRunWheelchairPlan));
    }

    private void SetWheelchairStatus(string value) => WheelchairStatus = value;
}
