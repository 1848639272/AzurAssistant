using System.Collections.ObjectModel;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>One editable cover box row on the extensions page.</summary>
public sealed record CoverBoxRow(int Index, AzurAssistant.Runtime.CoverBox Box)
{
    public string Label => $"遮挡 {Index}";
    public int X => Box.X;
    public int Y => Box.Y;
    public int Width => Box.Width;
    public int Height => Box.Height;
    public string Describe => Box.Describe;
}

/// <summary>扩展功能：以后各种特殊功能放这里。目前包含 UID 遮挡。</summary>
public sealed partial class MainViewModel
{
    private string _coverUidStatus = "未启用。启用后会跟随游戏窗口，把 UID 区域盖住；没有开游戏时可以勾选「在本窗口预览」先摆好位置。";

    public bool CoverUidEnabled
    {
        get => Settings.CoverUidEnabled;
        set
        {
            if (value == Settings.CoverUidEnabled) return;
            if (SaveSettings(Settings with { CoverUidEnabled = value })) { Notify(); NotifyCoverUid(); }
        }
    }

    public bool CoverUidPreviewOnSelf
    {
        get => Settings.CoverUidPreviewOnSelf;
        set
        {
            if (value == Settings.CoverUidPreviewOnSelf) return;
            if (SaveSettings(Settings with { CoverUidPreviewOnSelf = value })) { Notify(); NotifyCoverUid(); }
        }
    }

    public int CoverUidX
    {
        get => Settings.CoverUidX;
        set => SetCoverValue(value, 0, 8192, Settings.CoverUidX, updated => Settings with { CoverUidX = updated });
    }

    public int CoverUidY
    {
        get => Settings.CoverUidY;
        set => SetCoverValue(value, 0, 8192, Settings.CoverUidY, updated => Settings with { CoverUidY = updated });
    }

    public int CoverUidWidth
    {
        get => Settings.CoverUidWidth;
        set => SetCoverValue(value, 1, 4096, Settings.CoverUidWidth, updated => Settings with { CoverUidWidth = updated });
    }

    public int CoverUidHeight
    {
        get => Settings.CoverUidHeight;
        set => SetCoverValue(value, 1, 4096, Settings.CoverUidHeight, updated => Settings with { CoverUidHeight = updated });
    }

    /// <summary>Flips the cover on/off (used by the Ctrl+Alt+U hotkey).</summary>
    public void ToggleCoverUid() => CoverUidEnabled = !CoverUidEnabled;

    public string CoverUidStatus { get => _coverUidStatus; private set { _coverUidStatus = value; Notify(); } }

    /// <summary>Snapshot used by the overlay controller.</summary>
    public IReadOnlyList<CoverBox> CoverUidBoxList => CoverBoxes.Resolve(Settings.CoverUidBoxes,
        Settings.CoverUidX, Settings.CoverUidY, Settings.CoverUidWidth, Settings.CoverUidHeight);

    public UidCoverController.CoverRequest CoverUidRequest =>
        new(Settings.CoverUidEnabled, Settings.CoverUidPreviewOnSelf, CoverUidBoxList,
            Settings.CoverUidStyle, Settings.CoverUidColor);

    public string CoverUidStyle
    {
        get => CoverStyles.Normalize(Settings.CoverUidStyle);
        set
        {
            var normalized = CoverStyles.Normalize(value);
            if (normalized == Settings.CoverUidStyle) return;
            if (SaveSettings(Settings with { CoverUidStyle = normalized })) { Notify(); NotifyCoverUid(); }
        }
    }

    public string CoverUidColor
    {
        get => Settings.CoverUidColor;
        set
        {
            if (!PixelColor.TryParse(value, out _, out _, out _)) return;
            if (value == Settings.CoverUidColor) return;
            if (SaveSettings(Settings with { CoverUidColor = value })) { Notify(); NotifyCoverUid(); }
        }
    }

    public ObservableCollection<CoverBoxRow> CoverBoxRows { get; } = [];

    /// <summary>Rebuilds the editable box rows from the saved list.</summary>
    public void RefreshCoverBoxRows()
    {
        CoverBoxRows.Clear();
        var index = 1;
        foreach (var box in CoverUidBoxList) CoverBoxRows.Add(new CoverBoxRow(index++, box));
        Notify(nameof(CoverBoxRows));
    }

    public void AddCoverBox()
    {
        var boxes = CoverUidBoxList.ToList();
        if (boxes.Count >= CoverBoxes.MaxBoxes) return;
        boxes.Add(new CoverBox(800, 900, 320, 120));
        SaveCoverBoxes(boxes);
    }

    public void RemoveCoverBox(int index)
    {
        var boxes = CoverUidBoxList.ToList();
        if (index < 0 || index >= boxes.Count || boxes.Count == 1) return;
        boxes.RemoveAt(index);
        SaveCoverBoxes(boxes);
    }

    public void SaveCoverBoxes(IReadOnlyList<CoverBox> boxes)
    {
        var clamped = boxes.Select(box => CoverBoxes.Clamp(box.X, box.Y, box.Width, box.Height)).ToList();
        var text = CoverBoxes.Format(clamped);
        if (!SaveSettings(Settings with { CoverUidBoxes = text })) return;
        Notify(nameof(CoverUidBoxList));
        NotifyCoverUid();
        RefreshCoverBoxRows();
    }

    /// <summary>Called when a box was dragged on the overlay.</summary>
    public void SaveCoverBoxAt(int index, CoverBox box)
    {
        var boxes = CoverUidBoxList.ToList();
        if (index < 0 || index >= boxes.Count) return;
        boxes[index] = box;
        SaveCoverBoxes(boxes);
    }

    public void SetCoverUidStatus(string status)
    {
        if (string.Equals(_coverUidStatus, status, StringComparison.Ordinal)) return;
        CoverUidStatus = status;
    }

    /// <summary>Called by the overlay when the user finished dragging or resizing the box.</summary>
    public void SaveCoverUidRect(int x, int y, int width, int height)
    {
        var updated = Settings with
        {
            CoverUidX = Math.Clamp(x, 0, 8192),
            CoverUidY = Math.Clamp(y, 0, 8192),
            CoverUidWidth = Math.Clamp(width, 1, 4096),
            CoverUidHeight = Math.Clamp(height, 1, 4096),
        };
        if (SaveSettings(updated))
        {
            Notify(nameof(CoverUidX));
            Notify(nameof(CoverUidY));
            Notify(nameof(CoverUidWidth));
            Notify(nameof(CoverUidHeight));
        }
    }

    public void ResetCoverUid()
    {
        if (SaveSettings(Settings with
            {
                CoverUidX = UidCoverGeometry.DefaultX,
                CoverUidY = UidCoverGeometry.DefaultY,
                CoverUidWidth = UidCoverGeometry.DefaultWidth,
                CoverUidHeight = UidCoverGeometry.DefaultHeight,
            }))
        {
            Notify(nameof(CoverUidX));
            Notify(nameof(CoverUidY));
            Notify(nameof(CoverUidWidth));
            Notify(nameof(CoverUidHeight));
            NotifyCoverUid();
            SetCoverUidStatus("已恢复默认位置与尺寸。");
        }
    }

    private void SetCoverValue(int value, int min, int max, int current, Func<int, AzurAssistant.Runtime.AssistantSettings> build)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped == current) return;
        if (!SaveSettings(build(clamped))) return;
        Notify();
        NotifyCoverUid();
    }

    private void NotifyCoverUid()
    {
        Notify(nameof(CoverUidRequest));
    }

    // ---- 坐标拾取 ----

    private string _pickedCoordinate = "还没有拾取。点「拾取坐标」后到画面上点一下，会显示客户区坐标和 1920×1080 参考坐标。";
    private string _pickedSnippet = "";

    public string PickedCoordinate { get => _pickedCoordinate; private set { _pickedCoordinate = value; Notify(); } }
    public string PickedSnippet { get => _pickedSnippet; private set { _pickedSnippet = value; Notify(); } }

    /// <summary>Converts a screen point into client/reference coordinates against the game window (or this window).</summary>
    public void PickCoordinateAt(int screenX, int screenY, string colorHex = "")
    {
        int left = 0, top = 0, width = 0, height = 0;
        var source = "屏幕";
        try
        {
            var game = AzurAssistant.Platform.GameWindow.Find("AzurPromilia");
            var geometry = game.Observe();
            left = geometry.X;
            top = geometry.Y;
            width = geometry.Width;
            height = geometry.Height;
            source = "游戏客户区";
        }
        catch (InvalidOperationException)
        {
            // No game: fall back to the assistant window so the tool still works while positioning flows.
            var handle = new System.Windows.Interop.WindowInteropHelper(
                System.Windows.Application.Current?.MainWindow ?? throw new InvalidOperationException()).Handle;
            var origin = new AzurAssistant.Platform.NativeMethods.Point();
            if (handle == IntPtr.Zero
                || !AzurAssistant.Platform.NativeMethods.GetClientRect(handle, out var client)
                || !AzurAssistant.Platform.NativeMethods.ClientToScreen(handle, ref origin))
            {
                PickedCoordinate = "无法获取窗口客户区，请检查游戏或助手窗口状态。";
                return;
            }
            left = origin.X;
            top = origin.Y;
            width = client.Right;
            height = client.Bottom;
            source = "助手客户区（没有找到游戏窗口）";
        }

        var result = AzurAssistant.Runtime.PickCoordinate.FromScreen(screenX, screenY, left, top, width, height);
        var colorPart = colorHex.Length > 0 ? $"　颜色 {colorHex}" : "";
        PickedCoordinate = $"{source}：{result.Describe(width, height)}{colorPart}";
        PickedSnippet = result.InsideClient
            ? result.Snippet + (colorHex.Length > 0 ? Environment.NewLine + colorHex : "")
            : colorHex;
    }

    public void ClearPickedCoordinate()
    {
        PickedCoordinate = "还没有拾取。点「拾取坐标」后到画面上点一下，会显示客户区坐标和 1920×1080 参考坐标。";
        PickedSnippet = "";
    }
}
