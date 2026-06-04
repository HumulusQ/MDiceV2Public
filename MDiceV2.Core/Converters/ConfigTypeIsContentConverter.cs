using System;
using System.Globalization;
using Avalonia.Data.Converters;
using MDiceV2.Models;

/// <summary>
/// 配置类型内容转换器
/// 用于判断是否是可显示内容的配置类型（LineEdit或CheckBox）
/// 排除SectionLabel类型
/// </summary>
namespace MDiceV2.Core.Converters;

/// <summary>
/// 将ConfigType转换为布尔值
/// 当配置类型是LineEdit或CheckBox时返回true（是内容项）
/// 当配置类型是SectionLabel时返回false（仅用于分类显示）
/// </summary>
public class ConfigTypeIsContentConverter : IValueConverter
{
    /// <summary>
    /// 将ConfigType转换为布尔值
    /// 返回是否应该显示为内容项（带卡片的项）
    /// </summary>
    /// <param name="value">ConfigType枚举值</param>
    /// <param name="targetType">目标类型（通常为bool）</param>
    /// <param name="parameter">未使用</param>
    /// <param name="culture">文化信息</param>
    /// <returns>如果类型是LineEdit或CheckBox返回true，SectionLabel返回false</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ConfigType configType)
        {
            // 返回true当类型是内容项（LineEdit或CheckBox）
            // 返回false当类型是SectionLabel（仅用于分类显示）
            return configType is ConfigType.LineEdit or ConfigType.CheckBox;
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
