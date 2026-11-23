using System.Collections.ObjectModel;
using System.IO;
using AzurAssistant.Contracts;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>任务编排：把一条龙 / 实时辅助 / 流程 / 轮椅方案串成一条队列依次执行。</summary>
public sealed partial class MainViewModel
{
    private ChainStore? _chainStore;
    private (string Path, TaskChain Chain)? _selectedChain;
    private string _chainStatus = "队列文件放在程序目录的 chains 文件夹里；也可以直接导入别人分享的队列 JSON。";
    private bool _chainRunning;

    public ObservableCollection<TaskChain> Chains { get; } = [];
    public ObservableCollection<ChainStepResult> ChainSteps { get; } = [];

    private string ChainsDirectory => Path.Combine(
        Path.GetDirectoryName(SoundsDirectory) ?? AppContext.BaseDirectory, "chains");

    private ChainStore ChainFiles => _chainStore ??= new ChainStore(ChainsDirectory);

    public TaskChain? SelectedChain => _selectedChain?.Chain;
    public bool IsChainRunning => _chainRunning;
    public bool CanRunChain => !_chainRunning;

    public string ChainStatus { get => _chainStatus; private set { _chainStatus = value; Notify(); } }
    public string ChainSummary => SelectedChain is null ? "" : $"{SelectedChain.Name}：{SelectedChain.Summary}";

    public void SelectChain(int index)
    {
        if (index < 0 || index >= Chains.Count) return;
        var name = Chains[index].Name;
        foreach (var entry in ChainFiles.List())
        {
            if (entry.Chain.Name != name) continue;
            _selectedChain = entry;
            break;
        }
        Notify(nameof(SelectedChain));
        Notify(nameof(ChainSummary));
    }

    public void RefreshChains()
    {
        var selectedName = _selectedChain?.Chain.Name;
        Chains.Clear();
        _selectedChain = null;
        foreach (var entry in ChainFiles.List())
        {
            Chains.Add(entry.Chain);
            if (entry.Chain.Name == selectedName) _selectedChain = entry;
        }
        _selectedChain ??= ChainFiles.List().FirstOrDefault();
        Notify(nameof(SelectedChain));
        Notify(nameof(ChainSummary));
        ChainStatus = Chains.Count == 0 ? "还没有任务队列。可以点「导入队列…」。" : $"共 {Chains.Count} 个队列。";
    }

    /// <summary>Runs the selected chain: every item goes through the normal task runner and is awaited.</summary>
    public async Task RunSelectedChainAsync()
    {
        if (_chainRunning) { ChainStatus = "队列正在运行。"; return; }
        if (SelectedChain is not { } chain || automation is null) { ChainStatus = "请先选择一个队列。"; return; }
        ChainSteps.Clear();
        _chainRunning = true;
        Notify(nameof(IsChainRunning));
        Notify(nameof(CanRunChain));
        ChainStatus = $"开始执行队列「{chain.Name}」…";
        try
        {
            var outcome = await ChainRunner.RunAsync(chain, StartChainItemAsync, CancellationToken.None);
            foreach (var step in outcome.Steps) ChainSteps.Add(step);
            ChainStatus = outcome.Message + (outcome.Report.Length > 0 ? "（" + outcome.Report + "）" : "");
            Report("任务队列：" + outcome.Message);
        }
        finally
        {
            _chainRunning = false;
            Notify(nameof(IsChainRunning));
        Notify(nameof(CanRunChain));
        }
    }

    /// <summary>Runs a saved chain by name; used by the scheduler so a queue can be triggered on a timer.</summary>
    public async Task<bool> RunChainByNameAsync(string name)
    {
        if (_chainRunning) { ChainStatus = "队列正在运行。"; return false; }
        if (automation is null) return false;
        var entry = ChainFiles.List().FirstOrDefault(item => item.Chain.Name == name);
        if (string.IsNullOrWhiteSpace(entry.Chain?.Name))
        {
            ChainStatus = $"找不到队列「{name}」。";
            return false;
        }
        _selectedChain = entry;
        Notify(nameof(SelectedChain));
        Notify(nameof(ChainSummary));
        await RunSelectedChainAsync();
        return true;
    }

    /// <summary>Starts one chain item and resolves when that run finishes.</summary>
    private async Task<TaskResult> StartChainItemAsync(ChainItem item, CancellationToken token)
    {
        var started = item.Kind switch
        {
            ChainItem.Daily => automation!.StartDaily(),
            ChainItem.Realtime => automation!.StartRealtime(),
            ChainItem.Flow => StartFlowByName(item.Ref),
            ChainItem.Wheelchair => StartWheelchairByName(item.Ref),
            _ => false,
        };
        if (!started) return new TaskResult(false, "无法启动：" + item.Describe());
        var runner = Runner;
        if (runner is null) return new TaskResult(false, "任务运行器不可用。");
        // Wait until the runner reports the run finished (Successful or not).
        var completion = runner.Completion;
        while (runner.IsRunning)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(200, token).ConfigureAwait(false);
        }
        return await completion.ConfigureAwait(false);
    }

    private bool StartFlowByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var entry = FlowFiles.List().FirstOrDefault(candidate => candidate.Flow.Name == name);
        if (entry is null) return false;
        var actions = SavedActions.GroupBy(action => action.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Recording, StringComparer.Ordinal);
        return automation?.StartFlow(entry.Flow, actions) == true;
    }

    private bool StartWheelchairByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var entry = WheelchairPlans.List().FirstOrDefault(candidate => candidate.Name == name);
        if (entry is null) return false;
        var actions = SavedActions.GroupBy(action => action.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Recording, StringComparer.Ordinal);
        return automation?.StartWheelchair(entry.Plan, actions) == true;
    }

    public void ImportChain(string path)
    {
        try
        {
            var saved = ChainFiles.Import(path);
            ChainStatus = "已导入：" + Path.GetFileName(saved);
            RefreshChains();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            ChainStatus = "导入失败：" + ex.Message;
        }
    }

    public void ExportSelectedChain(string path)
    {
        if (SelectedChain is not { } chain) { ChainStatus = "请先选择一个队列。"; return; }
        try
        {
            ChainFiles.Export(chain, path);
            ChainStatus = "已导出：" + path;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ChainStatus = "导出失败：" + ex.Message;
        }
    }

    public void DeleteSelectedChain()
    {
        if (_selectedChain is not { } entry) { ChainStatus = "请先选择一个队列。"; return; }
        ChainFiles.Delete(entry.Path);
        _selectedChain = null;
        ChainStatus = "已删除：" + entry.Chain.Name;
        RefreshChains();
    }
}
