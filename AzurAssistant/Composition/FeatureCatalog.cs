using System.IO;
using AzurAssistant.Contracts;
using AzurAssistant.Features.ClaimMail;
using AzurAssistant.Features.Shop;
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
using AzurAssistant.Platform;

namespace AzurAssistant.Composition;

public sealed class FeatureCatalog : IDisposable
{
    private readonly string _routesDirectory;
    private readonly PpOcrReader _ocr = new();
    private readonly Lazy<TemplateCatalog> _dailyTemplates = new(() =>
        new TemplateCatalog(Path.Combine(AppContext.BaseDirectory, "assets", "daily")));
    private readonly Lazy<DailyUiRecognizer> _daily;
    private readonly Lazy<ShopRecognizer> _shop;
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
    private readonly Func<AssistantSettings, Func<FrameSnapshot, TaskContext, CancellationToken, Task<FrameSnapshot>>>? _reconnect;

    public FeatureCatalog(CommissionSettingsStore? commissionSettings = null, WeeklyProgressStore? weekly = null,
        Func<TimeSpan, CancellationToken, Task>? closeGame = null,
        Func<AssistantSettings, Func<FrameSnapshot, TaskContext, CancellationToken, Task<FrameSnapshot>>>? reconnect = null, string? routesDirectory = null)
    {
        _commissionSettings = commissionSettings ?? new(Path.Combine(AppContext.BaseDirectory, "commissions.json"));
        _weekly = weekly ?? new(Path.Combine(AppContext.BaseDirectory, "weekly-progress.json"));
        _closeGame = closeGame;
        _reconnect = reconnect;
        _routesDirectory = routesDirectory ?? Path.Combine(AppContext.BaseDirectory, "routes");
        _daily = new(() => new DailyUiRecognizer(_dailyTemplates.Value,
            new WorldHudReader(_ocr)));
        _journey = new(() => JourneyAssets.CreateRecognizer(_ocr, _daily.Value));
        _shop = new(() => new ShopRecognizer(_daily.Value, new TemplateCatalog(Path.Combine(AppContext.BaseDirectory, "assets", "shop"))));
        _realtime = new(() => RealtimeRecognizer.Load(_ocr));
        _navigation = new(() => new NavigationRecognizer(_daily.Value, _ocr));
        _dining = new(() => new DiningSatietyReader(_ocr));
        _activity = new(() => new ActivityRecognizer(_daily.Value, _ocr));
        _battlePass = new(() => BattlePassRecognizer.Load(_daily.Value));
        _commissions = new(() => new CommissionRecognizer(_daily.Value, _ocr,
            new TemplateCatalog(Path.Combine(AppContext.BaseDirectory, "assets", "commissions")), _commissionLayout.Value));
    }

    public IGameTask WithExtraRewards(IGameTask task, AssistantSettings? settings = null, bool daily = false)
    {
        if (settings is null) return new ExtraRewardTask(task, _dailyTemplates.Value);
        var response = ResponseTiming.FromLowPerformanceMode(settings.LowPerformanceMode);
        return daily
            ? new ExtraRewardTask(task, _dailyTemplates.Value, response.Scale(DailyUiTiming.Default.PollInterval),
                response.Scale(DailyUiTiming.Default.StableDuration), response.Scale(DailyUiTiming.Default.ActionSettle))
            : new ExtraRewardTask(task, _dailyTemplates.Value, response.Scale(RealtimeTiming.Default.PollInterval),
                afterClick: response.Scale(RealtimeTiming.Default.DialogueCooldown));
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
            recognitionTimeout: response.Scale(StartJourneyTask.InitialPageTimeout), entryTimeout: response.Scale(TimeSpan.FromSeconds(60)),
            timing: timing, updateTimeout: StartJourneyTask.UpdateTimeout, reconnect: _reconnect?.Invoke(settings), windowClosed: WindowClosed));
        var menuTasks = DailyFeatureOrder.Resolve(settings.DailyTaskOrder).Where(c => c.Selected(settings)).ToArray();
        var scheduled = new IGameTask[menuTasks.Length];
        var keepMenuOpen = false;
        // Plan backwards so a skipped commission does not request a menu handoff. Execute in the selected order.
        for (var i = menuTasks.Length - 1; i >= 0; i--)
        {
            scheduled[i] = menuTasks[i].Id switch
            {
                "claim-mail" => new ClaimMailTask(_daily.Value, timing, keepMenuOpen),
                "shop" => new ShopTask(_shop.Value, timing, keepMenuOpen),
                "home-center" => new HomeCenterTask(_daily.Value, _dining.Value, timing, keepMenuOpen, settings.HomeCakesOnly),
                "claim-activity" => new ActivityClaimsTask(_activity.Value, timing, keepMenuOpen),
                "claim-battle-pass" => new BattlePassTask(_battlePass.Value, timing, keepMenuOpen),
                "daily-commissions" => CreateCommission(CommissionKind.Daily, keepMenuOpen),
                "weekly-echoes" => CreateCommission(CommissionKind.Weekly, keepMenuOpen),
                "crisis-raids" => CreateCommission(CommissionKind.Crisis, keepMenuOpen),
                "story-commissions" => CreateCommission(CommissionKind.Story, keepMenuOpen),
                _ => throw new InvalidOperationException("未登记的一条龙功能。")
            };
            if (scheduled[i] is not CommissionTask { EntrySkipReason: not null }) keepMenuOpen = true;
        }
        tasks.AddRange(scheduled);
        if (settings.CustomRoutes)
        {
            if (settings.DailyRoutes.Count == 0) throw new InvalidDataException("自定义路线列表为空。");
            var store = new RouteStore(_routesDirectory, _navigation.Value.Maps);
            foreach (var choice in settings.DailyRoutes)
            {
                var path = Path.Combine(store.DirectoryPath, choice.File);
                if (Path.GetFileName(choice.File) != choice.File || !File.Exists(path))
                    throw new InvalidDataException($"找不到自定义路线“{choice.Name}”，请在一条龙中重新选择。");
                tasks.Add(CreateRoute(store.Read(path), choice.AutoPickup, _ => { }));
            }
        }
        var recovery = tasks.Count == 0 ? null : new MenuRecovery(_daily.Value,
            timing with { StepTimeout = response.Scale(TimeSpan.FromSeconds(5)) });
        var exit = settings.ExitGame ? new ExitGameTask(token => (_closeGame
            ?? throw new InvalidOperationException("关闭游戏能力未装配。"))(response.Scale(TimeSpan.FromSeconds(5)), token),
            settings.ExitGameDelaySeconds, settings.CloseAssistantAfterExitGame) : null;
        return new TaskSequence(tasks,
            (task, context, token) => task.Id == "start-journey"
                ? Task.FromResult(new TaskResult(true, "进入游戏自行确认登录起点"))
                : task is RouteTask ? Task.FromResult(new TaskResult(true, "路线自行确认导航起点")) : recovery!.EnsureAsync(context, token),
            recovery is null ? null : recovery.EnsureAsync,
            exit is null ? null : exit.ExecuteAsync, afterTaskName: exit?.Name,
            requireInitialGameFrame: settings.LaunchGame && tasks.Count == 0);

        CommissionTask CreateCommission(CommissionKind kind, bool keepMenuOpen) => new(kind, commissionOptions, _commissions.Value,
            _commissionLayout.Value, _weekly, timing, battleTimeout: response.Scale(TimeSpan.FromMinutes(10)),
            loadingTimeout: response.Scale(TimeSpan.FromSeconds(90)),
            actionTiming: new(response.Scale(CommissionActionTiming.Default.AutoBattleDelay),
                response.Scale(CommissionActionTiming.Default.ResultDelay), response.Scale(CommissionActionTiming.Default.MvpDelay))
                { AutoBattlePollInterval = response.Scale(CommissionActionTiming.Default.AutoBattlePollInterval) }, keepMenuOpen: keepMenuOpen);
    }

    public IGameTask CreateRealtime(AssistantSettings settings)
    {
        var response = ResponseTiming.FromLowPerformanceMode(settings.LowPerformanceMode);
        var defaults = RealtimeTiming.Default;
        var timing = new RealtimeTiming(response.Scale(defaults.PollInterval), response.Scale(defaults.DialogueCooldown),
            response.Scale(defaults.PickupCooldown), response.Scale(defaults.ResultTimeout))
        { ConfirmationSettle = response.Scale(defaults.ConfirmationSettle) };
        return new RealtimeTask(_realtime.Value, settings.AutoSkipDialogue, settings.AutoPickup, timing,
            autoMining: settings.AutoMining, autoLogging: settings.AutoLogging);
    }

    private static bool WindowClosed(FrameSnapshot frame) => !GameWindow.Exists(frame.WindowHandle, frame.ProcessId);

    public void Dispose()
    {
        if (_navigation.IsValueCreated) _navigation.Value.Dispose();
        if (_journey.IsValueCreated) _journey.Value.Dispose();
        _ocr.Dispose();
    }
    public IGameTask CreateNavigation(NavigationTestRequest request, Action<NavigationStatus> report)
        => new NavigationTestTask(_navigation.Value, request, report);
    public IGameTask CreateRoute(RouteDefinition route, bool autoPickup, Action<NavigationStatus> report)
        => new RouteTask(_navigation.Value, route, report, autoPickup ? PickupRecognizer.Load(_ocr) : null, autoPickup);
    public RouteRecordingTask CreateRouteRecording(Action<NavigationStatus> report) => new(_navigation.Value, report);
}
