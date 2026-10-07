using System.Globalization;
using System.Windows.Data;

namespace AzurAssistant.UI;

/// <summary>Formats the stored numeric difficulty using the game's visible tier labels.</summary>
public sealed class DifficultyLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        1 => "Ⅰ", 2 => "Ⅱ", 3 => "Ⅲ", 4 => "Ⅳ", 5 => "Ⅴ", 6 => "Ⅵ", _ => "—"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
