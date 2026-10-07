using System.IO;
using AzurAssistant.Contracts;
using AzurAssistant.Features.ClaimMail;
using AzurAssistant.Features.Commissions;
using AzurAssistant.Features.ActivityClaims;
using AzurAssistant.Features.BattlePass;
using AzurAssistant.Features.HomeCenter;
using AzurAssistant.Features.Realtime;
using AzurAssistant.Features.StartJourney;
using AzurAssistant.Features.ExitGame;
using AzurAssistant.Game.Ui;
using AzurAssistant.Runtime;
using AzurAssistant.Vision;
using AzurAssistant.Game.Navigation;
using AzurAssistant.Features.NavigationTest;
using AzurAssistant.Features.Routes;
using AzurAssistant.Game.Interaction;

namespace AzurAssistant.Composition;

public sealed class FeatureCatalog : IDisposable
{
    private readonly PpOcrReader _ocr = new();
    private readonly Lazy<DailyUiRecognizer> _daily;
    private readonly Lazy<StartJourneyRecognizer> _journey;
    private readonly Lazy<RealtimeRecognizer> _realtime;
    private readonly Lazy<DiningSatietyReader> _dining;
    private readonly Lazy<ActivityRecognizer> _activity;
    private readonly Lazy<BattlePassRecognizer> _battlePass;
    private readonly Lazy<CommissionRecognizer> _commissions;
    private readonly Lazy<NavigationRecognizer> _navigation;
    private readonly Lazy<CommissionLayout> _commissionLayout = new(() => CommissionLayout.Load());
    private readonly CommissionSettingsStore _commissionSettings;
    private readonly WeeklyProgressStore _weekly;
    private readonly Func<TimeSpan, CancellationToken, Task>? _closeGame;

    public FeatureCatalog(CommissionSettingsStore? commissionSettings = null, WeeklyProgressStore? weekly = null,
        Func<TimeSpan, CancellationToken, Task>? closeGame = null)
    {
        _commissionSettings = commissionSettings ?? new(Path.Combine(AppContext.BaseDirectory, "commissions.json"));
        _weekly = weekly ?? new(Path.Combine(AppContext.BaseDirectory, "weekly-progress.json"));
        _closeGame = closeGame;
        _daily = new(() => new DailyUiRecognizer(new TemplateCatalog(Path.Combine(AppContext.BaseDirectory, "assets", "daily")),
            new WorldHudReader(_ocr)));
        _journey = new(() => JourneyAssets.CreateRecognizer(_ocr, async (frame, token) =>
            (await _daily.Value.ObserveAsync(frame, token)).Page is DailyPage.World or DailyPage.Menu));
        _realtime = new(() => RealtimeRecognizer.Load(_ocr));
        _navigation = new(() => new NavigationRecognizer(_daily.Value, _ocr));
        _dining = new(() => new DiningSatietyReader(_ocr));
        _activity = new(() => new ActivityRecognizer(_daily.Value, _ocr));
        _battlePass = new(() => BattlePassRecognizer.Load(_daily.Value));
        _commissions = new(() => new CommissionRecognizer(_daily.Value, _ocr,
            new TemplateCatalog(Path.Combine(AppContext.BaseDirectory, "assets", "commissions")), _commissionLayout.Value));
    }

    public IGameTask CreateDaily(AssistantSettings settings)
    {
        var response = ResponseTiming.FromLowPerformanceMode(settings.LowPerformanceMode);
        var defaults = DailyUiTiming.Default;
        var timing = new DailyUiTiming(response.Scale(defaults.StepTimeout), response.Scale(defaults.PollInterval),
            response.Scale(defaults.ActionSettle))
        {
            StableDuration = response.Scale(defaults.StableDuration),
            RetryDelay = response.Scale(defaults.RetryDelay),
            InvalidFrameDelay = response.Scale(defaults.InvalidFrameDelay)
        };
        var tasks = new List<IGameTask>();
        if ((settings.DailyCommissions || settings.WeeklyEchoes || settings.CrisisRaids || settings.StoryCommissions) && _commissionSettings.Error is { } error)
            throw new InvalidOperationException(error);
        var commissionOptions = _commissionSettings.Current;
        if (settings.EnterGame) tasks.Add(new StartJourneyTask(_journey.Value,
            recognitionTimeout: response.Scale(TimeSpan.FromSeconds(15)), entryTimeout: response.Scale(TimeSpan.FromSeconds(60)),
            timing: timing, updateTimeout: response.Scale(TimeSpan.FromMinutes(5))));
        foreach (var choice in DailyFeatureOrder.Resolve(settings.DailyTaskOrder).Where(c => c.Selected(settings)))
            tasks.Add(choice.Id switch
            {
                "claim-mail" => new ClaimMailTask(_daily.Value, timing),
                "home-center" => new HomeCenterTask(_daily.Value, _dining.Value, timing),
                "claim-activity" => new ActivityClaimsTask(_activity.Value, timing),
                "claim-battle-pass" => new BattlePassTask(_battlePass.Value, timing),
                "daily-commissions" => CreateCommission(CommissionKind.Daily),
                "weekly-echoes" => CreateCommission(CommissionKind.Weekly),
                "crisis-raids" => CreateCommission(CommissionKind.Crisis),
                "story-commissions" => CreateCommission(CommissionKind.Story),
                _ => throw new InvalidOperationException("未登记的一条龙功能。")
            });
        var recovery = tasks.Count == 0 ? null : new MenuRecovery(_daily.Value,
            timing with { StepTimeout = response.Scale(TimeSpan.FromSeconds(5)) });
        var exit = settings.ExitGame ? new ExitGameTask(token => (_closeGame
            ?? throw new InvalidOperationException("关闭游戏能力未装配。"))(response.Scale(TimeSpan.FromSeconds(5)), token),
            settings.ExitGameDelaySeconds, settings.CloseAssistantAfterExitGame) : null;
        return new TaskSequence(tasks,
            (task, context, token) => task.Id == "start-journey"
                ? Task.FromResult(new TaskResult(true, "进入游戏自行确认登录起点"))
                : recovery!.EnsureAsync(context, token),
            recovery is null ? null : recovery.EnsureAsync,
            exit is null ? null : exit.ExecuteAsync, afterTaskName: exit?.Name);

        CommissionTask CreateCommission(CommissionKind kind) => new(kind, commissionOptions, _commissions.Value,
            _commissionLayout.Value, _weekly, timing, battleTimeout: response.Scale(TimeSpan.FromMinutes(10)),
            loadingTimeout: response.Scale(TimeSpan.FromSeconds(90)),
            actionTiming: new(response.Scale(CommissionActionTiming.Default.AutoBattleDelay),
                response.Scale(CommissionActionTiming.Default.ResultDelay), response.Scale(CommissionActionTiming.Default.MvpDelay))
                { AutoBattlePollInterval = response.Scale(CommissionActionTiming.Default.AutoBattlePollInterval) });
    }

    public IGameTask CreateRealtime(AssistantSettings settings)
    {
        var response = ResponseTiming.FromLowPerformanceMode(settings.LowPerformanceMode);
        var defaults = RealtimeTiming.Default;
        var timing = new RealtimeTiming(response.Scale(defaults.PollInterval), response.Scale(defaults.DialogueCooldown),
            response.Scale(defaults.PickupCooldown), response.Scale(defaults.ResultTimeout))
        { ConfirmationSettle = response.Scale(defaults.ConfirmationSettle) };
        return new RealtimeTask(_realtime.Value, settings.AutoSkipDialogue, settings.AutoPickup, timing);
    }

    public void Dispose()
    {
        if (_navigation.IsValueCreated) _navigation.Value.Dispose();
        if (_journey.IsValueCreated) _journey.Value.Dispose();
        _ocr.Dispose();
    }
    public IGameTask CreateNavigation(NavigationTestRequest request, Action<NavigationStatus> report)
        => new NavigationTestTask(_navigation.Value, request, report);
    public IGameTask CreateRoute(RouteDefinition route, Action<NavigationStatus> report)
        => new RouteTask(_navigation.Value, route, report, route.AutoPickup ? PickupRecognizer.Load(_ocr) : null);
    public IGameTask CreateRouteCapture(Action<NavigationStatus> report, Action<MapPosition> captured)
        => new RouteCaptureTask(_navigation.Value, report, captured);
}
