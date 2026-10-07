using AzurAssistant.Contracts;
using AzurAssistant.Game.Ui;

namespace AzurAssistant.Features.Commissions;

internal sealed record CommissionPreparation(CommissionFrame? Ready, int UnitCost, int? WeeklyRemaining,
    bool RequestedTeamSelected, string OriginalTeam, bool OriginalTeamPreserved, string? SkipReason = null);

public sealed class CommissionTask(CommissionKind kind, CommissionOptions options, ICommissionRecognizer recognizer,
    CommissionLayout layout, WeeklyProgressStore weekly, DailyUiTiming? timing = null, TimeSpan? battleTimeout = null,
    TimeSpan? loadingTimeout = null, CommissionActionTiming? actionTiming = null) : IGameTask
{
    public string Id => kind switch { CommissionKind.Daily => "daily-commissions", CommissionKind.Weekly => "weekly-echoes",
        CommissionKind.Story => "story-commissions", _ => "crisis-raids" };
    public string Name => kind switch { CommissionKind.Daily => "日常委托", CommissionKind.Weekly => "往厄残影",
        CommissionKind.Story => "主线委托", _ => "危机讨伐" };
    private CommissionActionTiming ActionTiming => actionTiming ?? CommissionActionTiming.Default;
    private string TeamName => kind == CommissionKind.Story ? options.StoryTeamName : options.TeamName;
    private string SoloName => kind switch { CommissionKind.Weekly => options.WeeklyDungeon,
        CommissionKind.Story => options.StoryDungeon, _ => options.DailyDungeon };
    private int? SoloClaimLimit => kind switch
    {
        CommissionKind.Story when options.StoryClaimLimitEnabled => options.StoryClaimLimit,
        CommissionKind.Daily when options.DailyClaimLimitEnabled => options.DailyClaimLimit,
        CommissionKind.Weekly when options.WeeklyClaimLimitEnabled => options.WeeklyClaimLimit,
        _ => null
    };

    public async Task<TaskResult> ExecuteAsync(TaskContext context, CancellationToken token)
    {
        options.Validate();
        if (kind == CommissionKind.Crisis && !options.CrisisSpecialEnabled && !options.CrisisNormalEnabled)
            return new(true, "危机讨伐：未勾选惊喜或普通副本，已跳过");
        if (kind == CommissionKind.Weekly && weekly.Claimed == 3)
            return new(true, "往厄残影：本周已确认领取 3 次，跳过；切换账号后请重置周本记录");
        var session = new CommissionSession(context, recognizer, layout, timing ?? DailyUiTiming.Default);
        try
        {
            return kind == CommissionKind.Crisis ? await RunCrisisAsync(session, context, token) : await RunSoloAsync(session, context, token);
        }
        catch (CommissionStepException error) { return new(false, Name + "未完成：" + error.Message); }
    }

    private async Task<TaskResult> RunSoloAsync(CommissionSession s, TaskContext context, CancellationToken token)
    {
        var isWeekly = kind == CommissionKind.Weekly;
        var preparation = kind == CommissionKind.Story ? await PrepareStoryAsync(s, context, token)
            : await PrepareSoloAsync(s, context, token, updateWeeklyProgress: true);
        if (preparation.Ready is null) return new(true, preparation.SkipReason!);
        var cost = preparation.UnitCost;
        await s.ClickAsync(preparation.Ready, "start", "开始委托", token);
        var claims = 0;
        int? lastRemainingStamina = null;
        int? lastWeeklyRemaining = null;
        for (var run = 0; run < 40; run++)
        {
            await FightAsync(s, token);
            var reward = await RewardAsync(s, RewardResource.Stamina, isWeekly, token);
            if (reward.Ui.Cost / reward.Ui.Multiplier != cost)
                throw new CommissionStepException("宝箱单次消耗与所选副本不一致，未领取");
            if (lastRemainingStamina is { } previous && reward.Ui.Stamina > previous + 2)
                throw new CommissionStepException("上次领取后体力未按预期减少或外部资源发生变化，未重复领取");
            if (isWeekly && lastWeeklyRemaining is { } expectedWeekly && reward.Ui.WeeklyRemaining != expectedWeekly)
                throw new CommissionStepException("本周剩余次数与上次结算不一致，未重复领取；请重新进入核对");
            if (isWeekly) weekly.ObserveRemaining(reward.Ui.WeeklyRemaining!.Value);
            var multiplier = LimitMultiplier(CommissionResourcePlan.Multiplier(reward.Ui.Stamina!.Value, cost, isWeekly), SoloClaimLimit, claims);
            if (isWeekly && reward.Ui.WeeklyRemaining == 0) multiplier = 0;
            if (multiplier == 0)
            {
                reward = await s.DelayAndObserveAsync("确认不领取页面已稳定", ActionTiming.ResultDelay,
                    u => u.Page == CommissionPage.Reward && u.Has("skip"), token);
                await s.ClickAsync(reward, "skip", "资源不足或达到领取上限，不领取并结算", token);
                var noClaim = await ResultReadyAsync(s, "等待不领取结算", token, CommissionPage.Result);
                await s.ClickAsync(noClaim, "return", "返回委托菜单", token);
                await ReturnWorldAsync(s, token);
                return new(true, $"{Name}完成，已领取 {claims} 次；剩余资源不足");
            }
            reward = await SelectMultiplierAsync(s, reward, multiplier, cost, RewardResource.Stamina, isWeekly, token);
            var remaining = reward.Ui.Stamina!.Value - cost * multiplier;
            var weeklyLeft = isWeekly ? reward.Ui.WeeklyRemaining!.Value - 1 : 0;
            await s.ClickAsync(reward, "claim", $"领取并结算（{multiplier} 倍，消耗 {cost * multiplier} 体力）", token);
            var result = await ResultReadyAsync(s, "确认领取结算", token, CommissionPage.Result);
            claims += multiplier;
            context.Log($"本次消耗 {cost * multiplier} 体力，剩余体力 {remaining}（按领取前 {reward.Ui.Stamina} 计算）；本次已领取 {claims} 份"
                + (SoloClaimLimit is { } limit ? $" / 上限 {limit} 份" : ""));
            lastRemainingStamina = remaining;
            lastWeeklyRemaining = weeklyLeft;
            if (isWeekly) weekly.ObserveRemaining(weeklyLeft);
            if (remaining < cost || isWeekly && weeklyLeft == 0 || SoloClaimLimit is { } maximum && claims >= maximum)
            {
                await s.ClickAsync(result, "return", "返回委托菜单", token);
                await ReturnWorldAsync(s, token);
                return new(true, $"{Name}完成，已确认领取 {claims} 次并返回大世界");
            }
            await s.ClickAsync(result, "again", "剩余资源可领取，继续挑战", token);
        }
        throw new CommissionStepException("达到单项 40 场上限，未继续挑战");
    }

    /// <summary>Reuses preparation only; this path cannot start combat or update the weekly cache.</summary>
    internal async Task<CommissionPreparation> PrepareOnlyAsync(TaskContext context, CancellationToken token)
    {
        if (kind is not (CommissionKind.Daily or CommissionKind.Weekly or CommissionKind.Story) || string.IsNullOrWhiteSpace(TeamName))
            throw new ArgumentException("委托预检仅支持日常、往厄残影或主线，且必须指定非空队伍名称。");
        options.Validate();
        var session = new CommissionSession(context, recognizer, layout, timing ?? DailyUiTiming.Default);
        var result = kind == CommissionKind.Story ? await PrepareStoryAsync(session, context, token)
            : await PrepareSoloAsync(session, context, token, updateWeeklyProgress: false);
        if (result.Ready is null || !result.RequestedTeamSelected) return result;
        var name = SoloName;
        var ready = await session.WaitAsync("预检确认指定队伍仍在准备页面", u => u.Page == CommissionPage.Prepare
            && Equal(u.Name, name) && Equal(u.Team, TeamName), token);
        return result with { Ready = ready };
    }

    private async Task<CommissionPreparation> PrepareSoloAsync(CommissionSession s, TaskContext context,
        CancellationToken token, bool updateWeeklyProgress)
    {
        var isWeekly = kind == CommissionKind.Weekly;
        var page = isWeekly ? CommissionPage.Weekly : CommissionPage.Daily;
        var name = isWeekly ? options.WeeklyDungeon : options.DailyDungeon;
        var tier = isWeekly ? options.WeeklyDifficulty : options.DailyDifficulty;
        await OpenCatalogAsync(s, page, token);
        if (!isWeekly)
        {
            var category = CommissionOptions.DailyChoices.Single(d => d.Name == name).Category;
            var tabs = await s.WaitAsync("读取日常委托分类", u => u.Page == page && u.Has(NameKey(category)), token, CommissionRead.Choices);
            await s.ClickAsync(tabs, NameKey(category), "选择" + category, token);
        }
        var selected = await ChooseNameAsync(s, page, name, token);
        selected = await SelectTierAsync(s, page, tier, token, selected);
        bool Ready(CommissionObservation u) => u.Page == page && Equal(u.Name, name) && u.Difficulty == tier
            && u.Stamina is not null && u.Cost is > 0 && (!isWeekly || u.WeeklyRemaining is not null);
        if (!Ready(selected.Ui)) selected = await s.WaitAsync("核对副本、难度与资源", Ready, token);
        if (isWeekly && updateWeeklyProgress) weekly.ObserveRemaining(selected.Ui.WeeklyRemaining!.Value);
        var cost = selected.Ui.Cost!.Value;
        if (selected.Ui.Stamina < cost || isWeekly && selected.Ui.WeeklyRemaining == 0)
        {
            await ReturnWorldAsync(s, token);
            return new(null, cost, selected.Ui.WeeklyRemaining, false, "", false,
                Name + "：体力或本周剩余次数不足，已跳过并返回大世界");
        }
        await s.ClickAsync(selected, "solo", "单人挑战", token);
        var ready = await s.PageAsync("确认准备页面或退队提示", token, CommissionPage.Prepare, CommissionPage.LeaveParty);
        if (ready.Ui.Page == CommissionPage.LeaveParty)
        {
            await s.ClickAsync(ready, "confirm", "确认退出当前小队并进入单人挑战", token);
            ready = await s.PageAsync("等待单人准备页面", token, CommissionPage.Prepare);
        }
        var originalTeam = ready.Ui.Team;
        var teamSelected = false;
        if (!string.IsNullOrWhiteSpace(TeamName))
            (ready, teamSelected) = await SelectTeamAsync(s, context, ready, token);
        ready = await s.WaitAsync("确认准备页仍为所选副本和队伍", u => u.Page == CommissionPage.Prepare
            && Equal(u.Name, name) && (!teamSelected || Equal(u.Team, TeamName)), token);
        return new(ready, cost, selected.Ui.WeeklyRemaining, teamSelected, originalTeam,
            !string.IsNullOrWhiteSpace(originalTeam) && Equal(originalTeam, ready.Ui.Team));
    }

    private async Task<CommissionPreparation> PrepareStoryAsync(CommissionSession s, TaskContext context, CancellationToken token)
    {
        await OpenCatalogAsync(s, CommissionPage.Story, token);
        var chapter = await s.PageAsync("读取主线章节", token, CommissionPage.Story);
        if (chapter.Ui.Chapter != options.StoryChapter)
        {
            await s.ClickAsync(chapter, "chapter-" + options.StoryChapter, $"选择主线第 {options.StoryChapter} 章", token);
            await s.WaitAsync("确认主线章节", u => u.Page == CommissionPage.Story && u.Chapter == options.StoryChapter, token);
        }
        var target = await FindNameAsync(s, CommissionPage.Story, options.StoryDungeon, token)
            ?? throw new CommissionStepException($"未找到主线关卡「{options.StoryDungeon}」");
        if (target.Ui.Chapter != options.StoryChapter) throw new CommissionStepException("主线章节发生变化，未选择关卡");
        await s.ClickAsync(target, NameKey(options.StoryDungeon), "选择主线关卡「" + options.StoryDungeon + "」", token);
        var ready = await s.PageAsync("确认主线准备页", token, CommissionPage.Prepare, CommissionPage.LeaveParty);
        if (ready.Ui.Page == CommissionPage.LeaveParty)
        {
            await s.ClickAsync(ready, "confirm", "确认退出当前队伍进入单人准备", token);
        }
        ready = await s.WaitAsync("核对主线关卡及体力", u => u.Page == CommissionPage.Prepare
            && Equal(u.Name, options.StoryDungeon) && u.Stamina is not null && u.Cost is > 0, token);
        var cost = ready.Ui.Cost!.Value;
        if (ready.Ui.Stamina < cost)
        {
            await s.KeyAsync(ready, GameKey.Escape, "体力不足，返回主线目录", token);
            await ReturnWorldAsync(s, token);
            return new(null, cost, null, false, "", false, "主线委托：体力不足，已跳过并返回大世界");
        }
        var originalTeam = ready.Ui.Team;
        var teamSelected = false;
        if (!string.IsNullOrWhiteSpace(TeamName))
            (ready, teamSelected) = await SelectTeamAsync(s, context, ready, token);
        ready = await s.WaitAsync("确认主线准备页仍为指定关卡和队伍", u => u.Page == CommissionPage.Prepare
            && Equal(u.Name, options.StoryDungeon) && u.Cost == cost && u.Stamina >= cost
            && (!teamSelected || Equal(u.Team, TeamName)), token);
        return new(ready, cost, null, teamSelected, originalTeam,
            !string.IsNullOrWhiteSpace(originalTeam) && Equal(originalTeam, ready.Ui.Team));
    }

    private async Task<TaskResult> RunCrisisAsync(CommissionSession s, TaskContext context, CancellationToken token)
    {
        var claims = 0;
        var unavailable = new List<string>();
        var resources = new List<RewardResource>();
        if (options.CrisisSpecialEnabled) resources.Add(RewardResource.BlueKey);
        if (options.CrisisNormalEnabled) resources.Add(RewardResource.YellowKey);
        if (!options.CrisisSpecialEnabled) context.Log("未开启惊喜副本，仅刷普通副本，保留蓝色异辉晶钥石");
        if (!options.CrisisNormalEnabled) context.Log("未开启普通副本，仅刷惊喜副本，保留黄色晶钥石");
        foreach (var resource in resources)
        {
            var name = resource == RewardResource.BlueKey ? options.CrisisSpecial : options.CrisisNormal;
            int? limit = resource == RewardResource.BlueKey
                ? options.CrisisSpecialClaimLimitEnabled ? options.CrisisSpecialClaimLimit : null
                : options.CrisisNormalClaimLimitEnabled ? options.CrisisNormalClaimLimit : null;
            var resourceName = resource == RewardResource.BlueKey ? "蓝色异辉晶钥石" : "黄色晶钥石";
            var typeName = resource == RewardResource.BlueKey ? "惊喜副本" : "普通副本";
            var typeClaims = 0;
            int? expectedRemaining = null;
            for (var run = 0; run < 40; run++)
            {
                if (limit is { } maximum && typeClaims >= maximum) break;
                var stock = await OpenCrisisCatalogAsync(s, token);
                var available = resource == RewardResource.BlueKey ? stock.Ui.BlueKeys!.Value : stock.Ui.YellowKeys!.Value;
                if (expectedRemaining is { } expected && available != expected)
                    throw new CommissionStepException("结算后晶钥石数量与预期不一致，停止后续领取");
                if (available == 0) { context.Log($"{resourceName} 已用完，结束{typeName}"); break; }
                var selected = await TrySelectCrisisTargetAsync(s, name, token);
                if (selected is null)
                { unavailable.Add(name); context.Log($"未找到指定副本「{name}」，停止该类查找，继续其他已配置副本"); break; }
                var entry = await PrepareCrisisEntryAsync(s, selected, token);
                if (entry.Ui.Page == CommissionPage.Crisis) await s.ClickAsync(entry, "enter", "进入副本", token);
                else await s.ClickAsync(entry, "launch", "开始讨伐", token);
                await FightAsync(s, token);
                var reward = await RewardAsync(s, resource, false, token);
                available = resource == RewardResource.BlueKey ? reward.Ui.BlueKeys!.Value : reward.Ui.YellowKeys!.Value;
                var unitCost = reward.Ui.Cost!.Value / reward.Ui.Multiplier!.Value;
                if (unitCost != 1) throw new CommissionStepException("晶钥石单次消耗不是已确认的 1 个，未领取");
                var multiplier = LimitMultiplier(CommissionResourcePlan.Multiplier(available, unitCost), limit, typeClaims);
                if (multiplier == 0) throw new CommissionStepException("进入后晶钥石不足，未领取；请手动退出副本");
                reward = await SelectMultiplierAsync(s, reward, multiplier, unitCost, resource, false, token);
                available = resource == RewardResource.BlueKey ? reward.Ui.BlueKeys!.Value : reward.Ui.YellowKeys!.Value;
                await s.ClickAsync(reward, "claim", $"领取并结算（{multiplier} 倍）", token);
                var result = await ResultReadyAsync(s, "确认讨伐奖励结算", token, CommissionPage.CrisisResult);
                expectedRemaining = available - multiplier;
                claims += multiplier;
                typeClaims += multiplier;
                context.Log($"{typeName}本次消耗 {multiplier} 个{resourceName}，结算后预计剩余 {expectedRemaining} 个；已领取 {typeClaims} 份"
                    + (limit is { } maximumClaims ? $" / 上限 {maximumClaims} 份" : ""));
                await s.ClickAsync(result, "return", "返回并重新核对资源", token);
                await ReturnWorldAsync(s, token);
                if (limit is { } completedLimit && typeClaims >= completedLimit) break;
                if (run == 39) throw new CommissionStepException("达到单类 40 场上限");
            }
            await ReturnWorldAsync(s, token);
        }
        return new(unavailable.Count == 0, $"危机讨伐已确认领取 {claims} 次，已返回大世界" +
            (unavailable.Count == 0 ? "" : "；未找到并跳过：" + string.Join("、", unavailable)));
    }

    /// <summary>Observes and selects the configured normal target, stopping in its catalog without changing a party or entering.</summary>
    internal async Task<CommissionFrame> PrepareCrisisCatalogOnlyAsync(TaskContext context, CancellationToken token)
    {
        if (kind != CommissionKind.Crisis) throw new ArgumentException("危机目录预检仅支持危机讨伐。");
        options.Validate();
        var session = new CommissionSession(context, recognizer, layout, timing ?? DailyUiTiming.Default);
        await OpenCrisisCatalogAsync(session, token);
        var selected = await TrySelectCrisisTargetAsync(session, options.CrisisNormal, token)
            ?? throw new CommissionStepException($"未找到指定副本「{options.CrisisNormal}」，危机目录预检未完成");
        return await session.WaitAsync("预检确认危机目录仍为所选目标", u => u.Page == CommissionPage.Crisis
            && Equal(u.Name, options.CrisisNormal) && u.Difficulty == options.CrisisDifficulty
            && (u.Has("create") || u.Has("enter") || u.Has("change")), token);
    }

    /// <summary>Prepares the normal target and its party, stopping before entering or launching combat.</summary>
    internal async Task<CommissionFrame> PrepareCrisisOnlyAsync(TaskContext context, CancellationToken token)
    {
        if (kind != CommissionKind.Crisis) throw new ArgumentException("危机准备预检仅支持危机讨伐。");
        options.Validate();
        var session = new CommissionSession(context, recognizer, layout, timing ?? DailyUiTiming.Default);
        await OpenCrisisCatalogAsync(session, token);
        var selected = await TrySelectCrisisTargetAsync(session, options.CrisisNormal, token)
            ?? throw new CommissionStepException($"未找到指定副本「{options.CrisisNormal}」，危机准备预检未完成");
        return await PrepareCrisisEntryAsync(session, selected, token);
    }

    private static async Task<CommissionFrame> PrepareCrisisEntryAsync(CommissionSession s, CommissionFrame selected,
        CancellationToken token)
    {
        var name = selected.Ui.Name;
        var tier = selected.Ui.Difficulty;
        if (selected.Ui.Has("change"))
        {
            await s.ClickAsync(selected, "change", "更换目标副本", token);
            selected = await s.WaitAsync("确认已更换目标", u => u.Page == CommissionPage.Crisis
                && Equal(u.Name, name) && u.Difficulty == tier && u.Has("enter"), token);
        }
        if (selected.Ui.Has("enter")) return selected;
        if (selected.Ui.Has("create"))
        {
            await s.ClickAsync(selected, "create", "创建讨伐队伍", token);
            return await s.WaitAsync("等待讨伐队伍页面与开始按钮", u => u.Page == CommissionPage.Lobby && u.Has("launch"), token);
        }
        throw new CommissionStepException("没有创建队伍或进入副本按钮，未进行匹配");
    }

    private async Task<CommissionFrame> OpenCrisisCatalogAsync(CommissionSession s, CancellationToken token)
    {
        await OpenCatalogAsync(s, CommissionPage.Crisis, token);
        var selected = await SelectTierAsync(s, CommissionPage.Crisis, options.CrisisDifficulty, token);
        if (selected.Ui.BlueKeys is not null && selected.Ui.YellowKeys is not null) return selected;
        return await s.WaitAsync("读取晶钥石", u => u.Page == CommissionPage.Crisis
            && u.BlueKeys is not null && u.YellowKeys is not null, token);
    }

    private async Task<CommissionFrame?> TrySelectCrisisTargetAsync(CommissionSession s, string name, CancellationToken token)
    {
        var selected = await TryChooseNameAsync(s, CommissionPage.Crisis, name, token);
        if (selected is null) return null;
        if (selected.Ui.Difficulty == options.CrisisDifficulty) return selected;
        return await s.WaitAsync("核对讨伐目标与难度", u => u.Page == CommissionPage.Crisis && Equal(u.Name, name)
            && u.Difficulty == options.CrisisDifficulty, token);
    }

    private async Task FightAsync(CommissionSession s, CancellationToken token)
    {
        s.Log("等待副本加载并确认自动战斗状态");
        var battle = await s.WaitAsync("等待副本加载与自动战斗按钮", u => u.Page is CommissionPage.Battle or CommissionPage.Reward or CommissionPage.LeaveParty,
            token, timeout: loadingTimeout ?? TimeSpan.FromSeconds(90));
        if (battle.Ui.Page == CommissionPage.LeaveParty)
        {
            await s.ClickAsync(battle, "confirm", "确认退出当前队伍并进入所选关卡", token);
            battle = await s.WaitAsync("等待单人副本加载", u => u.Page is CommissionPage.Battle or CommissionPage.Reward,
                token, timeout: loadingTimeout ?? TimeSpan.FromSeconds(90));
        }
        if (battle.Ui.Page == CommissionPage.Reward) return;
        if (battle.Ui.AutoRunning)
            s.Log("已确认游戏内自动战斗正在运行，继续等待结算");
        else
        {
            battle = await s.DelayAndObserveAsync("等待自动战斗按钮可操作", ActionTiming.AutoBattleDelay,
                u => u.Page is CommissionPage.Battle or CommissionPage.Reward, token);
            if (battle.Ui.Page == CommissionPage.Reward) return;
            if (!battle.Ui.AutoRunning)
            {
                await s.KeyAsync(battle, GameKey.AutoBattle, "按 F1 启用游戏内自动战斗", token);
                await s.WaitAsync("确认自动战斗已启用（需该难度三星通关）", u => u.Page == CommissionPage.Reward
                    || u.Page == CommissionPage.Battle && u.AutoRunning, token);
            }
            else s.Log("已确认游戏内自动战斗正在运行，继续等待结算");
        }
        await s.WaitAsync("等待游戏自动战斗与宝箱", u => u.Page == CommissionPage.Reward, token,
            timeout: battleTimeout ?? TimeSpan.FromMinutes(10), autoBattlePollInterval: ActionTiming.AutoBattlePollInterval);
    }

    private static Task<CommissionFrame> RewardAsync(CommissionSession s, RewardResource resource, bool weekly, CancellationToken token)
        => s.WaitAsync("核对宝箱消耗与可用资源", u => u.Page == CommissionPage.Reward && u.Resource == resource
            && u.Cost is > 0 && u.Multiplier is >= 1 and <= 3 && u.Cost % u.Multiplier == 0
            && (resource == RewardResource.Stamina ? u.Stamina is not null : u.BlueKeys is not null && u.YellowKeys is not null)
            && (!weekly || u.WeeklyRemaining is not null && u.Multiplier == 1), token);

    private async Task<CommissionFrame> SelectMultiplierAsync(CommissionSession s, CommissionFrame reward, int multiplier,
        int unitCost, RewardResource resource, bool weekly, CancellationToken token)
    {
        if (reward.Ui.Multiplier != multiplier)
        {
            await s.ClickAsync(reward, "multiplier", "打开领取倍数", token);
            var dropdown = await s.PageAsync("确认倍数列表", token, CommissionPage.Multiplier);
            await s.ClickAsync(dropdown, $"multiplier-{multiplier}", $"选择 {multiplier} 倍领取", token);
        }
        reward = await RewardAsync(s, resource, weekly, token);
        var before = Available(reward.Ui, resource);
        var weeklyBefore = reward.Ui.WeeklyRemaining;
        reward = await s.DelayAndObserveAsync("等待领取按钮稳定并重新核对资源", ActionTiming.ResultDelay,
            u => u.Page == CommissionPage.Reward && u.Resource == resource && u.Multiplier == multiplier
                && u.Cost == unitCost * multiplier && (!weekly || u.WeeklyRemaining == weeklyBefore), token);
        var available = resource switch { RewardResource.Stamina => reward.Ui.Stamina, RewardResource.BlueKey => reward.Ui.BlueKeys, _ => reward.Ui.YellowKeys };
        if (reward.Ui.Multiplier != multiplier || reward.Ui.Cost != unitCost * multiplier || available is null
            || available < reward.Ui.Cost || before is null || available < before || available > before + (resource == RewardResource.Stamina ? 2 : 0))
            throw new CommissionStepException("领取倍数未生效、消耗不一致或可用资源发生变化，未点击领取");
        return reward;
    }

    private async Task<CommissionFrame> ResultReadyAsync(CommissionSession s, string step, CancellationToken token, CommissionPage page)
    {
        bool IsSettlement(CommissionObservation ui) => ui.Page == page || ui.Page == CommissionPage.BondLevelUp
            || page == CommissionPage.CrisisResult && ui.Page == CommissionPage.Mvp;
        var current = await s.WaitAsync(step, IsSettlement, token);
        var bondDismissals = 0;
        var mvpDismissed = false;
        // A new stable observation is required after each overlay; never re-enter reward claiming here.
        for (var transition = 0; transition < 12; transition++)
        {
            if (current.Ui.Page == CommissionPage.BondLevelUp)
            {
                if (bondDismissals >= 3)
                    throw new CommissionStepException("牵绊提升提示关闭 3 次后仍未结束，未继续点击或重复领取");
                current = await s.DelayAndObserveAsync("等待牵绊提升画面稳定", ActionTiming.ResultDelay, IsSettlement, token);
                if (current.Ui.Page != CommissionPage.BondLevelUp) continue;
                await s.ClickAsync(current, "dismiss", $"点击空白关闭牵绊提升提示（第 {bondDismissals + 1} 次）", token);
                bondDismissals++;
                current = await s.WaitAsync(step + "：重新确认牵绊提示或结算", IsSettlement, token);
                continue;
            }
            if (current.Ui.Page == CommissionPage.Mvp)
            {
                if (mvpDismissed) throw new CommissionStepException("MVP 展示离开后再次出现，未继续点击或重复领取");
                current = await s.DelayAndObserveAsync("等待 MVP 展示稳定", ActionTiming.MvpDelay, IsSettlement, token);
                if (current.Ui.Page != CommissionPage.Mvp) continue;
                await s.ClickAsync(current, "dismiss", "点击空白关闭 MVP 展示", token, ActionTiming.ResultDelay);
                mvpDismissed = true;
                current = await s.WaitAsync(step + "：重新确认牵绊提示或结算", IsSettlement, token);
                // Only this non-consuming overlay may be clicked a second time, after fresh confirmation.
                // The short settlement delay replaces the usual post-click delay rather than adding to it.
                if (current.Ui.Page == CommissionPage.Mvp)
                {
                    await s.ClickAsync(current, "dismiss", "MVP 展示仍在，补点关闭（第 2 次，共 2 次）", token, ActionTiming.ResultDelay);
                    current = await s.WaitAsync(step + "：确认 MVP 展示已关闭", IsSettlement, token);
                    if (current.Ui.Page == CommissionPage.Mvp)
                        throw new CommissionStepException("MVP 展示点击 2 次后仍未关闭，未继续点击或重复领取");
                }
                continue;
            }
            current = await s.DelayAndObserveAsync(step + "：等待按钮稳定", ActionTiming.ResultDelay, IsSettlement, token);
            if (current.Ui.Page == page) return current;
        }
        throw new CommissionStepException("结算画面反复切换，未确认稳定结算页，停止该项");
    }

    private static int? Available(CommissionObservation ui, RewardResource resource) => resource switch
    { RewardResource.Stamina => ui.Stamina, RewardResource.BlueKey => ui.BlueKeys, RewardResource.YellowKey => ui.YellowKeys, _ => null };

    private static int LimitMultiplier(int affordable, int? limit, int claimed)
        => limit is { } maximum ? Math.Min(affordable, Math.Max(0, maximum - claimed)) : affordable;

    private async Task<(CommissionFrame Ready, bool Selected)> SelectTeamAsync(CommissionSession s, TaskContext context,
        CommissionFrame prepared, CancellationToken token)
    {
        await s.ClickAsync(prepared, "teams", "打开队伍列表", token);
        var row = await FindNameAsync(s, CommissionPage.Teams, TeamName, token);
        if (row is not null)
        {
            await s.ClickAsync(row, NameKey(TeamName), "选择队伍「" + TeamName + "」", token);
            var selected = await s.WaitAsync("确认所选队伍", u => (u.Page is CommissionPage.Teams or CommissionPage.Prepare)
                && Equal(u.Team, TeamName), token, CommissionRead.Choices);
            if (selected.Ui.Page == CommissionPage.Prepare) return (selected, true);
            await s.ClickAsync(selected, "teams", "关闭队伍列表", token);
            return (await s.WaitAsync("确认准备页面中的指定队伍", u => u.Page == CommissionPage.Prepare
                && Equal(u.Team, TeamName), token), true);
        }
        context.Log($"未找到队伍「{TeamName}」，保持当前队伍");
        var list = await s.PageAsync("确认队伍列表关闭按钮", token, CommissionPage.Teams);
        await s.ClickAsync(list, "teams", "关闭队伍列表", token);
        return (await s.PageAsync("返回委托准备页面", token, CommissionPage.Prepare), false);
    }

    private static async Task OpenCatalogAsync(CommissionSession s, CommissionPage page, CancellationToken token)
    {
        var current = await s.PageAsync("确认委托入口起点", token, CommissionPage.World, CommissionPage.Menu, CommissionPage.Hub, page);
        if (current.Ui.Page == page) return;
        if (current.Ui.Page == CommissionPage.World)
        {
            await s.KeyAsync(current, GameKey.Escape, "打开主菜单", token);
            current = await s.PageAsync("确认主菜单", token, CommissionPage.Menu);
        }
        if (current.Ui.Page == CommissionPage.Menu)
        {
            await s.ClickAsync(current, "entry", "打开委托", token);
            current = await s.PageAsync("等待委托入口页", token, CommissionPage.Hub);
        }
        var name = page switch { CommissionPage.Daily => "日常委托", CommissionPage.Weekly => "往厄残影",
            CommissionPage.Story => "主线委托", _ => "危机讨伐" };
        if (!current.Ui.Has(NameKey(name)))
        {
            var tab = page == CommissionPage.Crisis ? "星协集会" : "地区委托";
            await s.ClickAsync(current, NameKey(tab), "切换" + tab, token);
            current = await s.WaitAsync("按文字确认" + name + "入口", u => u.Page == CommissionPage.Hub && u.Has(NameKey(name)), token);
        }
        await s.ClickAsync(current, NameKey(name), "进入" + name, token);
        await s.WaitAsync("确认副本选择页", u => u.Page == page, token, CommissionRead.Navigation);
    }

    private static async Task<CommissionFrame> SelectTierAsync(CommissionSession s, CommissionPage page, int tier, CancellationToken token,
        CommissionFrame? initial = null)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = attempt == 0 && initial is not null ? initial : await s.PageAsync("读取当前难度", token, page);
            if (current.Ui.Difficulty == tier) return current;
            if (current.Ui.Has($"tier-{tier}"))
            {
                await s.ClickAsync(current, $"tier-{tier}", $"选择难度 {tier}", token);
                return await s.WaitAsync("确认难度选择已生效", u => u.Page == page && u.Difficulty == tier, token);
            }
            if (page != CommissionPage.Crisis) throw new CommissionStepException("所选难度未解锁或未识别，未进入挑战");
            await s.DragAsync(current, tier >= 5 ? "tier-up" : "tier-down", "滚动难度列表", token);
        }
        throw new CommissionStepException("滚动后仍未找到指定难度");
    }

    private static async Task<CommissionFrame> ChooseNameAsync(CommissionSession s, CommissionPage page, string name, CancellationToken token)
        => await TryChooseNameAsync(s, page, name, token) ?? throw new CommissionStepException($"未找到指定副本「{name}」");

    private static async Task<CommissionFrame?> TryChooseNameAsync(CommissionSession s, CommissionPage page, string name, CancellationToken token)
    {
        var current = await s.PageAsync("读取所选副本", token, page);
        if (Equal(current.Ui.Name, name)) return current;
        var target = await FindNameAsync(s, page, name, token);
        if (target is null) return null;
        await s.ClickAsync(target, NameKey(name), "选择副本「" + name + "」", token);
        return await s.WaitAsync("核对副本名称", u => u.Page == page && Equal(u.Name, name), token);
    }
    private static async Task<CommissionFrame?> FindNameAsync(CommissionSession s, CommissionPage page, string name, CancellationToken token)
    {
        var directions = page switch
        {
            CommissionPage.Daily => new[] { "list-left", "list-right" },
            CommissionPage.Story => new[] { "story-left", "story-right" },
            CommissionPage.Crisis => new[] { "crisis-up", "crisis-down" },
            CommissionPage.Teams => new[] { "team-up", "team-down" },
            _ => Array.Empty<string>()
        };
        var current = await s.WaitAsync("读取可见列表", u => u.Page == page, token, CommissionRead.Choices);
        if (current.Ui.Has(NameKey(name))) return current;
        var visible = VisibleNames(current.Ui);
        s.Log($"查找「{name}」：可见名称 [{string.Join("、", visible.Keys)}]");
        if (visible.Count == 0) throw new CommissionStepException("列表名称未识别，无法确认滚动覆盖，停止查找「" + name + "」");
        foreach (var direction in directions)
        for (var scroll = 0; scroll < 8; scroll++)
        {
            var before = visible;
            await s.DragAsync(current, direction, "滚动查找「" + name + "」", token);
            current = await s.WaitAsync("重新识别滚动后的列表", u => u.Page == page, token, CommissionRead.Choices);
            visible = VisibleNames(current.Ui);
            var common = before.Keys.Intersect(visible.Keys, StringComparer.Ordinal).ToArray();
            s.Log($"查找「{name}」{direction} 第 {scroll + 1} 段：滚动前 [{string.Join("、", before.Keys)}]；"
                + $"滚动后 [{string.Join("、", visible.Keys)}]；共同名称 [{string.Join("、", common)}]");
            if (visible.Count == 0) throw new CommissionStepException("列表名称未识别，无法确认滚动覆盖，停止查找「" + name + "」");
            if (common.Length == 0) throw new CommissionStepException("列表未确认重叠，可能跳过中间项，停止查找「" + name + "」");
            if (current.Ui.Has(NameKey(name))) return current;
            if (ListKey(visible) == ListKey(before)) break;
        }
        return null;
    }
    private static Dictionary<string, ClientPoint> VisibleNames(CommissionObservation ui) => ui.Targets
        .Where(t => t.Key.StartsWith("name:", StringComparison.Ordinal)
            && (ui.Page != CommissionPage.Daily || CommissionOptions.DailyChoices.Any(d => NameKey(d.Name) == t.Key))
            && (ui.Page != CommissionPage.Story || ui.Chapter is { } chapter
                && CommissionOptions.GetStoryChoices(chapter).Any(name => NameKey(name) == t.Key)))
        .ToDictionary(t => t.Key[5..], t => t.Value, StringComparer.Ordinal);
    private static string ListKey(IReadOnlyDictionary<string, ClientPoint> names) => string.Join("|", names
        .OrderBy(t => t.Key).Select(t => $"{t.Key}:{t.Value.X / 10}:{t.Value.Y / 10}"));

    private static async Task ReturnWorldAsync(CommissionSession s, CancellationToken token)
    {
        for (var step = 0; step < 5; step++)
        {
            var current = await s.PageAsync("确认返回路径", token, CommissionPage.World, CommissionPage.Menu, CommissionPage.Hub,
                CommissionPage.Daily, CommissionPage.Weekly, CommissionPage.Crisis, CommissionPage.Story);
            if (current.Ui.Page == CommissionPage.World) return;
            if (current.Ui.Page == CommissionPage.Menu) await s.KeyAsync(current, GameKey.Escape, "关闭主菜单", token);
            else await s.ClickAsync(current, "back", "返回上一页", token);
        }
        throw new CommissionStepException("未能在已知路径中返回大世界");
    }
    private static bool Equal(string a, string b) => CommissionRecognizer.Normalize(a) == CommissionRecognizer.Normalize(b);
    private static string NameKey(string name) => "name:" + CommissionRecognizer.Normalize(name);
}
