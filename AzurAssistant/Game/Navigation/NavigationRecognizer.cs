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
    private readonly Dictionary<string, MapImageMatcher> _landingTerrain = [];
    private string? _lastMiniMap;
    private string? _lastDisplayMap;
    private MapPosition? _lastPosition;
    private MapPosition? _landingHint;
    private FrameSnapshot? _lastFixFrame;
    private MapPosition? _candidate;
    private long _candidateFrame;
    private TimeSpan _candidateAt;
    private double _lastScale;
    private bool _checkingLanding;
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
        Maps = NavigationMapCatalog.Load(directory, availableOnly: true).Maps;
        var initial = Maps.FirstOrDefault(m => m.Id == _targetMap && m.Teleports.Any(t => t.Id == _targetTeleport)) ?? Maps[0];
        _targetMap = initial.Id;
        _targetTeleport = initial.Teleports.FirstOrDefault()?.Id ?? "";
        foreach (var map in Maps)
        {
            _maps.Add(map.Id, new(Path.Combine(directory, map.FeatureFile), map.Width, map.Height));
            if (map.LandingFeatureFile is { } file) _landingTerrain.Add(map.Id, new(Path.Combine(directory, file), map.Width, map.Height));
        }
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
        if (_templates.Match(frame, "world-map-title") is not null || _templates.Match(frame, "world-map-title-compact") is not null)
            return new(frame, NavigationPage.WorldMap, WorldRegion: _templates.Match(frame, Maps.Single(m => m.Id == _targetMap).WorldTemplate)?.Center);
        if (_templates.Match(frame, "adventure-title") is not null || _templates.Match(frame, "adventure-title-wide") is not null)
        {
            _lastFixFrame = null;
            _candidate = null;
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
        var daily = await _daily.ObserveAsync(frame, token);
        if (daily.Page == DailyPage.Menu) return new(frame, NavigationPage.MainMenu);
        if (daily.Page != DailyPage.World)
            return new(frame, NavigationPage.Unknown, Diagnostic: $"未确认大世界或地图，当前 {daily.Page}；{daily.Diagnostic}");
        return (await ObserveMinimapAsync(frame, token)) with { Page = NavigationPage.World };
    }

    public Task<NavigationObservation> ObserveMinimapAsync(FrameSnapshot frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _templates.ValidateFrame(frame);
        var registration = Register(frame, true, null, token);
        if (registration is null)
        {
            if (_candidate is null) _lastFixFrame = null;
            return Task.FromResult(new NavigationObservation(frame, NavigationPage.Unknown,
                Diagnostic: _candidate is null ? "小地图地形证据不足" : "小地图候选位置等待下一帧确认"));
        }
        var (map, mini) = registration.Value;
        var point = mini.Project(Layout.MinimapCenter.X, Layout.MinimapCenter.Y);
        var location = PositionOrNull(map, point);
        _lastPosition = location;
        _lastScale = mini.Scale;
        _lastFixFrame = frame;
        var diagnostic = (mini.Inliers < 14 || mini.Inliers < mini.Matches * .60 ? "连续帧确认候选；" : "") + Describe(mini);
        return Task.FromResult(new NavigationObservation(frame, NavigationPage.World, location, mini, FacingDegrees: ReadFacing(frame, mini), Diagnostic: diagnostic));
    }

    public Task<MovementMode> ReadMovementModeAsync(FrameSnapshot frame, CancellationToken token) => _movementMode.ReadAsync(frame, token);

    public void SelectTeleport(string mapId, string teleportId)
    {
        if (!Maps.Any(m => m.Id == mapId && m.Teleports.Any(t => t.Id == teleportId))) throw new ArgumentException("传送点不存在。");
        _targetMap = mapId; _targetTeleport = teleportId;
    }

    public void BeginLocalization(MapPosition? landingHint = null)
    {
        _lastPosition = null; _lastFixFrame = null; _candidate = null; _lastMiniMap = null;
        _landingHint = landingHint;
        _checkingLanding = landingHint is not null;
    }

    public void FinishLanding()
    {
        _checkingLanding = false;
        _landingHint = null;
        _candidate = null;
    }

    private (NavigationMap Map, MapImageMatch Match)? Register(FrameSnapshot frame, bool mini, ClientPoint? marker, CancellationToken token)
    {
        var cached = mini ? _landingHint?.MapId ?? _lastMiniMap : _lastDisplayMap;
        var previous = mini && _lastFixFrame is { } last && frame.SameViewport(last)
            && frame.SystemTimestamp >= last.SystemTimestamp && frame.SystemTimestamp - last.SystemTimestamp < TimeSpan.FromSeconds(3)
            ? _lastPosition : null;
        var region = mini ? Layout.Minimap : new PixelRegion(80, 100, 1300, 825);
        using var query = MapImageMatcher.Extract(frame, region, mini ? 1 : .35, mini, token, marker, mini ? 0 : 75);
        var result = FindBest(query);
        if (mini && result is null)
        {
            // Sample more of the visible terrain when icons and sparse detail dominate the central disk.
            using var detail = MapImageMatcher.Extract(frame, region, 1.35, true, token, detailedMinimap: true);
            result = MatchLandingTerrain(detail, token);
            if (result is null)
            {
                using var seed = MapImageMatcher.Extract(frame, region, 1.125, true, token, detailedMinimap: true);
                var coarseLocations = new Dictionary<string, Point2d>();
                result = FindBest(detail, seed, coarseLocations);
                if (result is null)
                {
                    using var fine = MapImageMatcher.Extract(frame, region, 1.65, true, token, detailedMinimap: true);
                    using var finer = MapImageMatcher.Extract(frame, region, 2.25, true, token, detailedMinimap: true);
                    using var pooled = MapQueryFeatures.MergeScales(query, seed, detail, fine, finer);
                    result = RefineSparseTerrain(pooled, detail, _landingHint ?? previous, coarseLocations, token);
                    if (result is null && previous is not null && !_checkingLanding)
                        return TrackCandidate(frame, previous, pooled, seed, token);
                }
            }
        }
        if (mini) _candidate = null;
        if (result is { } best)
        {
            if (mini) _lastMiniMap = best.Map.Id; else _lastDisplayMap = best.Map.Id;
        }
        return result;

        (NavigationMap Map, MapImageMatch Match)? FindBest(MapQueryFeatures features, MapQueryFeatures? seedFeatures = null,
            Dictionary<string, Point2d>? coarseLocations = null)
        {
            var candidates = new List<(NavigationMap Map, MapImageMatch Match)>();
            foreach (var map in Maps.OrderByDescending(m => m.Id == cached))
            {
                if (mini && _landingHint is { } destination && map.Id != destination.MapId) continue;
                var near = mini && (_landingHint ?? previous) is { } p && p.MapId == map.Id ? new Point2d(p.X, p.Y) : (Point2d?)null;
                var matcher = _maps[map.Id];
                MapImageMatch? match;
                if (seedFeatures is not null)
                {
                    match = MatchMinimapTerrain(matcher, features, seedFeatures, near, token, out var estimate);
                    if (estimate is { } point) coarseLocations![map.Id] = point;
                }
                else
                {
                    var local = near is null ? null : matcher.Match(features, 14, token, near, 450 * map.MovementScale);
                    match = local ?? matcher.Match(features, mini ? 14 : 40, token);
                }
                if (match is null || Math.Abs(Math.Atan2(match.B, match.A)) > .035) continue;
                if (!mini && match.Scale / map.LocalMapScale is < .9 or > 1.1) continue;
                if (seedFeatures is null && map.Id == cached) return (map, match);
                candidates.Add((map, match));
            }
            return SelectUnique(candidates);
        }
    }

    private static (NavigationMap Map, MapImageMatch Match)? SelectUnique(List<(NavigationMap Map, MapImageMatch Match)> candidates)
    {
        var ordered = candidates.OrderByDescending(c => c.Match.Inliers).ToArray();
        return ordered.Length == 0 || ordered.Length > 1 && ordered[0].Match.Inliers < ordered[1].Match.Inliers * 1.5
            ? null : ordered[0];
    }

    private (NavigationMap, MapImageMatch)? MatchLandingTerrain(MapQueryFeatures features, CancellationToken token)
    {
        // Every output is a transform of the current pixels. The destination hint only narrows the search.
        var candidates = new List<(NavigationMap, MapImageMatch)>();
        var hint = _landingHint is { } h ? new Point2d(h.X, h.Y) : (Point2d?)null;
        foreach (var map in Maps)
        {
            if (_landingHint is { } expected && expected.MapId != map.Id || !_landingTerrain.TryGetValue(map.Id, out var terrain)) continue;
            var hit = terrain.Match(features, 14, token, hint, 220);
            if (hit is { Coverage: >= .38, Scale: > 2.15 and < 2.39 } && Math.Abs(Math.Atan2(hit.B, hit.A)) < .035)
                candidates.Add((map, hit));
        }
        return SelectUnique(candidates);
    }

    private (NavigationMap, MapImageMatch)? TrackCandidate(FrameSnapshot frame, MapPosition previous,
        MapQueryFeatures detail, MapQueryFeatures seed, CancellationToken token)
    {
        var earlier = _candidate;
        _candidate = null;
        if (_lastFixFrame is not { } last || last.Id >= frame.Id) return null;
        var map = Maps.Single(m => m.Id == previous.MapId);
        var near = new Point2d(previous.X, previous.Y);
        var matcher = _maps[map.Id];
        var hit = matcher.Match(detail, 10, token, near, 250 * map.MovementScale, .76, .50)
            ?? matcher.Match(seed, 10, token, near, 250 * map.MovementScale, .76, .50);
        if (hit is null || Math.Abs(hit.Scale / _lastScale - 1) >= .04 || Math.Abs(Math.Atan2(hit.B, hit.A)) >= .035) return null;
        var current = PositionOrNull(map, hit.Project(Layout.MinimapCenter.X, Layout.MinimapCenter.Y));
        double MovementLimit(TimeSpan elapsed) => map.MovementScale * (5 + 100 * elapsed.TotalSeconds);
        if (current is null || previous.DistanceTo(current) > MovementLimit(frame.SystemTimestamp - last.SystemTimestamp)) return null;

        var age = frame.SystemTimestamp - _candidateAt;
        var agrees = earlier is not null && earlier.MapId == current.MapId && frame.Id > _candidateFrame
            && age > TimeSpan.Zero && age < TimeSpan.FromMilliseconds(900) && earlier.DistanceTo(current) <= MovementLimit(age);
        _candidate = current;
        _candidateAt = frame.SystemTimestamp;
        _candidateFrame = frame.Id;
        return agrees ? (map, hit) : null;
    }

    private (NavigationMap, MapImageMatch)? RefineSparseTerrain(MapQueryFeatures features, MapQueryFeatures detail, MapPosition? recent,
        IReadOnlyDictionary<string, Point2d> coarseLocations, CancellationToken token)
    {
        var candidates = new List<(NavigationMap, MapImageMatch)>();
        foreach (var map in Maps.OrderByDescending(m => recent?.MapId == m.Id))
        {
            if (_landingHint is { } destination && destination.MapId != map.Id) continue;
            var near = recent?.MapId == map.Id ? new Point2d(recent.X, recent.Y) : (Point2d?)null;
            var hasLandmarks = _landingTerrain.TryGetValue(map.Id, out var landmarks);
            var center = Layout.MinimapCenter;
            if (near is null && hasLandmarks)
            {
                var proposal = landmarks!.Match(detail, 8, token, descriptorRatio: .72, minimumInlierRatio: .50)
                    ?? landmarks.Match(features, 8, token, descriptorRatio: .72, minimumInlierRatio: .50);
                if (Plausible(proposal)) near = proposal!.Project(center.X, center.Y);
            }
            if (near is null && coarseLocations.TryGetValue(map.Id, out var coarse)) near = coarse;
            if (near is null) continue;
            var terrain = _maps[map.Id].Match(features, 14, token, near, 250 * map.MovementScale, .76);
            if (!Plausible(terrain) && hasLandmarks)
            {
                terrain = landmarks!.Match(features, 14, token, near, 250, .76);
                if (terrain is { Coverage: < .38 }) terrain = null;
            }
            if (!Plausible(terrain)) continue;
            if (recent?.MapId != map.Id && terrain!.Project(center.X, center.Y).DistanceTo(near.Value) > 6) continue;
            if (recent?.MapId == map.Id) return (map, terrain!);
            candidates.Add((map, terrain!));
        }
        return SelectUnique(candidates);
        static bool Plausible(MapImageMatch? hit) => hit is { Scale: > 2.15 and < 2.39 } && Math.Abs(Math.Atan2(hit.B, hit.A)) <= .035;
    }

    private MapImageMatch? MatchMinimapTerrain(MapImageMatcher matcher, MapQueryFeatures features, MapQueryFeatures seedFeatures, Point2d? near,
        CancellationToken token, out Point2d? globalCandidate)
    {
        globalCandidate = null;
        if (near is not null)
        {
            var local = matcher.Match(features, 14, token, near, 250, .76)
                ?? matcher.Match(seedFeatures, 14, token, near, 250, .76);
            if (local is not null && Math.Abs(Math.Atan2(local.B, local.A)) <= .035) return local;
        }
        // A coarse global hypothesis may propose a patch. Only a geometrically consistent, fully verified
        // second match can supply the actual position, including after a teleport to a different patch.
        var seed = matcher.Match(seedFeatures, 8, token, descriptorRatio: .74);
        if (seed is null || Math.Abs(Math.Atan2(seed.B, seed.A)) > .035) return null;
        var center = Layout.MinimapCenter;
        var estimate = seed.Project(center.X, center.Y);
        if (seed.Scale is > 2.15 and < 2.39) globalCandidate = estimate;
        var refined = matcher.Match(features, 14, token, estimate, Math.Max(200, seed.Scale * 110), .76);
        return refined is not null && Math.Abs(refined.Scale / seed.Scale - 1) <= .04
            && refined.Project(center.X, center.Y).DistanceTo(estimate) <= 6 ? refined : null;
    }

    private async Task<(ClientPoint? Point, string? Name)> ReadOverlapAsync(FrameSnapshot frame, CancellationToken token)
    {
        var icon = _templates.Match(frame, "overlap-teleport-icon");
        var divider = icon is null ? _templates.Match(frame, "overlap-row-divider")?.Center
            : new ClientPoint(icon.Center.X + 23, icon.Center.Y);
        if (divider is not { } anchor) return (null, null);
        var expected = Maps.Single(m => m.Id == _targetMap).Teleports.FirstOrDefault(t => t.Id == _targetTeleport);
        static string Letters(string s) => string.Concat(s.Where(char.IsLetterOrDigit));
        (ClientPoint? Point, string? Name) other = (null,null);
        foreach (var offset in new[] { 0, -1, 1, -2, 2, -3, 3 })
        {
            var y = anchor.Y + offset * 54;
            if (y < 100 || y > frame.Height - 100 || anchor.X + 170 >= frame.Width) continue;
            if (offset != 0 && _templates.Match(frame,"overlap-row-divider",new(anchor.X - 12, y - 24, 24, 48)) is null) continue;
            var x = anchor.X + 6;
            var roi = new PixelRegion(x, y - 22, Math.Min(350,frame.Width-x), 45);
            if (expected?.OverlapTemplate is { } label && _templates.Match(frame,label,roi) is { } match)
                return (match.Center,expected.Name);
            var text = await _ocr.ReadAsync(frame, token, roi, 2);
            var name = Letters(string.Concat(text.Where(t => t.Confidence >= .80).OrderBy(t => t.X).Select(t => t.Text)));
            var names = Maps.SelectMany(m => m.Teleports).Select(t => t.Name).Distinct().Where(s => Letters(s) == name).ToArray();
            if (names.Length != 1) continue;
            other = (new ClientPoint(x+75,y),names[0]);
            if (names[0] == expected?.Name) return other;
        }
        return other;
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
    public void Dispose() { foreach (var arrow in _arrows) arrow.Dispose(); foreach (var mask in _arrowMasks) mask.Dispose(); foreach (var map in _maps.Values.Concat(_landingTerrain.Values)) map.Dispose(); }
}
