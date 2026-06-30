using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MDiceV2.Models;
using static MDiceV2.Models.Dice;

#nullable enable
namespace MDiceV2.Models;

/// <summary>
/// MessageProcessor 的 partial 类，用于处理先攻列表指令 (.ri)
/// </summary>
public partial class MessageProcessor
{
    private enum InitiativeAdvantageMode
    {
        None,
        Bonus,
        Penalty
    }

    /// <summary>
    /// 处理先攻列表指令 (.ri)
    /// 支持的格式：
    ///   .ri                 → 投掷1次 d20（裸指令）
    ///   .ri list            → 显示先攻列表
    ///   .ri remove [名字]   → 移除条目
    ///   .ri clear           → 清空列表
    ///   .ri.b              → 奖励骰：投掷 d20 2次取高
    ///   .ri.p              → 惩罚骰：投掷 d20 2次取低
    ///   .ri.b3 20          → 奖励骰：投掷 d20 3次取高
    ///   .ri.p3 20          → 惩罚骰：投掷 d20 3次取低
    ///   .ri.b +2           → 奖励骰：投掷 d20+2 2次取高
    ///   .ri.p3+2           → 惩罚骰：投掷 d20+2 3次取低
    ///   .ri.b3 d20+5 张三  → 奖励骰：投掷 d20+5 3次取高，添加为张三
    ///   .ri#d20             → 投掷1次 d20
    ///   .ri#+3              → 投掷1次 d20+3
    ///   .ri3#+3             → 投掷3次 d20+3
    /// </summary>
    private void HandleInitiativeCommand(string args, Msg msg)
    {
        // 仅限群聊
        if (msg.Source != MessageSource.group)
        {
            Reply(GlobalFeedbackMessages.FeedbackTemplates["InitiativeGroupOnly"], msg);
            return;
        }

        string rawContent = msg.Content.Trim();

        // 第一步：检查特殊命令（优先级最高）
        if (rawContent.Equals(".ri list", System.StringComparison.OrdinalIgnoreCase))
        {
            HandleInitiativeList(msg);
            return;
        }

        if (rawContent.StartsWith(".ri remove ", System.StringComparison.OrdinalIgnoreCase))
        {
            string name = rawContent.Substring(".ri remove ".Length).Trim();
            HandleInitiativeRemove(name, msg);
            return;
        }

        if (rawContent.Equals(".ri clear", System.StringComparison.OrdinalIgnoreCase))
        {
            HandleInitiativeClear(msg);
            return;
        }

        // 第二步：检查投掷格式
        // 裸指令 .ri → 投掷 d20
        if (rawContent.Equals(".ri", System.StringComparison.OrdinalIgnoreCase))
        {
            HandleInitiativeRoll(1, "d20", null, msg);
            return;
        }

        string rollArgs = rawContent.Length > 3 ? rawContent[3..].Trim() : string.Empty;
        if (!TryParseRollCommandPrefixes(rollArgs, out int repeatCount, out var pickMode, out int pickCount, out string remaining))
        {
            Reply(GlobalFeedbackMessages.FeedbackTemplates["InitiativeFormatError"], msg);
            return;
        }

        SplitRollExpressionAndExtraContent(remaining, out string expression, out string? manualName);
        if (string.IsNullOrWhiteSpace(manualName))
        {
            manualName = null;
        }

        if (pickMode != RollPickMode.None)
        {
            var initiativeMode = pickMode == RollPickMode.Bonus
                ? InitiativeAdvantageMode.Bonus
                : InitiativeAdvantageMode.Penalty;

            if (string.IsNullOrWhiteSpace(expression))
            {
                expression = "d20";
            }
            else if (Regex.IsMatch(expression, @"^\d+$"))
            {
                expression = $"d{expression}";
            }
            else if (Regex.IsMatch(expression, @"^[+-]\d+$"))
            {
                expression = $"d20{expression}";
            }

            HandleInitiativeRoll(repeatCount, expression, manualName, msg, initiativeMode, pickCount);
            return;
        }

        if (string.IsNullOrWhiteSpace(expression))
        {
            expression = "d20";
        }
        if (Regex.IsMatch(expression, @"^\d+$"))
        {
            expression = $"d{expression}";
        }
        else if (Regex.IsMatch(expression, @"^[+-]\d+$"))
        {
            expression = $"d20{expression}";
        }

        HandleInitiativeRoll(
            repeatCount,
            expression,
            manualName,
            msg,
            InitiativeAdvantageMode.None,
            1);
    }

    /// <summary>
    /// 处理先攻投掷
    /// </summary>
    private void HandleInitiativeRoll(
        int rollTimes,
        string expression,
        string? manualName,
        Msg msg,
        InitiativeAdvantageMode advantageMode = InitiativeAdvantageMode.None,
        int advantageRollCount = 1)
    {
        // 检查表达式是否以 + 开头（表示添加到 d20）
        bool hasD20Prefix = expression.StartsWith("+");
        if (hasD20Prefix)
        {
            expression = expression.Substring(1); // 去掉前缀的 +
        }

        // 验证表达式不为空
        if (string.IsNullOrWhiteSpace(expression))
        {
            Reply(GlobalFeedbackMessages.FeedbackTemplates["InitiativeFormatError"], msg);
            return;
        }

        // 组装最终投掷表达式
        string fullExpression = hasD20Prefix ? $"d20+{expression}" : expression;

        // 获取人物名
        string personName = string.IsNullOrWhiteSpace(manualName)
            ? GetReasonableSenderName(msg.UserId, msg.IsSimulationMode)
            : manualName;

        // 进行投掷
        List<(int value, string detail)> rollResults = new();
        for (int i = 0; i < rollTimes; i++)
        {
            if (advantageMode == InitiativeAdvantageMode.None)
            {
                var rollResult = Dice.CalculateExpression(fullExpression);
                if (!rollResult.Success || rollResult.Total < 0)
                {
                    Reply(SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["InitiativeExpressionError"], fullExpression), msg);
                    return;
                }

                rollResults.Add((rollResult.Total, rollResult.Detail));
            }
            else
            {
                var picked = RollInitiativeAdvantage(fullExpression, advantageMode, advantageRollCount, msg);
                if (picked == null)
                {
                    return;
                }

                rollResults.Add(picked.Value);
            }
        }

        // 获取或创建群的先攻列表
        var initiativeList = groupInitiativeLists.GetOrAdd(msg.GroupId, _ =>
        {
            Log.Normal($"[先攻列表] 为群 {msg.GroupId} 创建新的先攻列表");
            return new InitiativeList { GroupId = msg.GroupId };
        });

        // 添加条目到列表
        List<string> actualNames = new();
        foreach (var (value, detail) in rollResults)
        {
            var entry = new InitiativeListEntry
            {
                Name = personName,
                InitiativeValue = value,
                DiceExpression = expression,
                RollDetail = detail
            };

            string actualName = initiativeList.AddEntry(entry);
            actualNames.Add(actualName);
            Log.Normal($"[先攻列表] 群{msg.GroupId}: 添加 {actualName} 先攻值 {value}");
        }

        // 立即保存
        SaveGroupInitiativeData(msg.GroupId);

        // 构建回复消息
        if (rollTimes == 1)
        {
            // 单次投掷
            var template = GlobalFeedbackMessages.FeedbackTemplates["InitiativeRollResult"];
            var listDisplay = BuildInitiativeListDisplay(initiativeList);

            string reply = template
                .Replace("{ManName}", actualNames[0])
                .Replace("{RollDetail}", rollResults[0].detail)
                .Replace("{InitValue}", rollResults[0].value.ToString())
                .Replace("{ListDisplay}", listDisplay);

            Reply(reply, msg);
        }
        else
        {
            // 多次投掷
            var template = GlobalFeedbackMessages.FeedbackTemplates["InitiativeMultiRollResult"];
            var itemTemplate = GlobalFeedbackMessages.FeedbackTemplates["InitiativeRollItem"];

            string rollsDetail = string.Empty;
            for (int i = 0; i < rollResults.Count; i++)
            {
                var item = itemTemplate
                    .Replace("{Index}", (i + 1).ToString())
                    .Replace("{RollDetail}", rollResults[i].detail)
                    .Replace("{InitValue}", rollResults[i].value.ToString());

                rollsDetail += item + "\n";
            }

            var listDisplay = BuildInitiativeListDisplay(initiativeList);

            string reply = template
                .Replace("{ManName}", personName)
                .Replace("{Times}", rollTimes.ToString())
                .Replace("{RollsDetail}", rollsDetail.TrimEnd())
                .Replace("{ListDisplay}", listDisplay);

            Reply(reply, msg);
        }
    }

    /// <summary>
    /// 在奖励骰/惩罚骰模式下对单次先攻执行多次投掷并择优取值。
    /// </summary>
    private (int value, string detail)? RollInitiativeAdvantage(
        string fullExpression,
        InitiativeAdvantageMode mode,
        int rollCount,
        Msg msg)
    {
        rollCount = Math.Clamp(rollCount, 1, 9);

        List<(int value, string detail)> candidates = new();
        for (int i = 0; i < rollCount; i++)
        {
            var rollResult = Dice.CalculateExpression(fullExpression);
            if (!rollResult.Success || rollResult.Total < 0)
            {
                Reply(SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["InitiativeExpressionError"], fullExpression), msg);
                return null;
            }

            candidates.Add((rollResult.Total, rollResult.Detail));
        }

        var chosen = candidates[0];
        for (int i = 1; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (mode == InitiativeAdvantageMode.Bonus)
            {
                if (candidate.value > chosen.value)
                {
                    chosen = candidate;
                }
            }
            else if (candidate.value < chosen.value)
            {
                chosen = candidate;
            }
        }

        string modeLabel = mode == InitiativeAdvantageMode.Bonus ? "奖励骰" : "惩罚骰";
        string pickLabel = mode == InitiativeAdvantageMode.Bonus ? "取高" : "取低";
        string allDetails = string.Join(
            "；",
            candidates.Select((candidate, index) => $"{index + 1}) {candidate.detail} → {candidate.value}"));
        string detail = $"{modeLabel}{rollCount}次：{allDetails}，{pickLabel} {chosen.value}";

        return (chosen.value, detail);
    }

    /// <summary>
    /// 显示先攻列表
    /// </summary>
    private void HandleInitiativeList(Msg msg)
    {
        var initiativeList = groupInitiativeLists.GetOrAdd(msg.GroupId, _ => new InitiativeList { GroupId = msg.GroupId });
        var listDisplay = BuildInitiativeListDisplay(initiativeList);
        Reply(listDisplay, msg);
    }

    /// <summary>
    /// 从先攻列表中移除条目
    /// </summary>
    private void HandleInitiativeRemove(string name, Msg msg)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Reply(GlobalFeedbackMessages.FeedbackTemplates["InitiativeFormatError"], msg);
            return;
        }

        if (!groupInitiativeLists.TryGetValue(msg.GroupId, out var initiativeList) || initiativeList.IsEmpty)
        {
            Reply("先攻列表为空，无法移除", msg);
            return;
        }

        if (initiativeList.RemoveByName(name))
        {
            SaveGroupInitiativeData(msg.GroupId);
            var listDisplay = BuildInitiativeListDisplay(initiativeList);
            Reply($"已移除 {name}\n\n【当前先攻列表】\n{listDisplay}", msg);
            Log.Normal($"[先攻列表] 群{msg.GroupId}: 已移除 {name}");
        }
        else
        {
            Reply($"未找到名为 {name} 的条目", msg);
        }
    }

    /// <summary>
    /// 清空先攻列表
    /// </summary>
    private void HandleInitiativeClear(Msg msg)
    {
        if (groupInitiativeLists.TryGetValue(msg.GroupId, out var initiativeList))
        {
            initiativeList.Clear();
            SaveGroupInitiativeData(msg.GroupId);
            Reply("先攻列表已清空", msg);
            Log.Normal($"[先攻列表] 群{msg.GroupId}: 列表已清空");
        }
        else
        {
            Reply("先攻列表为空", msg);
        }
    }

    /// <summary>
    /// 构建先攻列表的显示文本
    /// </summary>
    private string BuildInitiativeListDisplay(InitiativeList list)
    {
        if (list.IsEmpty)
        {
            return GlobalFeedbackMessages.FeedbackTemplates["InitiativeListEmpty"];
        }

        var sorted = list.GetSorted();
        var entryTemplate = GlobalFeedbackMessages.FeedbackTemplates["InitiativeListEntryFormat"];

        List<string> lines = new();
        for (int i = 0; i < sorted.Count; i++)
        {
            var entry = sorted[i];
            string line = entryTemplate
                .Replace("{Rank}", (i + 1).ToString())
                .Replace("{Name}", entry.Name)
                .Replace("{Value}", entry.InitiativeValue.ToString());

            lines.Add(line);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 从数据库加载所有群的先攻列表数据
    /// </summary>
    private void LoadAllGroupInitiativeData()
    {
        try
        {
            if (DataIO == null)
            {
                Log.Warn("[先攻列表] DataIO 未初始化，跳过加载");
                return;
            }

            Log.InfoFormat("[先攻列表] 开始加载所有群的先攻列表数据");

            // 尝试读取所有 InitiativeData_* 的数据
            var allData = DataIO.ReadAllData("InitiativeData");
            if (allData == null || allData.Count == 0)
            {
                Log.InfoFormat("[先攻列表] 没有保存的先攻列表数据");
                return;
            }

            int successCount = 0;
            int failureCount = 0;

            foreach (var kvp in allData)
            {
                try
                {
                    if (!long.TryParse(kvp.Key, out var groupId))
                    {
                        Log.Warn($"[先攻列表] 无效的群ID: {kvp.Key}");
                        failureCount++;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        Log.Warn($"[先攻列表] 群 {groupId} 的数据为空");
                        failureCount++;
                        continue;
                    }

                    var data = System.Text.Json.JsonSerializer.Deserialize<GroupInitiativeData>(kvp.Value);
                    if (data == null || data.Entries == null || data.Entries.Count == 0)
                    {
                        Log.Warn($"[先攻列表] 群 {groupId} 的数据格式无效");
                        failureCount++;
                        continue;
                    }

                    // 创建先攻列表并加载条目
                    var list = new InitiativeList { GroupId = groupId };
                    foreach (var entry in data.Entries)
                    {
                        list.AddEntry(entry);
                    }

                    groupInitiativeLists[groupId] = list;
                    successCount++;
                    Log.Normal($"[先攻列表] 成功加载群 {groupId} 的先攻列表 ({data.Entries.Count} 条记录)");
                }
                catch (Exception ex)
                {
                    Log.Error($"[先攻列表] 加载群ID {kvp.Key} 的数据失败: {ex.Message}");
                    failureCount++;
                }
            }

            Log.InfoFormat($"[先攻列表] 加载完成: 成功 {successCount} 个群，失败 {failureCount} 个群");
        }
        catch (Exception ex)
        {
            Log.Error($"[先攻列表] 加载所有群的先攻列表失败: {ex.Message}");
            Log.Error($"[先攻列表] 堆栈跟踪: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// 保存特定群的先攻列表数据
    /// </summary>
    private void SaveGroupInitiativeData(long groupId)
    {
        try
        {
            if (DataIO == null)
            {
                Log.Warn($"[先攻列表] DataIO 未初始化，跳过保存群 {groupId} 的先攻列表");
                return;
            }

            if (!groupInitiativeLists.TryGetValue(groupId, out var list))
            {
                Log.Warn($"[先攻列表] 群 {groupId} 未在内存中找到先攻列表");
                return;
            }

            var data = new GroupInitiativeData
            {
                GroupId = groupId,
                Entries = list.GetAll(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            string json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

            DataIO.SaveData("InitiativeData", groupId.ToString(), json);
            Log.Normal($"[先攻列表] 已保存群 {groupId} 的先攻列表 ({data.Entries.Count} 条记录)");
        }
        catch (Exception ex)
        {
            Log.Error($"[先攻列表] 保存群 {groupId} 的先攻列表失败: {ex.Message}");
            Log.Error($"[先攻列表] 堆栈跟踪: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// 保存所有群的先攻列表数据
    /// </summary>
    private void SaveAllGroupInitiativeData()
    {
        try
        {
            if (DataIO == null)
            {
                Log.Warn("[先攻列表] DataIO 未初始化，跳过保存所有群的先攻列表");
                return;
            }

            Log.InfoFormat("[先攻列表] 开始保存所有群的先攻列表数据");

            int count = 0;
            foreach (var kvp in groupInitiativeLists)
            {
                try
                {
                    SaveGroupInitiativeData(kvp.Key);
                    count++;
                }
                catch (Exception ex)
                {
                    Log.Error($"[先攻列表] 保存群 {kvp.Key} 的数据失败: {ex.Message}");
                }
            }

            Log.InfoFormat($"[先攻列表] 已保存 {count} 个群的先攻列表数据");
        }
        catch (Exception ex)
        {
            Log.Error($"[先攻列表] 保存所有群的先攻列表失败: {ex.Message}");
            Log.Error($"[先攻列表] 堆栈跟踪: {ex.StackTrace}");
        }
    }
}
