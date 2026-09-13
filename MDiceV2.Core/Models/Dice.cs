using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MDiceV2.Models;

/// <summary>
/// 掷骰结果类
/// 表示一次掷骰的结果
/// </summary>
public class DiceResult
{
    /// <summary>
    /// 每次掷骰的点数列表
    /// </summary>
    public List<int> Rolls { get; set; } = new();

    /// <summary>
    /// 掷骰结果总和
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// 掷骰结果的详细描述
    /// </summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// 掷骰是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 返回字符串表示
    /// </summary>
    /// <returns></returns>
    public override string ToString() => Detail;

    /// <summary>
    /// 返回整型结果
    /// </summary>
    /// <returns></returns>
    public int ToInt() => Total;
}

/// <summary>
/// 掷骰工具类
/// 提供各种掷骰和计算功能
/// </summary>
public static class Dice
{
    /// <summary>
    /// 解析如2d6、1d100、d20等表达式，返回DiceResult
    /// 当省略面数时，使用defaultSides作为补全值
    /// </summary>
    /// <param name="expr">掷骰表达式</param>
    /// <param name="defaultSides">默认骰子面数（当表达式中省略面数时使用），默认100</param>
    /// <returns>DiceResult对象</returns>
    public static DiceResult Roll(string expr, int defaultSides = 100)
    {
        expr = expr.Trim().ToLower();
        int num = 1, sides = defaultSides;

        try
        {
            var match = Regex.Match(expr, @"^(\d*)d(\d*)$");
            if (match.Success)
            {
                if (!string.IsNullOrEmpty(match.Groups[1].Value))
                    num = int.Parse(match.Groups[1].Value);
                
                // 当省略面数时，使用默认面数
                if (!string.IsNullOrEmpty(match.Groups[2].Value))
                    sides = int.Parse(match.Groups[2].Value);
                else
                    sides = defaultSides;
            }
            else if (expr.StartsWith("d") && expr.Length > 1)
            {
                // 处理 d<number> 或 d（无数字）
                string sidePart = expr.Substring(1);
                if (string.IsNullOrEmpty(sidePart))
                {
                    sides = defaultSides;
                }
                else if (int.TryParse(sidePart, out int s))
                {
                    sides = s;
                }
                else
                {
                    return new DiceResult
                    {
                        Rolls = new List<int>(),
                        Total = -1,
                        Detail = $"无效的骰子表达式: {expr}",
                        Success = false
                    };
                }
            }
            else if (int.TryParse(expr, out int s2))
            {
                sides = s2;
            }
            else
            {
                return new DiceResult
                {
                    Rolls = new List<int>(),
                    Total = -1,
                    Detail = $"无效的骰子表达式: {expr}",
                    Success = false
                };
            }

            if (num < 1 || num > 999 || sides < 2 || sides > 9999)
                return new DiceResult
                {
                    Rolls = new List<int>(),
                    Total = -1,
                    Detail = $"骰子数量(1-999)或面数(2-9999)超出范围: {num}d{sides}",
                    Success = false
                };

            int sum = 0;
            var rolls = new List<int>();
            for (int i = 0; i < num; i++)
            {
                int roll = GlobalRandom.Next(1, sides + 1);
                rolls.Add(roll);
                sum += roll;
            }

            // 当只有一个掷骰时，格式为 D{sides}={sum}
            string detail;
            if (num == 1)
            {
                detail = $"D{sides}={sum}";
            }
            else
            {
                detail = $"{num}d{sides} = [{string.Join(", ", rolls)}] = {sum}";
            }
            return new DiceResult { Rolls = rolls, Total = sum, Detail = detail, Success = true };
        }
        catch (Exception ex)
        {
            return new DiceResult
            {
                Rolls = new List<int>(),
                Total = -1,
                Detail = $"骰子表达式解析异常: {ex.Message}",
                Success = false
            };
        }
    }

    /// <summary>
    /// 计算掷骰表达式
    /// 流程：规范化表达式 = 掷骰替换后 = 最终结果
    /// 支持的表达式：2d6+3、d20*2、d+5 等
    /// </summary>
    /// <param name="expression">掷骰表达式</param>
    /// <param name="defaultSides">默认骰子面数，默认100</param>
    /// <returns>DiceResult对象</returns>
    public static DiceResult CalculateExpression(string expression, int defaultSides = 100)
    {
        expression = expression.Replace(" ", "").ToUpper(); // 移除空格，统一大小写
        
        // 检查非法字符（仅允许数字、D、+、-、*、/、()）
        if (!Regex.IsMatch(expression, @"^[\d+\-*/D()]*$"))
        {
            var invalidMatch = Regex.Match(expression, @"[^\d+\-*/D()]");
            return new DiceResult
            {
                Rolls = new List<int>(),
                Total = -1,
                Detail = $"表达式中包含非法字符: '{invalidMatch.Value}'",
                Success = false
            };
        }
        
        // ==== 第一部分：规范化表达式 ====
        // 补全缺失的掷骰参数：d -> 1D100, d20 -> 1D20, 2d -> 2D100
        var normalizedExpression = Regex.Replace(expression, @"(\d*)D(\d*)", match =>
        {
            string numPart = match.Groups[1].Value;
            string sidesPart = match.Groups[2].Value;
            
            int num = string.IsNullOrEmpty(numPart) ? 1 : int.Parse(numPart);
            int sides = string.IsNullOrEmpty(sidesPart) ? defaultSides : int.Parse(sidesPart);
            
            return $"{num}D{sides}";
        });
        
        var allRolls = new List<int>();
        var diceMatches = Regex.Matches(normalizedExpression, @"(\d+)D(\d+)", RegexOptions.IgnoreCase);
        
        // 如果没有掷骰表达式，进行纯算术计算
        if (diceMatches.Count == 0)
        {
            try
            {
                // 如果是纯数字，直接返回
                if (int.TryParse(normalizedExpression, out int simpleValue))
                {
                    return new DiceResult
                    {
                        Rolls = new List<int>(),
                        Total = simpleValue,
                        Detail = normalizedExpression,
                        Success = true
                    };
                }

                // 否则进行算术运算
                var noRollExpr = normalizedExpression;

                // 处理括号（从最内层开始）
                while (Regex.IsMatch(noRollExpr, @"\([^()]*\)"))
                {
                    noRollExpr = Regex.Replace(noRollExpr, @"\(([^()]*)\)", match =>
                    {
                        string innerExpr = match.Groups[1].Value;
                        int result = EvaluateSimpleExpression(innerExpr);
                        return result.ToString();
                    });
                }

                // 处理乘除运算
                while (Regex.IsMatch(noRollExpr, @"(\d+)([*/])(\d+)"))
                {
                    noRollExpr = Regex.Replace(noRollExpr, @"(\d+)([*/])(\d+)", match =>
                    {
                        int operand1 = int.Parse(match.Groups[1].Value);
                        string op = match.Groups[2].Value;
                        int operand2 = int.Parse(match.Groups[3].Value);
                        int result = ApplyOperator(operand1, operand2, op);
                        return result.ToString();
                    });
                }

                // 处理加减运算
                var noRollTokens = Regex.Matches(noRollExpr, @"([+\-]?\d+)").Cast<Match>().Select(m => m.Value).ToList();
                int noRollResult = 0;
                if (noRollTokens.Count > 0)
                {
                    noRollResult = int.Parse(noRollTokens[0]);
                    for (int i = 1; i < noRollTokens.Count; i++)
                    {
                        string token = noRollTokens[i];
                        if (token.StartsWith("+"))
                        {
                            noRollResult += int.Parse(token.Substring(1));
                        }
                        else if (token.StartsWith("-"))
                        {
                            noRollResult -= int.Parse(token.Substring(1));
                        }
                        else
                        {
                            noRollResult += int.Parse(token);
                        }
                    }
                }

                return new DiceResult
                {
                    Rolls = new List<int>(),
                    Total = noRollResult,
                    Detail = $"{normalizedExpression}={noRollResult}",
                    Success = true
                };
            }
            catch (Exception ex)
            {
                return new DiceResult
                {
                    Rolls = new List<int>(),
                    Total = -1,
                    Detail = $"计算表达式失败: {ex.Message}",
                    Success = false
                };
            }
        }
        
        // ==== 第二部分：掷骰替换 ====
        var afterDiceExpression = normalizedExpression;
        var diceReplacements = new List<(string original, string replacement)>();

        // 检查整个表达式是否只包含一个掷骰表达式（没有额外算数）
        bool isSingleDiceOnly = diceMatches.Count == 1 && normalizedExpression == diceMatches[0].Value;

        foreach (Match match in diceMatches)
        {
            var rollResult = Roll(match.Value, defaultSides);
            if (!rollResult.Success)
            {
                return rollResult;
            }
            allRolls.AddRange(rollResult.Rolls);

            // 解析掷骰表达式中的x值
            var diceMatch = Regex.Match(match.Value, @"(\d+)D(\d+)");
            int diceCount = int.Parse(diceMatch.Groups[1].Value);

            // 当x > 10时，直接返回和
            // 当掷骰结果只有一项时，不需要括号
            // 当只有一个掷骰项时，无括号；多个掷骰项时加括号
            string replacement;
            if (diceCount > 10)
            {
                replacement = rollResult.Total.ToString();
            }
            else if (rollResult.Rolls.Count == 1)
            {
                replacement = rollResult.Total.ToString();
            }
            else if (isSingleDiceOnly)
            {
                replacement = string.Join("+", rollResult.Rolls);
            }
            else
            {
                replacement = "(" + string.Join("+", rollResult.Rolls) + ")";
            }

            diceReplacements.Add((match.Value, replacement));
        }

        // 用掷骰结果替换掷骰表达式（按位置精确替换）
        var sb = new StringBuilder(afterDiceExpression);
        for (int i = diceReplacements.Count - 1; i >= 0; i--)
        {
            var (original, replacement) = diceReplacements[i];
            var match = diceMatches[i];
            sb.Remove(match.Index, match.Length);
            sb.Insert(match.Index, replacement);
        }
        afterDiceExpression = sb.ToString();
        
        // 如果掷骰替换后是纯数字，直接返回 1=2 格式
        if (int.TryParse(afterDiceExpression, out int singleValue))
        {
            return new DiceResult
            {
                Rolls = allRolls,
                Total = singleValue,
                Detail = $"{normalizedExpression}={afterDiceExpression}",
                Success = true
            };
        }
        
        // ==== 第三部分：算数运算 ====
        var currentExpression = afterDiceExpression;
        
        // 处理括号（从最内层开始）
        while (Regex.IsMatch(currentExpression, @"\([^()]*\)"))
        {
            currentExpression = Regex.Replace(currentExpression, @"\(([^()]*)\)", match =>
            {
                string innerExpr = match.Groups[1].Value;
                int result = EvaluateSimpleExpression(innerExpr);
                return result.ToString();
            });
        }
        
        // 处理乘除运算
        while (Regex.IsMatch(currentExpression, @"(\d+)([*/])(\d+)"))
        {
            currentExpression = Regex.Replace(currentExpression, @"(\d+)([*/])(\d+)", match =>
            {
                int operand1 = int.Parse(match.Groups[1].Value);
                string op = match.Groups[2].Value;
                int operand2 = int.Parse(match.Groups[3].Value);
                int result = ApplyOperator(operand1, operand2, op);
                return result.ToString();
            });
        }
        
        // 处理加减运算
        var finalTokens = Regex.Matches(currentExpression, @"([+\-]?\d+)").Cast<Match>().Select(m => m.Value).ToList();
        int finalResult = 0;
        if (finalTokens.Count > 0)
        {
            finalResult = int.Parse(finalTokens[0]);
            for (int i = 1; i < finalTokens.Count; i++)
            {
                string token = finalTokens[i];
                if (token.StartsWith("+"))
                {
                    finalResult += int.Parse(token.Substring(1));
                }
                else if (token.StartsWith("-"))
                {
                    finalResult -= int.Parse(token.Substring(1));
                }
                else
                {
                    finalResult += int.Parse(token);
                }
            }
        }
        
        return new DiceResult
        {
            Rolls = allRolls,
            Total = finalResult,
            Detail = $"{normalizedExpression}={afterDiceExpression}={finalResult}",
            Success = true
        };
    }

    /// <summary>
    /// 计算简单表达式（无括号），用于处理括号内的内容
    /// </summary>
    /// <param name="expression">不含括号的表达式</param>
    /// <returns>计算结果</returns>
    private static int EvaluateSimpleExpression(string expression)
    {
        var currentExpr = expression;
        
        // 处理乘除运算
        while (Regex.IsMatch(currentExpr, @"(\d+)([*/])(\d+)"))
        {
            currentExpr = Regex.Replace(currentExpr, @"(\d+)([*/])(\d+)", match =>
            {
                int operand1 = int.Parse(match.Groups[1].Value);
                string op = match.Groups[2].Value;
                int operand2 = int.Parse(match.Groups[3].Value);
                int result = ApplyOperator(operand1, operand2, op);
                return result.ToString();
            });
        }
        
        // 处理加减运算
        var tokens = Regex.Matches(currentExpr, @"([+\-]?\d+)").Cast<Match>().Select(m => m.Value).ToList();
        int result2 = 0;
        if (tokens.Count > 0)
        {
            result2 = int.Parse(tokens[0]);
            for (int i = 1; i < tokens.Count; i++)
            {
                string token = tokens[i];
                if (token.StartsWith("+"))
                {
                    result2 += int.Parse(token.Substring(1));
                }
                else if (token.StartsWith("-"))
                {
                    result2 -= int.Parse(token.Substring(1));
                }
                else
                {
                    result2 += int.Parse(token);
                }
            }
        }
        
        return result2;
    }

    /// <summary>
    /// 应用运算符
    /// </summary>
    /// <param name="operand1">操作数1</param>
    /// <param name="operand2">操作数2</param>
    /// <param name="op">运算符</param>
    /// <returns>计算结果</returns>
    private static int ApplyOperator(int operand1, int operand2, string op)
    {
        switch (op)
        {
            case "*":
                return operand1 * operand2;
            case "/":
                if (operand2 == 0) throw new DivideByZeroException("除数不能为零");
                return operand1 / operand2;
            default:
                throw new ArgumentException($"未知运算符: {op}");
        }
    }

    /// <summary>
    /// CoC7 规则辅助判定函数
    /// 判定优先级：
    /// 1. 大成功：roll ≤ 5 且 roll ≤ skillValue/5
    /// 2. 大失败：roll ≥ 96
    /// 3. 极限成功：roll ≤ skillValue/5 且 roll > 5
    /// 4. 困难成功：roll ≤ skillValue/2 且 roll > skillValue/5
    /// 5. 成功：roll ≤ skillValue 且 roll > skillValue/2
    /// 6. 失败：roll > skillValue
    /// </summary>
    /// <param name="roll">掷骰结果</param>
    /// <param name="skillValue">技能值</param>
    /// <returns>判定结果</returns>
    public static string CoC7_Check(int roll, int skillValue)
    {
        // 大成功：roll ≤ 5 且 roll ≤ skillValue/5
        if (roll <= 5 && roll <= skillValue / 5)
        {
            return "大成功";
        }
        
        // 大失败：roll ≥ 96
        if (roll >= 96)
        {
            return "大失败";
        }
        
        // 极限成功：roll ≤ skillValue/5 且 roll > 5
        if (roll <= skillValue / 5 && roll > 5)
        {
            return "极限成功";
        }
        
        // 困难成功：roll ≤ skillValue/2 且 roll > skillValue/5
        if (roll <= skillValue / 2 && roll > skillValue / 5)
        {
            return "困难成功";
        }
        
        // 成功：roll ≤ skillValue 且 roll > skillValue/2
        if (roll <= skillValue && roll > skillValue / 2)
        {
            return "成功";
        }
        
        // 失败：roll > skillValue
        return "失败";
    }
}