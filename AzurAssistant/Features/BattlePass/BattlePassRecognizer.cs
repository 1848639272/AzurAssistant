using System.IO;
using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;
using AzurAssistant.Vision;

namespace AzurAssistant.Features.BattlePass;

public sealed class BattlePassRecognizer(IDailyUiRecognizer shared, TemplateCatalog templates) : IDailyUiRecognizer
{
    public static BattlePassRecognizer Load(IDailyUiRecognizer shared) => new(shared,
        new TemplateCatalog(Path.Combine(AppContext.BaseDirectory, "assets", "battle-pass")));

    public async Task<DailyUiObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var targets = new Dictionary<string, ClientPoint>();
        var scores = new List<string>();
        bool Match(string id)
        {
            var found = templates.Match(frame, id, out var score);
            scores.Add($"{id}={score:F3}");
            if (found is null) return false;
            targets[id] = found.Center; return true;
        }
        DailyUiObservation Result(DailyPage page) => new(page, page == DailyPage.Unknown ? new Dictionary<string, ClientPoint>() : targets)
            { Diagnostic = string.Join(",", scores) };
        var level = Match("level-title"); var emblem = Match("level-emblem");
        if (level && emblem) return Result(DailyPage.PassLevelUp);
        if (level || emblem) return Result(DailyPage.Unknown);
        var reward = Match("reward-title"); var dismiss = Match("reward-dismiss");
        if (reward && dismiss) return Result(DailyPage.PassReward);
        if (reward || dismiss) return Result(DailyPage.Unknown);
        var special = Match("special-title"); var specialDismiss = Match("special-dismiss");
        if (special && specialDismiss) return Result(DailyPage.PassSpecialReward);
        if (special || specialDismiss) return Result(DailyPage.Unknown);
        if (Match("title"))
        {
            var tasks = Match("tasks-tab"); var rewards = Match("rewards-tab");
            if (tasks == rewards) return Result(DailyPage.Unknown);
            Match("tasks-label"); Match("rewards-label");
            var claim = Match("claim-all");
            var empty = Match("empty-unlocked");
            // A shifted, recognized purchase-status button is positive evidence of the no-claim layout.
            // Its position is observed only; this task never clicks purchase or level-buying controls.
            if (claim == empty) return Result(DailyPage.Unknown);
            return Result(tasks ? DailyPage.PassTasks : DailyPage.PassRewards);
        }
        var ui = await shared.ObserveAsync(frame, token);
        if (ui.Page == DailyPage.Menu && Match("menu-entry"))
            return ui with { Targets = ui.Targets.Concat(targets.Where(p => p.Key == "menu-entry")).ToDictionary() };
        return ui;
    }
}
