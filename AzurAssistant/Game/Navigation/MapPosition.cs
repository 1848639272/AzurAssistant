using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Game.Navigation;

public sealed record MapPosition(string MapId, int Floor, string CoordinateVersion, double X, double Y)
{
    public bool SameMap(MapPosition other) => (MapId, Floor, CoordinateVersion) == (other.MapId, other.Floor, other.CoordinateVersion);
    public double DistanceTo(MapPosition other)
    {
        if ((MapId, Floor, CoordinateVersion) != (other.MapId, other.Floor, other.CoordinateVersion))
            throw new InvalidOperationException("不能比较不同地图、层级或版本的坐标。");
        return Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));
    }
}
public sealed record TeleportPoint(string Id, string Name, double X, double Y,
    [property: JsonRequired] double LandingX, [property: JsonRequired] double LandingY);
public sealed record NavigationLayout(int SchemaVersion, string Version, string MapId, int Floor,
    string CoordinateVersion, string MapFile, string MapSha256, int Width, int Height,
    PixelRegion Minimap, ClientPoint MinimapCenter, int MinimapRadius, TeleportPoint Teleport)
{
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "assets", "navigation");
    public static NavigationLayout Load(string? directory = null)
    {
        directory ??= DirectoryPath;
        var result = JsonSerializer.Deserialize<NavigationLayout>(File.ReadAllText(Path.Combine(directory, "layout.json")))
            ?? throw new InvalidDataException("地图配置为空。");
        if (result.SchemaVersion != 2 || result.MapId != "200000" || result.CoordinateVersion != "village-pixel-v1"
            || result.Width != 2048 || result.Height != 2048 || result.Floor != 0 || result.Teleport is null
            || Path.GetFileName(result.MapFile) != result.MapFile || string.IsNullOrWhiteSpace(result.Version))
            throw new InvalidDataException("地图配置版本或坐标系不受支持。");
        result.Minimap.Validate(1920, 1080);
        if (result.Minimap.Width != 160 || result.Minimap.Height != 160 || result.MinimapRadius != 67
            || result.MinimapCenter != new ClientPoint(result.Minimap.X + 80, result.Minimap.Y + 79)
            || result.Teleport.Id != "village-star-node" || result.Teleport.Name != "夏露露村·星脉节点")
            throw new InvalidDataException("导航布局不符合当前已验证的小地图或节点定义。");
        if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, result.MapFile))))
            .Equals(result.MapSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("地图底图校验失败。");
        result.Position(result.Teleport.X, result.Teleport.Y);
        result.Position(result.Teleport.LandingX, result.Teleport.LandingY);
        return result;
    }
    public MapPosition Position(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= Width || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(x), "坐标必须在 0–2047 范围内。");
        return new(MapId, Floor, CoordinateVersion, x, y);
    }
}

public enum NavigationPage { Unknown, World, LocalMap, OtherMap, TeleportDetail, OverlapChoices, WorldMap }
public enum MovementMode { Unknown, Walk, Run }
public sealed record NavigationStatus(MapPosition? Position, DateTimeOffset ObservedAt, string Detail, double? FacingDegrees = null);
public sealed record NavigationObservation(FrameSnapshot Frame, NavigationPage Page, MapPosition? Position = null,
    MapImageMatch? Registration = null, ClientPoint? TeleportIcon = null, ClientPoint? TeleportButton = null,
    ClientPoint? OverlapChoice = null, string? SelectedName = null, double? FacingDegrees = null, string Diagnostic = "",
    string? DisplayedMapId = null, ClientPoint? WorldMapButton = null, ClientPoint? WorldRegion = null);
public interface INavigationRecognizer
{
    NavigationLayout Layout { get; }
    IReadOnlyList<NavigationMap> Maps => [NavigationMap.From(Layout)];
    void SelectTeleport(string mapId, string teleportId) { }
    Task<NavigationObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token);
    Task<NavigationObservation> ObserveTeleportAsync(FrameSnapshot frame, CancellationToken token) => ObserveAsync(frame, token);
    Task<NavigationObservation> ObserveMinimapAsync(FrameSnapshot frame, CancellationToken token) => ObserveAsync(frame, token);
    Task<MovementMode> ReadMovementModeAsync(FrameSnapshot frame, CancellationToken token) => Task.FromResult(MovementMode.Unknown);
}
