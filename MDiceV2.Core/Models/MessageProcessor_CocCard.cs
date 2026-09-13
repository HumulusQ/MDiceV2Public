namespace MDiceV2.Models;

public partial class MessageProcessor
{
    private CocCardUpdateService? _cocCardUpdateService;

    private CocCardUpdateService GetCocCardUpdateService() =>
        _cocCardUpdateService ??= new CocCardUpdateService(Log.Normal);

    private void HandleGetCommand(string args, Msg msg)
    {
        if (!args.Trim().Equals("coccard", StringComparison.OrdinalIgnoreCase))
        {
            Reply("用法：.get coccard", msg);
            return;
        }
        if (msg.Source != MessageSource.group || msg.GroupId <= 0)
        {
            Reply("请在需要获取人物卡的群聊中使用 .get coccard。", msg);
            return;
        }

        var path = GetCocCardUpdateService().LocalFilePath;
        if (!File.Exists(path))
        {
            Reply("人物卡文件尚未下载。请由 Master 先执行 #update coccard。", msg);
            return;
        }
        if (MessageDistribution?.WSconnection?.IsWsConnected != true)
        {
            Reply("WebSocket 未连接，暂时无法上传群文件。", msg);
            return;
        }

        Reply("正在上传最新版 CoC 人物卡文件到群聊。", msg);
        _ = UploadCocCardToGroupAsync(MessageDistribution.WSconnection, msg, path);
    }

    private async Task UploadCocCardToGroupAsync(WSconnection connection, Msg msg, string path)
    {
        try
        {
            var uploader = new CocCardGroupUploadService(
                (groupId, filePath, name) => connection.UploadGroupFileAsync(groupId, filePath, name));
            var result = await uploader.UploadAsync(msg.GroupId, path);

            if (result.Success)
            {
                if (result.UsedMdiceFallback)
                {
                    Log.Normal($"[CoC人物卡] HTML 上传失败，.mdice 回退上传成功: group={msg.GroupId}, file={result.FallbackFilePath}, htmlReason={result.HtmlAttempt.Message}");
                    Reply("✅ HTML 后缀上传失败，已改用 .mdice 后缀上传成功。下载后将扩展名改回 .html 即可使用。", msg);
                }
                else
                {
                    Log.Normal($"[CoC人物卡] HTML 上传成功: group={msg.GroupId}, file={path}");
                    Reply("✅ 最新版 CoC 人物卡文件已上传到群文件。", msg);
                }
                return;
            }

            var htmlReason = CocCardGroupUploadService.CompactFailureMessage(result.HtmlAttempt.Message);
            var mdiceReason = result.MdiceAttempt is not null
                ? CocCardGroupUploadService.CompactFailureMessage(result.MdiceAttempt.Message)
                : $"无法准备回退文件：{CocCardGroupUploadService.CompactFailureMessage(result.FallbackPreparationError)}";

            Log.Warn($"[CoC人物卡] HTML 与 .mdice 上传均失败: group={msg.GroupId}, htmlFile={path}, mdiceFile={result.FallbackFilePath}, htmlReason={result.HtmlAttempt.Message}, mdiceReason={result.MdiceAttempt?.Message ?? result.FallbackPreparationError}");
            Reply($"❌ CoC 人物卡文件上传失败。\nHTML：{htmlReason}\n.mdice：{mdiceReason}", msg);
        }
        catch (Exception ex)
        {
            Log.Error($"[CoC人物卡] 上传异常: group={msg.GroupId}, file={path}, error={ex}");
            Reply($"❌ CoC 人物卡文件上传异常：{ex.Message}", msg);
        }
    }

    private async Task TriggerCocCardUpdateAsync(Msg msg)
    {
        var result = await GetCocCardUpdateService().UpdateAsync();
        Reply(result.Success ? $"✅ {result.Message}" : $"❌ {result.Message}", msg);
    }
}
