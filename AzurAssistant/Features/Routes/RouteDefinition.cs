using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzurAssistant.Game.Navigation;

namespace AzurAssistant.Features.Routes;

public sealed record RouteMap([property: JsonRequired] string Id, [property: JsonRequired] int Floor,
    [property: JsonRequired] string CoordinateVersion);
public sealed record RoutePoint([property: JsonRequired] string Name, [property: JsonRequired] double X,
    [property: JsonRequired] double Y);
public sealed record RouteDefinition([property: JsonRequired] int SchemaVersion, [property: JsonRequired] string Name,
    [property: JsonRequired] RouteMap Map, [property: JsonRequired] bool AutoPickup,
    [property: JsonRequired] IReadOnlyList<RoutePoint> Points)
{
    public const int CurrentSchema = 1;
    public NavigationMap Validate(IReadOnlyList<NavigationMap> maps)
    {
        if (SchemaVersion != CurrentSchema) throw new InvalidDataException("不支持的路线文件版本，请使用蔚蓝助手路线 JSON。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Map is null || Points is not { Count: > 0 and <= 2000 })
            throw new InvalidDataException("路线名称、地图或点位数量无效（1–2000点）。");
        var map = maps.SingleOrDefault(m => (m.Id, m.Floor, m.CoordinateVersion) == (Map.Id, Map.Floor, Map.CoordinateVersion))
            ?? throw new InvalidDataException("路线所属地图、层级或坐标版本不受支持。");
        foreach (var point in Points)
        {
            if (point is null || string.IsNullOrWhiteSpace(point.Name) || point.Name.Length > 80)
                throw new InvalidDataException("每个点位需要1–80字的名称。");
            try { map.Position(point.X, point.Y); }
            catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("点位“" + point.Name + "”的" + ex.Message, ex); }
        }
        return map;
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
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, route, Json);
        return path;
    }
    public void Export(RouteDefinition route, string path)
    {
        route.Validate(maps);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(route, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
