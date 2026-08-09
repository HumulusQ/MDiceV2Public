using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MDiceV2.Core.Converters;

/// <summary>
/// BoolToWidthConverter - 将布尔值转换为宽度值
/// True -> 展开宽度，False -> 折叠宽度
/// </summary>
public class BoolToWidthConverter : IValueConverter
{
    /// <summary>
    /// 将布尔值转换为宽度值
    /// </summary>
    /// <param name="value">布尔值</param>
    /// <param name="targetType">目标类型</param>
    /// <param name="parameter">参数字符串，格式："折叠宽度,展开宽度"</param>
    /// <param name="culture">文化信息</param>
    /// <returns>宽度值</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue && parameter is string paramString)
        {
            var parts = paramString.Split(',');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], out double collapsedWidth) &&
                double.TryParse(parts[1], out double expandedWidth))
            {
                return boolValue ? expandedWidth : collapsedWidth;
            }
        }

        // 默认返回折叠宽度
        return 52.0;
    }

    /// <summary>
    /// 反向转换（通常不需要）
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}