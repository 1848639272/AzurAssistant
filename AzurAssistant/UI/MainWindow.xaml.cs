using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using AzurAssistant.Platform;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using AzurAssistant.Runtime;
using AzurAssistant.Contracts;
using AzurAssistant.Features.NavigationTest;

namespace AzurAssistant.UI;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _refresh;
    private bool _closing;
    private bool _closedCleanly;
    private bool _navigating;
    private readonly Func<Task<bool>> _confirmSettingsRestart;
    private readonly Action<string> _showHotkeyMessage;
    public bool IsCapturingHotkey { get; private set; }
    public event Action<bool>? HotkeyCaptureChanged;
    private int _mascotPose;
    private Point _dailyDragStart;
    private Point _dailyDragOffset;
    private string? _dailyDragId;
    private Border? _dailyDragTile;
    private Border? _dailyDropTarget;
    private const string DailyDragFormat = "AzurAssistant.DailyTask";
    private static readonly (string File, string Label)[] MascotPoses =
    [
        ("cabbird-64.png", "小跑"), ("cabbird-hop-64.png", "蹦跳"),
        ("cabbird-wave-64.png", "招手"), ("cabbird-peek-64.png", "探头")
    ];
    private bool _chromeReported;
    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel, Func<Task<bool>>? confirmSettingsRestart = null,
        Action<string>? showHotkeyMessage = null)
    {
        InitializeComponent();
        _appearanceCommit.Tick += OnAppearanceCommitTick;
        _appearanceApply.Tick += OnAppearanceApplyTick;
        System.Windows.Media.CompositionTarget.Rendering += OnFrameRendered;
        DataContext = ViewModel = viewModel;
        ApplyAppearance();
        _confirmSettingsRestart = confirmSettingsRestart ?? (() => Task.FromResult(MessageBox.Show(this,
            "设置已经修改，是否立即重启助手生效？", "蔚蓝助手", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes));
        _showHotkeyMessage = showHotkeyMessage ?? (message => MessageBox.Show(this, message, "蔚蓝助手",
            MessageBoxButton.OK, MessageBoxImage.Information));
        Activated += (_, _) => UpdateHotkeyCapture();
        Deactivated += (_, _) => { ViewModel.CommitHotkeyEdit(); SetHotkeyCapture(false); };
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => ViewModel.Refresh(), Dispatcher);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshMonitors();
        ApplyOverlays();
        _uidCover = new UidCoverController(this, ViewModel.SetCoverUidStatus);
        _uidCover.BoxChanged += (index, box) =>
        {
            ViewModel.SaveCoverBoxAt(index, box);
            _uidCover?.Update(ViewModel.CoverUidRequest);
        };
        _uidCover.Start();
        ApplyUidCover();
        ViewModel.RefreshCoverBoxRows();
        CoverStyleBox.SelectedIndex = ViewModel.CoverUidStyle == "mosaic" ? 1 : 0;
        ViewModel.LoadReminderState();
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _reminderTimer.Tick += (_, _) =>
        {
            foreach (var reminder in ViewModel.TakeDueReminders()) ShowToast(reminder.Title, reminder.Message);
        };
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _reminderTimer.Start();
        TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _progressTimer.Tick += (_, _) => UpdateTaskbarProgress();
        _progressTimer.Start();
        if (ViewModel.TaskRunnerInternal is { } runner) runner.RunCompleted += result => Dispatcher.BeginInvoke(() => OnRunCompleted(result));
        ApplyAppearance();
        _refresh.Start();
        await CheckUpdatesAsync(automatic: true);
        if (!_closing && !_updateLaunched && !ViewModel.RestartRequested) ViewModel.Initialize();
    }
    private void OnConnect(object sender, RoutedEventArgs e) => ViewModel.Connect();
    private async void OnStop(object sender, RoutedEventArgs e) => await ViewModel.StopAsync();
    private void OnStartDaily(object sender, RoutedEventArgs e) => ViewModel.StartDaily();
    private void OnSaveCommissions(object sender, RoutedEventArgs e) => ViewModel.SaveCommissionSettings();
    private void OnResetWeekly(object sender, RoutedEventArgs e) => ViewModel.ResetWeeklyProgress();
    private void OnStartRealtime(object sender, RoutedEventArgs e) => ViewModel.StartRealtime();
    private void OnNavigationObserve(object sender, RoutedEventArgs e) => ViewModel.StartNavigation(NavigationTestMode.Observe);
    private void OnNavigationTeleport(object sender, RoutedEventArgs e) => ViewModel.StartNavigation(NavigationTestMode.Teleport);
    private void OnNavigationMove(object sender, RoutedEventArgs e) => ViewModel.StartNavigation(NavigationTestMode.Move);
    private void OnUseObservedCoordinate(object sender, RoutedEventArgs e) => ViewModel.UseObservedCoordinate();
    private void OnBeginRouteRecording(object sender, RoutedEventArgs e) => ViewModel.BeginRouteRecording();
    private async void OnCaptureRoutePoint(object sender, RoutedEventArgs e)
    {
        await ViewModel.CaptureRoutePointAsync();
        if (!_closing) Activate();
    }
    private void OnCancelRouteRecording(object sender, RoutedEventArgs e) => ViewModel.CancelRouteRecording();
    private void OnSaveRecordedRoute(object sender, RoutedEventArgs e) => ViewModel.SaveRecordedRoute();
    private void OnRunRoute(object sender, RoutedEventArgs e) => ViewModel.RunSelectedRoute();
    private void OnRemoveRoutePoint(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: RoutePointDraft point }) ViewModel.RemoveRecordedPoint(point); }
    private void OnImportRoute(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入蔚蓝助手路线", Filter = "路线 JSON (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) ViewModel.ImportRoute(dialog.FileName);
    }
    private void OnExportRoute(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRoute is not { } route) return;
        var dialog = new SaveFileDialog { Title = "导出路线", Filter = "路线 JSON (*.json)|*.json", FileName = System.IO.Path.GetFileName(route.Path), DefaultExt = ".json" };
        if (dialog.ShowDialog(this) == true) ViewModel.ExportSelectedRoute(dialog.FileName);
    }
    private async void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (_navigating || _closing || sender is not FrameworkElement { Tag: string page }) return;
        ViewModel.CommitHotkeyEdit();
        _navigating = true;
        try
        {
            if (await ViewModel.NavigateAsync(page, _confirmSettingsRestart)) Close();
            else ViewModel.Refresh();
        }
        finally { _navigating = false; }
    }
    private void OnChooseGameExecutable(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择游戏启动文件", Filter = "游戏程序 (*.exe)|*.exe", CheckFileExists = true,
            Multiselect = false, FileName = ViewModel.GameExecutablePath
        };
        if (dialog.ShowDialog(this) == true) ViewModel.GameExecutablePath = dialog.FileName;
    }
    private async void OnCancelTask(object sender, RoutedEventArgs e) => await ViewModel.StopTaskAsync();
    private void OnBeginActionRecording(object sender, RoutedEventArgs e) => ViewModel.BeginActionRecording();
    private async void OnStopActionRecording(object sender, RoutedEventArgs e) => await ViewModel.StopActionRecordingAsync();
    private void OnRefreshActions(object sender, RoutedEventArgs e) => ViewModel.LoadActions();
    private void OnPlayAction(object sender, RoutedEventArgs e) => ViewModel.PlaySelectedAction();
    private void OnRenameAction(object sender, RoutedEventArgs e) => ViewModel.RenameSelectedAction();
    private void OnDeleteAction(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAction is not { } selected) return;
        if (MessageBox.Show($"确定删除动作“{selected.Name}”？此操作不可撤销。", "删除动作",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        ViewModel.DeleteSelectedAction();
    }
    private readonly DispatcherTimer _appearanceCommit = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private ThemePalette? _palette;
    private readonly DispatcherTimer _appearanceApply = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _appearanceDirty;
    private System.Windows.Media.Color? _accentApplied;
    private int _applyCount;
    private double _applyTotalMs;
    private double _applyMaxMs;
    private int _appliedUi = -1;
    private int _appliedText = -1;
    private int _appliedBackground = -1;
    private void OnDarkModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        // Base colours change with the theme, so this is the one path that rebuilds the palette.
        ApplyAppearance();
        ViewModel.Refresh();
    }

    private void OnAppearanceSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        QueueAppearanceUpdate();
    }

    /// <summary>Coalesces rapid slider ticks into at most one palette update per dispatcher pass.</summary>
    /// <summary>Applies the latest slider values at most once every 33 ms, so dragging stays smooth.</summary>
    private void QueueAppearanceUpdate()
    {
        _appearanceDirty = true;
        if (!_appearanceApply.IsEnabled)
        {
            // First movement applies immediately; later ticks are throttled by the timer.
            ApplyAppearanceFast();
            _appearanceDirty = false;
            _appearanceApply.Start();
        }
        // Persist once the drag settles instead of writing settings on every tick.
        _appearanceCommit.Stop();
        _appearanceCommit.Start();
    }

    private long _lastFrameTicks;
    private int _frameCount;
    private double _frameSumMs;
    private double _frameMaxMs;

    private void OnFrameRendered(object? sender, EventArgs e)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastFrameTicks != 0)
        {
            var ms = (now - _lastFrameTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (_appearanceApply.IsEnabled)
            {
                _frameCount++;
                _frameSumMs += ms;
                if (ms > _frameMaxMs) _frameMaxMs = ms;
            }
        }
        _lastFrameTicks = now;
    }

    private void OnAppearanceApplyTick(object? sender, EventArgs e)
    {
        if (_appearanceDirty)
        {
            _appearanceDirty = false;
            ApplyAppearanceFast();
        }
        else _appearanceApply.Stop();
    }

    private void OnAppearanceCommitTick(object? sender, EventArgs e)
    {
        _appearanceCommit.Stop();
        ViewModel.CommitAppearance();
        // Only log when the drag was actually slow, so normal use keeps the log clean.
        if (_applyCount >= 5 && _applyTotalMs / _applyCount > 12)
            ViewModel.Report($"外观偏慢：应用 {_applyCount} 次，平均 {_applyTotalMs / _applyCount:F1} ms，最长 {_applyMaxMs:F1} ms；帧 {_frameCount} 个，平均 {(_frameCount > 0 ? _frameSumMs / _frameCount : 0):F1} ms，最长 {_frameMaxMs:F1} ms；渲染层 {(System.Windows.Media.RenderCapability.Tier >> 16)}");
        _frameSumMs = 0;
        _frameMaxMs = 0;
        _applyCount = 0;
        _applyTotalMs = 0;
        _applyMaxMs = 0;
    }

    private void OnImportBackground(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择背景图", Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif",
            CheckFileExists = true, Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true) ViewModel.ImportBackgroundImage(dialog.FileName);
    }

    private void OnClearBackground(object sender, RoutedEventArgs e) => ViewModel.ClearBackgroundImage();

    private void OnResetAccent(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetAccent();
        QueueAppearanceUpdate();
    }

    /// <summary>Full rebuild: first apply and dark-mode switches (the base colours change).</summary>
    /// <summary>Full rebuild: first apply and dark-mode switches (the base colours change).</summary>
    private void ApplyAppearance()
    {
        try
        {
            // One palette per window; every later slider tick mutates these very brushes in place.
            _palette = ThemePalette.Apply(this, ViewModel.DarkMode, ViewModel.UiOpacityPercent,
                ViewModel.TextOpacityPercent, ViewModel.BackgroundOpacityPercent, ViewModel.AccentValue);
            ApplyWindowChrome();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { ViewModel.Report("应用外观失败：" + ex.Message); }
    }

    /// <summary>Slider path: only alpha (or the accent colour) changes, so the existing brushes are rewritten.</summary>
    private void ApplyAppearanceFast()
    {
        if (_palette is null) { ApplyAppearance(); return; }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        // Only the group whose slider moved is recomputed, so a single drag invalidates a third of the brushes.
        var ui = ViewModel.UiOpacityPercent;
        var text = ViewModel.TextOpacityPercent;
        var background = ViewModel.BackgroundOpacityPercent;
        if (ui != _appliedUi) { _palette.UpdateChromeOpacity(ui); _appliedUi = ui; }
        if (text != _appliedText) { _palette.UpdateTextOpacity(text); _appliedText = text; }
        if (background != _appliedBackground) { _palette.UpdateBackgroundOpacity(background); _appliedBackground = background; }
        var accent = ViewModel.AccentValue;
        if (_accentApplied != accent)
        {
            _palette.UpdateAccent(accent);
            _accentApplied = accent;
        }
        var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _applyCount++;
        _applyTotalMs += elapsedMs;
        if (elapsedMs > _applyMaxMs) _applyMaxMs = elapsedMs;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Colours the native caption and border so the frame matches the panel colours (Windows 11).</summary>
    private void ApplyWindowChrome()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        // Never create a new palette here: that used to orphan the live one and froze the sliders.
        var palette = _palette;
        if (palette is null) return;
        static int ToColorRef(System.Windows.Media.Color color) => color.R | (color.G << 8) | (color.B << 16);
        var caption = ToColorRef(palette.BaseColor("CardBackground"));
        var border = ToColorRef(palette.BaseColor("BorderBrush"));
        var text = ToColorRef(palette.BaseColor("Ink"));
        var dark = ViewModel.DarkMode ? 1 : 0;
        var results = new[]
        {
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)),
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int)),
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int)),
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int)),
        };
        if (results.Any(result => result != 0) || !_chromeReported)
        {
            _chromeReported = true;
            ViewModel.Report("窗口外框配色：" + string.Join(",", results) + $"；标题栏 #{caption:X6} 边框 #{border:X6}");
        }
    }

    private void OnAddScheduleTask(object sender, RoutedEventArgs e) => ViewModel.AddScheduleTask();
    private void OnRemoveScheduleTask(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScheduleTaskDraft draft }) ViewModel.RemoveScheduleTask(draft);
    }
    private void OnSaveScheduleTasks(object sender, RoutedEventArgs e) => ViewModel.SaveScheduleTasks();
    private void OnRunSelectedSchedule(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScheduleTaskDraft draft }) ViewModel.SelectedScheduleDraft = draft;
        ViewModel.RunSelectedScheduleNow();
    }
    private void OnRunFirstSchedule(object sender, RoutedEventArgs e) => ViewModel.RunFirstScheduleNow();
    private void OnPreviewStartSound(object sender, RoutedEventArgs e) => ViewModel.PreviewStartSound();
    private void OnPreviewEndSound(object sender, RoutedEventArgs e) => ViewModel.PreviewEndSound();
    private void OnImportStartSound(object sender, RoutedEventArgs e) => ImportSound(ViewModel.ImportStartSound);
    private void OnImportEndSound(object sender, RoutedEventArgs e) => ImportSound(ViewModel.ImportEndSound);
    private void ImportSound(Action<string> apply)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择提示音", Filter = "音频文件 (*.wav;*.mp3;*.wma)|*.wav;*.mp3;*.wma|所有文件 (*.*)|*.*",
            CheckFileExists = true, Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true) apply(dialog.FileName);
    }
    private void OnOpenPackFolder(object sender, RoutedEventArgs e) => OpenFolder(Path.Combine(AppContext.BaseDirectory, "语言包"));
    private void OnOpenScriptFolder(object sender, RoutedEventArgs e) => OpenFolder(Path.Combine(AppContext.BaseDirectory, "脚本"));
    private void OnOpenLanguageHelp(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "语言包", "README.txt");
        if (!File.Exists(path)) { ViewModel.Report("找不到 语言包\\README.txt"); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { ViewModel.Report("无法打开说明文件：" + path); }
    }
    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { ViewModel.Report("无法打开目录：" + path); }
    }
    private void OnOpenGitHub(object sender, RoutedEventArgs e) => OpenExternal("https://github.com/1848639272/AzurAssistant");
    private void OnOpenIssues(object sender, RoutedEventArgs e) => OpenExternal("https://github.com/1848639272/AzurAssistant/issues");
    private void OnCopyQqGroup(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText("1129014322");
            ViewModel.Report("QQ 群号 1129014322 已复制。");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        { ViewModel.Report("复制失败，请手动记下群号 1129014322。"); }
    }
    private void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { ViewModel.Report("无法打开浏览器：" + url); }
    }
    private void OnRemoveEditedRoutePoint(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RouteEditRow row }) ViewModel.RemoveEditedRoutePoint(row);
    }
    private void OnSaveEditedRoute(object sender, RoutedEventArgs e) => ViewModel.SaveEditedRoute();
    private async void OnAddEditedRoutePoint(object sender, RoutedEventArgs e) => await ViewModel.AddEditedRoutePointAsync();
    private void OnRemoveEditedActionRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ActionEditRow row }) ViewModel.RemoveEditedActionRow(row);
    }
    private void OnSaveEditedAction(object sender, RoutedEventArgs e) => ViewModel.SaveEditedAction();
    private void OnEditWheelchairPlan(object sender, RoutedEventArgs e) => ViewModel.EditSelectedWheelchairPlan();
    private void OnImportAction(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入动作", Filter = "动作文件 (*.json)|*.json", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true) ViewModel.ImportActionRecording(dialog.FileName);
    }
    private void OnExportAction(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAction is not { } selected) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出动作", FileName = selected.Name + ".json", Filter = "动作文件 (*.json)|*.json",
            DefaultExt = ".json", AddExtension = true,
        };
        if (dialog.ShowDialog(this) == true) ViewModel.ExportSelectedAction(dialog.FileName);
    }
    private void OnBeginWheelchairDraft(object sender, RoutedEventArgs e) => ViewModel.BeginWheelchairDraft();
    private async void OnCaptureWheelchairStep(object sender, RoutedEventArgs e) => await ViewModel.CaptureWheelchairStepAsync();
    private void OnRemoveWheelchairStep(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WheelchairStepDraft draft }) ViewModel.RemoveWheelchairStep(draft);
    }
    private void OnSaveWheelchairPlan(object sender, RoutedEventArgs e) => ViewModel.SaveWheelchairPlan();
    private void OnCancelWheelchairDraft(object sender, RoutedEventArgs e) => ViewModel.CancelWheelchairDraft();
    private void OnRunWheelchairPlan(object sender, RoutedEventArgs e) => ViewModel.RunWheelchairPlan();
    private void OnDeleteWheelchairPlan(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedWheelchairPlan is not { } selected) return;
        if (MessageBox.Show($"确定删除方案“{selected.Name}”？此操作不可撤销。", "删除方案",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        ViewModel.DeleteSelectedWheelchairPlan();
    }
    private void OnExportWheelchairPlan(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedWheelchairPlan is not { } selected) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出轮椅方案", FileName = selected.Name + ".json", Filter = "轮椅方案 (*.json)|*.json",
            DefaultExt = ".json", AddExtension = true,
        };
        if (dialog.ShowDialog(this) == true) ViewModel.ExportWheelchairPlan(dialog.FileName);
    }
    private void OnImportWheelchairPlan(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入轮椅方案", Filter = "轮椅方案 (*.json)|*.json", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true) ViewModel.ImportWheelchairPlan(dialog.FileName);
    }
    public async Task TogglePauseFromHotkeyAsync()
    {
        if (_closing || IsCapturingHotkey || ViewModel.IsRestarting) return;
        if (await ViewModel.TogglePauseAsync() == PauseToggleResult.None && !ViewModel.CanStopTask)
            _showHotkeyMessage("当前无暂停中任务");
    }
    public void ShowHotkeyFailure(string message)
    {
        if (!_closing && !ViewModel.IsRestarting) _showHotkeyMessage(message);
    }
    private void OnHotkeyFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        // GotKeyboardFocus must suspend registration before the next key is pressed. Lost focus settles after routing.
        if (e.RoutedEvent == Keyboard.GotKeyboardFocusEvent) UpdateHotkeyCapture();
        else
        {
            ViewModel.CommitHotkeyEdit();
            Dispatcher.BeginInvoke(UpdateHotkeyCapture, DispatcherPriority.Input);
        }
    }
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsCapturingHotkey) return;
        for (var element = e.OriginalSource as DependencyObject; element is not null;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element == PauseHotkeyBox || element == StopHotkeyBox) return;
        ViewModel.CommitHotkeyEdit();
        Keyboard.ClearFocus();
        SetHotkeyCapture(false);
    }
    private void UpdateHotkeyCapture() => SetHotkeyCapture(!_closing && IsActive && ViewModel.IsSettingsPage
        && (PauseHotkeyBox.IsKeyboardFocusWithin || StopHotkeyBox.IsKeyboardFocusWithin));
    private void SetHotkeyCapture(bool capturing)
    {
        if (capturing == IsCapturingHotkey) return;
        IsCapturingHotkey = capturing;
        HotkeyCaptureChanged?.Invoke(capturing);
    }
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string target }) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        e.Handled = true;
        if (e.IsRepeat) return;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
        uint flags = 0;
        if ((modifiers & ModifierKeys.Control) != 0) flags |= HotkeyBinding.Control;
        if ((modifiers & ModifierKeys.Alt) != 0) flags |= HotkeyBinding.Alt;
        if ((modifiers & ModifierKeys.Shift) != 0) flags |= HotkeyBinding.Shift;
        if ((modifiers & ModifierKeys.Windows) != 0 || key is Key.LWin or Key.RWin) flags |= 8;
        if (HotkeyBinding.TryFromVirtualKey(flags, (uint)KeyInterop.VirtualKeyFromKey(key), out var binding, out var error))
            ViewModel.RecordHotkey(target == "pause", binding.Text);
        else ViewModel.ShowHotkeyValidationError(error!);
    }
    private void OnOpenLogs(object sender, RoutedEventArgs e) => ViewModel.OpenLogs();
    private void OnRestartElevated(object sender, RoutedEventArgs e) => ViewModel.RestartElevated();

    private void OnDailyDragStart(object sender, MouseButtonEventArgs e)
    {
        _dailyDragStart = e.GetPosition(this);
        _dailyDragId = (sender as FrameworkElement)?.DataContext is DailyTaskOption row ? row.Id : null;
        _dailyDragTile = null;
        for (var parent = sender as DependencyObject; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not Border { Name: "DailyTaskTile" } tile) continue;
            _dailyDragTile = tile;
            _dailyDragOffset = e.GetPosition(tile);
            break;
        }
    }

    private void OnDailyDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dailyDragId = null; _dailyDragTile = null; return; }
        if (_dailyDragId is null || sender is not DependencyObject source) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dailyDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dailyDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        try
        {
            if (_dailyDragTile is { } tile)
                ShowDailyDragPreview(tile, _dailyDragOffset, e.GetPosition(DailyDragSurface));
            DragDrop.DoDragDrop(source, new DataObject(DailyDragFormat, _dailyDragId), DragDropEffects.Move);
        }
        finally
        {
            _dailyDragId = null;
            _dailyDragTile = null;
            ClearDailyDragPreview();
            ClearDailyDrop();
        }
    }

    private void ShowDailyDragPreview(Border tile, Point grabPoint, Point pointer)
    {
        if (tile.ActualWidth <= 0 || tile.ActualHeight <= 0) return;
        var size = new Size(tile.ActualWidth, tile.ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(tile);
        var bounds = new Rect(size);
        // Collapsed settings can retain larger descendant bounds; capture the current card in local DIP coordinates.
        var brush = new VisualBrush(tile)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = bounds,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = bounds,
            Stretch = Stretch.None
        };
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(brush, null, bounds);
        // Capture once in the source DPI; pointer positions stay in WPF device-independent units.
        var snapshot = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(size.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        snapshot.Render(drawing);
        snapshot.Freeze();
        _dailyDragOffset = grabPoint;
        DailyDragImage.Source = snapshot;
        DailyDragImage.Width = size.Width;
        DailyDragImage.Height = size.Height;
        MoveDailyDragPreview(pointer);
    }

    private void MoveDailyDragPreview(Point pointer)
    {
        Canvas.SetLeft(DailyDragImage, pointer.X - _dailyDragOffset.X);
        Canvas.SetTop(DailyDragImage, pointer.Y - _dailyDragOffset.Y);
        DailyDragOverlay.Visibility = Visibility.Visible;
    }

    private void OnDailyPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DailyDragFormat)) return;
        // Only a sortable row's bubbling handler may authorize a move; fixed rows and blank space cannot.
        e.Effects = DragDropEffects.None;
        if (DailyDragImage.Source is not null && e.Data.GetData(DailyDragFormat) as string == _dailyDragId)
            MoveDailyDragPreview(e.GetPosition(DailyDragSurface));
    }

    private void OnDailyPreviewDragLeave(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DailyDragFormat)) return;
        DailyDragOverlay.Visibility = Visibility.Collapsed;
        ClearDailyDrop();
    }

    private void OnDailyDragOutside(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DailyDragFormat)) return;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDailyPreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DailyDragFormat)) return;
        e.Effects = DragDropEffects.None;
        ClearDailyDragPreview();
        ClearDailyDrop();
    }

    private void OnDailyQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (!e.EscapePressed && e.KeyStates.HasFlag(DragDropKeyStates.LeftMouseButton)) return;
        ClearDailyDragPreview();
        ClearDailyDrop();
    }

    private void ClearDailyDragPreview()
    {
        DailyDragOverlay.Visibility = Visibility.Collapsed;
        DailyDragImage.Source = null;
    }

    private void OnDailyDragOver(object sender, DragEventArgs e)
    {
        var source = e.Data.GetData(DailyDragFormat) as string;
        var accepts = source is not null && ViewModel.OrderedDailyTasks.Any(x => x.Id == source);
        e.Effects = accepts ? DragDropEffects.Move : DragDropEffects.None;
        ClearDailyDrop();
        if (sender is Border border && accepts)
        {
            var after = e.GetPosition(border).Y >= border.ActualHeight / 2;
            DropIndicator(border, after ? "DropAfter" : "DropBefore").Visibility = Visibility.Visible;
            _dailyDropTarget = border;
        }
        e.Handled = true;
    }

    private static Border DropIndicator(Border row, string name)
        => ((Grid)row.Child).Children.OfType<Border>().Single(x => x.Name == name);

    private void OnDailyDragLeave(object sender, DragEventArgs e) => ClearDailyDrop();
    private void ClearDailyDrop()
    {
        if (_dailyDropTarget is null) return;
        DropIndicator(_dailyDropTarget, "DropBefore").Visibility = Visibility.Collapsed;
        DropIndicator(_dailyDropTarget, "DropAfter").Visibility = Visibility.Collapsed;
        _dailyDropTarget = null;
    }
    private void OnDailyDrop(object sender, DragEventArgs e)
    {
        ClearDailyDrop();
        if (sender is Border { DataContext: DailyTaskOption row } border
            && e.Data.GetData(DailyDragFormat) is string source
            && ViewModel.OrderedDailyTasks.Any(x => x.Id == source))
        {
            ViewModel.MoveDailyTask(source, row.Id, e.GetPosition(border).Y >= border.ActualHeight / 2);
            e.Effects = DragDropEffects.Move;
        }
        e.Handled = true;
    }

    private void OnChangeMascotPose(object sender, RoutedEventArgs e)
    {
        _mascotPose = (_mascotPose + 1) % MascotPoses.Length;
        var pose = MascotPoses[_mascotPose];
        MascotImage.Source = new BitmapImage(new Uri($"/AzurAssistant;component/assets/branding/{pose.File}", UriKind.Relative));
        AutomationProperties.SetName(MascotButton, $"菜鸡：{pose.Label}，点击切换动作");
        var bounce = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        MascotScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        MascotScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closedCleanly) return;
        e.Cancel = true;
        if (_closing) return;

        // Ask only for a user-initiated close; restarts, updates and shutdown paths must not block on a dialog.
        if (!SkipClosePrompt && !ViewModel.RestartRequested && !_updateLaunched && !ViewModel.IsRestarting)
        {
            var decision = ViewModel.CloseAction == "ask" ? AskCloseDecision() : ToDecision(ViewModel.CloseAction);
            if (decision == CloseDecision.Cancel) return;
            if (decision == CloseDecision.Tray && ViewModel.ShowTrayIcon)
            {
                Hide();
                _tray?.Show();
                return;
            }
        }
        _closing = true;

        // The HUD and the toast are independent top-level windows: closing only the main window would leave them
        // on screen (and, with ShutdownMode.OnLastWindowClose, keep the process alive).
        CloseOverlays();

        ViewModel.CommitHotkeyEdit();
        _updateCancellation?.Cancel();
        ClearDailyDragPreview();
        ClearDailyDrop();
        IsEnabled = false;
        _refresh.Stop();
        await ViewModel.StopAsync();
        _closedCleanly = true;
        // A stopped session may complete synchronously; never reenter Window.Close from its Closing event.
        _ = Dispatcher.BeginInvoke(Close);
    }

    /// <summary>Closes the floating panel, any toast and the tray icon so nothing survives the exit.</summary>
    private void CloseOverlays()
    {
        _uidCover?.Stop();
        _uidCover = null;
        _reminderTimer?.Stop();
        _reminderTimer = null;
        if (_hud is not null)
        {
            _hud.Close();
            _hud = null;
        }
        if (_toast is not null)
        {
            _toast.Close();
            _toast = null;
        }
        _tray?.Dispose();
        _tray = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Guarantee the process ends even if some overlay slipped through.
        System.Windows.Application.Current?.Shutdown();
    }
    /// <summary>Set by shutdown/restart paths that must not show the close prompt.</summary>
    internal static bool SkipClosePrompt { get; set; }

    private static CloseDecision ToDecision(string action) => action switch
    {
        "tray" => CloseDecision.Tray,
        "exit" => CloseDecision.Exit,
        _ => CloseDecision.Exit,
    };

    /// <summary>Shows the close dialog and stores the answer when the user asked to remember it.</summary>
    private CloseDecision AskCloseDecision()
    {
        try
        {
            var dialog = new CloseConfirmWindow(ViewModel.ShowTrayIcon) { Owner = this };
            if (dialog.ShowDialog() != true) return CloseDecision.Cancel;
            if (dialog.Remember)
                ViewModel.RememberCloseAction(dialog.Decision == CloseDecision.Tray ? "tray" : "exit");
            return dialog.Decision;
        }
        catch (Exception ex) when (ex is System.Windows.Markup.XamlParseException or InvalidOperationException)
        {
            // Never block closing because the dialog failed to build.
            ViewModel.Report("关闭确认框打开失败：" + ex.Message);
            var fallback = MessageBox.Show(this, "要完全关闭助手吗？\n选择「否」会隐藏到系统托盘。", "蔚蓝助手",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            return fallback == MessageBoxResult.Yes ? CloseDecision.Exit
                : ViewModel.ShowTrayIcon ? CloseDecision.Tray : CloseDecision.Exit;
        }
    }

    // ---- 任务编排 ----
    private void OnChainSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => ViewModel.SelectChain(ChainBox.SelectedIndex);

    private async void OnRunChain(object sender, RoutedEventArgs e) => await ViewModel.RunSelectedChainAsync();
    private void OnRefreshChains(object sender, RoutedEventArgs e) => ViewModel.RefreshChains();

    private void OnImportChain(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入任务队列", Filter = "队列 JSON (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) ViewModel.ImportChain(dialog.FileName);
    }

    private void OnExportChain(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedChain is not { } chain) { ViewModel.Report("请先选择一个队列。"); return; }
        var dialog = new SaveFileDialog
        {
            Title = "导出任务队列", Filter = "队列 JSON (*.json)|*.json", FileName = chain.Name + ".json",
        };
        if (dialog.ShowDialog(this) == true) ViewModel.ExportSelectedChain(dialog.FileName);
    }

    private void OnDeleteChain(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedChain is not { } chain) return;
        if (MessageBox.Show(this, $"确定删除队列「{chain.Name}」吗？", "任务编排",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ViewModel.DeleteSelectedChain();
    }

    // ---- 流程 ----
    private void OnRunFlow(object sender, RoutedEventArgs e) => ViewModel.RunSelectedFlow();
    private void OnRefreshFlows(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshFlows();
        ViewModel.RefreshFlowActionSources();
        ViewModel.RefreshFlowProgress();
    }

    private void OnConvertActionToFlow(object sender, RoutedEventArgs e) => ViewModel.ConvertActionToFlow();

    private void OnResumeFlow(object sender, RoutedEventArgs e) => ViewModel.ResumeSelectedFlow();

    private void OnClearFlowProgress(object sender, RoutedEventArgs e) => ViewModel.ClearFlowProgress();

    private void OnImportFlow(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入流程", Filter = "流程 JSON (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) ViewModel.ImportFlow(dialog.FileName);
    }

    private void OnExportFlow(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFlow is null) { ViewModel.Report("请先选择一个流程。"); return; }
        var dialog = new SaveFileDialog
        {
            Title = "导出流程", Filter = "流程 JSON (*.json)|*.json",
            FileName = ViewModel.SelectedFlow.Name + ".json",
        };
        if (dialog.ShowDialog(this) == true) ViewModel.ExportSelectedFlow(dialog.FileName);
    }

    private void OnDeleteFlow(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFlow is null) return;
        if (MessageBox.Show(this, $"确定删除流程「{ViewModel.SelectedFlow.Name}」吗？", "流程",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ViewModel.DeleteSelectedFlow();
    }

    // ---- 模板标注 ----
    private void OnAnnotateTemplate(object sender, RoutedEventArgs e)
    {
        var frame = ViewModel.FailureFrames.FirstOrDefault()?.ImagePath;
        if (frame is null)
        {
            var pick = new OpenFileDialog { Title = "选择一张画面截图", Filter = "图片 (*.png;*.jpg)|*.png;*.jpg", CheckFileExists = true };
            if (pick.ShowDialog(this) != true) return;
            frame = pick.FileName;
        }
        var window = new TemplateAnnotatorWindow(frame, ViewModel.TemplatePackDirectories) { Owner = this };
        if (window.ShowDialog() == true)
        {
            ViewModel.Report("模板标注：" + window.ResultMessage);
            MessageBox.Show(this, window.ResultMessage, "模板标注", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ---- 配置档 ----
    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveCurrentProfile();
    }

    private void OnApplyProfile(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"将把配置档「{ViewModel.SelectedProfile?.Name}」覆盖到当前数据（现有数据会先备份），继续吗？",
                "应用配置档", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!ViewModel.ApplySelectedProfile(out var message)) { MessageBox.Show(this, message, "配置档", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, message + "\n\n现在重启助手吗？", "配置档", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes)
            ViewModel.TryRestartAssistant();
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProfile is null) return;
        if (MessageBox.Show(this, $"确定删除配置档「{ViewModel.SelectedProfile.Name}」吗？", "配置档",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ViewModel.DeleteSelectedProfile();
    }

    // ---- 养成计算 ----
    private async void OnUpdateGrowthData(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "将从 promilia.wiki 下载最新养成数据并覆盖本地缓存，是否继续？", "养成数据",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await ViewModel.UpdateGrowthDataAsync(null);
    }

    private void OnExportGrowthCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出养成方案（CSV）", Filter = "CSV (*.csv)|*.csv", FileName = $"养成方案-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dialog.ShowDialog(this) == true) ViewModel.ExportGrowthCsv(dialog.FileName);
    }

    private void OnExportGrowthPng(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出养成方案（PNG）", Filter = "PNG (*.png)|*.png", FileName = $"养成方案-{DateTime.Now:yyyyMMdd-HHmm}.png",
        };
        if (dialog.ShowDialog(this) == true) ViewModel.ExportGrowthPng(dialog.FileName);
    }

    private void OnImportGrowthData(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入养成数据 JSON", Filter = "JSON (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        ViewModel.ImportGrowthData(dialog.FileName);
    }

    // ---- 工具与诊断 ----
    private void OnRefreshLanguagePacks(object sender, RoutedEventArgs e) => ViewModel.RefreshLanguagePacks();

    private async void OnApplyLanguagePack(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedLanguagePack is not { Ready: true } pack) { ViewModel.Report("请选择一个可用的语言包。"); return; }
        var confirm = MessageBox.Show(this,
            $"即将把游戏语言切换为：{pack.Label}\n\n脚本会自动备份原文件，切换期间请确保游戏已关闭。是否继续？",
            "切换游戏语言", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        await ViewModel.ApplySelectedLanguagePackAsync();
    }

    private async void OnRestoreLanguage(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this, "将从最近的备份还原游戏语言文件，是否继续？",
            "还原语言文件", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        await ViewModel.RestoreLanguageAsync();
    }

    private void OnExportDiagnosticBundle(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出诊断包",
            Filter = "Zip 压缩包 (*.zip)|*.zip",
            FileName = $"AzurAssistant-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        if (dialog.ShowDialog(this) != true) return;
        if (ViewModel.ExportDiagnosticBundle(dialog.FileName))
            MessageBox.Show(this, "诊断包已导出：\n" + dialog.FileName, "蔚蓝助手", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnInstallTemplatePackFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择模板包", Filter = "Zip 压缩包 (*.zip)|*.zip", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        await InstallTemplatePackAsync(dialog.FileName);
    }

    private async void OnInstallTemplatePackFromSource(object sender, RoutedEventArgs e)
    {
        var url = TemplatePackUpdater.SourceUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            ViewModel.Report("update-source.json 里没有配置 TemplatePackUrl，请改用「选择模板包 zip…」。");
            return;
        }
        await InstallTemplatePackAsync(url);
    }

    private async Task InstallTemplatePackAsync(string source)
    {
        if (MessageBox.Show(this, "即将替换识别模板（会先自动备份原文件）。确定继续吗？", "模板热更新",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await ViewModel.InstallTemplatePackAsync(source);
    }

    private void OnRefreshFailureFrames(object sender, RoutedEventArgs e) => ViewModel.RefreshFailureFrames();

    private void OnDeleteFailureFrames(object sender, RoutedEventArgs e)
    {
        if (ViewModel.FailureFrames.Count == 0) return;
        if (MessageBox.Show(this, $"确定删除全部 {ViewModel.FailureFrames.Count} 个失败帧吗？", "蔚蓝助手",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ViewModel.DeleteFailureFrames();
    }

    private void OnRegressionOnFrame(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FailureFrameRow row }) return;
        ViewModel.RunRegressionOnFrame(row);
    }

    private void OnOpenFailureFrame(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // The per-row 复测 button raises its own click; do not also open the image.
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null) return;
        if (sender is not FrameworkElement { DataContext: FailureFrameRow row }) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(row.ImagePath) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { ViewModel.Report("打开失败帧失败：" + ex.Message); }
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private void OnRefreshRunHistory(object sender, RoutedEventArgs e) => ViewModel.RefreshRunHistory();

    private void OnExportRunHistory(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出运行记录",
            Filter = "CSV 表格 (*.csv)|*.csv",
            FileName = $"AzurAssistant-运行记录-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dialog.ShowDialog(this) != true) return;
        if (ViewModel.ExportRunHistory(dialog.FileName))
            MessageBox.Show(this, "已导出：\n" + dialog.FileName, "蔚蓝助手", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnClearRunHistory(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "确定清空全部运行记录吗？", "蔚蓝助手",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ViewModel.ClearRunHistory();
    }

    // ---- 扩展功能：UID 遮挡 ----
    private UidCoverController? _uidCover;
    private DispatcherTimer? _reminderTimer;
    private DispatcherTimer? _progressTimer;
    private bool _coverAdjusting;

    private void OnToggleCoverAdjust(object sender, RoutedEventArgs e)
    {
        if (_uidCover is null) return;
        _coverAdjusting = !_coverAdjusting;
        _uidCover.SetAdjusting(_coverAdjusting);
        CoverAdjustButton.Content = _coverAdjusting ? "退出调整（保存）" : "调整位置（拖动）";
    }

    private void OnCoverStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires while the XAML is still loading; the view model is not attached yet.
        if (!IsLoaded) return;
        if (CoverStyleBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        ViewModel.CoverUidStyle = tag;
        ApplyUidCover();
    }

    private void OnAddCoverBox(object sender, RoutedEventArgs e)
    {
        ViewModel.AddCoverBox();
        ApplyUidCover();
    }

    private void OnRemoveCoverBox(object sender, RoutedEventArgs e)
    {
        ViewModel.RemoveCoverBox(ViewModel.CoverBoxRows.Count - 1);
        ApplyUidCover();
    }

    private void OnPickCoordinate(object sender, RoutedEventArgs e)
    {
        var picker = new PointPickerWindow { Owner = this };
        if (picker.ShowDialog() != true || picker.ScreenPoint is not { } point) return;
        ViewModel.PickCoordinateAt((int)Math.Round(point.X), (int)Math.Round(point.Y), picker.ColorHex);
    }

    private void OnCopyPickedCoordinate(object sender, RoutedEventArgs e)
    {
        var snippet = ViewModel.PickedSnippet;
        if (snippet.Length == 0) return;
        try { Clipboard.SetText(snippet); } catch (System.Runtime.InteropServices.COMException) { }
    }

    private void OnClearPickedCoordinate(object sender, RoutedEventArgs e) => ViewModel.ClearPickedCoordinate();

    private void OnResetCoverUid(object sender, RoutedEventArgs e)
    {
        _coverAdjusting = false;
        CoverAdjustButton.Content = "调整位置（拖动）";
        ViewModel.ResetCoverUid();
        _uidCover?.SetAdjusting(false);
        _uidCover?.Update(ViewModel.CoverUidRequest);
    }

    private void ApplyUidCover() => _uidCover?.Update(ViewModel.CoverUidRequest);

    // ---- 悬浮窗 / 托盘 / 通知 ----
    private HudWindow? _hud;
    private ToastWindow? _toast;
    private TrayIcon? _tray;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowHud) or nameof(MainViewModel.ShowTrayIcon)
            or nameof(MainViewModel.OverlayMonitor))
            ApplyOverlays();
        if (e.PropertyName is nameof(MainViewModel.CoverUidRequest)) ApplyUidCover();
    }

    /// <summary>Creates, places or hides the HUD and the tray icon according to the settings.</summary>
    private void ApplyOverlays()
    {
        var monitor = DisplayMonitors.Resolve(ViewModel.OverlayMonitor);
        if (ViewModel.ShowHud)
        {
            if (_hud is null)
            {
                _hud = new HudWindow { DataContext = ViewModel };
                _hud.Closed += (_, _) => _hud = null;
            }
            _hud.PlaceOn(monitor);
            if (!_hud.IsVisible) _hud.Show();
        }
        else if (_hud is { IsVisible: true }) _hud.Hide();

        if (ViewModel.ShowTrayIcon)
        {
            if (_tray is null)
            {
                _tray = new TrayIcon(this, "蔚蓝助手 - 双击显示窗口");
                _tray.Activated += ShowFromTray;
                _tray.MenuRequested += ShowTrayMenu;
            }
            _tray.Show();
        }
        else _tray?.Hide();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        var show = new MenuItem { Header = "显示主窗口" };
        show.Click += (_, _) => ShowFromTray();
        var pause = new MenuItem { Header = "暂停 / 继续" };
        pause.Click += async (_, _) => await ViewModel.TogglePauseAsync();
        var stop = new MenuItem { Header = "停止任务" };
        stop.Click += async (_, _) => await ViewModel.StopTaskAsync();
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(show);
        menu.Items.Add(new Separator());
        menu.Items.Add(pause);
        menu.Items.Add(stop);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        menu.IsOpen = true;
    }

    /// <summary>Drives the taskbar progress bar and the HUD "remaining" hint from the run history estimate.</summary>
    private void UpdateTaskbarProgress()
    {
        if (TaskbarItemInfo is not { } taskbar) return;
        var runner = ViewModel.TaskRunnerInternal;
        if (runner is null || !runner.IsRunning || runner.StartedAt is not { } started)
        {
            if (taskbar.ProgressState != System.Windows.Shell.TaskbarItemProgressState.None)
            {
                taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
                taskbar.Description = "";
            }
            return;
        }
        var elapsed = (DateTimeOffset.Now - started).TotalSeconds;
        var estimate = ViewModel.EstimateTaskSeconds(runner.CurrentTaskName);
        var remaining = TaskDurationEstimator.Remaining(elapsed, estimate);
        taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
        taskbar.ProgressValue = TaskDurationEstimator.Progress(elapsed, estimate, running: true);
        taskbar.Description = remaining.Length > 0 ? remaining : runner.CurrentTaskName;
        if (remaining.Length > 0 && ViewModel.HudDetail != remaining) ViewModel.SetHudDetail(remaining);
    }

    /// <summary>Shows a bottom-right notification on the configured monitor.</summary>
    private void ShowToast(string title, string message)
    {
        var toast = new ToastWindow();
        toast.Closed += (_, _) => { if (ReferenceEquals(_toast, toast)) _toast = null; };
        _toast = toast;
        toast.Present(title, message, DisplayMonitors.Resolve(ViewModel.OverlayMonitor));
    }

    private void OnRunCompleted(AzurAssistant.Contracts.TaskResult result)
    {
        if (!ViewModel.NotifyOnTaskEnd) return;
        ShowToast(result.Success ? "任务完成" : "任务失败", result.Message);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && ViewModel.MinimizeToTray && ViewModel.ShowTrayIcon)
        {
            Hide();
            _tray?.Show();
        }
    }
}
