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

        MessageDistribution.WSconnection.UploadGroupFile(msg.GroupId, path, Path.GetFileName(path));
        Reply("正在上传最新版 CoC 人物卡文件到群聊。", msg);
    }

    private async Task TriggerCocCardUpdateAsync(Msg msg)
    {
        var result = await GetCocCardUpdateService().UpdateAsync();
        Reply(result.Success ? $"✅ {result.Message}" : $"❌ {result.Message}", msg);
    }
}
