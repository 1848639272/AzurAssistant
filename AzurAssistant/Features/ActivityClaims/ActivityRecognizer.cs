using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;
using AzurAssistant.Vision;

namespace AzurAssistant.Features.ActivityClaims;

public enum ActivityChest { Unknown, Locked, Ready, Claimed }
public sealed record ActivityObservation(DailyPage Page, IReadOnlyDictionary<string, ClientPoint> Targets,
    int? Activity, string FirstTask, bool TasksSettled, ActivityChest[] Chests) : DailyUiObservation(Page, Targets);

public sealed class ActivityRecognizer : IDailyUiRecognizer
{
    private readonly IDailyUiRecognizer _shared;
    private readonly TemplateCatalog _templates;
    private readonly PpOcrReader _ocr;
    private readonly Layout _layout;
    private sealed record Layout(int SchemaVersion, PixelRegion Activity, PixelRegion ActivityValue, PixelRegion FirstTask,
        int[] ChestCenters, PixelRegion ChestAlertSearch, ClientPoint? ChestAlertOffset, int[] RowCenters);

    public ActivityRecognizer(IDailyUiRecognizer shared, PpOcrReader ocr, string? directory = null)
    {
        _shared = shared; _ocr = ocr;
        directory ??= Path.Combine(AppContext.BaseDirectory, "assets", "activity");
        _templates = new(directory);
        _layout = JsonSerializer.Deserialize<Layout>(File.ReadAllText(Path.Combine(directory, "layout.json")))
            ?? throw new InvalidDataException("日常周常布局为空。");
        if (_layout.SchemaVersion != 1 || _layout.ChestCenters is not { Length: 5 } || _layout.RowCenters is not { Length: 6 }
            || _layout.ChestCenters.Any(x => x < 70 || x > 1850) || _layout.RowCenters.Any(y => y < 30 || y > 880)
            || _layout.ChestAlertOffset is not { X: >= -60 and <= -10, Y: >= 0 and <= 60 })
            throw new InvalidDataException("日常周常布局版本或槽位无效。");
        _layout.Activity.Validate(1920, 1080); _layout.ActivityValue.Validate(1920, 1080); _layout.FirstTask.Validate(1920, 1080);
        foreach (var center in _layout.ChestCenters)
            (_layout.ChestAlertSearch with { X = _layout.ChestAlertSearch.X + center - _layout.ChestCenters[0] }).Validate(1920, 1080);
    }

    public async Task<DailyUiObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var shared = await _shared.ObserveAsync(frame, token);
        if (shared.Page is DailyPage.MailReward or DailyPage.HomeLevelUp or DailyPage.MailEmpty or DailyPage.HomeReward) return shared;
        var targets = new Dictionary<string, ClientPoint>();
        var scores = new List<string>();
        bool Match(string id, PixelRegion? region = null, string? target = null)
        {
            var found = _templates.Match(frame, id, out var score, region);
            scores.Add($"{id}={score:F3}");
            if (found is null) return false;
            targets[target ?? id] = found.Center; return true;
        }
        var day = Match("day-tab"); var week = Match("week-tab");
        var close = Match("close");
        if (day != week && close)
        {
            // The selected tab includes its light background so inactive text alone cannot select a page.
            if (!Match("day-label")) Match("day-label-inactive", target: "day-label");
            Match("week-label");
            var page = day ? DailyPage.ActivityDay : DailyPage.ActivityWeek;
            var rowStates = new List<string>();
            for (var i = 0; i < _layout.RowCenters.Length; i++)
            {
                var region = new PixelRegion(1625, _layout.RowCenters[i] - 30, 143, 65);
                var claim = Match(day ? "row-claim-day" : "row-claim-week", region, $"claim-{i}");
                var settled = !claim && (Match("row-progress", region, $"settled-{i}")
                    || Match("row-go", region, $"settled-{i}") || Match("row-claimed", region, $"settled-{i}"));
                rowStates.Add(claim ? "ready" : settled ? "settled" : "unknown");
            }
            var chests = new ActivityChest[5];
            for (var i = 0; i < chests.Length; i++)
            {
                var region = new PixelRegion(_layout.ChestCenters[i] - 57, 935, 120, 87);
                var alertRegion = _layout.ChestAlertSearch with { X = _layout.ChestAlertSearch.X + _layout.ChestCenters[i] - _layout.ChestCenters[0] };
                var ready = Match(day ? "chest-alert" : "chest-alert-week", alertRegion, $"chest-{i}");
                if (ready)
                {
                    ready = IsRedAlert(frame, targets[$"chest-{i}"], out var redPixels);
                    scores.Add($"chest-{i}-red={redPixels}");
                    if (!ready) targets.Remove($"chest-{i}");
                }
                if (ready)
                {
                    // Anchor the hit area to the static alert, independent of the animated chest artwork.
                    var alert = targets[$"chest-{i}"];
                    var offset = _layout.ChestAlertOffset!.Value;
                    targets[$"chest-{i}"] = new(alert.X + offset.X, alert.Y + offset.Y);
                }
                chests[i] = ready ? ActivityChest.Ready
                    : Match("chest-open", region, $"opened-{i}") || !day && Match("chest-open-week", region, $"opened-{i}") ? ActivityChest.Claimed
                    : Match("chest-locked", region, $"locked-{i}") ? ActivityChest.Locked : ActivityChest.Unknown;
            }
            var text = await _ocr.ReadAsync(frame, token, _layout.Activity, 2);
            int? activity = ParseActivity(text);
            if (activity is null) { text = await _ocr.ReadAsync(frame, token, _layout.Activity, 1); activity = ParseActivity(text); }
            if (activity is null) { text = await _ocr.ReadAsync(frame, token, _layout.ActivityValue, 2); activity = ParseActivity(text); }
            var taskName = "";
            if (targets.ContainsKey("claim-0"))
            {
                var names = await _ocr.ReadAsync(frame, token, _layout.FirstTask, 2);
                taskName = string.Concat(names.Where(t => t.Confidence >= 0.85).OrderBy(t => t.X).Select(t => t.Text));
            }
            return new ActivityObservation(page, targets, activity, taskName, rowStates.All(s => s == "settled"), chests)
            {
                EvidenceKey = $"{activity}:{taskName}:{string.Join(',', rowStates)}:{string.Join(',', chests)}",
                Diagnostic = $"活跃度={activity?.ToString() ?? "未知"}；OCR={string.Join('|', text.Select(t => $"{t.Text}({t.Confidence:F2})"))}；行={string.Join(',', rowStates)}；宝箱={string.Join(',', chests)}；" + string.Join(",", scores)
            };
        }
        if (day || week) return new(DailyPage.Unknown, new Dictionary<string, ClientPoint>()) { Diagnostic = string.Join(",", scores) };
        if (shared.Page == DailyPage.Menu && Match("menu-entry"))
            return shared with { Targets = shared.Targets.Concat(targets.Where(p => p.Key == "menu-entry")).ToDictionary() };
        return shared;
    }

    private static int? ParseActivity(IReadOnlyList<TextRegion> text)
    {
        // The large left-hand value is separate from the small denominator and activity label.
        var candidates = text.Where(t => t.Confidence >= 0.85 && t.X < 395 && t.Height >= 28)
            .Select(t => Regex.Match(t.Text.Replace(" ", ""), @"^(\d{1,3})(?:/100)?$"))
            .Where(m => m.Success).ToArray();
        return candidates.Length == 1 && int.TryParse(candidates[0].Groups[1].Value, out var value) && value <= 100 ? value : null;
    }

    private static bool IsRedAlert(FrameSnapshot frame, ClientPoint center, out int red)
    {
        // Require red pixels around the white exclamation mark, not just a gray shape match.
        red = 0;
        for (var y = center.Y - 6; y < center.Y + 6; y++)
        for (var x = center.X - 5; x < center.X + 5; x++)
        {
            var index = y * frame.Stride + x * 4;
            var pixels = frame.Pixels.Span;
            if (pixels[index + 2] >= 160 && pixels[index + 2] - pixels[index + 1] >= 45
                && pixels[index + 2] - pixels[index] >= 30) red++;
        }
        return red >= 20;
    }
}
