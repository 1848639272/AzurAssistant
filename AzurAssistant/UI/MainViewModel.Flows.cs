using System.Collections.ObjectModel;
using System.IO;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>流程（移动/点击/动作/等待/循环/条件）的列表、导入导出与运行入口。</summary>
public sealed partial class MainViewModel
{
    private FlowStore? _flowStore;
    private SavedFlow? _selectedFlow;
    private string _flowStatus = "流程文件放在程序目录的 flows 文件夹里；也可以直接导入别人分享的流程 JSON。";

    public ObservableCollection<SavedFlow> Flows { get; } = [];

    private string FlowsDirectory => Path.Combine(
        Path.GetDirectoryName(SoundsDirectory) ?? AppContext.BaseDirectory, "flows");

    private FlowStore FlowFiles => _flowStore ??= new FlowStore(FlowsDirectory);

    public SavedFlow? SelectedFlow
    {
        get => _selectedFlow;
        set
        {
            if (ReferenceEquals(_selectedFlow, value)) return;
            _selectedFlow = value;
            Notify();
            Notify(nameof(FlowSummary));
        }
    }

    public string FlowStatus { get => _flowStatus; private set { _flowStatus = value; Notify(); } }

    public string FlowSummary => SelectedFlow is null ? "" : $"{SelectedFlow.Name}：{SelectedFlow.Summary}";

    public void RefreshFlows()
    {
        var selectedPath = SelectedFlow?.Path;
        Flows.Clear();
        foreach (var flow in FlowFiles.List()) Flows.Add(flow);
        _selectedFlow = Flows.FirstOrDefault(flow => flow.Path == selectedPath) ?? Flows.FirstOrDefault();
        Notify(nameof(SelectedFlow));
        Notify(nameof(FlowSummary));
        FlowStatus = Flows.Count == 0
            ? "还没有流程文件。可以点「导入流程…」导入一份 JSON。"
            : $"共 {Flows.Count} 个流程。";
    }

    public void RunSelectedFlow()
    {
        if (SelectedFlow is not { } selected) { FlowStatus = "请先选择一个流程。"; return; }
        if (automation is null) { FlowStatus = "流程功能未装配。"; return; }
        var actions = SavedActions.GroupBy(action => action.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Recording, StringComparer.Ordinal);
        FlowStatus = automation.StartFlow(selected.Flow, actions)
            ? $"开始运行「{selected.Name}」，按停止快捷键可中断。"
            : "无法开始运行，请查看日志。";
    }

    public void ImportFlow(string path)
    {
        try
        {
            var saved = FlowFiles.Import(path);
            FlowStatus = "已导入：" + Path.GetFileName(saved);
            RefreshFlows();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            FlowStatus = "导入失败：" + ex.Message;
        }
    }

    public void ExportSelectedFlow(string path)
    {
        if (SelectedFlow is not { } selected) { FlowStatus = "请先选择一个流程。"; return; }
        try
        {
            FlowFiles.Export(selected.Flow, path);
            FlowStatus = "已导出：" + path;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FlowStatus = "导出失败：" + ex.Message;
        }
    }

    public void DeleteSelectedFlow()
    {
        if (SelectedFlow is not { } selected) { FlowStatus = "请先选择一个流程。"; return; }
        FlowFiles.Delete(selected.Path);
        _selectedFlow = null;
        FlowStatus = "已删除：" + selected.Name;
        RefreshFlows();
    }
    // ---- 录制→流程 / 断点续跑 ----

    private FlowProgressStore? _flowProgressStore;
    private string? _flowActionSource;
    private string _flowResumeText = "";
    private FlowProgress? _flowProgress;

    public ObservableCollection<string> FlowActionSources { get; } = [];
    public string FlowResumeText { get => _flowResumeText; private set { _flowResumeText = value; Notify(); } }

    private string FlowProgressPath => Path.Combine(
        Path.GetDirectoryName(SoundsDirectory) ?? AppContext.BaseDirectory, "flow-progress.json");

    private FlowProgressStore FlowProgressFile => _flowProgressStore ??= new FlowProgressStore(FlowProgressPath);

    public string? SelectedFlowActionSource
    {
        get => _flowActionSource;
        set { if (value == _flowActionSource) return; _flowActionSource = value; Notify(); }
    }

    /// <summary>Fills the source list with the saved action recordings (names shown, unique).</summary>
    public void RefreshFlowActionSources()
    {
        LoadActions();
        FlowActionSources.Clear();
        foreach (var action in SavedActions.Select(item => item.Name).Distinct(StringComparer.Ordinal))
            FlowActionSources.Add(action);
        _flowActionSource = FlowActionSources.FirstOrDefault();
        Notify(nameof(SelectedFlowActionSource));
    }

    /// <summary>Converts the selected recording into a flow file inside the flows folder.</summary>
    public void ConvertActionToFlow()
    {
        if (_flowActionSource is not { } name) { FlowStatus = "请先选择要转换的录制。"; return; }
        LoadActions();
        var action = SavedActions.FirstOrDefault(item => item.Name == name);
        if (action is null) { FlowStatus = $"找不到录制「{name}」。"; return; }
        try
        {
            var conversion = FlowFromRecording.Convert(action.Recording, "录制·" + name);
            var saved = FlowFiles.Save(conversion.Flow);
            RefreshFlows();
            _selectedFlow = Flows.FirstOrDefault(flow => flow.Path == saved) ?? _selectedFlow;
            Notify(nameof(SelectedFlow));
            Notify(nameof(FlowSummary));
            FlowStatus = $"已由录制「{name}」生成流程：{conversion.Clicks} 次点击、{conversion.Keys} 次按键、"
                + $"{conversion.Delays} 段等待" + (conversion.SkippedKeys > 0 ? $"，跳过 {conversion.SkippedKeys} 个不支持的事件" : "")
                + (conversion.Truncated ? "（超过 400 步已截断）" : "") + $"：{Path.GetFileName(saved)}";
            Report("由录制生成流程：" + Path.GetFileName(saved));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            FlowStatus = "生成流程失败：" + ex.Message;
        }
    }

    /// <summary>Loads the stored interruption point and shows it on the page.</summary>
    public void RefreshFlowProgress()
    {
        _flowProgress = FlowProgressFile.Load();
        FlowResumeText = _flowProgress is null ? "" : _flowProgress.Describe;
        Notify(nameof(FlowResumeText));
    }

    public void ClearFlowProgress()
    {
        FlowProgressFile.Clear();
        _flowProgress = null;
        FlowResumeText = "";
        FlowStatus = "已清除断点记录。";
    }

    /// <summary>Runs the selected flow, optionally from the stored interruption point.</summary>
    public bool ResumeSelectedFlow()
    {
        var progress = FlowProgressFile.Load();
        if (progress is null)
        {
            FlowStatus = "没有可继续的断点。";
            return false;
        }
        if (SelectedFlow is not { } selected)
        {
            FlowStatus = "请先选择要继续的流程（上次中断的是「" + progress.FlowName + "」）。";
            return false;
        }
        if (selected.Name != progress.FlowName)
        {
            FlowStatus = $"断点属于流程「{progress.FlowName}」，当前选中的是「{selected.Name}」；请选中对应流程再继续。";
            return false;
        }
        LoadActions();
        var actions = SavedActions.GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Recording, StringComparer.Ordinal);
        var started = automation?.StartFlow(selected.Flow, actions, TrackFlowProgress, progress.NextStep - 1) == true;
        FlowStatus = started
            ? $"从第 {progress.NextStep} 步继续流程「{selected.Name}」。"
            : "流程未能启动（是否有任务在运行？）。";
        return started;
    }

    /// <summary>Records how far a running flow got, so a failure can be resumed.</summary>
    private void TrackFlowProgress(int completed, int total)
    {
        if (total <= 0) return;
        if (completed >= total)
        {
            FlowProgressFile.Clear();
            _flowProgress = null;
            FlowResumeText = "";
            return;
        }
        var progress = new FlowProgress
        {
            FlowName = SelectedFlow?.Name ?? "",
            NextStep = completed + 2,
            TotalSteps = total,
            SavedAt = DateTimeOffset.Now,
        };
        FlowProgressFile.Save(progress);
        _flowProgress = progress;
        FlowResumeText = progress.Describe;
    }
}
