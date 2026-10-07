using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;
using AzurAssistant.Vision;

namespace AzurAssistant.Features.Commissions;

public sealed record CommissionLayout(int SchemaVersion, string Version, Dictionary<string, PixelRegion> Regions, Dictionary<string, ClientPoint> Points)
{
    public static CommissionLayout Load(string? directory = null)
    {
        var value = JsonSerializer.Deserialize<CommissionLayout>(File.ReadAllText(Path.Combine(directory ??
            Path.Combine(AppContext.BaseDirectory, "assets", "commissions"), "layout.json"))) ?? throw new InvalidDataException("委托布局为空。");
        if (value.SchemaVersion != 1 || string.IsNullOrWhiteSpace(value.Version)) throw new InvalidDataException("委托布局版本不受支持。");
        foreach (var region in value.Regions.Values) region.Validate(1920, 1080);
        foreach (var point in value.Points.Values)
            if (point.X is < 0 or >= 1920 || point.Y is < 0 or >= 1080) throw new InvalidDataException("委托坐标超出客户区。");
        return value;
    }
}

public sealed class CommissionRecognizer(IDailyUiRecognizer shared, PpOcrReader ocr, TemplateCatalog templates, CommissionLayout layout) : ICommissionRecognizer
{
    public static string Normalize(string text)
    {
        var value = string.Concat(text.Normalize(System.Text.NormalizationForm.FormKC).Where(char.IsLetterOrDigit));
        // Only aliases demonstrated by the supplied replay images are accepted.
        return value switch { "深巢梦魔" => "深巢梦魇", "丘数之陲" => "丘薮之陲", "干锤百炼" => "千锤百炼", _ => value };
    }

    internal static string ParsePreparationTeam(string text)
    {
        // The preparation header prefixes the name with a two-digit display index and a slash.
        // Remove only that first prefix; digits and slashes inside the actual team name are significant.
        var prefix = Regex.Match(text, @"^\s*[0-9]{2}\s*/\s*");
        return (prefix.Success ? text[prefix.Length..] : text).Trim();
    }

    internal static string ParsePreparationName(string text)
    {
        // Only the preparation title has this display prefix; strip it once before whole-name aliases.
        var display = text.Normalize(System.Text.NormalizationForm.FormKC);
        var prefix = Regex.Match(display, @"^\s*难度\s*(?:VI|IV|V|III|II|I)(?![A-Za-z0-9])\s*");
        var name = Normalize(prefix.Success ? display[prefix.Length..] : display);
        // The preserved preparation frame confirms this complete OCR reading, not a character-wide substitution.
        return name == "丘数之睡" ? "丘薮之陲" : name;
    }

    internal static bool IsAutoBattleShortcut(IReadOnlyList<TextRegion> words, PixelRegion region)
        => words.Count == 1 && words[0] is var word && word.Text.Trim() == "F1"
            && double.IsFinite(word.Confidence) && word.Confidence is >= .90 and <= 1
            && word.Width > 0 && word.Height > 0 && word.X > region.X && word.Y > region.Y
            && word.X + word.Width < region.X + region.Width && word.Y + word.Height < region.Y + region.Height;

    internal IReadOnlyList<PixelRegion> TeamTitleRegions(FrameSnapshot frame)
    {
        var rows = new List<PixelRegion>();
        var nameColumn = layout.Regions["team-names"];
        var searches = new Stack<PixelRegion>();
        searches.Push(layout.Regions["team-headers"]);
        while (searches.TryPop(out var search) && rows.Count < 6)
        {
            if (search.Height < 28) continue;
            var selected = templates.Match(frame, "team-selected", search);
            var unselected = templates.Match(frame, "team-unselected", search);
            var found = selected is not null && (unselected is null || selected.Score >= unselected.Score) ? selected : unselected;
            if (found is null) continue;
            var center = found.Center.Y;
            var row = new PixelRegion(nameColumn.X, center - 17, nameColumn.Width, 34);
            if (row.Y >= nameColumn.Y && row.Y + row.Height <= nameColumn.Y + nameColumn.Height) rows.Add(row);
            var upperHeight = center - 17 - search.Y;
            if (upperHeight >= 28) searches.Push(search with { Height = upperHeight });
            var lowerY = center + 17;
            var lowerHeight = search.Y + search.Height - lowerY;
            if (lowerHeight >= 28) searches.Push(search with { Y = lowerY, Height = lowerHeight });
        }
        return rows.OrderBy(row => row.Y).ToArray();
    }

    internal static string ParseTeamTitle(IEnumerable<TextRegion> words, PixelRegion row)
    {
        var values = words.OrderBy(t => t.X).ToArray();
        if (values.Length == 0 || values.Any(t => t.Confidence < .80 || t.X <= row.X || t.X + t.Width >= row.X + row.Width
            || t.Y <= row.Y || t.Y + t.Height >= row.Y + row.Height)) return "";
        return Normalize(string.Concat(values.Select(t => t.Text)));
    }

    public async Task<CommissionObservation> ObserveAsync(FrameSnapshot frame, CommissionRead read, CancellationToken token)
    {
        templates.ValidateFrame(frame);
        token.ThrowIfCancellationRequested();
        var targets = new Dictionary<string, ClientPoint>();
        var diagnostic = new List<string>();
        bool Match(string id, string? target = null, PixelRegion? region = null)
        {
            var found = templates.Match(frame, id, out var score, region);
            diagnostic.Add($"{id}={score:F2}");
            if (found is null) return false;
            targets[target ?? id] = found.Center; return true;
        }
        CommissionObservation Result(CommissionPage page) => new(page, page == CommissionPage.Unknown ? new Dictionary<string, ClientPoint>() : targets)
            { Diagnostic = string.Join(",", diagnostic) };
        async Task<IReadOnlyList<TextRegion>> Text(string id, int scale = 2, PixelRegion? region = null, bool keepLowConfidence = false)
        {
            var text = await ocr.ReadAsync(frame, token, region ?? layout.Regions[id], scale);
            diagnostic.Add(id + "=" + string.Join("/", text.Select(t => t.Text)));
            return keepLowConfidence ? text : text.Where(t => t.Confidence >= .80).ToArray();
        }
        async Task<string> Line(string id) => string.Concat((await Text(id)).OrderBy(t => t.Y).ThenBy(t => t.X).Select(t => t.Text));
        async Task AddNames(string region)
        {
            foreach (var text in await Text(region, 2))
            {
                var name = Normalize(text.Text);
                if (name.Length > 0) targets["name:" + name] = new((int)(text.X + text.Width / 2), (int)(text.Y + text.Height / 2));
            }
        }
        async Task AddDailyNames()
        {
            var region = layout.Regions["daily-list"];
            var known = CommissionOptions.DailyChoices.Select(c => Normalize(c.Name)).ToHashSet(StringComparer.Ordinal);
            var words = await Text("daily-list", 1, keepLowConfidence: true);
            foreach (var word in words)
            {
                var candidate = word;
                var name = Normalize(word.Text);
                if (word.Confidence < .80 || !known.Contains(name))
                {
                    var left = Math.Max(region.X, (int)Math.Floor(word.X) - 8);
                    var right = Math.Min(region.X + region.Width, (int)Math.Ceiling(word.X + word.Width) + 8);
                    var top = Math.Max(region.Y, (int)Math.Floor(word.Y) - 6);
                    var bottom = Math.Min(region.Y + region.Height, (int)Math.Ceiling(word.Y + word.Height) + 6);
                    if (right <= left || bottom <= top) continue;
                    var retry = await Text("daily-row-" + left + "-" + top, 2, new(left, top, right - left, bottom - top), true);
                    if (retry.Count != 1 || retry[0].Confidence < .80) continue;
                    candidate = retry[0]; name = Normalize(candidate.Text);
                }
                if (known.Contains(name) && candidate.X > region.X && candidate.X + candidate.Width < region.X + region.Width
                    && candidate.Y > region.Y && candidate.Y + candidate.Height < region.Y + region.Height)
                    targets["name:" + name] = new((int)(candidate.X + candidate.Width / 2), (int)(candidate.Y + candidate.Height / 2));
            }
            // Only the three complete category labels participate in page stability.
            var categories = CommissionOptions.DailyChoices.Select(c => Normalize(c.Category)).ToHashSet(StringComparer.Ordinal);
            foreach (var word in await Text("daily-tabs"))
            {
                var name = Normalize(word.Text);
                if (categories.Contains(name)) targets["name:" + name] = new((int)(word.X + word.Width / 2), (int)(word.Y + word.Height / 2));
            }
        }
        async Task AddStoryNames(int chapter)
        {
            var numbers = await Text("story-numbers");
            var names = await Text("story-names", keepLowConfidence: true);
            foreach (var name in names)
            {
                var region = layout.Regions["story-names"];
                if (name.X <= region.X || name.X + name.Width >= region.X + region.Width) continue;
                var center = name.X + name.Width / 2;
                static Match Index(TextRegion word) => Regex.Match(word.Text.Replace(" ", ""), @"^([1-4])[-－—]([1-9]|10)$");
                var candidates = numbers.Where(n => Math.Abs(n.X + n.Width / 2 - center) <= 35 && Index(n).Success).ToArray();
                if (candidates.Length == 0)
                {
                    // A faint gold number can be merged with its card art in the wide strip. Re-read only that same card.
                    var left = Math.Max(region.X, (int)center - 78);
                    var right = Math.Min(region.X + region.Width, (int)center + 78);
                    candidates = (await Text("story-number-" + left, 3, new(left, 295, right - left, 68)))
                        .Where(n => Math.Abs(n.X + n.Width / 2 - center) <= 35 && Index(n).Success).ToArray();
                }
                if (candidates.Length != 1) continue;
                var index = Index(candidates[0]);
                if (int.Parse(index.Groups[1].Value) != chapter) continue;
                var candidate = name;
                var full = Normalize(index.Value + candidate.Text);
                var known = CommissionOptions.GetStoryChoices(chapter).Select(Normalize).ToHashSet(StringComparer.Ordinal);
                if (candidate.Confidence < .80 || !known.Contains(full))
                {
                    var left = Math.Max(region.X, (int)center - 90);
                    var right = Math.Min(region.X + region.Width, (int)center + 90);
                    var retry = await Text("story-name-" + left, 3, new(left, 405, right - left, 62), true);
                    if (retry.Count != 1 || retry[0].Confidence < .80) continue;
                    candidate = retry[0]; full = Normalize(index.Value + candidate.Text);
                }
                if (known.Contains(full) && candidate.X > region.X && candidate.X + candidate.Width < region.X + region.Width)
                    targets["name:" + full] = new((int)(candidate.X + candidate.Width / 2), (int)(candidate.Y + candidate.Height / 2));
            }
        }
        async Task AddCrisisNames()
        {
            var region = layout.Regions["crisis-list"];
            var words = await Text("crisis-list", 1, keepLowConfidence: true);
            foreach (var word in words.Where(t => t.X >= region.X && t.X <= region.X + 57))
            {
                var candidate = word;
                if (word.Confidence < .80)
                {
                    var top = Math.Max(region.Y, (int)Math.Floor(word.Y) - 6);
                    var bottom = Math.Min(region.Y + region.Height, (int)Math.Ceiling(word.Y + word.Height) + 6);
                    if (bottom <= top) continue;
                    var row = new PixelRegion(region.X, top, region.Width, bottom - top);
                    var retry = (await Text("crisis-row-" + top, 2, row, keepLowConfidence: true))
                        .Where(t => t.X >= region.X && t.X <= region.X + 57
                            && Math.Abs(t.Y + t.Height / 2 - (word.Y + word.Height / 2)) <= 12).ToArray();
                    if (retry.Length != 1 || retry[0].Confidence < .80) continue;
                    candidate = retry[0];
                }
                var name = Normalize(candidate.Text);
                if (name.Length > 0) targets["name:" + name] = new((int)(candidate.X + candidate.Width / 2),
                    (int)(candidate.Y + candidate.Height / 2));
            }
        }
        async Task<CommissionObservation> Resources(CommissionObservation value)
        {
            var entries = await Text("resources-menu");
            foreach (var entry in entries)
            {
                var number = Regex.Match(entry.Text.Replace(" ", ""), @"(?<!\d)(\d{1,4})/(300|10|5|3)(?!\d)");
                if (!number.Success) continue;
                var current = int.Parse(number.Groups[1].Value);
                value = number.Groups[2].Value switch
                {
                    "300" => value with { Stamina = current },
                    "3" when current <= 3 => value with { WeeklyRemaining = current },
                    "5" => value with { BlueKeys = current },
                    "10" => value with { YellowKeys = current },
                    _ => value
                };
            }
            return value;
        }
        static int? Integer(string text)
        {
            var match = Regex.Match(text, @"(?<!\d)(\d{1,3})(?!\d)");
            return match.Success ? int.Parse(match.Value) : null;
        }

        // Overlay anchors precede every underlying menu or battle HUD.
        if (Match("bond-level-title"))
        {
            targets.Clear();
            targets["dismiss"] = layout.Points["bond-dismiss"];
            return Result(CommissionPage.BondLevelUp);
        }
        var reward = Match("reward-title");
        var claim = Match("reward-claim", "claim");
        if (reward || claim)
        {
            if (!reward || !claim) return Result(CommissionPage.Unknown);
            Match("reward-skip", "skip"); Match("reward-cancel", "cancel");
            var open = Match("multiplier-open");
            var value = await Resources(Result(open ? CommissionPage.Multiplier : CommissionPage.Reward));
            var cost = await RewardCostReader.ReadAsync(ocr, frame, layout.Regions["reward-cost"],
                layout.Regions["reward-cost-number"], token);
            diagnostic.Add(cost.Diagnostic);
            var multiplierText = await Line("multiplier");
            var m = Regex.Match(multiplierText, @"消耗\s*([123])\s*倍");
            if (m.Success)
            {
                targets["multiplier"] = layout.Points["multiplier"];
                if (open) for (var n = 1; n <= 3; n++) targets[$"multiplier-{n}"] = layout.Points[$"multiplier-{n}"];
            }
            var resource = value.Stamina is not null ? RewardResource.Stamina
                : value.BlueKeys is not null && value.YellowKeys is not null ? KeyColor(frame, new(952, 397, 20, 23)) : RewardResource.Unknown;
            return value with { Cost = cost.Value, Multiplier = m.Success ? int.Parse(m.Groups[1].Value) : value.WeeklyRemaining is not null ? 1 : null,
                Resource = resource, Diagnostic = string.Join(",", diagnostic) };
        }
        if (Match("leave-message"))
            return Result(Match("leave-confirm", "confirm") ? CommissionPage.LeaveParty : CommissionPage.Unknown);
        if (Match("mvp")) { targets["dismiss"] = layout.Points["mvp-dismiss"]; return Result(CommissionPage.Mvp); }
        if (Match("result-title"))
        {
            if (!Match("result-return", "return") || !Match("result-again", "again")) return Result(CommissionPage.Unknown);
            return Result(CommissionPage.Result);
        }
        if (Match("crisis-result-return", "return") && Match("crisis-result-reward")) return Result(CommissionPage.CrisisResult);
        targets.Clear();
        if (Match("crisis-lobby") && Match("crisis-launch", "launch")) return Result(CommissionPage.Lobby);
        if (Match("prepare-start", "start") && Match("team-list", "teams"))
        {
            if (Match("team-title"))
            {
                if (read == CommissionRead.Navigation) return Result(CommissionPage.Teams);
                var rows = TeamTitleRegions(frame);
                diagnostic.Add("team-headers=" + string.Join('/', rows.Select(row => row.Y)));
                foreach (var row in rows)
                {
                    var name = ParseTeamTitle(await Text("team-row-" + row.Y, region: row, keepLowConfidence: true), row);
                    if (name.Length > 0) targets["name:" + name] = new(row.X + row.Width / 2, row.Y + row.Height / 2);
                }
                var selected = templates.Match(frame, "team-selected");
                var team = selected is null ? "" : targets.Where(t => t.Key.StartsWith("name:") && Math.Abs(t.Value.Y - selected.Center.Y) < 22)
                    .Select(t => t.Key[5..]).FirstOrDefault() ?? "";
                return Result(CommissionPage.Teams) with { Team = team };
            }
            var preparedName = ParsePreparationName(await Line("prepare-name"));
            var story = Enumerable.Range(1, 4).SelectMany(CommissionOptions.GetStoryChoices).Any(choice => Normalize(choice) == preparedName);
            return (await Resources(Result(CommissionPage.Prepare))) with
                { Name = preparedName, Team = ParsePreparationTeam(await Line("prepare-team")),
                    Cost = story ? Integer(await Line("prepare-cost")) : null, Resource = story ? RewardResource.Stamina : RewardResource.Unknown };
        }
        targets.Clear();
        if (Match("story-title"))
        {
            var selected = Enumerable.Range(1, 4).Where(n =>
                templates.Match(frame, "story-chapter-selected", new(layout.Points[$"chapter-{n}"].X - 25, 925, 50, 62)) is not null).ToArray();
            if (selected.Length != 1) return Result(CommissionPage.Unknown);
            targets["back"] = layout.Points["back"];
            for (var n = 1; n <= 4; n++) targets[$"chapter-{n}"] = layout.Points[$"chapter-{n}"];
            var value = Result(CommissionPage.Story) with { Chapter = selected[0] };
            if (read == CommissionRead.Navigation) return value;
            if (read == CommissionRead.Choices) await AddStoryNames(selected[0]);
            return await Resources(value with { Diagnostic = string.Join(",", diagnostic) });
        }
        targets.Clear();
        var daily = Match("daily-title");
        var weekly = !daily && Match("weekly-title");
        if (daily || weekly)
        {
            targets["back"] = layout.Points["back"];
            if (!Match(daily ? "solo-daily" : "solo-weekly", "solo")) return Result(CommissionPage.Unknown);
            var page = daily ? CommissionPage.Daily : CommissionPage.Weekly;
            if (read == CommissionRead.Navigation) return Result(page);
            if (read == CommissionRead.Choices)
            {
                if (daily) await AddDailyNames(); else await AddNames("weekly-list");
                return Result(page);
            }
            var name = await Line(daily ? "daily-name" : "weekly-name");
            var equipment = CommissionOptions.DailyChoices.SingleOrDefault(d => d.Category == "武备获取" && Normalize(d.Name) == Normalize(name));
            var first = layout.Points[daily ? "daily-tier-1" : "weekly-tier-1"];
            var choices = equipment is not null
                ? Enumerable.Range(equipment.MinDifficulty, 6 - equipment.MinDifficulty).Select(n => (n,
                    (equipment.MinDifficulty == 1 ? 1471 : equipment.MinDifficulty == 2 ? 1504 : 1571) + (int)Math.Round((n - equipment.MinDifficulty) * 66.7))).ToArray()
                : Enumerable.Range(1, 6).Select(n => (n, first.X + (int)Math.Round((n - 1) * 66.7))).ToArray();
            var selectedTiers = new List<int>();
            foreach (var (n, x) in choices)
            {
                var region = new PixelRegion(x - 28, first.Y - 27, 55, 55);
                if (templates.Match(frame, daily ? "lock-daily" : "lock-weekly", region) is null)
                    targets[$"tier-{n}"] = new(x, first.Y);
                if ((daily ? new[] { "ring-daily", "ring-boss", "ring-weapon" } : new[] { "ring-weekly" })
                    .Any(id => templates.Match(frame, id, region) is not null)) selectedTiers.Add(n);
            }
            return (await Resources(Result(page))) with { Name = name, Difficulty = selectedTiers.Count == 1 ? selectedTiers[0] : null,
                Cost = Integer(await Line(daily ? "daily-cost" : "weekly-cost")), Resource = RewardResource.Stamina };
        }
        if (Match("crisis-title"))
        {
            targets["back"] = layout.Points["back"];
            if (!Match("crisis-create", "create")) Match("crisis-create-dark", "create");
            Match("crisis-enter", "enter"); Match("crisis-change", "change");
            if (read == CommissionRead.Navigation)
                return Result(targets.ContainsKey("create") || targets.ContainsKey("enter") || targets.ContainsKey("change")
                    ? CommissionPage.Crisis : CommissionPage.Unknown);
            int? tier = null;
            for (var n = 1; n <= 6; n++)
            {
                if (Match($"crisis-tier-{n}", $"tier-{n}")) tier = n;
                else Match($"crisis-glyph-{n}", $"tier-{n}");
            }
            if (read == CommissionRead.Choices) { await AddCrisisNames(); return Result(CommissionPage.Crisis) with { Difficulty = tier }; }
            var name = Regex.Replace(await Line("crisis-name"), @"\d{1,2}:\d{2}:\d{2}", "");
            return (await Resources(Result(CommissionPage.Crisis))) with { Difficulty = tier, Name = name };
        }
        if (Match("hub-title"))
        {
            var regionalSelected = Match("hub-regional-selected");
            var regionalUnselected = Match("hub-regional-unselected");
            var assemblySelected = Match("hub-assembly-selected");
            var assemblyUnselected = Match("hub-assembly-unselected");
            var regional = regionalSelected && !regionalUnselected && assemblyUnselected && !assemblySelected;
            var assembly = assemblySelected && !assemblyUnselected && regionalUnselected && !regionalSelected;
            if (!regional && !assembly) return Result(CommissionPage.Unknown);
            // Fixed cards are authorized only by the title and both mutually exclusive bottom-tab states.
            // Reading decorative card art with OCR is too slow for the one-second input observation budget.
            targets.Clear();
            targets["back"] = layout.Points["back"];
            targets["name:地区委托"] = layout.Points["hub-regional-tab"];
            targets["name:星协集会"] = layout.Points["hub-assembly-tab"];
            if (regional)
            {
                targets["name:日常委托"] = layout.Points["hub-daily"];
                targets["name:往厄残影"] = layout.Points["hub-weekly"];
                targets["name:主线委托"] = layout.Points["hub-story"];
            }
            else targets["name:危机讨伐"] = layout.Points["hub-crisis"];
            return Result(CommissionPage.Hub) with
                { HubSection = regional ? CommissionHubSection.Regional : CommissionHubSection.Assembly };
        }
        targets.Clear();
        var gear = Match("auto-gear");
        var runningText = Match("auto-running");
        if (gear || runningText)
        {
            // Either enabled-state marker prevents toggling F1; the background goal panel is not required.
            targets.Clear();
            return Result(CommissionPage.Battle) with { AutoRunning = true };
        }
        if (Match("auto-icon", "auto"))
        {
            var keyRegion = layout.Regions["auto-key"];
            var keyWords = await Text("auto-key", 3, keyRegion, keepLowConfidence: true);
            var shortcut = IsAutoBattleShortcut(keyWords, keyRegion);
            diagnostic.Add("auto-key-confirmed=" + shortcut);
            if (shortcut || Match("dungeon-goal") || Match("dungeon-goal-crisis") || Match("dungeon-goal-story"))
            {
                // F1 is a key action. Evidence-source changes must not reset otherwise stable battle observations.
                targets.Clear();
                return Result(CommissionPage.Battle) with { AutoRunning = false };
            }
        }
        targets.Clear();
        var common = await shared.ObserveAsync(frame, token);
        if (common.Page == DailyPage.Menu)
        {
            Match("menu-entry", "entry"); return Result(CommissionPage.Menu);
        }
        return Result(common.Page == DailyPage.World ? CommissionPage.World : CommissionPage.Unknown);
    }

    private static RewardResource KeyColor(FrameSnapshot frame, PixelRegion region)
    {
        var blue = 0; var gold = 0;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        for (var x = region.X; x < region.X + region.Width; x++)
        {
            var p = (y * frame.Width + x) * 4;
            var b = frame.Pixels.Span[p]; var g = frame.Pixels.Span[p + 1]; var r = frame.Pixels.Span[p + 2];
            if (b > r + 25 && b > g + 5) blue++;
            if (r > b + 30 && g > b + 12) gold++;
        }
        return blue >= 8 && blue > gold * 2 ? RewardResource.BlueKey
            : gold >= 8 && gold > blue * 2 ? RewardResource.YellowKey : RewardResource.Unknown;
    }
}
