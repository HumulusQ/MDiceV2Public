using CommunityToolkit.Mvvm.ComponentModel;
using System;
using MDiceV2.Models;

/// <summary>
/// 配置项模型类
/// 表示配置容器中的单个配置项，包含键、类型、值以及列宽度设置
/// </summary>
namespace MDiceV2.Models;

/// <summary>
/// 配置项数据模型
/// 实现INotifyPropertyChanged用于UI数据绑定
/// </summary>
public partial class ConfigItem : ObservableObject
{
    /// <summary>
    /// 配置项的唯一标识符/标签
    /// </summary>
    [ObservableProperty]
    private string key;

    /// <summary>
    /// 配置项的控件类型
    /// 决定UI中显示的控件类型（文本框或复选框）
    /// </summary>
    [ObservableProperty]
    private ConfigType type;

    /// <summary>
    /// 配置项的值
    /// 对于CheckBox为bool，对于LineEdit为string
    /// </summary>
    [ObservableProperty]
    private object? value;

    /// <summary>
    /// 默认值，用于显示重置按钮和恢复默认
    /// </summary>
    [ObservableProperty]
    private object? defaultValue;

    partial void OnValueChanged(object? value)
    {
        // 当Value属性变化时调用回调
        if (ValueChangedCallback != null)
        {
            try
            {
                ValueChangedCallback?.Invoke(Key, value);
            }
            catch (Exception ex)
            {
                LogSender.Error($"[ConfigItem.OnValueChanged] 异常: {ex.Message}");
            }
        }
        
        OnPropertyChanged(nameof(ValueAsBool));
        OnPropertyChanged(nameof(ShowResetButton));
    }

    partial void OnDefaultValueChanged(object? value)
    {
        OnPropertyChanged(nameof(ShowResetButton));
    }

    /// <summary>
    /// 值变化回调
    /// </summary>
    private Action<string, object?>? valueChangedCallback;

    /// <summary>
    /// 值变化回调属性
    /// </summary>
    public Action<string, object?>? ValueChangedCallback
    {
        get => valueChangedCallback;
        set => valueChangedCallback = value;
    }


    /// <summary>
    /// 左侧列的宽度比例（0.0-1.0）
    /// 用于持久化GridSplitter的位置
    /// </summary>
    [ObservableProperty]
    private double leftColumnRatio = 0.5;

    /// <summary>
    /// 右侧列的宽度比例（0.0-1.0）
    /// 用于持久化GridSplitter的位置
    /// </summary>
    [ObservableProperty]
    private double rightColumnRatio = 0.5;

    /// <summary>
    /// 是否显示重置按钮：有默认值且当前值与默认不同
    /// </summary>
    public bool ShowResetButton
    {
        get
        {
            if (DefaultValue == null) return false;
            if (Type == ConfigType.CheckBox)
            {
                bool? currentBool = ValueAsBool;
                bool? defaultBool = ToNullableBool(DefaultValue);
                if (currentBool.HasValue && defaultBool.HasValue)
                {
                    return currentBool.Value != defaultBool.Value;
                }
            }

            string current = Value?.ToString() ?? string.Empty;
            string def = DefaultValue?.ToString() ?? string.Empty;
            return !string.Equals(current, def, StringComparison.Ordinal);
        }
    }

    private static bool? ToNullableBool(object? value)
    {
        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }



    /// <summary>
    /// 配置项值转换为布尔值（用于CheckBox绑定）
    /// </summary>
    public bool? ValueAsBool
    {
        get
        {
            if (Value is bool boolValue)
                return boolValue;
            if (Value is string stringValue)
                return bool.TryParse(stringValue, out var result) ? result : null;
            return null;
        }
        set
        {
            if (value.HasValue)
            {
                // 只设置Value，OnValueChanged会自动触发回调
                // 不要在这里重复调用ValueChangedCallback
                Value = value.Value;
            }
        }
    }

    /// <summary>
    /// 配置项构造函数
    /// </summary>
    /// <param name="key">配置项的键/标签</param>
    /// <param name="type">配置项的类型</param>
    /// <param name="value">配置项的初始值，可为null</param>
    public ConfigItem(string key, ConfigType type, object? value = null)
    {
        Key = key;
        Type = type;
        Value = value;
    }
}