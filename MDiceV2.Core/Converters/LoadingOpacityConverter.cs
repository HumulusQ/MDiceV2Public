using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MDiceV2.Core.Converters;

public sealed class LoadingOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 0.0 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
