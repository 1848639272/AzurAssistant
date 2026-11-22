using System.Windows;
using System.Windows.Media;

namespace AzurAssistant.UI;

/// <summary>
/// Window-scoped semantic colours shared by the production UI and independent WPF hosts.
/// Brushes are mutable on purpose: opacity sliders only rewrite their alpha in place, so dragging a slider repaints
/// instead of rebuilding the whole resource dictionary (which used to stall the UI on every tick).
/// The accent colour can be overridden by the user and re-derives the accent foreground and the selected tint.
/// </summary>
public sealed class ThemePalette : ResourceDictionary
{
    private static readonly string[] BackgroundKeys =
    [
        "SidebarBackground", "CardBackground", "TileBackground", "InputBackground",
        "HoverBackground", "SelectedBackground", "PreviewBackground", "MascotBackground", "MascotHoverBackground",
        "MascotPressedBackground",
    ];

    private static readonly string[] TextKeys =
    [
        "Ink", "Muted", "Subtle", "NavigationInk", "NavigationSelectedInk", "HandleInk", "InactiveInk",
        "WarningInk", "SuccessInk", "PreviewInk", "PreviewMuted",
    ];

    private static readonly string[] ChromeKeys =
    [
        "BorderBrush", "InputBorderBrush", "DividerBrush", "MascotBorderBrush", "DropIndicatorBrush",
        "Accent", "AccentForeground",
    ];

    private readonly Dictionary<string, Color> _colors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
    private FrameworkElement? _scope;
    private int _frozenSwaps;
    private int _uiOpacity = 100;
    private int _textOpacity = 100;
    private int _backgroundOpacity = 100;
    private Color _accent;
    private Color _selectedBase;

    public ThemePalette() : this(false, 100, 100, 100, null) { }

    private ThemePalette(bool dark, int uiOpacity, int textOpacity, int backgroundOpacity, Color? accent)
    {
        Dark = dark;
        // Deliberate contrast between the window fill and the surfaces, so the background slider is visible in both themes.
        AddBrush("WindowBackground", dark ? "#000000" : "#E7EDF0");
        AddBrush("SidebarBackground", dark ? "#0D0D0D" : "#DFE7EB");
        AddBrush("CardBackground", dark ? "#1B1B1B" : "#FFFFFF");
        AddBrush("TileBackground", dark ? "#262626" : "#F2F6F8");
        AddBrush("InputBackground", dark ? "#141414" : "#FFFFFF");
        AddBrush("BorderBrush", dark ? "#6E6E6E" : "#D2DEE4");
        AddBrush("InputBorderBrush", dark ? "#9A9A9A" : "#B5C7CA");
        AddBrush("DividerBrush", dark ? "#4F4F4F" : "#DCE6EA");
        AddBrush("Ink", dark ? "#FFFFFF" : "#243544");
        AddBrush("Muted", dark ? "#D0D0D0" : "#768591");
        AddBrush("Subtle", dark ? "#B4B4B4" : "#86959E");
        AddBrush("Accent", dark ? "#62D2C2" : "#178C84");
        AddBrush("AccentForeground", dark ? "#102824" : "#FFFFFF");
        AddBrush("NavigationInk", dark ? "#DADADA" : "#667B86");
        AddBrush("NavigationSelectedInk", dark ? "#8BE4D7" : "#16776F");
        AddBrush("HoverBackground", dark ? "#2E2E2E" : "#E4EEEF");
        AddBrush("SelectedBackground", dark ? "#234740" : "#DCEDEB");
        AddBrush("HandleInk", dark ? "#C4C4C4" : "#82959D");
        AddBrush("WarningInk", dark ? "#F3B679" : "#B06F40");
        AddBrush("SuccessInk", dark ? "#68D6AE" : "#1C9C84");
        AddBrush("InactiveInk", dark ? "#9C9C9C" : "#A6B1B9");
        AddBrush("DropIndicatorBrush", dark ? "#77D0BD" : "#5D9B90");
        AddBrush("MascotBackground", dark ? "#254238" : "#E0F0E9");
        AddBrush("MascotHoverBackground", dark ? "#305544" : "#D3EADD");
        AddBrush("MascotPressedBackground", dark ? "#3B6751" : "#C4DFD0");
        AddBrush("MascotBorderBrush", dark ? "#4C715D" : "#C7DFD3");
        AddBrush("PreviewBackground", dark ? "#0C1216" : "#172733");
        AddBrush("PreviewInk", dark ? "#C3D3DC" : "#A6B8C1");
        AddBrush("PreviewMuted", dark ? "#93ABB8" : "#718995");

        _accent = (Color)_colors["Accent"];
        _selectedBase = (Color)_colors["SelectedBackground"];
        if (accent is { } custom) ApplyAccentColors(custom);
        UpdateOpacity(uiOpacity, textOpacity, backgroundOpacity);
    }

    public bool Dark { get; }

    public Color BaseColor(string key) => _colors.TryGetValue(key, out var color) ? color : Colors.Transparent;

    public Color AccentColor => _accent;

    /// <summary>Creates a palette and pushes it into the scope's resources; returns it for later in-place updates.</summary>
    public static ThemePalette Apply(FrameworkElement scope, bool dark, int uiOpacity = 100, int textOpacity = 100,
        int backgroundOpacity = 100, Color? accent = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var palette = new ThemePalette(dark, Clamp(uiOpacity), Clamp(textOpacity), Clamp(backgroundOpacity), accent);
        palette._scope = scope;
        foreach (var (key, brush) in palette._brushes) scope.Resources[key] = brush;
        return palette;
    }

    public static ThemePalette Create(bool dark, int uiOpacity = 100, int textOpacity = 100, int backgroundOpacity = 100, Color? accent = null)
        => new(dark, Clamp(uiOpacity), Clamp(textOpacity), Clamp(backgroundOpacity), accent);

    /// <summary>Rewrites the alpha of every grouped brush in place; consumers repaint without re-resolving resources.</summary>
    public void UpdateOpacity(int uiOpacity, int textOpacity, int backgroundOpacity)
    {
        UpdateChromeOpacity(uiOpacity);
        UpdateTextOpacity(textOpacity);
        UpdateBackgroundOpacity(backgroundOpacity);
    }

    /// <summary>Only the chrome group: used while the interface slider is being dragged.</summary>
    public void UpdateChromeOpacity(int percent)
    {
        _uiOpacity = Clamp(percent);
        foreach (var key in ChromeKeys) SetAlpha(key, _uiOpacity);
    }

    /// <summary>Only the text group.</summary>
    public void UpdateTextOpacity(int percent)
    {
        _textOpacity = Clamp(percent);
        foreach (var key in TextKeys) SetAlpha(key, _textOpacity);
    }

    /// <summary>Only the surfaces group.</summary>
    public void UpdateBackgroundOpacity(int percent)
    {
        _backgroundOpacity = Clamp(percent);
        foreach (var key in BackgroundKeys) SetAlpha(key, _backgroundOpacity);
    }

    /// <summary>Re-derives the accent, its readable foreground and the selected tint from the user's colour.</summary>
    public void UpdateAccent(Color accent)
    {
        ApplyAccentColors(accent);
        SetAlpha("Accent", _uiOpacity);
        SetAlpha("AccentForeground", _uiOpacity);
        SetAlpha("DropIndicatorBrush", _uiOpacity);
        SetAlpha("SelectedBackground", _backgroundOpacity);
    }

    private void ApplyAccentColors(Color accent)
    {
        _accent = accent;
        _colors["Accent"] = accent;
        // Keep labels readable on any accent the user picks.
        var luminance = 0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B;
        _colors["AccentForeground"] = luminance > 140 ? Color.FromRgb(16, 40, 36) : Colors.White;
        _colors["SelectedBackground"] = Blend(_selectedBase, accent, 0.35);
        _colors["DropIndicatorBrush"] = accent;
    }

    private static Color Blend(Color from, Color to, double ratio)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * ratio);
        return Color.FromArgb(from.A, Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    private static int Clamp(int value) => Math.Clamp(value, 0, 100);

    private void AddBrush(string key, string color)
    {
        var parsed = (Color)ColorConverter.ConvertFromString(color);
        _colors[key] = parsed;
        var brush = new SolidColorBrush(parsed);
        _brushes[key] = brush;
        Add(key, brush);
    }

    /// <summary>How many brushes had to be replaced because WPF froze them (reported once for diagnostics).</summary>
    public int FrozenSwapCount => _frozenSwaps;

    /// <summary>
    /// Rewrites the brush alpha in place. WPF may freeze brushes that ended up in a sealed template, so a frozen
    /// brush is replaced by a fresh instance instead of throwing (that exception used to crash the app mid-drag).
    /// </summary>
    private void SetAlpha(string key, int percent)
    {
        if (!_colors.TryGetValue(key, out var baseColor)) return;
        if (!_brushes.TryGetValue(key, out var brush)) return;
        var alpha = (byte)Math.Round(255 * Clamp(percent) / 100.0);
        var color = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
        if (brush.Color == color) return;
        if (!brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }
        var replacement = new SolidColorBrush(color);
        _frozenSwaps++;
        _brushes[key] = replacement;
        this[key] = replacement;
        if (_scope is not null) _scope.Resources[key] = replacement;
    }
}
