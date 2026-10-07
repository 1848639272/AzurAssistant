using System.Windows;
using System.Windows.Media;

namespace AzurAssistant.UI;

/// <summary>Window-scoped semantic colors shared by the production UI and independent WPF hosts.</summary>
public sealed class ThemePalette : ResourceDictionary
{
    public ThemePalette() : this(false) { }

    private ThemePalette(bool dark)
    {
        AddBrush("WindowBackground", dark ? "#101315" : "#F6F8FA");
        AddBrush("SidebarBackground", dark ? "#161B1E" : "#EFF3F5");
        AddBrush("CardBackground", dark ? "#1B2226" : "#FFFFFF");
        AddBrush("TileBackground", dark ? "#222C31" : "#F7FAFA");
        AddBrush("InputBackground", dark ? "#141C20" : "#FFFFFF");
        AddBrush("BorderBrush", dark ? "#35434A" : "#E1E7EB");
        AddBrush("InputBorderBrush", dark ? "#60757F" : "#B5C7CA");
        AddBrush("DividerBrush", dark ? "#35434A" : "#E2ECEB");
        AddBrush("Ink", dark ? "#E8EEF1" : "#243544");
        AddBrush("Muted", dark ? "#ADBDC5" : "#768591");
        AddBrush("Subtle", dark ? "#98AAB4" : "#86959E");
        AddBrush("Accent", dark ? "#62D2C2" : "#178C84");
        AddBrush("AccentForeground", dark ? "#102824" : "#FFFFFF");
        AddBrush("NavigationInk", dark ? "#BDCCD2" : "#667B86");
        AddBrush("NavigationSelectedInk", dark ? "#8BE4D7" : "#16776F");
        AddBrush("HoverBackground", dark ? "#2C3D43" : "#E4EEEF");
        AddBrush("SelectedBackground", dark ? "#234740" : "#DCEDEB");
        AddBrush("HandleInk", dark ? "#A1B5BF" : "#82959D");
        AddBrush("WarningInk", dark ? "#F3B679" : "#B06F40");
        AddBrush("SuccessInk", dark ? "#68D6AE" : "#1C9C84");
        AddBrush("InactiveInk", dark ? "#91A0A8" : "#A6B1B9");
        AddBrush("DropIndicatorBrush", dark ? "#77D0BD" : "#5D9B90");
        AddBrush("MascotBackground", dark ? "#254238" : "#E0F0E9");
        AddBrush("MascotHoverBackground", dark ? "#305544" : "#D3EADD");
        AddBrush("MascotPressedBackground", dark ? "#3B6751" : "#C4DFD0");
        AddBrush("MascotBorderBrush", dark ? "#4C715D" : "#C7DFD3");
        AddBrush("PreviewBackground", dark ? "#0C1216" : "#172733");
        AddBrush("PreviewInk", dark ? "#C3D3DC" : "#A6B8C1");
        AddBrush("PreviewMuted", dark ? "#93ABB8" : "#718995");
    }

    public static ResourceDictionary Create(bool dark) => new ThemePalette(dark);

    public static void Apply(FrameworkElement scope, bool dark)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var palette = Create(dark);
        foreach (var key in palette.Keys)
            scope.Resources[key] = palette[key];
    }

    private void AddBrush(string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Add(key, brush);
    }
}
