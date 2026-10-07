using System.Collections.ObjectModel;
using System.IO;
using AzurAssistant.Features.Routes;
using AzurAssistant.Game.Navigation;

namespace AzurAssistant.UI;

public sealed record SavedRoute(string Path, RouteDefinition Route)
{
    public string Name => Route.Name;
}
public sealed class RoutePointDraft(string name, double x, double y)
{
    public string Name { get; set; } = name;
    public double X { get; } = x;
    public double Y { get; } = y;
}

public sealed partial class MainViewModel
{
    private RouteStore? _routeStore;
    private RouteStore Routes => _routeStore ??= new(routesDirectory ?? Path.Combine(AppContext.BaseDirectory, "routes"), NavigationMaps);
    private RouteMap? _recordingMap;
    private bool _capturingPoint;
    private string _routeName = "";
    private bool _routeAutoPickup;
    private SavedRoute? _selectedRoute;
    public ObservableCollection<SavedRoute> SavedRoutes { get; } = [];
    public ObservableCollection<RoutePointDraft> RecordedPoints { get; } = [];
    public bool IsRecordingRoute { get; private set; }
    public string RouteMessage { get; private set; } = "选择路线运行，或逐点录制一条同地图路线。";
    public string RouteName { get => _routeName; set { _routeName = value; Notify(); Notify(nameof(CanSaveRoute)); } }
    public bool RouteAutoPickup { get => _routeAutoPickup; set { _routeAutoPickup = value; Notify(); } }
    public string RecordingMapText => _recordingMap is { } map ? $"所属地图：{MapName(map.Id)} · {map.Floor} 层" : "记录首个点位后确定所属地图";
    public SavedRoute? SelectedRoute { get => _selectedRoute; set { _selectedRoute = value; Notify(); Notify(nameof(SelectedRouteDescription)); Notify(nameof(SelectedRoutePoints)); Notify(nameof(CanRunRoute)); } }
    public string SelectedRouteDescription => SelectedRoute is { } selected ? $"{MapName(selected.Route.Map.Id)} · {selected.Route.Points.Count} 个点位 · 自动拾取{(selected.Route.AutoPickup ? "开启" : "关闭")}" : "尚未选择路线";
    public IReadOnlyList<RoutePoint> SelectedRoutePoints => SelectedRoute?.Route.Points ?? [];
    public bool CanBeginRoute => CanConfigure && !IsRecordingRoute && !_capturingPoint;
    public bool CanCaptureRoutePoint => CanConfigure && IsRecordingRoute && !_capturingPoint;
    public bool CanSaveRoute => CanCaptureRoutePoint && RecordedPoints.Count > 0 && !string.IsNullOrWhiteSpace(RouteName);
    public bool CanRunRoute => CanBeginRoute && SelectedRoute is not null;

    public void LoadRoutes()
    {
        if (IsRecordingRoute || _capturingPoint) return;
        var selected = SelectedRoute?.Path;
        SavedRoutes.Clear();
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
        if (invalid > 0) SetRouteMessage($"有 {invalid} 个无效路线文件，已跳过并保留原文件，详见日志。");
    }
    public void BeginRouteRecording()
    {
        if (!CanBeginRoute) return;
        RecordedPoints.Clear(); _recordingMap = null; RouteName = ""; RouteAutoPickup = false;
        IsRecordingRoute = true; Notify(nameof(IsRecordingRoute)); Notify(nameof(RecordingMapText));
        SetRouteMessage("移动角色到点位后点击“记录当前点”，再编辑该点名称；完成后命名并保存整条路线。"); RefreshRouteState();
    }
    public async Task CaptureRoutePointAsync()
    {
        if (!CanCaptureRoutePoint || automation is null) return;
        _capturingPoint = true; RefreshRouteState(); SetRouteMessage("正在读取当前角色坐标…");
        try
        {
            var position = await automation.CaptureRoutePointAsync();
            if (position is null) { SetRouteMessage("未获得有效点位，请查看运行日志；没有保存旧坐标。"); return; }
            var map = new RouteMap(position.MapId, position.Floor, position.CoordinateVersion);
            if (_recordingMap is not null && map != _recordingMap)
            { SetRouteMessage("角色已切换地图，本条路线只支持同地图，未添加该点位。"); return; }
            _recordingMap = map;
            RecordedPoints.Add(new("点位 " + (RecordedPoints.Count + 1), Math.Round(position.X, 2), Math.Round(position.Y, 2)));
            Notify(nameof(RecordingMapText)); SetRouteMessage("已记录当前点位，请编辑名称，然后移动到下一个点。");
        }
        finally { _capturingPoint = false; RefreshRouteState(); Refresh(); }
    }
    public void RemoveRecordedPoint(RoutePointDraft point)
    {
        if (!CanCaptureRoutePoint) return;
        RecordedPoints.Remove(point); if (RecordedPoints.Count == 0) _recordingMap = null;
        Notify(nameof(RecordingMapText)); RefreshRouteState();
    }
    public void SaveRecordedRoute()
    {
        if (!CanSaveRoute || _recordingMap is null) return;
        try
        {
            var route = new RouteDefinition(RouteDefinition.CurrentSchema, RouteName.Trim(), _recordingMap, RouteAutoPickup,
                RecordedPoints.Select(p => new RoutePoint(p.Name.Trim(), p.X, p.Y)).ToArray());
            var path = Routes.Save(route); IsRecordingRoute = false; Notify(nameof(IsRecordingRoute));
            LoadRoutes(); SelectedRoute = SavedRoutes.Single(r => r.Path == path);
            SetRouteMessage("路线已保存为 JSON，可导出分享或直接运行。"); RefreshRouteState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { SetRouteMessage("保存失败：" + ex.Message); }
    }
    public void CancelRouteRecording()
    {
        if (_capturingPoint) return;
        IsRecordingRoute = false; RecordedPoints.Clear(); _recordingMap = null;
        Notify(nameof(IsRecordingRoute)); Notify(nameof(RecordingMapText)); RefreshRouteState(); SetRouteMessage("已取消本次录制。");
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
    public void RunSelectedRoute()
    {
        if (!CanRunRoute || SelectedRoute is not { } selected) return;
        if (automation?.StartRoute(selected.Route) == true) SetRouteMessage("正在运行“" + selected.Name + "”，可随时暂停或停止。");
        Refresh();
    }
    private void SetRouteMessage(string value) { RouteMessage = value; Notify(nameof(RouteMessage)); }
    private void RefreshRouteState()
    { Notify(nameof(CanBeginRoute)); Notify(nameof(CanCaptureRoutePoint)); Notify(nameof(CanSaveRoute)); Notify(nameof(CanRunRoute)); }
}
