using MDiceV2.Interfaces.Mod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AIMod.Trpg;

/// <summary>
/// 单个 AI 角色的独立会话上下文。
/// 每个活跃 AI 角色在 .logon 时创建一个实例，包含独立的历史记录、Prompt 构建和响应逻辑。
/// </summary>
public class AiCharacterSession
{
    public AiCharacterEntry Character { get; }
    public TrpgScope Scope { get; }

    private readonly ChatDatabase _db;
    private readonly PromptAssembler _promptAssembler;
    private readonly MemoryWatchdog _memoryWatchdog;
    private readonly PostProcessor _postProcessor;
    private readonly MessageRouter _messageRouter;
    private readonly StateInterceptor _stateInterceptor;
    private readonly TrpgContextPipeline _contextPipeline;
    private readonly IModContext _context;
    private readonly TrpgPlayerConfig _config;
    private readonly Func<List<ChatMessage>, Task<string?>> _apiCaller;
    private readonly LlmCallTracker? _llmCallTracker;
    private readonly Action<long>? _enterApiScope;
    private readonly Action? _exitApiScope;
    private string _latestGmInputText = "";
    private string _latestTriggerText = "";
    private string _lastResponseTurnId = "";
    private string _lastResponseSourceMessageId = "";
    private string _lastResponseSourceSummary = "";
    private int _recallCount = 0;
    private int _rawCount = 0;
    private DateTime _lastResetTime = DateTime.UtcNow;
    private bool _hasLoggedFullPrompt = false;

    private AiRuntimeMode _runtimeMode = AiRuntimeMode.Act;
    public AiRuntimeMode RuntimeMode => _runtimeMode;

    /// <summary>AI 发言广播回调：(sourceCharacterId, sourceDisplayName, visibleContent)</summary>
    public Action<string, string, string>? OnAiSpeechBroadcast { get; set; }

    public AiCharacterSession(
        TrpgScope scope,
        AiCharacterEntry character,
        ChatDatabase db,
        PromptAssembler promptAssembler,
        MemoryWatchdog memoryWatchdog,
        PostProcessor postProcessor,
        MessageRouter messageRouter,
        StateInterceptor stateInterceptor,
        TrpgContextPipeline contextPipeline,
        IModContext context,
        TrpgPlayerConfig config,
        Func<List<ChatMessage>, Task<string?>> apiCaller,
        LlmCallTracker? llmCallTracker = null,
        Action<long>? enterApiScope = null,
        Action? exitApiScope = null)
    {
        Scope = scope;
        Character = character;
        _db = db;
        _promptAssembler = promptAssembler;
        _memoryWatchdog = memoryWatchdog;
        _postProcessor = postProcessor;
        _messageRouter = messageRouter;
        _stateInterceptor = stateInterceptor;
        _contextPipeline = contextPipeline;
        _context = context;
        _config = config;
        _apiCaller = apiCaller;
        _llmCallTracker = llmCallTracker;
        _enterApiScope = enterApiScope;
        _exitApiScope = exitApiScope;
    }

    /// <summary>
    /// 处理单条群消息：分类、记录历史、检查触发、生成响应。
    /// </summary>
    public async Task<ModMessageResult?> HandleMessageAsync(long groupId, long userId, string content, bool isAted, TeamSnapshot? team, bool allowResponse = true)
    {
        _enterApiScope?.Invoke(groupId);
        try
        {
        // 过滤空消息（转发消息、表情包等）
        if (string.IsNullOrWhiteSpace(content))
            return null;

        // 过滤 AI 自己的消息，避免自循环
        if (userId == Character.VirtualId)
            return null;

        // Off 模式：完全跳过，不分类、不拦截、不写历史、不响应
        if (_runtimeMode == AiRuntimeMode.Off)
        {
            _context.Log(LogLevel.Debug,
                $"[AIMod:TRPG] RuntimeMode=off, skip message completely (Group={groupId}, Char={Character.CharacterId})");
            return null;
        }

        var incomingTurnId = BuildTurnId(groupId, content);
        var incomingSourceMessageId = BuildSourceMessageId(groupId, content);
        var incomingSourceSummary = BuildSourceSummary(content);
        using var incomingTurnContext = LlmCallTracker.PushAmbientTurnContext(
            incomingTurnId,
            incomingSourceMessageId,
            incomingSourceSummary);

        // 1. 消息分类
        var (speakerType, nickname, formatted) = _messageRouter.ClassifyAndFormat(
            groupId, userId, content, isAted, team, _config.OocPrefix, _context);
        if (speakerType == null) return null;

        // 阶段1：状态拦截与更新（仅 GM 输入触发）
        if (string.Equals(speakerType, "GM", StringComparison.OrdinalIgnoreCase))
        {
            _latestGmInputText = content;
            if (_config.StateInterceptionEnabled)
            {
                try
                {
                    await _stateInterceptor.InterceptAndUpdateAsync(Scope, Character.CharacterId, speakerType, content);
                    _context.Log(LogLevel.Debug,
                        $"[AIMod:TRPG] MessageAuthorityDiagnostics | speakerType={speakerType} | state_interceptor_called=true | reason=GMOnlyStateAuthority");
                }
                catch (Exception ex)
                {
                    _context.Log(LogLevel.Error, $"[AIMod:TRPG] 状态拦截异常，已隔离，不影响角色回复: {ex.Message}");
                }
            }
        }
        else
        {
            _context.Log(LogLevel.Debug,
                $"[AIMod:TRPG] MessageAuthorityDiagnostics | speakerType={speakerType} | state_interceptor_called=false | reason=NotGM");
        }

        await _db.InsertHistoryAsync(Scope, Character.CharacterId, speakerType, nickname, "user", formatted);

        // 3. 检查是否触发 AI 响应
        if (!_messageRouter.ShouldTrigger(speakerType, isAted)) return null;
        if (_runtimeMode == AiRuntimeMode.Silent || !allowResponse)
        {
            _context.Log(LogLevel.Debug, $"[AIMod:TRPG] RuntimeMode=silent/turn gate, record only and skip response (Group={groupId}, Char={Character.CharacterId}, Speaker={speakerType})");

            // silent 模式下可以继续进行历史折叠/维护，但不能生成回复
            try
            {
                await _memoryWatchdog.CheckAndFoldAsync(Scope, Character.CharacterId);
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn,
                    $"[AIMod:TRPG] Silent-mode memory fold check failed: {ex.Message}");
            }

            return null;
        }

        _latestTriggerText = content;
        _lastResponseTurnId = incomingTurnId;
        _lastResponseSourceMessageId = incomingSourceMessageId;
        _lastResponseSourceSummary = incomingSourceSummary;

        // 4. 记录触发时间和消息
        _messageRouter.RecordTriggerTime(groupId, Character.CharacterId);
        _messageRouter.RecordPendingMessage(groupId, Character.CharacterId, formatted);

        // 5. 冷却检查（每个角色独立冷却）
        var (canExecute, hasPending, cooldownEndsAt) = _messageRouter.TryAcquireCooldown(groupId, Character.CharacterId, _config.CooldownSeconds);

        if (canExecute)
        {
            // 可以执行
            if (hasPending)
            {
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 冷却完成，执行待执行请求 (Group={groupId}, Char={Character.CharacterId})");
            }
            else
            {
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 冷却已过，立即执行 (Group={groupId}, Char={Character.CharacterId})");
            }
            await RespondAsync(groupId);
        }
        else
        {
            // 冷却中，取消之前的延时任务，启动新的延时任务
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 冷却中，取消旧延时任务并启动新延时任务 (Group={groupId}, Char={Character.CharacterId}), 冷却结束于: {cooldownEndsAt:yyyy-MM-dd HH:mm:ss}");
            
            // 取消之前的延时任务
            _messageRouter.CancelDelayedTask(groupId, Character.CharacterId);
            _messageRouter.ClearPendingExecution(groupId, Character.CharacterId);

            if (cooldownEndsAt.HasValue)
            {
                var delay = cooldownEndsAt.Value - DateTime.UtcNow;
                if (delay.TotalMilliseconds > 0)
                {
                    // 启动新的延时任务
                    var delayedTask = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(delay);
                            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 冷却时间到，收集冷却期间消息 (Group={groupId}, Char={Character.CharacterId})");

                            // 获取冷却期间的所有消息
                            var pendingMessages = _messageRouter.GetAndClearPendingMessages(groupId, Character.CharacterId);
                            
                            if (pendingMessages.Count > 0)
                            {
                                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 收集到 {pendingMessages.Count} 条冷却期间消息，执行响应 (Group={groupId}, Char={Character.CharacterId})");
                                await RespondAsync(groupId);
                            }
                            else
                            {
                                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 冷却期间无新消息，取消执行 (Group={groupId}, Char={Character.CharacterId})");
                            }
                        }
                        catch (Exception ex)
                        {
                            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 延时任务异常: {ex.Message}");
                        }
                    });
                    
                    // 存储延时任务引用
                    _messageRouter.StoreDelayedTask(groupId, Character.CharacterId, delayedTask);
                }
            }
        }

        return null;
        }
        finally
        {
            _exitApiScope?.Invoke();
        }
    }

    public async Task RecordObservedAiMessageAsync(long groupId, string sourceCharacterId, string sourceDisplayName, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        if (string.Equals(sourceCharacterId, Character.CharacterId, StringComparison.OrdinalIgnoreCase))
            return;

        var displayName = string.IsNullOrWhiteSpace(sourceDisplayName) ? "AI" : sourceDisplayName.Trim();
        var formatted = $"[AI-{displayName}]: {content.Trim()}";
        await _db.InsertHistoryAsync(Scope, Character.CharacterId, "AI", displayName, "user", formatted);
        _context.Log(LogLevel.Debug, $"[AIMod:TRPG] Recorded observed AI message (Group={groupId}, Observer={Character.CharacterId}, Source={sourceCharacterId})");
    }

    public void SetRuntimeMode(long groupId, AiRuntimeMode mode)
    {
        _runtimeMode = mode;

        if (mode != AiRuntimeMode.Act)
        {
            CancelPendingResponse(groupId);
        }
    }

    public void CancelPendingResponse(long groupId)
    {
        try
        {
            _messageRouter.CancelDelayedTask(groupId, Character.CharacterId);
            _messageRouter.ClearPendingExecution(groupId, Character.CharacterId);
            _messageRouter.GetAndClearPendingMessages(groupId, Character.CharacterId);
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn,
                $"[AIMod:TRPG] CancelPendingResponse failed (Group={groupId}, Char={Character.CharacterId}): {ex.Message}");
        }
    }

    /// <summary>
    /// 独立生成并发送 AI 响应。
    /// 支持工具调用模式：如果 AI 发起 recall 请求，执行检索后重新生成。
    /// </summary>
    public async Task RespondAsync(long groupId)
    {
        if (_runtimeMode != AiRuntimeMode.Act)
        {
            _context.Log(LogLevel.Debug,
                $"[AIMod:TRPG] RespondAsync skipped because RuntimeMode={_runtimeMode} (Group={groupId}, Char={Character.CharacterId})");
            return;
        }

        _enterApiScope?.Invoke(groupId);
        var turnContext = ResolveResponseTurnContext(groupId);
        using var ambientTurnContext = LlmCallTracker.PushAmbientTurnContext(
            turnContext.TurnId,
            turnContext.SourceMessageId,
            turnContext.SourceSummary);
        // 重置计数器（每分钟重置一次）
        if ((DateTime.UtcNow - _lastResetTime).TotalMinutes > 1)
        {
            _recallCount = 0;
            _rawCount = 0;
            _lastResetTime = DateTime.UtcNow;
            _hasLoggedFullPrompt = false; // 重置日志标志
        }

        try
        {
            await _memoryWatchdog.CheckAndFoldAsync(Scope, Character.CharacterId);
            var contextInputText = !string.IsNullOrWhiteSpace(_latestTriggerText) ? _latestTriggerText : _latestGmInputText;
            var trpgContext = await _contextPipeline.BuildContextAsync(Scope, Character, contextInputText);
            var messages = await _promptAssembler.BuildAsync(Scope, Character, trpgContext);

            // 只在第一次请求时输出完整prompt
            if (!_hasLoggedFullPrompt)
            {
                _context.Log(LogLevel.Info, BuildPromptLogSnapshot(groupId, messages));
                _hasLoggedFullPrompt = true;
            }
            else
            {
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] Reusing prompt (Group={groupId}, Char={Character.CharacterId}, MessageCount={messages.Count})");
            }

            var response = await CallAiAsync(messages, "MainCharacterResponse");
            if (response == null) return;

            // 使用新的 HandleWithRecallAsync 方法
            var (tookAction, recallKeywords, rawRequest, fourLayerTags) = await _postProcessor.HandleWithRecallAsync(Scope, Character.VirtualId, Character.CharacterId, Character.DisplayName, response, trpgContext);
            BroadcastAiSpeech(Character.CharacterId, Character.DisplayName, response);

            // 如果检测到 recall 请求，执行检索后重新生成
            if (recallKeywords != null && recallKeywords.Count > 0)
            {
                // 限制每轮最多一次 recall
                if (_recallCount >= 1)
                {
                    _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Recall 次数超限（当前: {_recallCount}），跳过本次请求");
                    await _postProcessor.HandleAsync(Scope, Character.VirtualId, Character.CharacterId, Character.DisplayName, response, trpgContext);
                    return;
                }

                _recallCount++;
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 执行记忆检索: {string.Join(", ", recallKeywords)}");

                // 执行检索
                var queryText = string.Join(" ", recallKeywords);
                var recalls = await _db.SearchMemoryNodesBySimilarityAsync(
                    Scope, Character.CharacterId, queryText,
                    minSimilarity: 0.15, topK: 10,  // 降低阈值以增加召回率
                    queryEmbedding: null,
                    currentEntities: trpgContext.PresentEntityIds,
                    currentSceneId: trpgContext.CurrentSceneId);

                // 构建检索结果字符串
                var recallResult = await BuildRecallResultStringAsync(Scope, Character.CharacterId, recalls, recallKeywords);
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 检索结果: {recallResult}");

                // 将检索结果添加到 prompt 上下文
                trpgContext.RecalledMemoryVar = recallResult;

                // 重新构建 prompt 并生成回复（不输出完整日志）
                messages = await _promptAssembler.BuildAsync(Scope, Character, trpgContext);
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] Recall retry (Group={groupId}, Char={Character.CharacterId}, MessageCount={messages.Count})");
                response = await CallAiAsync(messages, "RecallAugmentedResponse");

                if (response == null)
                {
                    _context.Log(LogLevel.Warn, "[AIMod:TRPG] Recall retry returned null response");
                    return;
                }

                if (string.IsNullOrWhiteSpace(response))
                {
                    _context.Log(LogLevel.Warn, "[AIMod:TRPG] Recall retry returned empty response");
                    return;
                }

                _context.Log(LogLevel.Info, $"[AIMod:TRPG] Recall retry response received, length={response.Length}");

                // 再次处理（这次应该没有 recall 标签）
                await _postProcessor.HandleAsync(Scope, Character.VirtualId, Character.CharacterId, Character.DisplayName, response, trpgContext);
                BroadcastAiSpeech(Character.CharacterId, Character.DisplayName, response);
            }

            // 如果检测到 raw 请求，执行原始档案检索后重新生成
            if (!string.IsNullOrWhiteSpace(rawRequest))
            {
                // 限制每轮最多一次 raw
                if (_rawCount >= 1)
                {
                    _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Raw 次数超限（当前: {_rawCount}），跳过本次请求");
                    await _postProcessor.HandleAsync(Scope, Character.VirtualId, Character.CharacterId, Character.DisplayName, response, trpgContext);
                    return;
                }

                _rawCount++;
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 执行原始档案检索: {rawRequest}");

                // 获取包含 RawExcerpt 的记忆节点
                var allMemories = await _db.GetAllMemoryNodesAsync(Scope, Character.CharacterId, limit: 50);
                var rawMemories = allMemories.Where(m => !string.IsNullOrWhiteSpace(m.RawExcerpt) && m.RawExcerpt != "[]").ToList();

                // 构建原始档案结果字符串
                var rawResult = BuildRawArchiveString(rawMemories, rawRequest);
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 原始档案结果: {rawResult}");

                // 将原始档案结果添加到 prompt 上下文
                trpgContext.RecalledMemoryVar = rawResult;

                // 重新构建 prompt 并生成回复（不输出完整日志）
                messages = await _promptAssembler.BuildAsync(Scope, Character, trpgContext);
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] Raw archive retry (Group={groupId}, Char={Character.CharacterId}, MessageCount={messages.Count})");
                response = await CallAiAsync(messages, "RawArchiveAugmentedResponse");

                if (response == null)
                {
                    _context.Log(LogLevel.Warn, "[AIMod:TRPG] Raw archive retry returned null response");
                    return;
                }

                if (string.IsNullOrWhiteSpace(response))
                {
                    _context.Log(LogLevel.Warn, "[AIMod:TRPG] Raw archive retry returned empty response");
                    return;
                }

                _context.Log(LogLevel.Info, $"[AIMod:TRPG] Raw archive retry response received, length={response.Length}");

                // 再次处理（这次应该没有 raw 标签）
                await _postProcessor.HandleAsync(Scope, Character.VirtualId, Character.CharacterId, Character.DisplayName, response, trpgContext);
                BroadcastAiSpeech(Character.CharacterId, Character.DisplayName, response);
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] AI角色 '{Character.DisplayName}' 响应失败: {ex.Message}");
        }
        finally
        {
            _exitApiScope?.Invoke();
        }
    }


    /// <summary>剥离内部标签(&lt;recall&gt;等)，保留可见文本</summary>
    private static string StripInternalTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        return System.Text.RegularExpressions.Regex.Replace(text,
            @"</?(recall|event|memory|command|inventory_mutation|affective_tag|new_entity_check|scene_snapshot|entity_change|identity_merge|objective|complete|abandon|fact|relationship|summary|presence_snapshot|entity_profile)[^>]*>",
            "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private void BroadcastAiSpeech(string characterId, string displayName, string response)
    {
        OnAiSpeechBroadcast?.Invoke(characterId, displayName, StripInternalTags(response));
    }

    private async Task<string> BuildRecallResultStringAsync(TrpgScope scope, string characterId, List<MemoryNode> recalls, List<string> recallKeywords)
    {
        if (recalls.Count == 0)
            return "未找到相关记忆。";

        var memories = await _db.GetCharacterMemoriesAsync(scope, characterId, limit: 200);
        var queryTokens = ExtractRecallTokens(string.Join(" ", recallKeywords));

        var sb = new StringBuilder();
        sb.AppendLine("[检索结果]");

        var renderedCount = 0;
        foreach (var node in recalls)
        {
            var memory = FindBestMatchingCharacterMemory(memories, node, queryTokens);
            if (memory != null)
            {
                renderedCount++;
                sb.AppendLine($"[{renderedCount}] {memory.Content}");
                continue;
            }

            // fallback: 回退到原文切片，而不是摘要
            var excerpts = ParseRawExcerpts(node.RawExcerpt);
            if (excerpts.Count > 0)
            {
                foreach (var excerpt in excerpts.Take(2))
                {
                    renderedCount++;
                    sb.AppendLine($"[{renderedCount}] {excerpt}");
                }
            }
        }

        if (renderedCount == 0)
        {
            foreach (var (node, i) in recalls.Select((n, i) => (n, i)))
                sb.AppendLine($"[{i + 1}] {node.Summary}");
        }
        return sb.ToString();
    }

    private Task<string?> CallAiAsync(List<ChatMessage> messages, string requestKind)
    {
        return (_llmCallTracker ?? throw new InvalidOperationException("LlmCallTracker is required for AIMod LLM calls."))
            .CallAsync(Scope, Character.CharacterId, messages, "ActionAgent", requestKind, _apiCaller);
    }

    private ResponseTurnContext ResolveResponseTurnContext(long groupId)
    {
        if (!string.IsNullOrWhiteSpace(_lastResponseTurnId) && !string.IsNullOrWhiteSpace(_lastResponseSourceMessageId))
        {
            return new ResponseTurnContext(_lastResponseTurnId, _lastResponseSourceMessageId, _lastResponseSourceSummary);
        }

        var fallbackText = !string.IsNullOrWhiteSpace(_latestTriggerText) ? _latestTriggerText : _latestGmInputText;
        if (string.IsNullOrWhiteSpace(fallbackText))
            return new ResponseTurnContext(null, null, null);

        return new ResponseTurnContext(
            BuildTurnId(groupId, fallbackText),
            BuildSourceMessageId(groupId, fallbackText),
            BuildSourceSummary(fallbackText));
    }

    private string BuildTurnId(long groupId, string text)
        => $"turn:{Scope.WorldId}:{groupId}:{HashText(text)}";

    private string BuildSourceMessageId(long groupId, string text)
        => $"src:{Scope.WorldId}:{groupId}:{HashText(text)}";

    private static string BuildSourceSummary(string text)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return normalized.Length <= 80 ? normalized : normalized[..80];
    }

    private static string HashText(string? text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")));

    private static string BuildRawArchiveString(List<MemoryNode> rawMemories, string timeRange)
    {
        if (rawMemories.Count == 0)
            return "未找到原始档案。";

        var sb = new StringBuilder();
        sb.AppendLine($"[原始档案] 时间范围: {timeRange}");

        foreach (var (node, i) in rawMemories.Select((n, i) => (n, i)))
        {
            sb.AppendLine($"[{i + 1}] {node.Summary}");

            // 解析并显示 RawExcerpt
            try
            {
                var excerpts = JsonSerializer.Deserialize<List<string>>(node.RawExcerpt);
                if (excerpts != null && excerpts.Count > 0)
                {
                    sb.AppendLine("  原文:");
                    foreach (var excerpt in excerpts)
                    {
                        sb.AppendLine($"  - {excerpt}");
                    }
                }
            }
            catch
            {
                // JSON 解析失败，跳过
            }
        }

        return sb.ToString();
    }

    private static HashSet<string> ExtractRecallTokens(string text)
    {
        return text
            .Split(new[] { ' ', '\t', '\r', '\n', '，', ',', '。', '！', '？', '、', ':', '：', ';', '；', '|', '/', '-', '_', '(', ')', '[', ']', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length >= 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> ParseRawExcerpts(string rawExcerpt)
    {
        if (string.IsNullOrWhiteSpace(rawExcerpt) || rawExcerpt == "[]")
            return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(rawExcerpt) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static EpisodicMemory.CharacterMemory? FindBestMatchingCharacterMemory(
        List<EpisodicMemory.CharacterMemory> memories,
        MemoryNode node,
        HashSet<string> queryTokens)
    {
        if (memories.Count == 0)
            return null;

        var excerpts = ParseRawExcerpts(node.RawExcerpt);
        var nodeTokens = ExtractRecallTokens($"{node.Keywords} {node.Summary} {string.Join(" ", excerpts)}");
        foreach (var token in queryTokens)
            nodeTokens.Add(token);

        var best = memories
            .Select(m =>
            {
                var lower = m.Content ?? "";
                var score = nodeTokens.Count(token => lower.Contains(token, StringComparison.OrdinalIgnoreCase));
                return new { Memory = m, Score = score };
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Memory.Confidence)
            .FirstOrDefault();

        return best != null && best.Score > 0 ? best.Memory : null;
    }

    private string BuildPromptLogSnapshot(long groupId, List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[AIMod:TRPG] FullPromptSnapshot (Group={groupId}, Char={Character.CharacterId}, Name={Character.DisplayName}, MessageCount={messages.Count})");
        sb.AppendLine("======================== BEGIN PROMPT ========================");
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            sb.AppendLine($"[#{i}] role={msg.Role}");
            sb.AppendLine(msg.Content ?? string.Empty);
            sb.AppendLine("------------------------");
        }
        sb.AppendLine("========================= END PROMPT =========================");
        return sb.ToString();
    }

    private sealed record ResponseTurnContext(string? TurnId, string? SourceMessageId, string? SourceSummary);
}
