using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CloudDriveSync.App.Infrastructure;

/// <summary>Visible when the value is true, a non-empty text, a number other than 0 or any other object; "Invert" turns it round.</summary>
public sealed class VisibleWhen : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool flag => flag,
            string text => text.Length > 0,
            int number => number != 0,
            long number => number != 0,
            _ => true,
        };
        return visible != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the value (an enum) has the name given as parameter; several names separated by "|".</summary>
public sealed class VisibleWhenEquals : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var names = (parameter as string ?? "").Split('|');
        var equal = value is not null && names.Contains(value.ToString());
        return equal != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Two-way binding of radio buttons to an enum value (parameter: the name of the value).</summary>
public sealed class EnumIs : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && value.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

/// <summary>Negates a boolean (e.g. IsEnabled while busy).</summary>
public sealed class Not : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
