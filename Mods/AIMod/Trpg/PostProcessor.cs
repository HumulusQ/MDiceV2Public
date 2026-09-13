using MDiceV2.Interfaces.Mod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AIMod.Trpg;

/// <summary>
/// 后处理：[PASS] 拦截 + 正常发言回传 + <command> 指令提取与执行 + <attention> 标记解析
/// </summary>
public class PostProcessor
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly AttentionBuffer _attentionBuffer;
    private readonly ObjectiveLayer _objectiveLayer;
    private readonly EntityCanonicalizer _entityCanonicalizer;
    private readonly EventLog _eventLog;
    private readonly SceneSnapshotManager _sceneSnapshotManager;
    private readonly RuntimeValidator _validator;
    private readonly StateMutationPipeline _mutationPipeline;
    private readonly CausalGraph _causalGraph;
    private readonly EpisodicMemory _episodicMemory;
    private readonly NarrativeGravityEngine _gravityEngine;
    private readonly Action<long, long, string, string>? _trpgLogWriter;

    // 匹配 <command>...</command> 标签，DOTALL 以支持跨行
    private static readonly Regex CommandTagRegex = new(
        @"<command>(.*?)</command>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 匹配 <attention>...</attention> 标签
    private static readonly Regex AttentionTagRegex = new(
        @"<attention>(.*?)</attention>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 匹配 <recall>...</recall> 标签
    private static readonly Regex RecallTagRegex = new(
        @"<recall>(.*?)</recall>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 匹配 <raw>...</raw> 标签
    private static readonly Regex RawTagRegex = new(
        @"<raw>(.*?)</raw>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 匹配 <objective>...</objective> 标签
    private static readonly Regex ObjectiveTagRegex = new(
        @"<objective>(.*?)</objective>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 匹配 <complete>...</complete> 标签
    private static readonly Regex CompleteTagRegex = new(
        @"<complete>(.*?)</complete>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 匹配 <abandon>...</abandon> 标签
    private static readonly Regex AbandonTagRegex = new(
        @"<abandon>(.*?)</abandon>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 匹配 <identity_merge>...</identity_merge> 标签
    private static readonly Regex IdentityMergeTagRegex = new(
        @"<identity_merge>(.*?)</identity_merge>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 匹配 <event>...</event> 标签
    private static readonly Regex EventTagRegex = new(
        @"<event>(.*?)</event>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 匹配 <memory>...</memory> 标签
    private static readonly Regex MemoryTagRegex = new(
        @"<memory>(.*?)</memory>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 匹配 <entity_change>...</entity_change> 标签
    private static readonly Regex EntityChangeTagRegex = new(
        @"<entity_change>(.*?)</entity_change>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 匹配 <scene_snapshot>...</scene_snapshot> 标签
    private static readonly Regex SceneSnapshotTagRegex = new(
        @"<scene_snapshot>(.*?)</scene_snapshot>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // 匹配 <tag_pass/> 标签
    private static readonly Regex TagPassRegex = new(
        @"<tag_pass\s*/>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 匹配 [STATE]...[/STATE] 区块
    private static readonly Regex StateBlockRegex = new(
        @"\[STATE\][\s\S]*?\[/STATE\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 匹配首行角色名头：[名字]： 或 [名字]:
    private static readonly Regex HeaderLineRegex = new(
        @"^\s*\[[^\]\r\n]+\]\s*[:：]?\s*$",
        RegexOptions.Compiled);


    public PostProcessor(
        IModContext context, 
        ChatDatabase db, 
        AttentionBuffer? attentionBuffer = null,
        ObjectiveLayer? objectiveLayer = null,
        EntityCanonicalizer? entityCanonicalizer = null,
        EventLog? eventLog = null,
        SceneSnapshotManager? sceneSnapshotManager = null,
        RuntimeValidator? validator = null,
        StateMutationPipeline? mutationPipeline = null,
        CausalGraph? causalGraph = null,
        EpisodicMemory? episodicMemory = null,
        NarrativeGravityEngine? gravityEngine = null,
        Action<long, long, string, string>? trpgLogWriter = null)
    {
        _context = context;
        _db = db;
        _attentionBuffer = attentionBuffer ?? new AttentionBuffer();
        _objectiveLayer = objectiveLayer ?? new ObjectiveLayer(context, db);
        _entityCanonicalizer = entityCanonicalizer ?? new EntityCanonicalizer(context, db);
        _eventLog = eventLog ?? new EventLog(context, db);
        _sceneSnapshotManager = sceneSnapshotManager ?? new SceneSnapshotManager(context, db);
        _validator = validator ?? new RuntimeValidator(context, db);
        _trpgLogWriter = trpgLogWriter;
        _causalGraph = causalGraph ?? new CausalGraph(context, db, _eventLog);
        _episodicMemory = episodicMemory ?? new EpisodicMemory(context, db, _eventLog);
        
        // 创建新组件
        _gravityEngine = gravityEngine ?? new NarrativeGravityEngine(context, db, _eventLog, _causalGraph, _objectiveLayer);
        
        // StateMutationPipeline 需要依赖其他组件，所以在这里创建
        if (mutationPipeline == null)
        {
            var projection = new WorldStateProjection(context, db, _eventLog, _entityCanonicalizer, _objectiveLayer);
            _mutationPipeline = new StateMutationPipeline(context, db, _validator, _eventLog, _entityCanonicalizer, _objectiveLayer, projection);
        }
        else
        {
            _mutationPipeline = mutationPipeline;
        }
    }

    /// <summary>
    /// 处理 AI 响应。返回 true 表示 AI 采取了行动，false 表示 [PASS] 静默。
    /// 自动提取 <command>...</command> 标签内的指令，发送到群聊并交由本体执行。
    /// 如果检测到 <recall> 或 <raw> 标签，返回特殊标记表示需要重新生成。
    /// 提取四层架构相关标签：objective, complete, abandon, identity_merge, event, memory, entity_change, scene_snapshot
    /// </summary>
    public async Task<(bool TookAction, List<string>? RecallKeywords, string? RawRequest, Dictionary<string, string>? FourLayerTags)> HandleWithRecallAsync(TrpgScope scope, long virtualId, string characterId, string characterName, string rawResponse, TrpgPromptContext? trpgContext = null)
    {
        var groupId = scope.GroupId;
        var cleaned = rawResponse.Trim();

        // [PASS] 静默机制
        if (IsPassResponse(cleaned))
        {
            _context.Log(LogLevel.Info, "[AIMod:TRPG] AI 判定无需行动，[PASS] 机制触发。");
            return (false, null, null, null);
        }

        // 拆分叙述文本与指令
        var (narrative, commands) = ExtractCommandTags(cleaned);
        var attentionMarkers = ExtractAttentionTags(cleaned);
        var recallKeywords = ExtractRecallTags(cleaned);
        var rawRequest = ExtractRawTags(cleaned);
        var fourLayerTags = ExtractFourLayerTags(cleaned);
        var hasTagPass = TagPassRegex.IsMatch(cleaned);
        narrative = SanitizeNarrative(narrative);
        narrative = NormalizeCharacterHeader(narrative, characterName);

        // 检查是否为纯 recall 预检响应（仅含 recall 标签，无其他 IC/OOC 内容）
        var isPureRecall = recallKeywords.Count > 0 && string.IsNullOrWhiteSpace(narrative) && commands.Count == 0 && fourLayerTags == null;

        // 处理记忆检索请求
        if (recallKeywords.Count > 0)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 检测到记忆检索请求: {string.Join(", ", recallKeywords)}, 纯预检: {isPureRecall}");
            // 返回检索关键词，让上层处理
            return (false, recallKeywords, null, null);
        }

        // 处理原始档案请求
        if (!string.IsNullOrWhiteSpace(rawRequest))
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 检测到原始档案请求: {rawRequest}");
            // 返回 raw 请求，让上层处理
            return (false, null, rawRequest, null);
        }

        // 处理注意力标记：添加到缓存
        foreach (var marker in attentionMarkers)
        {
            _attentionBuffer.AddMarker(scope, characterId, marker);
            _context.Log(LogLevel.Debug, $"[AIMod:TRPG] Attention Marker 缓存: {marker.Type} -> {marker.Target} (importance={marker.Importance})");
        }

        // 1. 发送叙述文本到群聊
        if (!string.IsNullOrWhiteSpace(narrative))
        {
            AiOutputEchoGuard.Mark(groupId, narrative, characterId, characterName);
            _context.SendGroupMessage(groupId, narrative);
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] AI 发言已发送 (Group={groupId}, Char={characterId}): {narrative.Substring(0, Math.Min(80, narrative.Length))}...");
            // 记录到主程序 TRPG 日志（若当前群未开启 .log，则会自然跳过）
            WriteTrpgLog(groupId, virtualId, characterName, narrative);
            // 记录净化后的叙述文本到历史
            await _db.InsertHistoryAsync(scope, characterId, "Narrative", characterName, "assistant", narrative);
        }

        // 记录完整的原始响应（包括标签）到日志
        _context.Log(LogLevel.Info, $"[AIMod:TRPG] AI 完整原始响应 (Group={groupId}, Char={characterId}): {rawResponse}");

        // 2. 逐条处理 <command> 指令：文本发群 + 本体执行
        foreach (var cmd in commands)
        {
            var trimmedCmd = cmd.Trim();
            if (string.IsNullOrWhiteSpace(trimmedCmd)) continue;

            // 发送指令文本到群聊（显示给用户看）
            AiOutputEchoGuard.Mark(groupId, trimmedCmd, characterId, characterName);
            _context.SendGroupMessage(groupId, trimmedCmd);
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] AI 指令文本已发送 (Group={groupId}, Char={characterId}): {trimmedCmd}");
            // 记录到主程序 TRPG 日志（若当前群未开启 .log，则会自然跳过）
            WriteTrpgLog(groupId, virtualId, characterName, trimmedCmd);

            // 交由程序本体 command handler 执行（使用 AI 的虚拟ID作为执行者）
            _context.ExecuteCommand(groupId, virtualId, trimmedCmd);
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] AI 指令已交由本体执行 (UserId={virtualId}): {trimmedCmd}");

            // 在历史中记录为 AI 的行动指令
            await _db.InsertHistoryAsync(scope, characterId, "DiceCommand", characterName, "assistant", trimmedCmd);
        }

        // 3. 处理四层架构标签
        if (fourLayerTags != null && fourLayerTags.Count > 0)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 检测到四层架构标签: {string.Join(", ", fourLayerTags.Keys)}");
            await ProcessFourLayerTagsAsync(scope, characterId, fourLayerTags, trpgContext);
        }
        else if (hasTagPass)
        {
            _context.Log(LogLevel.Info, "[AIMod:TRPG] 检测到 <tag_pass/>，本轮无状态维护标签");
        }

        // 4. 自动构建因果图谱（如果有事件标签）
        if (fourLayerTags != null && fourLayerTags.ContainsKey("event"))
        {
            try
            {
                await _causalGraph.AutoBuildGraphAsync(scope, characterId);
                _context.Log(LogLevel.Info, "[AIMod:TRPG] 因果图谱自动构建完成");
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod:TRPG] 因果图谱自动构建失败: {ex.Message}");
            }
        }

        // 5. 自动生成角色记忆（如果有事件标签）
        if (fourLayerTags != null && fourLayerTags.ContainsKey("event"))
        {
            try
            {
                // 从事件标签中提取事件信息
                var eventContent = fourLayerTags["event"];
                var evt = new WorldEvent
                {
                    EventType = "dialogue",
                    Result = eventContent,
                    Timestamp = DateTime.UtcNow,
                    SourceEntityId = characterId
                };
                await _episodicMemory.AutoGenerateEpisodicMemoryAsync(scope, characterId, evt);
                _context.Log(LogLevel.Info, "[AIMod:TRPG] 角色记忆自动生成完成");
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod:TRPG] 角色记忆自动生成失败: {ex.Message}");
            }
        }

        return (true, null, null, fourLayerTags);
    }

    /// <summary>
    /// 处理 AI 响应。返回 true 表示 AI 采取了行动，false 表示 [PASS] 静默。
    /// 自动提取 <command>...</command> 标签内的指令，发送到群聊并交由本体执行。
    /// </summary>
    public async Task<bool> HandleAsync(TrpgScope scope, long virtualId, string characterId, string characterName, string rawResponse, TrpgPromptContext? trpgContext = null)
    {
        var (tookAction, _, _, _) = await HandleWithRecallAsync(scope, virtualId, characterId, characterName, rawResponse, trpgContext);
        return tookAction;
    }


    /// <summary>
    /// 从 AI 回复中提取 <command>...</command> 标签内的指令。
    /// 返回 (移除标签后的叙述文本, 指令列表)。
    /// </summary>
    private static (string Narrative, List<string> Commands) ExtractCommandTags(string response)
    {
        var commands = new List<string>();
        var narrative = CommandTagRegex.Replace(response, match =>
        {
            var cmd = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(cmd))
                commands.Add(cmd);
            return string.Empty;
        });

        // 清理多余的空行
        var cleanedLines = narrative.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(line => line.TrimEnd())
            .ToList();
        // 移除末尾的空行
        while (cleanedLines.Count > 0 && string.IsNullOrWhiteSpace(cleanedLines[^1]))
            cleanedLines.RemoveAt(cleanedLines.Count - 1);

        narrative = string.Join("\n", cleanedLines).Trim();
        return (narrative, commands);
    }

    /// <summary>
    /// 提取 <recall>...</recall> 标签并返回关键词列表
    /// </summary>
    private static List<string> ExtractRecallTags(string response)
    {
        var keywords = new List<string>();
        var matches = RecallTagRegex.Matches(response);

        foreach (Match match in matches)
        {
            var keyword = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(keyword))
                keywords.Add(keyword);
        }

        return keywords;
    }

    /// <summary>
    /// 提取 <raw>...</raw> 标签并返回时间范围
    /// </summary>
    private static string? ExtractRawTags(string response)
    {
        var matches = RawTagRegex.Matches(response);
        if (matches.Count > 0)
        {
            return matches[0].Groups[1].Value.Trim();
        }
        return null;
    }

    /// <summary>
    /// 提取四层架构相关标签并返回字典
    /// </summary>
    private static Dictionary<string, string>? ExtractFourLayerTags(string response)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 提取 objective 标签
        var objectiveMatches = ObjectiveTagRegex.Matches(response);
        foreach (Match match in objectiveMatches)
        {
            var content = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(content))
                tags["objective"] = content;
        }

        // 提取 complete 标签
        var completeMatches = CompleteTagRegex.Matches(response);
        foreach (Match match in completeMatches)
        {
            var content = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(content))
                tags["complete"] = content;
        }

        // 提取 abandon 标签
        var abandonMatches = AbandonTagRegex.Matches(response);
        foreach (Match match in abandonMatches)
        {
            var content = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(content))
                tags["abandon"] = content;
        }

        // 提取 identity_merge 标签
        var identityMergeMatches = IdentityMergeTagRegex.Matches(response);
        foreach (Match match in identityMergeMatches)
        {
            var content = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(content))
                tags["identity_merge"] = content;
        }

        // 提取 event 标签
        var eventMatches = EventTagRegex.Matches(response);
        foreach (Match match in eventMatches)
        {
            var content = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(content))
                tags["event"] = content;
        }

        // 提取 memory 标签
        var memoryMatches = MemoryTagRegex.Matches(response);
        foreach (Match match in memoryMatches)
        {
            var content = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(content))
                tags["memory"] = content;
        }

        // 提取 entity_change 标签
        var entityChangeMatches = EntityChangeTagRegex.Matches(response);
        foreach (Match match in entityChangeMatches)
        {
            var content = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(content))
                tags["entity_change"] = content;
        }

        // 提取 scene_snapshot 标签
        var sceneSnapshotMatches = SceneSnapshotTagRegex.Matches(response);
        foreach (Match match in sceneSnapshotMatches)
        {
            var content = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(content))
                tags["scene_snapshot"] = content;
        }

        return tags.Count > 0 ? tags : null;
    }

    /// <summary>
    /// 处理四层架构标签
    /// 使用 StateMutationPipeline 以事务方式处理状态变更
    /// </summary>
    private async Task ProcessFourLayerTagsAsync(TrpgScope scope, string characterId, Dictionary<string, string> tags, TrpgPromptContext? trpgContext)
    {
        var groupId = scope.GroupId;
        if (tags.Count == 0)
            return;

        // 提取场景ID
        var sceneId = trpgContext?.CurrentSceneId;

        // 获取事务执行前的事件数量
        var eventsBefore = await _eventLog.ReplayEventsAsync(scope, 0, null);
        var eventCountBefore = eventsBefore.Count;

        // 构建事务
        var transaction = _mutationPipeline.BuildTransactionFromTags(tags, sceneId);

        // 执行事务
        var result = await _mutationPipeline.ExecuteTransactionAsync(scope, transaction, characterId);

        if (result.Success)
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 状态变更事务执行成功: {result.TransactionId}, 变更数量: {result.ExecutedMutations.Count}");
            
            // 获取事务执行后的事件数量
            var eventsAfter = await _eventLog.ReplayEventsAsync(scope, 0, null);
            var eventCountAfter = eventsAfter.Count;
            
            // 如果有新事件创建，计算叙事引力并分类
            if (eventCountAfter > eventCountBefore)
            {
                var newEvents = eventsAfter.Skip(eventCountBefore).ToList();
                foreach (var evt in newEvents)
                {
                    // 计算叙事引力
                    var weight = await _gravityEngine.CalculateGravityAsync(scope, characterId, evt);
                    
                    // 分类事件
                    var classification = new SalienceClassification();
                    var resultClassification = classification.ClassifyEvent(evt, weight);
                    
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] 事件分类: EventId={evt.EventId}, Type={resultClassification.Type}, Gravity={weight.NarrativeGravity:F2}");
                    
                    // 如果是基础骨架事件，写入永久记忆
                    if (resultClassification.Type == SalienceClassification.SalienceType.Foundational)
                    {
                        await _episodicMemory.DigestEventAsFoundationalAsync(scope, characterId, evt);
                    }
                }
            }
        }
        else
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 状态变更事务执行失败: {result.ErrorMessage}");
        }
    }

    /// <summary>
    /// 从 vision 字符串中提取场景ID
    /// </summary>
    private static string ExtractSceneId(string vision)
    {
        var match = Regex.Match(vision, @"Current_Scene_ID:\s*(\S+)");
        return match.Success ? match.Groups[1].Value : "unknown";
    }

    /// <summary>
    /// 提取 <attention>...</attention> 标签并解析为 AttentionMarker 对象
    /// </summary>
    private static List<AttentionMarker> ExtractAttentionTags(string response)
    {
        var markers = new List<AttentionMarker>();
        var matches = AttentionTagRegex.Matches(response);

        foreach (Match match in matches)
        {
            var jsonStr = match.Groups[1].Value.Trim();
            try
            {
                // 简单 JSON 解析（实际项目中应使用 JsonSerializer）
                var marker = new AttentionMarker();
                var parts = jsonStr.Split(new[] { "\", \"" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var keyValue = part.Split(new[] { "\":", ":\"" }, StringSplitOptions.RemoveEmptyEntries);
                    if (keyValue.Length == 2)
                    {
                        var key = keyValue[0].Trim().Trim('"').Trim();
                        var value = keyValue[1].Trim().Trim('"').Trim();

                        switch (key.ToLower())
                        {
                            case "type":
                                marker.Type = value;
                                break;
                            case "target":
                                marker.Target = value;
                                break;
                            case "keywords":
                                marker.Keywords = value.Split(new[] { "[", "]", "\",\"", "\", \"", "," }, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(k => k.Trim().Trim('"').Trim())
                                    .Where(k => !string.IsNullOrWhiteSpace(k))
                                    .ToList();
                                break;
                            case "scene_id":
                                marker.SceneId = value;
                                break;
                            case "importance":
                                if (double.TryParse(value, out var imp))
                                    marker.Importance = imp;
                                break;
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(marker.Type))
                    markers.Add(marker);
            }
            catch (Exception)
            {
                // JSON 解析失败，跳过该标记
            }
        }

        return markers;
    }

    private static string SanitizeNarrative(string narrative)
    {
        if (string.IsNullOrWhiteSpace(narrative)) return string.Empty;

        // 移除 [STATE] 区块
        narrative = StateBlockRegex.Replace(narrative, "");

        // 移除 <attention> 标签
        narrative = AttentionTagRegex.Replace(narrative, "");

        // 移除 <tag_pass/> 标签
        narrative = TagPassRegex.Replace(narrative, "");

        // 移除四层架构标签（这些标签不应显示给玩家）
        narrative = ObjectiveTagRegex.Replace(narrative, "");
        narrative = CompleteTagRegex.Replace(narrative, "");
        narrative = AbandonTagRegex.Replace(narrative, "");
        narrative = IdentityMergeTagRegex.Replace(narrative, "");
        narrative = EventTagRegex.Replace(narrative, "");
        narrative = MemoryTagRegex.Replace(narrative, "");
        narrative = EntityChangeTagRegex.Replace(narrative, "");
        narrative = SceneSnapshotTagRegex.Replace(narrative, "");

        var lines = narrative.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(line => line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !IsEmptyFormatPlaceholderLine(line))
            .ToList();

        return string.Join("\n", lines).Trim();
    }

    private static bool IsEmptyFormatPlaceholderLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed == "()"
               || trimmed == "（）"
               || trimmed == "#"
               || trimmed == "\"\""
               || trimmed == "“”";
    }

    private static string NormalizeCharacterHeader(string narrative, string characterName)
    {
        if (string.IsNullOrWhiteSpace(narrative) || string.IsNullOrWhiteSpace(characterName))
            return narrative;

        var lines = narrative.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .ToList();

        var firstNonEmptyIndex = lines.FindIndex(line => !string.IsNullOrWhiteSpace(line));
        if (firstNonEmptyIndex < 0)
            return narrative;

        var normalizedHeader = $"[{characterName.Trim()}]：";
        if (HeaderLineRegex.IsMatch(lines[firstNonEmptyIndex]))
        {
            lines[firstNonEmptyIndex] = normalizedHeader;
        }
        else
        {
            lines.Insert(firstNonEmptyIndex, normalizedHeader);
        }

        // 仅保留一个角色头，清理后续可能出现的错误角色名头，防止上下文污染
        var hasHeaderBeenKept = false;
        var normalizedLines = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (HeaderLineRegex.IsMatch(line))
            {
                if (!hasHeaderBeenKept)
                {
                    normalizedLines.Add(normalizedHeader);
                    hasHeaderBeenKept = true;
                }
                continue;
            }
            normalizedLines.Add(line);
        }

        return string.Join("\n", normalizedLines).Trim();
    }

    private void WriteTrpgLog(long groupId, long virtualId, string senderName, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            if (_trpgLogWriter != null)
            {
                _trpgLogWriter(groupId, virtualId, senderName, message);
                return;
            }

            var logManager = MDiceV2.Models.MessageProcessor.Instance?.TrpgLogManager;
            logManager?.WriteLog(groupId, virtualId, senderName, message);
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] 写入主程序跑团日志失败 (Group={groupId}, Char={senderName}): {ex.Message}");
        }
    }

    private static bool IsPassResponse(string response)
    {
        if (string.IsNullOrEmpty(response)) return true;

        // 精确匹配 [PASS]
        if (response.Equals("[PASS]", StringComparison.OrdinalIgnoreCase))
            return true;

        // 包含 [PASS] 但可能被包裹在其他文本中（宽松匹配）
        var upper = response.ToUpperInvariant();
        if (upper.Contains("[PASS]") && upper.Trim().Length <= 20)
            return true;

        return false;
    }
}
