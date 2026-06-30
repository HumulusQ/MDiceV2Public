using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace MDiceV2.Core.UI.Models;

/// <summary>
/// CustomizedReply Mod 的 ViewModel
/// 用于管理自定义回复规则的 UI 状态和交互逻辑
/// 
/// 功能：
/// 1. 加载和显示规则库
/// 2. 添加、编辑、删除规则
/// 3. 保存规则到 data.json
/// 4. 与 CustomizedReply Mod 通信
/// </summary>
public partial class CustomizedReplyViewModel : ObservableObject
{
    /// <summary>
    /// 规则列表（绑定到 UI）
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ReplyRuleItem> rules = new();

    /// <summary>
    /// 当前选中的规则（用于编辑）
    /// </summary>
    [ObservableProperty]
    private ReplyRuleItem? selectedRule;

    /// <summary>
    /// 新增或编辑的触发词
    /// </summary>
    [ObservableProperty]
    private string newTrigger = string.Empty;

    /// <summary>
    /// 新增或编辑的回复内容
    /// </summary>
    [ObservableProperty]
    private string newReply = string.Empty;

    /// <summary>
    /// 选中的匹配类型
    /// </summary>
    [ObservableProperty]
    private int selectedMatchTypeIndex = 0;

    /// <summary>
    /// 是否处于编辑模式
    /// </summary>
    [ObservableProperty]
    private bool isEditingMode = false;

    /// <summary>
    /// 规则数据文件路径
    /// 通常为 data/mods/CustomizedReply/data.json
    /// </summary>
    private string _dataFilePath = string.Empty;

    /// <summary>
    /// 构造函数
    /// </summary>
    public CustomizedReplyViewModel()
    {
        // 初始化数据文件路径
        _dataFilePath = Path.Combine(
            Environment.CurrentDirectory,
            "data", "mods", "CustomizedReply", "data.json"
        );

        // 加载现有规则
        LoadRules();
    }

    /// <summary>
    /// 从 data.json 加载规则
    /// </summary>
    private void LoadRules()
    {
        try
        {
            Rules.Clear();

            if (!File.Exists(_dataFilePath))
            {
                // 文件不存在，静默返回
                return;
            }

            var json = File.ReadAllText(_dataFilePath);
            using var document = JsonDocument.Parse(json);
            
            if (!document.RootElement.TryGetProperty("replies", out var repliesElement))
                return;

            foreach (var ruleElement in repliesElement.EnumerateArray())
            {
                var trigger = ruleElement.GetProperty("trigger").GetString() ?? "";
                var matchTypeStr = ruleElement.GetProperty("matchType").GetString() ?? "exact";
                var matchType = Enum.Parse<MatchType>(matchTypeStr, ignoreCase: true);

                var ruleReplies = new ObservableCollection<string>();
                foreach (var replyElement in ruleElement.GetProperty("replies").EnumerateArray())
                {
                    ruleReplies.Add(replyElement.GetString() ?? "");
                }

                Rules.Add(new ReplyRuleItem
                {
                    Trigger = trigger,
                    MatchType = matchType,
                    Replies = ruleReplies
                });
            }
        }
        catch
        {
            // 加载失败，静默处理
        }
    }

    /// <summary>
    /// 添加新规则
    /// </summary>
    [RelayCommand]
    private void AddRule()
    {
        if (string.IsNullOrWhiteSpace(NewTrigger) || string.IsNullOrWhiteSpace(NewReply))
        {
            return;
        }

        var matchType = SelectedMatchTypeIndex switch
        {
            0 => MatchType.Exact,
            1 => MatchType.Regex,
            2 => MatchType.Fuzzy,
            _ => MatchType.Exact
        };

        var newRule = new ReplyRuleItem
        {
            Trigger = NewTrigger,
            MatchType = matchType,
            Replies = new ObservableCollection<string> { NewReply }
        };

        Rules.Add(newRule);

        // 清空输入
        NewTrigger = string.Empty;
        NewReply = string.Empty;
        SelectedMatchTypeIndex = 0;

        // 保存到文件
        SaveRules();
    }

    /// <summary>
    /// 删除选中的规则
    /// </summary>
    [RelayCommand]
    private void DeleteRule()
    {
        if (SelectedRule == null)
            return;

        Rules.Remove(SelectedRule);
        SaveRules();
    }

    /// <summary>
    /// 编辑选中的规则
    /// </summary>
    [RelayCommand]
    private void EditRule()
    {
        if (SelectedRule == null)
            return;

        NewTrigger = SelectedRule.Trigger;
        NewReply = string.Join("; ", SelectedRule.Replies);
        SelectedMatchTypeIndex = (int)SelectedRule.MatchType;
        IsEditingMode = true;
    }

    /// <summary>
    /// 保存编辑
    /// </summary>
    [RelayCommand]
    private void SaveEdit()
    {
        if (SelectedRule == null || string.IsNullOrWhiteSpace(NewTrigger))
            return;

        SelectedRule.Trigger = NewTrigger;
        SelectedRule.MatchType = SelectedMatchTypeIndex switch
        {
            0 => MatchType.Exact,
            1 => MatchType.Regex,
            2 => MatchType.Fuzzy,
            _ => MatchType.Exact
        };

        // 清空回复列表并添加新回复
        SelectedRule.Replies.Clear();
        foreach (var reply in NewReply.Split(';'))
        {
            var trimmed = reply.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                SelectedRule.Replies.Add(trimmed);
        }

        IsEditingMode = false;
        SaveRules();
    }

    /// <summary>
    /// 取消编辑
    /// </summary>
    [RelayCommand]
    private void CancelEdit()
    {
        IsEditingMode = false;
        NewTrigger = string.Empty;
        NewReply = string.Empty;
        SelectedMatchTypeIndex = 0;
    }

    /// <summary>
    /// 将规则保存到 data.json
    /// </summary>
    private void SaveRules()
    {
        try
        {
            // 确保目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(_dataFilePath) ?? "");

            // 构建 JSON 结构
            var options = new JsonSerializerOptions { WriteIndented = true };
            var rulesData = new
            {
                description = "CustomizedReply Mod 的规则库",
                replies = Rules.Select(r => new
                {
                    description = $"{r.MatchType} 匹配",
                    trigger = r.Trigger,
                    matchType = r.MatchType.ToString().ToLower(),
                    replies = r.Replies.ToList()
                }).ToList()
            };

            var json = JsonSerializer.Serialize(rulesData, options);
            File.WriteAllText(_dataFilePath, json);
        }
        catch
        {
            // 保存失败，静默处理
        }
    }

    /// <summary>
    /// 刷新规则列表（从文件重新加载）
    /// </summary>
    [RelayCommand]
    private void RefreshRules()
    {
        LoadRules();
    }

    /// <summary>
    /// 打开规则文件所在的文件夹
    /// </summary>
    [RelayCommand]
    private void OpenRulesFolder()
    {
        try
        {
            var folderPath = Path.GetDirectoryName(_dataFilePath);
            if (!string.IsNullOrEmpty(folderPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = folderPath,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // 打开文件夹失败，静默处理
        }
    }
}

/// <summary>
/// 单条回复规则项
/// </summary>
public partial class ReplyRuleItem : ObservableObject
{
    /// <summary>触发词</summary>
    [ObservableProperty]
    private string trigger = string.Empty;

    /// <summary>匹配类型</summary>
    [ObservableProperty]
    private MatchType matchType = MatchType.Exact;

    /// <summary>可能的回复列表</summary>
    [ObservableProperty]
    private ObservableCollection<string> replies = new();

    /// <summary>
    /// 标签显示（用于列表显示）
    /// 格式：触发词 (匹配类型)
    /// </summary>
    public string DisplayLabel => $"{Trigger} ({MatchType})";
}

/// <summary>
/// 匹配类型枚举
/// </summary>
public enum MatchType
{
    /// <summary>精确匹配</summary>
    Exact = 0,

    /// <summary>正则表达式匹配</summary>
    Regex = 1,

    /// <summary>模糊匹配</summary>
    Fuzzy = 2
}
