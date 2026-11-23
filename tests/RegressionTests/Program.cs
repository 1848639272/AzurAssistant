using System.Globalization;
using System.IO;
using AzurAssistant.Runtime;

namespace AzurAssistant.Tests;

/// <summary>
/// Offline regression checks for the pure logic (no game, no network). Run with
/// <c>dotnet run --project tests/RegressionTests -c Release</c>; the exit code is the number of failures.
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _skipped;

    [STAThread]
    private static int Main()
    {
        Run("growth data", GrowthDataChecks);
        Run("stamina", StaminaChecks);
        Run("inventory", InventoryChecks);
        Run("export", ExportChecks);
        Run("flow conversion", FlowConversionChecks);
        Run("flow resume", FlowResumeChecks);
        Run("cover boxes", CoverBoxChecks);
        Run("reminders", ReminderChecks);
        Run("duration estimate", DurationChecks);
        Run("crash reporter", CrashChecks);
        Run("repository index", RepositoryChecks);
        Run("screen sampling", ScreenChecks);

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL PASS（跳过 {_skipped} 项环境相关检查）"
            : $"{_failures} FAILURE(S)，跳过 {_skipped} 项");
        return _failures;
    }

    private static void Run(string name, Action body)
    {
        Console.WriteLine($"-- {name}");
        try { body(); }
        catch (Exception ex)
        {
            Fail(name + " 抛出异常", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void Check(string label, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}{(detail.Length > 0 ? "  " + detail : "")}");
        if (!ok) _failures++;
    }

    private static void Skip(string label, string reason)
    {
        Console.WriteLine($"SKIP  {label}  {reason}");
        _skipped++;
    }

    private static void Fail(string label, string detail) => Check(label, false, detail);

    private static string AssetsRoot => Path.Combine(AppContext.BaseDirectory, "assets", "growth");

    private static void GrowthDataChecks()
    {
        var data = GrowthData.Load(Path.Combine(AssetsRoot, "growth-data.json"));
        if (data is null) { Fail("data loads", "growth-data.json 缺失"); return; }
        Check("four kinds", data.Heroes.Count == 23 && data.Pets.Count == 204 && data.Souls.Count == 75
            && data.Equipments.Count == 143,
            $"{data.Heroes.Count}/{data.Pets.Count}/{data.Souls.Count}/{data.Equipments.Count}");

        var hero = data.Heroes.First(entry => entry.Id == "101003");
        var plan = GrowthCalculator.Compute(new GrowthRequest(hero, 1, 0, 100, 1, 13, true),
            data.GoldPerExpFor(GrowthData.Hero), data.ExpItems, data.Icons, Path.Combine(AssetsRoot, "icons"));
        Check("hero needs 829020 exp", plan.Exp == 829020, plan.Exp.ToString(CultureInfo.InvariantCulture));
        Check("hero materials have icons", plan.Materials.Count(row => row.HasIcon) >= plan.Materials.Count - 1,
            $"{plan.Materials.Count} 行");

        var equip = data.Equipments.First(entry => entry.Stages.Count > 3);
        var equipPlan = GrowthCalculator.Compute(new GrowthRequest(equip, 1, 0, 1, 1, equip.MaxStageTarget, false),
            0, data.ExpItems, data.Icons, Path.Combine(AssetsRoot, "icons"));
        Check("equipment has 强化 and 升星",
            equip.Stages.Any(stage => stage.Kind == "enhance") && equip.Stages.Any(stage => stage.Kind == "star"),
            $"{equip.Stages.Count} 段");
        Check("equipment lists copies", equipPlan.Materials.Any(row => row.Name.Contains("本体")));

        var pet = data.Pets.First(entry => entry.Potentials.Count > 0);
        Check("pet potentials parsed", pet.Potentials.Count == 6, pet.Potentials.Count.ToString(CultureInfo.InvariantCulture));
        var potential = pet.Potentials[0];
        Check("potential item count", potential.ItemsBetween(1, 9) == 8, potential.ItemsBetween(1, 9).ToString(CultureInfo.InvariantCulture));
    }

    private static void StaminaChecks()
    {
        var data = GrowthData.Load(Path.Combine(AssetsRoot, "growth-data.json"));
        if (data is null) return;
        Check("farms loaded", data.Farms.Count >= 90, data.Farms.Count.ToString(CultureInfo.InvariantCulture));
        var stamina = new GrowthStamina(data);
        var gold = new GrowthMaterialRow("尤尼", null, "10", "2") { Need = 1000 };
        var described = stamina.Describe(gold);
        Check("gold has a farm rate", described.Contains("体力"), described);
        var (total, runs, _) = stamina.Estimate([gold]);
        Check("stamina totals", total > 0 && runs > 0, $"{total} 体力 / {runs} 次");
        var shop = data.ShopKeys.FirstOrDefault(key => !data.Farms.Any(farm => farm.Yields.Any(yield => yield.Key == key)));
        if (shop is not null)
        {
            var parts = shop.Split(':');
            var probe = new GrowthMaterialRow("probe", null, parts[0], parts[1]) { Need = 5 };
            Check("shop-only label", stamina.Describe(probe) == "商店/家园产出", stamina.Describe(probe));
        }
    }

    private static void InventoryChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), "azur-inventory-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new GrowthInventoryStore(path);
        Check("empty inventory", store.Load().Count == 0);
        store.Save(new Dictionary<string, long>(StringComparer.Ordinal) { ["3:302011"] = 120, ["10:2"] = 0 });
        var loaded = store.Load();
        Check("inventory round trip", loaded.Count == 1 && loaded["3:302011"] == 120, loaded.Count.ToString(CultureInfo.InvariantCulture));
        File.Delete(path);
    }

    private static void ExportChecks()
    {
        var rows = new List<GrowthMaterialRow> { new("尤尼", null, "10", "2") { Need = 1000, Owned = 200 } };
        var snapshot = new GrowthPlanExport.PlanSnapshot("测试 · 对象", "需要经验 1,000", "结晶 × 2", "≈10 体力",
            rows, "潜能「生命」1→9", "2026-01-01 00:00:00");
        var csv = GrowthPlanExport.ToCsv(snapshot);
        Check("csv header", csv.StartsWith("材料,需要,拥有,缺口,体力估算"), csv.Split('\n')[0].Trim());
        Check("csv rows", csv.Contains("尤尼") && csv.Contains("潜能"));
        var bitmap = GrowthPlanExport.ToPng(snapshot);
        Check("png bitmap", bitmap.PixelWidth == 1280 && bitmap.PixelHeight > 200, $"{bitmap.PixelWidth}x{bitmap.PixelHeight}");
    }

    private static void FlowConversionChecks()
    {
        var events = new List<AzurAssistant.Contracts.ActionEvent>
        {
            new() { Us = 0, Kind = AzurAssistant.Contracts.ActionEventKind.KeyDown, Vk = 0x1B },
            new() { Us = 90_000, Kind = AzurAssistant.Contracts.ActionEventKind.LeftDown, X = 960, Y = 540 },
            new() { Us = 200_000, Kind = AzurAssistant.Contracts.ActionEventKind.KeyDown, Vk = 0x7A },
            new() { Us = 300_000, Kind = AzurAssistant.Contracts.ActionEventKind.LeftDown, X = 480, Y = 270 },
        };
        var recording = new AzurAssistant.Contracts.ActionRecording(1, "回归录制", DateTimeOffset.Now, 1920, 1080, 300_000, events);
        var conversion = FlowFromRecording.Convert(recording, "回归流程");
        Check("clicks/keys/skipped", conversion.Clicks == 2 && conversion.Keys == 1 && conversion.SkippedKeys == 1,
            $"{conversion.Clicks}/{conversion.Keys}/{conversion.SkippedKeys}");
        var click = conversion.Flow.Steps.First(step => step.Kind == FlowStep.Click);
        Check("reference coordinates", Math.Abs(click.X - 960) < 0.01 && Math.Abs(click.Y - 540) < 0.01, $"{click.X},{click.Y}");
        Check("key step kind registered", FlowStep.Kinds.Contains(FlowStep.Key));
    }

    private static void FlowResumeChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), "azur-flow-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new FlowProgressStore(path);
        Check("no progress", store.Load() is null);
        store.Save(new FlowProgress { FlowName = "回归流程", NextStep = 3, TotalSteps = 5 });
        var loaded = store.Load();
        Check("progress round trip", loaded is not null && loaded.NextStep == 3, loaded?.Describe ?? "null");
        store.Clear();
        Check("progress cleared", store.Load() is null);

        var host = new FakeFlowHost(failAt: 1);
        var flow = new FlowDefinition(1, "断点", [new(FlowStep.Delay, DurationMs: 1), new(FlowStep.Delay, DurationMs: 1),
            new(FlowStep.Delay, DurationMs: 1)]);
        var reported = new List<int>();
        var result = new FlowExecutor(host).RunAsync(flow, CancellationToken.None, 0, index => reported.Add(index))
            .GetAwaiter().GetResult();
        Check("failure reported", !result.Success && reported.Count == 1, string.Join(",", reported));
        var resumed = new FlowExecutor(new FakeFlowHost(-1)).RunAsync(flow, CancellationToken.None, 2, null)
            .GetAwaiter().GetResult();
        Check("resume runs the rest", resumed.Success && resumed.StepsExecuted == 1, resumed.Message);
    }

    private static void CoverBoxChecks()
    {
        var boxes = CoverBoxes.Parse("78,1026,132,26; 800,900,320,120");
        Check("two boxes", boxes.Count == 2 && boxes[1].Width == 320, CoverBoxes.Format(boxes));
        Check("junk ignored", CoverBoxes.Parse("a,b,c,d;1,2,3").Count == 0);
        Check("clamped", CoverBoxes.Parse("99999,99999,99999,99999")[0].X == 8191);
        Check("max eight", CoverBoxes.Parse(string.Join(";", Enumerable.Repeat("1,2,3,4", 12))).Count == CoverBoxes.MaxBoxes);
        Check("legacy fallback", CoverBoxes.Resolve("", 10, 20, 30, 40)[0].Describe == "10,20 30×40");
        Check("style normalise", CoverStyles.Normalize("MOSAIC") == CoverStyles.Mosaic);
        Check("colour parse", PixelColor.TryParse("#00FF7F", out var red, out var green, out var blue)
            && red == 0 && green == 0xFF && blue == 0x7F);
        Check("colour rejects junk", !PixelColor.TryParse("zzz", out _, out _, out _));
    }

    private static void ReminderChecks()
    {
        var scheduler = new ReminderScheduler(4, 90);
        var noon = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(8));
        Check("next reset", scheduler.NextDailyReset(noon) == new DateTimeOffset(2026, 1, 2, 4, 0, 0, noon.Offset));
        var reset = new DateTimeOffset(2026, 1, 1, 4, 0, 20, noon.Offset);
        Check("due once", scheduler.IsDailyResetDue(reset, null) && !scheduler.IsDailyResetDue(reset, reset));
        Check("stamina interval", !scheduler.IsStaminaDue(noon.AddMinutes(10), noon) && scheduler.IsStaminaDue(noon.AddMinutes(91), noon));
        Check("stamina off", !new ReminderScheduler(4, 0).IsStaminaDue(noon, null));
        Check("format", ReminderScheduler.Format(TimeSpan.FromMinutes(95)) == "1 小时 35 分");
    }

    private static void DurationChecks()
    {
        var now = DateTimeOffset.Now;
        var records = new List<RunRecord>
        {
            new("一条龙", now.AddMinutes(-60), now.AddMinutes(-58), 120, true, "ok"),
            new("一条龙", now.AddMinutes(-30), now.AddMinutes(-27), 180, true, "ok"),
            new("一条龙", now.AddMinutes(-10), now.AddMinutes(-9), 60, false, "fail"),
        };
        Check("average", Math.Abs((TaskDurationEstimator.AverageSeconds(records, "一条龙") ?? 0) - 120) < 0.01);
        Check("no history", TaskDurationEstimator.AverageSeconds(records, "无") is null);
        Check("progress capped", TaskDurationEstimator.Progress(600, 120, true) <= 0.97
            && Math.Abs(TaskDurationEstimator.Progress(60, 120, true) - 0.5) < 0.001);
        Check("remaining text", TaskDurationEstimator.Remaining(60, 200) == "预计还剩 2 分 20 秒",
            TaskDurationEstimator.Remaining(60, 200));
    }

    private static void CrashChecks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "azur-crash-" + Guid.NewGuid().ToString("N"));
        var bundle = CrashReporter.Report(directory, "回归", new InvalidOperationException("模拟"),
            path => { File.WriteAllText(path, "zip"); return path; });
        Check("bundle written", bundle is not null && File.Exists(bundle), bundle ?? "null");
        Check("trace written", Directory.GetFiles(directory, "crash-*.txt").Length == 1);
        Check("factory failure tolerated", CrashReporter.Report(directory, "回归", new Exception("二次"),
            _ => throw new UnauthorizedAccessException("失败")) is null);
        Check("two traces", Directory.GetFiles(directory, "crash-*.txt").Length == 2);
        Directory.Delete(directory, true);
    }

    private static void RepositoryChecks()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "items": [
            { "kind": "flow", "name": "示例流程", "author": "tester", "description": "说明", "url": "https://example.com/a.json", "sha256": "" },
            { "kind": "chain", "name": "示例队列", "author": "tester", "description": "", "url": "https://example.com/b.json" },
            { "kind": "unknown", "name": "坏条目", "url": "not-a-url" }
          ]
        }
        """;
        var index = RepositoryIndex.Parse(json);
        Check("index parsed", index.Items.Count == 2, index.Items.Count.ToString(CultureInfo.InvariantCulture));
        Check("kinds", index.Items.All(item => item.Kind is "flow" or "chain" or "assets"),
            string.Join(",", index.Items.Select(item => item.Kind)));
        Check("bad entries dropped", index.Items.All(item => item.Url.StartsWith("https://", StringComparison.Ordinal)));
        Check("invalid json reported", RepositoryIndex.Parse("{not json}") is { Items.Count: 0 } parsed && parsed.Error is not null);
        Check("target folder", RepositoryTarget.FolderFor("flow") == "flows" && RepositoryTarget.FolderFor("chain") == "chains",
            RepositoryTarget.FolderFor("flow"));
    }

    private static void ScreenChecks()
    {
        var bitmap = AzurAssistant.Platform.ScreenCapture.CaptureMosaic(0, 0, 160, 90, 8);
        if (bitmap is null) Skip("mosaic capture", "当前会话无法截屏");
        else Check("mosaic capture", bitmap.PixelWidth == 20 && bitmap.PixelHeight == 11, $"{bitmap.PixelWidth}x{bitmap.PixelHeight}");
    }

    private sealed class FakeFlowHost(int failAt) : IFlowHost
    {
        private int _index;
        public Task MoveAsync(double x, double y, CancellationToken token) => Task.CompletedTask;
        public Task ClickAsync(double x, double y, CancellationToken token) => Task.CompletedTask;
        public Task PlayActionAsync(string action, CancellationToken token) => Task.CompletedTask;
        public Task PressKeyAsync(string key, CancellationToken token) => Task.CompletedTask;
        public Task DelayAsync(int milliseconds, CancellationToken token)
        {
            var current = _index++;
            if (failAt >= 0 && current == failAt) throw new FlowStepException("模拟失败");
            return Task.Delay(1, token);
        }
        public Task<bool> WaitTemplateAsync(string? pack, string template, int timeoutMs, CancellationToken token) => Task.FromResult(true);
        public Task<bool> TemplateVisibleAsync(string? pack, string template, CancellationToken token) => Task.FromResult(true);
        public void Log(string message) { }
    }
}
