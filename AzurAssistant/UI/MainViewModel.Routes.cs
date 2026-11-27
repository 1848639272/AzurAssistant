using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using AzurAssistant.Features.Routes;
using AzurAssistant.Runtime;
using AzurAssistant.Game.Navigation;

namespace AzurAssistant.UI;

public sealed record SavedRoute(string Path, RouteDefinition Route)
{
    public string Name => Route.Name;
}
public sealed class RoutePointDraft(RoutePoint point, string mapName, string? destinationName, int number) : INotifyPropertyChanged
{
    public RoutePoint Point { get; } = point;
    public string Name { get; set; } = point.Name;
    public int Number { get; private set; } = number;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void SetNumber(int value)
    {
        if (Number == value) return;
        Number = value;
        PropertyChanged?.Invoke(this, new(nameof(Number)));
    }
    public string Location => Point.TeleportId is not null
        ? Point.Landing is { } landing ? $"{destinationName}落点 ({landing.X:F1}, {landing.Y:F1})" : $"{mapName} · 传送"
        : $"{mapName} · X {Point.X:F1}   Y {Point.Y:F1}" + (Point.FacingDegrees is { } angle ? $"   {angle:F1}°" : "");
}

public sealed class DailyRouteRow(MainViewModel owner, DailyRouteChoice choice, int index)
{
    public int Index { get; } = index;
    public string Label => $"{Index + 1}. {owner.SavedRoutes.FirstOrDefault(r => string.Equals(Path.GetFileName(r.Path), choice.File, StringComparison.OrdinalIgnoreCase))?.Name ?? choice.Name}";
    public bool AutoPickup { get => choice.AutoPickup; set => owner.SetDailyRoutePickup(Index, value); }
}

public sealed partial class MainViewModel
{
    private RouteStore? _routeStore;
    private RouteStore Routes => _routeStore ??= new(routesDirectory ?? Path.Combine(AppContext.BaseDirectory, "routes"), NavigationMaps);
    private bool _capturingPoint;
    private SavedRoute? _editing;
    public bool IsRouteDraftOpen => IsRecordingRoute || _editing is not null;
    public bool CanCancelDraft => IsRouteDraftOpen && !_capturingPoint;
    public bool CanChangeDraft => IsRouteDraftOpen && !_capturingPoint && !IsPaused && (CanConfigure || automation?.IsRecording == true);
    public bool CanRemovePoint => IsRouteDraftOpen ? CanChangeDraft : CanRunRoute;
    private string _routeName = "";
    private bool _routeAutoPickup;
    private SavedRoute? _selectedRoute;
    public ObservableCollection<SavedRoute> SavedRoutes { get; } = [];
    public ObservableCollection<RoutePointDraft> RecordedPoints { get; } = [];
    public bool IsRecordingRoute { get; private set; }
    public string RouteMessage { get; private set; } = "选择路线运行，或开始录制。";
    public string RouteName { get => _routeName; set { _routeName = value; Notify(); Notify(nameof(CanSaveRoute)); } }
    public bool RouteAutoPickup { get => _routeAutoPickup; set { _routeAutoPickup = value; Notify(); } }
    public SavedRoute? SelectedRoute
    {
        get => _selectedRoute;
        set
        {
            if (_selectedRoute == value) return;
            _selectedRoute = value;
            SelectedRoutePoints = value is null ? [] : Routes.ForSaving(value.Route).Points
                .Select((point, i) => MakeDraft(point, value.Route.Map, i + 1)).ToArray();
            Notify(); Notify(nameof(VisibleRoutePoints)); Notify(nameof(CanRemovePoint));
            Notify(nameof(SelectedRoutePoints)); Notify(nameof(CanRunRoute));
        }
    }
    public IReadOnlyList<RoutePointDraft> SelectedRoutePoints { get; private set; } = [];
    public IEnumerable<RoutePointDraft> VisibleRoutePoints => IsRouteDraftOpen ? RecordedPoints : SelectedRoutePoints;
    private RoutePointDraft MakeDraft(RoutePoint point, RouteMap owner, int number) =>
        new(point, MapName((point.Map ?? owner).Id), point.Landing is { } l ? MapName(l.Map.Id) : null, number);
    public bool CanBeginRoute => CanConfigure && !IsRouteDraftOpen && !_capturingPoint;
    public bool CanCaptureRoutePoint => CanChangeDraft;
    public bool CanRecordTeleport => CanCaptureRoutePoint && _editing?.Route.SchemaVersion != 1;
    public bool CanSaveRoute => CanChangeDraft && RecordedPoints.Count > 0 && !string.IsNullOrWhiteSpace(RouteName);
    public bool CanRunRoute => CanBeginRoute && SelectedRoute is not null;

    public bool CustomRoutes { get => Settings.CustomRoutes; set => SaveSettings(Settings with { CustomRoutes = value }); }
    public string DailyRouteHint => SavedRoutes.Count == 0 ? "先在路线管理录制或导入路线。" : Settings.DailyRoutes.Count == 0
        ? "选择已保存的路线后点击添加，可重复添加同一路线。" : "按列表顺序执行。开启拾取辅助后，仅在到点时采集、挖矿和砍树。";
    public IReadOnlyList<DailyRouteRow> DailyRouteRows => Settings.DailyRoutes.Select((entry, i) => new DailyRouteRow(this, entry, i)).ToArray();
    public void AddDailyRoute()
    {
        if (!CanConfigure || SelectedRoute is not { } selected) return;
        SaveSettings(Settings with { DailyRoutes = new([.. Settings.DailyRoutes, new DailyRouteChoice(Path.GetFileName(selected.Path), selected.Name, false)]) });
    }
    public void SetDailyRoutePickup(int index, bool enabled)
    {
        if (!CanConfigure || index < 0 || index >= Settings.DailyRoutes.Count || Settings.DailyRoutes[index].AutoPickup == enabled) return;
        var choices = Settings.DailyRoutes.ToArray();
        choices[index] = choices[index] with { AutoPickup = enabled };
        SaveSettings(Settings with { DailyRoutes = new(choices) });
    }
    public void RemoveDailyRoute(int index)
    {
        if (CanConfigure) SaveSettings(Settings with { DailyRoutes = new(Settings.DailyRoutes.Where((_, i) => i != index)) });
    }
    public void MoveDailyRoute(int index, int offset)
    {
        var choices = Settings.DailyRoutes.ToList();
        var to = index + offset;
        if (!CanConfigure || index < 0 || index >= choices.Count || to < 0 || to >= choices.Count) return;
        (choices[index], choices[to]) = (choices[to], choices[index]);
        SaveSettings(Settings with { DailyRoutes = new(choices) });
    }

    public void LoadRoutes()
    {
        if (IsRouteDraftOpen || _capturingPoint) return;
        var selected = SelectedRoute?.Path;
        SavedRoutes.Clear();
        Notify(nameof(DailyRouteHint));
        if (!Directory.Exists(Routes.DirectoryPath)) { SelectedRoute = null; return; }
        string[] paths;
        try { paths = Directory.GetFiles(Routes.DirectoryPath, "*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SelectedRoute = null; SetRouteMessage("无法读取路线目录：" + ex.Message); return; }
        var invalid = 0;
        foreach (var path in paths.OrderBy(p => p))
        {
            try { SavedRoutes.Add(new(path, Routes.Read(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException) { invalid++; Report("路线读取失败：" + Path.GetFileName(path) + "；" + ex.Message); }
        }
        SelectedRoute = SavedRoutes.FirstOrDefault(r => r.Path == selected) ?? SavedRoutes.FirstOrDefault();
        Notify(nameof(DailyRouteHint)); Notify(nameof(DailyRouteRows));
        if (invalid > 0) SetRouteMessage($"有 {invalid} 个无效路线文件，已跳过并保留原文件，详见日志。");
    }
    public void BeginRouteRecording()
    {
        if (!CanBeginRoute) return;
        if (automation?.StartRecording() != true) { SetRouteMessage("无法开始定位，请检查助手与游戏连接。"); return; }
        RecordedPoints.Clear(); RouteName = "";
        IsRecordingRoute = true; DraftChanged();
        SetRouteMessage("移动角色到点位后点击“记录当前点”，再编辑该点名称；完成后命名并保存整条路线。"); RefreshRouteState();
    }
    public void EditSelectedRoute()
    {
        if (!CanRunRoute || SelectedRoute is not { } selected) return;
        _editing = selected;
        RecordedPoints.Clear();
        foreach (var row in SelectedRoutePoints)
            RecordedPoints.Add(MakeDraft(row.Point, selected.Route.Map, row.Number));
        RouteName = selected.Name;
        DraftChanged();
        SetRouteMessage(selected.Route.SchemaVersion == 1 ? "旧版单地图路线支持追加同图步行点，保存时保留原格式。" : "编辑完成后保存，取消可还原此次修改。");
    }
    public void OpenRouteDirectory()
    {
        try
        {
            Directory.CreateDirectory(Routes.DirectoryPath);
            Process.Start(new ProcessStartInfo(Routes.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        { SetRouteMessage("无法打开路线文件夹：" + ex.Message); }
    }
    public async Task CaptureRoutePointAsync(bool teleport = false)
    {
        if (!CanCaptureRoutePoint || (teleport && !CanRecordTeleport)) return;
        if (automation?.StartRecording() != true) { SetRouteMessage("无法开始定位，请检查助手与游戏连接。"); return; }
        _capturingPoint = true; RefreshRouteState();
        try
        {
            SetRouteMessage(teleport ? "正在前往所选传送点…" : "正在确认地图、坐标与朝向…");
            var result = await automation.RecordPointAsync(teleport ? SelectedTeleportMap?.Id : null, teleport ? SelectedTeleport?.Id : null);
            if (result.Point is { Map: { } map } point)
            {
                if (point.TeleportId is null) point = point with { Name = "点位 " + (RecordedPoints.Count + 1) };
                if (_editing?.Route is { SchemaVersion: 1 } legacy)
                {
                    if (map != legacy.Map) { SetRouteMessage("旧版单地图路线只能追加起始地图中的点位。"); return; }
                    point = point with { Map = null, FacingDegrees = null };
                }
                RecordedPoints.Add(MakeDraft(point, map, RecordedPoints.Count + 1));
            }
            SetRouteMessage(result.Message);
        }
        finally { _capturingPoint = false; RefreshRouteState(); Refresh(); }
    }
    public void RemoveRecordedPoint(RoutePointDraft point)
    {
        if (!IsRouteDraftOpen && CanRunRoute && SelectedRoutePoints.Contains(point))
        {
            var index = point.Number - 1;
            EditSelectedRoute();
            point = RecordedPoints[index];
        }
        if (!CanChangeDraft || !RecordedPoints.Remove(point)) return;
        for (var i = point.Number - 1; i < RecordedPoints.Count; i++) RecordedPoints[i].SetNumber(i + 1);
        RefreshRouteState();
    }
    public void SaveRecordedRoute()
    {
        if (!CanSaveRoute) return;
        try
        {
            var original = _editing?.Route;
            var route = new RouteDefinition(original?.SchemaVersion == 1 ? 1 : RouteDefinition.CurrentSchema, RouteName.Trim(),
                RecordedPoints[0].Point.Map ?? original!.Map, original?.AutoPickup,
                RecordedPoints.Select(p => p.Point with { Name = p.Name.Trim() }).ToArray());
            string path;
            if (_editing is { } editing) { path = editing.Path; Routes.Update(path, editing.Route, route); }
            else path = Routes.Save(route);
            EndDraft();
            SelectWrittenRoute(path, "路线已保存，可导出分享或直接运行。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException) { SetRouteMessage("保存失败：" + ex.Message); }
    }
    public async Task CancelRouteRecordingAsync()
    {
        if (!CanCancelDraft) return;
        _capturingPoint = true;
        RefreshRouteState();
        try
        {
            if (automation is not null) await automation.CancelRecordingAsync();
            EndDraft();
        }
        finally { _capturingPoint = false; RefreshRouteState(); }
        LoadRoutes(); SetRouteMessage("已取消本次修改。");
    }
    private void EndDraft()
    {
        if (automation?.IsRecording == true) automation.FinishRecording();
        IsRecordingRoute = false; _editing = null; RecordedPoints.Clear(); DraftChanged();
    }
    private void DraftChanged()
    {
        Notify(nameof(IsRecordingRoute)); Notify(nameof(IsRouteDraftOpen)); Notify(nameof(VisibleRoutePoints)); RefreshRouteState();
    }
    public void ImportRoute(string path)
    {
        if (!CanBeginRoute) return;
        try
        {
            var route = Routes.Read(path);
            SelectWrittenRoute(Routes.Save(route), "已导入“" + route.Name + "”，点击运行路线开始。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException) { SetRouteMessage("导入失败：" + ex.Message); }
    }
    private void SelectWrittenRoute(string path, string message)
    {
        LoadRoutes();
        SelectedRoute = SavedRoutes.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        SetRouteMessage(SelectedRoute is null ? "文件已写入，但暂未能重新载入，请检查路线目录。" : message);
    }
    public void ExportSelectedRoute(string path)
    {
        if (SelectedRoute is not { } selected) return;
        try { Routes.Export(selected.Route, path); SetRouteMessage("路线已导出：" + path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { SetRouteMessage("导出失败：" + ex.Message); }
    }
    public void DeleteSelectedRoute(Func<SavedRoute, bool> confirm)
    {
        if (!CanRunRoute || SelectedRoute is not { } selected || !confirm(selected)) return;
        if (!CanRunRoute || SelectedRoute != selected) return;

        var original = Settings;
        var fileName = Path.GetFileName(selected.Path);
        var remaining = new DailyRouteList(original.DailyRoutes.Where(entry =>
            !string.Equals(entry.File, fileName, StringComparison.OrdinalIgnoreCase)));
        var hasReferences = remaining.Count != original.DailyRoutes.Count;
        if (hasReferences && !SaveSettings(original with { DailyRoutes = remaining, CustomRoutes = original.CustomRoutes && remaining.Count > 0 }))
        {
            SetRouteMessage("无法保存一条龙路线列表，已保留路线文件。");
            return;
        }
        try { Routes.Delete(selected.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            var restored = !hasReferences || SaveSettings(original);
            SetRouteMessage($"删除路线失败：{ex.Message}" + (restored ? "" : " 请检查一条龙路线列表，原配置未能恢复。"));
            return;
        }
        LoadRoutes();
        SetRouteMessage($"已删除路线“{selected.Name}”。");
    }
    public void RunSelectedRoute()
    {
        if (!CanRunRoute || SelectedRoute is not { } selected) return;
        if (automation?.StartRoute(selected.Route, RouteAutoPickup) == true) SetRouteMessage("正在运行“" + selected.Name + "”，可随时暂停或停止。");
        Refresh();
    }
    private void SetRouteMessage(string value) { RouteMessage = value; Notify(nameof(RouteMessage)); }
    private void RefreshRouteState()
    { Notify(nameof(CanBeginRoute)); Notify(nameof(CanRemovePoint)); Notify(nameof(CanChangeDraft)); Notify(nameof(CanCancelDraft)); Notify(nameof(CanCaptureRoutePoint)); Notify(nameof(CanRecordTeleport)); Notify(nameof(CanSaveRoute)); Notify(nameof(CanRunRoute)); }
}
