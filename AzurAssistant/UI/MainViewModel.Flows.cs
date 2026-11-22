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
}
