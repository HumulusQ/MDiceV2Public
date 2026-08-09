using System.Collections.Concurrent;
using System.Text;

namespace MDiceV2.Models.CharacterCards;

/// <summary>Coordinates the safe, group-only character-card import pipeline.</summary>
public sealed class CharacterCardFileImportCoordinator : IDisposable
{
    public const string FocusPrefix = "character_card_import:";
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(10);
    private readonly MessageDistribution _messageDistribution;
    private readonly MessageProcessor _messageProcessor;
    private readonly OneBotFileContentResolver _fileResolver;
    private readonly HtmlCharacterCardParser _parser;
    private readonly CharacterCardMapper _mapper;
    private readonly ConcurrentDictionary<string, DateTime> _recentFiles = new();
    private readonly ConcurrentDictionary<string, PendingImport> _pendingImports = new();
    private readonly object _pendingGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public CharacterCardFileImportCoordinator(
        MessageDistribution messageDistribution,
        MessageProcessor messageProcessor,
        OneBotFileContentResolver? fileResolver = null,
        HtmlCharacterCardParser? parser = null,
        CharacterCardMapper? mapper = null)
    {
        _messageDistribution = messageDistribution;
        _messageProcessor = messageProcessor;
        _fileResolver = fileResolver ?? new OneBotFileContentResolver(messageDistribution);
        _parser = parser ?? new HtmlCharacterCardParser();
        _mapper = mapper ?? new CharacterCardMapper();
        _messageDistribution.OnFileMessage += OnFileMessage;
        _messageDistribution.OnCharacterCardImportConfirmation += OnImportConfirmation;
    }

    public void OnFileMessage(OneBotFileInfo file)
    {
        if (_disposed) return;
        _ = HandleSafelyAsync(file, _shutdown.Token);
    }

    internal async Task HandleFileMessageAsync(OneBotFileInfo file, CancellationToken cancellationToken)
    {
        if (file.GroupId <= 0 || !IsCandidateFile(file) || !_messageProcessor.IsBotEnabled(file.GroupId)) return;
        if (!TryAcceptFile(file)) return;

        Log.Normal($"[人物卡导入] start group={file.GroupId} user={file.UserId} fileId={file.FileId} name={file.FileName} source={file.SourceKind} size={file.FileSize}");
        if (file.UserId <= 0)
        {
            SendFailure(file, "无法确定上传者，不能导入到人物卡列表。");
            return;
        }
        if (file.FileSize > OneBotFileContentResolver.MaxFileSizeBytes)
        {
            SendFailure(file, "文件超过 5 MiB，无法自动导入。");
            return;
        }

        if (!TryCreatePendingImport(file)) return;
        SendConfirmationPrompt(file);
    }

    private async Task DownloadAndImportAsync(OneBotFileInfo file, CancellationToken cancellationToken)
    {
        if (!_messageProcessor.IsBotEnabled(file.GroupId)) return;

        var resolved = await _fileResolver.ResolveAsync(file, cancellationToken);
        if (!resolved.Success)
        {
            Log.Warn($"[人物卡导入] resolve failed group={file.GroupId} user={file.UserId} fileId={file.FileId} stage=resolve");
            SendFailure(file, resolved.ErrorMessage);
            return;
        }

        var html = DecodeUtf8(resolved.Content!);
        var parsed = _parser.Parse(html);
        if (!parsed.Success)
        {
            Log.Warn($"[人物卡导入] parse failed group={file.GroupId} user={file.UserId} fileId={file.FileId} stage=parse");
            SendFailure(file, parsed.ErrorMessage);
            return;
        }

        var mappedCards = parsed.Documents.Select(_mapper.Map).ToList();
        var failedMapping = mappedCards.FirstOrDefault(x => !x.Success);
        if (failedMapping is not null)
        {
            Log.Warn($"[人物卡导入] map failed group={file.GroupId} user={file.UserId} fileId={file.FileId} stage=map");
            SendFailure(file, failedMapping.ErrorMessage);
            return;
        }

        var importedCards = new List<(CharacterCardImportResult Imported, CharacterSheetMappingResult Mapped, bool Renamed)>();
        for (var index = 0; index < mappedCards.Count; index++)
        {
            var mapped = mappedCards[index];
            var requestedName = mapped.CharacterSheet!.Name;
            var imported = _messageProcessor.ImportCharacterCard(
                file.UserId,
                mapped.CharacterSheet,
                CharacterCardConflictPolicy.Rename,
                setAsCurrent: index == mappedCards.Count - 1);
            if (!imported.Success)
            {
                Log.Warn($"[人物卡导入] import failed group={file.GroupId} user={file.UserId} fileId={file.FileId} stage=persist imported={importedCards.Count} total={mappedCards.Count}");
                var partial = importedCards.Count == 0 ? string.Empty : $"已成功导入 {importedCards.Count} 张，其余未导入。\n";
                SendFailure(file, partial + imported.Message);
                return;
            }

            importedCards.Add((
                imported,
                mapped,
                !string.Equals(requestedName, imported.FinalCharacterName, StringComparison.Ordinal)));
        }

        var finalCard = importedCards[^1];
        Log.Normal($"[人物卡导入] success group={file.GroupId} user={file.UserId} fileId={file.FileId} via={resolved.Source} schema={parsed.SourceSchema} version={parsed.SourceVersion} cards={importedCards.Count} current={finalCard.Imported.FinalCharacterName} skills={mappedCards.Sum(x => x.SkillCount)} conflicts={mappedCards.Sum(x => x.ConflictCount)}");
        SendSuccess(file, importedCards, parsed.IsLibrary);
    }

    public static bool IsCandidateFile(OneBotFileInfo file)
    {
        var fileName = file.FileName?.Trim() ?? string.Empty;
        return fileName.EndsWith(".mdice.html", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".mdice", StringComparison.OrdinalIgnoreCase)
            // v216 initially exported library bundles with a plain .html suffix.
            // Keep those already-created packages recognizable without treating
            // every arbitrary HTML upload as a character card.
            || (fileName.StartsWith("CoC7_全部调查员_", StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
    }

    private bool TryCreatePendingImport(OneBotFileInfo file)
    {
        CleanupExpiredPendingImports();
        var userKey = file.UserId.ToString();
        var focus = BuildFocusValue(file);
        var pendingKey = BuildPendingKey(file.GroupId, file.UserId);
        lock (_pendingGate)
        {
            if (_messageDistribution.UserFocusStates.TryGetValue(userKey, out var existingFocus))
            {
                if (string.Equals(existingFocus, focus, StringComparison.Ordinal))
                    return false;
                SendFailure(file, "你当前有待处理操作，请先完成或取消后再上传人物卡。");
                return false;
            }

            if (!_pendingImports.TryAdd(pendingKey, new PendingImport(file, DateTime.UtcNow)))
                return false;
            _messageDistribution.SetUserFocus(userKey, focus);
            Log.Normal($"[人物卡导入] awaiting confirmation group={file.GroupId} user={file.UserId} fileId={file.FileId} name={file.FileName}");
            return true;
        }
    }

    private void OnImportConfirmation(Msg msg, string message)
    {
        if (_disposed || msg.Source != MessageSource.group) return;
        var key = BuildPendingKey(msg.GroupId, msg.UserId);
        if (!_pendingImports.TryGetValue(key, out var pending)) return;

        var focus = BuildFocusValue(pending.File);
        if (!string.Equals(_messageDistribution.GetUserFocus(msg.UserId.ToString()), focus, StringComparison.Ordinal)) return;
        if (DateTime.UtcNow - pending.CreatedAt > ConfirmationLifetime)
        {
            ClearPendingImport(pending.File);
            _messageDistribution.WSconnection.SendGroupMessage(msg.GroupId, "人物卡导入确认已超时，请重新上传文件。");
            return;
        }

        var reply = message.Trim();
        if (reply.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            ClearPendingImport(pending.File);
            _messageDistribution.WSconnection.SendGroupMessage(msg.GroupId, "已确认，正在下载并导入人物卡…");
            _ = DownloadSafelyAsync(pending.File, _shutdown.Token);
            return;
        }

        if (reply.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            ClearPendingImport(pending.File);
            _messageDistribution.WSconnection.SendGroupMessage(msg.GroupId, "已取消人物卡导入。");
            return;
        }

        _messageDistribution.WSconnection.SendGroupMessage(msg.GroupId, "请回复 y 确认下载并导入人物卡，或回复 n 取消。");
    }

    private async Task DownloadSafelyAsync(OneBotFileInfo file, CancellationToken cancellationToken)
    {
        try
        {
            await DownloadAndImportAsync(file, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"[人物卡导入] download/import exception group={file.GroupId} user={file.UserId} fileId={file.FileId}: {ex}");
            SendFailure(file, "下载或导入人物卡时发生错误。");
        }
    }

    private async Task HandleSafelyAsync(OneBotFileInfo file, CancellationToken cancellationToken)
    {
        try
        {
            await HandleFileMessageAsync(file, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"[人物卡导入] unhandled exception group={file.GroupId} user={file.UserId} fileId={file.FileId}: {ex}");
        }
    }

    private bool TryAcceptFile(OneBotFileInfo file)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _recentFiles)
            if (now - entry.Value > TimeSpan.FromMinutes(2)) _recentFiles.TryRemove(entry.Key, out _);
        return _recentFiles.TryAdd(BuildDeduplicationKey(file), now);
    }

    private static string BuildDeduplicationKey(OneBotFileInfo file) =>
        !string.IsNullOrWhiteSpace(file.FileId)
            ? $"{file.GroupId}:{file.UserId}:{file.FileId}"
            : $"{file.GroupId}:{file.UserId}:{file.FileName}:{file.FileSize}";

    private static string BuildPendingKey(long groupId, long userId) => $"{groupId}:{userId}";
    private static string BuildFocusValue(OneBotFileInfo file) => $"{FocusPrefix}{file.GroupId}:{file.UserId}";

    private void ClearPendingImport(OneBotFileInfo file)
    {
        _pendingImports.TryRemove(BuildPendingKey(file.GroupId, file.UserId), out _);
        var userKey = file.UserId.ToString();
        if (string.Equals(_messageDistribution.GetUserFocus(userKey), BuildFocusValue(file), StringComparison.Ordinal))
            _messageDistribution.ClearUserFocus(userKey);
    }

    private void CleanupExpiredPendingImports()
    {
        var now = DateTime.UtcNow;
        foreach (var pending in _pendingImports)
            if (now - pending.Value.CreatedAt > ConfirmationLifetime)
                ClearPendingImport(pending.Value.File);
    }

    private static string DecodeUtf8(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            return Encoding.UTF8.GetString(content, 3, content.Length - 3);
        return Encoding.UTF8.GetString(content);
    }

    private void SendSuccess(
        OneBotFileInfo file,
        IReadOnlyList<(CharacterCardImportResult Imported, CharacterSheetMappingResult Mapped, bool Renamed)> cards,
        bool isLibrary)
    {
        var current = cards[^1];
        if (!isLibrary && cards.Count == 1)
        {
            var singleMessage = $"✓ 已导入人物卡「{current.Imported.FinalCharacterName}」\n属性：{current.Imported.CharacteristicCount} 项\n技能：{current.Mapped.SkillCount} 项";
            if (current.Renamed) singleMessage += "\n原名称已存在，因此自动使用新名称。";
            singleMessage += "\n已切换为当前人物卡。";
            _messageDistribution.WSconnection.SendGroupMessage(file.GroupId, singleMessage);
            return;
        }

        var renamedCount = cards.Count(x => x.Renamed);
        var names = string.Join("、", cards.Take(8).Select(x => $"「{x.Imported.FinalCharacterName}」"));
        if (cards.Count > 8) names += $"等 {cards.Count} 张";
        var message = $"✓ 已导入人物卡数据包\n成功导入：{cards.Count} 张\n人物卡：{names}";
        if (renamedCount > 0) message += $"\n其中 {renamedCount} 张因名称重复已自动改名。";
        message += $"\n当前人物卡：「{current.Imported.FinalCharacterName}」";
        _messageDistribution.WSconnection.SendGroupMessage(file.GroupId, message);
    }

    private void SendFailure(OneBotFileInfo file, string reason)
    {
        var fileName = string.IsNullOrWhiteSpace(file.FileName) ? "该文件" : file.FileName;
        _messageDistribution.WSconnection.SendGroupMessage(file.GroupId, $"无法导入「{fileName}」：\n{reason}");
    }

    private void SendConfirmationPrompt(OneBotFileInfo file)
    {
        var fileName = string.IsNullOrWhiteSpace(file.FileName) ? "该人物卡文件" : file.FileName;
        _messageDistribution.WSconnection.SendGroupMessage(
            file.GroupId,
            $"检测到人物卡或人物卡数据包「{fileName}」。\n回复 y 确认下载并导入，回复 n 取消");
    }

    public bool IsFor(MessageDistribution distribution, MessageProcessor processor) =>
        ReferenceEquals(_messageDistribution, distribution) && ReferenceEquals(_messageProcessor, processor);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _messageDistribution.OnFileMessage -= OnFileMessage;
        _messageDistribution.OnCharacterCardImportConfirmation -= OnImportConfirmation;
        foreach (var pending in _pendingImports.Values) ClearPendingImport(pending.File);
        _fileResolver.Dispose();
        _shutdown.Dispose();
    }

    private sealed record PendingImport(OneBotFileInfo File, DateTime CreatedAt);
}
