using System.IO;
using AzurAssistant.Runtime;

namespace AzurAssistant.Composition;

public sealed record DailyFeatureChoice(string Id, string Name, Func<AssistantSettings, bool> Selected,
    Func<AssistantSettings, bool, AssistantSettings> SetSelected);

public static class DailyFeatureOrder
{
    public static IReadOnlyList<DailyFeatureChoice> Choices { get; } = Array.AsReadOnly<DailyFeatureChoice>([
        new("claim-mail", "领取邮件", s => s.ClaimMail, (s, v) => s with { ClaimMail = v }),
        new("home-center", "家园中枢", s => s.HomeCenter, (s, v) => s with { HomeCenter = v }),
        new("claim-activity", "日常周常领取", s => s.ClaimActivity, (s, v) => s with { ClaimActivity = v }),
        new("claim-battle-pass", "通行证领取", s => s.ClaimBattlePass, (s, v) => s with { ClaimBattlePass = v }),
        new("weekly-echoes", "往厄残影", s => s.WeeklyEchoes, (s, v) => s with { WeeklyEchoes = v }),
        new("daily-commissions", "日常委托", s => s.DailyCommissions, (s, v) => s with { DailyCommissions = v }),
        new("crisis-raids", "危机讨伐", s => s.CrisisRaids, (s, v) => s with { CrisisRaids = v }),
        new("story-commissions", "主线委托", s => s.StoryCommissions, (s, v) => s with { StoryCommissions = v })]);

    public static IReadOnlyList<DailyFeatureChoice> Resolve(string order)
    {
        var ids = order.Length == 0 ? [] : order.Split(',');
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !Choices.Any(c => c.Id == id)))
            throw new InvalidDataException("一条龙顺序包含重复或未支持的任务ID。");
        return ids.Select(id => Choices.Single(c => c.Id == id)).Concat(Choices.Where(c => !ids.Contains(c.Id))).ToArray();
    }
}
