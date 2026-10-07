using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzurAssistant.Contracts;
using AzurAssistant.Runtime;
using System.Diagnostics;
using AzurAssistant.Composition;
using AzurAssistant.Features.Commissions;
using AzurAssistant.Features.NavigationTest;
using AzurAssistant.Game.Navigation;

namespace AzurAssistant.UI;

public sealed partial class MainViewModel(CapturePreviewService preview, DailyLogStore? log = null, TaskRunner? runner = null, IGameTask? task = null,
    Action? restartElevated = null, AutomationController? automation = null, bool runDailyFromCommandLine = false,
    Func<bool>? restartAssistant = null, string? routesDirectory = null) : INotifyPropertyChanged
{
    private FrameSnapshot? _displayedFrame;
    private PreviewSnapshot? _lastSnapshot;
    private long _lastLog;
    private bool _initialized;
    private string _selectedPage = "home";
    private AssistantSettings _localSettings = new();
    private readonly ApplicationPreferences _startupPreferences = ApplicationPreferences.From(automation?.Settings ?? new());
    private bool _navigating;
    private TimeSpan _lastPreviewUpdate;
    private string? _displayedDailyOrder;
    private bool _exitGameSettingsExpanded;
    private string? _exitGameDelayText;
    private string? _pauseHotkeyText;
    private string? _stopHotkeyText;
    public ObservableCollection<DailyTaskOption> OrderedDailyTasks { get; } = [];
    private TaskRunner? Runner => automation?.Runner ?? runner;
    private bool IsBusy => automation?.IsBusy ?? (Runner?.IsRunning == true);
    private AssistantSettings Settings => automation?.Settings ?? _localSettings;
    private AssistantSettings EffectiveSettings => automation?.EffectiveSettings ?? _startupPreferences.ApplyTo(_localSettings);
    public bool HasPendingSettings => automation?.HasPendingSettings
        ?? ApplicationPreferences.From(Settings) != _startupPreferences;
    public bool IsRestarting { get; private set; }
    public bool RestartRequested { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<string> Logs { get; } = [];
    public BitmapSource? PreviewImage { get; private set; }
    public string StateText { get; private set; } = "未连接";
    public string Message { get; private set; } = "连接游戏后，这里将显示实时画面。";
    public string FrameInfo { get; private set; } = "等待画面";
    public string FrameTime { get; private set; } = "—";
    public bool IsLive { get; private set; }
    public bool CanConnect => !preview.IsRunning && !IsRestarting && !IsBusy && !IsUpdateBusy;
    public bool CanStop => preview.IsRunning;
    public bool ShowPlaceholder => PreviewImage is null;
    public bool IsFaulted { get; private set; }
    public string PreviewHint => IsFaulted ? "画面暂不可用" : "等待游戏画面";
    public string ConnectLabel => IsFaulted ? "重新连接" : "连接游戏";

    public string SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is not ("home" or "daily" or "realtime" or "navigation" or "routes" or "settings") || value == _selectedPage) return;
            _selectedPage = value;
            Notify(string.Empty);
        }
    }
    public bool IsHomePage => SelectedPage == "home";
    public bool IsDailyPage => SelectedPage == "daily";
    public bool IsRealtimePage => SelectedPage == "realtime";
    public bool IsNavigationPage => SelectedPage == "navigation";
    public bool IsRoutesPage => SelectedPage == "routes";
    public bool IsSettingsPage => SelectedPage == "settings";
    public string PageTitle => SelectedPage switch { "daily" => "一条龙", "realtime" => "实时辅助", "navigation" => "导航测试", "routes" => "路线管理", "settings" => "设置", _ => "控制台" };
    public bool CanConfigure => !IsBusy && !IsRestarting && !IsUpdateBusy;
    public bool IsUpdateBusy { get; private set; }
    public string UpdateStatus { get; private set; } = "GitHub 更新仓库尚未配置";
    public bool CanCheckUpdates => !IsUpdateBusy && !IsRestarting;
    public void SetUpdateStatus(bool busy, string status)
    { IsUpdateBusy = busy; UpdateStatus = status; Notify(string.Empty); }
    public bool CanStartDaily => automation is not null && CanConfigure && !HasSettingsError
        && !(ExitGame && HasExitGameDelayError)
        && (LaunchGame || EnterGame || ExitGame || DailyFeatureOrder.Choices.Any(c => c.Selected(Settings)));
    public bool CanStartRealtime => automation is not null && CanConfigure && (AutoSkipDialogue || AutoPickup);
    public bool CanStartNavigation => automation is not null && CanConfigure && !HasSettingsError;
    private string _navigationX = "";
    private string _navigationY = "";
    public string NavigationX { get => _navigationX; set { _navigationX = value; Notify(); Notify(nameof(CanMoveToCoordinate)); } }
    public string NavigationY { get => _navigationY; set { _navigationY = value; Notify(); Notify(nameof(CanMoveToCoordinate)); } }
    public bool CanMoveToCoordinate => CanStartNavigation && TryNavigationTarget(out _, out _);
    public IReadOnlyList<NavigationMap> NavigationMaps => automation?.NavigationMaps ?? [];
    private NavigationMap? _teleportMap = automation?.NavigationMaps.FirstOrDefault();
    private NavigationTeleport? _teleport = automation?.NavigationMaps.FirstOrDefault()?.Teleports.FirstOrDefault();
    public NavigationMap? SelectedTeleportMap
    {
        get => _teleportMap;
        set
        {
            if (value is null || value == _teleportMap || !NavigationMaps.Contains(value)) return;
            // Clear the old selection before replacing its item collection, then select an item from the new collection.
            _teleport = null; Notify(nameof(SelectedTeleport));
            _teleportMap = value; Notify(); Notify(nameof(TeleportChoices));
            _teleport = value.Teleports.FirstOrDefault(); Notify(nameof(SelectedTeleport));
        }
    }
    public IReadOnlyList<NavigationTeleport> TeleportChoices => SelectedTeleportMap?.Teleports ?? [];
    public NavigationTeleport? SelectedTeleport { get => _teleport; set { if (value is null || !TeleportChoices.Contains(value)) return; _teleport = value; Notify(); } }
    private string MapName(string id) => NavigationMaps.FirstOrDefault(m => m.Id == id)?.Name ?? (id == "200000" ? "夏露露村" : id);
    public string NavigationMapText => automation?.NavigationStatus?.Position is { } p ? $"{MapName(p.MapId)} · {p.Floor} 层" : "当前地图未确认";
    public string NavigationCoordinateText => automation?.NavigationStatus is { Position: { } p } state
        ? $"X {p.X:F1}    Y {p.Y:F1}    角度 {(state.FacingDegrees is { } angle ? angle.ToString("F1", CultureInfo.CurrentCulture) + "°" : "—")}" : "—";
    public string NavigationObservationText => IsPaused ? "已暂停，继续后重新定位"
        : automation?.NavigationStatus is not { } state ? "尚未定位"
        : DateTimeOffset.Now - state.ObservedAt > TimeSpan.FromSeconds(1.5) ? "上次观测，当前坐标待更新"
        : state.Position is null ? "未确认角色坐标" : "定位有效";
    private bool TryNavigationTarget(out double x, out double y)
    {
        y = 0;
        return double.TryParse(NavigationX, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            && double.TryParse(NavigationY, NumberStyles.Float, CultureInfo.InvariantCulture, out y)
            && double.IsFinite(x) && double.IsFinite(y) && x >= 0 && y >= 0 && x < 8192 && y < 8192;
    }
    public void StartNavigation(NavigationTestMode mode)
    {
        if (!CanStartNavigation) return;
        var x = 0d; var y = 0d;
        if (mode == NavigationTestMode.Move && !TryNavigationTarget(out x, out y)) return;
        automation?.StartNavigation(new(mode, x, y, mode == NavigationTestMode.Teleport ? SelectedTeleportMap?.Id : null,
            mode == NavigationTestMode.Teleport ? SelectedTeleport?.Id : null)); Refresh();
    }
    public void UseObservedCoordinate()
    {
        if (automation?.NavigationStatus is not { Position: { } p }) return;
        NavigationX = p.X.ToString("F1", CultureInfo.InvariantCulture);
        NavigationY = p.Y.ToString("F1", CultureInfo.InvariantCulture);
    }
    private CommissionOptions? _commissionDraft;
    private string? _dailyCategory;
    private bool? _customCrisisSpecial;
    private readonly Dictionary<string, string> _claimLimitDrafts = new();
    private const string CustomSpecialChoice = "自定义副本名称";
    // Keep option collection identities stable through the periodic status refresh so an open popup retains focus.
    private static readonly IReadOnlyList<string> DailyCategories = Array.AsReadOnly(
        Features.Commissions.CommissionOptions.DailyChoices.Select(d => d.Category).Distinct().ToArray());
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DailyNamesByCategory =
        Features.Commissions.CommissionOptions.DailyChoices.GroupBy(d => d.Category).ToDictionary(
            group => group.Key, group => (IReadOnlyList<string>)Array.AsReadOnly(group.Select(d => d.Name).ToArray()));
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<int>> DailyTiers =
        Features.Commissions.CommissionOptions.DailyChoices.ToDictionary(d => d.Name,
            d => (IReadOnlyList<int>)Array.AsReadOnly(Enumerable.Range(d.MinDifficulty, d.MaxDifficulty - d.MinDifficulty + 1).ToArray()));
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<string>> SpecialNames = Enumerable.Range(1, 6)
        .ToDictionary(tier => tier, tier => (IReadOnlyList<string>)Array.AsReadOnly(
            Features.Commissions.CommissionOptions.GetSpecialChoices(tier).Append(CustomSpecialChoice).ToArray()));
    private CommissionOptions CommissionOptions => _commissionDraft ?? automation?.CommissionSettings?.Current ?? new();
    public IReadOnlyList<string> DailyCategoryChoices => DailyCategories;
    public string DailyCategory
    {
        get => _dailyCategory ?? Features.Commissions.CommissionOptions.DailyChoices
            .SingleOrDefault(d => d.Name == DailyDungeon)?.Category ?? "";
        set
        {
            if (!DailyCategoryChoices.Contains(value) || value == DailyCategory) return;
            _dailyCategory = value;
            // Require an explicit target after changing category; never substitute a resource-consuming dungeon.
            if (!DailyDungeonChoices.Contains(DailyDungeon))
                SetCommissionDraft(CommissionOptions with { DailyDungeon = "" });
            Notify(string.Empty);
        }
    }
    public IReadOnlyList<string> DailyDungeonChoices => DailyNamesByCategory.GetValueOrDefault(DailyCategory) ?? Array.Empty<string>();
    public IReadOnlyList<string> WeeklyDungeonChoices => Features.Commissions.CommissionOptions.WeeklyChoices;
    public IReadOnlyList<StoryChapterChoice> StoryChapterChoices => Features.Commissions.CommissionOptions.StoryChapters;
    public IReadOnlyList<string> StoryDungeonChoices => Features.Commissions.CommissionOptions.GetStoryChoices(StoryChapter);
    public int StoryChapter
    {
        get => CommissionOptions.StoryChapter;
        set
        {
            if (!StoryChapterChoices.Any(c => c.Number == value) || value == StoryChapter) return;
            SetCommissionDraft(CommissionOptions with { StoryChapter = value, StoryDungeon = "" });
        }
    }
    public string StoryDungeon
    {
        get => CommissionOptions.StoryDungeon;
        set { if (StoryDungeonChoices.Contains(value)) SetCommissionDraft(CommissionOptions with { StoryDungeon = value }); }
    }
    public string StoryTeam { get => CommissionOptions.StoryTeamName; set => SetCommissionDraft(CommissionOptions with { StoryTeamName = value }); }
    public IReadOnlyList<string> CrisisDungeonChoices => Features.Commissions.CommissionOptions.GetCrisisChoices(CrisisDifficulty);
    public IReadOnlyList<string> SpecialDungeonChoices => SpecialNames.GetValueOrDefault(CrisisDifficulty) ?? Array.Empty<string>();
    public bool CrisisSpecialEnabled
    {
        get => CommissionOptions.CrisisSpecialEnabled;
        set => SetCommissionDraft(CommissionOptions with { CrisisSpecialEnabled = value });
    }
    public bool CrisisNormalEnabled
    {
        get => CommissionOptions.CrisisNormalEnabled;
        set => SetCommissionDraft(CommissionOptions with { CrisisNormalEnabled = value });
    }
    public bool CrisisUsesCustomSpecial => _customCrisisSpecial
        ?? !Features.Commissions.CommissionOptions.GetSpecialChoices(CrisisDifficulty).Contains(CrisisSpecial);
    public string CrisisSpecialChoice
    {
        get => CrisisUsesCustomSpecial ? CustomSpecialChoice : CrisisSpecial;
        set
        {
            if (value == CrisisSpecialChoice) return;
            if (value == CustomSpecialChoice)
            {
                _customCrisisSpecial = true;
                SetCommissionDraft(CommissionOptions with { CrisisSpecial = "" });
            }
            else if (Features.Commissions.CommissionOptions.GetSpecialChoices(CrisisDifficulty).Contains(value))
            {
                _customCrisisSpecial = false;
                SetCommissionDraft(CommissionOptions with { CrisisSpecial = value });
            }
            else return;
            Notify(nameof(CrisisSpecialChoice));
            Notify(nameof(CrisisUsesCustomSpecial));
        }
    }
    public string CrisisOrderDescription => (CrisisSpecialEnabled, CrisisNormalEnabled) switch
    {
        (true, true) => "先刷惊喜副本，再刷普通副本",
        (true, false) => "仅刷惊喜副本",
        (false, true) => "仅刷普通副本",
        _ => "未选择刷取类型，本任务将跳过"
    };
    public string CrisisCustomNameHint => CrisisNormalEnabled
        ? "请输入当前难度下的完整名称；未找到时会记录并继续普通副本"
        : "请输入当前难度下的完整名称；未找到时会记录并结束本任务";
    public bool DailyClaimLimitEnabled { get => CommissionOptions.DailyClaimLimitEnabled; set => SetCommissionDraft(CommissionOptions with { DailyClaimLimitEnabled = value }); }
    public bool WeeklyClaimLimitEnabled { get => CommissionOptions.WeeklyClaimLimitEnabled; set => SetCommissionDraft(CommissionOptions with { WeeklyClaimLimitEnabled = value }); }
    public bool StoryClaimLimitEnabled { get => CommissionOptions.StoryClaimLimitEnabled; set => SetCommissionDraft(CommissionOptions with { StoryClaimLimitEnabled = value }); }
    public bool CrisisSpecialClaimLimitEnabled { get => CommissionOptions.CrisisSpecialClaimLimitEnabled; set => SetCommissionDraft(CommissionOptions with { CrisisSpecialClaimLimitEnabled = value }); }
    public bool CrisisNormalClaimLimitEnabled { get => CommissionOptions.CrisisNormalClaimLimitEnabled; set => SetCommissionDraft(CommissionOptions with { CrisisNormalClaimLimitEnabled = value }); }
    public string DailyClaimLimitText { get => ClaimLimitText(nameof(DailyClaimLimitText), CommissionOptions.DailyClaimLimit); set => SetClaimLimitText(nameof(DailyClaimLimitText), value); }
    public string WeeklyClaimLimitText { get => ClaimLimitText(nameof(WeeklyClaimLimitText), CommissionOptions.WeeklyClaimLimit); set => SetClaimLimitText(nameof(WeeklyClaimLimitText), value); }
    public string StoryClaimLimitText { get => ClaimLimitText(nameof(StoryClaimLimitText), CommissionOptions.StoryClaimLimit); set => SetClaimLimitText(nameof(StoryClaimLimitText), value); }
    public string CrisisSpecialClaimLimitText { get => ClaimLimitText(nameof(CrisisSpecialClaimLimitText), CommissionOptions.CrisisSpecialClaimLimit); set => SetClaimLimitText(nameof(CrisisSpecialClaimLimitText), value); }
    public string CrisisNormalClaimLimitText { get => ClaimLimitText(nameof(CrisisNormalClaimLimitText), CommissionOptions.CrisisNormalClaimLimit); set => SetClaimLimitText(nameof(CrisisNormalClaimLimitText), value); }
    private string ClaimLimitText(string name, int value) => _claimLimitDrafts.GetValueOrDefault(name) ?? value.ToString(CultureInfo.InvariantCulture);
    private void SetClaimLimitText(string name, string value)
    {
        _claimLimitDrafts[name] = value ?? "";
        _commissionDraft ??= CommissionOptions;
        _commissionError = null;
        CommissionNote = "委托设置尚未保存；运行中的任务保持原设置";
        Notify(name); Notify(nameof(CommissionNote)); Notify(nameof(CommissionError));
    }
    private static int ParseClaimLimit(bool enabled, string text, int saved, string name)
    {
        if (!enabled) return saved;
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 999)
            throw new System.IO.InvalidDataException($"{name}领取次数请输入 1–999 的整数，按领取份数计算。");
        return count;
    }
    public IReadOnlyList<int> SixDifficulties { get; } = [1, 2, 3, 4, 5, 6];
    public IReadOnlyList<int> DailyDifficulties => DailyTiers.GetValueOrDefault(DailyDungeon ?? "") ?? Array.Empty<int>();
    public string DailyDungeon
    {
        get => CommissionOptions.DailyDungeon;
        set
        {
            var d = Features.Commissions.CommissionOptions.DailyChoices.SingleOrDefault(x => x.Name == value && x.Category == DailyCategory);
            if (d is not null) SetCommissionDraft(CommissionOptions with { DailyDungeon = value, DailyDifficulty = Math.Clamp(DailyDifficulty, d.MinDifficulty, d.MaxDifficulty) });
        }
    }
    public int DailyDifficulty { get => CommissionOptions.DailyDifficulty; set { if (DailyDifficulties.Contains(value)) SetCommissionDraft(CommissionOptions with { DailyDifficulty = value }); } }
    public string WeeklyDungeon { get => CommissionOptions.WeeklyDungeon; set { if (WeeklyDungeonChoices.Contains(value)) SetCommissionDraft(CommissionOptions with { WeeklyDungeon = value }); } }
    public int WeeklyDifficulty { get => CommissionOptions.WeeklyDifficulty; set { if (SixDifficulties.Contains(value)) SetCommissionDraft(CommissionOptions with { WeeklyDifficulty = value }); } }
    public int CrisisDifficulty
    {
        get => CommissionOptions.CrisisDifficulty;
        set
        {
            if (!SixDifficulties.Contains(value) || value == CrisisDifficulty) return;
            var normal = Features.Commissions.CommissionOptions.GetCrisisChoices(value);
            _customCrisisSpecial = null;
            SetCommissionDraft(CommissionOptions with
            {
                CrisisDifficulty = value,
                CrisisNormal = normal.Contains(CrisisNormal) ? CrisisNormal : "",
                CrisisSpecial = ""
            });
        }
    }
    public string CrisisNormal
    {
        get => CommissionOptions.CrisisNormal;
        set { if (string.IsNullOrEmpty(value) || CrisisDungeonChoices.Contains(value)) SetCommissionDraft(CommissionOptions with { CrisisNormal = value ?? "" }); }
    }
    public string CrisisSpecial
    {
        get => CommissionOptions.CrisisSpecial;
        set
        {
            // Text entry stays custom even when an intermediate prefix equals a known preset.
            _customCrisisSpecial = true;
            SetCommissionDraft(CommissionOptions with { CrisisSpecial = value ?? "" });
            Notify(nameof(CrisisSpecialChoice));
            Notify(nameof(CrisisUsesCustomSpecial));
        }
    }
    public string CommissionTeam { get => CommissionOptions.TeamName; set => SetCommissionDraft(CommissionOptions with { TeamName = value }); }
    public string CommissionNote { get; private set; } = "请先手动三星通关所选难度";
    public string? CommissionError => _commissionError ?? automation?.CommissionSettings?.Error;
    private string? _commissionError;
    public string WeeklyProgressText
    {
        get
        {
            try { return $"本周刷取次数：{automation?.WeeklyProgress?.Claimed ?? 0}/3 次"; }
            catch (System.IO.IOException) { return "周本记录暂时无法读取"; }
            catch (UnauthorizedAccessException) { return "周本记录暂时无法读取"; }
        }
    }
    private void SetCommissionDraft(CommissionOptions value)
    { if (value == CommissionOptions) return; _commissionDraft = value; _commissionError = null; CommissionNote = "委托设置尚未保存；运行中的任务保持原设置"; Notify(string.Empty); }
    public bool SaveCommissionSettings()
    {
        try
        {
            var options = CommissionOptions with
            {
                CrisisSpecial = (CrisisSpecial ?? "").Trim(),
                DailyClaimLimit = ParseClaimLimit(DailyClaimLimitEnabled, DailyClaimLimitText, CommissionOptions.DailyClaimLimit, "日常委托"),
                WeeklyClaimLimit = ParseClaimLimit(WeeklyClaimLimitEnabled, WeeklyClaimLimitText, CommissionOptions.WeeklyClaimLimit, "往厄残影"),
                StoryClaimLimit = ParseClaimLimit(StoryClaimLimitEnabled, StoryClaimLimitText, CommissionOptions.StoryClaimLimit, "主线委托"),
                CrisisSpecialClaimLimit = ParseClaimLimit(CrisisSpecialEnabled && CrisisSpecialClaimLimitEnabled, CrisisSpecialClaimLimitText, CommissionOptions.CrisisSpecialClaimLimit, "危机惊喜副本"),
                CrisisNormalClaimLimit = ParseClaimLimit(CrisisNormalEnabled && CrisisNormalClaimLimitEnabled, CrisisNormalClaimLimitText, CommissionOptions.CrisisNormalClaimLimit, "危机普通副本")
            };
            options.Validate();
            if (automation?.CommissionSettings is { } store)
            {
                if (!store.Save(options)) { _commissionError = store.Error; Notify(string.Empty); return false; }
                _commissionDraft = null;
            }
            else _commissionDraft = options;
            _dailyCategory = null;
            _commissionError = null; CommissionNote = "委托设置已保存；下次运行生效"; Notify(string.Empty); return true;
        }
        catch (System.IO.InvalidDataException ex) { _commissionError = ex.Message; Notify(string.Empty); return false; }
    }
    public void ResetWeeklyProgress() { automation?.ResetWeeklyProgress(); Notify(string.Empty); }
    public string DailySelectionText => SelectionText(new[] { (LaunchGame, "启动游戏"), (EnterGame, "进入游戏") }
        .Concat(OrderedDailyTasks.Select(c => (c.IsSelected, c.Name))).Append((ExitGame, "关闭游戏")).ToArray());
    public string RealtimeSelectionText => SelectionText([(AutoSkipDialogue, "自动跳过对话"), (AutoPickup, "自动拾取采集物")]);
    public string GameExecutablePath { get => Settings.GameExecutablePath; set => SaveSettings(Settings with { GameExecutablePath = value }); }
    public string LaunchPathHint => string.IsNullOrWhiteSpace(EffectiveSettings.GameExecutablePath)
        ? "请先在设置中选择游戏启动文件" : EffectiveSettings.GameExecutablePath;
    public bool AutoConnect { get => Settings.AutoConnect; set => SaveSettings(Settings with { AutoConnect = value }); }
    public bool AutoRunDaily { get => Settings.AutoRunDaily; set => SaveSettings(Settings with { AutoRunDaily = value }); }
    public bool LowPerformanceMode { get => Settings.LowPerformanceMode; set => SaveSettings(Settings with { LowPerformanceMode = value }); }
    public bool DarkMode { get => Settings.DarkMode; set => SaveSettings(Settings with { DarkMode = value }); }
    public bool AutoUpdate { get => Settings.AutoUpdate; set => SaveSettings(Settings with { AutoUpdate = value }); }
    public bool EffectiveAutoUpdate => EffectiveSettings.AutoUpdate;
    public async Task<bool> InstallUpdateAsync(Func<bool> launchInstaller)
    {
        if (IsRestarting) return false;
        RestartRequested = true;
        IsRestarting = true;
        Notify(string.Empty);
        try
        {
            await StopAsync();
            return launchInstaller();
        }
        finally { IsRestarting = false; Notify(string.Empty); }
    }
    public bool EffectiveDarkMode => EffectiveSettings.DarkMode;
    public string PauseHotkeyText => _pauseHotkeyText ?? Settings.PauseHotkey;
    public string StopHotkeyText => _stopHotkeyText ?? Settings.StopHotkey;
    public string EffectivePauseHotkey => EffectiveSettings.PauseHotkey;
    public string EffectiveStopHotkey => EffectiveSettings.StopHotkey;
    public string StopTaskTooltip => $"停止所有任务（{EffectiveStopHotkey}）";
    public string? HotkeyError { get; private set; }
    public bool HasHotkeyError => !string.IsNullOrEmpty(HotkeyError);
    public bool RecordHotkey(bool pause, string value)
    {
        if (pause) _pauseHotkeyText = value; else _stopHotkeyText = value;
        if (!HotkeyBinding.TryParsePair(PauseHotkeyText, StopHotkeyText, out _, out _, out var error))
        { ShowHotkeyValidationError(error!); return false; }
        HotkeyError = null;
        Notify(string.Empty);
        return true;
    }
    public bool CommitHotkeyEdit()
    {
        if (_pauseHotkeyText is null && _stopHotkeyText is null) return true;
        if (!HotkeyBinding.TryParsePair(PauseHotkeyText, StopHotkeyText, out var pauseBinding, out var stopBinding, out var error))
        { ShowHotkeyValidationError(error!); return false; }
        if (!SaveSettings(Settings with { PauseHotkey = pauseBinding.Text, StopHotkey = stopBinding.Text }))
        { ShowHotkeyValidationError(SettingsError ?? "快捷键保存失败，修改尚未生效。"); return false; }
        _pauseHotkeyText = null; _stopHotkeyText = null; HotkeyError = null;
        Notify(string.Empty);
        return true;
    }
    public void ShowHotkeyValidationError(string error) { HotkeyError = error; Notify(string.Empty); }
    public bool DisablePreview { get => !Settings.ShowPreview; set => SaveSettings(Settings with { ShowPreview = !value }); }
    public bool ShowPreview => EffectiveSettings.ShowPreview;
    public bool ShowPreviewFootnote => IsHomePage && ShowPreview;
    public bool ExitGame { get => Settings.ExitGame; set => SaveSettings(Settings with { ExitGame = value }); }
    public bool ExitGameSettingsExpanded
    {
        get => _exitGameSettingsExpanded;
        set { if (_exitGameSettingsExpanded == value) return; _exitGameSettingsExpanded = value; Notify(); }
    }
    public string ExitGameDelayText
    {
        get => _exitGameDelayText ?? Settings.ExitGameDelaySeconds.ToString(CultureInfo.InvariantCulture);
        set
        {
            _exitGameDelayText = value ?? "";
            if (int.TryParse(_exitGameDelayText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                && seconds is >= 0 and <= 3600)
            {
                ExitGameDelayError = null;
                SaveSettings(Settings with { ExitGameDelaySeconds = seconds });
            }
            else ExitGameDelayError = "关闭前等待时间请输入 0–3600 的整数秒。";
            Notify(nameof(ExitGameDelayText));
            Notify(nameof(ExitGameDelayError));
            Notify(nameof(HasExitGameDelayError));
            Notify(nameof(CanStartDaily));
        }
    }
    public string? ExitGameDelayError { get; private set; }
    public bool HasExitGameDelayError => ExitGameDelayError is not null;
    public bool CloseAssistantAfterExitGame
    {
        get => Settings.CloseAssistantAfterExitGame;
        set => SaveSettings(Settings with { CloseAssistantAfterExitGame = value });
    }
    public bool LaunchGame { get => Settings.LaunchGame; set => SaveSettings(Settings with { LaunchGame = value }); }
    public bool EnterGame { get => Settings.EnterGame; set => SaveSettings(Settings with { EnterGame = value }); }
    public bool ClaimMail { get => Settings.ClaimMail; set => SaveSettings(Settings with { ClaimMail = value }); }
    public bool HomeCenter { get => Settings.HomeCenter; set => SaveSettings(Settings with { HomeCenter = value }); }
    public bool ClaimActivity { get => Settings.ClaimActivity; set => SaveSettings(Settings with { ClaimActivity = value }); }
    public bool ClaimBattlePass { get => Settings.ClaimBattlePass; set => SaveSettings(Settings with { ClaimBattlePass = value }); }
    public bool IsDailySelected(string id) => DailyFeatureOrder.Choices.Single(c => c.Id == id).Selected(Settings);
    public void SetDailySelected(string id, bool selected) => SaveSettings(DailyFeatureOrder.Choices.Single(c => c.Id == id).SetSelected(Settings, selected));
    public bool MoveDailyTask(string sourceId, string targetId, bool after)
    {
        var ids = OrderedDailyTasks.Select(c => c.Id).ToList();
        if (sourceId == targetId || !ids.Contains(sourceId) || !ids.Contains(targetId)) return false;
        ids.Remove(sourceId);
        ids.Insert(ids.IndexOf(targetId) + (after ? 1 : 0), sourceId);
        return SaveSettings(Settings with { DailyTaskOrder = string.Join(',', ids) });
    }
    public bool AutoSkipDialogue { get => Settings.AutoSkipDialogue; set => SaveSettings(Settings with { AutoSkipDialogue = value }); }
    public bool AutoPickup { get => Settings.AutoPickup; set => SaveSettings(Settings with { AutoPickup = value }); }
    public string? SettingsError => automation?.SettingsError;
    public bool HasSettingsError => !string.IsNullOrWhiteSpace(SettingsError);
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        automation?.StartConfigured(runDailyFromCommandLine);
        Refresh();
    }
    public void StartDaily()
    {
        if (IsRestarting || IsBusy || IsUpdateBusy) return;
        if (ExitGame && HasExitGameDelayError)
        {
            ExitGameSettingsExpanded = true;
            Report(ExitGameDelayError!);
            return;
        }
        if (_commissionDraft is not null && !SaveCommissionSettings()) return;
        automation?.StartDaily();
        Refresh();
    }
    public void StartRealtime() { if (IsRestarting || IsBusy || IsUpdateBusy) return; automation?.StartRealtime(); Refresh(); }

    /// <returns>True only after a replacement assistant was started and this window may close.</returns>
    public async Task<bool> NavigateAsync(string page, Func<Task<bool>> confirmRestart)
    {
        if (_navigating || page is not ("home" or "daily" or "realtime" or "navigation" or "routes" or "settings") || page == SelectedPage) return false;
        _navigating = true;
        try
        {
            if (IsSettingsPage && HasPendingSettings && await confirmRestart() && HasPendingSettings)
            {
                RestartRequested = true;
                IsRestarting = true;
                Refresh();
                await StopAsync();
                if (restartAssistant?.Invoke() == true) return true;
                Report("助手未重启，已保存的设置将在下次启动时生效。");
                return false;
            }
            SelectedPage = page;
            if (page == "routes") LoadRoutes();
            return false;
        }
        catch (Exception ex)
        {
            Report($"助手重启未完成：{ex.Message}；当前窗口保留。");
            return false;
        }
        finally
        {
            IsRestarting = false;
            Refresh();
            // A rejected restart leaves the current page selected even though another navigation button was clicked.
            Notify(nameof(IsHomePage));
            Notify(nameof(IsDailyPage));
            Notify(nameof(IsRealtimePage));
            Notify(nameof(IsNavigationPage));
            Notify(nameof(IsRoutesPage));
            Notify(nameof(IsSettingsPage));
            _navigating = false;
        }
    }
    private bool SaveSettings(AssistantSettings settings)
    {
        if (Settings == settings && !HasSettingsError) return true;
        var saved = true;
        if (automation is not null) saved = automation.SaveSettings(settings);
        else _localSettings = settings;
        Refresh();
        Notify(string.Empty);
        return saved;
    }
    private static string SelectionText((bool Selected, string Name)[] choices)
    {
        var selected = choices.Where(x => x.Selected).Select(x => x.Name).ToArray();
        return selected.Length == 0 ? "尚未选择功能" : string.Join(" · ", selected);
    }

    public void Connect() { if (IsRestarting || IsBusy) return; preview.Start(); Refresh(); }
    public async Task StopAsync() { await StopTaskAsync(); var stop = preview.StopAsync(); Refresh(); await stop; Refresh(); }
    public bool CanStartTask => Runner is not null && !IsBusy && !IsRestarting;
    public bool CanStopTask => Runner?.IsRunning == true;
    public bool IsPaused => Runner?.IsPaused == true;
    public bool RequiresElevation => Runner?.RequiresElevation == true;
    public void RestartElevated() => restartElevated?.Invoke();
    public void Report(string message) { log?.Write(message); Refresh(); }
    public string TaskStatus => IsPaused ? $"功能已暂停；按 {EffectivePauseHotkey} 继续" : Runner?.Status ?? "等待开始";
    public string? LogError => log?.StorageError;
    public bool HasLogError => !string.IsNullOrEmpty(LogError);
    public void StartTask() { if (!IsRestarting && !IsBusy && task is not null) Runner?.Start(task); Refresh(); }
    public async Task StopTaskAsync() { if (automation is not null) await automation.StopAsync(); else if (Runner is not null) await Runner.StopAsync(); Refresh(); }
    public async Task<PauseToggleResult> TogglePauseAsync()
    {
        var result = Runner is not null && !IsRestarting ? await Runner.TogglePauseAsync() : PauseToggleResult.None;
        Refresh();
        return result;
    }
    public void OpenLogs()
    {
        if (log is null) return;
        try { log.Maintain(); Process.Start(new ProcessStartInfo(log.DirectoryPath) { UseShellExecute = true }); }
        catch (Exception ex) { log.Write($"无法打开日志文件夹：{ex.Message}"); }
    }

    public void Refresh()
    {
        if (_displayedDailyOrder != Settings.DailyTaskOrder)
        {
            _displayedDailyOrder = Settings.DailyTaskOrder;
            var expanded = OrderedDailyTasks.Where(x => x.IsExpanded).Select(x => x.Id).ToHashSet();
            OrderedDailyTasks.Clear();
            try { foreach (var choice in DailyFeatureOrder.Resolve(Settings.DailyTaskOrder)) OrderedDailyTasks.Add(new(choice, this) { IsExpanded = expanded.Contains(choice.Id) }); }
            catch (System.IO.InvalidDataException) { /* SettingsError reports the invalid order; execution stays disabled. */ }
            Notify(nameof(DailySelectionText));
        }
        foreach (var choice in OrderedDailyTasks) choice.Refresh();
        var current = preview.Current;
        // A busy UI must not show a previously received frame as live after it ages out.
        var now = TimeSpan.FromSeconds((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency);
        var frame = current.Frame;
        if (frame is not null && now - frame.SystemTimestamp > TimeSpan.FromSeconds(1)) frame = null;
        IsLive = current.State == PreviewState.Live && frame is not null;
        IsFaulted = current.State == PreviewState.Faulted;
        StateText = current.State switch
        {
            PreviewState.Live => IsLive ? "已连接" : "等待新画面",
            PreviewState.Connecting => "连接中",
            PreviewState.Stopping => "停止中",
            PreviewState.Faulted => "连接中断",
            _ => "未连接"
        };
        Message = current.Message;
        // Preview rendering is independent of the frame feed used by running tasks.
        var displayFrame = ShowPreview ? frame : null;
        var previewInterval = TimeSpan.FromMilliseconds(EffectiveSettings.LowPerformanceMode ? 400 : 200);
        if (displayFrame is null || !ReferenceEquals(_displayedFrame, displayFrame)
            && (PreviewImage is null || now - _lastPreviewUpdate >= previewInterval))
        {
            _displayedFrame = displayFrame;
            PreviewImage = null;
            if (displayFrame is not null)
            {
                PreviewImage = BitmapSource.Create(displayFrame.Width, displayFrame.Height, 96, 96, PixelFormats.Bgr32, null,
                    displayFrame.Pixels.ToArray(), displayFrame.Stride);
                PreviewImage.Freeze();
                _lastPreviewUpdate = now;
            }
        }
        FrameInfo = frame is null ? "等待画面" : $"{frame.Width} × {frame.Height}  ·  帧 #{frame.Id}";
        FrameTime = frame is null ? "—" : $"更新于 {frame.CapturedAt:HH:mm:ss}";
        if (log is not null)
        {
            log.Maintain();
            foreach (var entry in log.ReadAfter(_lastLog))
            { Logs.Insert(0, entry.ToString()); _lastLog = entry.Id; }
            while (Logs.Count > 200) Logs.RemoveAt(Logs.Count - 1);
        }
        else if (_lastSnapshot?.State != current.State || _lastSnapshot.Message != current.Message)
        {
            Logs.Insert(0, $"{DateTime.Now:HH:mm:ss}   {current.Message}");
            if (Logs.Count > 80) Logs.RemoveAt(Logs.Count - 1);
        }
        _lastSnapshot = current;
        // IME composition defers text source updates; polling must not push old editable values back into controls.
        Notify(nameof(PreviewImage));
        Notify(nameof(StateText));
        Notify(nameof(Message));
        Notify(nameof(FrameInfo));
        Notify(nameof(FrameTime));
        Notify(nameof(IsLive));
        Notify(nameof(CanConnect));
        Notify(nameof(CanStop));
        Notify(nameof(ShowPlaceholder));
        Notify(nameof(IsFaulted));
        Notify(nameof(PreviewHint));
        Notify(nameof(ConnectLabel));
        Notify(nameof(CanConfigure));
        Notify(nameof(CanStartDaily));
        Notify(nameof(CanStartRealtime));
        Notify(nameof(CanStartNavigation));
        Notify(nameof(CanMoveToCoordinate));
        Notify(nameof(NavigationCoordinateText));
        Notify(nameof(NavigationMapText));
        RefreshRouteState();
        Notify(nameof(NavigationObservationText));
        Notify(nameof(IsRestarting));
        Notify(nameof(RestartRequested));
        Notify(nameof(CanStartTask));
        Notify(nameof(CanStopTask));
        Notify(nameof(IsPaused));
        Notify(nameof(RequiresElevation));
        Notify(nameof(TaskStatus));
        Notify(nameof(LogError));
        Notify(nameof(HasLogError));
        Notify(nameof(SettingsError));
        Notify(nameof(HasSettingsError));
        Notify(nameof(WeeklyProgressText));
        Notify(nameof(CommissionError));
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
