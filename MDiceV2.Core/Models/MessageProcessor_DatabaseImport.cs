namespace MDiceV2.Models;

public partial class MessageProcessor
{
    private DatabaseImportCoordinator? _databaseImportCoordinator;

    private DatabaseImportCoordinator GetDatabaseImportCoordinator()
    {
        if (MessageDistribution == null)
        {
            throw new InvalidOperationException("MessageDistribution is not initialized");
        }

        return _databaseImportCoordinator ??= new DatabaseImportCoordinator(MessageDistribution, () => DataIO);
    }

    private void InitializeDatabaseImportCoordinator()
    {
        if (MessageDistribution == null)
        {
            return;
        }

        _ = GetDatabaseImportCoordinator();
        Log.Normal("[数据库导入] 文件接收通道已初始化");
    }

    private void HandleDatabaseImportCommand(Msg msg)
    {
        if (!msg.IsMasterAccount)
        {
            Reply("❌ 仅 Master 账号可以导入数据库。", msg);
            return;
        }

        var result = GetDatabaseImportCoordinator().Prepare(msg.UserId);
        Reply(result.Message, msg);
    }
}
