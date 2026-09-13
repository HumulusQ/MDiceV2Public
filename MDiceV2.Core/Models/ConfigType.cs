/// <summary>
/// 配置项类型枚举
/// 定义配置容器中支持的控件类型
/// </summary>
namespace MDiceV2.Models;

/// <summary>
/// 配置项的控件类型
/// LineEdit: 文本输入框
/// CheckBox: 复选框
/// SectionLabel: 分割标签（仅用于分类显示，靠左无背景）
/// </summary>
public enum ConfigType
{
    /// <summary>
    /// 文本输入控件，用于输入字符串值
    /// </summary>
    LineEdit,

    /// <summary>
    /// 复选框控件，用于布尔值切换
    /// </summary>
    CheckBox,

    /// <summary>
    /// 分割标签，用于区分配置项的大类
    /// 仅显示左对齐的标签，无背景卡片，占据高度用于视觉分割
    /// </summary>
    SectionLabel
}