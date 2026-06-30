using System;
using System.Globalization;
using Avalonia.Data.Converters;
using MDiceV2.Models;

/// <summary>
/// 配置类型转换器
/// 用于在XAML中根据配置类型显示不同的UI控件
/// </summary>
namespace MDiceV2.Core.Converters;

/// <summary>
/// 将ConfigType转换为布尔值，用于控制UI元素的可见性
/// 当配置类型匹配参数时返回true，否则返回false
/// </summary>
public class ConfigTypeToBoolConverter : IValueConverter
{
    /// <summary>
    /// 将ConfigType转换为布尔值
    /// </summary>
    /// <param name="value">ConfigType枚举值</param>
    /// <param name="targetType">目标类型（通常为bool）</param>
    /// <param name="parameter">要比较的ConfigType字符串（如"LineEdit"或"CheckBox"）</param>
    /// <param name="culture">文化信息</param>
    /// <returns>如果类型匹配返回true，否则false</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ConfigType configType && parameter is string param)
        {
            if (Enum.TryParse<ConfigType>(param, out var targetTypeEnum))
            {
                return configType == targetTypeEnum;
            }
        }
        return false;
    }

    /// <summary>
    /// 反向转换（未实现）
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}