using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MDiceV2.Models;

public partial class MessageProcessor
{
    private enum RollPickMode
    {
        None,
        Bonus,
        Penalty
    }

    private bool TryParseRollCommandPrefixes(
        string input,
        out int repeatCount,
        out RollPickMode pickMode,
        out int pickCount,
        out string remaining)
    {
        repeatCount = 1;
        pickMode = RollPickMode.None;
        pickCount = 2;
        remaining = (input ?? string.Empty).Trim();

        while (!string.IsNullOrWhiteSpace(remaining))
        {
            string trimmed = remaining.TrimStart();
            if (!string.Equals(trimmed, remaining, StringComparison.Ordinal))
            {
                remaining = trimmed;
                continue;
            }

            if (TryConsumeRepeatPrefix(remaining, out int parsedRepeatCount, out string afterRepeat))
            {
                repeatCount = parsedRepeatCount;
                remaining = afterRepeat;
                continue;
            }

            if (TryConsumePickModePrefix(remaining, out RollPickMode parsedMode, out int parsedPickCount, out string afterPickMode))
            {
                pickMode = parsedMode;
                pickCount = parsedPickCount;
                remaining = afterPickMode;
                continue;
            }

            if (remaining[0] == '#')
            {
                remaining = remaining[1..];
                continue;
            }

            break;
        }

        remaining = remaining.TrimStart();
        return true;
    }

    private void HandleRollPickMode(
        int repeatCount,
        RollPickMode mode,
        int pickCount,
        string remaining,
        Msg msg,
        bool isHiddenMode)
    {
        if (!TryNormalizeRollPickExpression(remaining, out string expression, out string extraContent, out string? errorMessage))
        {
            Reply(errorMessage ?? GlobalFeedbackMessages.FeedbackTemplates["RollPickModeFormatError"], msg);
            return;
        }

        const int userDefaultDice = 100;
        string rollResultText = string.Empty;
        for (int i = 0; i < Math.Clamp(repeatCount, 1, 9); i++)
        {
            if (!TryRollWithPickMode(expression, mode, pickCount, userDefaultDice, out var pickedDetail, out _, out string failureDetail))
            {
                Reply(failureDetail, msg);
                return;
            }

            rollResultText += "\n" + pickedDetail;
        }

        string refinedRollTemplate = RefineMsg(GlobalFeedbackMessages.FeedbackTemplates["RollResult"], msg);
        string finalReply = SafeFormatString(refinedRollTemplate, rollResultText, extraContent);
        SendRollReply(finalReply, msg, isHiddenMode);
    }

    private bool TryRollWithPickMode(
        string expression,
        RollPickMode mode,
        int pickCount,
        int userDefaultDice,
        out string detail,
        out int chosenValue,
        out string failureDetail)
    {
        detail = string.Empty;
        chosenValue = 0;
        failureDetail = string.Empty;
        pickCount = Math.Clamp(pickCount, 1, 9);

        List<(int value, string detail)> candidates = new();
        for (int i = 0; i < pickCount; i++)
        {
            var rollResult = Dice.CalculateExpression(expression, userDefaultDice);
            if (!rollResult.Success)
            {
                failureDetail = rollResult.Detail;
                return false;
            }

            candidates.Add((rollResult.Total, rollResult.Detail));
        }

        var chosen = candidates[0];
        for (int i = 1; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (mode == RollPickMode.Bonus)
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

        string modeLabel = mode == RollPickMode.Bonus ? "奖励骰" : "惩罚骰";
        string pickLabel = mode == RollPickMode.Bonus ? "取高" : "取低";
        string allDetails = string.Join(
            "\n",
            candidates.Select((candidate, index) => $"{index + 1}) {candidate.detail} → {candidate.value}"));

        chosenValue = chosen.value;
        detail = $"{modeLabel}{pickCount}次：{allDetails}，{pickLabel} {chosen.value}";
        return true;
    }

    private bool TryNormalizeRollPickExpression(
        string rest,
        out string expression,
        out string extraContent,
        out string? errorMessage)
    {
        expression = string.Empty;
        extraContent = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rest))
        {
            expression = "d20";
            return true;
        }

        SplitRollExpressionAndExtraContent(rest, out expression, out extraContent);
        if (string.IsNullOrWhiteSpace(expression))
        {
            errorMessage = GlobalFeedbackMessages.FeedbackTemplates["RollPickModeFormatError"];
            return false;
        }

        if (Regex.IsMatch(expression, @"^\d+$") || Regex.IsMatch(expression, @"^[+-]\d+$"))
        {
            errorMessage = GlobalFeedbackMessages.FeedbackTemplates["RollPickModeExplicitDiceRequired"];
            return false;
        }

        return true;
    }

    private static bool TryConsumeRepeatPrefix(string input, out int repeatCount, out string remaining)
    {
        repeatCount = 1;
        remaining = input;

        if (string.IsNullOrWhiteSpace(input) || input.Length < 2)
        {
            return false;
        }

        if (!char.IsDigit(input[0]) || input[1] != '#')
        {
            return false;
        }

        int parsedRepeatCount = input[0] - '0';
        if (parsedRepeatCount < 1 || parsedRepeatCount > 9)
        {
            return false;
        }

        repeatCount = parsedRepeatCount;
        remaining = input[2..];
        return true;
    }

    private static bool TryConsumePickModePrefix(string input, out RollPickMode mode, out int pickCount, out string remaining)
    {
        mode = RollPickMode.None;
        pickCount = 2;
        remaining = input;

        if (string.IsNullOrWhiteSpace(input) || !input.StartsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var match = Regex.Match(input, @"^\.(?<mode>[bp])(?<count>\d*)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        mode = string.Equals(match.Groups["mode"].Value, "b", StringComparison.OrdinalIgnoreCase)
            ? RollPickMode.Bonus
            : RollPickMode.Penalty;

        var countText = match.Groups["count"].Value;
        if (!string.IsNullOrWhiteSpace(countText) && int.TryParse(countText, out int parsedCount))
        {
            pickCount = Math.Clamp(parsedCount, 1, 9);
        }

        remaining = input[match.Length..];
        return true;
    }

    private void SplitRollExpressionAndExtraContent(string input, out string expression, out string extraContent)
    {
        expression = string.Empty;
        extraContent = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        string trimmedInput = input.Trim();
        int exprLength = trimmedInput.Length;
        for (int i = 0; i < trimmedInput.Length; i++)
        {
            char c = trimmedInput[i];
            if (c == ' ' || (c >= '\u4E00' && c <= '\u9FFF'))
            {
                exprLength = i;
                break;
            }
        }

        expression = trimmedInput[..exprLength].Trim();
        extraContent = exprLength < trimmedInput.Length ? trimmedInput[exprLength..].Trim() : string.Empty;
    }

    private void SendRollReply(string finalReply, Msg msg, bool isHiddenMode)
    {
        if (isHiddenMode)
        {
            if (msg.Source == MessageSource.group)
            {
                string refinedPublicTemplate = RefineMsg(GlobalFeedbackMessages.FeedbackTemplates["HiddenRollPublic"], msg);
                Reply(refinedPublicTemplate, msg);
            }

            string refinedHiddenTemplate = RefineMsg(GlobalFeedbackMessages.FeedbackTemplates["HiddenRollPrivatePrefix"], msg);
            string hiddenReply = SafeFormatString(refinedHiddenTemplate, finalReply);

            if (msg.IsSimulationMode)
            {
                Reply(hiddenReply, msg);
            }
            else if (MessageDistribution != null)
            {
                if (MessageDistribution.WSconnection.IsWsConnected)
                {
                    MessageDistribution.WSconnection.SendPrivateMessage(msg.UserId, hiddenReply);
                }
                else
                {
                    Log.Error("未知错误，WebSocket 未连接，无法发送私聊消息。");
                }
            }
            else
            {
                Log.Error("暗骰发送失败：MessageDistribution为空");
            }

            return;
        }

        Reply(finalReply, msg);
    }
}
