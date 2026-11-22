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

    private string BackgroundsDirectory => Path.Combine(
        Path.GetDirectoryName(SoundsDirectory) ?? AppContext.BaseDirectory, "backgrounds");

    public int UiOpacityPercent
    {
        get => Settings.UiOpacity;
        set => SaveAppearance(Settings with { UiOpacity = Math.Clamp(value, 0, 100) });
    }

    public int TextOpacityPercent
    {
        get => Settings.TextOpacity;
        set => SaveAppearance(Settings with { TextOpacity = Math.Clamp(value, 0, 100) });
    }

    public int BackgroundOpacityPercent
    {
        get => Settings.BackgroundOpacity;
        set => SaveAppearance(Settings with { BackgroundOpacity = Math.Clamp(value, 0, 100) });
    }

    public int WindowOpacityPercent
    {
        get => Settings.WindowOpacity;
        set => SaveAppearance(Settings with { WindowOpacity = Math.Clamp(value, 0, 100) });
    }

    public string UiOpacityText => $"{UiOpacityPercent}%";
    public string TextOpacityText => $"{TextOpacityPercent}%";
    public string BackgroundOpacityText => $"{BackgroundOpacityPercent}%";
    public string WindowOpacityText => $"{WindowOpacityPercent}%";

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
        Notify(nameof(WindowOpacityPercent));
        Notify(nameof(UiOpacityText));
        Notify(nameof(TextOpacityText));
        Notify(nameof(BackgroundOpacityText));
        Notify(nameof(WindowOpacityText));
        Notify(nameof(BackgroundImage));
        Notify(nameof(BackgroundStretch));
        Notify(nameof(BackgroundLayerOpacity));
        Notify(nameof(BackgroundStatus));
        Notify(nameof(BackgroundStretchMode));
        Notify(nameof(BackgroundFitMode));
    }
}
