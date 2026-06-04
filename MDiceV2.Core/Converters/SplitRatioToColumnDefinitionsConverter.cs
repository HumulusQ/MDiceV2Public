using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

/// <summary>
/// 分割比例转换器
/// 将分割比例转换为Grid的ColumnDefinitions
/// </summary>
namespace MDiceV2.Core.Converters;

/// <summary>
/// 将double类型的分割比例转换为ColumnDefinitions对象
/// 用于动态设置Grid的列定义（已废弃，当前使用固定列定义）
/// </summary>
public class SplitRatioToColumnDefinitionsConverter : IValueConverter
{
    /// <summary>
    /// 将分割比例转换为ColumnDefinitions
    /// </summary>
    /// <param name="value">分割比例（0.0-1.0）</param>
    /// <param name="targetType">目标类型</param>
    /// <param name="parameter">转换参数</param>
    /// <param name="culture">文化信息</param>
    /// <returns>ColumnDefinitions对象</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double splitRatio)
        {
            var left = splitRatio;
            var right = 1 - splitRatio;
            return new ColumnDefinitions
            {
                new ColumnDefinition(left, GridUnitType.Star),
                new ColumnDefinition(4, GridUnitType.Pixel), // GridSplitter width
                new ColumnDefinition(right, GridUnitType.Star)
            };
        }
        return new ColumnDefinitions
        {
            new ColumnDefinition(0.5, GridUnitType.Star),
            new ColumnDefinition(4, GridUnitType.Pixel),
            new ColumnDefinition(0.5, GridUnitType.Star)
        };
    }

    /// <summary>
    /// 反向转换（未实现）
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}