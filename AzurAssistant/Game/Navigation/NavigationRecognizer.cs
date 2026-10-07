using AzurAssistant.Contracts;
using System.IO;
using System.Text.Json;
using AzurAssistant.Game.Ui;
using AzurAssistant.Vision;
using OpenCvSharp;

namespace AzurAssistant.Game.Navigation;

public sealed class NavigationRecognizer : INavigationRecognizer, IDisposable
{
    private readonly IDailyUiRecognizer _daily;
    private readonly PpOcrReader _ocr;
    private readonly TemplateCatalog _templates;
    private readonly Dictionary<string, MapImageMatcher> _maps = [];
    private string? _lastMiniMap;
    private string? _lastDisplayMap;
    private MapPosition? _lastPosition;
    private string _targetMap = "200000";
    private string _targetTeleport = "village-star-node";
    private readonly MovementModeReader _movementMode;
    private readonly List<Mat> _arrows = [];
    private readonly List<Mat> _arrowMasks = [];
    public NavigationLayout Layout { get; }
    public IReadOnlyList<NavigationMap> Maps { get; }

    public NavigationRecognizer(IDailyUiRecognizer daily, PpOcrReader ocr, string? directory = null)
    {
        directory ??= NavigationLayout.DirectoryPath;
        Layout = NavigationLayout.Load(directory);
        _daily = daily; _ocr = ocr;
        _templates = new(directory);
        Maps = NavigationMapCatalog.Load(directory).Maps;
        foreach (var map in Maps) _maps.Add(map.Id, new(Path.Combine(directory, map.MapFile), Path.Combine(directory, map.FeatureFile)));
        _movementMode = new(ocr, directory);
        var manifest = JsonSerializer.Deserialize<TemplateManifest>(File.ReadAllText(Path.Combine(directory, "recognition.json")))!;
        using var arrow = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(directory, manifest.Templates["map-player-arrow"].File)), ImreadModes.Color);
        using var shape = ArrowOutline(arrow);
        Cv2.FindContours(shape, out var outlines, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        using var shapeMask = new Mat(shape.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillConvexPoly(shapeMask, Cv2.ConvexHull(outlines.MaxBy(c => Cv2.ContourArea(c))!), Scalar.All(255));
        for (var angle = 0; angle < 360; angle += 5)
        {
            using var rotation = Cv2.GetRotationMatrix2D(new Point2f(24, 24), angle, 1);
            var rotated = new Mat();
            Cv2.WarpAffine(shape, rotated, rotation, shape.Size(), InterpolationFlags.Linear);
            _arrows.Add(rotated);
            var rotatedMask = new Mat();
            Cv2.WarpAffine(shapeMask, rotatedMask, rotation, shape.Size(), InterpolationFlags.Nearest);
            _arrowMasks.Add(rotatedMask);
        }
    }

    public Task<NavigationObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token) => ObserveAsync(frame, token, true);
    public Task<NavigationObservation> ObserveTeleportAsync(FrameSnapshot frame, CancellationToken token) => ObserveAsync(frame, token, false);

    private async Task<NavigationObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token, bool locateMapPlayer)
    {
        token.ThrowIfCancellationRequested();
        _templates.ValidateFrame(frame);
        if (_templates.Match(frame, "world-map-title") is not null)
            return new(frame, NavigationPage.WorldMap, WorldRegion: _templates.Match(frame, Maps.Single(m => m.Id == _targetMap).WorldTemplate)?.Center);
        if (_templates.Match(frame, "adventure-title") is not null)
        {
            // Keep the right-hand details panel and all fixed edge UI out of reference matching.
            // Teleport selection needs the map transform and named target, not a scan for the player's outline.
            var marker = locateMapPlayer ? FindPlayerMarker(frame) : null;
            var registration = Register(frame, false, marker, token);
            var world = _templates.Match(frame, "world-map-button")?.Center;
            if (registration is null)
            {
                var overlap = await ReadOverlapAsync(frame, token);
                return new(frame, overlap.Point is null ? NavigationPage.OtherMap : NavigationPage.OverlapChoices,
                    OverlapChoice: overlap.Point, SelectedName: overlap.Name,
                    Diagnostic: "已知地图底图配准未通过（缩放/内点/分布）", WorldMapButton: world);
            }
            var (map, match) = registration.Value;
            var position = marker is { } player ? PositionOrNull(map, match.Project(player.X, player.Y)) : null;
            var button = _templates.Match(frame, "teleport-button");
            if (button is not null)
            {
                var titles = map.Teleports.Where(t => _templates.Match(frame, t.TitleTemplate) is not null).ToArray();
                return new(frame, NavigationPage.TeleportDetail, position, match, TeleportButton: button.Center,
                    SelectedName: titles.Length == 1 ? titles[0].Name : null, Diagnostic: Describe(match), DisplayedMapId: map.Id);
            }
            var choice = await ReadOverlapAsync(frame, token);
            if (choice.Point is not null)
                return new(frame, NavigationPage.OverlapChoices, position, match, OverlapChoice: choice.Point,
                    SelectedName: choice.Name, Diagnostic: Describe(match), DisplayedMapId: map.Id);
            var expected = map.Teleports.SingleOrDefault(t => map.Id == _targetMap && t.Id == _targetTeleport);
            ClientPoint? icon = null;
            var iconDiagnostic = "";
            if (expected is not null)
            {
                var projected = match.Unproject(expected.X, expected.Y);
                if (projected.X is > 95 and < 1730 && projected.Y is > 160 and < 915)
                {
                    var search = new PixelRegion((int)projected.X - 40, (int)projected.Y - 48, 80, 96);
                    var found = _templates.Match(frame, expected.IconTemplate, out var score, search)?.Center;
                    iconDiagnostic = $"；目标图标得分 {score:F3}";
                    if (found is null && expected.IconTemplate == "map-node-icon")
                    {
                        found = _templates.Match(frame, "map-simple-node-icon", out var simpleScore, search)?.Center;
                        iconDiagnostic += $"，简易节点 {simpleScore:F3}";
                    }
                    if (found is { } p && Math.Abs(p.X - projected.X) < 30 && Math.Abs(p.Y - projected.Y) < 35) icon = p;
                    iconDiagnostic += icon is not null ? "，已确认" : found is null ? "，未通过图标匹配" : "，偏离预期位置";
                }
                else iconDiagnostic = "；目标尚未进入可点击范围";
            }
            return new(frame, NavigationPage.LocalMap, position, match, TeleportIcon: icon, Diagnostic: Describe(match) + iconDiagnostic,
                DisplayedMapId: map.Id, WorldMapButton: world);
        }
        if ((await _daily.ObserveAsync(frame, token)).Page != DailyPage.World)
            return new(frame, NavigationPage.Unknown, Diagnostic: "未确认地图或世界画面");
        return (await ObserveMinimapAsync(frame, token)) with { Page = NavigationPage.World };
    }

    public Task<NavigationObservation> ObserveMinimapAsync(FrameSnapshot frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _templates.ValidateFrame(frame);
        var registration = Register(frame, true, null, token);
        if (registration is null)
            return Task.FromResult(new NavigationObservation(frame, NavigationPage.Unknown, Diagnostic: "小地图未匹配已知地图"));
        var (map, mini) = registration.Value;
        var point = mini.Project(Layout.MinimapCenter.X, Layout.MinimapCenter.Y);
        var location = PositionOrNull(map, point);
        _lastPosition = location;
        return Task.FromResult(new NavigationObservation(frame, NavigationPage.World, location, mini, FacingDegrees: ReadFacing(frame, mini), Diagnostic: Describe(mini)));
    }

    public Task<MovementMode> ReadMovementModeAsync(FrameSnapshot frame, CancellationToken token) => _movementMode.ReadAsync(frame, token);

    public void SelectTeleport(string mapId, string teleportId)
    {
        if (!Maps.Any(m => m.Id == mapId && m.Teleports.Any(t => t.Id == teleportId))) throw new ArgumentException("传送点不存在。");
        _targetMap = mapId; _targetTeleport = teleportId;
    }

    private (NavigationMap Map, MapImageMatch Match)? Register(FrameSnapshot frame, bool mini, ClientPoint? marker, CancellationToken token)
    {
        var cached = mini ? _lastMiniMap : _lastDisplayMap;
        var candidates = new List<(NavigationMap Map, MapImageMatch Match)>();
        using var query = MapImageMatcher.Extract(frame, mini ? Layout.Minimap : new(80, 100, 1300, 825),
            mini ? 1 : .35, mini, token, marker, mini ? 0 : 75);
        foreach (var map in Maps.OrderByDescending(m => m.Id == cached))
        {
            MapImageMatch? Match(Point2d? near) => _maps[map.Id].Match(query, mini ? 14 : 40,
                token, nearReference: near, referenceRadius: 450 * map.MovementScale);
            var near = mini && _lastPosition is { } p && p.MapId == map.Id ? new Point2d(p.X, p.Y) : (Point2d?)null;
            var match = near is null ? Match(null) : Match(near) ?? Match(null);
            if (match is null || Math.Abs(Math.Atan2(match.B, match.A)) > .035) continue;
            var ratio = match.Scale / map.LocalMapScale;
            // The local map's default zoom varies by region independently of the HUD minimap.
            // Minimap scale comes from distributed SIFT/RANSAC evidence, not a local-map zoom multiplier.
            if (!mini && ratio is < .9 or > 1.1) continue;
            if (map.Id == cached) return (map, match);
            candidates.Add((map, match));
        }
        var ordered = candidates.OrderByDescending(c => c.Match.Inliers).ToArray();
        if (ordered.Length == 0 || ordered.Length > 1 && ordered[0].Match.Inliers < ordered[1].Match.Inliers * 1.5) return null;
        var best = ordered[0];
        if (mini) _lastMiniMap = best.Map.Id; else _lastDisplayMap = best.Map.Id;
        return best;
    }

    private async Task<(ClientPoint? Point, string? Name)> ReadOverlapAsync(FrameSnapshot frame, CancellationToken token)
    {
        var icon = _templates.Match(frame, "overlap-teleport-icon");
        if (icon is null) return (null, null);
        var roi = new PixelRegion(icon.Center.X + 24, Math.Max(90, icon.Center.Y - 23),
            Math.Min(350, frame.Width - icon.Center.X - 35), 47);
        var text = await _ocr.ReadAsync(frame, token, roi, 2);
        var name = string.Concat(text.Where(x => x.Confidence >= .80).OrderBy(x => x.X).Select(x => x.Text))
            .Replace(" ", "").Replace("・", "·").Replace(".", "·");
        return Maps.SelectMany(m => m.Teleports).Any(t => t.Name == name)
            ? (new ClientPoint(icon.Center.X + 130, icon.Center.Y), name) : (null, null);
    }

    private ClientPoint? FindPlayerMarker(FrameSnapshot frame)
    {
        using var color = MapImageMatcher.ReadRegion(frame, new(30, 95, 1750, 855));
        using var binary = ArrowOutline(color);
        Cv2.FindContours(binary, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var candidates = new List<(double Score, ClientPoint Point)>();
        foreach (var contour in contours)
        {
            var box = Cv2.BoundingRect(contour);
            if (Cv2.ContourArea(contour) is < 45 or > 1100 || box.Width is < 13 or > 48 || box.Height is < 13 or > 48) continue;
            var x = box.X + box.Width / 2 - 32; var y = box.Y + box.Height / 2 - 32;
            if (x < 0 || y < 0 || x + 65 > binary.Width || y + 65 > binary.Height) continue;
            using var area = new Mat(binary, new Rect(x, y, 65, 65));
            var best = 0d; var center = default(ClientPoint);
            var bestAngle = 0;
            void MatchAngle(int angle)
            {
                using var scores = new Mat();
                Cv2.MatchTemplate(area, _arrows[angle], scores, TemplateMatchModes.CCorrNormed, _arrowMasks[angle]);
                Cv2.PatchNaNs(scores, 0);
                using var invalid = new Mat();
                Cv2.Compare(scores, Scalar.All(1.0001), invalid, CmpTypes.GT);
                scores.SetTo(Scalar.All(0), invalid);
                Cv2.MinMaxLoc(scores, out _, out var score, out _, out var pos);
                if (score > best) { best = score; bestAngle = angle; center = new(30 + x + pos.X + 24, 95 + y + pos.Y + 24); }
            }
            // Coarse rotation search followed by adjacent 5-degree refinements avoids 72 masked matches per contour.
            for (var angle = 0; angle < _arrows.Count; angle += 3) MatchAngle(angle);
            var coarse = bestAngle;
            MatchAngle((coarse + _arrows.Count - 1) % _arrows.Count);
            MatchAngle((coarse + 1) % _arrows.Count);
            if (best >= .80 && !candidates.Any(c => Math.Abs(c.Point.X - center.X) <= 3 && Math.Abs(c.Point.Y - center.Y) <= 3))
                candidates.Add((best, center));
        }
        if (candidates.Count != 1) return null;
        return candidates[0].Point;
    }
    // The brown outline stays opaque while the surrounding orange pulse expands and merges with terrain.
    private static Mat ArrowOutline(Mat color)
    {
        var output = new Mat(color.Rows, color.Cols, MatType.CV_8UC1);
        var inputBytes = new byte[color.Rows * color.Cols * color.ElemSize()];
        System.Runtime.InteropServices.Marshal.Copy(color.Data, inputBytes, 0, inputBytes.Length);
        var mask = new byte[color.Rows * color.Cols]; var channels = color.Channels();
        for (var i = 0; i < mask.Length; i++)
        {
            var b = inputBytes[i * channels]; var g = inputBytes[i * channels + 1]; var r = inputBytes[i * channels + 2];
            if (r is >= 100 and <= 220 && g is >= 65 and <= 180 && b <= 145
                && r - g is >= 20 and <= 90 && g - b is >= 15 and <= 80) mask[i] = 255;
        }
        System.Runtime.InteropServices.Marshal.Copy(mask, 0, output.Data, mask.Length);
        return output;
    }
    private double? ReadFacing(FrameSnapshot frame, MapImageMatch map)
    {
        var center = Layout.MinimapCenter;
        using var image = MapImageMatcher.ReadRegion(frame, new(center.X - 12, center.Y - 12, 25, 25));
        using var mask = Orange(image, minimap: true);
        var angle = ArrowOrientation.Read(mask);
        return angle is { } value ? ArrowOrientation.NorthClockwise(value + Math.Atan2(map.B, map.A) * 180 / Math.PI) : null;
    }
    private static Mat Orange(Mat color, bool minimap = false)
    {
        var channels = color.Split();
        try
        {
            using var r = new Mat(); using var g = new Mat(); using var b = new Mat();
            Cv2.InRange(channels[2], Scalar.All(231), Scalar.All(255), r);
            Cv2.InRange(channels[1], Scalar.All(101), Scalar.All(minimap ? 240 : 227), g);
            Cv2.InRange(channels[0], Scalar.All(0), Scalar.All(minimap ? 190 : 139), b);
            var result = new Mat(); Cv2.BitwiseAnd(r, g, result); Cv2.BitwiseAnd(result, b, result);
            return result;
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
    }
    private static MapPosition? PositionOrNull(NavigationMap map, Point2d point) => point.X >= 0 && point.Y >= 0 && point.X < map.Width && point.Y < map.Height
        ? map.Position(point.X, point.Y) : null;
    private static string Describe(MapImageMatch match) => $"内点 {match.Inliers}/{match.Matches}，误差 {match.MedianError:F2}px，比例 {match.Scale:F4}";
    public void Dispose() { foreach (var arrow in _arrows) arrow.Dispose(); foreach (var mask in _arrowMasks) mask.Dispose(); foreach (var map in _maps.Values) map.Dispose(); }
}
