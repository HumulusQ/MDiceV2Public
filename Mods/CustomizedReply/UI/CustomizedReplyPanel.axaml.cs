using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CustomizedReply.UI;

/// <summary>
/// 布尔值取反转换器
/// </summary>
public class BoolNegationConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        return value is bool boolValue ? !boolValue : false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        return value is bool boolValue ? !boolValue : false;
    }
}

/// <summary>
/// 条件类型到背景色的转换器
/// </summary>
public class ConditionTypeColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        if (value is MatchConditionType conditionType)
        {
            return conditionType switch
            {
                MatchConditionType.MatchType => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#258292")), // 青色
                MatchConditionType.QQRestriction => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF9800")), // 橙色
                MatchConditionType.GroupRestriction => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#66BB6A")), // 绿色
                MatchConditionType.LevelRestriction => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#66BB6A")), // 绿色
                _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#444444"))
            };
        }
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#444444"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 判断是否为匹配类型条件的转换器
/// </summary>
public class IsMatchTypeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        bool isMatchType = value is MatchConditionType type && type == MatchConditionType.MatchType;
        
        // 如果有 parameter="Invert"，则取反
        bool shouldInvert = parameter?.ToString() == "Invert";
        return shouldInvert ? !isMatchType : isMatchType;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// MatchConditionType 到中文显示名称的转换器
/// </summary>
public class MatchConditionTypeDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        if (value is MatchConditionType type)
        {
            return type switch
            {
                MatchConditionType.MatchType => "匹配类型",
                MatchConditionType.QQRestriction => "账号限制",
                MatchConditionType.GroupRestriction => "群号限制",
                MatchConditionType.LevelRestriction => "名牌等级限制",
                MatchConditionType.DailyUsageLimit => "每日次数限制",
                MatchConditionType.TimeCooldown => "时间限制",
                _ => "未知"
            };
        }
        return value?.ToString() ?? "未知";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        if (value is string str)
        {
            return str switch
            {
                "账号限制" => MatchConditionType.QQRestriction,
                "群号限制" => MatchConditionType.GroupRestriction,
                "名牌等级限制" => MatchConditionType.LevelRestriction,
                "每日次数限制" => MatchConditionType.DailyUsageLimit,
                "时间限制" => MatchConditionType.TimeCooldown,
                _ => null
            };
        }
        return null;
    }
}

/// <summary>
/// 匹配条件卡片的 DataTemplate 选择器
/// 根据条件类型选择不同的 UI 模板
/// </summary>
public class ConditionCardTemplateSelector : IDataTemplate
{
    public IDataTemplate? MatchTypeTemplate { get; set; }
    public IDataTemplate? QQRestrictionTemplate { get; set; }
    public IDataTemplate? GroupRestrictionTemplate { get; set; }
    public IDataTemplate? LevelRestrictionTemplate { get; set; }
    public IDataTemplate? DailyLimitTemplate { get; set; }
    public IDataTemplate? TimeCooldownTemplate { get; set; }

    public Control? Build(object? param)
    {
        if (param is MatchConditionCard card)
        {
            var template = card.ConditionType switch
            {
                MatchConditionType.MatchType => MatchTypeTemplate,
                MatchConditionType.QQRestriction => QQRestrictionTemplate,
                MatchConditionType.GroupRestriction => GroupRestrictionTemplate,
                MatchConditionType.LevelRestriction => LevelRestrictionTemplate,
                MatchConditionType.DailyUsageLimit => DailyLimitTemplate,
                MatchConditionType.TimeCooldown => TimeCooldownTemplate,
                _ => null
            };
            
            return template?.Build(param);
        }
        return null;
    }

    public bool Match(object? data)
    {
        return data is MatchConditionCard;
    }
}

/// <summary>
/// 匹配类型卡片专用模板匹配器
/// </summary>
public class MatchTypeTemplateSelector : IDataTemplate
{
    public IDataTemplate? Template { get; set; }

    public Control? Build(object? param) => Template?.Build(param);

    public bool Match(object? data)
    {
        return data is MatchConditionCard { ConditionType: MatchConditionType.MatchType };
    }
}

/// <summary>
/// QQ限制卡片专用模板匹配器
/// </summary>
public class QQRestrictionTemplateSelector : IDataTemplate
{
    public IDataTemplate? Template { get; set; }

    public Control? Build(object? param) => Template?.Build(param);

    public bool Match(object? data)
    {
        return data is MatchConditionCard { ConditionType: MatchConditionType.QQRestriction };
    }
}

/// <summary>
/// 群号限制卡片专用模板匹配器
/// </summary>
public class GroupRestrictionTemplateSelector : IDataTemplate
{
    public IDataTemplate? Template { get; set; }

    public Control? Build(object? param) => Template?.Build(param);

    public bool Match(object? data)
    {
        return data is MatchConditionCard { ConditionType: MatchConditionType.GroupRestriction };
    }
}

/// <summary>
/// 名牌等级限制卡片专用模板匹配器
/// </summary>
public class LevelRestrictionTemplateSelector : IDataTemplate
{
    public IDataTemplate? Template { get; set; }

    public Control? Build(object? param) => Template?.Build(param);

    public bool Match(object? data)
    {
        return data is MatchConditionCard { ConditionType: MatchConditionType.LevelRestriction };
    }
}

/// <summary>
/// 每日次数限制卡片专用模板匹配器
/// </summary>
public class DailyLimitTemplateSelector : IDataTemplate
{
    public IDataTemplate? Template { get; set; }

    public Control? Build(object? param) => Template?.Build(param);

    public bool Match(object? data)
    {
        return data is MatchConditionCard { ConditionType: MatchConditionType.DailyUsageLimit };
    }
}

/// <summary>
/// 时间限制（冷却时间）卡片专用模板匹配器
/// </summary>
public class TimeCooldownTemplateSelector : IDataTemplate
{
    public IDataTemplate? Template { get; set; }

    public Control? Build(object? param) => Template?.Build(param);

    public bool Match(object? data)
    {
        return data is MatchConditionCard { ConditionType: MatchConditionType.TimeCooldown };
    }
}

/// <summary>
/// 匹配条件的类型
/// </summary>
public enum MatchConditionType
{
    MatchType = 0,        // 匹配类型（精确、正则、模糊）- 不可删除
    QQRestriction = 1,    // 账号限制
    GroupRestriction = 2, // 群号限制
    LevelRestriction = 3, // 名牌等级限制
    DailyUsageLimit = 4,  // 每日次数限制
    TimeCooldown = 5      // 时间限制（冷却时间）
}

/// <summary>
/// 单个匹配条件卡片的数据模型
/// </summary>
public class MatchConditionCard : INotifyPropertyChanged
{
    private MatchConditionType _conditionType;
    private string _value = "";
    private string _value2 = ""; // 用于作用域、单位等附加参数
    private bool _isInverted = false;

    /// <summary>
    /// 所属的规则，用于在类型切换时通知父集合刷新卡片
    /// </summary>
    public ReplyRuleItem? Owner { get; set; }

    public MatchConditionType ConditionType
    {
        get => _conditionType;
        set
        {
            if (SetField(ref _conditionType, value, out var changed))
            {
                if (changed)
                {
                    // 当类型切换为DailyUsageLimit或TimeCooldown时，设置Value2的默认值
                    if (string.IsNullOrEmpty(_value2))
                    {
                        _value2 = value switch
                        {
                            MatchConditionType.DailyUsageLimit => "按用户",
                            MatchConditionType.TimeCooldown => "秒",
                            _ => _value2
                        };
                    }
                    
                    // 通知父规则：此卡片类型发生变化，需要强制重建模板
                    Owner?.RefreshCard(this);
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConditionTypeDisplay)));
                }
            }
        }
    }

    /// <summary>
    /// 卡片类型的中文显示名称（用于 ComboBox 绑定）
    /// </summary>
    public string ConditionTypeDisplay
    {
        get => DisplayName;
        set
        {
            var type = value switch
            {
                "账号限制" => MatchConditionType.QQRestriction,
                "群号限制" => MatchConditionType.GroupRestriction,
                "名牌等级限制" => MatchConditionType.LevelRestriction,
                "每日次数限制" => MatchConditionType.DailyUsageLimit,
                "时间限制" => MatchConditionType.TimeCooldown,
                _ => ConditionType
            };
            if (type != ConditionType)
            {
                ConditionType = type;
            }
        }
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    /// <summary>
    /// 附加值（用于作用域、单位等）
    /// </summary>
    public string Value2
    {
        get => _value2;
        set => SetProperty(ref _value2, value);
    }

    /// <summary>
    /// 是否反相匹配
    /// </summary>
    public bool IsInverted
    {
        get => _isInverted;
        set => SetProperty(ref _isInverted, value);
    }

    /// <summary>
    /// 获取条件的显示名称
    /// </summary>
    public string DisplayName => ConditionType switch
    {
        MatchConditionType.MatchType => "匹配类型",
        MatchConditionType.QQRestriction => "账号限制",
        MatchConditionType.GroupRestriction => "群号限制",
        MatchConditionType.LevelRestriction => "名牌等级限制",
        MatchConditionType.DailyUsageLimit => "每日次数限制",
        MatchConditionType.TimeCooldown => "时间限制",
        _ => "未知条件"
    };

    /// <summary>
    /// 是否可以删除（匹配类型不可删除）
    /// </summary>
    public bool CanDelete => ConditionType != MatchConditionType.MatchType;

    /// <summary>
    /// 是否可以修改卡片类型（匹配类型不可修改）
    /// </summary>
    public bool CanChangeType => ConditionType != MatchConditionType.MatchType;

    /// <summary>
    /// 获取所有可用的条件类型
    /// </summary>
    public static IEnumerable<MatchConditionType> AvailableConditionTypes =>
        Enum.GetValues(typeof(MatchConditionType)).Cast<MatchConditionType>();

    /// <summary>
    /// 获取可用的匹配类型选项
    /// </summary>
    public static IEnumerable<string> MatchTypeOptions => new[] { "精确匹配", "正则表达式", "模糊匹配" };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        
        // 当匹配条件卡片的值改变时，通知 Owner（ReplyRuleItem）进行即时保存
        if (Owner != null)
        {
            if (propertyName == nameof(Value) && ConditionType == MatchConditionType.MatchType)
            {
                // MatchType 卡片的 Value 改变时
                Owner.NotifyMatchTypeChanged();
            }
            else if (propertyName == nameof(Value) || propertyName == nameof(Value2) || propertyName == nameof(IsInverted))
            {
                // 其他条件的 Value、Value2 或 IsInverted 改变时，立即同步到 Mod
                Owner.NotifyConditionValueChanged();
            }
            else if (propertyName == nameof(ConditionType))
            {
                // 条件类型改变时，需要同步全部条件
                Owner.NotifyConditionValueChanged();
            }
        }
    }

    // 帮助方法：在需要知道是否实际发生变化时使用
    private bool SetField<T>(ref T field, T value, out bool changed, [CallerMemberName] string propertyName = "")
    {
        if (Equals(field, value))
        {
            changed = false;
            return false;
        }

        field = value;
        changed = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

/// <summary>
/// 自定义回复规则的数据模型
/// </summary>
public class ReplyRuleItem : INotifyPropertyChanged
{
    private string _trigger = "";
    private ObservableCollection<MatchConditionCard> _matchConditions = new();
    private List<string> _replies = new();
    private bool _isScriptEditMode = false;
    private string _scriptContent = "";
    private string _scriptFilePath = "";
    private string? _scriptInstanceUid;
    private ObservableCollection<string> _scriptCalls = new();
    
    /// <summary>
    /// 关联的 Mod 规则对象，属性修改时会同步到此对象
    /// </summary>
    public dynamic? AssociatedModRule { get; set; }

    public string Trigger
    {
        get => _trigger;
        set => SetProperty(ref _trigger, value);
    }

    /// <summary>
    /// 匹配条件卡片集合（第一个总是匹配类型，不可删除）
    /// </summary>
    public ObservableCollection<MatchConditionCard> MatchConditions
    {
        get => _matchConditions;
        set => SetProperty(ref _matchConditions, value);
    }

    /// <summary>
    /// 获取或设置匹配类型（便捷属性，实际存储在第一个MatchCondition中）
    /// </summary>
    public string MatchType
    {
        get => _matchConditions.FirstOrDefault()?.Value ?? "";
        set
        {
            var matchTypeCard = _matchConditions.FirstOrDefault();
            if (matchTypeCard != null && matchTypeCard.Value != value)
            {
                matchTypeCard.Value = value;
                // 触发PropertyChanged以通知绑定和事件处理器
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchType)));
            }
        }
    }

    public List<string> Replies
    {
        get => _replies;
        set => SetProperty(ref _replies, value);
    }

    /// <summary>
    /// 是否处于脚本编辑模式
    /// </summary>
    public bool IsScriptEditMode
    {
        get => _isScriptEditMode;
        set => SetProperty(ref _isScriptEditMode, value);
    }

    /// <summary>
    /// Lua脚本内容
    /// </summary>
    public string ScriptContent
    {
        get => _scriptContent;
        set => SetProperty(ref _scriptContent, value);
    }

    /// <summary>
    /// 选中的脚本文件路径
    /// </summary>
    public string ScriptFilePath
    {
        get => _scriptFilePath;
        set => SetProperty(ref _scriptFilePath, value);
    }

    /// <summary>
    /// 脚本实例 UID
    /// </summary>
    public string? ScriptInstanceUid
    {
        get => _scriptInstanceUid;
        set => SetProperty(ref _scriptInstanceUid, value);
    }

    /// <summary>
    /// 脚本函数调用列表
    /// </summary>
    public ObservableCollection<string> ScriptCalls
    {
        get => _scriptCalls;
        set => SetProperty(ref _scriptCalls, value);
    }

    public ReplyRuleItem()
    {
        // 初始化时添加匹配类型卡片（不可删除）
        _matchConditions.Add(new MatchConditionCard
        {
            ConditionType = MatchConditionType.MatchType,
            Value = "精确匹配",  // 中文值，与 MatchTypeOptions 匹配供 ComboBox 绑定
            Owner = this
        });
    }

    /// <summary>
    /// 当某个匹配条件卡片的类型发生变化时，强制替换集合中的元素，触发 UI 重新选择 DataTemplate
    /// </summary>
    public void RefreshCard(MatchConditionCard card)
    {
        var index = _matchConditions.IndexOf(card);
        if (index < 0) return;

        var newCard = new MatchConditionCard
        {
            ConditionType = card.ConditionType,
            Value = card.Value,
            IsInverted = card.IsInverted,
            Owner = this
        };

        _matchConditions[index] = newCard;
    }

    /// <summary>
    /// 通知匹配类型属性已改变（由 MatchConditionCard 在其 Value 改变时调用）
    /// </summary>
    public void NotifyMatchTypeChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchType)));
    }

    /// <summary>
    /// 通知条件值已改变，用于即时保存条件到 Mod 内存
    /// 当 MatchConditionCard 的 Value、Value2 或 IsInverted 属性改变时调用
    /// </summary>
    public void NotifyConditionValueChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchConditions)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


/// <summary>
/// CustomizedReplyPanel 的 ViewModel
/// 管理规则列表、编辑状态和用户交互
/// </summary>
public class CustomizedReplyPanelViewModel : INotifyPropertyChanged
{
    private ObservableCollection<ReplyRuleItem> _rules = new();
    private ReplyRuleItem? _selectedRule;
    private string _searchText = "";
    private ObservableCollection<ScriptResource> _scriptResources = new();
    private ScriptResource? _selectedScriptResource;
    private string _scriptSearchText = "";
    private ObservableCollection<string> _scriptInstances = new();
    private string? _selectedScriptFile;
    private ObservableCollection<string> _availableScriptFiles = new();

    public ObservableCollection<ReplyRuleItem> Rules
    {
        get => _rules;
        set => SetProperty(ref _rules, value);
    }

    public ReplyRuleItem? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (!Equals(_selectedRule, value))
            {
                _selectedRule = value;
                // 切换规则时，加载该规则的脚本文件选择（如果有）
                _selectedScriptFile = value?.ScriptFilePath;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRule)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedScriptFile)));
                // 当选中规则改变时，通知所有相关属性也改变了
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRepliesText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScriptEditMode)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedScriptContent)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedScriptFileName)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScriptFileIcon)));
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    /// <summary>
    /// 脚本资源列表
    /// </summary>
    public ObservableCollection<ScriptResource> ScriptResources
    {
        get => _scriptResources;
        set => SetProperty(ref _scriptResources, value);
    }

    /// <summary>
    /// 选中的脚本资源
    /// 当选择脚本时，自动生成ScriptInstanceUid、关联文件名，并解析脚本中的函数
    /// </summary>
    public ScriptResource? SelectedScriptResource
    {
        get => _selectedScriptResource;
        set
        {
            if (!Equals(_selectedScriptResource, value))
            {
                _selectedScriptResource = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedScriptResource)));
                
                // 当选择脚本资源时，自动生成ScriptInstanceUid并同步文件名
                if (value != null && _selectedRule != null)
                {
                    // 直接通过属性setter赋值，会自动触发PropertyChanged事件
                    _selectedRule.ScriptInstanceUid = Guid.NewGuid().ToString();
                    _selectedRule.ScriptFilePath = value.FileName;
                    
                    // 不需要这里调用SaveRulesImmediately，PropertyChanged事件会自动触发
                    // OnUIRulePropertyChanged会处理同步和保存
                }
            }
        }
    }

    /// <summary>
    /// 脚本搜索文本
    /// </summary>
    public string ScriptSearchText
    {
        get => _scriptSearchText;
        set => SetProperty(ref _scriptSearchText, value);
    }

    /// <summary>
    /// 可用的脚本实例 UID 列表（用于下拉选择）
    /// </summary>
    public ObservableCollection<string> ScriptInstances
    {
        get => _scriptInstances;
        set => SetProperty(ref _scriptInstances, value);
    }

    /// <summary>
    /// 所有可用的脚本文件列表（显示在 ComboBox 中）
    /// 从 ScriptResources 中提取文件名
    /// </summary>
    public ObservableCollection<string> AvailableScriptFiles
    {
        get => _availableScriptFiles;
        set => SetProperty(ref _availableScriptFiles, value);
    }

    /// <summary>
    /// 当前选择的脚本文件名
    /// 选择后会自动生成 ScriptInstanceUid
    /// </summary>
    public string? SelectedScriptFile
    {
        get => _selectedScriptFile;
        set
        {
            if (!Equals(_selectedScriptFile, value))
            {
                _selectedScriptFile = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedScriptFile)));
                
                // 当用户选择脚本时，自动生成 ScriptInstanceUid 并保存脚本文件路径
                if (!string.IsNullOrEmpty(value) && _selectedRule != null)
                {
                    _selectedRule.ScriptInstanceUid = Guid.NewGuid().ToString();
                    _selectedRule.ScriptFilePath = value;  // 保存脚本文件路径
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRule)));
                }
            }
        }
    }

    /// <summary>
    /// 选中规则的回复内容（分号分隔的字符串）
    /// 当规则改变时自动更新，当文本改变时同步到 Replies 列表
    /// </summary>
    public string SelectedRepliesText
    {
        get => _selectedRule != null ? string.Join(";", _selectedRule.Replies) : "";
        set
        {
            if (_selectedRule != null)
            {
                _selectedRule.Replies = value.Split(';')
                    .Select(r => r.Trim())
                    .Where(r => !string.IsNullOrEmpty(r))
                    .ToList();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRepliesText)));
            }
        }
    }

    /// <summary>
    /// 选中规则是否处于脚本编辑模式
    /// </summary>
    public bool IsScriptEditMode
    {
        get => _selectedRule?.IsScriptEditMode ?? false;
        set
        {
            if (_selectedRule != null && _selectedRule.IsScriptEditMode != value)
            {
                _selectedRule.IsScriptEditMode = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScriptEditMode)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedScriptContent)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedScriptFileName)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScriptFileIcon)));
            }
        }
    }

    /// <summary>
    /// 选中规则的Lua脚本内容
    /// </summary>
    public string SelectedScriptContent
    {
        get => _selectedRule?.ScriptContent ?? "";
        set
        {
            if (_selectedRule != null)
            {
                _selectedRule.ScriptContent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedScriptContent)));
            }
        }
    }

    /// <summary>
    /// 选中规则的脚本文件名（仅文件名，不包含路径）
    /// </summary>
    public string SelectedScriptFileName
    {
        get
        {
            if (_selectedRule?.ScriptFilePath == null) return "";
            return Path.GetFileName(_selectedRule.ScriptFilePath);
        }
    }

    /// <summary>
    /// 脚本文件icon的图片源
    /// 如果选中了脚本文件，显示加载成功的icon；否则显示空icon
    /// </summary>
    public object? ScriptFileIcon
    {
        get
        {
            if (string.IsNullOrEmpty(_selectedRule?.ScriptFilePath))
            {
                // 返回默认icon或null
                return null;
            }
            // 返回脚本已加载的icon
            try
            {
                return new Avalonia.Media.Imaging.Bitmap("avares://CustomizedReply/Assets/Sprite/Success.png");
            }
            catch
            {
                return null;
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 公开方法用于通知属性变化
    /// 允许外部代码（如 UI 代码后台）手动触发属性变化通知
    /// </summary>
    public void NotifyPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// CustomizedReply Mod 的导航面板
/// 显示和编辑自定义回复规则的 UI
/// </summary>
public partial class CustomizedReplyPanel : UserControl
{
    private CustomizedReplyPanelViewModel _viewModel;
    private CustomizedReplyMod? _mod;
    private bool _isInitialized = false;  // ✅ 防止重复初始化

    public CustomizedReplyPanel()
    {
        InitializeComponent();
        
        _viewModel = new CustomizedReplyPanelViewModel();
        DataContext = _viewModel;

        // 为 ItemsControl 添加模板匹配器
        if (this.FindControl<ItemsControl>("MatchConditionsItemsControl") is ItemsControl itemsControl)
        {
            if (Resources["MatchTypeSelector"] is IDataTemplate matchTypeTemplate)
                itemsControl.DataTemplates.Add(matchTypeTemplate);
            if (Resources["QQRestrictionSelector"] is IDataTemplate qqTemplate)
                itemsControl.DataTemplates.Add(qqTemplate);
            if (Resources["GroupRestrictionSelector"] is IDataTemplate groupTemplate)
                itemsControl.DataTemplates.Add(groupTemplate);
            if (Resources["LevelRestrictionSelector"] is IDataTemplate levelTemplate)
                itemsControl.DataTemplates.Add(levelTemplate);
            if (Resources["DailyLimitSelector"] is IDataTemplate dailyLimitTemplate)
                itemsControl.DataTemplates.Add(dailyLimitTemplate);
            if (Resources["TimeCooldownSelector"] is IDataTemplate timeCooldownTemplate)
                itemsControl.DataTemplates.Add(timeCooldownTemplate);
        }

    }

    /// <summary>
    /// 通过构造函数传入mod实例以访问已加载的规则
    /// </summary>
    public CustomizedReplyPanel(CustomizedReplyMod mod) : this()
    {
        _mod = mod;
        
        // ✅ 监听ViewModel的PropertyChanged事件，以处理脚本选择
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += (s, e) =>
            {
                // 当脚本文件被选择时
                if (e.PropertyName == nameof(CustomizedReplyPanelViewModel.SelectedScriptFile) && 
                    !string.IsNullOrEmpty(_viewModel.SelectedScriptFile) && 
                    _viewModel.SelectedRule != null)
                {
                    HandleScriptFileSelected(_viewModel.SelectedRule, _viewModel.SelectedScriptFile);
                }
            };
        }
        
        // ✅ 初始化时仅加载一次规则、防止标签页切换时重复加载
        if (!_isInitialized)
        {
            RefreshScriptsFromDisk();  // 先加载脚本资源，以便规则初始化时可以使用
            RefreshRulesFromMod();     // 再加载规则，这样可以为规则初始化脚本函数
            _isInitialized = true;
        }
        
        // 订阅mod的ConfigChanged事件，当远程配置推送时刷新UI
        if (_mod != null)
        {
            _mod.ConfigChanged += (key, value) =>
            {
                var lowerKey = key?.ToLowerInvariant() ?? "";
                
                // 处理规则配置变化（推送拉取都触发）
                if (lowerKey == "mod.customreply.rules")
                {
                    _mod?.LogInfo($"[CustomizedReplyPanel] ► ConfigChanged event received: key='{key}', valueLength={value?.Length ?? 0}");
                    _mod?.LogInfo($"[CustomizedReplyPanel] ► Detected rules config change, scheduling UI refresh on main thread...");
                    
                    // 确保在主线程上刷新UI，因为ConfigChanged事件可能从gRPC线程触发
                    Dispatcher.UIThread.Post(() => 
                    {
                        _mod?.LogInfo($"[CustomizedReplyPanel] ► Executing RefreshRulesFromMod from UI thread");
                        try
                        {
                            RefreshScriptsFromDisk();  // 先刷新脚本资源，以便规则初始化时可以使用
                            RefreshRulesFromMod();     // 再刷新规则，这样可以为规则初始化脚本函数
                            UpdateScriptInstances();    // 更新脚本实例UID列表
                            _mod?.LogInfo($"[CustomizedReplyPanel] ✓ RefreshScriptsFromDisk, RefreshRulesFromMod and UpdateScriptInstances completed successfully");
                        }
                        catch (Exception ex)
                        {
                            _mod?.LogInfo($"[CustomizedReplyPanel] ✗ RefreshScriptsFromDisk or RefreshRulesFromMod failed: {ex.Message}");
                        }
                    });
                }
                
                // 处理脚本配置变化（远程推送脚本时刷新UI）
                else if (lowerKey == "mod.customreply.scripts")
                {
                    _mod?.LogInfo($"[CustomizedReplyPanel] ► ConfigChanged event received: key='{key}', valueLength={value?.Length ?? 0}");
                    _mod?.LogInfo($"[CustomizedReplyPanel] ► Detected scripts config change, scheduling UI refresh on main thread...");
                    
                    // 确保在主线程上刷新UI，因为ConfigChanged事件可能从gRPC线程触发
                    Dispatcher.UIThread.Post(() => 
                    {
                        _mod?.LogInfo($"[CustomizedReplyPanel] ► Executing RefreshScriptsFromDisk from UI thread");
                        try
                        {
                            RefreshScriptsFromDisk();  // 刷新脚本UI，脚本已经被 ApplyConfigAsync 写入 sync-scripts/
                            _mod?.LogInfo($"[CustomizedReplyPanel] ✓ RefreshScriptsFromDisk completed successfully");
                        }
                        catch (Exception ex)
                        {
                            _mod?.LogInfo($"[CustomizedReplyPanel] ✗ RefreshScriptsFromDisk failed: {ex.Message}");
                        }
                    });
                }
            };
            _mod?.LogInfo($"[CustomizedReplyPanel] ✓ ConfigChanged event subscription established");
        }
        else
        {
            _mod?.LogInfo($"[CustomizedReplyPanel] ⚠ Mod is null, cannot subscribe to events");
        }
    }

    /// <summary>
    /// 从mod加载规则到UI，并建立UI规则与Mod规则的关联
    /// </summary>
    private void RefreshRulesFromMod()
    {
        if (_mod == null)
        {
            return;
        }

        try
        {
            _mod?.LogInfo($"[CustomizedReplyPanel] ◄ RefreshRulesFromMod invoked");
            
            // 获取mod中已加载的规则
            var actualLoadedRules = _mod.GetActualLoadedRules();
            _mod?.LogInfo($"[CustomizedReplyPanel] ◄ Retrieved {actualLoadedRules?.Count ?? 0} rules from mod");
            
            if (actualLoadedRules == null || actualLoadedRules.Count == 0)
            {
                _mod?.LogInfo($"[CustomizedReplyPanel] ⚠ No rules found in mod, clearing UI");
                _viewModel.Rules.Clear();
                _mod?.LogInfo($"[CustomizedReplyPanel] ✓ UI rules cleared, final count: {_viewModel.Rules.Count}");
                return;
            }
            
            // 清空现有规则
            _mod?.LogInfo($"[CustomizedReplyPanel] ◄ Clearing {_viewModel.Rules.Count} existing UI rules");
            _viewModel.Rules.Clear();
            _mod?.LogInfo($"[CustomizedReplyPanel] ✓ UI rules cleared");
            
            // 转换为UI格式并添加到集合，同时建立与Mod规则的关联
            int ruleIndex = 0;
            foreach (var modRule in actualLoadedRules)
            {
                ruleIndex++;
                _mod?.LogInfo($"[CustomizedReplyPanel] ◄ Processing rule #{ruleIndex}: trigger='{modRule.Trigger}', matchType={modRule.MatchType}, replies={modRule.Replies?.Count ?? 0}");
                
                var ruleItem = new ReplyRuleItem
                {
                    Trigger = modRule.Trigger,
                    MatchType = modRule.MatchType.ToString(),
                    Replies = new List<string>(modRule.Replies),
                    ScriptInstanceUid = modRule.ScriptInstanceUid,  // 从модRule加载脚本实例UID
                    ScriptFilePath = modRule.ScriptFilePath,  // 从модRule加载脚本文件路径
                    ScriptContent = "",  // 新架构：脚本内容从文件加载，不再嵌入在规则中
                    // 加载保存的脚本编辑模式状态，如果没有保存，则根据 ScriptInstanceUid 推断
                    IsScriptEditMode = modRule.IsScriptEditMode || !string.IsNullOrEmpty(modRule.ScriptInstanceUid),
                    // 建立关联关系
                    AssociatedModRule = (dynamic)modRule
                };
                
                // 恢复匹配条件卡片
                ruleItem.MatchConditions.Clear();
                
                // 总是首先添加 MatchType 卡片（不可删除）
                var matchTypeDisplayValue = modRule.MatchType.ToString() switch
                {
                    "Regex" => "正则表达式",
                    "Fuzzy" => "模糊匹配",
                    _ => "精确匹配"  // Exact 和其他默认为精确匹配
                };
                
                ruleItem.MatchConditions.Add(new MatchConditionCard
                {
                    ConditionType = MatchConditionType.MatchType,
                    Value = matchTypeDisplayValue,  // 中文值供 ComboBox 绑定
                    Owner = ruleItem
                });
                _mod?.LogInfo($"[CustomizedReplyPanel] Added MatchType card: {modRule.MatchType.ToString()} ({matchTypeDisplayValue})");
                
                // 然后加载其他条件
                if (modRule.Conditions != null && modRule.Conditions.Count > 0)
                {
                    _mod?.LogInfo($"[CustomizedReplyPanel] Loading {modRule.Conditions.Count} additional conditions for rule '{modRule.Trigger}'");
                    foreach (var condition in modRule.Conditions)
                    {
                        try
                        {
                            // 解析条件类型 - 支持枚举值和字符串
                            MatchConditionType condType;
                            if (int.TryParse(condition.ConditionType, out int typeValue))
                            {
                                condType = (MatchConditionType)typeValue;
                            }
                            else
                            {
                                // 尝试按字符串解析
                                if (Enum.TryParse<MatchConditionType>(condition.ConditionType, out var parsedType))
                                {
                                    condType = parsedType;
                                }
                                else
                                {
                                    // 默认为 QQRestriction（其他条件的默认值）
                                    condType = MatchConditionType.QQRestriction;
                                    _mod?.LogInfo($"[CustomizedReplyPanel] WARNING: Unknown condition type '{condition.ConditionType}', using QQRestriction");
                                }
                            }

                            var conditionCard = new MatchConditionCard
                            {
                                ConditionType = condType,
                                Value = condition.Value ?? "",
                                Value2 = condition.Value2 ?? "",
                                IsInverted = condition.IsInverted,
                                Owner = ruleItem
                            };
                            
                            // 为加载的条件卡片添加属性改变监听器，以支持实时编辑
                            conditionCard.PropertyChanged += (s, e) => 
                            {
                                _mod?.LogInfo($"[CustomizedReplyPanel] Loaded condition card property changed: {e.PropertyName}");
                                SyncConditionsToMod(ruleItem);
                            };
                            
                            ruleItem.MatchConditions.Add(conditionCard);
                            _mod?.LogInfo($"[CustomizedReplyPanel] Loaded condition: Type={condType}, Value='{condition.Value}', Value2='{condition.Value2}', Inverted={condition.IsInverted}");
                        }
                        catch (Exception ex)
                        {
                            _mod?.LogInfo($"[CustomizedReplyPanel] ERROR parsing condition: {ex.Message}");
                        }
                    }
                }
                else
                {
                    _mod?.LogInfo($"[CustomizedReplyPanel] No additional conditions found for rule '{modRule.Trigger}'");
                }
                
                // 为UI规则的属性改变添加监听器
                ruleItem.PropertyChanged += (s, e) => OnUIRulePropertyChanged(ruleItem, e);
                
                _viewModel.Rules.Add(ruleItem);
                _mod?.LogInfo($"[CustomizedReplyPanel] ✓ Added rule #{ruleIndex} to UI, total UI rules: {_viewModel.Rules.Count}");
                
                // 初始化脚本函数列表：如果规则有脚本文件路径，则从脚本资源中加载并解析函数
                if (!string.IsNullOrEmpty(ruleItem.ScriptFilePath))
                {
                    var scriptResource = _viewModel.ScriptResources?.FirstOrDefault(sr => sr.FileName == ruleItem.ScriptFilePath);
                    if (scriptResource != null)
                    {
                        _mod?.LogInfo($"[CustomizedReplyPanel] ◄ Initializing script functions for rule #{ruleIndex}: '{ruleItem.ScriptFilePath}'");
                        ParseScriptFunctions(scriptResource.FileName, scriptResource.Content, ruleItem);
                        _mod?.LogInfo($"[CustomizedReplyPanel] ✓ Parsed {ruleItem.ScriptCalls.Count} functions for rule #{ruleIndex}");
                    }
                    else
                    {
                        _mod?.LogInfo($"[CustomizedReplyPanel] ⚠ Script file not found: '{ruleItem.ScriptFilePath}' for rule #{ruleIndex}");
                        ruleItem.ScriptCalls.Clear();
                    }
                }
            }
            
            _mod?.LogInfo($"[CustomizedReplyPanel] ✓✓ RefreshRulesFromMod complete: Loaded {_viewModel.Rules.Count} rules from mod, all displayed in UI");
        }
        catch (Exception ex)
        {
            _mod?.LogInfo($"[CustomizedReplyPanel] ✗ ERROR in RefreshRulesFromMod: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 从磁盘加载脚本资源到ViewModel
    /// </summary>
    private void RefreshScriptsFromDisk()
    {
        try
        {
            _mod?.LogInfo($"[CustomizedReplyPanel] ◄ RefreshScriptsFromDisk invoked");
            
            // ✅ 使用 Mod 的 GetCurrentScriptsDirectory() 方法，支持根据同步模式智能切换目录
            // 正常模式：data/mods/CustomizedReply/scripts
            // 同步模式：data/mods/CustomizedReply/sync-scripts
            var scriptsDir = _mod?.GetCurrentScriptsDirectory() ?? Path.Combine(AppContext.BaseDirectory, "data", "mods", "CustomizedReply", "scripts");
            
            // 创建目录如果不存在
            if (!Directory.Exists(scriptsDir))
            {
                Directory.CreateDirectory(scriptsDir);
                _mod?.LogInfo($"[CustomizedReplyPanel] Created scripts directory: {scriptsDir}");
            }
            
            _mod?.LogInfo($"[CustomizedReplyPanel] Using scripts directory: {scriptsDir}");
            
            // 从ScriptExecutor加载脚本资源
            var scriptResources = ScriptExecutor.LoadScriptResourcesFromDirectory(scriptsDir);
            _mod?.LogInfo($"[CustomizedReplyPanel] Loaded {scriptResources.Count} script resources from {scriptsDir}");
            
            // 更新ViewModel中的脚本资源集合
            _viewModel.ScriptResources = new ObservableCollection<ScriptResource>(scriptResources);
            
            // 更新可用的脚本文件列表（用于ComboBox）
            var scriptFileNames = scriptResources.Select(sr => sr.FileName).OrderBy(name => name).ToList();
            _viewModel.AvailableScriptFiles = new ObservableCollection<string>(scriptFileNames);
            _mod?.LogInfo($"[CustomizedReplyPanel] Updated {scriptFileNames.Count} available script files for ComboBox");
            
            // 从已加载的规则中提取所有脚本实例UID并填充到ScriptInstances
            var scriptInstanceUids = new HashSet<string>();
            foreach (var rule in _viewModel.Rules)
            {
                if (!string.IsNullOrEmpty(rule.ScriptInstanceUid))
                {
                    scriptInstanceUids.Add(rule.ScriptInstanceUid);
                }
            }
            
            _viewModel.ScriptInstances = new ObservableCollection<string>(scriptInstanceUids);
            _mod?.LogInfo($"[CustomizedReplyPanel] Populated {_viewModel.ScriptInstances.Count} script instance UIDs from rules");
        }
        catch (Exception ex)
        {
            _mod?.LogInfo($"[CustomizedReplyPanel] ✗ ERROR in RefreshScriptsFromDisk: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 更新脚本实例UID列表（在规则中的UID改变时调用）
    /// 确保ScriptInstances下拉框总是显示所有已被规则引用的UID
    /// </summary>
    private void UpdateScriptInstances()
    {
        var scriptInstanceUids = new HashSet<string>();
        
        // 从所有规则中收集脚本实例UID
        foreach (var rule in _viewModel.Rules)
        {
            if (!string.IsNullOrEmpty(rule.ScriptInstanceUid))
            {
                scriptInstanceUids.Add(rule.ScriptInstanceUid);
            }
        }
        
        // 只有当UID列表实际改变时才更新集合
        var currentUids = new HashSet<string>(_viewModel.ScriptInstances ?? new ObservableCollection<string>());
        
        if (!scriptInstanceUids.SetEquals(currentUids))
        {
            _viewModel.ScriptInstances = new ObservableCollection<string>(scriptInstanceUids);
        }
    }
    
    /// <summary>
    /// 刷新规则按钮点击事件
    /// </summary>
    private void OnRefreshClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 从 mod 重新加载规则和脚本
        RefreshScriptsFromDisk();  // 先加载脚本资源
        RefreshRulesFromMod();     // 再加载规则，以便初始化脚本函数
        UpdateScriptInstances();  // 确保脚本实例UID列表保持同步
    }

    /// <summary>
    /// 添加新规则按钮点击事件
    /// 直接添加到Mod的内部规则列表，保证即时生效
    /// </summary>
    private void OnAddRuleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_mod == null) return;

        try
        {
            // 创建UI规则项
            var uiRule = new ReplyRuleItem
            {
                Trigger = "新规则",
                MatchType = "精确匹配",
                Replies = new List<string> { "默认回复" }
            };
            _viewModel.Rules.Add(uiRule);
            _viewModel.SelectedRule = uiRule;

            // 关键：同时创建Mod内部规则并添加到_replyRules
            // 这样UI和Mod的数据始终保持同步
            var modRule = CreateModRuleFromUIRule(uiRule);
            if (modRule != null)
            {
                // 建立关联关系：保存对应的Mod规则引用
                uiRule.AssociatedModRule = modRule;
                
                // 为UI规则的属性改变添加监听器，以便同步到Mod规则
                uiRule.PropertyChanged += (s, e) => OnUIRulePropertyChanged(uiRule, e);
                
                _mod.AddRuleDirectly(modRule);
                var totalModRules = _mod.GetActualLoadedRules().Count;
                var totalUIRules = _viewModel.Rules.Count;
                _mod?.LogInfo($"[CustomizedReplyPanel] Rule added. UI rules: {totalUIRules}, Mod rules: {totalModRules}");
                
                // 自动保存到文件以确保规则不会丢失
                // ✅ 关键修复：直接保存Mod中的规则，不再使用SaveUIRulesToMod()（会清空并重建，可能丢失新规则）
                try
                {
                    // 规则已通过 AddRuleDirectly() 添加到 Mod，现在直接保存到文件
                    _mod.SaveRulesImmediately();
                    System.Diagnostics.Debug.WriteLine($"[CustomizedReplyPanel] ✓ Auto-saved new rule: '{uiRule.Trigger}'");
                    _mod?.LogInfo($"[CustomizedReplyPanel] ✓ Auto-saved new rule: '{uiRule.Trigger}' to file");
                }
                catch (Exception saveEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[CustomizedReplyPanel] ✗ Auto-save failed: {saveEx.Message}");
                    _mod?.LogInfo($"[CustomizedReplyPanel] ✗ Auto-save failed when adding rule: {saveEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CustomizedReplyPanel] Error adding rule: {ex.Message}");
        }
    }

    /// <summary>
    /// 保存规则按钮点击事件（将当前内存规则保存到文件）
    /// </summary>
    private void OnOverwriteRuleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedRule != null && _mod != null)
        {
            try
            {
                // 验证所有字段是否正确更新
                var repliesStr = string.Join(", ", _viewModel.SelectedRule.Replies);
                System.Diagnostics.Debug.WriteLine($"[CustomizedReply] Saving rule - Trigger: {_viewModel.SelectedRule.Trigger}, MatchType: {_viewModel.SelectedRule.MatchType}, Replies: [{repliesStr}]");
                System.Diagnostics.Debug.WriteLine($"[CustomizedReply] Saving {_mod.GetLoadedRules().Count} rules from Mod to file...");
                
                // 先同步UI中所有规则的当前状态到Mod规则
                foreach (var uiRule in _viewModel.Rules)
                {
                    if (uiRule.AssociatedModRule != null)
                    {
                        // 同步关键字段
                        var modRuleType = uiRule.AssociatedModRule.GetType();
                        var triggerProp = modRuleType.GetProperty("Trigger");
                        var repliesProp = modRuleType.GetProperty("Replies");
                        var matchTypeProp = modRuleType.GetProperty("MatchType");
                        
                        triggerProp?.SetValue(uiRule.AssociatedModRule, uiRule.Trigger);
                        repliesProp?.SetValue(uiRule.AssociatedModRule, new List<string>(uiRule.Replies));
                        
                        // 同步匹配类型
                        if (matchTypeProp != null && !string.IsNullOrEmpty(uiRule.MatchType))
                        {
                            int matchTypeValue = ConvertChineseToMatchTypeValue(uiRule.MatchType);
                            var matchTypeEnum = _mod.GetType().Assembly.GetType("CustomizedReply.CustomizedReplyMod+MatchType")
                                ?? _mod.GetType().Assembly.GetType("MatchType");
                            if (matchTypeEnum != null)
                            {
                                var enumValue = System.Enum.GetValues(matchTypeEnum).Cast<object>().ElementAtOrDefault(matchTypeValue);
                                if (enumValue != null)
                                {
                                    matchTypeProp.SetValue(uiRule.AssociatedModRule, enumValue);
                                }
                            }
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[CustomizedReply] Synced UI rule to Mod: '{uiRule.Trigger}'");
                    }
                }
                
                System.Diagnostics.Debug.WriteLine("[CustomizedReply] ✓ Synced all UI rules to Mod");
                
                // 保存Mod中的所有规则到文件（不重新构建，直接保存当前的规则列表）
                _mod.SaveRulesImmediately();
                
                System.Diagnostics.Debug.WriteLine("[CustomizedReply] ✓ Rules saved successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CustomizedReply] ✗ Error saving rules: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[CustomizedReply] Stack trace: {ex.StackTrace}");
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[CustomizedReply] ⚠ Cannot save: SelectedRule={_viewModel.SelectedRule}, Mod={_mod}");
        }
    }

    /// <summary>
    /// 从UI规则项创建Mod内部的ReplyRule对象
    /// </summary>
    private dynamic? CreateModRuleFromUIRule(ReplyRuleItem uiRule)
    {
        try
        {
            // 使用反射创建ReplyRule实例
            // ReplyRule 是顶级类，不是嵌套类，所以类型名是 "CustomizedReply.ReplyRule"
            var replyRuleType = _mod.GetType().Assembly.GetType("CustomizedReply.ReplyRule");
            if (replyRuleType == null)
            {
                _mod?.LogInfo("[CustomizedReplyPanel] ERROR: Cannot find ReplyRule type. Trying alternative namespace...");
                // 尝试另一种命名空间格式
                replyRuleType = _mod.GetType().Assembly.GetType("ReplyRule");
                if (replyRuleType == null)
                {
                    _mod?.LogInfo("[CustomizedReplyPanel] ERROR: ReplyRule type not found in any namespace");
                    return null;
                }
                _mod?.LogInfo($"[CustomizedReplyPanel] ✓ Found ReplyRule in alternate namespace: {replyRuleType.FullName}");
            }

            var newRule = System.Activator.CreateInstance(replyRuleType);
            if (newRule == null)
            {
                _mod?.LogInfo("[CustomizedReplyPanel] ERROR: Failed to create ReplyRule instance");
                return null;
            }
            _mod?.LogInfo($"[CustomizedReplyPanel] ✓ Created ReplyRule instance successfully");

            // 设置属性
            var triggerProp = replyRuleType.GetProperty("Trigger");
            var matchTypeProp = replyRuleType.GetProperty("MatchType");
            var repliesProp = replyRuleType.GetProperty("Replies");
            var scriptInstanceUidProp = replyRuleType.GetProperty("ScriptInstanceUid");
            var scriptCallsProp = replyRuleType.GetProperty("ScriptCalls");
            var conditionsProp = replyRuleType.GetProperty("Conditions");

            triggerProp?.SetValue(newRule, uiRule.Trigger);
            
            // ✅ 设置回复列表（修复：新规则的回复为空的问题）
            repliesProp?.SetValue(newRule, new List<string>(uiRule.Replies));
            
            // ✅ 使用共享的转换方法
            var matchTypeValue = ConvertChineseToMatchTypeValue(uiRule.MatchType);
            
            // ✅ 设置匹配类型（修复：新规则的MatchType没有被设置）
            var matchTypeEnum = _mod.GetType().Assembly.GetType("CustomizedReply.MatchType") 
                ?? _mod.GetType().Assembly.GetType("MatchType");
            if (matchTypeEnum != null && matchTypeProp != null)
            {
                var enumValue = System.Enum.GetValues(matchTypeEnum).Cast<object>().ElementAtOrDefault(matchTypeValue);
                if (enumValue != null)
                {
                    matchTypeProp.SetValue(newRule, enumValue);
                    _mod?.LogInfo($"[CustomizedReplyPanel] ✓ Set MatchType to {enumValue}");
                }
            }
            
            // TODO: 根据 IsScriptEditMode 和 ScriptContent 确定 ScriptInstanceUid
            // 新架构中，脚本内容存储在独立的文件中，规则仅保存引用 UID
            if (uiRule.IsScriptEditMode && !string.IsNullOrEmpty(uiRule.ScriptContent))
            {
                // 这里应该调用脚本存储逻辑，创建或更新脚本资源，返回 UID
                // 暂时设置为空，等待后续实现
                scriptInstanceUidProp?.SetValue(newRule, null);
                _mod?.LogInfo($"[CustomizedReplyPanel] ⚠ Script content set but resource management not yet implemented");
            }
            else
            {
                scriptInstanceUidProp?.SetValue(newRule, null);
            }
            
            // 设置匹配条件
            if (uiRule.MatchConditions.Count > 1)  // Count > 1 because first card is always MatchType
            {
                var matchConditionType = _mod.GetType().Assembly.GetType("CustomizedReply.MatchCondition") 
                    ?? _mod.GetType().Assembly.GetType("MatchCondition");
                    
                if (matchConditionType != null)
                {
                    // 使用反射创建 List<MatchCondition> 类型
                    var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(matchConditionType);
                    var conditionsList = System.Activator.CreateInstance(listType) as System.Collections.IList;
                    
                    if (conditionsList != null)
                    {
                        // Skip the first card (MatchType) and only process other conditions
                        var otherConditions = uiRule.MatchConditions.Skip(1);
                        
                        foreach (var card in otherConditions)
                        {
                            try
                            {
                                var condition = System.Activator.CreateInstance(matchConditionType);
                                if (condition != null)
                                {
                                    var condTypeProp = matchConditionType.GetProperty("ConditionType");
                                    var condValueProp = matchConditionType.GetProperty("Value");
                                    var condValue2Prop = matchConditionType.GetProperty("Value2");
                                    var condInvertedProp = matchConditionType.GetProperty("IsInverted");
                                    
                                    condTypeProp?.SetValue(condition, card.ConditionType.ToString());
                                    condValueProp?.SetValue(condition, card.Value);
                                    condValue2Prop?.SetValue(condition, card.Value2);
                                    condInvertedProp?.SetValue(condition, card.IsInverted);
                                    
                                    conditionsList.Add(condition);
                                }
                            }
                            catch (Exception ex)
                            {
                                _mod?.LogInfo($"[CustomizedReplyPanel] Error creating condition: {ex.Message}");
                            }
                        }
                        
                        if (conditionsList.Count > 0 && conditionsProp != null)
                        {
                            conditionsProp.SetValue(newRule, conditionsList);
                            _mod?.LogInfo($"[CustomizedReplyPanel] ✓ Set {conditionsList.Count} conditions to rule object (skipped MatchType card)");
                        }
                    }
                }
            }

            return (dynamic)newRule;
        }
        catch (Exception ex)
        {
            _mod?.LogInfo($"[CustomizedReplyPanel] ERROR creating mod rule: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 将UI中的规则同步回Mod的内部规则列表
    /// </summary>
    private void SaveUIRulesToMod()
    {
        if (_mod == null) return;

        try
        {
            // 获取mod的内部规则列表（通过反射访问）
            var rulesField = _mod.GetType().GetField("_replyRules", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (rulesField == null)
            {
                System.Diagnostics.Debug.WriteLine("[CustomizedReply] Cannot find _replyRules field");
                return;
            }

            var internalRules = (System.Collections.IList?)rulesField.GetValue(_mod);
            if (internalRules == null)
            {
                System.Diagnostics.Debug.WriteLine("[CustomizedReply] _replyRules list is null");
                return;
            }

            // 清空现有规则
            internalRules.Clear();

            // 获取ReplyRule类型
            var replyRuleType = _mod.GetType().Assembly.GetType("CustomizedReply.CustomizedReplyMod+ReplyRule");
            if (replyRuleType == null)
            {
                System.Diagnostics.Debug.WriteLine("[CustomizedReply] Cannot find ReplyRule type");
                return;
            }

            // 获取MatchType枚举
            var matchTypeEnum = _mod.GetType().Assembly.GetType("CustomizedReply.CustomizedReplyMod+MatchType");
            if (matchTypeEnum == null)
            {
                System.Diagnostics.Debug.WriteLine("[CustomizedReply] Cannot find MatchType enum");
                return;
            }

            // 从UI规则转换为mod规则
            foreach (var uiRule in _viewModel.Rules)
            {
                // ✅ 使用共享的转换方法
                int matchTypeValue = ConvertChineseToMatchTypeValue(uiRule.MatchType);

                // 创建新规则实例
                var newRule = System.Activator.CreateInstance(replyRuleType);
                if (newRule == null)
                {
                    System.Diagnostics.Debug.WriteLine("[CustomizedReply] Failed to create ReplyRule instance");
                    continue;
                }

                // 设置属性
                var triggerProp = replyRuleType.GetProperty("Trigger");
                var matchTypeProp = replyRuleType.GetProperty("MatchType");
                var repliesProp = replyRuleType.GetProperty("Replies");
                var hasScriptProp = replyRuleType.GetProperty("HasScript");
                var scriptContentProp = replyRuleType.GetProperty("ScriptContent");
                var scriptMetadataProp = replyRuleType.GetProperty("ScriptMetadata");

                triggerProp?.SetValue(newRule, uiRule.Trigger);
                matchTypeProp?.SetValue(newRule, Convert.ChangeType(matchTypeValue, matchTypeEnum));
                repliesProp?.SetValue(newRule, new List<string>(uiRule.Replies));

                // 设置脚本相关属性
                if (uiRule.IsScriptEditMode && !string.IsNullOrEmpty(uiRule.ScriptContent))
                {
                    hasScriptProp?.SetValue(newRule, true);
                    scriptContentProp?.SetValue(newRule, uiRule.ScriptContent);

                    // 创建ScriptMetadata实例（如果有脚本）
                    if (scriptMetadataProp != null)
                    {
                        var scriptMetadataType = _mod.GetType().Assembly.GetType("CustomizedReply.CustomizedReplyMod+ScriptMetadata");
                        if (scriptMetadataType != null)
                        {
                            var metadata = System.Activator.CreateInstance(scriptMetadataType);
                            if (metadata != null)
                            {
                                var scriptNameProp = scriptMetadataType.GetProperty("ScriptName");
                                var createdAtProp = scriptMetadataType.GetProperty("CreatedAt");
                                scriptNameProp?.SetValue(metadata, $"script_{DateTime.Now:yyyyMMdd_HHmmss}");
                                createdAtProp?.SetValue(metadata, DateTime.Now);
                                scriptMetadataProp.SetValue(newRule, metadata);
                            }
                        }
                    }
                }
                else
                {
                    hasScriptProp?.SetValue(newRule, false);
                    scriptContentProp?.SetValue(newRule, null);
                }

                // 保存匹配条件卡片
                var conditionsProp = replyRuleType.GetProperty("Conditions");
                if (conditionsProp != null && uiRule.MatchConditions.Count > 0)
                {
                    var matchConditionType = _mod.GetType().Assembly.GetType("CustomizedReply.CustomizedReplyMod+MatchCondition");
                    if (matchConditionType != null)
                    {
                        var conditionsList = new System.Collections.Generic.List<object>();
                        
                        foreach (var card in uiRule.MatchConditions)
                        {
                            // 跳过匹配类型卡片（不作为独立条件）
                            if (card.ConditionType == MatchConditionType.MatchType)
                                continue;

                            var conditionObj = System.Activator.CreateInstance(matchConditionType);
                            if (conditionObj != null)
                            {
                                var typeProp = matchConditionType.GetProperty("ConditionType");
                                var valueProp = matchConditionType.GetProperty("Value");
                                var value2Prop = matchConditionType.GetProperty("Value2");
                                var invertedProp = matchConditionType.GetProperty("IsInverted");

                                // 转换条件类型为字符串
                                string conditionTypeStr = card.ConditionType switch
                                {
                                    MatchConditionType.QQRestriction => "QQRestriction",
                                    MatchConditionType.GroupRestriction => "GroupRestriction",
                                    MatchConditionType.LevelRestriction => "LevelRestriction",
                                    _ => card.ConditionType.ToString()
                                };

                                typeProp?.SetValue(conditionObj, conditionTypeStr);
                                valueProp?.SetValue(conditionObj, card.Value);
                                value2Prop?.SetValue(conditionObj, card.Value2);
                                invertedProp?.SetValue(conditionObj, card.IsInverted);

                                conditionsList.Add(conditionObj);
                            }
                        }

                        conditionsProp.SetValue(newRule, conditionsList);
                    }
                }

                internalRules.Add(newRule);
            }

            System.Diagnostics.Debug.WriteLine($"[CustomizedReply] Synced {internalRules.Count} rules to mod");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CustomizedReply] Error syncing rules to mod: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 当UI规则的属性改变时调用，同步改变到关联的Mod规则
    /// </summary>
    private void OnUIRulePropertyChanged(ReplyRuleItem uiRule, PropertyChangedEventArgs e)
    {
        if (_mod == null || uiRule.AssociatedModRule == null) return;

        try
        {
            var modRule = uiRule.AssociatedModRule;
            var modRuleType = modRule.GetType();
            bool needsSave = false;

            switch (e.PropertyName)
            {
                case nameof(ReplyRuleItem.Trigger):
                    // 同步触发词
                    var triggerProp = modRuleType.GetProperty("Trigger");
                    triggerProp?.SetValue(modRule, uiRule.Trigger);
                    _mod?.LogInfo($"[CustomizedReplyPanel] Synced Trigger: '{uiRule.Trigger}'");
                    needsSave = true;
                    break;

                case nameof(ReplyRuleItem.MatchType):
                    // 同步匹配类型
                    var matchTypeProp = modRuleType.GetProperty("MatchType");
                    if (matchTypeProp != null)
                    {
                        _mod?.LogInfo($"[CustomizedReplyPanel] MatchType changed: OLD={matchTypeProp.GetValue(modRule)}, NEW UI value='{uiRule.MatchType}'");
                        
                        // ✅ 使用共享的转换方法
                        int matchTypeValue = ConvertChineseToMatchTypeValue(uiRule.MatchType);
                        _mod?.LogInfo($"[CustomizedReplyPanel] MatchType switch result: '{uiRule.MatchType}' -> {matchTypeValue}");
                        
                        // MatchType 是顶级枚举
                        var matchTypeEnum = _mod.GetType().Assembly.GetType("CustomizedReply.MatchType") 
                            ?? _mod.GetType().Assembly.GetType("MatchType");
                        if (matchTypeEnum != null)
                        {
                            var enumValue = System.Enum.GetValues(matchTypeEnum).Cast<object>().ElementAtOrDefault(matchTypeValue);
                            if (enumValue != null)
                            {
                                matchTypeProp.SetValue(modRule, enumValue);
                                _mod?.LogInfo($"[CustomizedReplyPanel] ✓ MatchType synced: {enumValue} (verified: {matchTypeProp.GetValue(modRule)})");
                            }
                            else
                            {
                                _mod?.LogInfo($"[CustomizedReplyPanel] ✗ ERROR: Enum value not found at index {matchTypeValue}");
                            }
                        }
                        else
                        {
                            _mod?.LogInfo($"[CustomizedReplyPanel] ✗ ERROR: MatchType enum not found");
                        }
                    }
                    needsSave = true;
                    break;

                case nameof(ReplyRuleItem.Replies):
                    // 同步回复列表
                    var repliesProp = modRuleType.GetProperty("Replies");
                    repliesProp?.SetValue(modRule, new List<string>(uiRule.Replies));
                    _mod?.LogInfo($"[CustomizedReplyPanel] Synced Replies: {uiRule.Replies.Count} items");
                    needsSave = true;
                    break;

                case nameof(ReplyRuleItem.IsScriptEditMode):
                    // 同步脚本编辑模式
                    var scriptModeProp = modRuleType.GetProperty("IsScriptEditMode");
                    scriptModeProp?.SetValue(modRule, uiRule.IsScriptEditMode);
                    _mod?.LogInfo($"[CustomizedReplyPanel] Synced IsScriptEditMode: {uiRule.IsScriptEditMode}");
                    needsSave = true;
                    break;

                case nameof(ReplyRuleItem.ScriptContent):
                    // 同步脚本内容
                    var scriptContentProp = modRuleType.GetProperty("ScriptContent");
                    scriptContentProp?.SetValue(modRule, uiRule.ScriptContent);
                    _mod?.LogInfo($"[CustomizedReplyPanel] Synced ScriptContent: {uiRule.ScriptContent.Length} chars");
                    // 脚本内容改变时，重新解析脚本函数
                    if (!string.IsNullOrEmpty(uiRule.ScriptFilePath))
                    {
                        ParseScriptFunctions(uiRule.ScriptFilePath, uiRule.ScriptContent, uiRule);
                    }
                    needsSave = true;
                    break;

                case nameof(ReplyRuleItem.ScriptFilePath):
                    // 同步脚本文件路径
                    var scriptFileProp = modRuleType.GetProperty("ScriptFilePath");
                    scriptFileProp?.SetValue(modRule, uiRule.ScriptFilePath);
                    _mod?.LogInfo($"[CustomizedReplyPanel] Synced ScriptFilePath: '{uiRule.ScriptFilePath}'");
                    // 脚本文件路径改变时，查找脚本资源并解析函数
                    if (!string.IsNullOrEmpty(uiRule.ScriptFilePath))
                    {
                        var scriptResource = _viewModel.ScriptResources?.FirstOrDefault(sr => sr.FileName == uiRule.ScriptFilePath);
                        if (scriptResource != null)
                        {
                            ParseScriptFunctions(scriptResource.FileName, scriptResource.Content, uiRule);
                        }
                        else
                        {
                            uiRule.ScriptCalls.Clear();
                        }
                    }
                    else
                    {
                        uiRule.ScriptCalls.Clear();
                    }
                    // 脚本文件路径改变时立即保存
                    _mod?.SaveRulesImmediately();
                    break;

                case nameof(ReplyRuleItem.ScriptInstanceUid):
                    // 同步脚本实例UID
                    var scriptInstanceUidProp = modRuleType.GetProperty("ScriptInstanceUid");
                    scriptInstanceUidProp?.SetValue(modRule, uiRule.ScriptInstanceUid);
                    _mod?.LogInfo($"[CustomizedReplyPanel] Synced ScriptInstanceUid: '{uiRule.ScriptInstanceUid}'");
                    // 脚本实例UID改变时立即保存
                    _mod?.SaveRulesImmediately();
                    break;

                case nameof(ReplyRuleItem.MatchConditions):
                    // 使用 SyncConditionsToMod 方法同步匹配条件卡片到内存
                    // ✅ 只同步到内存中的 Mod 对象，不设置 needsSave=true，因此不会保存到文件
                    SyncConditionsToMod(uiRule);
                    _mod?.LogInfo($"[CustomizedReplyPanel] ✓ 条件即时同步到内存: '{uiRule.Trigger}'");
                    break;
            }

            // 验证更新后的Mod规则状态
            _mod?.LogInfo($"[CustomizedReplyPanel] Before UpdateRuleDirectly - Rule '{uiRule.Trigger}' final state: MatchType={modRuleType.GetProperty("MatchType")?.GetValue(modRule)}");
            
            // 调用UpdateRuleDirectly进行记录
            _mod.UpdateRuleDirectly(modRule);
            
            // 自动保存到文件（如果需要）
            if (needsSave)
            {
                try
                {
                    _mod.SaveRulesImmediately();
                    System.Diagnostics.Debug.WriteLine($"[CustomizedReplyPanel] ✓ Auto-saved after editing property: {e.PropertyName}");
                    _mod?.LogInfo($"[CustomizedReplyPanel] ✓ Auto-saved after editing property: {e.PropertyName}");
                }
                catch (Exception saveEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[CustomizedReplyPanel] ✗ Auto-save failed: {saveEx.Message}");
                    _mod?.LogInfo($"[CustomizedReplyPanel] ✗ Auto-save failed: {saveEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _mod?.LogInfo($"[CustomizedReplyPanel] Error syncing UI rule to Mod: {ex.Message}");
        }
    }

    /// <summary>
    /// 删除规则按钮点击事件
    /// 直接从Mod的内部规则列表中删除
    /// </summary>
    private void OnDeleteRuleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedRule != null && _mod != null)
        {
            try
            {
                var selectedRule = _viewModel.SelectedRule;
                
                // 从ViewModel中删除
                _viewModel.Rules.Remove(selectedRule);
                _viewModel.SelectedRule = null;

                // 关键：使用关联的Mod规则对象直接删除
                // 避免使用Trigger匹配（如果用户改变了Trigger会导致匹配失败）
                if (selectedRule.AssociatedModRule != null)
                {
                    _mod.RemoveRuleDirectly(selectedRule.AssociatedModRule);
                    System.Diagnostics.Debug.WriteLine($"[CustomizedReplyPanel] Rule deleted from both UI and Mod using object reference: '{selectedRule.Trigger}'");
                }
                else
                {
                    // 备用方案：如果没有关联引用（比如是从旧数据加载的），使用Trigger匹配
                    System.Diagnostics.Debug.WriteLine($"[CustomizedReplyPanel] Warning: No associated Mod rule found for '{selectedRule.Trigger}'");
                }
                
                // 自动保存到文件以确保删除持久化
                // ✅ 关键修复：直接保存Mod中的规则，不再使用SaveUIRulesToMod()
                try
                {
                    // 规则已通过 RemoveRuleDirectly() 从 Mod 中删除，现在直接保存到文件
                    _mod.SaveRulesImmediately();
                    System.Diagnostics.Debug.WriteLine($"[CustomizedReplyPanel] ✓ Auto-saved after deleting rule: '{selectedRule.Trigger}'");
                    _mod?.LogInfo($"[CustomizedReplyPanel] ✓ Auto-saved after deleting rule: '{selectedRule.Trigger}'");
                }
                catch (Exception saveEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[CustomizedReplyPanel] ✗ Auto-save failed after delete: {saveEx.Message}");
                    _mod?.LogInfo($"[CustomizedReplyPanel] ✗ Auto-save failed after deleting rule: {saveEx.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CustomizedReplyPanel] Error deleting rule: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 打开规则文件按钮点击事件
    /// </summary>
    private void OnOpenRuleFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 打开 data.json 文件
    }

    /// <summary>
    /// 同步条件卡片和脚本信息到 Mod 对象
    /// 在条件值改变时调用，执行即时保存到内存和文件
    /// </summary>
    private void SyncConditionsToMod(ReplyRuleItem uiRule)
    {
        if (_mod == null || uiRule.AssociatedModRule == null)
        {
            return;
        }

        try
        {
            var modRule = uiRule.AssociatedModRule;
            var modRuleType = modRule.GetType();
            
            // 日志：显示哪些条件正在同步
            var conditionsSummary = string.Join(", ", 
                uiRule.MatchConditions.Skip(1).Select(c => $"{c.DisplayName}(Value:{c.Value},Value2:{c.Value2})"));
            _mod?.LogInfo($"[CustomizedReplyPanel] 即时保存条件到内存: '{uiRule.Trigger}' - [{conditionsSummary}]");
            
            // 同步脚本实例UID
            var scriptInstanceUidProp = modRuleType.GetProperty("ScriptInstanceUid");
            if (scriptInstanceUidProp != null)
            {
                scriptInstanceUidProp.SetValue(modRule, uiRule.ScriptInstanceUid);
                _mod?.LogInfo($"[CustomizedReplyPanel] Synced ScriptInstanceUid: '{uiRule.ScriptInstanceUid}' to rule '{uiRule.Trigger}'");
            }
            
            // 同步脚本文件路径
            var scriptFilePathProp = modRuleType.GetProperty("ScriptFilePath");
            if (scriptFilePathProp != null)
            {
                scriptFilePathProp.SetValue(modRule, uiRule.ScriptFilePath);
                _mod?.LogInfo($"[CustomizedReplyPanel] Synced ScriptFilePath: '{uiRule.ScriptFilePath}' to rule '{uiRule.Trigger}'");
            }
            
            // 同步脚本编辑模式标志
            var isScriptEditModeProp = modRuleType.GetProperty("IsScriptEditMode");
            if (isScriptEditModeProp != null)
            {
                isScriptEditModeProp.SetValue(modRule, uiRule.IsScriptEditMode);
                _mod?.LogInfo($"[CustomizedReplyPanel] Synced IsScriptEditMode: {uiRule.IsScriptEditMode} to rule '{uiRule.Trigger}'");
            }
            
            // 同步脚本函数调用列表
            var scriptCallsProp = modRuleType.GetProperty("ScriptCalls");
            if (scriptCallsProp != null && uiRule.ScriptCalls != null)
            {
                var callsList = new List<string>(uiRule.ScriptCalls);
                scriptCallsProp.SetValue(modRule, callsList);
                _mod?.LogInfo($"[CustomizedReplyPanel] Synced {callsList.Count} script calls to rule '{uiRule.Trigger}'");
            }
            
            // 从 UI 条件卡片列表构建 Mod 条件对象列表
            var matchConditionType = _mod.GetType().Assembly.GetType("CustomizedReply.MatchCondition")
                ?? _mod.GetType().Assembly.GetType("MatchCondition");
            
            System.Collections.IList? conditionsList = null;
            
            if (matchConditionType != null)
            {
                // 使用反射创建 List<MatchCondition> 类型
                var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(matchConditionType);
                conditionsList = System.Activator.CreateInstance(listType) as System.Collections.IList;
                
                if (conditionsList != null)
                {
                    // Skip the first card (MatchType) and only sync other conditions
                    var otherConditions = uiRule.MatchConditions.Skip(1);
                    
                    foreach (var card in otherConditions)
                    {
                        try
                        {
                            var condition = System.Activator.CreateInstance(matchConditionType);
                            if (condition != null)
                            {
                                var condTypeProp = matchConditionType.GetProperty("ConditionType");
                                var condValueProp = matchConditionType.GetProperty("Value");
                                var condValue2Prop = matchConditionType.GetProperty("Value2");
                                var condInvertedProp = matchConditionType.GetProperty("IsInverted");
                                
                                condTypeProp?.SetValue(condition, card.ConditionType.ToString());
                                condValueProp?.SetValue(condition, card.Value);
                                condValue2Prop?.SetValue(condition, card.Value2);
                                condInvertedProp?.SetValue(condition, card.IsInverted);
                                
                                conditionsList.Add(condition);
                            }
                        }
                        catch (Exception ex)
                        {
                            _mod?.LogInfo($"[CustomizedReplyPanel] Error creating condition object: {ex.Message}");
                        }
                    }
                }
            }
            
            // 设置 Mod 规则的条件列表
            if (conditionsList != null)
            {
                var conditionsProp = modRuleType.GetProperty("Conditions");
                if (conditionsProp != null)
                {
                    conditionsProp.SetValue(modRule, conditionsList);
                    _mod?.LogInfo($"[CustomizedReplyPanel] Synced {conditionsList.Count} conditions to Mod rule: '{uiRule.Trigger}' (skipped MatchType card)");
                }
            }
            
            // 更新脚本实例UID列表
            UpdateScriptInstances();
        }
        catch (Exception ex)
        {
            _mod?.LogInfo($"[CustomizedReplyPanel] ERROR syncing conditions to Mod: {ex.Message}");
        }
    }

    /// <summary>
    /// 删除匹配条件卡片事件处理
    /// </summary>
    private void OnDeleteConditionCard(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is MatchConditionCard card && _viewModel.SelectedRule != null)
        {
            if (card.CanDelete)
            {
                _viewModel.SelectedRule.MatchConditions.Remove(card);
                _mod?.LogInfo($"[CustomizedReplyPanel] Removed condition card: {card.ConditionType}");
                
                // 删除后也要同步到 Mod
                SyncConditionsToMod(_viewModel.SelectedRule);
            }
        }
    }

    /// <summary>
    /// 添加新匹配条件卡片事件处理
    /// </summary>
    private void OnAddConditionCard(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedRule != null && _mod != null)
        {
            // 打开一个选择菜单来选择要添加的条件类型
            var newCard = new MatchConditionCard
            {
                ConditionType = MatchConditionType.QQRestriction,
                Value = "",
                Owner = _viewModel.SelectedRule
            };
            
            // 为新条件卡片添加属性改变监听器
            newCard.PropertyChanged += (s, e) => 
            {
                _mod?.LogInfo($"[CustomizedReplyPanel] Condition card property changed: {e.PropertyName}");
                // 同步到 Mod 的条件列表
                SyncConditionsToMod(_viewModel.SelectedRule);
            };
            
            _viewModel.SelectedRule.MatchConditions.Add(newCard);
            _mod?.LogInfo($"[CustomizedReplyPanel] Added new condition card: {newCard.ConditionType}");
            
            // 立即同步到 Mod
            SyncConditionsToMod(_viewModel.SelectedRule);
        }
    }

    /// <summary>
    /// 切换反相模式事件处理
    /// </summary>
    private void OnToggleInverted(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is MatchConditionCard card)
        {
            card.IsInverted = !card.IsInverted;
            _mod?.LogInfo($"[CustomizedReplyPanel] Toggled condition inverted: {card.ConditionType} = {card.IsInverted}");
            
            // 同步到 Mod
            if (_viewModel.SelectedRule != null)
            {
                SyncConditionsToMod(_viewModel.SelectedRule);
            }
            
            // 直接更新Image的Source
            if (button.Content is Image image)
            {
                var imagePath = card.IsInverted
                    ? "avares://CustomizedReply/Assets/Sprite/BlackListMode.png"
                    : "avares://CustomizedReply/Assets/Sprite/WriteListMode.png";
                
                try
                {
                    var assets = Avalonia.Platform.AssetLoader.Open(new Uri(imagePath));
                    image.Source = new Bitmap(assets);
                }
                catch (Exception ex)
                {
                }
            }
        }
    }

    /// <summary>
    /// 选择Lua脚本文件按钮点击事件
    /// </summary>
    private async void OnSelectScriptFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_viewModel.SelectedRule == null)
            {
                System.Diagnostics.Debug.WriteLine("[OnSelectScriptFile] No rule selected");
                return;
            }

            // 获取主窗口以打开文件对话框
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (mainWindow == null)
            {
                System.Diagnostics.Debug.WriteLine("[OnSelectScriptFile] Cannot find main window");
                return;
            }

            var openFileDialog = new Avalonia.Controls.OpenFileDialog
            {
                Title = "选择 Lua 脚本文件",
                Filters = new List<Avalonia.Controls.FileDialogFilter>
                {
                    new Avalonia.Controls.FileDialogFilter
                    {
                        Name = "Lua 脚本",
                        Extensions = new List<string> { "lua" }
                    },
                    new Avalonia.Controls.FileDialogFilter
                    {
                        Name = "所有文件",
                        Extensions = new List<string> { "*" }
                    }
                },
                AllowMultiple = false
            };

            var result = await openFileDialog.ShowAsync(mainWindow);
            if (result != null && result.Length > 0)
            {
                string filePath = result[0];
                _viewModel.SelectedRule.ScriptFilePath = filePath;
                
                // 尝试读取文件内容
                try
                {
                    string content = await File.ReadAllTextAsync(filePath);
                    _viewModel.SelectedRule.ScriptContent = content;
                    System.Diagnostics.Debug.WriteLine($"[OnSelectScriptFile] Script loaded: {filePath}");
                }
                catch (Exception readEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[OnSelectScriptFile] Error reading file: {readEx.Message}");
                }
                
                // 刷新UI - 通知绑定属性已改变
                _viewModel.NotifyPropertyChanged(nameof(CustomizedReplyPanelViewModel.SelectedScriptFileName));
                _viewModel.NotifyPropertyChanged(nameof(CustomizedReplyPanelViewModel.ScriptFileIcon));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnSelectScriptFile] Error: {ex.Message}");
        }
    }

    /// <summary>
    /// 清除脚本实例 UID 按钮点击事件
    /// </summary>
    private void OnClearScriptInstanceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedRule != null)
        {
            _viewModel.SelectedRule.ScriptInstanceUid = null;
            _viewModel.NotifyPropertyChanged(nameof(CustomizedReplyPanelViewModel.SelectedRule));
            _mod?.LogInfo("[CustomizedReplyPanel] Cleared script instance UID");
        }
    }

    /// <summary>
    /// 处理脚本文件被选择时的逻辑
    /// 同步UI规则属性到Mod层规则对象并触发脚本初始化
    /// </summary>
    private void HandleScriptFileSelected(ReplyRuleItem uiRule, string scriptFilePath)
    {
        if (_mod == null || uiRule.AssociatedModRule == null) return;
        
        try
        {
            var modRule = uiRule.AssociatedModRule;
            var modRuleType = modRule.GetType();
            
            // 创建或更新ScriptInstanceUid
            if (string.IsNullOrEmpty(uiRule.ScriptInstanceUid))
            {
                uiRule.ScriptInstanceUid = Guid.NewGuid().ToString();
            }
            
            // 同步两个属性到Mod层规则对象
            var scriptInstanceUidProp = modRuleType.GetProperty("ScriptInstanceUid");
            scriptInstanceUidProp?.SetValue(modRule, uiRule.ScriptInstanceUid);
            
            var scriptFilePathProp = modRuleType.GetProperty("ScriptFilePath");
            scriptFilePathProp?.SetValue(modRule, scriptFilePath);
            
            _mod?.LogInfo($"[CustomizedReplyPanel] ✓ User selected script: '{scriptFilePath}', UID: {uiRule.ScriptInstanceUid}");
            
            // 立即保存规则到文件，确保脚本选择落盘
            _mod.SaveRulesImmediately();
            
            // ✅ 关键：直接调用UpdateRuleDirectly来通知Mod层规则已改变，这会触发RefreshScriptInstances()
            _mod.UpdateRuleDirectly(modRule);
            
            _mod?.LogInfo($"[CustomizedReplyPanel] ✓ UpdateRuleDirectly called, script initialization should be triggered");
        }
        catch (Exception ex)
        {
            _mod?.LogInfo($"[CustomizedReplyPanel] ✗ Error handling script file selection: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析Lua脚本中的函数定义并填充到指定规则的ScriptCalls列表
    /// 支持的格式：function functionName(...) 或 local function functionName(...)
    /// </summary>
    private void ParseScriptFunctions(string scriptFileName, string scriptContent, ReplyRuleItem targetRule)
    {
        if (targetRule == null || string.IsNullOrEmpty(scriptContent))
        {
            targetRule?.ScriptCalls.Clear();
            return;
        }

        try
        {
            var functionNames = new List<string>();
            
            // 使用正则表达式匹配Lua函数定义
            // 匹配 function name(...) 和 local function name(...) 的模式
            var functionPattern = new System.Text.RegularExpressions.Regex(
                @"(?:local\s+)?function\s+([a-zA-Z_][a-zA-Z0-9_]*)\s*\(",
                System.Text.RegularExpressions.RegexOptions.Multiline
            );

            var matches = functionPattern.Matches(scriptContent);
            
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    var functionName = match.Groups[1].Value;
                    if (!functionNames.Contains(functionName))
                    {
                        functionNames.Add(functionName);
                    }
                }
            }

            // 清空并重新填充ScriptCalls列表
            targetRule.ScriptCalls.Clear();
            foreach (var name in functionNames)
            {
                targetRule.ScriptCalls.Add(name);
            }

            _mod?.LogInfo($"[CustomizedReplyPanel] Parsed {functionNames.Count} functions from script '{scriptFileName}' for rule '{targetRule.Trigger}'");
        }
        catch (Exception ex)
        {
            _mod?.LogInfo($"[CustomizedReplyPanel] ERROR parsing script functions: {ex.Message}");
            targetRule?.ScriptCalls.Clear();
        }
    }

    /// <summary>
    /// 创建新脚本按钮点击事件
    /// </summary>
    private void OnCreateNewScript(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_mod == null) return;
        
        try
        {
            var newResource = new ScriptResource
            {
                FileName = $"script_{DateTime.Now:yyyyMMdd_HHmmss}.lua",
                Content = ScriptExecutor.GenerateScriptTemplate(),
                LastModified = DateTime.UtcNow,
                Description = "新脚本"
            };
            
            _viewModel.ScriptResources.Add(newResource);
            _viewModel.SelectedScriptResource = newResource;
            _mod.LogInfo($"[CustomizedReplyPanel] ✓ Created new script: {newResource.FileName}");
            
            // ✅【新增】保存脚本到文件（使用当前模式对应的目录）
            var scriptsDir = _mod.GetCurrentScriptsDirectory();
            bool saveSuccess = ScriptExecutor.SaveScriptResourceToFile(newResource, scriptsDir);
            
            if (saveSuccess)
            {
                _mod.LogInfo($"[CustomizedReplyPanel] ✓ New script saved to file: {newResource.FileName}");
                
                // ✅【新增】保存新脚本的名称
                string newScriptFileName = newResource.FileName;
                
                // ✅【新增】刷新脚本列表UI，更新combobox
                _mod.LogInfo($"[CustomizedReplyPanel] ► Refreshing scripts UI...");
                RefreshScriptsFromDisk();
                
                // ✅【新增】重新选中新创建的脚本
                var reselectedScript = _viewModel.ScriptResources.FirstOrDefault(s => s.FileName == newScriptFileName);
                if (reselectedScript != null)
                {
                    _viewModel.SelectedScriptResource = reselectedScript;
                    _mod.LogInfo($"[CustomizedReplyPanel] ✓ Restored selection to new script: {newScriptFileName}");
                }
                
                // ✅【新增】通知Mod脚本已修改，触发推送到远程
                _mod.LogInfo($"[CustomizedReplyPanel] ► Notifying mod of script changes for remote sync...");
                _mod.NotifyScriptsModified();
                
                _mod.LogInfo($"[CustomizedReplyPanel] ✓ New script created with UI refresh and remote sync");
            }
            else
            {
                _mod.LogInfo($"[CustomizedReplyPanel] ✗ Failed to save new script to file");
            }
        }
        catch (Exception ex)
        {
            _mod?.LogInfo($"[CustomizedReplyPanel] ✗ Error creating new script: {ex.Message}");
        }
    }

    /// <summary>
    /// 从文件导入脚本按钮点击事件
    /// </summary>
    private void OnImportScriptFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_mod == null) return;
        
        try
        {
            _mod.LogInfo("[CustomizedReplyPanel] ► Opening file dialog for script import...");
            
            // ✅ 使用当前模式对应的脚本目录（正常模式或同步模式）
            var scriptsDir = _mod.GetCurrentScriptsDirectory();
            Directory.CreateDirectory(scriptsDir);
            
            // 使用 System.Diagnostics 打开系统文件浏览器
            try
            {
                // 在Windows/Linux/Mac上打开脚本目录
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = scriptsDir;
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
                
                _mod.LogInfo($"[CustomizedReplyPanel] ✓ Opened scripts directory: {scriptsDir}");
                _mod.LogInfo($"[CustomizedReplyPanel] ► Please copy your Lua scripts to this folder and then click 'Refresh' button");
            }
            catch (Exception ex)
            {
                _mod.LogInfo($"[CustomizedReplyPanel] ✗ Could not open file browser: {ex.Message}");
                _mod.LogInfo($"[CustomizedReplyPanel] ► Manual import: Copy your .lua files to: {scriptsDir}");
            }
        }
        catch (Exception ex)
        {
            _mod?.LogInfo($"[CustomizedReplyPanel] ✗ Error in OnImportScriptFile: {ex.Message}");
        }
    }

    /// <summary>
    /// 保存脚本按钮点击事件
    /// </summary>
    private void OnSaveScriptClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedScriptResource != null && _mod != null)
        {
            try
            {
                var scriptsDir = _mod.GetCurrentScriptsDirectory();
                bool success = ScriptExecutor.SaveScriptResourceToFile(_viewModel.SelectedScriptResource, scriptsDir);
                
                if (success)
                {
                    _mod.LogInfo($"[CustomizedReplyPanel] ✓ Script saved: {_viewModel.SelectedScriptResource.FileName} to {scriptsDir}");
                    
                    // 保存当前选中的脚本文件名
                    string selectedFileName = _viewModel.SelectedScriptResource?.FileName;
                    _mod.LogInfo($"[CustomizedReplyPanel] ► Saving selected script context: {selectedFileName}");
                    
                    // ✅【新增】刷新脚本列表UI，更新combobox
                    _mod.LogInfo($"[CustomizedReplyPanel] ► Refreshing scripts UI after save...");
                    RefreshScriptsFromDisk();
                    
                    // ✅【新增】恢复选中状态
                    if (!string.IsNullOrEmpty(selectedFileName))
                    {
                        var reselectedScript = _viewModel.ScriptResources.FirstOrDefault(s => s.FileName == selectedFileName);
                        if (reselectedScript != null)
                        {
                            _viewModel.SelectedScriptResource = reselectedScript;
                            _mod.LogInfo($"[CustomizedReplyPanel] ✓ Restored selection to: {selectedFileName}");
                        }
                    }
                    
                    // ✅【新增】通知Mod脚本已修改，触发推送到远程
                    _mod.LogInfo($"[CustomizedReplyPanel] ► Notifying mod of script changes for remote sync...");
                    _mod.NotifyScriptsModified();
                    _mod.LogInfo($"[CustomizedReplyPanel] ✓ Script save completed with UI refresh and remote sync");
                }
                else
                {
                    _mod.LogInfo($"[CustomizedReplyPanel] ✗ Failed to save script: {_viewModel.SelectedScriptResource.FileName}");
                }
            }
            catch (Exception ex)
            {
                _mod?.LogInfo($"[CustomizedReplyPanel] Error saving script: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 删除脚本按钮点击事件
    /// </summary>
    private void OnDeleteScriptClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedScriptResource != null && _mod != null)
        {
            try
            {
                var scriptFileName = _viewModel.SelectedScriptResource.FileName;
                var scriptsDir = _mod.GetCurrentScriptsDirectory();
                bool success = ScriptExecutor.DeleteScriptResourceFile(scriptFileName, scriptsDir);
                
                if (success)
                {
                    _viewModel.ScriptResources.Remove(_viewModel.SelectedScriptResource);
                    _viewModel.SelectedScriptResource = null;
                    _mod.LogInfo($"[CustomizedReplyPanel] ✓ Script deleted: {scriptFileName} from {scriptsDir}");
                    
                    // ✅【新增】刷新脚本列表UI，更新combobox
                    _mod.LogInfo($"[CustomizedReplyPanel] ► Refreshing scripts UI after delete...");
                    RefreshScriptsFromDisk();
                    
                    // ✅【新增】通知Mod脚本已修改，触发推送到远程
                    _mod.LogInfo($"[CustomizedReplyPanel] ► Notifying mod of script changes for remote sync...");
                    _mod.NotifyScriptsModified();
                    _mod.LogInfo($"[CustomizedReplyPanel] ✓ Script delete completed with UI refresh and remote sync");
                }
                else
                {
                    _mod.LogInfo($"[CustomizedReplyPanel] ✗ Failed to delete script: {scriptFileName}");
                }
            }
            catch (Exception ex)
            {
                _mod?.LogInfo($"[CustomizedReplyPanel] Error deleting script: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 插入脚本模板按钮点击事件
    /// </summary>
    private void OnInsertScriptTemplate(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedScriptResource != null && _mod != null)
        {
            try
            {
                _viewModel.SelectedScriptResource.Content = ScriptExecutor.GenerateScriptTemplate();
                _viewModel.NotifyPropertyChanged(nameof(CustomizedReplyPanelViewModel.SelectedScriptResource));
                _mod.LogInfo("[CustomizedReplyPanel] ✓ Inserted script template");
                
                // ✅【新增】自动保存模板到文件（使用当前模式对应的目录）
                var scriptsDir = _mod.GetCurrentScriptsDirectory();
                bool success = ScriptExecutor.SaveScriptResourceToFile(_viewModel.SelectedScriptResource, scriptsDir);
                
                if (success)
                {
                    _mod.LogInfo($"[CustomizedReplyPanel] ✓ Template auto-saved: {_viewModel.SelectedScriptResource.FileName} to {scriptsDir}");
                    
                    // ✅【新增】刷新脚本列表UI
                    RefreshScriptsFromDisk();
                    
                    // ✅【新增】通知Mod脚本已修改
                    _mod.NotifyScriptsModified();
                    _mod.LogInfo($"[CustomizedReplyPanel] ✓ Template completed with UI refresh and remote sync");
                }
            }
            catch (Exception ex)
            {
                _mod?.LogInfo($"[CustomizedReplyPanel] Error inserting script template: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// ✅ 统一的 MatchType 转换方法：中文字符串 -> 整数值
    /// </summary>
    private static int ConvertChineseToMatchTypeValue(string chinese)
    {
        return chinese switch
        {
            "精确匹配" => 0,      // Exact
            "正则表达式" => 1,    // Regex  
            "模糊匹配" => 2,      // Fuzzy
            _ => 0                // 默认精确匹配
        };
    }

    /// <summary>
    /// ✅ 统一的 MatchType 转换方法：枚举对象 -> 中文字符串
    /// </summary>
    private static string ConvertMatchTypeToChinese(object? matchTypeEnum)
    {
        if (matchTypeEnum == null)
            return "精确匹配";  // 默认值

        var typeString = matchTypeEnum.ToString() ?? "";
        return typeString switch
        {
            "Regex" => "正则表达式",
            "Fuzzy" => "模糊匹配",
            "Exact" or _ => "精确匹配"  // Exact 或其他
        };
    }
}