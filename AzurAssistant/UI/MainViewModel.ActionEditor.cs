using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using AzurAssistant.Contracts;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>
/// One editable action row. Key presses and clicks keep a press/release pair, so editing the start time and the hold
/// duration moves both ends; moves and wheel ticks only move their single event.
/// </summary>
public sealed class ActionEditRow
{
    public string Kind { get; init; } = "";
    public string Detail { get; init; } = "";
    public string TimeText { get; set; } = "0";
    public string DurationText { get; set; } = "0";
    public bool CanEditDuration { get; init; }
    public ActionEvent? Press { get; init; }
    public ActionEvent? Release { get; init; }
    public ActionEvent? Single { get; init; }

    public long TimeMs => long.TryParse(TimeText, out var value) ? Math.Max(0, value) : 0;
    public long DurationMs => CanEditDuration && long.TryParse(DurationText, out var value) ? Math.Max(0, value) : 0;

    public IEnumerable<ActionEvent> Build()
    {
        if (Press is { } press)
        {
            yield return press with { Us = TimeMs * 1000 };
            if (Release is { } release) yield return release with { Us = (TimeMs + DurationMs) * 1000 };
            yield break;
        }
        if (Single is { } single) yield return single with { Us = TimeMs * 1000 };
    }
}

/// <summary>动作编辑：把录像拆成“什么时刻、按了多久”的行，改完原地保存。</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<ActionEditRow> EditableActionEvents { get; } = [];

    public string ActionEditSummary => SelectedAction is null
        ? "尚未选择动作"
        : $"正在编辑「{SelectedAction.Name}」：{EditableActionEvents.Count} 行，时长 {SelectedAction.Recording.DurationText}";

    public bool CanEditAction => SelectedAction is not null && EditableActionEvents.Count > 0;

    /// <summary>Pairs key/button presses with their release so a row shows a real hold duration.</summary>
    public void LoadActionForEdit()
    {
        EditableActionEvents.Clear();
        if (SelectedAction is { } selected)
        {
            var events = selected.Recording.Events;
            var releases = new Dictionary<(ActionEventKind, int, bool), Queue<ActionEvent>>();
            foreach (var item in events.Where(item => item.Kind is ActionEventKind.KeyUp or ActionEventKind.LeftUp or ActionEventKind.RightUp or ActionEventKind.MiddleUp))
            {
                var key = (item.Kind, item.Scan, item.Ext);
                if (!releases.TryGetValue(key, out var queue)) releases[key] = queue = new Queue<ActionEvent>();
                queue.Enqueue(item);
            }
            foreach (var item in events)
            {
                switch (item.Kind)
                {
                    case ActionEventKind.KeyDown:
                    {
                        var queue = releases.TryGetValue((ActionEventKind.KeyUp, item.Scan, item.Ext), out var found) ? found : null;
                        var release = queue is { Count: > 0 } ? queue.Dequeue() : null;
                        EditableActionEvents.Add(new ActionEditRow
                        {
                            Kind = "按键", Detail = KeyLabel(item.Vk), CanEditDuration = true,
                            TimeText = (item.Us / 1000).ToString(CultureInfo.InvariantCulture),
                            DurationText = release is null ? "0" : ((release.Us - item.Us) / 1000).ToString(CultureInfo.InvariantCulture),
                            Press = item, Release = release,
                        });
                        break;
                    }
                    case ActionEventKind.LeftDown or ActionEventKind.RightDown or ActionEventKind.MiddleDown:
                    {
                        var kind = item.Kind switch
                        {
                            ActionEventKind.LeftDown => ActionEventKind.LeftUp,
                            ActionEventKind.RightDown => ActionEventKind.RightUp,
                            _ => ActionEventKind.MiddleUp,
                        };
                        var queue = releases.TryGetValue((kind, 0, false), out var found) ? found : null;
                        var release = queue is { Count: > 0 } ? queue.Dequeue() : null;
                        EditableActionEvents.Add(new ActionEditRow
                        {
                            Kind = item.Kind == ActionEventKind.LeftDown ? "左键" : item.Kind == ActionEventKind.RightDown ? "右键" : "中键",
                            Detail = $"({item.X}, {item.Y})", CanEditDuration = true,
                            TimeText = (item.Us / 1000).ToString(CultureInfo.InvariantCulture),
                            DurationText = release is null ? "0" : ((release.Us - item.Us) / 1000).ToString(CultureInfo.InvariantCulture),
                            Press = item, Release = release,
                        });
                        break;
                    }
                    case ActionEventKind.Move:
                        EditableActionEvents.Add(new ActionEditRow
                        {
                            Kind = "移动", Detail = $"位移 {item.Dx}, {item.Dy}",
                            TimeText = (item.Us / 1000).ToString(CultureInfo.InvariantCulture), Single = item,
                        });
                        break;
                    case ActionEventKind.Wheel:
                        EditableActionEvents.Add(new ActionEditRow
                        {
                            Kind = "滚轮", Detail = $"{(item.Wheel > 0 ? "上" : "下")} {Math.Abs(item.Wheel)}",
                            TimeText = (item.Us / 1000).ToString(CultureInfo.InvariantCulture), Single = item,
                        });
                        break;
                }
            }
        }
        Notify(nameof(ActionEditSummary));
        Notify(nameof(CanEditAction));
        SetActionStatus(SelectedAction is null
            ? "尚未选择动作。"
            : "编辑说明：时刻是相对动作开始的毫秒数；按键/点击的“持续”是按下到松开的毫秒数。改完点“保存修改”。");
    }

    public void RemoveEditedActionRow(ActionEditRow? row)
    {
        if (row is null) return;
        EditableActionEvents.Remove(row);
        Notify(nameof(ActionEditSummary));
        Notify(nameof(CanEditAction));
    }

    public void SaveEditedAction()
    {
        if (SelectedAction is not { } selected || EditableActionEvents.Count == 0) return;
        var events = EditableActionEvents.SelectMany(row => row.Build()).OrderBy(item => item.Us).ToArray();
        if (events.Length == 0) { SetActionStatus("保存失败：动作里没有任何事件。"); return; }
        var duration = events[^1].Us;
        var updated = selected.Recording with { DurationUs = duration, Events = events };
        try
        {
            updated.Validate();
            Actions.Export(updated, selected.Path);
            LoadActions();
            SelectedAction = SavedActions.FirstOrDefault(item => item.Path == selected.Path) ?? SelectedAction;
            SetActionStatus($"已保存「{updated.Name}」：{events.Length} 个事件，时长 {updated.DurationText}。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { SetActionStatus("保存失败：" + ex.Message); }
    }

    private static string KeyLabel(int virtualKey) => virtualKey switch
    {
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x6F),
        0x20 => "空格", 0x0D => "回车", 0x1B => "Esc", 0x09 => "Tab", 0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt",
        0x25 => "左方向键", 0x26 => "上方向键", 0x27 => "右方向键", 0x28 => "下方向键",
        _ => $"VK 0x{virtualKey:X2}",
    };
}
