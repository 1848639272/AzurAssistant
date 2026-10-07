using AzurAssistant.Contracts;
using AzurAssistant.Vision;

namespace AzurAssistant.Game.Ui;

public enum DailyPage { Unknown, World, Menu, Mail, MailReward, HomeCenter, Buildings, Ranch, HomeReward, MailEmpty, DiningTable, HomeLevelUp,
    ActivityDay, ActivityWeek, PassTasks, PassRewards, PassLevelUp, PassReward, PassSpecialReward }

public record DailyUiObservation(DailyPage Page, IReadOnlyDictionary<string, ClientPoint> Targets)
{
    public bool Has(string target) => Targets.ContainsKey(target);
    public string Diagnostic { get; init; } = "";
    public string EvidenceKey { get; init; } = "";
}

public interface IDailyUiRecognizer
{
    Task<DailyUiObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token);
}

public sealed class DailyUiRecognizer(TemplateCatalog templates, WorldHudReader hudReader) : IDailyUiRecognizer
{
    public async Task<DailyUiObservation> ObserveAsync(FrameSnapshot frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var observed = ObserveTemplates(frame, out var worldCandidate);
        token.ThrowIfCancellationRequested();
        if (!worldCandidate) return observed;
        var esc = await hudReader.ReadEscAsync(frame, token);
        var enter = await hudReader.ReadEnterAsync(frame, token);
        token.ThrowIfCancellationRequested();
        return observed with
        {
            Page = esc.Point is not null && enter.Point is not null ? DailyPage.World : DailyPage.Unknown,
            Targets = esc.Point is { } point && enter.Point is { } enterPoint ? new Dictionary<string, ClientPoint>
                { ["world-esc"] = point, ["world-enter"] = enterPoint } : observed.Targets,
            Diagnostic = observed.Diagnostic + ", " + esc.Diagnostic + ", " + enter.Diagnostic
        };
    }

    private DailyUiObservation ObserveTemplates(FrameSnapshot frame, out bool worldCandidate)
    {
        worldCandidate = false;
        var targets = new Dictionary<string, ClientPoint>();
        var scores = new List<string>();
        DailyUiObservation Result(DailyPage page) => new(page, page == DailyPage.Unknown ? new Dictionary<string, ClientPoint>() : targets)
            { Diagnostic = string.Join(", ", scores) };
        bool Match(string id)
        {
            var match = templates.Match(frame, id, out var score);
            scores.Add($"{id}={score:F3}{(match is null ? "未命中" : "命中")}");
            if (match is null) return false;
            targets[id] = match.Center;
            return true;
        }

        // Overlays are checked first because the underlying page may remain visible.
        var homeLevel = Match("home-level-title");
        var levelRewards = Match("home-level-rewards");
        if (homeLevel && levelRewards) return Result(DailyPage.HomeLevelUp);
        if (homeLevel || levelRewards) return Result(DailyPage.Unknown);
        var emptyMessage = Match("mail-empty-message");
        var emptyConfirm = Match("mail-empty-confirm");
        if (emptyMessage && emptyConfirm) return Result(DailyPage.MailEmpty);
        if (emptyMessage || emptyConfirm) return Result(DailyPage.Unknown);
        var mailReward = Match("mail-reward");
        var mailDismiss = Match("mail-dismiss");
        if (mailReward && mailDismiss) return Result(DailyPage.MailReward);
        if (mailReward || mailDismiss) return Result(DailyPage.Unknown);
        var homeReward = Match("home-reward");
        var homeClose = Match("home-reward-close");
        if (homeReward && homeClose) return Result(DailyPage.HomeReward);
        if (homeReward || homeClose) return Result(DailyPage.Unknown);
        var diningTitle = Match("dining-title");
        var diningSatiety = Match("dining-satiety");
        if (diningTitle || diningSatiety)
        {
            var diningClose = Match("dining-close");
            if (!diningTitle || !diningSatiety || !diningClose) return Result(DailyPage.Unknown);
            Match("dining-add");
            return Result(DailyPage.DiningTable);
        }
        if (Match("mail-title") && Match("mail-close"))
        {
            Match("mail-claim");
            return Result(DailyPage.Mail);
        }
        if (Match("buildings-title"))
        {
            if (Match("buildings-production")) Match("buildings-claim-label");
            return Result(DailyPage.Buildings);
        }
        if (Match("ranch-title"))
        {
            var claim = Match("ranch-claim");
            var empty = Match("ranch-basket-empty");
            if (claim && empty)
            {
                // Conflicting basket appearances cannot authorize either collection or an empty result.
                targets.Remove("ranch-claim");
                targets.Remove("ranch-basket-empty");
            }
            return Result(DailyPage.Ranch);
        }
        var homeTitle = Match("home-title");
        var dining = Match("home-dining");
        var buildings = Match("home-buildings");
        var ranch = Match("home-ranch");
        if (homeTitle && (dining || buildings || ranch)) return Result(DailyPage.HomeCenter);
        if (Match("menu-mail") && Match("menu-home")) return Result(DailyPage.Menu);
        // Translucent HUD backgrounds invalidate image correlation; full text is the world-page evidence.
        worldCandidate = true;
        return Result(DailyPage.Unknown);
    }
}
