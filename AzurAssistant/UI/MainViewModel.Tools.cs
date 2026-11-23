using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>One saved failure frame (png + json pair) that the debug page can preview.</summary>
public sealed record FailureFrameRow(string ImagePath, string MetaPath, string TaskName, string Reason,
    string WhenText, string SizeText);

/// <summary>工具与诊断页面：语言包、诊断包、失败帧浏览、运行报表。</summary>
public sealed partial class MainViewModel
{
    private readonly RunHistoryStore? _runHistory = runHistoryStore;
    private readonly DiagnosticBundle? _diagnostics = diagnostics;
    private readonly LanguagePackService? _languagePacks = languagePacks;
    private string _languageStatus = "";
    private string _diagnosticsStatus = "";
    private string _debugStatus = "";
    private string _reportStatus = "";
    private LanguagePack? _selectedLanguagePack;
    private bool _languageBusy;

    public ObservableCollection<LanguagePack> LanguagePacks { get; } = [];
    public ObservableCollection<FailureFrameRow> FailureFrames { get; } = [];
    public ObservableCollection<RunRecord> RunHistory { get; } = [];
    public ObservableCollection<TemplateProbe> RegressionProbes { get; } = [];
    private string _regressionSummary = "";
    private string _templateStatus = "";
    private string _profileName = "";
    private string _profileStatus = "";
    private ProfileInfo? _selectedProfile;

    public LanguagePack? SelectedLanguagePack
    {
        get => _selectedLanguagePack;
        set
        {
            if (ReferenceEquals(_selectedLanguagePack, value)) return;
            _selectedLanguagePack = value;
            Notify();
            Notify(nameof(CanApplyLanguagePack));
        }
    }

    public bool CanApplyLanguagePack => !_languageBusy && SelectedLanguagePack is { Ready: true };
    public bool IsLanguageBusy => _languageBusy;
    public string LanguageStatus { get => _languageStatus; private set { _languageStatus = value; Notify(); } }
    public string DiagnosticsStatus { get => _diagnosticsStatus; private set { _diagnosticsStatus = value; Notify(); } }
    public string DebugStatus { get => _debugStatus; private set { _debugStatus = value; Notify(); } }
    public string ReportStatus { get => _reportStatus; private set { _reportStatus = value; Notify(); } }

    public string RunReportSummary
    {
        get
        {
            if (RunHistory.Count == 0) return "还没有运行记录。跑一次任务后这里会出现统计。";
            var success = RunHistory.Count(record => record.Success);
            var average = RunHistory.Average(record => record.DurationSeconds);
            return $"共 {RunHistory.Count} 次：成功 {success}、失败 {RunHistory.Count - success}；平均用时 {average:F0} 秒。";
        }
    }

    // ---- 语言包 ----
    public void RefreshLanguagePacks()
    {
        LanguagePacks.Clear();
        if (_languagePacks is null)
        {
            LanguageStatus = "语言包服务不可用。";
            return;
        }
        foreach (var pack in _languagePacks.List()) LanguagePacks.Add(pack);
        SelectedLanguagePack ??= LanguagePacks.FirstOrDefault(pack => pack.Ready) ?? LanguagePacks.FirstOrDefault();
        LanguageStatus = LanguagePacks.Count == 0
            ? "语言包目录里还没有可用语言包，把导出的语言包放进 语言包\\<代码>\\ 即可。"
            : $"找到 {LanguagePacks.Count} 个语言包，选中后点「应用语言包」。";
    }

    public async Task ApplySelectedLanguagePackAsync()
    {
        if (_languagePacks is null || SelectedLanguagePack is not { Ready: true } pack || _languageBusy) return;
        _languageBusy = true;
        Notify(nameof(CanApplyLanguagePack));
        Notify(nameof(IsLanguageBusy));
        LanguageStatus = $"正在应用 {pack.Label}…";
        try
        {
            var (ok, output) = await _languagePacks.ApplyAsync(pack.Code, CancellationToken.None);
            LanguageStatus = (ok ? "应用完成：" : "应用失败：") + output;
            Report(LanguageStatus);
        }
        finally
        {
            _languageBusy = false;
            Notify(nameof(CanApplyLanguagePack));
            Notify(nameof(IsLanguageBusy));
        }
    }

    public async Task RestoreLanguageAsync()
    {
        if (_languagePacks is null || _languageBusy) return;
        _languageBusy = true;
        Notify(nameof(IsLanguageBusy));
        LanguageStatus = "正在还原备份…";
        try
        {
            var (ok, output) = await _languagePacks.RestoreAsync(CancellationToken.None);
            LanguageStatus = (ok ? "已还原：" : "还原失败：") + output;
            Report(LanguageStatus);
        }
        finally
        {
            _languageBusy = false;
            Notify(nameof(CanApplyLanguagePack));
            Notify(nameof(IsLanguageBusy));
        }
    }

    // ---- 诊断包 ----
    public bool ExportDiagnosticBundle(string zipPath)
    {
        if (_diagnostics is null)
        {
            DiagnosticsStatus = "诊断服务不可用。";
            return false;
        }
        try
        {
            DiagnosticsStatus = "已导出：" + _diagnostics.Create(zipPath);
            Report(DiagnosticsStatus);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticsStatus = "导出失败：" + ex.Message;
            return false;
        }
    }

    // ---- 失败帧浏览 ----
    public void RefreshFailureFrames()
    {
        FailureFrames.Clear();
        var directory = log?.DirectoryPath;
        if (directory is null || !Directory.Exists(directory))
        {
            DebugStatus = "还没有日志目录，运行一次任务后再来看。";
            return;
        }
        var files = new DirectoryInfo(directory).GetFiles("AzurAssistant-failure-*.png")
            .OrderByDescending(file => file.LastWriteTime).Take(60).ToList();
        foreach (var file in files)
        {
            var meta = Path.ChangeExtension(file.FullName, ".json");
            string taskName = "未知任务", reason = "";
            if (File.Exists(meta))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(meta));
                    if (document.RootElement.TryGetProperty("taskName", out var name)) taskName = name.GetString() ?? taskName;
                    if (document.RootElement.TryGetProperty("reason", out var why)) reason = why.GetString() ?? "";
                }
                catch (JsonException) { }
            }
            FailureFrames.Add(new FailureFrameRow(file.FullName, meta, taskName, reason,
                file.LastWriteTime.ToString("MM-dd HH:mm:ss"), $"{file.Length / 1024} KB"));
        }
        DebugStatus = FailureFrames.Count == 0
            ? "没有失败帧。任务失败时会自动保存最后一帧，方便比对模板。"
            : $"共 {FailureFrames.Count} 个失败帧（最近 60 个），双击可打开图片。";
    }

    public void DeleteFailureFrames()
    {
        var removed = 0;
        foreach (var row in FailureFrames.ToList())
        {
            try
            {
                if (File.Exists(row.ImagePath)) { File.Delete(row.ImagePath); removed++; }
                if (File.Exists(row.MetaPath)) File.Delete(row.MetaPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        RefreshFailureFrames();
        DebugStatus = $"已删除 {removed} 个失败帧。";
    }

    /// <summary>Scores every template against a saved failure frame, so a broken template is obvious.</summary>
    public void RunRegressionOnFrame(FailureFrameRow row)
    {
        RegressionProbes.Clear();
        var frame = TemplateRegression.LoadImage(row.ImagePath);
        if (frame is null)
        {
            _regressionSummary = "无法读取这一帧图片：" + row.ImagePath;
            Notify(nameof(RegressionSummary));
            return;
        }
        var report = TemplateRegression.Run(Path.Combine(AppContext.BaseDirectory, "assets"), frame);
        foreach (var probe in report.Probes) RegressionProbes.Add(probe);
        _regressionSummary = $"复测 {Path.GetFileName(row.ImagePath)}：{report.Summary}";
        Notify(nameof(RegressionSummary));
        Report(_regressionSummary);
    }

    public string RegressionSummary => _regressionSummary;

    // ---- 模板热更新 ----
    public string TemplateStatus { get => _templateStatus; private set { _templateStatus = value; Notify(); } }

    /// <summary>Asset packs (folders with recognition.json) that a template can be added to.</summary>
    public IReadOnlyList<string> TemplatePackDirectories
    {
        get
        {
            var root = Path.Combine(AppContext.BaseDirectory, "assets");
            if (!Directory.Exists(root)) return [];
            return Directory.GetDirectories(root)
                .Where(directory => File.Exists(Path.Combine(directory, "recognition.json")))
                .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public string TemplatePackSourceText
    {
        get
        {
            var url = TemplatePackUpdater.SourceUrl();
            return string.IsNullOrWhiteSpace(url) ? "update-source.json 里没有配置 TemplatePackUrl" : url;
        }
    }

    public async Task InstallTemplatePackAsync(string source)
    {
        TemplateStatus = "正在安装模板包…";
        var updater = new TemplatePackUpdater(Path.Combine(AppContext.BaseDirectory, "assets"));
        var result = await updater.InstallAsync(source, CancellationToken.None);
        TemplateStatus = result.Message;
        Report("模板包：" + result.Message);
    }

    // ---- 配置档 ----
    public ObservableCollection<ProfileInfo> Profiles { get; } = [];

    private string DataDirectory => Path.GetDirectoryName(SoundsDirectory) ?? AppContext.BaseDirectory;

    public string ProfileName
    {
        get => _profileName;
        set { _profileName = value ?? ""; Notify(); }
    }

    public ProfileInfo? SelectedProfile
    {
        get => _selectedProfile;
        set { if (!ReferenceEquals(_selectedProfile, value)) { _selectedProfile = value; Notify(); } }
    }

    public string ProfileStatus { get => _profileStatus; private set { _profileStatus = value; Notify(); } }

    public void RefreshProfiles()
    {
        var store = new ProfileStore(DataDirectory);
        Profiles.Clear();
        foreach (var profile in store.List()) Profiles.Add(profile);
        SelectedProfile ??= Profiles.FirstOrDefault();
        ProfileStatus = Profiles.Count == 0
            ? "还没有配置档。填个名字点「保存当前」即可把当前设置/定时任务/动作/轮椅方案存成一份。"
            : $"共 {Profiles.Count} 份配置档。";
    }

    public void SaveCurrentProfile()
    {
        var store = new ProfileStore(DataDirectory);
        if (!store.Save(ProfileName)) { ProfileStatus = store.Error ?? "保存失败。"; return; }
        ProfileStatus = $"已保存配置档「{ProfileName.Trim()}」。";
        RefreshProfiles();
    }

    /// <summary>Applies a profile and reports whether a restart is required (it always is).</summary>
    public bool ApplySelectedProfile(out string message)
    {
        message = "";
        if (SelectedProfile is null) { message = "请先选择一个配置档。"; ProfileStatus = message; return false; }
        var store = new ProfileStore(DataDirectory);
        if (!store.Apply(SelectedProfile.Name, out var backup)) { message = store.Error ?? "应用失败。"; ProfileStatus = message; return false; }
        message = $"已应用配置档「{SelectedProfile.Name}」，原数据备份在 {Path.GetFileName(backup)}。重启助手后生效。";
        ProfileStatus = message;
        return true;
    }

    public void DeleteSelectedProfile()
    {
        if (SelectedProfile is null) { ProfileStatus = "请先选择一个配置档。"; return; }
        var name = SelectedProfile.Name;
        var store = new ProfileStore(DataDirectory);
        if (!store.Delete(name)) { ProfileStatus = store.Error ?? "删除失败。"; return; }
        _selectedProfile = null;
        ProfileStatus = $"已删除配置档「{name}」。";
        RefreshProfiles();
    }

    /// <summary>Restarts the assistant through the same path the settings page uses.</summary>
    public bool TryRestartAssistant() => restartAssistant?.Invoke() == true;

    /// <summary>Average seconds this task took historically, or null when there is no usable history.</summary>
    public double? EstimateTaskSeconds(string taskName)
        => _runHistory is null ? null : TaskDurationEstimator.AverageSeconds(_runHistory.ListAll(), taskName);

    // ---- 运行报表 ----
    public void RefreshRunHistory()
    {
        RunHistory.Clear();
        if (_runHistory is null)
        {
            ReportStatus = "运行记录服务不可用。";
            return;
        }
        foreach (var record in _runHistory.ListAll().Reverse()) RunHistory.Add(record);
        Notify(nameof(RunReportSummary));
        ReportStatus = RunHistory.Count == 0
            ? "还没有运行记录。"
            : $"最近 {Math.Min(RunHistory.Count, 200)} 条记录（新的在前）。";
    }

    public bool ExportRunHistory(string csvPath)
    {
        if (_runHistory is null) return false;
        var ok = _runHistory.Export(csvPath, RunHistory.Reverse().ToList());
        ReportStatus = ok ? "已导出：" + csvPath : _runHistory.Error ?? "导出失败。";
        return ok;
    }

    public void ClearRunHistory()
    {
        if (_runHistory is null) return;
        _runHistory.Clear();
        RefreshRunHistory();
        ReportStatus = "已清空运行记录。";
    }
}
