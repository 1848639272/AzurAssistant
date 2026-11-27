using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzurAssistant.Game.Navigation;

namespace AzurAssistant.Features.Routes;

public sealed record RouteMap([property: JsonRequired] string Id, [property: JsonRequired] int Floor,
    [property: JsonRequired] string CoordinateVersion);
public sealed record RouteLanding([property: JsonRequired] RouteMap Map, [property: JsonRequired] double X,
    [property: JsonRequired] double Y);
public sealed record RoutePoint([property: JsonRequired] string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? X,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouteMap? Map = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? FacingDegrees = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TeleportId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouteLanding? Landing = null);
public sealed record RouteDefinition([property: JsonRequired] int SchemaVersion, [property: JsonRequired] string Name,
    [property: JsonRequired] RouteMap Map, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AutoPickup,
    [property: JsonRequired] IReadOnlyList<RoutePoint> Points)
{
    public const int CurrentSchema = 3;
    public NavigationMap Validate(IReadOnlyList<NavigationMap> maps)
    {
        if (SchemaVersion is not (1 or 2 or CurrentSchema)) throw new InvalidDataException("不支持的路线文件版本，请使用蔚蓝助手路线 JSON。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Map is null || Points is not { Count: > 0 and <= 2000 })
            throw new InvalidDataException("路线名称、地图或点位数量无效（1–2000点）。");
        if ((SchemaVersion == 1) != AutoPickup.HasValue)
            throw new InvalidDataException("路线文件的拾取设置与格式版本不符。");
        NavigationMap Resolve(RouteMap map) => maps.SingleOrDefault(m => (m.Id, m.Floor, m.CoordinateVersion) ==
            (map.Id, map.Floor, map.CoordinateVersion)) ?? throw new InvalidDataException("路线所属地图、层级或坐标版本不受支持。");
        var first = Resolve(Map);
        var expected = first;
        if (SchemaVersion >= 2 && Points[0]?.Map != Map) throw new InvalidDataException("路线首点地图与起始地图不一致。");
        foreach (var point in Points)
        {
            if (point is null || string.IsNullOrWhiteSpace(point.Name) || point.Name.Length > 80)
                throw new InvalidDataException("每个点位需要1–80字的名称。");
            if (SchemaVersion == 1 && (point.Map is not null || point.FacingDegrees is not null || point.TeleportId is not null))
                throw new InvalidDataException("旧版路线不支持逐点地图、方向或传送。");
            var map = SchemaVersion == 1 ? first : Resolve(point.Map ?? throw new InvalidDataException("点位缺少所属地图。"));
            if (point.TeleportId is { } id)
            {
                var node = map.Teleports.SingleOrDefault(t => t.Id == id)
                    ?? throw new InvalidDataException($"地图目录中已找不到传送点“{point.Name}”。请在当前版本重新录制这一步。");
                expected = maps.Single(m => m.Id == node.ArrivalMapId);
                if (SchemaVersion == CurrentSchema && node.LandingX.HasValue && node.LandingY.HasValue && point.Landing is null)
                    throw new InvalidDataException("传送步骤缺少目的地落点坐标：" + point.Name);
                if (point.Landing is { } landing)
                {
                    if (landing.Map is null || Resolve(landing.Map) != expected)
                        throw new InvalidDataException("传送步骤的落点地图与目的地不一致：" + point.Name);
                    try { expected.Position(landing.X, landing.Y); }
                    catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("传送落点坐标超出地图范围：" + point.Name, ex); }
                }
            }
            else if (point.Landing is not null) throw new InvalidDataException("只有传送步骤可以附带落点坐标。");
            else if (map.Id != expected.Id)
                throw new InvalidDataException($"路线存在错误：点位“{point.Name}”位于{map.Name}，前一步所在地图为{expected.Name}，请补入传送或删除该点。");
            if ((SchemaVersion >= 2 && point.TeleportId is null && point.FacingDegrees is null)
                || point.FacingDegrees is { } angle && (!double.IsFinite(angle) || angle < 0 || angle >= 360))
                throw new InvalidDataException("点位朝向无效：" + point.Name);
            if (SchemaVersion == CurrentSchema && point.TeleportId is not null)
            {
                if (point.X is not null || point.Y is not null)
                    throw new InvalidDataException("传送步骤只能包含落点坐标：" + point.Name);
                continue;
            }
            if (point.X is not { } x || point.Y is not { } y)
                throw new InvalidDataException("点位缺少坐标：" + point.Name);
            try { map.Position(x, y); }
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
    public RouteDefinition ForSaving(RouteDefinition route)
    {
        route.Validate(maps);
        if (route.SchemaVersion != 2) return route;
        var points = route.Points.Select(point =>
        {
            if (point.TeleportId is null) return point;
            var node = maps.Single(m => m.Id == point.Map!.Id).Teleports.Single(t => t.Id == point.TeleportId);
            var destination = maps.Single(m => m.Id == node.ArrivalMapId);
            var landing = point.Landing;
            if (landing is null && node.LandingX is { } x && node.LandingY is { } y)
                landing = new(new(destination.Id, destination.Floor, destination.CoordinateVersion), x, y);
            return point with { X = null, Y = null, Landing = landing };
        }).ToArray();
        return route with { SchemaVersion = RouteDefinition.CurrentSchema, Points = points };
    }
    public string Save(RouteDefinition route)
    {
        route = ForSaving(route); Directory.CreateDirectory(directory);
        var name = string.Concat(route.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (name.Length == 0) name = "路线";
        var path = Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        WriteAtomic(route, path, WriteMode.Create);
        return path;
    }
    private string OwnedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.TrimEndingDirectorySeparator(DirectoryPath), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("只能修改路线目录中的 JSON 文件。", nameof(path));
        return fullPath;
    }
    public void Delete(string path) => File.Delete(OwnedPath(path));
    public void Update(string path, RouteDefinition original, RouteDefinition edited)
    {
        path = OwnedPath(path);
        if (JsonSerializer.Serialize(Read(path), Json) != JsonSerializer.Serialize(original, Json))
            throw new IOException("路线文件已在其他位置修改，请取消编辑并重新载入。");
        WriteAtomic(ForSaving(edited), path, WriteMode.Update);
    }
    public void Export(RouteDefinition route, string path) => WriteAtomic(ForSaving(route), path, WriteMode.Export);

    private enum WriteMode { Create, Update, Export }
    private static void WriteAtomic(RouteDefinition route, string path, WriteMode mode)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, route, Json);
            if (mode == WriteMode.Update) File.Replace(temporary, path, null);
            else File.Move(temporary, path, overwrite: mode == WriteMode.Export);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
