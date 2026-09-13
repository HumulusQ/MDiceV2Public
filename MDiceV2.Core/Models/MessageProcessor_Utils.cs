using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;
using static MDiceV2.Models.Dice;

namespace MDiceV2.Models;

public partial class MessageProcessor : ObservableObject
{
    /// <summary>
    /// 简单解析主干部分，避免复杂的正则表达式回溯
    /// 支持#符号用于循环模式：#表示重复上一次投掷，#N（N为0-9）表示投掷N次
    /// </summary>
    private List<(string fullText, List<string> subCmds, string skill, string value)> ParseMainPartSimple(string input, string mode)
    {
        var results = new List<(string, List<string>, string, string)>();

        if (string.IsNullOrWhiteSpace(input))
        {
            return results;
        }

        // 逐字符扫描，允许指令与技能紧贴（如 .b3力量）
        int index = 0;
        var currentSubCmds = new List<string>();

        while (index < input.Length)
        {
            // 跳过空白
            while (index < input.Length && char.IsWhiteSpace(input[index]))
            {
                index++;
            }

            if (index >= input.Length)
            {
                break;
            }

            // 尝试匹配#符号（循环模式特殊符号，仅在#开头时）
            if (input[index] == '#')
            {
                var hashMatch = Regex.Match(input[index..], @"^#(\d?)");
                if (hashMatch.Success)
                {
                    // 识别#或#N（N为0-9）
                    string hashValue = hashMatch.Groups[1].Value; // 如果有数字则为数字，否则为空
                    string fullText = hashMatch.Value;
                    // 使用特殊标记"#"作为skill，数字作为value
                    results.Add((fullText, new List<string>(currentSubCmds), "#", hashValue));
                    currentSubCmds.Clear();
                    index += hashMatch.Length;
                    continue;
                }
            }

            // 尝试匹配子命令（.p/.b/.v/.a/.d 可带后缀；.sX/.r/.#X）
            var subCmdMatch = Regex.Match(input[index..], @"^(\.(?:p|b|v|a|d)\d*|\.s\d*|\.r|\.\#\d?)", RegexOptions.IgnoreCase);
            if (subCmdMatch.Success)
            {
                currentSubCmds.Add(subCmdMatch.Value);
                index += subCmdMatch.Length;

                // 跳过空白，然后尝试匹配紧跟的技能名（无数字，允许 +/- 修饰符或纯数字修饰）
                int tempIndex = index;
                while (tempIndex < input.Length && char.IsWhiteSpace(input[tempIndex]))
                {
                    tempIndex++;
                }

                var skillAfterCmd = Regex.Match(input[tempIndex..], @"^([A-Za-z_\u4e00-\u9fa5]+)");
                if (skillAfterCmd.Success)
                {
                    string skill = skillAfterCmd.Groups[1].Value;
                    tempIndex += skillAfterCmd.Length;
                    
                    // 跳过空白，然后尝试匹配修饰符
                    int modIndex = tempIndex;
                    while (modIndex < input.Length && char.IsWhiteSpace(input[modIndex]))
                    {
                        modIndex++;
                    }
                    
                    var modifierMatch = Regex.Match(input[modIndex..], @"^([-+]?\d+)");
                    if (modifierMatch.Success)
                    {
                        skill += modifierMatch.Groups[1].Value;
                        index = modIndex + modifierMatch.Length;
                    }
                    else
                    {
                        index = tempIndex;
                    }
                    
                    string fullText = (string.Join("", currentSubCmds) + " " + skill).Trim();
                    results.Add((fullText, new List<string>(currentSubCmds), skill, string.Empty));
                    currentSubCmds.Clear();
                }

                continue;
            }

            // 尝试匹配技能（允许直接跟随纯数字、骰子表达式(+d3)或 +/- 修饰符，并允许技能名和修饰符之间有空格）
            var skillMatch = Regex.Match(input[index..], @"^([A-Za-z_\u4e00-\u9fa5]+)");
            if (skillMatch.Success && !string.IsNullOrEmpty(skillMatch.Groups[1].Value))
            {
                string skill = skillMatch.Groups[1].Value;
                int tempIndex = index + skillMatch.Length;

                // 保存原始位置，用于后续处理
                int startIndex = index;

                // 跳过空白，尝试匹配修饰符
                while (tempIndex < input.Length && char.IsWhiteSpace(input[tempIndex]))
                {
                    tempIndex++;
                }

                // 尝试匹配 +/- 修饰符或纯数字
                var modMatch = Regex.Match(input[tempIndex..], @"^([-+]?(?:\d+|d\d+))");
                if (modMatch.Success)
                {
                    string modifier = modMatch.Groups[1].Value;
                    skill += modifier;
                    tempIndex += modMatch.Length;
                    index = tempIndex;
                }
                else
                {
                    // 没有修饰符，只推进技能名的长度
                    index = tempIndex;
                }

                string fullText = (string.Join("", currentSubCmds) + " " + skill).Trim();
                results.Add((fullText, new List<string>(currentSubCmds), skill, string.Empty));
                currentSubCmds.Clear();
                continue;
            }

            // 尝试匹配纯数值
            var valueMatch = Regex.Match(input[index..], @"^(\d+)");
            if (valueMatch.Success)
            {
                string value = valueMatch.Groups[1].Value;
                string fullText = (string.Join("", currentSubCmds) + " " + value).Trim();
                results.Add((fullText, new List<string>(currentSubCmds), string.Empty, value));
                currentSubCmds.Clear();
                index += valueMatch.Length;
                continue;
            }

            // 未匹配到有效元素时，跳过当前字符防止死循环
            index++;
        }

        return results;
    }

    /// <summary>
    /// 简化的CoC7主干部分处理
    /// 优先级处理顺序：
    /// 1. 纯数字（value）→ 直接使用作为检定值
    /// 2. 技能+修饰 → 从字典查找技能值，应用修饰符
    /// 3. 仅修饰符或数字 → 直接使用该值
    /// 4. 技能不存在 → 若无修饰符则报错，有修饰符则按0处理
    /// </summary>
    private (string detail, string exmessage) ProcessCoC7MainPartSimple(string fullText, List<string> subCmds, string skill, string value, ConcurrentDictionary<string, int> characterSkillsDict, ref List<string> lastSubCmds, ref string lastSkillName, ref int lastSkillValue)
    {
        string currentSkillName = "";
        int currentSkillValue = 0;
        
        // 处理 # 符号（循环模式特殊符号）
        if (skill == "#")
        {
            // # 表示重复上一次投掷，#N 表示投掷N次
            int repeatCount = 1;
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out int parsedCount) && parsedCount >= 0 && parsedCount <= 9)
            {
                repeatCount = parsedCount;
                if (repeatCount == 0) repeatCount = 10; // #0 表示投掷10次
            }

            // 保存上一次的技能名和值，以供重复使用
            string savedSkillName = lastSkillName;
            int savedSkillValue = lastSkillValue;

            var hashResults = new List<string>();
            for (int i = 0; i < repeatCount; i++)
            {
                // 重新组装为 skill="力量40" 的形式进行递归，确保修饰符被正确应用
                // 这样能保留修饰值，在递归中通过正常的修饰符处理逻辑进行覆盖
                string recursiveSkill = !string.IsNullOrEmpty(savedSkillName) 
                    ? $"{savedSkillName}{savedSkillValue}" 
                    : savedSkillValue.ToString();
                
                var (hashDetail, hashExmsg) = ProcessCoC7MainPartSimple(fullText, subCmds, recursiveSkill, "", characterSkillsDict, ref lastSubCmds, ref lastSkillName, ref lastSkillValue);
                hashResults.Add(hashDetail);
            }

            return (string.Join("\n", hashResults), "");
        }

        else if (!string.IsNullOrEmpty(skill))
        {
            var skillMatch = Regex.Match(skill, @"^([A-Za-z_\u4e00-\u9fa5]+)([-+]?(?:\d+|d\d+))?$");

          if (skillMatch.Success && !string.IsNullOrEmpty(skillMatch.Groups[1].Value))
            {
                // 有技能名
                currentSkillName = skillMatch.Groups[1].Value;
                string modifierStr = skillMatch.Groups[2].Value;

                // 从字典查找技能
                if (characterSkillsDict.TryGetValue(currentSkillName, out int storedSkillValue))
                {
                    currentSkillValue = storedSkillValue;
                    lastSkillName = currentSkillName;
                }
                else
                {
                    // 技能不存在
                    if (modifierStr != null)
                    {
                        // 有修饰符，技能视为0
                        currentSkillValue = 0;
                        lastSkillName = currentSkillName;
                    }
                    else
                    {
                        // 无修饰符，报错
                        return (SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["SkillNotFound"], currentSkillName), "");
                    }
                }

                // 解析修饰符（可能是纯数字、骰子表达式或 +/- 修饰）
                if (!string.IsNullOrEmpty(modifierStr))
                {
                    if (modifierStr.StartsWith("+"))
                    {
                        string valueStr = modifierStr.Substring(1);
                        // 检查是否为骰子表达式（+d3、+d20等）
                        if (valueStr.ToLowerInvariant().StartsWith("d"))
                        {
                            var diceRoll = Dice.Roll(valueStr);
                            if (diceRoll.Success)
                                currentSkillValue += diceRoll.Total;
                        }
                        else if (int.TryParse(valueStr, out int positiveVal))
                            currentSkillValue += positiveVal;
                    }
                    else if (modifierStr.StartsWith("-"))
                    {
                        string valueStr = modifierStr.Substring(1);
                        // 检查是否为骰子表达式（-d3、-d20等）
                        if (valueStr.ToLowerInvariant().StartsWith("d"))
                        {
                            var diceRoll = Dice.Roll(valueStr);
                            if (diceRoll.Success)
                                currentSkillValue -= diceRoll.Total;
                        }
                        else if (int.TryParse(valueStr, out int negativeVal))
                            currentSkillValue -= negativeVal;
                    }
                    else
                    {
                        // 纯数字或骰子表达式，直接作为修饰值
                        if (int.TryParse(modifierStr, out int pureModifier))
                            currentSkillValue = pureModifier;
                        else if (modifierStr.ToLowerInvariant().StartsWith("d"))
                        {
                            var diceRoll = Dice.Roll(modifierStr);
                            if (diceRoll.Success)
                                currentSkillValue = diceRoll.Total;
                        }
                    }
                }

                lastSkillValue = currentSkillValue;



            }
            else
            {
                // 优先级3: 可能只是修饰符或数字
                var modMatch = Regex.Match(skill, @"^([-+]?(?:\d+|d\d+))$");
                if (modMatch.Success && int.TryParse(modMatch.Groups[1].Value, out int modVal))
                {
                    currentSkillValue = modVal;
                    lastSkillValue = currentSkillValue;
                    currentSkillName = lastSkillName; // 使用上次的技能名
                }
                else if (modMatch.Success)
                {
                    // 可能是骰子表达式
                    var diceRoll = Dice.Roll(skill);
                    if (diceRoll.Success)
                    {
                        currentSkillValue = diceRoll.Total;
                        lastSkillValue = currentSkillValue;
                        currentSkillName = lastSkillName;
                    }
                    else
                    {
                        // 无法解析，使用上次值
                        currentSkillName = lastSkillName;
                        currentSkillValue = lastSkillValue;
                    }
                }
                else
                {
                    // 无法解析，使用上次值
                    currentSkillName = lastSkillName;
                    currentSkillValue = lastSkillValue;
                }
            }
        }
        else
        {
            // 没有技能名，检查是否有纯数值（.cc30 的情况）
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out int pureValue))
            {
                // 直接使用提供的数值作为检定目标
                currentSkillValue = pureValue;
                currentSkillName = "直接";
                lastSkillValue = currentSkillValue;
                lastSkillName = currentSkillName;
            }
            else
            {
                // 没有技能和数值，使用上次的值
                currentSkillName = lastSkillName;
                currentSkillValue = lastSkillValue;
            }
        }

        var effectiveSubCmds = (subCmds != null && subCmds.Count > 0) ? subCmds : lastSubCmds;
        if (effectiveSubCmds.Count > 0)
        {
            lastSubCmds = new List<string>(effectiveSubCmds);
        }

        // 统计奖励/惩罚骰数量
        int bonusDice = 0;
        foreach (var cmd in effectiveSubCmds)
        {
            if (cmd.StartsWith(".p", StringComparison.OrdinalIgnoreCase))
            {
                bonusDice -= ParseDiceCount(cmd);
            }
            else if (cmd.StartsWith(".b", StringComparison.OrdinalIgnoreCase))
            {
                bonusDice += ParseDiceCount(cmd);
            }
        }

        // CoC7 奖惩骰：固定个位，掷(1+max(bonus, penalty))个十位骰，按规则取优/劣
        var onesRoll = Dice.Roll("1d10");
        if (!onesRoll.Success) return (SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["DiceRollError"], onesRoll.Detail), "");
        int ones = onesRoll.Total == 10 ? 0 : onesRoll.Total;

        var tensRolls = new List<int>();
        for (int i = 0; i < 1+Math.Abs(bonusDice); i++)
        {
            var tens = Dice.Roll("1d10");
            if (!tens.Success) return (SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["DiceRollError"], tens.Detail), "");
            tensRolls.Add(tens.Total == 10 ? 0 : tens.Total);
        }

        int chosenTensIndex = 0;
        string diceNotation = "";
        if (bonusDice < 0)
        {
            // 取最大十位（最差）
            chosenTensIndex = tensRolls.IndexOf(tensRolls.Max());
            string tensList = string.Join(",", tensRolls);
            diceNotation = $"#P[{tensList}]";
        }
        else if (bonusDice > 0)
        {
            // 取最小十位（最好）
            chosenTensIndex = tensRolls.IndexOf(tensRolls.Min());
            string tensList = string.Join(",", tensRolls);
            diceNotation = $"#B[{tensList}]";
        }

        int finalRoll = tensRolls[chosenTensIndex] * 10 + ones;
        if (finalRoll == 0) finalRoll = 100;

        string checkResult = Dice.CoC7_Check(finalRoll, currentSkillValue);
        string detail = SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["CoCCheckResult"], finalRoll.ToString(), currentSkillValue.ToString(), diceNotation, checkResult, currentSkillName);

        // 根据检定结果获取个性化文本
        string exmessageKey = checkResult switch
        {
            "极限成功" => "CoCExMessageExtremeSuccess",
            "困难成功" => "CoCExMessageHardSuccess",
            "成功" => "CoCExMessageSuccess",
            "失败" => "CoCExMessageFailure",
            "大成功" => "CoCExMessageCriticalSuccess",
            "大失败" => "CoCExMessageCriticalFailure",
            _ => "CoCExMessageFailure"
        };
        string exmessage = GlobalFeedbackMessages.FeedbackTemplates[exmessageKey];

        return (detail, exmessage);
    }

    /// <summary>
    /// 简化的ET主干部分处理
    /// </summary>
    private (string detail, string exmessage) ProcessETMainPartSimple(string fullText, List<string> subCmds, string skill, string value, ConcurrentDictionary<string, int> characterSkillsDict, ref List<string> lastSubCmds, ref string lastSkillName, ref int lastSkillValue)
    {
        // 类似的逻辑，适配ET规则
        string currentSkillName = "";
        int currentSkillValue = 0;

        // 处理 # 符号
        if (skill == "#")
        {
            // # 表示重复上一次投掷，#N 表示投掷N次
            int repeatCount = 1;
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out int parsedCount) && parsedCount >= 0 && parsedCount <= 9)
            {
                repeatCount = parsedCount;
                if (repeatCount == 0) repeatCount = 10; // #0 表示投掷10次
            }

            // 保存上一次的技能名和值，以供重复使用
            string savedSkillName = lastSkillName;
            int savedSkillValue = lastSkillValue;

            var hashResults = new List<string>();
            for (int i = 0; i < repeatCount; i++)
            {
                // 重新组装为 skill="力量40" 的形式进行递归，确保修饰符被正确应用
                // 这样能保留修饰值，在递归中通过正常的修饰符处理逻辑进行覆盖
                string recursiveSkill = !string.IsNullOrEmpty(savedSkillName) 
                    ? $"{savedSkillName}{savedSkillValue}" 
                    : savedSkillValue.ToString();
                
                var (hashDetail, hashExmsg) = ProcessETMainPartSimple(fullText, lastSubCmds, recursiveSkill, "", characterSkillsDict, ref lastSubCmds, ref lastSkillName, ref lastSkillValue);
                hashResults.Add(hashDetail);
            }

            return (string.Join("\n", hashResults), "");
        }

        else if (!string.IsNullOrEmpty(skill))
        {
            var skillMatch = Regex.Match(skill, @"^([A-Za-z_\u4e00-\u9fa5]+)([-+]?(?:\d+|d\d+))?$");

          if (skillMatch.Success && !string.IsNullOrEmpty(skillMatch.Groups[1].Value))
            {
                // 有技能名
                currentSkillName = skillMatch.Groups[1].Value;
                string modifierStr = skillMatch.Groups[2].Value;

                // 从字典查找技能
                if (characterSkillsDict.TryGetValue(currentSkillName, out int storedSkillValue))
                {
                    currentSkillValue = storedSkillValue;
                    lastSkillName = currentSkillName;
                }
                else
                {
                    // 技能不存在
                    if (modifierStr != null)
                    {
                        // 有修饰符，技能视为0
                        currentSkillValue = 0;
                        lastSkillName = currentSkillName;
                    }
                    else
                    {
                        // 无修饰符，报错
                        return (SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["SkillNotFound"], currentSkillName), "");
                    }
                }

                // 解析修饰符（可能是纯数字、骰子表达式或 +/- 修饰）
                if (!string.IsNullOrEmpty(modifierStr))
                {
                    if (modifierStr.StartsWith("+"))
                    {
                        string valueStr = modifierStr.Substring(1);
                        // 检查是否为骰子表达式（+d3、+d20等）
                        if (valueStr.ToLowerInvariant().StartsWith("d"))
                        {
                            var diceRoll = Dice.Roll(valueStr);
                            if (diceRoll.Success)
                                currentSkillValue += diceRoll.Total;
                        }
                        else if (int.TryParse(valueStr, out int positiveVal))
                            currentSkillValue += positiveVal;
                    }
                    else if (modifierStr.StartsWith("-"))
                    {
                        string valueStr = modifierStr.Substring(1);
                        // 检查是否为骰子表达式（-d3、-d20等）
                        if (valueStr.ToLowerInvariant().StartsWith("d"))
                        {
                            var diceRoll = Dice.Roll(valueStr);
                            if (diceRoll.Success)
                                currentSkillValue -= diceRoll.Total;
                        }
                        else if (int.TryParse(valueStr, out int negativeVal))
                            currentSkillValue -= negativeVal;
                    }
                    else
                    {
                        // 纯数字或骰子表达式，直接作为修饰值
                        if (int.TryParse(modifierStr, out int pureModifier))
                            currentSkillValue = pureModifier;
                        else if (modifierStr.ToLowerInvariant().StartsWith("d"))
                        {
                            var diceRoll = Dice.Roll(modifierStr);
                            if (diceRoll.Success)
                                currentSkillValue = diceRoll.Total;
                        }
                    }
                }

                lastSkillValue = currentSkillValue;



            }
            else
            {
                // 优先级3: 可能只是修饰符或数字
                var modMatch = Regex.Match(skill, @"^([-+]?(?:\d+|d\d+))$");
                if (modMatch.Success && int.TryParse(modMatch.Groups[1].Value, out int modVal))
                {
                    currentSkillValue = modVal;
                    lastSkillValue = currentSkillValue;
                    currentSkillName = lastSkillName; // 使用上次的技能名
                }
                else if (modMatch.Success)
                {
                    // 可能是骰子表达式
                    var diceRoll = Dice.Roll(skill);
                    if (diceRoll.Success)
                    {
                        currentSkillValue = diceRoll.Total;
                        lastSkillValue = currentSkillValue;
                        currentSkillName = lastSkillName;
                    }
                    else
                    {
                        // 无法解析，使用上次值
                        currentSkillName = lastSkillName;
                        currentSkillValue = lastSkillValue;
                    }
                }
                else
                {
                    // 无法解析，使用上次值
                    currentSkillName = lastSkillName;
                    currentSkillValue = lastSkillValue;
                }
            }
        }
        else
        {
            // 没有技能名，检查是否有纯数值（.cc30 的情况）
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out int pureValue))
            {
                // 直接使用提供的数值作为检定目标
                currentSkillValue = pureValue;
                currentSkillName = "直接";
                lastSkillValue = currentSkillValue;
                lastSkillName = currentSkillName;
            }
            else
            {
                // 没有技能和数值，使用上次的值
                currentSkillName = lastSkillName;
                currentSkillValue = lastSkillValue;
            }
        }

        var effectiveSubCmds = (subCmds != null && subCmds.Count > 0) ? subCmds : lastSubCmds;
        if (effectiveSubCmds.Count > 0)
        {
            lastSubCmds = new List<string>(effectiveSubCmds);
        }

        int bonusDice = 0;
        int vDelta = 0;
        bool hasAdjust = false;
        var vDetails = new List<string>();
        int supplementDice = 0;
        foreach (var cmd in effectiveSubCmds)
        {
            if (cmd.StartsWith(".p", StringComparison.OrdinalIgnoreCase)) bonusDice -= ParseDiceCount(cmd);
            else if (cmd.StartsWith(".b", StringComparison.OrdinalIgnoreCase)) bonusDice += ParseDiceCount(cmd);
            else if (cmd.StartsWith(".v", StringComparison.OrdinalIgnoreCase))
            {
                var expr = cmd.Substring(2);
                if (string.IsNullOrWhiteSpace(expr)) expr = "0";

                int sign = 1;
                if (expr.StartsWith("+"))
                {
                    expr = expr.Substring(1);
                }
                else if (expr.StartsWith("-"))
                {
                    sign = -1;
                    expr = expr.Substring(1);
                }

                if (string.IsNullOrWhiteSpace(expr)) expr = "0";

                var calc = Dice.CalculateExpression(expr);
                if (calc.Success)
                {
                    int delta = sign * calc.Total;
                    vDelta += delta;
                    hasAdjust = true;
                    vDetails.Add($".v {cmd.Substring(2)} = {(delta >= 0 ? "+" : "")}{delta} (明细: {calc.Detail})");
                }
            }
            else if (cmd.StartsWith(".a", StringComparison.OrdinalIgnoreCase))
            {
                supplementDice = ParseDiceCount(cmd);
            }
        }

        // v 调整
        int baseSkillValue = currentSkillValue;
        int adjustedSkillValue = currentSkillValue + vDelta;
        if (hasAdjust)
        {
            adjustedSkillValue = Math.Clamp(adjustedSkillValue, 0, 9999);
        }
        // 奖励/惩罚骰：多投 d20，取最佳或最差
        int rollCount = 1 + Math.Abs(bonusDice);
        var d20Rolls = new List<int>();
        for (int i = 0; i < rollCount; i++)
        {
            var r = Dice.Roll("1d20");
            if (!r.Success) return (SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["DiceRollError"], r.Detail), "");
            d20Rolls.Add(r.Total);
        }

        int chosenIdx = 0;
        if (bonusDice < 0)
        {
            // 取结果最差：检定数值最低
            chosenIdx = d20Rolls
                .Select((val, idx) => new { idx, score = CalculateETCheckValue(val, adjustedSkillValue) })
                .OrderBy(x => x.score)
                .First().idx;
        }
        else if (bonusDice > 0)
        {
            // 取结果最好：检定数值最高
            chosenIdx = d20Rolls
                .Select((val, idx) => new { idx, score = CalculateETCheckValue(val, adjustedSkillValue) })
                .OrderByDescending(x => x.score)
                .First().idx;
        }

        int rollValue = d20Rolls[chosenIdx];

        // 追补骰 .a
        string supplementDetail = string.Empty;

        string calculationFormula = string.Empty;
        if (supplementDice > 0 && adjustedSkillValue > 20)
        {
            supplementDice = Math.Min(supplementDice, (adjustedSkillValue - 20) / 5);// 每5点技能数值加1增补骰，限制到可能的最大值
            var sup = Dice.Roll($"{supplementDice}d10");
            int supplementSum = sup.Total;
            rollValue += supplementSum;
            calculationFormula = $"{rollValue}[a:{supplementSum}]";
        }
        else calculationFormula = $"{rollValue}";
        string mainpart = string.Empty;
        if (bonusDice > 0)
            mainpart = $"B{Math.Abs(bonusDice)}[{string.Join(", ", d20Rolls)}] ={calculationFormula}/{adjustedSkillValue}";
        else if (bonusDice < 0)
            mainpart = $"P{Math.Abs(bonusDice)}[{string.Join(", ", d20Rolls)}] ={calculationFormula}/{adjustedSkillValue}";
        else
            mainpart = $"D20 ={calculationFormula}/{adjustedSkillValue}";
        string etResult = DetermineETResult(rollValue, adjustedSkillValue);
        int checkValue = CalculateETCheckValue(rollValue, adjustedSkillValue, etResult);



        if (etResult == "大成功" || etResult == "成功")
            calculationFormula += $"+ {adjustedSkillValue}/2 =";
        else calculationFormula = $"{adjustedSkillValue}/2 =";

        string detail = SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["ETCheckResult"], currentSkillName, mainpart, etResult, calculationFormula, checkValue.ToString());

        // 根据检定结果获取个性化文本
        string exmessageKey = etResult switch
        {
            "大成功" => "ETExMessageCriticalSuccess",
            "成功" => "ETExMessageSuccess",
            "拙劣" => "ETExMessageFailure",
            "大失败" => "ETExMessageCriticalFailure",
            _ => "ETExMessageFailure"
        };
        string exmessage = GlobalFeedbackMessages.FeedbackTemplates[exmessageKey];

        return (detail, exmessage);
    }

    private string DetermineETResult(int roll, int skillValue)
    {
        if (roll == 1) return "大失败";
        else if (roll == skillValue) return "大成功";
        else if (roll < skillValue) return "成功";
        else return "拙劣";
    }

    private int CalculateETCheckValue(int roll, int skillValue, string? result = null)
    {
        if (result == null) result = DetermineETResult(roll, skillValue);
        int baseValue = skillValue / 2;
        if (result == "大成功" || result == "成功")
        {
            return baseValue + roll;
        }
        else
        {
            return baseValue;
        }
    }

    private int ParseDiceCount(string cmd)
    {
        var m = Regex.Match(cmd, @"(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int val))
        {
            return Math.Clamp(val, 1, 9);
        }

        return 1;
    }
}
