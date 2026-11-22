using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
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
    private void OnDarkModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyAppearance();
        ViewModel.Refresh();
    }

    private void OnAppearanceSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        ApplyAppearance();
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

    /// <summary>Applies the palette with the three opacity sliders, the window opacity and the DWM frame colours.</summary>
    private void ApplyAppearance()
    {
        ThemePalette.Apply(this, ViewModel.DarkMode, ViewModel.UiOpacityPercent, ViewModel.TextOpacityPercent,
            ViewModel.BackgroundOpacityPercent);
        // Keep a floor so the window can never become completely unclickable.
        Opacity = Math.Max(10, ViewModel.WindowOpacityPercent) / 100.0;
        ApplyWindowChrome();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Colours the native caption and border so the frame matches the panel colours (Windows 11).</summary>
    private void ApplyWindowChrome()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var palette = ThemePalette.Create(ViewModel.DarkMode);
        static int ToColorRef(System.Windows.Media.Color color) => color.R | (color.G << 8) | (color.B << 16);
        var caption = ToColorRef(((System.Windows.Media.SolidColorBrush)palette["CardBackground"]).Color);
        var border = ToColorRef(((System.Windows.Media.SolidColorBrush)palette["BorderBrush"]).Color);
        var text = ToColorRef(((System.Windows.Media.SolidColorBrush)palette["Ink"]).Color);
        var dark = ViewModel.DarkMode ? 1 : 0;
        var results = new[]
        {
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)),      // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int)),    // DWMWA_CAPTION_COLOR
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int)),     // DWMWA_BORDER_COLOR
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int)),       // DWMWA_TEXT_COLOR
        };
        var failure = results.Any(result => result != 0);
        if (failure || !_chromeReported)
        {
            _chromeReported = true;
            ViewModel.Report("窗口外框配色：" + string.Join(",", results) + $"；标题栏 #{caption:X6} 边框 #{border:X6}" + (failure ? "（系统不支持时为 0）" : ""));
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
        ViewModel.CommitHotkeyEdit();
        _closing = true;
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
}
