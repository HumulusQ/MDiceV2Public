using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;
using Avalonia.Threading;
using MDiceV2.Abstractions;

namespace MDiceV2.Core.UI.ViewModels;

public partial class ConfigContainerViewModel : ObservableObject
{
    private readonly MDiceV2.Abstractions.IDispatcher? _dispatcher;
    private int _bulkUpdateDepth;
    
    /// <summary>
    /// 【修复】追踪最后一次推送的值，用于检测真实的值变化
    /// ConfigItem在UI销毁重建时会被重新初始化，导致Value被重新赋值
    /// 这个字典记录了最后一次推送到远程的值，用于比较新值是否真的改变了
    /// 只有当值真的从这个记录中的值改变时，才触发推送
    /// </summary>
    private readonly System.Collections.Generic.Dictionary<string, object?> _lastPushedValues = new();

    /// <summary>
    /// 所有配置项的集合
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ConfigItem> items = new();

    /// <summary>
    /// 过滤后的配置项集合，用于UI显示
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ConfigItem> filteredItems = new();

    /// <summary>
    /// 分割比例（已废弃，当前每项独立控制）
    /// </summary>
    [ObservableProperty]
    private double splitRatio = 0.5;

    /// <summary>
    /// 当前的匹配字符串，用于过滤配置项
    /// </summary>
    private string matching = string.Empty;

    /// <summary>
    /// 值变化事件委托
    /// 当配置项值改变时触发外部回调
    /// </summary>
    public System.Action<string, object?>? OnValueChanged;

    /// <summary>
    /// 是否启用值变化回调（用于在批量操作时临时禁用）
    /// </summary>
    private bool _isCallbackEnabled = true;
    public bool IsCallbackEnabled 
    {
        get => _isCallbackEnabled;
        set => _isCallbackEnabled = value;
    }

    /// <summary>
    /// 标题文本
    /// </summary>
    [ObservableProperty]
    private string title = "Configuration";

    /// <summary>
    /// 搜索面板是否可见
    /// </summary>
    [ObservableProperty]
    private bool isSearchPanelVisible = false;

    /// <summary>
    /// Add面板是否可见
    /// </summary>
    [ObservableProperty]
    private bool isAddPanelVisible = false;

    /// <summary>
    /// Help面板是否可见
    /// </summary>
    [ObservableProperty]
    private bool isHelpPanelVisible = false;

    /// <summary>
    /// Key筛选文本
    /// </summary>
    [ObservableProperty]
    private string keyFilterText = string.Empty;

    /// <summary>
    /// Value筛选文本
    /// </summary>
    [ObservableProperty]
    private string valueFilterText = string.Empty;

    /// <summary>
    /// 是否处于编辑模式
    /// 编辑模式时禁用虚拟化以获得更好的动画体验
    /// </summary>
    [ObservableProperty]
    private bool isEditMode = false;

    /// <summary>
    /// 初始化默认值
    /// </summary>
    public void InitializeDefaults()
    {
        // 为所有配置项设置默认值
        foreach (var item in Items)
        {
            if (!GlobalFeedbackMessages.GetAllBasicSettings().ContainsKey(item.Key))
            {
                // 如果数据库中没有该设置，使用默认值
                if (item.Type == ConfigType.CheckBox)
                {
                    GlobalFeedbackMessages.SetBasicSetting(item.Key, "False");
                }
                else if (item.Type == ConfigType.LineEdit)
                {
                    GlobalFeedbackMessages.SetBasicSetting(item.Key, "");
                }
            }
        }
    }

    /// <summary>
    /// 属性变化处理方法
    /// 监听筛选文本变化并更新过滤结果
    /// </summary>
    partial void OnKeyFilterTextChanged(string value)
    {
        UpdateFilteredItems();
    }

    partial void OnValueFilterTextChanged(string value)
    {
        UpdateFilteredItems();
    }

    public ConfigContainerViewModel(MDiceV2.Abstractions.IDispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher;
        FilteredItems = new ObservableCollection<ConfigItem>(Items);
        Items.CollectionChanged += (s, e) =>
        {
            if (_bulkUpdateDepth == 0)
                UpdateFilteredItems();
        };

        // 监听配置项值变化，用于同步到GlobalFeedbackMessages
        PropertyChanged += OnPropertyChanged;
    }

    /// <summary>
    /// Defers filtering while a large set of items is populated so the UI list
    /// is rebuilt only once instead of once per configuration item.
    /// </summary>
    public void BeginBulkUpdate() => _bulkUpdateDepth++;

    /// <summary>
    /// Completes a deferred update and rebuilds the visible collection once.
    /// </summary>
    public void EndBulkUpdate()
    {
        if (_bulkUpdateDepth == 0)
            return;

        _bulkUpdateDepth--;
        if (_bulkUpdateDepth == 0)
            UpdateFilteredItems();
    }

    /// <summary>
    /// 属性变化处理 - 用于同步模板修改
    /// </summary>
    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == null) return;
        
        // 检查是否是Value属性的变化
        if (e.PropertyName.StartsWith("Value_"))
        {
            var key = e.PropertyName.Substring(5); // 移除"Value_"前缀
            var item = Items.FirstOrDefault(i => i.Key == key);
            if (item != null && item.Value is string value)
            {
                // 线程安全地更新GlobalFeedbackMessages.Templates
                if (_dispatcher != null)
                {
                    _dispatcher.Post(() => GlobalFeedbackMessages.FeedbackTemplates[key] = value);
                }
                else
                {
                    Dispatcher.UIThread.Post(() => GlobalFeedbackMessages.FeedbackTemplates[key] = value);
                }
            }
        }
    }

    public void SetValue(string name, object value)
    {
        var item = Items.FirstOrDefault(i => i.Key == name);
        if (item != null)
        {
            // 【修复】在设置值前更新最后推送值的记录
            // 这样在SetValue后，如果ConfigItem被再次初始化为相同值时不会触发推送
            _lastPushedValues[name] = value;
            
            // 只需设置Value，ConfigItem.OnValueChanged会自动调用回调
            // 不要在这里重复调用OnValueChanged
            item.Value = value;
        }
    }

    public object? GetValue(string name)
    {
        var item = Items.FirstOrDefault(i => i.Key == name);
        return item?.Value;
    }

    public void AddConfig(string key, ConfigType type, object? defaultValue = null)
    {
        
        var item = new ConfigItem(key, type)
        {
            DefaultValue = defaultValue
        };
        // 设置值变化回调
        item.ValueChangedCallback = (k, v) =>
        {
            // 【修复】只有在回调启用时才触发事件
            if (!IsCallbackEnabled)
            {
                //LogSender.InfoFormat($"🟢 [ConfigContainerViewModel.ValueChangedCallback] 回调已禁用，跳过 Key={k}");
                return;
            }
            
            // 【关键修复】检查值是否真的改变了（防止UI重建导致的虚假推送）
            // 当ConfigItem被销毁并重新创建时，会重新赋值，但如果值与上次推送的值相同，则跳过推送
            if (_lastPushedValues.TryGetValue(k, out var lastValue))
            {
                if (Equals(lastValue, v))
                {
                    //LogSender.InfoFormat($"🟢 [ConfigContainerViewModel.ValueChangedCallback] 值未改变，跳过推送 Key={k}, Value={v}");
                    return;
                }
            }
            
            // 更新最后推送的值
            _lastPushedValues[k] = v;
            
            //LogSender.InfoFormat($"🟢 [ConfigContainerViewModel.ValueChangedCallback] TRIGGERED for Key={k}, Value={v}");
            if (OnValueChanged == null)
            {
                LogSender.Warn($"🟢 [ConfigContainerViewModel.ValueChangedCallback] ⚠️ OnValueChanged event is NULL!");
            }
            else
            {
                //LogSender.InfoFormat($"🟢 [ConfigContainerViewModel.ValueChangedCallback] ✓ OnValueChanged event EXISTS, 开始调用");
                try
                {
                    OnValueChanged?.Invoke(k, v ?? new object());
                    //LogSender.InfoFormat($"🟢 [ConfigContainerViewModel.ValueChangedCallback] ✓ OnValueChanged event调用成功");
                }
                catch (Exception ex)
                {
                    LogSender.Error($"🟢 [ConfigContainerViewModel.ValueChangedCallback] ❌ OnValueChanged event异常: {ex.Message}");
                }
            }
        };
        Items.Add(item);
        //LogSender.InfoFormat($"🟢 [ConfigContainerViewModel.AddConfig] 配置项已添加到Items, Key={key}, Type={type}, DefaultValue={defaultValue}");
        
        // Initial value based on type
        if (type == ConfigType.LineEdit)
        {
            //LogSender.InfoFormat($"🟢 [ConfigContainerViewModel.AddConfig] LineEdit初始化值: {defaultValue ?? string.Empty}");
            item.Value = defaultValue ?? string.Empty;
        }
        else if (type == ConfigType.CheckBox)
        {
            // 【修复】对于CheckBox，defaultValue来自GetAllBasicSettings返回的字符串
            // 需要正确转换字符串"true"/"false"为布尔值
            object? initialValue = defaultValue;
            if (defaultValue is string stringValue)
            {
                // 字符串类型 - 保持原样，ValueAsBool会处理转换
                initialValue = stringValue;
                //LogSender.InfoFormat($"🟢 [ConfigContainerViewModel.AddConfig] CheckBox初始化值（字符串）: {stringValue}");
            }
            else if (defaultValue is bool boolValue)
            {
                // 已经是bool类型
                initialValue = boolValue;
                //LogSender.InfoFormat($"🟢 [ConfigContainerViewModel.AddConfig] CheckBox初始化值（bool）: {boolValue}");
            }
            else
            {
                // null或其他类型，默认为false
                initialValue = false;
                //LogSender.InfoFormat($"🟢 [ConfigContainerViewModel.AddConfig] CheckBox初始化值（默认）: false");
            }
            
            item.Value = initialValue;
        }
        
        // 【修复】记录初始值为"已推送值"，防止初始化时的自动推送
        _lastPushedValues[key] = defaultValue;
        
        //LogSender.InfoFormat($"🟢 [ConfigContainerViewModel.AddConfig] Item.Value最终值: {item.Value} (Type: {item.Value?.GetType().Name ?? "null"})");
    }

    /// <summary>
    /// 将指定配置项重置为默认值
    /// </summary>
    public void ResetToDefault(string key)
    {
        try
        { 
            
            var item = Items.FirstOrDefault(i => i.Key == key);

            var oldValue = item.Value;
            item.Value = item.DefaultValue;
            
            OnValueChanged?.Invoke(key, item.Value ?? string.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ConfigContainerViewModel.ResetToDefault] 异常: {ex.Message}");
        }
    }

    public void SetMatching(string matching)
    {
        this.matching = matching;
        UpdateFilteredItems();
    }

    public void UpdateFilteredItems()
    {
        FilteredItems.Clear();
        
        // 【诊断】记录过滤前的数据
        //LogSender.InfoFormat($"🔍 [ConfigContainerViewModel.UpdateFilteredItems] 开始过滤，Items总数: {Items.Count}");
        //LogSender.InfoFormat($"🔍 [ConfigContainerViewModel.UpdateFilteredItems] 过滤条件 - KeyFilterText: '{KeyFilterText}', ValueFilterText: '{ValueFilterText}', matching: '{matching}'");
        
        int passedCount = 0;
        foreach (var item in Items)
        {
            bool matchesKey = string.IsNullOrEmpty(KeyFilterText) || item.Key.ToLower().Contains(KeyFilterText.ToLower());
            bool matchesValue = string.IsNullOrEmpty(ValueFilterText) || item.Value?.ToString()?.ToLower().Contains(ValueFilterText.ToLower()) == true;
            bool matchesOldFilter = string.IsNullOrEmpty(matching) || item.Key.ToLower().Contains(matching.ToLower());

            // 【诊断】记录每个项的过滤结果
            //LogSender.InfoFormat($"🔍 [Item {passedCount+1}] Key='{item.Key}', Type='{item.Type}', Value='{item.Value}' → matchesKey={matchesKey}, matchesValue={matchesValue}, matchesOldFilter={matchesOldFilter}");

            if (matchesKey && matchesValue && matchesOldFilter)
            {
                FilteredItems.Add(item);
                //LogSender.InfoFormat($"✅ [Item {passedCount+1}] '{item.Key}' (Type={item.Type}) 通过过滤，已添加到FilteredItems");
                passedCount++;
            }
            else
            {
                //LogSender.InfoFormat($"❌ [Item] '{item.Key}' (Type={item.Type}) 被过滤排除");
            }
        }
        
        // 【诊断】记录过滤后的数据
        //LogSender.InfoFormat($"🔍 [ConfigContainerViewModel.UpdateFilteredItems] 过滤完成，FilteredItems数: {FilteredItems.Count}, 通过项数: {passedCount}");
    }
}
