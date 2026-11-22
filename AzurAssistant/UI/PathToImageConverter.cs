using System.Globalization;
using System.Windows.Data;

namespace AzurAssistant.UI;

/// <summary>Converts a material icon path into a frozen bitmap (null when the file is missing).</summary>
public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => MainViewModel.LoadIcon(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
