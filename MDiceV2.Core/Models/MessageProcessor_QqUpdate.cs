using System;
using System.Linq;
using System.Threading.Tasks;

#nullable enable
namespace MDiceV2.Models;

public partial class MessageProcessor
{
    private QqUpdatePackageReceiver? _qqUpdatePackageReceiver;

    private QqUpdatePackageReceiver GetQqUpdatePackageReceiver()
    {
        return _qqUpdatePackageReceiver ??= new QqUpdatePackageReceiver(
            () => MessageDistribution?.WSconnection,
            message => Log.Normal(message));
    }

    private void InitializeQqUpdatePackageReceiver()
    {
        if (MessageDistribution == null)
        {
            return;
        }

        _ = GetQqUpdatePackageReceiver();
        MessageDistribution.OnFileMessage -= HandleQqUpdateFileReceived;
        MessageDistribution.OnFileMessage += HandleQqUpdateFileReceived;
        MessageDistribution.OnQqUpdateConfirmation -= HandleQqUpdateConfirmation;
        MessageDistribution.OnQqUpdateConfirmation += HandleQqUpdateConfirmation;
        Log.Normal("[QQ更新包] 文件接收通道已初始化");
    }

    private void HandleQqUpdateFileReceived(OneBotFileInfo fileInfo)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var receiver = GetQqUpdatePackageReceiver();
                var result = await receiver.OnFileReceivedAsync(fileInfo);
                if (!result.ShouldReply || result.ReplyUserId <= 0 || string.IsNullOrWhiteSpace(result.Message))
                {
                    return;
                }

                if (MessageDistribution?.WSconnection?.IsWsConnected == true)
                {
                    if (fileInfo.GroupId > 0 && receiver.IsReadyForGroupConfirmation(fileInfo.UserId, fileInfo.GroupId))
                    {
                        MessageDistribution.SetUserFocus(fileInfo.UserId.ToString(), "qq_update_confirm");
                        MessageDistribution.WSconnection.SendGroupMessage(
                            fileInfo.GroupId,
                            result.Message + "\n\nReply y in this group to install, or n to cancel.");
                    }
                    else
                    {
                        MessageDistribution.WSconnection.SendPrivateMessage(result.ReplyUserId, result.Message);
                    }
                }
                else
                {
                    Log.Warn("[QQ更新包] WebSocket 未连接，无法回复文件接收结果");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[QQ更新包] 处理文件事件失败: {ex.Message}");
            }
        });
    }

    private void HandleQqUpdateCommand(string args, Msg msg)
    {
        var receiver = GetQqUpdatePackageReceiver();
        var tail = (args ?? string.Empty).Trim();
        if (tail.StartsWith("qq", StringComparison.OrdinalIgnoreCase))
        {
            tail = tail.Length == 2 ? string.Empty : tail[2..].Trim();
        }

        if (string.IsNullOrWhiteSpace(tail))
        {
            ReplyQqUpdateUsage(msg);
            return;
        }

        var parts = tail.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "prepare":
                if (!EnsurePrivateQqUpdateCommand(msg))
                {
                    return;
                }

                if (parts.Length == 1)
                {
                    var trustPrepareResult = receiver.PrepareTrust(msg.UserId);
                    Reply(trustPrepareResult.Message, msg);
                    return;
                }

                if (parts.Length < 3 || parts.Length > 4)
                {
                    ReplyQqUpdateUsage(msg);
                    return;
                }

                long? expectedSize = null;
                if (parts.Length == 4)
                {
                    if (!TryParseSize(parts[3], out var parsedSize))
                    {
                        Reply("size 格式无效，请使用正整数 byte 数，或 100mb / 200mb 这类后缀。", msg);
                        return;
                    }

                    expectedSize = parsedSize;
                }

                if (parts.Length == 4 && expectedSize == null)
                {
                    Reply("size 格式无效，请使用正整数 byte 数，或 100mb / 200mb 这类后缀。", msg);
                    return;
                }

                var prepareResult = receiver.PrepareStrict(
                    parts[1],
                    parts[2],
                    expectedSize,
                    msg.UserId);
                Reply(prepareResult.Message, msg);
                return;

            case "group":
                if (msg.Source != MessageSource.group || msg.GroupId <= 0)
                {
                    Reply("This command must be sent by Master in the target group.", msg);
                    return;
                }

                if (!msg.IsMasterAccount)
                {
                    Reply("❌ Only the configured Master account can start a group update upload.", msg);
                    return;
                }

                if (parts.Length != 1)
                {
                    Reply("Usage: #update qq group", msg);
                    return;
                }

                Reply(receiver.PrepareGroupTrust(msg.UserId, msg.GroupId).Message, msg);
                return;

            case "status":
                Reply(receiver.GetStatusText(), msg);
                return;

            case "cancel":
                if (!EnsurePrivateQqUpdateCommand(msg))
                {
                    return;
                }

                Reply(receiver.Cancel().Message, msg);
                return;

            case "clear":
                if (!EnsurePrivateQqUpdateCommand(msg))
                {
                    return;
                }

                Reply(receiver.Clear().Message, msg);
                return;

            case "apply":
                if (!EnsurePrivateQqUpdateCommand(msg))
                {
                    return;
                }

                Reply("⏳ 已收到二次确认，正在校验本地更新包并准备更新脚本...", msg);
                TriggerQqUpdateApplyAsync(msg).Wait();
                return;

            default:
                ReplyQqUpdateUsage(msg);
                return;
        }
    }

    private void HandleManualGroupUpdateCommand(Msg msg)
    {
        if (msg.Source != MessageSource.group || msg.GroupId <= 0)
        {
            Reply("❌ #update manually 必须由 Master 在目标群聊中发送。", msg);
            return;
        }

        if (!msg.IsMasterAccount)
        {
            Reply("❌ 仅配置的 Master 账号可以发起群聊手动更新。", msg);
            return;
        }

        var result = GetQqUpdatePackageReceiver().PrepareGroupTrust(msg.UserId, msg.GroupId);
        Reply(result.Message, msg);
    }

    private void HandleQqUpdateConfirmation(Msg msg, string input)
    {
        var answer = (input ?? string.Empty).Trim().ToLowerInvariant();
        var receiver = GetQqUpdatePackageReceiver();

        if (answer is "n" or "no")
        {
            MessageDistribution?.ClearUserFocus(msg.UserId.ToString());
            Reply(receiver.Cancel().Message, msg);
            return;
        }

        if (answer is not ("y" or "yes"))
        {
            Reply("Please reply y to install the verified update package, or n to cancel.", msg);
            return;
        }

        var confirmation = receiver.ConfirmGroupApply(msg.UserId, msg.GroupId);
        if (!confirmation.Success)
        {
            MessageDistribution?.ClearUserFocus(msg.UserId.ToString());
            Reply($"❌ {confirmation.Message}", msg);
            return;
        }

        MessageDistribution?.ClearUserFocus(msg.UserId.ToString());
        Reply("⏳ Confirmation received. Preparing the local update package...", msg);
        TriggerQqUpdateApplyAsync(msg).Wait();
    }

    private async Task TriggerQqUpdateApplyAsync(Msg msg)
    {
        try
        {
            var receiver = GetQqUpdatePackageReceiver();
            void Logger(string message)
            {
                Log.Normal($"[QQ更新包/应用] {message}");
            }

            var result = await receiver.ApplyAsync(Logger);
            if (result.Success)
            {
                Reply($"✅ {result.Message}", msg);
            }
            else
            {
                Reply($"❌ QQ 更新包应用失败：{result.Message}", msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[QQ更新包] apply 失败: {ex.Message}");
            Reply($"❌ QQ 更新包应用失败：{ex.Message}", msg);
        }
    }

    private bool EnsurePrivateQqUpdateCommand(Msg msg)
    {
        if (msg.Source == MessageSource.privatechat)
        {
            return true;
        }

        Reply("为安全起见，请私聊机器人执行 QQ 更新包命令。", msg);
        return false;
    }

    private void ReplyQqUpdateUsage(Msg msg)
    {
        Reply(
            "QQ 更新包命令：\n" +
            "便捷模式：\n" +
            "#update qq prepare\n" +
            "发送文件\n" +
            "#update qq apply\n\n" +
            "严格模式：\n" +
            "#update qq prepare <version> <sha256> [size]\n" +
            "发送文件\n" +
            "#update qq apply\n\n" +
            "#update qq status\n" +
            "#update qq apply\n" +
            "#update qq cancel\n" +
            "#update qq clear\n\n" +
            "说明：便捷模式信赖 Master 私聊文件，不需要手动输入 version 和 sha256；程序仍会计算 SHA256 用于日志和状态展示。",
            msg);
    }

    private static bool TryParseSize(string value, out long size)
    {
        size = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().ToLowerInvariant();
        long multiplier = 1;
        foreach (var (suffix, factor) in new[] { ("gb", 1024L * 1024 * 1024), ("mb", 1024L * 1024), ("kb", 1024L) })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                multiplier = factor;
                text = text[..^suffix.Length].Trim();
                break;
            }
        }

        if (!long.TryParse(text, out var parsed) || parsed <= 0)
        {
            return false;
        }

        size = parsed * multiplier;
        return size > 0;
    }
}
