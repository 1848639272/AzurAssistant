using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzurAssistant.Runtime;

namespace AzurAssistant.UI;

/// <summary>外观设置：四个透明度滑杆 + 自定义背景图（拉伸/保持比例）。</summary>
public sealed partial class MainViewModel
{
    private ImageSource? _backgroundImage;
    private string? _backgroundImageSource;
    // Pending slider values: applied live to the palette, persisted once the drag settles.
    private int? _pendingUi;
    private int? _pendingText;
    private int? _pendingBackground;

    private string BackgroundsDirectory => Path.Combine(
        Path.GetDirectoryName(SoundsDirectory) ?? AppContext.BaseDirectory, "backgrounds");

    public int UiOpacityPercent
    {
        get => _pendingUi ?? Settings.UiOpacity;
        set => SetPending(ref _pendingUi, value, nameof(UiOpacityPercent), nameof(UiOpacityText));
    }

    public int TextOpacityPercent
    {
        get => _pendingText ?? Settings.TextOpacity;
        set => SetPending(ref _pendingText, value, nameof(TextOpacityPercent), nameof(TextOpacityText));
    }

    public int BackgroundOpacityPercent
    {
        get => _pendingBackground ?? Settings.BackgroundOpacity;
        set => SetPending(ref _pendingBackground, value, nameof(BackgroundOpacityPercent), nameof(BackgroundOpacityText));
    }


    public bool HasPendingAppearance => _pendingUi.HasValue || _pendingText.HasValue
        || _pendingBackground.HasValue;

    private void SetPending(ref int? field, int value, params string[] names)
    {
        var clamped = Math.Clamp(value, 0, 100);
        if (field == clamped) return;
        field = clamped;
        foreach (var name in names) Notify(name);
        Notify(nameof(HasPendingAppearance));
    }

    /// <summary>Writes the slider values to disk once the user stops dragging (one write instead of one per tick).</summary>
    public void CommitAppearance()
    {
        if (!HasPendingAppearance) return;
        var updated = Settings with
        {
            UiOpacity = _pendingUi ?? Settings.UiOpacity,
            TextOpacity = _pendingText ?? Settings.TextOpacity,
            BackgroundOpacity = _pendingBackground ?? Settings.BackgroundOpacity,
        };
        _pendingUi = _pendingText = _pendingBackground = null;
        Notify(nameof(HasPendingAppearance));
    }

    public string UiOpacityText => $"{UiOpacityPercent}%";
    public string TextOpacityText => $"{TextOpacityPercent}%";
    public string BackgroundOpacityText => $"{BackgroundOpacityPercent}%";

    public ImageSource? BackgroundImage
    {
        get
        {
            var path = Settings.BackgroundImagePath;
            if (string.IsNullOrWhiteSpace(path)) { _backgroundImage = null; _backgroundImageSource = null; return null; }
            if (_backgroundImage is not null && _backgroundImageSource == path) return _backgroundImage;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                _backgroundImage = bitmap;
                _backgroundImageSource = path;
                return bitmap;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or UriFormatException or ArgumentException)
            {
                _backgroundImage = null;
                _backgroundImageSource = null;
                return null;
            }
        }
    }

    public Stretch BackgroundStretch => BackgroundFitMode ? Stretch.Uniform : Stretch.Fill;

    /// <summary>The picture fades together with the background transparency setting.</summary>
    public double BackgroundLayerOpacity => BackgroundOpacityPercent / 100.0;

    public bool BackgroundStretchMode
    {
        get => !BackgroundFitMode;
        set { if (value) ChangeBackgroundMode("stretch"); }
    }

    public bool BackgroundFitMode
    {
        get => Settings.BackgroundImageMode == "fit";
        set { if (value) ChangeBackgroundMode("fit"); }
    }

    public string BackgroundStatus
    {
        get
        {
            var path = Settings.BackgroundImagePath;
            if (string.IsNullOrWhiteSpace(path)) return "未设置背景图（使用纯色背景）";
            var mode = BackgroundFitMode ? "保持比例，上下/左右留白" : "拉伸铺满";
            return $"已设置：{Path.GetFileName(path)} · {mode}";
        }
    }

    public void ImportBackgroundImage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".gif"))
        { Report("背景图只支持 png / jpg / jpeg / bmp / webp / gif。"); return; }
        try
        {
            if (new FileInfo(path).Length > 20L * 1024 * 1024) { Report("背景图超过 20MB，请先压缩。"); return; }
            Directory.CreateDirectory(BackgroundsDirectory);
            var name = string.Concat(Path.GetFileName(path).Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            var target = Path.Combine(BackgroundsDirectory, name);
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(path, target, overwrite: true);
            SaveAppearance(Settings with { BackgroundImagePath = target });
            Report($"已设置背景图：{name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Report("设置背景图失败：" + ex.Message); }
    }

    public void ClearBackgroundImage()
    {
        SaveAppearance(Settings with { BackgroundImagePath = "" });
        Report("已清除背景图，恢复纯色背景。");
    }

    private void ChangeBackgroundMode(string mode)
    {
        if (Settings.BackgroundImageMode == mode) return;
        SaveAppearance(Settings with { BackgroundImageMode = mode });
    }

    private void SaveAppearance(AssistantSettings settings)
    {
        if (!SaveSettings(settings)) return;
        Notify(nameof(UiOpacityPercent));
        Notify(nameof(TextOpacityPercent));
        Notify(nameof(BackgroundOpacityPercent));
        Notify(nameof(UiOpacityText));
        Notify(nameof(TextOpacityText));
        Notify(nameof(BackgroundOpacityText));
        Notify(nameof(BackgroundImage));
        Notify(nameof(BackgroundStretch));
        Notify(nameof(BackgroundLayerOpacity));
        Notify(nameof(BackgroundStatus));
        Notify(nameof(BackgroundStretchMode));
        Notify(nameof(BackgroundFitMode));
    }
}
