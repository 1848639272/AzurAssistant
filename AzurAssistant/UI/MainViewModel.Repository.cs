using System.Collections.ObjectModel;
using System.IO;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>在线仓库：从社区索引里一键安装流程 / 任务队列 / 模板包。</summary>
public sealed partial class MainViewModel
{
    private RepositoryService? _repository;
    private string _repositoryUrl = "";
    private string _repositoryStatus = "填一个索引地址后点「刷新」，就能看到别人分享的流程、队列和模板包。";

    private string RepositoryDataRoot => Path.GetDirectoryName(SoundsDirectory) ?? AppContext.BaseDirectory;

    private RepositoryService Repository => _repository ??= new RepositoryService(RepositoryDataRoot);

    public ObservableCollection<RepositoryItem> RepositoryItems { get; } = [];

    public string RepositoryUrl
    {
        get => _repositoryUrl;
        set
        {
            if (value == _repositoryUrl) return;
            _repositoryUrl = value;
            Notify();
            if (SaveSettings(Settings with { RepositoryUrl = value })) { }
        }
    }

    public string RepositoryStatus { get => _repositoryStatus; private set { _repositoryStatus = value; Notify(); } }

    /// <summary>Restores the saved index URL when the assistant starts.</summary>
    public void LoadRepositorySettings()
    {
        _repositoryUrl = Settings.RepositoryUrl;
        Notify(nameof(RepositoryUrl));
    }

    public async Task RefreshRepositoryAsync()
    {
        var url = _repositoryUrl.Trim();
        if (url.Length == 0)
        {
            RepositoryStatus = "请先填写索引地址（一个返回 JSON 的 http/https 链接）。";
            return;
        }
        RepositoryStatus = "正在获取索引…";
        var index = await Repository.FetchIndexAsync(url, CancellationToken.None);
        RepositoryItems.Clear();
        if (index.Error is { Length: > 0 } error)
        {
            RepositoryStatus = error;
            return;
        }
        foreach (var item in index.Items) RepositoryItems.Add(item);
        RepositoryStatus = RepositoryItems.Count == 0
            ? "索引里没有可用的条目。"
            : $"共 {RepositoryItems.Count} 个条目（索引版本 {index.SchemaVersion}{(index.Version.Length > 0 ? " · " + index.Version : "")}）。";
    }

    /// <summary>Loads an index from a local file so the feature works offline too.</summary>
    public void ImportRepositoryIndex(string path)
    {
        try
        {
            var index = RepositoryIndex.Parse(File.ReadAllText(path));
            RepositoryItems.Clear();
            if (index.Error is { Length: > 0 } error) { RepositoryStatus = error; return; }
            foreach (var item in index.Items) RepositoryItems.Add(item);
            RepositoryStatus = $"已从本地导入 {RepositoryItems.Count} 个条目。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RepositoryStatus = "读取索引失败：" + ex.Message;
        }
    }

    public async Task InstallRepositoryItemAsync(RepositoryItem? item)
    {
        if (item is null) { RepositoryStatus = "请先选择一个条目。"; return; }
        RepositoryStatus = $"正在下载「{item.Name}」…";
        var (success, message, path) = await Repository.InstallAsync(item, CancellationToken.None);
        RepositoryStatus = message;
        if (!success || path is null) return;
        Report("在线仓库：" + message);
        if (item.Kind == RepositoryTarget.Flow) RefreshFlows();
        else if (item.Kind == RepositoryTarget.Chain) RefreshChains();
        else RepositoryStatus = message + "（模板包请在「模板热更新」里安装）";
    }
}
