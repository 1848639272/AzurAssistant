using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>One selectable scheduled action: the daily chain, or a saved route / action / wheelchair plan.</summary>
public sealed record ScheduleChoice(string Key, string Label);

/// <summary>One editable scheduled job row.</summary>
public sealed class ScheduleTaskDraft(string name)
{
    public string Name { get; set; } = name;
    public bool Enabled { get; set; } = true;
    public bool UseInterval { get; set; }
    public string TimeText { get; set; } = "08:00";
    public string IntervalText { get; set; } = "60";
    public string Target { get; set; } = "daily";
}

/// <summary>Read-only row shown on the home page.</summary>
public sealed record ScheduleTaskSummary(string Name, string Detail, string NextText);

/// <summary>One selectable chime preset.</summary>
public sealed record SoundChoice(string Key, string Label);

/// <summary>定时执行：多条独立命名的任务，可分别设定每天时刻或固定间隔，并可选择开始/结束提示音。</summary>
public sealed partial class MainViewModel
{
    private ScheduleStore? _scheduleStore;
    private ScheduleStore Schedules => _scheduleStore ??= new(schedulePath ?? Path.Combine(AppContext.BaseDirectory, "schedule.json"));
    private ScheduleDefinition _schedule = ScheduleDefinition.Empty;
    private DispatcherTimer? _scheduleTimer;
    private bool _scheduleBusy;
    private bool _scheduleLoaded;
    private string _scheduleStatus = "还没有定时任务。点“新增任务”添加一条。";
    private ScheduleTaskDraft? _selectedScheduleDraft;

    public ObservableCollection<ScheduleTaskDraft> ScheduleDrafts { get; } = [];
    public ObservableCollection<ScheduleChoice> ScheduleChoices { get; } = [];
    public ObservableCollection<ScheduleTaskSummary> ScheduleSummaries { get; } = [];
    public ObservableCollection<SoundChoice> SoundChoices { get; } = [];

    public ScheduleTaskDraft? SelectedScheduleDraft
    {
        get => _selectedScheduleDraft;
        set { _selectedScheduleDraft = value; Notify(); Notify(nameof(CanRunSelectedSchedule)); }
    }

    public bool CanRunSelectedSchedule => SelectedScheduleDraft is not null && !_scheduleBusy;
    public string ScheduleStatus { get => _scheduleStatus; private set { _scheduleStatus = value; Notify(); } }

    public string ScheduleSummary => _schedule.Tasks.Count == 0
        ? "未配置定时任务"
        : $"{_schedule.Tasks.Count(task => task.Enabled)}/{_schedule.Tasks.Count} 条启用中";

    // ---- 声音设置 ----
    public bool ScheduleSound
    {
        get => Settings.ScheduleSound;
        set { if (SaveSettings(Settings with { ScheduleSound = value })) { Notify(); Notify(nameof(SoundSummary)); } }
    }

    public SoundChoice? ScheduleStartSoundChoice
    {
        get => SoundChoices.FirstOrDefault(choice => choice.Key == ScheduleClock.NormalizeSound(Settings.ScheduleStartSound));
        set { if (value is not null && SaveSettings(Settings with { ScheduleStartSound = value.Key })) { Notify(); Notify(nameof(SoundSummary)); } }
    }

    public SoundChoice? ScheduleEndSoundChoice
    {
        get => SoundChoices.FirstOrDefault(choice => choice.Key == ScheduleClock.NormalizeSound(Settings.ScheduleEndSound));
        set { if (value is not null && SaveSettings(Settings with { ScheduleEndSound = value.Key })) { Notify(); Notify(nameof(SoundSummary)); } }
    }

    public string SoundSummary => Settings.ScheduleSound
        ? $"开始：{ScheduleClock.SoundLabel(Settings.ScheduleStartSound)} · 结束：{ScheduleClock.SoundLabel(Settings.ScheduleEndSound)}"
        : "提示音已关闭";

    public void PreviewStartSound() => TaskSound.PlayStart(Settings.ScheduleStartSound);
    public void PreviewEndSound() => TaskSound.PlayFinish(Settings.ScheduleEndSound);

    public void RefreshSchedulePage()
    {
        BuildScheduleChoices();
        EnsureSoundChoices();
        LoadSchedule();
        Notify(nameof(ScheduleSound));
        Notify(nameof(ScheduleStartSoundChoice));
        Notify(nameof(ScheduleEndSoundChoice));
        Notify(nameof(SoundSummary));
        EnsureScheduleTimer();
        RefreshScheduleNextTimes();
        if (Schedules.Error is { } error) SetScheduleStatus(error);
    }

    // ---- 任务编辑 ----
    public void AddScheduleTask()
    {
        ScheduleDrafts.Add(new ScheduleTaskDraft($"任务 {ScheduleDrafts.Count + 1}"));
        SelectedScheduleDraft ??= ScheduleDrafts[^1];
        SetScheduleStatus("已新增一条任务：填好名称与内容后点“保存任务”。");
    }

    public void RemoveScheduleTask(ScheduleTaskDraft? draft)
    {
        if (draft is null) return;
        ScheduleDrafts.Remove(draft);
        if (ReferenceEquals(SelectedScheduleDraft, draft)) SelectedScheduleDraft = ScheduleDrafts.FirstOrDefault();
        SetScheduleStatus("已移除一条任务，记得点“保存任务”。");
    }

    public void SaveScheduleTasks()
    {
        var tasks = new List<ScheduleTask>();
        foreach (var draft in ScheduleDrafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Length > 40) { SetScheduleStatus("任务名称需要 1–40 字。"); return; }
            if (!ScheduleClock.TryParseTime(draft.TimeText, out var time)) { SetScheduleStatus($"任务“{draft.Name}”的时刻格式无效（HH:mm）。"); return; }
            if (!int.TryParse(draft.IntervalText, out var minutes) || minutes is < 1 or > 1440)
            { SetScheduleStatus($"任务“{draft.Name}”的间隔应为 1–1440 分钟。"); return; }
            var previous = _schedule.Tasks.FirstOrDefault(task => task.Name == draft.Name.Trim());
            tasks.Add(new ScheduleTask
            {
                Name = draft.Name.Trim(),
                Enabled = draft.Enabled,
                Mode = draft.UseInterval ? "interval" : "daily",
                Time = $"{time.Hours:00}:{time.Minutes:00}",
                IntervalMinutes = minutes,
                Target = draft.Target,
                LastRunUnixMs = previous?.LastRunUnixMs ?? 0,
            });
        }
        var definition = new ScheduleDefinition(ScheduleDefinition.CurrentSchema, tasks);
        if (!Schedules.Save(definition)) { SetScheduleStatus(Schedules.Error ?? "保存失败。"); return; }
        _schedule = definition;
        EnsureScheduleTimer();
        RefreshScheduleNextTimes();
        SetScheduleStatus($"已保存 {tasks.Count} 条定时任务。");
    }

    public void RunSelectedScheduleNow()
    {
        if (SelectedScheduleDraft is null) { SetScheduleStatus("请先选中一条任务。"); return; }
        if (_scheduleBusy) { SetScheduleStatus("上一次定时任务还在运行。"); return; }
        var task = _schedule.Tasks.FirstOrDefault(item => item.Name == SelectedScheduleDraft.Name.Trim());
        if (task is null) { SetScheduleStatus("这条任务还没保存，请先点“保存任务”。"); return; }
        _ = RunScheduledAsync(task, manual: true);
    }

    /// <summary>Home page button: runs the first enabled saved job right away.</summary>
    public void RunFirstScheduleNow()
    {
        if (_scheduleBusy) { SetScheduleStatus("上一次定时任务还在运行。"); return; }
        var task = _schedule.Tasks.FirstOrDefault(item => item.Enabled);
        if (task is null) { SetScheduleStatus("还没有启用的定时任务。"); return; }
        _ = RunScheduledAsync(task, manual: true);
    }

    // ---- 调度 ----
    private void LoadSchedule()
    {
        _schedule = Schedules.Load();
        if (_schedule.Tasks.Count == 0 && ScheduleDrafts.Count == 0 && SeedFromLegacySettings())
            Schedules.Save(_schedule);
        ScheduleDrafts.Clear();
        foreach (var task in _schedule.Tasks)
        {
            ScheduleDrafts.Add(new ScheduleTaskDraft(task.Name)
            {
                Enabled = task.Enabled,
                UseInterval = task.Mode == "interval",
                TimeText = task.Time,
                IntervalText = task.IntervalMinutes.ToString(),
                Target = task.Target,
            });
        }
        SelectedScheduleDraft = ScheduleDrafts.FirstOrDefault();
        Notify(nameof(ScheduleDrafts));
        Notify(nameof(ScheduleSummary));
    }

    /// <summary>Carries the earlier single-job configuration over the first time the job list is opened.</summary>
    private bool SeedFromLegacySettings()
    {
        if (!Settings.ScheduleEnabled) return false;
        _schedule = new ScheduleDefinition(ScheduleDefinition.CurrentSchema, [new ScheduleTask
        {
            Name = "默认任务",
            Mode = Settings.ScheduleMode,
            Time = Settings.ScheduleTime,
            IntervalMinutes = Settings.ScheduleIntervalMinutes,
            Target = Settings.ScheduleTarget,
        }]);
        return true;
    }

    private void BuildScheduleChoices()
    {
        ScheduleChoices.Clear();
        ScheduleChoices.Add(new("daily", "一条龙（按一条龙页面的配置）"));
        try { LoadRoutes(); foreach (var route in SavedRoutes) ScheduleChoices.Add(new("route:" + route.Name, "路线：" + route.Name)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        LoadActions();
        foreach (var action in SavedActions) ScheduleChoices.Add(new("action:" + action.Name, "动作：" + action.Name));
        LoadWheelchairPlans();
        foreach (var plan in SavedWheelchairPlans) ScheduleChoices.Add(new("wheelchair:" + plan.Name, "轮椅：" + plan.Name));
        Notify(nameof(ScheduleChoices));
    }

    private void EnsureSoundChoices()
    {
        if (SoundChoices.Count == ScheduleClock.SoundPresets.Count) return;
        SoundChoices.Clear();
        foreach (var preset in ScheduleClock.SoundPresets) SoundChoices.Add(new(preset, ScheduleClock.SoundLabel(preset)));
    }

    private void EnsureScheduleTimer()
    {
        if (!_scheduleLoaded)
        {
            // The home page shows the job list, so it must be loaded before the schedule page is ever opened.
            _scheduleLoaded = true;
            EnsureSoundChoices();
            BuildScheduleChoices();
            LoadSchedule();
            RefreshScheduleNextTimes();
        }
        if (_scheduleTimer is null)
        {
            _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _scheduleTimer.Tick += OnScheduleTick;
        }
        if (_schedule.Tasks.Any(task => task.Enabled))
        {
            if (!_scheduleTimer.IsEnabled) _scheduleTimer.Start();
        }
        else if (_scheduleTimer.IsEnabled) _scheduleTimer.Stop();
    }

    private void OnScheduleTick(object? sender, EventArgs e)
    {
        if (_scheduleBusy || automation is null) return;
        var now = DateTimeOffset.Now;
        foreach (var task in _schedule.Tasks.Where(task => task.Enabled))
        {
            var next = ScheduleClock.Next(task, now, task.LastRun);
            if (next is null || now < next) continue;
            _ = RunScheduledAsync(task, manual: false);
            return; // one job at a time; the next tick picks up whatever else is due
        }
    }

    private async Task RunScheduledAsync(ScheduleTask task, bool manual)
    {
        if (_scheduleBusy) return;
        _scheduleBusy = true;
        try
        {
            MarkRan(task.Name);
            var started = TryStartScheduled(task.Target, out var message);
            SetScheduleStatus($"「{task.Name}」{(manual ? "手动触发：" : "定时触发：")}{message}");
            if (!started) return;
            if (Settings.ScheduleSound) TaskSound.PlayStart(Settings.ScheduleStartSound);
            var completion = automation!.Runner.Completion;
            await completion;
            if (Settings.ScheduleSound) TaskSound.PlayFinish(Settings.ScheduleEndSound);
            var result = automation.Runner.LastResult;
            SetScheduleStatus($"「{task.Name}」{(result?.Success == true ? "已完成" : "已结束")}：{result?.Message ?? "无结果"}");
        }
        catch (Exception ex) { SetScheduleStatus($"「{task.Name}」执行异常：{ex.Message}"); }
        finally
        {
            _scheduleBusy = false;
            RefreshScheduleNextTimes();
        }
    }

    /// <summary>Records the fire time so interval jobs count from it and daily jobs wait for tomorrow.</summary>
    private void MarkRan(string name)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var tasks = _schedule.Tasks.Select(task => task.Name == name ? task with { LastRunUnixMs = now } : task).ToArray();
        _schedule = new ScheduleDefinition(ScheduleDefinition.CurrentSchema, tasks);
        Schedules.Save(_schedule);
    }

    private void RefreshScheduleNextTimes()
    {
        var now = DateTimeOffset.Now;
        ScheduleSummaries.Clear();
        foreach (var task in _schedule.Tasks)
        {
            var next = ScheduleClock.Next(task, now, task.LastRun);
            var target = ScheduleChoices.FirstOrDefault(choice => choice.Key == task.Target)?.Label ?? task.Target;
            ScheduleSummaries.Add(new ScheduleTaskSummary(task.Name, $"{ScheduleClock.Describe(task)} · {target}",
                next is { } time ? $"下次 {time:MM-dd HH:mm}" : "已停用"));
        }
        Notify(nameof(ScheduleSummary));
        Notify(nameof(ScheduleSummaries));
    }

    private bool TryStartScheduled(string? rawTarget, out string message)
    {
        message = "";
        if (automation is null) { message = "自动化未装配。"; return false; }
        if (automation.IsBusy) { message = "已有任务在运行，本次跳过。"; return false; }
        var target = rawTarget ?? "daily";
        if (target == "daily")
        {
            if (!automation.StartDaily()) { message = "一条龙未启动，请先检查一条龙页面是否已选功能。"; return false; }
            message = "已开始一条龙。";
            return true;
        }
        var separator = target.IndexOf(':');
        if (separator <= 0) { message = "定时任务的内容无效。"; return false; }
        var kind = target[..separator];
        var name = target[(separator + 1)..];
        switch (kind)
        {
            case "route":
                LoadRoutes();
                var route = SavedRoutes.FirstOrDefault(item => item.Name == name);
                if (route is null) { message = $"找不到路线“{name}”。"; return false; }
                if (!automation.StartRoute(route.Route)) { message = $"路线“{name}”未能启动。"; return false; }
                message = $"已开始路线“{name}”。";
                return true;
            case "action":
                LoadActions();
                var action = SavedActions.FirstOrDefault(item => item.Name == name);
                if (action is null) { message = $"找不到动作“{name}”。"; return false; }
                if (!automation.StartActionPlayback(action.Recording)) { message = $"动作“{name}”未能回放。"; return false; }
                message = $"已开始回放动作“{name}”。";
                return true;
            case "wheelchair":
                LoadActions();
                LoadWheelchairPlans();
                var plan = SavedWheelchairPlans.FirstOrDefault(item => item.Name == name);
                if (plan is null) { message = $"找不到轮椅方案“{name}”。"; return false; }
                var actions = SavedActions.GroupBy(item => item.Name, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First().Recording, StringComparer.Ordinal);
                if (!automation.StartWheelchair(plan.Plan, actions)) { message = $"轮椅方案“{name}”未能启动。"; return false; }
                message = $"已开始轮椅方案“{name}”。";
                return true;
            default:
                message = "定时任务的内容无效。";
                return false;
        }
    }

    private void SetScheduleStatus(string value)
    {
        ScheduleStatus = value;
        Report(value);
    }
}
