using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>One selectable scheduled action: the daily chain, or a saved route / action / wheelchair plan.</summary>
public sealed record ScheduleChoice(string Key, string Label);

/// <summary>定时执行：按每天固定时刻或固定间隔，自动启动一条龙、路线、动作或轮椅方案。</summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? _scheduleTimer;
    private DateTimeOffset? _nextScheduleRun;
    private DateTimeOffset? _lastScheduleRun;
    private bool _scheduleBusy;
    private string _scheduleStatus = "未启用。勾选“启用定时执行”后生效。";
    private ScheduleChoice? _scheduleChoice;
    private string _scheduleTimeText = "";
    private string _scheduleIntervalText = "";

    public ObservableCollection<ScheduleChoice> ScheduleChoices { get; } = [];

    public bool ScheduleEnabled
    {
        get => Settings.ScheduleEnabled;
        set
        {
            if (!SaveSettings(Settings with { ScheduleEnabled = value })) return;
            Notify();
            Notify(nameof(ScheduleSummary));
            ResetScheduleClock();
        }
    }

    public bool ScheduleSound
    {
        get => Settings.ScheduleSound;
        set { if (SaveSettings(Settings with { ScheduleSound = value })) Notify(); }
    }

    public bool ScheduleUseInterval
    {
        get => Settings.ScheduleMode == "interval";
        set
        {
            if (!SaveSettings(Settings with { ScheduleMode = value ? "interval" : "daily" })) return;
            Notify();
            Notify(nameof(ScheduleUseDaily));
            Notify(nameof(ScheduleSummary));
            ResetScheduleClock();
        }
    }

    // RadioButton.IsChecked binds two-way, so the derived flag needs a setter that flips the real setting.
    public bool ScheduleUseDaily
    {
        get => !ScheduleUseInterval;
        set { if (value) ScheduleUseInterval = false; }
    }

    public string ScheduleTimeText { get => _scheduleTimeText; set { _scheduleTimeText = value; Notify(); } }
    public string ScheduleIntervalText { get => _scheduleIntervalText; set { _scheduleIntervalText = value; Notify(); } }

    public ScheduleChoice? ScheduleChoice
    {
        get => _scheduleChoice;
        set
        {
            _scheduleChoice = value;
            if (value is not null && !SaveSettings(Settings with { ScheduleTarget = value.Key })) return;
            Notify();
            Notify(nameof(ScheduleSummary));
        }
    }

    public string ScheduleStatus { get => _scheduleStatus; private set { _scheduleStatus = value; Notify(); } }
    public string ScheduleNextText => Settings.ScheduleEnabled
        ? _nextScheduleRun is { } next ? $"下次执行：{next:MM-dd HH:mm:ss}" : "下次执行：等待计算"
        : "未启用";

    public string ScheduleSummary => ScheduleEnabled
        ? $"{ScheduleClock.Describe(Settings)} · {ScheduleChoice?.Label ?? "一条龙"}"
        : "未启用";

    public void RefreshSchedulePage()
    {
        BuildScheduleChoices();
        _scheduleTimeText = Settings.ScheduleTime;
        _scheduleIntervalText = Settings.ScheduleIntervalMinutes.ToString();
        Notify(nameof(ScheduleTimeText));
        Notify(nameof(ScheduleIntervalText));
        Notify(nameof(ScheduleUseInterval));
        Notify(nameof(ScheduleUseDaily));
        Notify(nameof(ScheduleEnabled));
        Notify(nameof(ScheduleSound));
        Notify(nameof(ScheduleSummary));
        ResetScheduleClock();
    }

    /// <summary>Validates and stores the text fields; invalid input keeps the previous saved configuration.</summary>
    public void ApplyScheduleTiming()
    {
        if (!ScheduleClock.TryParseTime(ScheduleTimeText, out var time))
        { SetScheduleStatus("时刻格式无效，请按 08:30 这样填写。"); return; }
        if (!int.TryParse(ScheduleIntervalText, out var minutes) || minutes is < 1 or > 1440)
        { SetScheduleStatus("间隔无效，请填 1–1440 分钟。"); return; }
        if (!SaveSettings(Settings with { ScheduleTime = $"{time.Hours:00}:{time.Minutes:00}", ScheduleIntervalMinutes = minutes }))
        { SetScheduleStatus("保存失败，请查看日志与设置提示。"); return; }
        ScheduleTimeText = Settings.ScheduleTime;
        ScheduleIntervalText = Settings.ScheduleIntervalMinutes.ToString();
        SetScheduleStatus("已保存定时设置。");
        Notify(nameof(ScheduleSummary));
        ResetScheduleClock();
    }

    public void RunScheduleNow()
    {
        if (_scheduleBusy) { SetScheduleStatus("上一次定时任务还在运行。"); return; }
        _ = RunScheduledAsync(manual: true);
    }

    private void BuildScheduleChoices()
    {
        var current = Settings.ScheduleTarget;
        ScheduleChoices.Clear();
        ScheduleChoices.Add(new("daily", "一条龙（按一条龙页面的配置）"));
        try
        {
            LoadRoutes();
            foreach (var route in SavedRoutes) ScheduleChoices.Add(new("route:" + route.Name, "路线：" + route.Name));
        }
        catch (Exception ex) when (ex is InvalidDataException or System.IO.IOException) { }
        LoadActions();
        foreach (var action in SavedActions) ScheduleChoices.Add(new("action:" + action.Name, "动作：" + action.Name));
        LoadWheelchairPlans();
        foreach (var plan in SavedWheelchairPlans) ScheduleChoices.Add(new("wheelchair:" + plan.Name, "轮椅：" + plan.Name));
        _scheduleChoice = ScheduleChoices.FirstOrDefault(choice => choice.Key == current) ?? ScheduleChoices[0];
        Notify(nameof(ScheduleChoice));
    }

    private void EnsureScheduleTimer()
    {
        if (_scheduleTimer is null)
        {
            _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _scheduleTimer.Tick += OnScheduleTick;
        }
        if (Settings.ScheduleEnabled)
        {
            _nextScheduleRun ??= ScheduleClock.Next(Settings, DateTimeOffset.Now, _lastScheduleRun);
            if (!_scheduleTimer.IsEnabled) _scheduleTimer.Start();
        }
        else if (_scheduleTimer.IsEnabled)
        {
            _scheduleTimer.Stop();
            _nextScheduleRun = null;
        }
    }

    private void ResetScheduleClock()
    {
        _nextScheduleRun = ScheduleClock.Next(Settings, DateTimeOffset.Now, _lastScheduleRun);
        Notify(nameof(ScheduleNextText));
        EnsureScheduleTimer();
    }

    private void OnScheduleTick(object? sender, EventArgs e)
    {
        if (!Settings.ScheduleEnabled || _scheduleBusy || automation is null) return;
        var now = DateTimeOffset.Now;
        if (_nextScheduleRun is null) { _nextScheduleRun = ScheduleClock.Next(Settings, now, _lastScheduleRun); Notify(nameof(ScheduleNextText)); return; }
        if (now < _nextScheduleRun) return;
        _ = RunScheduledAsync(manual: false);
    }

    private async Task RunScheduledAsync(bool manual)
    {
        if (_scheduleBusy) return;
        _scheduleBusy = true;
        try
        {
            var started = TryStartScheduled(out var message);
            SetScheduleStatus((manual ? "手动触发：" : "定时触发：") + message);
            if (!started) return;
            if (Settings.ScheduleSound) TaskSound.PlayStart();
            var completion = automation!.Runner.Completion;
            await completion;
            _lastScheduleRun = DateTimeOffset.Now;
            if (Settings.ScheduleSound) TaskSound.PlayFinish();
            var result = automation.Runner.LastResult;
            SetScheduleStatus($"上次执行 {_lastScheduleRun:MM-dd HH:mm}：{(result?.Success == true ? "成功" : "失败")} · {result?.Message ?? "无结果"}");
        }
        catch (Exception ex) { SetScheduleStatus("定时执行异常：" + ex.Message); }
        finally
        {
            _scheduleBusy = false;
            _nextScheduleRun = ScheduleClock.Next(Settings, DateTimeOffset.Now, _lastScheduleRun);
            Notify(nameof(ScheduleNextText));
        }
    }

    private bool TryStartScheduled(out string message)
    {
        message = "";
        if (automation is null) { message = "自动化未装配。"; return false; }
        if (automation.IsBusy) { message = "已有任务在运行，本次跳过。"; return false; }
        var target = Settings.ScheduleTarget ?? "daily";
        if (target == "daily")
        {
            if (!automation.StartDaily()) { message = "一条龙未启动，请先检查一条龙页面是否已选功能。"; return false; }
            message = "已开始一条龙。";
            return true;
        }
        var separator = target.IndexOf(':');
        if (separator <= 0) { message = "定时执行的内容无效。"; return false; }
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
                message = "定时执行的内容无效。";
                return false;
        }
    }

    private void SetScheduleStatus(string value)
    {
        ScheduleStatus = value;
        Report(value);
    }
}
