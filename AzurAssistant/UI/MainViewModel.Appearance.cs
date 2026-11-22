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
        || _pendingBackground.HasValue || _accentRed.HasValue || _accentGreen.HasValue || _accentBlue.HasValue;

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
        var accent = _accentRed.HasValue || _accentGreen.HasValue || _accentBlue.HasValue;
        var colour = AccentValue;
        var updated = Settings with
        {
            UiOpacity = _pendingUi ?? Settings.UiOpacity,
            TextOpacity = _pendingText ?? Settings.TextOpacity,
            BackgroundOpacity = _pendingBackground ?? Settings.BackgroundOpacity,
            AccentColor = accent ? $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}" : Settings.AccentColor,
        };
        _pendingUi = _pendingText = _pendingBackground = null;
        _accentRed = _accentGreen = _accentBlue = null;
        if (!SaveSettings(updated))
        {
            Notify(nameof(UiOpacityPercent));
            Notify(nameof(TextOpacityPercent));
            Notify(nameof(BackgroundOpacityPercent));
            Notify(nameof(AccentRed));
            Notify(nameof(AccentGreen));
            Notify(nameof(AccentBlue));
            Notify(nameof(AccentValue));
        }
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


    // ---- 界面颜色（按红绿蓝三原色配比） ----
    private int? _accentRed;
    private int? _accentGreen;
    private int? _accentBlue;

    /// <summary>Theme default accent, used when the user has not picked a colour.</summary>
    public System.Windows.Media.Color DefaultAccent => DarkMode
        ? System.Windows.Media.Color.FromRgb(0x62, 0xD2, 0xC2)
        : System.Windows.Media.Color.FromRgb(0x17, 0x8C, 0x84);

    public System.Windows.Media.Color AccentValue
    {
        get
        {
            // Pending slider values win, so dragging previews live before the debounced save lands.
            var baseColour = FromStoredOrDefault();
            return System.Windows.Media.Color.FromRgb(
                (byte)(_accentRed ?? baseColour.R),
                (byte)(_accentGreen ?? baseColour.G),
                (byte)(_accentBlue ?? baseColour.B));
        }
    }

    public int AccentRed
    {
        get => _accentRed ?? FromStoredOrDefault().R;
        set => SetAccentChannel(ref _accentRed, value, nameof(AccentRed));
    }

    public int AccentGreen
    {
        get => _accentGreen ?? FromStoredOrDefault().G;
        set => SetAccentChannel(ref _accentGreen, value, nameof(AccentGreen));
    }

    public int AccentBlue
    {
        get => _accentBlue ?? FromStoredOrDefault().B;
        set => SetAccentChannel(ref _accentBlue, value, nameof(AccentBlue));
    }

    public string AccentHexText => $"#{AccentValue.R:X2}{AccentValue.G:X2}{AccentValue.B:X2}";

    public System.Windows.Media.Brush AccentPreview
    {
        get
        {
            var brush = new System.Windows.Media.SolidColorBrush(AccentValue);
            brush.Freeze();
            return brush;
        }
    }

    private System.Windows.Media.Color FromStoredOrDefault()
    {
        var stored = Settings.AccentColor;
        if (stored is { Length: 7 } && stored[0] == '#')
        {
            try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(stored)!; }
            catch (FormatException) { }
        }
        return DefaultAccent;
    }

    private void SetAccentChannel(ref int? field, int value, string name)
    {
        var clamped = Math.Clamp(value, 0, 255);
        if (field == clamped) return;
        field = clamped;
        Notify(name);
        Notify(nameof(AccentHexText));
        Notify(nameof(AccentPreview));
        Notify(nameof(AccentValue));
        Notify(nameof(HasPendingAppearance));
    }

    /// <summary>Restores the theme accent for the current light/dark mode.</summary>
    public void ResetAccent()
    {
        var fallback = DefaultAccent;
        _accentRed = fallback.R;
        _accentGreen = fallback.G;
        _accentBlue = fallback.B;
        Notify(nameof(AccentRed));
        Notify(nameof(AccentGreen));
        Notify(nameof(AccentBlue));
        Notify(nameof(AccentHexText));
        Notify(nameof(AccentPreview));
        Notify(nameof(AccentValue));
        Notify(nameof(HasPendingAppearance));
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
