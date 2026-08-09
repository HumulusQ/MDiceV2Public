using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AIMod;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// 桌面信息与角色认知提取器。
/// 职责：从桌面文本提取角色认知、桌面事件、场景认知缓存、物品变化、目标变化与身份候选。
/// </summary>
public class InfoExtractor
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly Func<List<ChatMessage>, Task<string?>> _apiCaller;
    private readonly TrpgContextPipeline _contextPipeline;
    private readonly EntityCanonicalizer _entityCanonicalizer;
    private readonly ObjectiveLayer _objectiveLayer;
    private readonly LlmCallTracker? _llmCallTracker;
    private readonly EntitySalienceService? _entitySalienceService;

    public InfoExtractor(
        IModContext context,
        ChatDatabase db,
        Func<List<ChatMessage>, Task<string?>> apiCaller,
        TrpgContextPipeline contextPipeline,
        EntityCanonicalizer entityCanonicalizer,
        ObjectiveLayer objectiveLayer,
        LlmCallTracker? llmCallTracker = null,
        EntitySalienceService? entitySalienceService = null)
    {
        _context = context;
        _db = db;
        _apiCaller = apiCaller;
        _contextPipeline = contextPipeline;
        _entityCanonicalizer = entityCanonicalizer;
        _objectiveLayer = objectiveLayer;
        _llmCallTracker = llmCallTracker;
        _entitySalienceService = entitySalienceService;
    }

    /// <summary>
    /// 从GM叙述中提取结构化信息
    /// </summary>
    public async Task<InfoExtractionResult> ExtractAsync(TrpgScope scope, string characterId, string gmNarrative)
    {
        try
        {
            // 构建信息提取prompt
            var promptText = await BuildExtractionPromptAsync(scope, characterId, gmNarrative);
            
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 信息提取模型调用 (World={scope.WorldId}, Group={scope.GroupId}, Char={characterId})");
            
            // 构建ChatMessage列表
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", $"{AimodPromptPrefixes.BackendCommonPrefixV1}\n\n{ExtractionSystemPrompt}"),
                new ChatMessage("user", promptText)
            };
            
            // 调用API
            var response = await CallTrackedAsync(scope, characterId, messages, "InfoExtractor", "InfoExtractor");
            if (string.IsNullOrWhiteSpace(response))
            {
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] 信息提取模型返回空响应");
                return new InfoExtractionResult();
            }

            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 信息提取模型响应: {response}");

            // 解析响应
            var result = ParseExtractionResponse(response);
            LogInfoExtractorStats(promptText, result);

            // 对提取的实体增加热度
            if (_entitySalienceService != null && result.HasContent)
            {
                await TouchEntityHeatFromExtractionAsync(scope, gmNarrative, result);
                // 定期衰减全量实体热度
                var currentFoldCount = await _db.GetCurrentFoldCountAsync(scope, characterId);
                await _entitySalienceService.DecayEntityHeatAsync(scope, currentFoldCount, halfLifeFolds: 8);
            }

            return result;
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 信息提取异常: {ex.Message}");
            return new InfoExtractionResult();
        }
    }

    /// <summary>
    /// 构建信息提取prompt
    /// </summary>
    private async Task<string> BuildExtractionPromptAsync(TrpgScope scope, string characterId, string gmNarrative)
    {
        var sb = new StringBuilder();
        sb.AppendLine(AimodPromptPrefixes.BackendCommonPrefixV1);
        sb.AppendLine();
        sb.AppendLine(ExtractionSystemPrompt);
        sb.AppendLine();

        var activeHistory = await _db.GetActiveHistoryAsync(scope, characterId);
        var allEntities = await _entityCanonicalizer.GetAllEntitiesAsync(scope);
        var timelineNodes = await _db.GetVisibleTimelineNodesAsync(scope, characterId);
        var characterIcMemory = await _db.GetCharacterMemoriesAsync(scope, characterId, limit: 16);
        var playerTableMemory = await _db.SearchPlayerTableMemoryNodesAsync(scope, gmNarrative, limit: 16);
        var activeObjectives = await _objectiveLayer.GetActiveObjectivesAsync(scope, characterId);
        var objectivesText = activeObjectives.Count == 0
            ? "无"
            : string.Join("\n", activeObjectives.Select(obj => $"- [{obj.Priority}] {obj.Description}"));
        var inventoryItems = await _db.GetActiveInventoryItemsAsync(scope, characterId);
        var inventoryText = inventoryItems.Count == 0
            ? "无"
            : string.Join("\n", inventoryItems.Select(item => $"- {item.DisplayName} x{item.Quantity:g}{item.Unit} state={item.State} confidence={item.Confidence:F2} evidence={item.LastEvidence}"));
        var activeAffectiveTags = await _db.GetActiveAffectiveTagStatesAsync(scope, characterId, 12);
        var affectiveText = AffectiveTagController.FormatForPrompt(activeAffectiveTags);
        if (string.IsNullOrWhiteSpace(affectiveText))
            affectiveText = "无";

        var state = new TrpgRuntimeState
        {
            CurrentSceneId = "scene_from_extractor",
            PresentEntities = new List<string> { characterId }, // 仅当前角色，不包含全量实体
            LatestGmNarrative = gmNarrative
        };
        var contextPack = await new TrpgAgentContextPackBuilder(_db, _context, _entitySalienceService).BuildAsync(
            scope,
            new AiCharacterEntry { CharacterId = characterId, WorldId = scope.WorldId, GroupId = scope.GroupId, TeamName = scope.TeamName, OwnerUserId = scope.OwnerUserId },
            state,
            activeHistory,
            gmNarrative,
            "由桌面文本与最近缓存构成，非客观世界真相。",
            objectivesText,
            inventoryText,
            affectiveText,
            timelineNodes,
            characterIcMemory,
            playerTableMemory);

        sb.AppendLine("## 大上下文视图");
        sb.AppendLine(contextPack.ForInfoExtractorFullView());
        sb.AppendLine();

        // 当前IC内容
        sb.AppendLine("## 当前IC内容");
        sb.AppendLine(gmNarrative);
        sb.AppendLine();

        // 提取指令
        sb.AppendLine("## 任务");
        sb.AppendLine("请分析上述IC内容，提取以下信息并使用对应标签输出：");
        sb.AppendLine("1. 如果描述了新场景或场景切换 → 使用 <scene_snapshot> 标签");
        sb.AppendLine("2. 如果模型准备创建新NPC或新角色 → 必须同时输出 <entity_change> 与 <new_entity_check>");
        sb.AppendLine("3. 只有 GM 明确确认 X 就是 Y、GM 明确纠正、GM 明确“你认出 X 是 Y”时 → 才使用 <identity_merge> 标签");
        sb.AppendLine("4. 如果分配了新任务或目标 → 使用 <objective> 标签");
        sb.AppendLine("5. 如果完成了目标 → 使用 <complete> 标签");
        sb.AppendLine("6. 如果放弃了目标 → 使用 <abandon> 标签");
        sb.AppendLine("7. 对GM叙述中每个值得记录的叙事时刻，使用 <event> 标签，写一句话散文描述（主语+动词+结果/状态）。每次GM叙述应产出1~3条event。");
        sb.AppendLine("8. 对角色可确认的事实性认知，使用 <fact> 标签。");
        sb.AppendLine("9. 如果实体间关系发生变化，使用 <relationship> 标签。");
        sb.AppendLine("10. 每次GM叙述都必须输出一个 <summary> 标签，作为当前情景摘要。");
        sb.AppendLine("11. 如果GM明确描述在场人物变化（进入/离开/只有X在/这里空无一人等），输出 <presence_snapshot> 标签。");
        sb.AppendLine();
        sb.AppendLine("【标签格式】");
        sb.AppendLine("- <scene_snapshot>场景ID|场景名称|在场实体列表</scene_snapshot>");
        sb.AppendLine("- <entity_change>显示名称|别名1,别名2,...</entity_change>");
        sb.AppendLine("- <new_entity_check>candidate_name|possible_existing_entity_id_or_name|decision|reason</new_entity_check>");
        sb.AppendLine("- <identity_merge>旧名称->新名称</identity_merge>");
        sb.AppendLine("- <objective>目标描述</objective>");
        sb.AppendLine("- <complete>目标描述</complete>");
        sb.AppendLine("- <abandon>目标描述</abandon>");
        sb.AppendLine("- <event>主角名+动作/经历，如：波柚惊醒后发现车窗被浓雾笼罩，无法辨认位置</event>");
        sb.AppendLine("- <fact>实体名|事实描述|分类</fact>");
        sb.AppendLine("- <relationship>实体A|实体B|关系类型|变化值|是否创伤|原因</relationship>");
        sb.AppendLine("- <summary>上下文概括</summary>");
        sb.AppendLine("- <presence_snapshot>scene_id|present_entities|absent_entities|is_full_snapshot|authority|evidence</presence_snapshot>");
        sb.AppendLine("- <entity_profile>实体名|核心简介|稳定事实1;稳定事实2|当前状态</entity_profile>");
        sb.AppendLine();
        sb.AppendLine("【特别注意】");
        sb.AppendLine("- 你不是 KP，不维护客观世界真相，只整理桌面文本中的角色认知、桌面记录、场景认知缓存、物品变化、目标变化和身份候选。");
        sb.AppendLine("- NPC 自称、他人称呼、玩家猜测、暧昧反应，不得直接 identity_merge。");
        sb.AppendLine("- new_entity_check decision 只能是 CreateNew / MapToExisting / HoldCandidate。");
        sb.AppendLine("- CreateNew 允许创建新实体；MapToExisting 给已有实体增加别名或显示名候选；HoldCandidate 只作为 PL 桌面线索，不创建实体。");
        sb.AppendLine("- 缺少 new_entity_check 时，程序会保守默认为 HoldCandidate，除非文本明确写“新人物出现/进入/首次登场”。");
        sb.AppendLine("- <event>必须是有实质内容的叙事描述，禁止写'叙事事件'或'重要事件'等空泛词语");
        sb.AppendLine("- 只输出标签，不要输出其他内容。如果没有需要提取的信息，输出 [NONE]。");
        sb.AppendLine();
        sb.AppendLine("【在场快照规则】");
        sb.AppendLine("- GM 明确纠正在场人物时必须输出 presence_snapshot。");
        sb.AppendLine("- present_entities 用逗号分隔；absent_entities 用逗号分隔。");
        sb.AppendLine("- is_full_snapshot=true 表示当前列表完整替换在场状态；false 表示增量更新。");
        sb.AppendLine("- authority 为 GMCorrection / SceneDescription / NarrativeInference。\"这里只有A\"\"房间里只有你\"\"B不在\"\"C已经离开\"\"D跟你一起进屋\"都属于presence_snapshot。");
        sb.AppendLine("- GMCorrection 权威最高。仅被提及、目标相关、回忆中、已离开的人不得进入 present_entities。");
        sb.AppendLine();
        sb.AppendLine("【实体简介规则】");
        sb.AppendLine("- 当 GM 揭示角色的关键设定、背景或状态变化时，输出 <entity_profile>。");
        sb.AppendLine("- 核心简介控制在 1-2 句话，不超过 160 字。");
        sb.AppendLine("- 稳定事实用分号分隔；当前状态用简短描述。");

        sb.AppendLine();
        sb.AppendLine("情感标签候选：");
        sb.AppendLine("- 若 IC 内容显示角色产生、维持、压抑、释放或转化了情绪/关系态度/压力状态，则输出 affective_tag。来源可以是 GM 叙事、玩家 IC 行动、角色自身台词或动作，但不要从 OOC 或纯规则讨论中提取。");
        sb.AppendLine("- 推荐格式：<affective_tag>tag_type|display_name|source_key|target_entity_id|intensity_tier|effect_kind|stack_policy_hint|novelty|evidence|reason</affective_tag>。");
        sb.AppendLine("- source_key 必须稳定描述触发源，如 fog_window、direct_threat_knife、npc_meiqiu_trust；同一氛围或同一对象重复出现时沿用同一个 source_key。");
        sb.AppendLine("- GM 明确纠正或否认优先。玩家/AI 自述情绪可作为 SelfReported/Expressed 证据，但权重应低于 GM 明确叙事。");
        sb.AppendLine("- 不要把短暂语气夸大成长期关系事实；不要把 OOC 玩笑写入情绪状态。");
        sb.AppendLine("- 示例：<affective_tag>Fear.Ambient|寒意|fog_window||Mild|ApplyOrRefresh|RefreshOnly|Medium|车窗外浓雾让角色不安|环境带来不安</affective_tag>");
        sb.AppendLine("- 示例：<affective_tag>Trust.Damage|残留不信任|meiqiu_lied|煤球|Moderate|ApplyOrRefresh|Escalating|High|煤球的话与已知事实矛盾|明确矛盾损害信任</affective_tag>");
        sb.AppendLine("- 可用 tag_type: Fear.Ambient, Fear.DirectThreat, Fear.Shock, Alertness.EnvironmentalThreat, Trust.Damage, Suspicion.Entity, Anger.Suppressed, Anger.Open, Sadness.Loss, Shame.Exposed, Affection.Warmth, Stress.Pressure, NeedForReassurance, CombatReadiness。");
        sb.AppendLine("- 重复氛围仅维持或刷新，不要在没有新证据时升级。");

        sb.AppendLine();
        sb.AppendLine("物品栏变化：");
        sb.AppendLine("输出格式：");
        sb.AppendLine("<inventory_mutation>operation|item_key|display_name|quantity_delta|quantity_set|unit|new_state|source_kind|authority_rank|confidence|target_entity_id|is_full_snapshot|evidence</inventory_mutation>");
        sb.AppendLine("规则：");
        sb.AppendLine("- 可以从角色/玩家明确行动中乐观更新已有物品或场景中合理存在的物品。");
        sb.AppendLine("- 不要把“看到一个物品”当成获得。");
        sb.AppendLine("- 不要凭空生成未出现、未获得、未配置的关键物品。");
        sb.AppendLine("- GM 否认、纠正、完整清点必须输出 correction 或 snapshot，authority_rank=90。");
        sb.AppendLine("- “你现在只有 A、B、C”属于 is_full_snapshot=true。");

        return sb.ToString();
    }

    /// <summary>
    /// 解析信息提取响应
    /// </summary>
    private InfoExtractionResult ParseExtractionResponse(string response)
    {
        var result = new InfoExtractionResult();

        if (response.Trim().Equals("[NONE]", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        // 提取 scene_snapshot
        var sceneMatches = Regex.Matches(response, @"<scene_snapshot>(.*?)</scene_snapshot>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in sceneMatches)
        {
            result.SceneSnapshots.Add(match.Groups[1].Value.Trim());
        }

        // 提取 entity_change
        var entityMatches = Regex.Matches(response, @"<entity_change>(.*?)</entity_change>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in entityMatches)
        {
            result.EntityChanges.Add(match.Groups[1].Value.Trim());
        }

        // 提取 identity_merge
        var identityMergeMatches = Regex.Matches(response, @"<identity_merge>(.*?)</identity_merge>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in identityMergeMatches)
        {
            result.IdentityMerges.Add(match.Groups[1].Value.Trim());
        }

        // 提取 objective
        var objectiveMatches = Regex.Matches(response, @"<objective>(.*?)</objective>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in objectiveMatches)
        {
            result.Objectives.Add(match.Groups[1].Value.Trim());
        }

        // 提取 complete
        var completeMatches = Regex.Matches(response, @"<complete>(.*?)</complete>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in completeMatches)
        {
            result.CompletedObjectives.Add(match.Groups[1].Value.Trim());
        }

        // 提取 abandon
        var abandonMatches = Regex.Matches(response, @"<abandon>(.*?)</abandon>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in abandonMatches)
        {
            result.AbandonedObjectives.Add(match.Groups[1].Value.Trim());
        }

        // 提取 event
        var eventMatches = Regex.Matches(response, @"<event>(.*?)</event>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in eventMatches)
        {
            result.Events.Add(match.Groups[1].Value.Trim());
        }

        // 提取 fact
        var factMatches = Regex.Matches(response, @"<fact>(.*?)</fact>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in factMatches)
        {
            result.Facts.Add(match.Groups[1].Value.Trim());
        }

        // 提取 relationship
        var relationshipMatches = Regex.Matches(response, @"<relationship>(.*?)</relationship>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in relationshipMatches)
        {
            result.Relationships.Add(match.Groups[1].Value.Trim());
        }

        var affectiveMatches = Regex.Matches(response, @"<affective_tag>(.*?)</affective_tag>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in affectiveMatches)
        {
            var candidate = ParseAffectiveTagCandidate(match.Groups[1].Value.Trim());
            if (candidate != null)
                result.AffectiveTagCandidates.Add(candidate);
        }

        var inventoryMatches = Regex.Matches(response, @"<inventory_mutation>(.*?)</inventory_mutation>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in inventoryMatches)
        {
            var mutation = ParseInventoryMutation(match.Groups[1].Value.Trim());
            if (mutation != null)
                result.InventoryMutations.Add(mutation);
        }

        var newEntityMatches = Regex.Matches(response, @"<new_entity_check>(.*?)</new_entity_check>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in newEntityMatches)
        {
            var check = ParseNewEntityCheck(match.Groups[1].Value.Trim());
            if (check != null)
                result.NewEntityChecks.Add(check);
        }

        // 提取 summary
        var summaryMatches = Regex.Matches(response, @"<summary>(.*?)</summary>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in summaryMatches)
        {
            result.Summaries.Add(match.Groups[1].Value.Trim());
        }

        // 提取 presence_snapshot
        var presenceMatches = Regex.Matches(response, @"<presence_snapshot>(.*?)</presence_snapshot>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in presenceMatches)
        {
            var snapshot = ParsePresenceSnapshot(match.Groups[1].Value.Trim());
            if (snapshot != null)
                result.PresenceSnapshots.Add(snapshot);
        }

        // 提取 entity_profile
        var profileMatches = Regex.Matches(response, @"<entity_profile>(.*?)</entity_profile>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in profileMatches)
        {
            result.EntityProfiles.Add(match.Groups[1].Value.Trim());
        }

        return result;
    }

    private NewEntityCheck? ParseNewEntityCheck(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw.Split('|').Select(x => x.Trim()).ToArray();
        var candidateName = Part(parts, 0);
        if (string.IsNullOrWhiteSpace(candidateName))
            return null;

        var decision = Part(parts, 2);
        if (!Enum.TryParse<NewEntityCheckDecision>(decision, true, out var parsedDecision))
            parsedDecision = NewEntityCheckDecision.HoldCandidate;

        return new NewEntityCheck
        {
            CandidateName = candidateName,
            PossibleExistingEntityIdOrName = Part(parts, 1),
            Decision = parsedDecision,
            Reason = Part(parts, 3)
        };
    }

    private void LogInfoExtractorStats(string promptText, InfoExtractionResult result)
    {
        var createNew = result.NewEntityChecks.Count(c => c.Decision == NewEntityCheckDecision.CreateNew);
        var mapToExisting = result.NewEntityChecks.Count(c => c.Decision == NewEntityCheckDecision.MapToExisting);
        var holdCandidate = result.NewEntityChecks.Count(c => c.Decision == NewEntityCheckDecision.HoldCandidate);
        var timelineLines = Regex.Matches(promptText, @"^\s*-\s*(L1|L2|L3):", RegexOptions.Multiline).Count;
        _context.Log(LogLevel.Info,
            $"[AIMod:TRPG] InfoExtractor stats | full_context_chars={promptText.Length} " +
            $"entity_count={result.EntityChanges.Count} timeline_lines_count={timelineLines} " +
            $"new_entity_check_count={result.NewEntityChecks.Count} create_new_count={createNew} " +
            $"map_to_existing_count={mapToExisting} hold_candidate_count={holdCandidate}");
    }

    private static AffectiveTagCandidate? ParseAffectiveTagCandidate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text.Split('|').Select(x => x.Trim()).ToArray();
        var tagType = Part(parts, 0);
        if (string.IsNullOrWhiteSpace(tagType))
            return null;

        var displayName = "";
        var sourceKey = "";
        var targetEntityId = "";
        var intensityTier = "Mild";
        var effectKind = "ApplyOrRefresh";
        var stackPolicyHint = "";
        var novelty = "Medium";
        var evidence = "";
        var reason = "";

        if (parts.Length >= 8)
        {
            displayName = Part(parts, 1);
            sourceKey = Part(parts, 2);
            targetEntityId = Part(parts, 3);
            intensityTier = FirstNonEmpty(Part(parts, 4), "Mild");
            effectKind = FirstNonEmpty(Part(parts, 5), "ApplyOrRefresh");
            stackPolicyHint = Part(parts, 6);
            novelty = FirstNonEmpty(Part(parts, 7), "Medium");
            evidence = Part(parts, 8);
            reason = Part(parts, 9);
        }
        else if (parts.Length >= 4 && !LooksLikeTier(Part(parts, 2)))
        {
            displayName = Part(parts, 1);
            sourceKey = Part(parts, 2);
            targetEntityId = Part(parts, 3);
            evidence = Part(parts, 4);
        }
        else
        {
            sourceKey = Part(parts, 1);
            intensityTier = LooksLikeTier(Part(parts, 2)) ? Part(parts, 2) : "Mild";
            evidence = LooksLikeTier(Part(parts, 2)) ? Part(parts, 3) : FirstNonEmpty(Part(parts, 2), Part(parts, 3));
            targetEntityId = Part(parts, 4);
            effectKind = FirstNonEmpty(Part(parts, 5), "ApplyOrRefresh");
            stackPolicyHint = Part(parts, 6);
            novelty = FirstNonEmpty(Part(parts, 7), "Medium");
            reason = Part(parts, 8);
        }

        if (string.IsNullOrWhiteSpace(sourceKey))
            sourceKey = BuildAffectiveSourceKey(tagType, targetEntityId, evidence, displayName);

        return new AffectiveTagCandidate
        {
            TagType = tagType,
            DisplayName = displayName,
            SourceKey = sourceKey,
            TargetEntityId = string.IsNullOrWhiteSpace(targetEntityId) ? null : targetEntityId,
            IntensityTier = intensityTier,
            EffectKind = effectKind,
            StackPolicyHint = stackPolicyHint,
            Novelty = novelty,
            Evidence = evidence,
            Reason = reason
        };
    }

    private InventoryMutation? ParseInventoryMutation(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            var parts = raw.Split('|').Select(x => x.Trim()).ToArray();
            var operation = Part(parts, 0);
            var itemKey = Part(parts, 1);
            var displayName = Part(parts, 2);
            if (string.IsNullOrWhiteSpace(operation) || (string.IsNullOrWhiteSpace(itemKey) && string.IsNullOrWhiteSpace(displayName)))
                return null;

            return new InventoryMutation
            {
                Operation = operation,
                ItemKey = itemKey,
                DisplayName = displayName,
                QuantityDelta = ParseDoublePart(parts, 3, 0),
                QuantitySet = TryParseNullableDouble(Part(parts, 4)),
                Unit = Part(parts, 5),
                NewState = Part(parts, 6),
                SourceKind = FirstNonEmpty(Part(parts, 7), "PlayerDeclared"),
                AuthorityRank = ParseIntPart(parts, 8, 30),
                Confidence = ParseDoublePart(parts, 9, 0.7),
                TargetEntityId = Part(parts, 10),
                IsFullSnapshot = ParseBoolPart(parts, 11),
                Evidence = Part(parts, 12)
            };
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] inventory_mutation parse skipped: {ex.Message}");
            return null;
        }
    }

    private static int ParseIntPart(string[] parts, int index, int fallback)
        => int.TryParse(Part(parts, index), out var value) ? value : fallback;

    private static double ParseDoublePart(string[] parts, int index, double fallback)
        => double.TryParse(Part(parts, index), out var value) ? value : fallback;

    private static double? TryParseNullableDouble(string value)
        => double.TryParse(value, out var parsed) ? parsed : null;

    private static bool ParseBoolPart(string[] parts, int index)
    {
        var value = Part(parts, index);
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string Part(string[] parts, int index)
        => index >= 0 && index < parts.Length ? parts[index].Trim() : "";

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";

    private static bool LooksLikeTier(string value)
        => value.Equals("Trace", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Mild", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Medium", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Moderate", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Strong", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Extreme", StringComparison.OrdinalIgnoreCase);

    private static string BuildAffectiveSourceKey(string tagType, string targetEntityId, string evidence, string displayName)
    {
        var prefix = NormalizeSourceKey(tagType);
        var seed = FirstNonEmpty(targetEntityId, evidence, displayName, tagType);
        var normalizedSeed = NormalizeSourceKey(seed);
        if (string.IsNullOrWhiteSpace(prefix))
            prefix = "affect";
        if (string.IsNullOrWhiteSpace(normalizedSeed))
            normalizedSeed = "scene";
        return $"{prefix}_{normalizedSeed}";
    }

    private static string NormalizeSourceKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var sb = new StringBuilder();
        var lastWasSeparator = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                sb.Append('_');
                lastWasSeparator = true;
            }

            if (sb.Length >= 64)
                break;
        }

        return sb.ToString().Trim('_');
    }

    /// <summary>
    /// 从提取结果中解析涉及的实体并增加热度
    /// </summary>
    private async Task TouchEntityHeatFromExtractionAsync(TrpgScope scope, string gmNarrative, InfoExtractionResult result)
    {
        if (_entitySalienceService == null) return;

        var touchedEntityIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evidence = gmNarrative.Length > 100 ? gmNarrative.Substring(0, 100) : gmNarrative;

        // 从 entity_change 中提取实体名并解析为 EntityId
        foreach (var entityChange in result.EntityChanges)
        {
            var parts = entityChange.Split('|');
            var displayName = parts.Length > 0 ? parts[0].Trim() : "";
            if (string.IsNullOrWhiteSpace(displayName)) continue;

            var entityId = await _entityCanonicalizer.ResolveEntityIdAsync(scope, displayName) ?? displayName;
            if (touchedEntityIds.Add(entityId))
            {
                await _entitySalienceService.TouchEntityAsync(
                    scope, entityId,
                    deltaHeat: 2.0,
                    source: "InfoExtractor",
                    evidence: evidence);
            }
        }

        // 从 NewEntityChecks 中提取候选实体名
        foreach (var check in result.NewEntityChecks)
        {
            if (string.IsNullOrWhiteSpace(check.CandidateName)) continue;
            if (check.Decision == NewEntityCheckDecision.CreateNew)
            {
                var entityId = await _entityCanonicalizer.ResolveEntityIdAsync(scope, check.CandidateName) ?? check.CandidateName;
                if (touchedEntityIds.Add(entityId))
                {
                    await _entitySalienceService.TouchEntityAsync(
                        scope, entityId,
                        deltaHeat: 1.5,
                        source: "InfoExtractor",
                        evidence: evidence);
                }
            }
        }

        _context.Log(LogLevel.Debug,
            $"[AIMod:TRPG] EntitySalience touch from InfoExtractor | touched_entity_count={touchedEntityIds.Count}");
    }

    private static PresenceSnapshot? ParsePresenceSnapshot(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split('|').Select(x => x.Trim()).ToArray();
        if (parts.Length < 4) return null;
        return new PresenceSnapshot
        {
            SceneId = Part(parts, 0),
            PresentEntities = Part(parts, 1).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            AbsentEntities = Part(parts, 2).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            IsFullSnapshot = Part(parts, 3).Equals("true", StringComparison.OrdinalIgnoreCase),
            Authority = FirstNonEmpty(Part(parts, 4), "NarrativeInference"),
            Evidence = Part(parts, 5)
        };
    }

    private const string ExtractionSystemPrompt = """
你是一个桌面信息与角色认知提取器，负责从TRPG桌面文本中提取角色认知与桌面记录。

你的职责：
1. 维护场景认知缓存：识别场景切换、在场实体与角色可确认处境
2. 维护NPC身份候选：识别新NPC、别名候选、GM明确身份确认
3. 维护任务目标：识别新目标，标记完成/放弃的目标
4. 维护桌面事件记录：记录重要桌面事件
5. 维护角色事实性认知：识别角色可确认的稳定事实（如"老王知道钥匙位置"、"老王左眼瞎了"）
6. 维护关系变化：识别实体间关系的变化（如"老王对玩家信任增加"）
7. 概括上下文：在需要时概括当前上下文

你不是 KP/GM，不维护客观世界真相。所有输出都是对桌面文本的提取建议，必须保守处理身份与新实体。

【身份识别规则】
- NPC首次出现时用描述性称呼（如"风衣女孩"）→ 使用 <entity_change> 创建实体
- 准备创建实体时必须同时输出 <new_entity_check>candidate_name|possible_existing_entity_id_or_name|decision|reason</new_entity_check>
- 只有 GM 明确确认 X 就是 Y、GM 明确纠正、GM 明确“你认出 X 是 Y”时，才使用 <identity_merge>
- NPC 自称、他人称呼、玩家猜测、暧昧反应，不得直接 identity_merge
- identity_merge格式：<identity_merge>旧名称->新名称</identity_merge>
- new_entity_check decision 只能是 CreateNew / MapToExisting / HoldCandidate

标签格式：
- <scene_snapshot>场景ID|场景名称|在场实体列表</scene_snapshot>
- <entity_change>显示名称|别名1,别名2,...</entity_change>
- <new_entity_check>candidate_name|possible_existing_entity_id_or_name|decision|reason</new_entity_check>
- <identity_merge>旧名称->新名称</identity_merge>
- <objective>目标描述</objective>
- <complete>目标描述</complete>
- <abandon>目标描述</abandon>
- <event>事件描述</event>
- <fact>实体名|事实描述|分类</fact>
- <relationship>实体A|实体B|关系类型|变化值|是否创伤|原因</relationship>
- <summary>上下文概括</summary>

注意事项：
- 实体使用别名系统，支持身份转换，不要固定实体ID
- identity_merge 只用于 GM 明确确认的身份转换
- complete用于标记目标完成，abandon用于标记目标放弃
- fact用于记录角色事实性认知，分类包括：knowledge（知识）、physical（身体特征）、affiliation（所属组织）、ability（能力）
- relationship用于记录关系变化，关系类型包括：trust（信任）、affection（好感）、respect（尊重）、fear（恐惧）
- relationship变化值范围：-100到100，正值表示正面变化，负值表示负面变化
- relationship是否创伤：true表示重大影响事件（如背叛、救命），false表示普通事件
- 概括应该简洁明了，包含当前关键信息
- 只输出标签，不要输出其他解释或分析
""";

    private const string TimelineExtractionSystemPrompt = """
你是一个TRPG叙事分析助手，专门从GM叙述中提取有叙事价值的事件并按层级分类。

层级定义：
- L1：场景级骨架，只记录“进入/离开地点、场景目标改变、核心谜题被揭示、不可逆重大转折”。同一场景通常只有少量L1。
- L2：当前L1下的关键推进，记录新信息揭示、处境明确改变、行动产生明确结果、重要异常现象。
- L3：具体动作及GM反馈，记录玩家尝试、观察、移动、对话等局部步骤。

分层原则：
- 默认优先输出L2或L3；只有达到“场景级骨架”标准时才输出L1。
- 如果已有L1能承载当前事件，请将新事件放入L2或L3，并在父节点关键词中写已有L1的关键词。

正向标准：
- 只输出具体剧情信息。必须点明明确角色/实体/地点/对象之一，并说明行动、状态变化、信息揭示、关系变化、目标变化、危险变化或结果。
- 删除后不影响后续理解的内容不得输出。
- 如果没有合格内容，输出 [NONE]。

输出格式（每行一条）：
[L1/L2/L3] 事件描述 || 父节点关键词 || importance:1-10 || foreshadowing:true/false

importance评分：
1-3：过渡动作、气氛描写、无后果的简单行动
4-6：揭示新信息、改变局部状态
7-8：场景级转折、重大发现
9-10：篇章级转折、核心谜题揭示

foreshadowing=true（满足任一）：
- 出现无法解释的现象
- 出现未被识别的人物/符号/物品
- GM叙述含"似乎""仿佛""隐约""还不清楚"等模糊词
- 信息明显不完整，需要后续揭示

合并规则（输出前执行）：
- 同一玩家连续动作服务同一目标 → 合并为一条L3
- 同一目标多次尝试不同方式 → 合并为一次总结
- 对同一事物的分步观察 → 合并为一次完整观察

禁止：
- 禁止"叙事事件""事件发生"等空泛描述
- 禁止将纯气氛描写提取为L2或L1
- 禁止为了给L2寻找父节点而新造L1
- 禁止把同一场景内的每次发现都提升为L1
- 单次最多输出5条，超出则保留importance最高的
- 如果没有符合“具体剧情信息”的内容，输出 [NONE]

只输出格式行，不要有其他内容。若无值得记录的事件，输出 [NONE]。
""";

    /// <summary>
    /// 从GM叙述中提取时间轴事件，供TimelineWriter写入分层时间轴
    /// </summary>
    public async Task<List<TimelineEventExtraction>> ExtractTimelineEventsAsync(
        TrpgScope scope,
        string characterId,
        string sceneId,
        string gmNarrative,
        List<TimelineNode> existingL1Nodes,
        List<TimelineNode> existingL2Nodes)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## 当前场景：{sceneId}");
            sb.AppendLine();

            if (existingL1Nodes.Count > 0)
            {
                sb.AppendLine("## 当前场景已有L1节点（供父节点匹配参考）：");
                foreach (var n in existingL1Nodes.TakeLast(5))
                    sb.AppendLine($"  [{n.Id}] {n.Content}");
                sb.AppendLine();
            }

            if (existingL2Nodes.Count > 0)
            {
                sb.AppendLine("## 当前场景已有L2节点（供父节点匹配参考）：");
                foreach (var n in existingL2Nodes.TakeLast(8))
                    sb.AppendLine($"  [{n.Id}] {n.Content}");
                sb.AppendLine();
            }

            sb.AppendLine("## GM叙述：");
            sb.AppendLine(gmNarrative);

            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", $"{AimodPromptPrefixes.BackendCommonPrefixV1}\n\n{TimelineExtractionSystemPrompt}"),
                new ChatMessage("user", sb.ToString())
            };

            var response = await CallTrackedAsync(scope, characterId, messages, "InfoExtractor", "TimelineEventExtraction");
            if (string.IsNullOrWhiteSpace(response) || response.Trim() == "[NONE]")
                return new List<TimelineEventExtraction>();

            var results = TimelineEventExtraction.ParseAll(response);
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 时间轴事件提取: {results.Count} 条 (Scene={sceneId})");
            return results;
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 时间轴事件提取失败: {ex.Message}");
            return new List<TimelineEventExtraction>();
        }
    }

    private async Task<string?> CallTrackedAsync(TrpgScope scope, string characterId, List<ChatMessage> messages, string agentName, string requestKind)
    {
        if (_llmCallTracker != null)
            return await _llmCallTracker.CallAsync(scope, characterId, messages, agentName, requestKind, _apiCaller);

        return await _apiCaller.Invoke(messages);
    }
}

/// <summary>
/// 信息提取结果
/// </summary>
public class InfoExtractionResult
{
    public List<string> SceneSnapshots { get; set; } = new();
    public List<string> EntityChanges { get; set; } = new();
    public List<string> IdentityMerges { get; set; } = new();
    public List<string> Objectives { get; set; } = new();
    public List<string> CompletedObjectives { get; set; } = new();
    public List<string> AbandonedObjectives { get; set; } = new();
    public List<string> Events { get; set; } = new();
    public List<string> Facts { get; set; } = new();
    public List<string> Relationships { get; set; } = new();
    public List<string> Summaries { get; set; } = new();
    public List<AffectiveTagCandidate> AffectiveTagCandidates { get; set; } = new();
    public List<InventoryMutation> InventoryMutations { get; set; } = new();
    public List<NewEntityCheck> NewEntityChecks { get; set; } = new();
    public List<PresenceSnapshot> PresenceSnapshots { get; set; } = new();
    public List<string> EntityProfiles { get; set; } = new();

    public bool HasContent => SceneSnapshots.Count > 0 || EntityChanges.Count > 0 || IdentityMerges.Count > 0 ||
                             Objectives.Count > 0 || CompletedObjectives.Count > 0 || AbandonedObjectives.Count > 0 ||
                             Events.Count > 0 || Facts.Count > 0 || Relationships.Count > 0 || Summaries.Count > 0 ||
                             AffectiveTagCandidates.Count > 0 || InventoryMutations.Count > 0 || NewEntityChecks.Count > 0 ||
                             PresenceSnapshots.Count > 0 || EntityProfiles.Count > 0;
}

public sealed class NewEntityCheck
{
    public string CandidateName { get; set; } = "";
    public string PossibleExistingEntityIdOrName { get; set; } = "";
    public NewEntityCheckDecision Decision { get; set; } = NewEntityCheckDecision.HoldCandidate;
    public string Reason { get; set; } = "";
}

public enum NewEntityCheckDecision
{
    CreateNew,
    MapToExisting,
    HoldCandidate
}

public sealed class PresenceSnapshot
{
    public string SceneId { get; set; } = "";
    public List<string> PresentEntities { get; set; } = new();
    public List<string> AbsentEntities { get; set; } = new();
    public bool IsFullSnapshot { get; set; }
    public string Authority { get; set; } = "NarrativeInference";
    public string Evidence { get; set; } = "";
}
