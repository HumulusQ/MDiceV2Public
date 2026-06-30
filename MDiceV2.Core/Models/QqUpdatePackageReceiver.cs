using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

#nullable enable
namespace MDiceV2.Models;

public enum QqUpdateSessionState
{
    WaitingForFile,
    Downloading,
    ReadyToApply,
    Applying
}

public enum QqUpdateMode
{
    StrictHashMode,
    MasterPrivateTrustMode
}

public sealed class QqUpdateSession
{
    public QqUpdateMode Mode { get; set; } = QqUpdateMode.StrictHashMode;
    public string? ExpectedVersion { get; set; }
    public string? ExpectedSha256 { get; set; }
    public long? ExpectedSize { get; set; }
    public long RequestedByUserId { get; set; }
    public DateTime PreparedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadyAt { get; set; }
    public QqUpdateSessionState State { get; set; } = QqUpdateSessionState.WaitingForFile;
    public string? PackagePath { get; set; }
    public string? PackageFileName { get; set; }
    public long? ActualSize { get; set; }
    public string? ActualSha256 { get; set; }
    public string? LastError { get; set; }
    public string? ReceivedSourceKind { get; set; }
    public long? ReceivedFromUserId { get; set; }
    public long? ReceivedFromGroupId { get; set; }
    public DateTime? ReceivedAt { get; set; }
}

public sealed record QqUpdateCommandResult(bool Success, string Message);

public sealed record QqUpdateFileReceiveResult(bool ShouldReply, long ReplyUserId, string Message);

public sealed class QqUpdatePackageReceiver
{
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromHours(2);
    private static readonly string[] AllowedPackageNames =
    {
        "MDiceV2.Core.Zip",
        "MDiceV2.Core.Dice",
        "MDiceV2.Core.UpdatePackage"
    };

    private readonly object _sync = new();
    private readonly OneBotFileDownloadService _downloadService;
    private readonly Action<string> _logger;
    private QqUpdateSession? _session;

    public QqUpdatePackageReceiver(Func<WSconnection?> getConnection, Action<string>? logger = null)
    {
        _logger = logger ?? Log.Normal;
        _downloadService = new OneBotFileDownloadService(getConnection, _logger);
    }

    public QqUpdateCommandResult PrepareTrust(long requestedByUserId)
    {
        var versionLabel = $"QQPackage-{DateTime.Now:yyyyMMdd-HHmmss}";
        return PrepareSession(
            QqUpdateMode.MasterPrivateTrustMode,
            versionLabel,
            null,
            null,
            requestedByUserId,
            "✅ 已进入 QQ 更新包接收模式。\n" +
            $"模式：{QqUpdateMode.MasterPrivateTrustMode}\n" +
            $"版本标签：{versionLabel}\n" +
            $"发起者：{requestedByUserId}\n\n" +
            "请在 30 分钟内私聊发送 MDiceV2.Core.Zip / MDiceV2.Core.Dice / MDiceV2.Core.UpdatePackage。\n" +
            "此模式信赖 Master 私聊文件，不需要手动输入版本号或 SHA256。\n" +
            "收到文件后不会自动更新；确认应用请执行：#update qq apply");
    }

    public QqUpdateCommandResult PrepareStrict(string version, string sha256, long? expectedSize, long requestedByUserId)
    {
        version = (version ?? string.Empty).Trim();
        var normalizedSha = FileHashUtility.NormalizeSha256(sha256);

        if (string.IsNullOrWhiteSpace(version))
        {
            return Fail("版本号不能为空。格式：#update qq prepare <version> <sha256> [size]");
        }

        if (!Regex.IsMatch(normalizedSha, "^[a-f0-9]{64}$", RegexOptions.IgnoreCase))
        {
            return Fail("SHA256 格式无效，应为 64 位 hex 字符串。");
        }

        var sizeLine = expectedSize.HasValue ? $"\n预期大小：{expectedSize.Value} bytes" : string.Empty;
        return PrepareSession(
            QqUpdateMode.StrictHashMode,
            version,
            normalizedSha,
            expectedSize,
            requestedByUserId,
            "✅ QQ 更新包会话已准备。\n" +
            $"模式：{QqUpdateMode.StrictHashMode}\n" +
            $"版本：{version}\n" +
            $"SHA256：{normalizedSha}\n" +
            $"发起者：{requestedByUserId}{sizeLine}\n\n" +
            "请在 30 分钟内通过私聊向机器人发送更新包文件。\n" +
            "收到文件后只会暂存和校验，不会自动更新；确认应用请执行：#update qq apply");
    }

    public string GetStatusText()
    {
        ExpireIfNeeded();
        QqUpdateSession? snapshot;
        lock (_sync)
        {
            snapshot = CloneSession(_session);
        }

        if (snapshot == null)
        {
            return "当前没有待处理的 QQ 更新包会话。";
        }

        var stateText = snapshot.State switch
        {
            QqUpdateSessionState.WaitingForFile => "等待私聊文件",
            QqUpdateSessionState.Downloading => "正在下载/复制文件",
            QqUpdateSessionState.ReadyToApply => "已校验，等待 apply",
            QqUpdateSessionState.Applying => "正在应用更新",
            _ => snapshot.State.ToString()
        };

        var lines = new[]
        {
            "QQ 更新包状态：",
            $"模式：{snapshot.Mode}",
            $"状态：{stateText}",
            $"版本标签：{snapshot.ExpectedVersion ?? "未指定"}",
            snapshot.Mode == QqUpdateMode.StrictHashMode
                ? $"预期 SHA256：{snapshot.ExpectedSha256 ?? "未指定"}"
                : "预期 SHA256：未要求（MasterPrivateTrustMode）",
            $"发起者：{snapshot.RequestedByUserId}",
            $"准备时间：{snapshot.PreparedAt:yyyy-MM-dd HH:mm:ss} UTC",
            snapshot.ExpectedSize.HasValue ? $"预期大小：{snapshot.ExpectedSize.Value} bytes" : "预期大小：未指定",
            snapshot.ActualSize.HasValue ? $"实际大小：{snapshot.ActualSize.Value} bytes" : "实际大小：未接收",
            !string.IsNullOrWhiteSpace(snapshot.PackageFileName) ? $"文件名：{snapshot.PackageFileName}" : string.Empty,
            !string.IsNullOrWhiteSpace(snapshot.ActualSha256) ? $"实际 SHA256：{snapshot.ActualSha256}" : string.Empty,
            !string.IsNullOrWhiteSpace(snapshot.ReceivedSourceKind) ? $"接收来源：{snapshot.ReceivedSourceKind}" : string.Empty,
            snapshot.ReceivedFromUserId.HasValue ? $"来源 QQ：{snapshot.ReceivedFromUserId.Value}" : string.Empty,
            snapshot.ReceivedFromGroupId.HasValue && snapshot.ReceivedFromGroupId.Value != 0 ? $"来源群：{snapshot.ReceivedFromGroupId.Value}" : string.Empty,
            snapshot.ReceivedAt.HasValue ? $"接收时间：{snapshot.ReceivedAt.Value:yyyy-MM-dd HH:mm:ss} UTC" : string.Empty,
            !string.IsNullOrWhiteSpace(snapshot.PackagePath) ? $"暂存路径：{snapshot.PackagePath}" : string.Empty,
            !string.IsNullOrWhiteSpace(snapshot.LastError) ? $"上次错误：{snapshot.LastError}" : string.Empty
        };

        return string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    public QqUpdateCommandResult Cancel()
    {
        QqUpdateSession? oldSession;
        lock (_sync)
        {
            oldSession = _session;
            _session = null;
        }

        DeleteSessionPackageQuietly(oldSession);
        _logger("[QQ更新包] 会话已取消并清理暂存文件");
        return Ok("✅ 已取消当前 QQ 更新包会话，并清理已暂存文件。");
    }

    public QqUpdateCommandResult Clear()
    {
        QqUpdateSession? oldSession;
        lock (_sync)
        {
            oldSession = _session;
            _session = null;
        }

        DeleteSessionPackageQuietly(oldSession);
        _logger("[QQ更新包] 会话状态已清空");
        return Ok("✅ 已清空 QQ 更新包状态，并清理已暂存文件。");
    }

    public async Task<QqUpdateFileReceiveResult> OnFileReceivedAsync(OneBotFileInfo fileInfo)
    {
        ExpireIfNeeded();
        QqUpdateSession? snapshot;
        lock (_sync)
        {
            snapshot = CloneSession(_session);
        }

        if (snapshot == null)
        {
            _logger($"[QQ更新包] 收到文件但没有 pending 会话: source={fileInfo.SourceKind}, user={fileInfo.UserId}, group={fileInfo.GroupId}, file={fileInfo.FileName}");
            return NoReply();
        }

        if (fileInfo.UserId != snapshot.RequestedByUserId)
        {
            _logger($"[QQ更新包] 忽略非发起者文件: expected={snapshot.RequestedByUserId}, actual={fileInfo.UserId}, file={fileInfo.FileName}");
            return NoReply();
        }

        if (!fileInfo.IsPrivateMessage)
        {
            _logger($"[QQ更新包] 拒绝非私聊文件: user={fileInfo.UserId}, source={fileInfo.SourceKind}, file={fileInfo.FileName}");
            return ReplyTo(snapshot.RequestedByUserId, "当前 QQ 更新包会话只接受私聊文件，请改为私聊机器人发送更新包。");
        }

        if (snapshot.State == QqUpdateSessionState.ReadyToApply)
        {
            return ReplyTo(snapshot.RequestedByUserId, "当前已有一个已校验完成的更新包。如需重新上传，请先执行 #update qq cancel 或 #update qq clear。");
        }

        SetState(QqUpdateSessionState.Downloading, null);

        OneBotFileDownloadResult download;
        try
        {
            download = await _downloadService.DownloadAsync(fileInfo);
        }
        catch (Exception ex)
        {
            SetState(QqUpdateSessionState.WaitingForFile, ex.Message);
            return ReplyTo(snapshot.RequestedByUserId, $"❌ 下载更新包失败：{ex.Message}");
        }

        if (!download.Success || string.IsNullOrWhiteSpace(download.LocalPath))
        {
            SetState(QqUpdateSessionState.WaitingForFile, download.ErrorMessage);
            return ReplyTo(snapshot.RequestedByUserId, $"❌ 下载更新包失败：{download.ErrorMessage}");
        }

        try
        {
            var packageName = download.FileName;
            if (!IsAllowedPackageFileName(packageName))
            {
                DeleteFileQuietly(download.LocalPath);
                var message = $"更新包文件名不被允许：{packageName}\n仅支持：{string.Join(" / ", AllowedPackageNames)}";
                SetState(QqUpdateSessionState.WaitingForFile, message);
                return ReplyTo(snapshot.RequestedByUserId, $"❌ {message}");
            }

            if (snapshot.ExpectedSize.HasValue && download.FileSize > 0 && download.FileSize != snapshot.ExpectedSize.Value)
            {
                DeleteFileQuietly(download.LocalPath);
                var message = $"更新包大小不匹配。预期 {snapshot.ExpectedSize.Value} bytes，实际 {download.FileSize} bytes。";
                SetState(QqUpdateSessionState.WaitingForFile, message);
                return ReplyTo(snapshot.RequestedByUserId, $"❌ {message}");
            }

            ValidatePackageStructure(download.LocalPath, packageName);

            var actualSha = await FileHashUtility.ComputeSha256HexAsync(download.LocalPath);
            _logger($"[QQ更新包] 文件已暂存并计算 SHA256: mode={snapshot.Mode}, user={snapshot.RequestedByUserId}, file={packageName}, size={download.FileSize}, sha={actualSha}");

            if (snapshot.Mode == QqUpdateMode.StrictHashMode &&
                !string.Equals(actualSha, snapshot.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                DeleteFileQuietly(download.LocalPath);
                var message = $"SHA256 校验失败。\n预期：{snapshot.ExpectedSha256}\n实际：{actualSha}\n暂存文件已删除，请重新发送正确更新包。";
                SetState(QqUpdateSessionState.WaitingForFile, message);
                return ReplyTo(snapshot.RequestedByUserId, $"❌ {message}");
            }

            lock (_sync)
            {
                if (_session != null &&
                    _session.RequestedByUserId == snapshot.RequestedByUserId &&
                    _session.Mode == snapshot.Mode &&
                    string.Equals(_session.ExpectedVersion, snapshot.ExpectedVersion, StringComparison.Ordinal) &&
                    string.Equals(_session.ExpectedSha256, snapshot.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteSessionPackageQuietly(_session);
                    _session.PackagePath = download.LocalPath;
                    _session.PackageFileName = packageName;
                    _session.ActualSize = download.FileSize > 0 ? download.FileSize : null;
                    _session.ActualSha256 = actualSha;
                    _session.ReceivedSourceKind = fileInfo.SourceKind;
                    _session.ReceivedFromUserId = fileInfo.UserId;
                    _session.ReceivedFromGroupId = fileInfo.GroupId;
                    _session.ReceivedAt = DateTime.UtcNow;
                    _session.ReadyAt = DateTime.UtcNow;
                    _session.State = QqUpdateSessionState.ReadyToApply;
                    _session.LastError = null;
                }
                else
                {
                    DeleteFileQuietly(download.LocalPath);
                    _logger("[QQ更新包] 文件校验完成时会话已变更，已删除暂存文件");
                    return NoReply();
                }
            }

            if (snapshot.Mode == QqUpdateMode.MasterPrivateTrustMode)
            {
                return ReplyTo(snapshot.RequestedByUserId,
                    "✅ 更新包已接收。\n" +
                    "当前为 Master 私聊信任模式，未要求手动 SHA256。\n" +
                    $"文件：{packageName}\n" +
                    $"大小：{download.FileSize} bytes\n" +
                    $"程序计算 SHA256：{actualSha}\n\n" +
                    "输入 #update qq apply 应用更新。");
            }

            return ReplyTo(snapshot.RequestedByUserId,
                "✅ QQ 更新包已下载并通过 SHA256 校验，当前为可 apply 状态。\n" +
                $"模式：{snapshot.Mode}\n" +
                $"版本：{snapshot.ExpectedVersion}\n" +
                $"文件：{packageName}\n" +
                $"大小：{download.FileSize} bytes\n" +
                $"程序计算 SHA256：{actualSha}\n\n" +
                "确认应用请执行：#update qq apply");
        }
        catch (Exception ex)
        {
            DeleteFileQuietly(download.LocalPath);
            SetState(QqUpdateSessionState.WaitingForFile, ex.Message);
            return ReplyTo(snapshot.RequestedByUserId, $"❌ 校验更新包时出错：{ex.Message}");
        }
    }

    public async Task<UpdateResult> ApplyAsync(Action<string>? updateLogger = null)
    {
        ExpireIfNeeded();
        QqUpdateSession? snapshot;
        lock (_sync)
        {
            snapshot = CloneSession(_session);
            if (_session != null && _session.State == QqUpdateSessionState.ReadyToApply)
            {
                _session.State = QqUpdateSessionState.Applying;
                _session.LastError = null;
            }
        }

        if (snapshot == null)
        {
            return new UpdateResult { Success = false, Message = "当前没有待处理的 QQ 更新包会话。" };
        }

        if (snapshot.State != QqUpdateSessionState.ReadyToApply ||
            string.IsNullOrWhiteSpace(snapshot.PackagePath))
        {
            return new UpdateResult { Success = false, Message = "当前没有已校验完成、可 apply 的 QQ 更新包。" };
        }

        try
        {
            _logger($"[QQ更新包] 开始应用本地更新包: mode={snapshot.Mode}, user={snapshot.RequestedByUserId}, file={snapshot.PackageFileName}, sha={snapshot.ActualSha256}");
            var manager = new CustomUpdateManager(updateLogger ?? _logger);
            return await manager.ExecuteCustomUpdateFromLocalPackageAsync(
                snapshot.PackagePath,
                snapshot.ExpectedVersion,
                snapshot.ExpectedSha256,
                snapshot.Mode == QqUpdateMode.StrictHashMode);
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (_session != null && _session.RequestedByUserId == snapshot.RequestedByUserId)
                {
                    _session.State = QqUpdateSessionState.ReadyToApply;
                    _session.LastError = ex.Message;
                }
            }

            return new UpdateResult { Success = false, Message = $"应用更新包失败：{ex.Message}" };
        }
    }

    private QqUpdateCommandResult PrepareSession(
        QqUpdateMode mode,
        string? expectedVersion,
        string? expectedSha256,
        long? expectedSize,
        long requestedByUserId,
        string successMessage)
    {
        QqUpdateSession? oldSession = null;
        lock (_sync)
        {
            oldSession = _session;
            _session = new QqUpdateSession
            {
                Mode = mode,
                ExpectedVersion = expectedVersion,
                ExpectedSha256 = expectedSha256,
                ExpectedSize = expectedSize,
                RequestedByUserId = requestedByUserId,
                PreparedAt = DateTime.UtcNow,
                State = QqUpdateSessionState.WaitingForFile
            };
        }

        DeleteSessionPackageQuietly(oldSession);
        _logger($"[QQ更新包] 已创建 prepare 会话: mode={mode}, version={expectedVersion ?? "未指定"}, user={requestedByUserId}, size={expectedSize?.ToString() ?? "未指定"}");
        return Ok(successMessage);
    }

    private void SetState(QqUpdateSessionState state, string? error)
    {
        lock (_sync)
        {
            if (_session == null)
            {
                return;
            }

            _session.State = state;
            _session.LastError = error;
        }
    }

    private void ExpireIfNeeded()
    {
        QqUpdateSession? expired = null;
        lock (_sync)
        {
            if (_session == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var expiredBeforeFile = _session.State == QqUpdateSessionState.WaitingForFile &&
                                    now - _session.PreparedAt > PrepareTimeout;
            var expiredReady = _session.State == QqUpdateSessionState.ReadyToApply &&
                               _session.ReadyAt.HasValue &&
                               now - _session.ReadyAt.Value > ReadyTimeout;

            if (expiredBeforeFile || expiredReady)
            {
                expired = _session;
                _session = null;
            }
        }

        if (expired != null)
        {
            DeleteSessionPackageQuietly(expired);
            _logger("[QQ更新包] 会话已过期并清理");
        }
    }

    private static void ValidatePackageStructure(string localPath, string packageName)
    {
        if (!IsZipPackage(packageName))
        {
            return;
        }

        using var archive = ZipFile.OpenRead(localPath);
        var diceEntry = archive.Entries.FirstOrDefault(entry =>
            !string.IsNullOrWhiteSpace(entry.Name) &&
            entry.Name.Equals("MDiceV2.Core.Dice", StringComparison.OrdinalIgnoreCase));

        if (diceEntry == null)
        {
            throw new Exception("Zip 包内未找到 MDiceV2.Core.Dice");
        }
    }

    private static bool IsZipPackage(string packageName)
    {
        var name = packageName ?? string.Empty;
        return name.EndsWith("MDiceV2.Core.Zip", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("MDiceV2.Core.UpdatePackage", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedPackageFileName(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return false;
        }

        return AllowedPackageNames.Any(name =>
            name.Equals(packageName, StringComparison.OrdinalIgnoreCase));
    }

    private static QqUpdateSession? CloneSession(QqUpdateSession? session)
    {
        if (session == null)
        {
            return null;
        }

        return new QqUpdateSession
        {
            Mode = session.Mode,
            ExpectedVersion = session.ExpectedVersion,
            ExpectedSha256 = session.ExpectedSha256,
            ExpectedSize = session.ExpectedSize,
            RequestedByUserId = session.RequestedByUserId,
            PreparedAt = session.PreparedAt,
            ReadyAt = session.ReadyAt,
            State = session.State,
            PackagePath = session.PackagePath,
            PackageFileName = session.PackageFileName,
            ActualSize = session.ActualSize,
            ActualSha256 = session.ActualSha256,
            LastError = session.LastError,
            ReceivedSourceKind = session.ReceivedSourceKind,
            ReceivedFromUserId = session.ReceivedFromUserId,
            ReceivedFromGroupId = session.ReceivedFromGroupId,
            ReceivedAt = session.ReceivedAt
        };
    }

    private static void DeleteSessionPackageQuietly(QqUpdateSession? session)
    {
        DeleteFileQuietly(session?.PackagePath);
    }

    private static void DeleteFileQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static QqUpdateCommandResult Ok(string message) => new(true, message);

    private static QqUpdateCommandResult Fail(string message) => new(false, message);

    private static QqUpdateFileReceiveResult ReplyTo(long userId, string message) => new(true, userId, message);

    private static QqUpdateFileReceiveResult NoReply() => new(false, 0, string.Empty);
}
