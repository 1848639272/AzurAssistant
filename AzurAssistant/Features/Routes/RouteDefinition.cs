using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzurAssistant.Game.Navigation;

namespace AzurAssistant.Features.Routes;

public sealed record RouteMap([property: JsonRequired] string Id, [property: JsonRequired] int Floor,
    [property: JsonRequired] string CoordinateVersion);
public sealed record RoutePoint([property: JsonRequired] string Name, [property: JsonRequired] double X,
    [property: JsonRequired] double Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouteMap? Map = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? FacingDegrees = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TeleportId = null);
public sealed record RouteDefinition([property: JsonRequired] int SchemaVersion, [property: JsonRequired] string Name,
    [property: JsonRequired] RouteMap Map, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AutoPickup,
    [property: JsonRequired] IReadOnlyList<RoutePoint> Points)
{
    public const int CurrentSchema = 2;
    public NavigationMap Validate(IReadOnlyList<NavigationMap> maps)
    {
        if (SchemaVersion is not (1 or CurrentSchema)) throw new InvalidDataException("不支持的路线文件版本，请使用蔚蓝助手路线 JSON。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Map is null || Points is not { Count: > 0 and <= 2000 })
            throw new InvalidDataException("路线名称、地图或点位数量无效（1–2000点）。");
        if ((SchemaVersion == 1) != AutoPickup.HasValue)
            throw new InvalidDataException("路线文件的拾取设置与格式版本不符。");
        NavigationMap Resolve(RouteMap map) => maps.SingleOrDefault(m => (m.Id, m.Floor, m.CoordinateVersion) ==
            (map.Id, map.Floor, map.CoordinateVersion)) ?? throw new InvalidDataException("路线所属地图、层级或坐标版本不受支持。");
        var first = Resolve(Map);
        var expected = first;
        if (SchemaVersion == CurrentSchema && Points[0]?.Map != Map) throw new InvalidDataException("路线首点地图与起始地图不一致。");
        foreach (var point in Points)
        {
            if (point is null || string.IsNullOrWhiteSpace(point.Name) || point.Name.Length > 80)
                throw new InvalidDataException("每个点位需要1–80字的名称。");
            if (SchemaVersion == 1 && (point.Map is not null || point.FacingDegrees is not null || point.TeleportId is not null))
                throw new InvalidDataException("旧版路线不支持逐点地图、方向或传送。");
            var map = SchemaVersion == 1 ? first : Resolve(point.Map ?? throw new InvalidDataException("点位缺少所属地图。"));
            if (point.TeleportId is { } id)
            {
                var node = map.Teleports.SingleOrDefault(t => t.Id == id) ?? throw new InvalidDataException("找不到传送点：" + point.Name);
                expected = maps.Single(m => m.Id == node.ArrivalMapId);
            }
            else if (map.Id != expected.Id)
                throw new InvalidDataException($"路线存在错误：点位“{point.Name}”位于{map.Name}，前一步所在地图为{expected.Name}，请补入传送或删除该点。");
            if ((SchemaVersion == CurrentSchema && point.TeleportId is null && point.FacingDegrees is null)
                || point.FacingDegrees is { } angle && (!double.IsFinite(angle) || angle < 0 || angle >= 360))
                throw new InvalidDataException("点位朝向无效：" + point.Name);
            try { map.Position(point.X, point.Y); }
            catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("点位“" + point.Name + "”的" + ex.Message, ex); }
        }
        return first;
    }
}

/// <summary>Versioned data files only; importing a route never executes it or overwrites its source.</summary>
public sealed class RouteStore(string directory, IReadOnlyList<NavigationMap> maps)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 12
    };
    public string DirectoryPath => Path.GetFullPath(directory);
    public RouteDefinition Read(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("路线文件超过2MB。");
        var route = JsonSerializer.Deserialize<RouteDefinition>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("路线文件为空。");
        route.Validate(maps);
        return route;
    }
    public string Save(RouteDefinition route)
    {
        route.Validate(maps); Directory.CreateDirectory(directory);
        var name = string.Concat(route.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (name.Length == 0) name = "路线";
        var path = Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, route, Json);
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
    public void Delete(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.TrimEndingDirectorySeparator(DirectoryPath), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("只能删除路线目录中的 JSON 文件。", nameof(path));
        File.Delete(fullPath);
    }
    public void Export(RouteDefinition route, string path)
    {
        route.Validate(maps);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(route, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
