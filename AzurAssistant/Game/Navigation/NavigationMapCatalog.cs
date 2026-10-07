using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AzurAssistant.Game.Navigation;

public sealed record NavigationTeleport(string Id, string Name, double X, double Y, string TitleTemplate,
    string IconTemplate, string ArrivalMapId, double ArrivalRadius, double? LandingX = null, double? LandingY = null);

public sealed record NavigationMap(string Id, string Name, int Floor, string CoordinateVersion, int Width, int Height,
    string MapFile, string MapSha256, string FeatureFile, string FeatureSha256, double LocalMapScale,
    double MovementScale, string WorldTemplate, IReadOnlyList<NavigationTeleport> Teleports)
{
    public MapPosition Position(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= Width || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"{Name}坐标须在 X 0–{Width - 1}、Y 0–{Height - 1} 内。");
        return new(Id, Floor, CoordinateVersion, x, y);
    }
    public bool Contains(MapPosition p) => (p.MapId, p.Floor, p.CoordinateVersion) == (Id, Floor, CoordinateVersion);
    public static NavigationMap From(NavigationLayout l) => new(l.MapId, "夏露露村", l.Floor, l.CoordinateVersion, l.Width, l.Height,
        l.MapFile, l.MapSha256, "", "", .931, 1, "world-village",
        [new(l.Teleport.Id, l.Teleport.Name, l.Teleport.X, l.Teleport.Y, "village-teleport-title", "teleport-icon", l.MapId, 35,
            l.Teleport.LandingX, l.Teleport.LandingY)]);
}

public sealed record NavigationMapCatalog(int SchemaVersion, string Version, IReadOnlyList<NavigationMap> Maps)
{
    public static NavigationMapCatalog Load(string? directory = null, bool verifyFiles = true)
    {
        directory ??= NavigationLayout.DirectoryPath;
        var catalog = JsonSerializer.Deserialize<NavigationMapCatalog>(File.ReadAllText(Path.Combine(directory, "maps.json")))
            ?? throw new InvalidDataException("地图目录为空。");
        if (catalog.SchemaVersion != 1 || string.IsNullOrWhiteSpace(catalog.Version) || catalog.Maps is not { Count: > 0 and <= 20 }
            || catalog.Maps.Select(m => m.Id).Distinct().Count() != catalog.Maps.Count) throw new InvalidDataException("地图目录版本或地图ID无效。");
        foreach (var map in catalog.Maps)
        {
            if (string.IsNullOrWhiteSpace(map.Id) || string.IsNullOrWhiteSpace(map.Name) || string.IsNullOrWhiteSpace(map.CoordinateVersion)
                || map.Floor != 0 || map.Width is < 256 or > 16384 || map.Height is < 256 or > 16384
                || !double.IsFinite(map.LocalMapScale) || map.LocalMapScale is < .1 or > 10
                || !double.IsFinite(map.MovementScale) || map.MovementScale is < .1 or > 10
                || map.Teleports is not { Count: > 0 } || map.Teleports.Select(t => t.Id).Distinct().Count() != map.Teleports.Count)
                throw new InvalidDataException("地图参数无效：" + map.Id);
            foreach (var (file, hash) in new[] { (map.MapFile, map.MapSha256), (map.FeatureFile, map.FeatureSha256) })
            {
                if (string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file || hash?.Length != 64)
                    throw new InvalidDataException("地图文件定义无效。");
                if (verifyFiles)
                {
                    using var stream = File.OpenRead(Path.Combine(directory, file));
                    if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("地图文件校验失败：" + file);
                }
            }
            foreach (var teleport in map.Teleports)
            {
                map.Position(teleport.X, teleport.Y);
                var arrival = catalog.Maps.SingleOrDefault(m => m.Id == teleport.ArrivalMapId)
                    ?? throw new InvalidDataException("传送落点地图不存在。");
                if (string.IsNullOrWhiteSpace(teleport.Id) || string.IsNullOrWhiteSpace(teleport.Name)
                    || string.IsNullOrWhiteSpace(teleport.TitleTemplate) || string.IsNullOrWhiteSpace(teleport.IconTemplate)
                    || !double.IsFinite(teleport.ArrivalRadius) || teleport.ArrivalRadius is <= 0 or > 1000
                    || (teleport.LandingX is null) != (teleport.LandingY is null)) throw new InvalidDataException("传送点参数无效。");
                if (teleport.LandingX is { } x && teleport.LandingY is { } y) arrival.Position(x, y);
            }
        }
        return catalog;
    }
}
