using MDiceV2.Models.CharacterCards;

namespace MDiceV2.Models;

public sealed record DatabaseImportCommandResult(bool Success, string Message);

/// <summary>
/// Receives a master-uploaded MDiceV2.db and applies its BinaryJsonData only after
/// an explicit focused y/n confirmation. Private files are preferred, while a group
/// file from the same master is accepted as the OneBot 11 fallback.
/// </summary>
public sealed class DatabaseImportCoordinator : IDisposable
{
    public const string ExpectedFileName = "MDiceV2.db";
    public const string FocusPrefix = "database_import:";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly MessageDistribution _messageDistribution;
    private readonly Func<DataIO?> _getTargetData;
    private readonly OneBotFileContentResolver _fileResolver;
    private readonly BinaryJsonDataImportService _importService;
    private readonly object _gate = new();
    private PendingImport? _pending;
    private bool _disposed;

    public DatabaseImportCoordinator(
        MessageDistribution messageDistribution,
        Func<DataIO?> getTargetData,
        OneBotFileContentResolver? fileResolver = null,
        BinaryJsonDataImportService? importService = null)
    {
        _messageDistribution = messageDistribution;
        _getTargetData = getTargetData;
        _fileResolver = fileResolver ?? new OneBotFileContentResolver(messageDistribution);
        _importService = importService ?? new BinaryJsonDataImportService();
        _messageDistribution.OnFileMessage += OnFileMessage;
        _messageDistribution.OnDatabaseImportConfirmation += OnConfirmation;
    }

    public DatabaseImportCommandResult Prepare(long masterUserId)
    {
        if (_getTargetData() == null)
        {
            return new DatabaseImportCommandResult(false, "数据库尚未初始化，暂时不能导入。");
        }

        lock (_gate)
        {
            ClearExpiredPendingUnsafe();
            if (_messageDistribution.GetUserFocus(masterUserId.ToString()) != null)
            {
                return new DatabaseImportCommandResult(false, "你当前有待确认操作，请先完成或取消后再导入数据库。");
            }

            if (_pending != null && _pending.UserId != masterUserId)
            {
                return new DatabaseImportCommandResult(false, "已有另一位 Master 正在导入数据库，请等待其完成或超时。");
            }

            _pending = new PendingImport(masterUserId, PendingState.WaitingForFile, DateTime.UtcNow, null, null);
            return new DatabaseImportCommandResult(
                true,
                $"请在 10 分钟内优先私聊发送文件 {ExpectedFileName}。\n" +
                "若当前 OneBot 11 不会上报私聊文件，请在群聊发送同名文件作为方案 B；仅本次发起导入的 Master 文件会被接受。\n" +
                "文件通过校验后，回复 y 才会写入并热加载；回复 n 取消。");
        }
    }

    private void OnFileMessage(OneBotFileInfo file)
    {
        if (_disposed || !string.Equals(file.FileName, ExpectedFileName, StringComparison.Ordinal))
        {
            return;
        }

        lock (_gate)
        {
            ClearExpiredPendingUnsafe();
            if (_pending == null || _pending.State != PendingState.WaitingForFile || file.UserId != _pending.UserId)
            {
                return;
            }

            _pending = _pending with { State = PendingState.Validating, UpdatedAt = DateTime.UtcNow };
        }

        _ = ValidateFileAsync(file);
    }

    private async Task ValidateFileAsync(OneBotFileInfo file)
    {
        try
        {
            var resolved = await _fileResolver.ResolveAsync(file, CancellationToken.None);
            if (!resolved.Success || resolved.Content == null)
            {
                ResetToWaitingForFile(file.UserId);
                Send(file, $"❌ 无法读取 {ExpectedFileName}：{resolved.ErrorMessage}");
                return;
            }

            var targetData = _getTargetData();
            if (targetData == null)
            {
                Cancel(file.UserId);
                Send(file, "❌ 数据库尚未初始化，导入已取消。");
                return;
            }

            var preview = _importService.TryCreatePlan(resolved.Content, targetData, out var plan);
            if (!preview.Success || plan == null)
            {
                ResetToWaitingForFile(file.UserId);
                Send(file, $"❌ {preview.Message}");
                return;
            }

            lock (_gate)
            {
                if (_pending == null || _pending.UserId != file.UserId || _pending.State != PendingState.Validating)
                {
                    return;
                }

                if (_messageDistribution.GetUserFocus(file.UserId.ToString()) != null)
                {
                    _pending = _pending with { State = PendingState.WaitingForFile, UpdatedAt = DateTime.UtcNow };
                    Send(file, "❌ 你已有待确认操作，暂时不能进入数据库导入确认。");
                    return;
                }

                _pending = _pending with { State = PendingState.WaitingForConfirmation, UpdatedAt = DateTime.UtcNow, Plan = plan, File = file };
                _messageDistribution.SetUserFocus(file.UserId.ToString(), FocusPrefix + file.UserId);
            }

            Send(file,
                $"检测到 {ExpectedFileName}，已通过 SQLite 与 JSON 格式校验。\n" +
                $"BinaryJsonData：{preview.SourceRowCount} 项；默认模板字段跳过：{preview.DefaultEntriesSkipped} 项；待写入：{preview.RowsWritten} 项。\n" +
                "回复 y 确认覆盖并热加载，回复 n 取消。确认前不会修改当前数据库。");
        }
        catch (Exception ex)
        {
            Log.Error($"[数据库导入] 验证上传文件失败: {ex}");
            ResetToWaitingForFile(file.UserId);
            Send(file, "❌ 校验上传数据库时发生错误，请重新发送文件。");
        }
    }

    private void OnConfirmation(Msg msg, string message)
    {
        PendingImport? pending;
        lock (_gate)
        {
            ClearExpiredPendingUnsafe();
            pending = _pending;
            if (pending == null || pending.UserId != msg.UserId || pending.State != PendingState.WaitingForConfirmation ||
                !string.Equals(_messageDistribution.GetUserFocus(msg.UserId.ToString()), FocusPrefix + msg.UserId, StringComparison.Ordinal))
            {
                return;
            }

            var answer = message.Trim();
            if (answer.Equals("n", StringComparison.OrdinalIgnoreCase) || answer.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                ClearPendingUnsafe();
                Send(msg, "已取消数据库导入，当前内容未修改。");
                return;
            }

            if (!answer.Equals("y", StringComparison.OrdinalIgnoreCase) && !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                Send(msg, "请回复 y 确认导入并热加载，或回复 n 取消。");
                return;
            }

            ClearPendingUnsafe();
        }

        _ = ApplyAsync(msg, pending!);
    }

    private async Task ApplyAsync(Msg msg, PendingImport pending)
    {
        try
        {
            var targetData = _getTargetData();
            if (targetData == null || pending.Plan == null)
            {
                Send(msg, "❌ 数据库未初始化，无法导入。");
                return;
            }

            var result = await Task.Run(() => _importService.Apply(pending.Plan, targetData));
            Send(msg, result.Success
                ? $"✅ {result.Message}\n读取：{result.SourceRowCount} 项；跳过默认字段：{result.DefaultEntriesSkipped} 项；实际写入：{result.RowsWritten} 项。"
                : $"❌ {result.Message}");
        }
        catch (Exception ex)
        {
            Log.Error($"[数据库导入] 应用异常: {ex}");
            Send(msg, "❌ 导入时发生异常，请查看日志。");
        }
    }

    private void ResetToWaitingForFile(long userId)
    {
        lock (_gate)
        {
            if (_pending?.UserId == userId)
            {
                _pending = _pending with { State = PendingState.WaitingForFile, UpdatedAt = DateTime.UtcNow, Plan = null, File = null };
            }
        }
    }

    private void Cancel(long userId)
    {
        lock (_gate)
        {
            if (_pending?.UserId == userId)
            {
                ClearPendingUnsafe();
            }
        }
    }

    private void ClearExpiredPendingUnsafe()
    {
        if (_pending != null && DateTime.UtcNow - _pending.UpdatedAt > Lifetime)
        {
            ClearPendingUnsafe();
        }
    }

    private void ClearPendingUnsafe()
    {
        if (_pending != null &&
            string.Equals(_messageDistribution.GetUserFocus(_pending.UserId.ToString()), FocusPrefix + _pending.UserId, StringComparison.Ordinal))
        {
            _messageDistribution.ClearUserFocus(_pending.UserId.ToString());
        }

        _pending = null;
    }

    private void Send(OneBotFileInfo file, string text)
    {
        if (file.GroupId > 0)
            _messageDistribution.WSconnection.SendGroupMessage(file.GroupId, text);
        else
            _messageDistribution.WSconnection.SendPrivateMessage(file.UserId, text);
    }

    private void Send(Msg msg, string text)
    {
        if (msg.Source == MessageSource.group)
            _messageDistribution.WSconnection.SendGroupMessage(msg.GroupId, text);
        else
            _messageDistribution.WSconnection.SendPrivateMessage(msg.UserId, text);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) ClearPendingUnsafe();
        _messageDistribution.OnFileMessage -= OnFileMessage;
        _messageDistribution.OnDatabaseImportConfirmation -= OnConfirmation;
        _fileResolver.Dispose();
    }

    private enum PendingState { WaitingForFile, Validating, WaitingForConfirmation }

    private sealed record PendingImport(
        long UserId,
        PendingState State,
        DateTime UpdatedAt,
        BinaryJsonDataImportPlan? Plan,
        OneBotFileInfo? File);
}
