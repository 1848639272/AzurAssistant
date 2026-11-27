using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AzurAssistant.Game.Navigation;

public sealed record NavigationTeleport(string Id, string Name, double X, double Y, string TitleTemplate,
    string IconTemplate, string ArrivalMapId, double ArrivalRadius, double? LandingX = null, double? LandingY = null,
    string? OverlapTemplate = null);

public sealed record NavigationMap(string Id, string Name, int Floor, string CoordinateVersion, int Width, int Height,
    string FeatureFile, string FeatureSha256, double LocalMapScale,
    double MovementScale, string WorldTemplate, IReadOnlyList<NavigationTeleport> Teleports,
    string? LandingFeatureFile = null, string? LandingFeatureSha256 = null)
{
    public MapPosition Position(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= Width || y >= Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"{Name}坐标须在 X 0–{Width - 1}、Y 0–{Height - 1} 内。");
        return new(Id, Floor, CoordinateVersion, x, y);
    }
    public bool Contains(MapPosition p) => (p.MapId, p.Floor, p.CoordinateVersion) == (Id, Floor, CoordinateVersion);
    public static NavigationMap From(NavigationLayout l) => new(l.MapId, "夏露露村", l.Floor, l.CoordinateVersion, l.Width, l.Height,
        "", "", .931, 1, "world-village",
        [new(l.Teleport.Id, l.Teleport.Name, l.Teleport.X, l.Teleport.Y, "village-teleport-title", "teleport-icon", l.MapId, 35,
            l.Teleport.LandingX, l.Teleport.LandingY)]);
}

public sealed record NavigationMapCatalog(int SchemaVersion, string Version, IReadOnlyList<NavigationMap> Maps)
{
    public static NavigationMapCatalog Load(string? directory = null, bool verifyFiles = true, bool availableOnly = false)
    {
        directory ??= NavigationLayout.DirectoryPath;
        var catalog = JsonSerializer.Deserialize<NavigationMapCatalog>(File.ReadAllText(Path.Combine(directory, "maps.json")))
            ?? throw new InvalidDataException("地图目录为空。");
        if (catalog.SchemaVersion is not (1 or 2) || string.IsNullOrWhiteSpace(catalog.Version) || catalog.Maps is not { Count: > 0 and <= 20 }
            || catalog.Maps.Select(m => m.Id).Distinct().Count() != catalog.Maps.Count) throw new InvalidDataException("地图目录版本或地图ID无效。");
        var missing = new HashSet<string>();
        foreach (var map in catalog.Maps)
        {
            if (string.IsNullOrWhiteSpace(map.Id) || string.IsNullOrWhiteSpace(map.Name) || string.IsNullOrWhiteSpace(map.CoordinateVersion)
                || map.Floor != 0 || map.Width is < 256 or > 16384 || map.Height is < 256 or > 16384
                || !double.IsFinite(map.LocalMapScale) || map.LocalMapScale is < .1 or > 10
                || !double.IsFinite(map.MovementScale) || map.MovementScale is < .1 or > 10
                || map.Teleports is not { Count: > 0 } || map.Teleports.Select(t => t.Id).Distinct().Count() != map.Teleports.Count)
                throw new InvalidDataException("地图参数无效：" + map.Id);
            var files = new List<(string, string)> { (map.FeatureFile, map.FeatureSha256) };
            if ((map.LandingFeatureFile is null) != (map.LandingFeatureSha256 is null)
                || catalog.SchemaVersion == 2 && map.LandingFeatureFile is null)
                throw new InvalidDataException("落点地形索引定义不完整：" + map.Id);
            if (map.LandingFeatureFile is { } landingFeatures) files.Add((landingFeatures, map.LandingFeatureSha256!));
            foreach (var (file, hash) in files)
            {
                if (string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file || !file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                    || hash?.Length != 64 || !hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException("地图特征索引定义无效：" + map.Id);
                if (availableOnly && !File.Exists(Path.Combine(directory, file))) { missing.Add(map.Id); continue; }
                if (verifyFiles)
                {
                    using var stream = File.OpenRead(Path.Combine(directory, file));
                    if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("地图特征索引校验失败：" + file);
                }
            }
            foreach (var teleport in map.Teleports)
            {
                map.Position(teleport.X, teleport.Y);
                var arrival = catalog.Maps.SingleOrDefault(m => m.Id == teleport.ArrivalMapId);
                if (arrival is null && !availableOnly) throw new InvalidDataException("传送落点地图不存在。");
                if (string.IsNullOrWhiteSpace(teleport.Id) || string.IsNullOrWhiteSpace(teleport.Name)
                    || string.IsNullOrWhiteSpace(teleport.TitleTemplate) || string.IsNullOrWhiteSpace(teleport.IconTemplate)
                    || !double.IsFinite(teleport.ArrivalRadius) || teleport.ArrivalRadius is <= 0 or > 1000
                    || (teleport.LandingX is null) != (teleport.LandingY is null)) throw new InvalidDataException("传送点参数无效。");
                if (teleport.LandingX is { } x && teleport.LandingY is { } y) arrival?.Position(x, y);
            }
        }
        if (!availableOnly) return catalog;
        var available = catalog.Maps.Where(m => !missing.Contains(m.Id)).ToArray();
        if (available.Length == 0) throw new InvalidDataException("本机没有完整的地图特征索引。");
        var ids = available.Select(m => m.Id).ToHashSet();
        return catalog with { Maps = available.Select(m => m with { Teleports = m.Teleports.Where(t => ids.Contains(t.ArrivalMapId)).ToArray() }).ToArray() };
    }
}
