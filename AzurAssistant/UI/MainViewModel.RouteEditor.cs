using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using AzurAssistant.Features.Routes;

namespace AzurAssistant.UI;

/// <summary>Editable route point. Coordinates stay as text so a half-typed number never corrupts the route.</summary>
public sealed class RouteEditRow(string name, double x, double y)
{
    public string Name { get; set; } = name;
    public string XText { get; set; } = x.ToString("0.##", CultureInfo.InvariantCulture);
    public string YText { get; set; } = y.ToString("0.##", CultureInfo.InvariantCulture);
    public double X => double.TryParse(XText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
    public double Y => double.TryParse(YText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
}

/// <summary>路线编辑：把已保存的路线加载成可改的坐标表，改完原地保存。</summary>
public sealed partial class MainViewModel
{
    private string? _routeEditPath;

    public ObservableCollection<RouteEditRow> EditableRoutePoints { get; } = [];

    public string RoutePreviewText
    {
        get
        {
            if (EditableRoutePoints.Count == 0) return "尚未加载点位";
            var points = EditableRoutePoints.Select(point => $"{point.Name}({point.XText}, {point.YText})").ToArray();
            var text = string.Join(" → ", points);
            return text.Length <= 220 ? text : text[..220] + " …";
        }
    }

    public bool CanEditRoute => SelectedRoute is not null && EditableRoutePoints.Count > 0 && !IsRecordingRoute;

    /// <summary>Loads the selected route into the editor; called when the selection changes.</summary>
    public void LoadRouteForEdit()
    {
        EditableRoutePoints.Clear();
        var selected = SelectedRoute;
        _routeEditPath = selected?.Path;
        if (selected is not null)
            foreach (var point in selected.Route.Points) EditableRoutePoints.Add(new RouteEditRow(point.Name, point.X, point.Y));
        Notify(nameof(RoutePreviewText));
        Notify(nameof(CanEditRoute));
        SetRouteMessage(selected is null
            ? "尚未选择路线。"
            : $"正在预览「{selected.Route.Name}」：共 {selected.Route.Points.Count} 个点位，可在下方直接改名称和坐标。");
    }
    public void RemoveEditedRoutePoint(RouteEditRow? row)
    {
        if (row is null) return;
        EditableRoutePoints.Remove(row);
        Notify(nameof(RoutePreviewText));
        Notify(nameof(CanEditRoute));
    }

    public async Task AddEditedRoutePointAsync()
    {
        if (automation is null || SelectedRoute is null) return;
        SetRouteMessage("正在读取当前坐标，读取后会追加到路线末尾…");
        var position = await automation.CaptureRoutePointAsync();
        if (position is null) { SetRouteMessage("未取得坐标，未添加点位。"); return; }
        EditableRoutePoints.Add(new RouteEditRow($"点位 {EditableRoutePoints.Count + 1}", position.X, position.Y));
        Notify(nameof(RoutePreviewText));
        Notify(nameof(CanEditRoute));
        SetRouteMessage($"已追加第 {EditableRoutePoints.Count} 个点位，记得点“保存修改”。");
    }

    public void SaveEditedRoute()
    {
        if (SelectedRoute is not { } selected || _routeEditPath is null || EditableRoutePoints.Count == 0) return;
        var points = new List<RoutePoint>();
        foreach (var row in EditableRoutePoints)
        {
            if (string.IsNullOrWhiteSpace(row.Name)) { SetRouteMessage("点位名称不能为空。"); return; }
            if (double.IsNaN(row.X) || double.IsNaN(row.Y)) { SetRouteMessage($"点位“{row.Name}”的坐标不是有效数字。"); return; }
            points.Add(new RoutePoint(row.Name.Trim(), row.X, row.Y));
        }
        var updated = selected.Route with { Points = points };
        try
        {
            Routes.Export(updated, _routeEditPath);
            LoadRoutes();
            SelectedRoute = SavedRoutes.FirstOrDefault(route => route.Path == _routeEditPath) ?? SelectedRoute;
            SetRouteMessage($"已保存「{updated.Name}」：{points.Count} 个点位。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { SetRouteMessage("保存失败：" + ex.Message); }
    }
}
