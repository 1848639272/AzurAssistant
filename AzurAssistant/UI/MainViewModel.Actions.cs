using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using AzurAssistant.Contracts;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>动作录制页面：被动录制玩家的真实键鼠，按原时序回放。</summary>
public sealed partial class MainViewModel
{
    private ActionRecordingStore? _actionStore;
    private ActionRecordingStore Actions => _actionStore ??= new(actionsDirectory ?? Path.Combine(AppContext.BaseDirectory, "actions"));
    private DispatcherTimer? _actionTimer;
    private string _actionName = "";
    private string _actionStatus = "点“开始录制”后切到游戏正常操作；切回助手窗口约 1 秒后自动结束并保存。";
    private string _actionRecordingText = "";
    private SavedActionRecording? _selectedAction;
    private bool _isActionRecording;

    public ObservableCollection<SavedActionRecording> SavedActions { get; } = [];

    public bool IsActionRecording
    {
        get => _isActionRecording;
        private set
        {
            if (_isActionRecording == value) return;
            _isActionRecording = value;
            Notify();
            Notify(nameof(CanBeginActionRecording));
            Notify(nameof(CanStopActionRecording));
            Notify(nameof(CanPlayAction));
        }
    }

    public string ActionStatus { get => _actionStatus; private set { _actionStatus = value; Notify(); } }
    public string ActionRecordingText { get => _actionRecordingText; private set { _actionRecordingText = value; Notify(); } }

    public string ActionName
    {
        get => _actionName;
        set { _actionName = value; Notify(); Notify(nameof(CanBeginActionRecording)); }
    }

    public SavedActionRecording? SelectedAction
    {
        get => _selectedAction;
        set { _selectedAction = value; Notify(); Notify(nameof(SelectedActionSummary)); Notify(nameof(CanPlayAction)); LoadActionForEdit(); }
    }

    public string SelectedActionSummary => SelectedAction is { } selected
        ? $"{selected.Summary} · {selected.CreatedText} · {selected.FileSizeText}"
        : "尚未选择动作文件";

    public bool CanBeginActionRecording => CanConfigure && !IsActionRecording;
    public bool CanStopActionRecording => IsActionRecording;
    public bool CanPlayAction => CanConfigure && !IsActionRecording && SelectedAction is not null;

    public void LoadActions()
    {
        if (IsActionRecording) return;
        var selected = SelectedAction?.Path;
        SavedActions.Clear();
        try { foreach (var item in Actions.List()) SavedActions.Add(item); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetActionStatus("无法读取动作目录：" + ex.Message); RefreshActionChoices(); return; }
        SelectedAction = SavedActions.FirstOrDefault(item => item.Path == selected) ?? SavedActions.FirstOrDefault();
        RefreshActionChoices();
    }

    /// <summary>Copies a shared action file into the local action directory.</summary>
    public void ImportActionRecording(string path)
    {
        try
        {
            var stored = Actions.Import(path);
            LoadActions();
            SelectedAction = SavedActions.FirstOrDefault(item => item.Path == stored) ?? SelectedAction;
            SetActionStatus($"已导入动作「{SelectedAction?.Name}」。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        { SetActionStatus("导入失败：" + ex.Message); }
    }

    /// <summary>Writes a shareable copy of the selected action.</summary>
    public void ExportSelectedAction(string path)
    {
        if (SelectedAction is not { } selected) return;
        try
        {
            Actions.Export(selected.Recording, path);
            SetActionStatus($"已导出到 {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { SetActionStatus("导出失败：" + ex.Message); }
    }

    public void BeginActionRecording()
    {
        if (!CanBeginActionRecording) return;
        if (automation is null) { SetActionStatus("动作录制未装配自动化控制器。"); return; }
        var name = string.IsNullOrWhiteSpace(ActionName) ? $"动作 {DateTime.Now:MMdd-HHmm}" : ActionName.Trim();
        if (!automation.StartActionRecording(name, out var completion) || completion is null)
        { SetActionStatus("未能开始录制：请确认游戏已打开且只保留一个游戏窗口，详见日志。"); return; }
        IsActionRecording = true;
        ActionRecordingText = "录制中：0.0 秒 · 0 个事件";
        SetActionStatus("录制中：切到游戏正常操作；切回助手窗口约 1 秒后自动结束并保存。");
        StartActionTimer();
        _ = WatchActionRecordingAsync(completion);
    }

    public async Task StopActionRecordingAsync()
    {
        if (!IsActionRecording || automation is null) return;
        SetActionStatus("正在结束录制…");
        // The watcher owns saving so a manual stop and an automatic stop cannot both write the same recording.
        await automation.StopActionRecordingAsync();
    }

    private async Task WatchActionRecordingAsync(Task<ActionRecording> completion)
    {
        ActionRecording? recording = null;
        try { recording = await completion; }
        catch (Exception ex) { SetActionStatus($"录制失败：{ex.Message}"); }
        StopActionTimer();
        IsActionRecording = false;
        if (recording is null) return;
        try
        {
            var path = Actions.Save(recording);
            LoadActions();
            SelectedAction = SavedActions.FirstOrDefault(item => item.Path == path) ?? SelectedAction;
            SetActionStatus($"已保存「{recording.Name}」：{recording.Summary}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { SetActionStatus($"保存失败：{ex.Message}"); }
    }

    public void PlaySelectedAction()
    {
        if (!CanPlayAction || automation is null || SelectedAction is null) return;
        if (automation.StartActionPlayback(SelectedAction.Recording)) SetActionStatus($"开始回放「{SelectedAction.Name}」，按 F10 停止。");
        else SetActionStatus("无法开始回放，请查看日志。");
    }

    public void DeleteSelectedAction()
    {
        if (SelectedAction is not { } selected) return;
        try
        {
            Actions.Delete(selected.Path);
            SetActionStatus($"已删除「{selected.Name}」。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetActionStatus("删除失败：" + ex.Message); }
        LoadActions();
    }

    public void RenameSelectedAction()
    {
        if (SelectedAction is not { } selected) return;
        if (string.IsNullOrWhiteSpace(ActionName)) { SetActionStatus("请先在上方“动作名称”里填写新名称，再点重命名。"); return; }
        try
        {
            var path = Actions.Rename(selected.Path, ActionName.Trim());
            LoadActions();
            SelectedAction = SavedActions.FirstOrDefault(item => item.Path == path) ?? SelectedAction;
            SetActionStatus($"已重命名为「{ActionName.Trim()}」。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { SetActionStatus("重命名失败：" + ex.Message); }
    }

    private void StartActionTimer()
    {
        _actionTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _actionTimer.Tick -= OnActionTimerTick;
        _actionTimer.Tick += OnActionTimerTick;
        _actionTimer.Start();
    }

    private void StopActionTimer() => _actionTimer?.Stop();

    private void OnActionTimerTick(object? sender, EventArgs e)
    {
        if (automation is null || !IsActionRecording) return;
        ActionRecordingText = $"录制中：{automation.ActionRecordingElapsed.TotalSeconds:0.0} 秒 · {automation.ActionRecordingEvents} 个事件";
    }

    private void SetActionStatus(string value) => ActionStatus = value;
}
