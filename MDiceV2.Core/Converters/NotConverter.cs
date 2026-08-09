using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MDiceV2.Core.Converters;

/// <summary>
/// 布尔值反转转换器
/// 用于将 true 转换为 false，将 false 转换为 true
/// 常用于控制 IsVisible 等布尔属性
/// </summary>
public class NotConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return false;
    }
}
