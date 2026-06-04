using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

/// <summary>
/// 比率转换器
/// 将double类型的比率转换为GridLength（Star单位）
/// </summary>
namespace MDiceV2.Core.Converters;

/// <summary>
/// 将double类型的列宽度比例转换为GridLength对象
/// 用于动态设置Grid的列宽度为Star单位
/// </summary>
public class RatioToStarConverter : IValueConverter
{
    /// <summary>
    /// 将比率转换为GridLength
    /// </summary>
    /// <param name="value">列宽度比例（0.0-1.0）</param>
    /// <param name="targetType">目标类型</param>
    /// <param name="parameter">转换参数</param>
    /// <param name="culture">文化信息</param>
    /// <returns>GridLength对象</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double ratio && ratio >= 0 && ratio <= 1)
        {
            return new GridLength(ratio, GridUnitType.Star);
        }
        if (value is double invalidRatio)
        {
            var clampedRatio = Math.Clamp(invalidRatio, 0.0, 1.0);
            return new GridLength(clampedRatio, GridUnitType.Star);
        }
        return new GridLength(0.5, GridUnitType.Star); // 默认值
    }

    /// <summary>
    /// 将GridLength转换回比率
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is GridLength gridLength && gridLength.GridUnitType == GridUnitType.Star)
        {
            return gridLength.Value;
        }
        return 0.5; // 默认值
    }
}