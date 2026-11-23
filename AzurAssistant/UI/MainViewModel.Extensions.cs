using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

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

    public string CoverUidStatus { get => _coverUidStatus; private set { _coverUidStatus = value; Notify(); } }

    /// <summary>Snapshot used by the overlay controller.</summary>
    public UidCoverController.CoverRequest CoverUidRequest =>
        new(Settings.CoverUidEnabled, Settings.CoverUidPreviewOnSelf,
            Settings.CoverUidX, Settings.CoverUidY, Settings.CoverUidWidth, Settings.CoverUidHeight);

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
}
