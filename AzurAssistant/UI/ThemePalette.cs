using System.Windows;
using System.Windows.Media;

namespace AzurAssistant.UI;

/// <summary>Window-scoped semantic colors shared by the production UI and independent WPF hosts.</summary>
public sealed class ThemePalette : ResourceDictionary
{
    public ThemePalette() : this(false) { }

    private ThemePalette(bool dark)
    {
        AddBrush("WindowBackground", dark ? "#000000" : "#F6F8FA");
        AddBrush("SidebarBackground", dark ? "#0A0A0A" : "#EFF3F5");
        AddBrush("CardBackground", dark ? "#121212" : "#FFFFFF");
        AddBrush("TileBackground", dark ? "#1C1C1C" : "#F7FAFA");
        AddBrush("InputBackground", dark ? "#0D0D0D" : "#FFFFFF");
        AddBrush("BorderBrush", dark ? "#6E6E6E" : "#E1E7EB");
        AddBrush("InputBorderBrush", dark ? "#9A9A9A" : "#B5C7CA");
        AddBrush("DividerBrush", dark ? "#4F4F4F" : "#E2ECEB");
        AddBrush("Ink", dark ? "#FFFFFF" : "#243544");
        AddBrush("Muted", dark ? "#D0D0D0" : "#768591");
        AddBrush("Subtle", dark ? "#B4B4B4" : "#86959E");
        AddBrush("Accent", dark ? "#62D2C2" : "#178C84");
        AddBrush("AccentForeground", dark ? "#102824" : "#FFFFFF");
        AddBrush("NavigationInk", dark ? "#DADADA" : "#667B86");
        AddBrush("NavigationSelectedInk", dark ? "#8BE4D7" : "#16776F");
        AddBrush("HoverBackground", dark ? "#2A2A2A" : "#E4EEEF");
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
