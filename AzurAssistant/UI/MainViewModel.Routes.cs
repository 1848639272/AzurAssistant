using System.Collections.ObjectModel;
using System.IO;
using AzurAssistant.Features.Routes;
using AzurAssistant.Runtime;
using AzurAssistant.Game.Navigation;

namespace AzurAssistant.UI;

public sealed record SavedRoute(string Path, RouteDefinition Route)
{
    public string Name => Route.Name;
}
public sealed class RoutePointDraft(RoutePoint point, string mapName)
{
    public RoutePoint Point { get; } = point;
    public string Name { get; set; } = point.Name;
    public string Location => $"{mapName} · {(Point.TeleportId is null ? "坐标" : "传送")}   X {Point.X:F1}   Y {Point.Y:F1}"
        + (Point.FacingDegrees is { } angle ? $"   {angle:F1}°" : "");
}

public sealed class DailyRouteRow(MainViewModel owner, DailyRouteChoice choice, int index)
{
    public int Index { get; } = index;
    public string Label => $"{Index + 1}. {choice.Name}";
    public bool AutoPickup { get => choice.AutoPickup; set => owner.SetDailyRoutePickup(Index, value); }
}

public sealed partial class MainViewModel
{
    private RouteStore? _routeStore;
    private RouteStore Routes => _routeStore ??= new(routesDirectory ?? Path.Combine(AppContext.BaseDirectory, "routes"), NavigationMaps);
    private bool _capturingPoint;
    private string _routeName = "";
    private bool _routeAutoPickup;
    private SavedRoute? _selectedRoute;
    public ObservableCollection<SavedRoute> SavedRoutes { get; } = [];
    public ObservableCollection<RoutePointDraft> RecordedPoints { get; } = [];
    public bool IsRecordingRoute { get; private set; }
    public string RouteMessage { get; private set; } = "选择路线运行，或开始录制。";
    public string RouteName { get => _routeName; set { _routeName = value; Notify(); Notify(nameof(CanSaveRoute)); } }
    public bool RouteAutoPickup { get => _routeAutoPickup; set { _routeAutoPickup = value; Notify(); } }
    public SavedRoute? SelectedRoute { get => _selectedRoute; set { _selectedRoute = value; Notify(); Notify(nameof(SelectedRouteDescription)); Notify(nameof(SelectedRoutePoints)); Notify(nameof(CanRunRoute)); } }
    public string SelectedRouteDescription => SelectedRoute is { } selected ? $"{MapName(selected.Route.Map.Id)} 起始 · {selected.Route.Points.Count} 个点位" : "尚未选择路线";
    public IReadOnlyList<RoutePointDraft> SelectedRoutePoints => SelectedRoute is { Route: var route }
        ? route.Points.Select(p => new RoutePointDraft(p, MapName((p.Map ?? route.Map).Id))).ToArray() : [];
    public bool CanBeginRoute => CanConfigure && !IsRecordingRoute && !_capturingPoint;
    public bool CanCaptureRoutePoint => IsRecordingRoute && !_capturingPoint && !IsPaused && (CanConfigure || automation?.IsRecording == true);
    public bool CanSaveRoute => CanCaptureRoutePoint && RecordedPoints.Count > 0 && !string.IsNullOrWhiteSpace(RouteName);
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
        if (IsRecordingRoute || _capturingPoint) return;
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
        Notify(nameof(DailyRouteHint));
        if (invalid > 0) SetRouteMessage($"有 {invalid} 个无效路线文件，已跳过并保留原文件，详见日志。");
    }
    public void BeginRouteRecording()
    {
        if (!CanBeginRoute) return;
        if (automation?.StartRecording() != true) { SetRouteMessage("无法开始定位，请检查助手与游戏连接。"); return; }
        RecordedPoints.Clear(); RouteName = "";
        IsRecordingRoute = true; Notify(nameof(IsRecordingRoute));
        SetRouteMessage("移动角色到点位后点击“记录当前点”，再编辑该点名称；完成后命名并保存整条路线。"); RefreshRouteState();
    }
    public async Task CaptureRoutePointAsync(bool teleport = false)
    {
        if (!CanCaptureRoutePoint || automation is null || !automation.StartRecording()) return;
        _capturingPoint = true; RefreshRouteState();
        try
        {
            SetRouteMessage(teleport ? "正在前往所选传送点…" : "正在确认地图、坐标与朝向…");
            var result = await automation.RecordPointAsync(teleport ? SelectedTeleportMap?.Id : null, teleport ? SelectedTeleport?.Id : null);
            if (result.Point is { Map: { } map } point)
            {
                if (point.TeleportId is null) point = point with { Name = "点位 " + (RecordedPoints.Count + 1) };
                RecordedPoints.Add(new(point, MapName(map.Id)));
            }
            SetRouteMessage(result.Message);
        }
        finally { _capturingPoint = false; RefreshRouteState(); Refresh(); }
    }
    public void RemoveRecordedPoint(RoutePointDraft point)
    {
        if (!CanCaptureRoutePoint) return;
        RecordedPoints.Remove(point);
        RefreshRouteState();
    }
    public void SaveRecordedRoute()
    {
        if (!CanSaveRoute) return;
        try
        {
            var route = new RouteDefinition(RouteDefinition.CurrentSchema, RouteName.Trim(), RecordedPoints[0].Point.Map!, null,
                RecordedPoints.Select(p => p.Point with { Name = p.Name.Trim() }).ToArray());
            var path = Routes.Save(route); automation?.FinishRecording(); IsRecordingRoute = false; Notify(nameof(IsRecordingRoute));
            LoadRoutes(); SelectedRoute = SavedRoutes.Single(r => r.Path == path);
            SetRouteMessage("路线已保存为 JSON，可导出分享或直接运行。"); RefreshRouteState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { SetRouteMessage("保存失败：" + ex.Message); }
    }
    public void CancelRouteRecording()
    {
        if (_capturingPoint) return;
        automation?.FinishRecording(); IsRecordingRoute = false; RecordedPoints.Clear();
        Notify(nameof(IsRecordingRoute)); RefreshRouteState(); SetRouteMessage("已取消本次录制。");
    }
    public void ImportRoute(string path)
    {
        if (!CanBeginRoute) return;
        try
        {
            var route = Routes.Read(path); var saved = Routes.Save(route); LoadRoutes(); SelectedRoute = SavedRoutes.Single(r => r.Path == saved);
            SetRouteMessage("已导入“" + route.Name + "”，点击运行路线开始。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException) { SetRouteMessage("导入失败：" + ex.Message); }
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
    { Notify(nameof(CanBeginRoute)); Notify(nameof(CanCaptureRoutePoint)); Notify(nameof(CanSaveRoute)); Notify(nameof(CanRunRoute)); }
}
