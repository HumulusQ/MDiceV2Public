using MDiceV2.Interfaces.Mod;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AIMod.Trpg;

public class StateInterceptor
{
    private static readonly TimeSpan SharedExtractionCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, CachedTask<InfoExtractionResult>> SharedInfoExtractionCache = new();
    private static readonly ConcurrentDictionary<string, CachedTask<List<TimelineEventExtraction>>> SharedTimelineExtractionCache = new();
    private static readonly Regex SceneRegex = new(@"(?:场景|地点|来到|进入|抵达|前往|回到)[:：\s]*(?<scene>[\u4e00-\u9fa5A-Za-z0-9_\-]{2,40})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NameRegex = new(@"(?:^|[\s，。！？、：:""“”'‘’\(\)（）\[\]【】])(?<name>[\u4e00-\u9fa5A-Za-z]{2,10})(?:走来|出现|进入|离开|看向|站在|坐在|冲向|说道|问道|喊道|低声说|说)(?=$|[\s，。！？、：:""“”'‘’\)）\]】])", RegexOptions.Compiled);

    //private static readonly Regex SelfIntroRegex = new(@"(?:^|[\s，。！？、：:\"\"“”'‘’\(\)（）\[\]【】])(?:我是|我叫|我名叫|叫我|称我)(?<name>[\u4e00-\u9fa5A-Za-z]{2,10})(?=$|[\s，。！？、：:\"\"“”'‘’\)）\]】])", RegexOptions.Compiled);

    private static readonly HashSet<string> InvalidEntityTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "但是","然后","如果","因为","所以","并且","而且","不过","现在","已经","应该","可以","需要","你能","你们","他们","她们",
        "车窗外面","车轮行驶","终点站","黑暗","迷雾","轨道","碰撞声",
        "实验参与者","参与者","研究人员","研究员"
    };

    private readonly ChatDatabase _db;
    private readonly TrpgStateCache _stateCache;
    private readonly IModContext _context;
    private readonly AttentionBuffer? _attentionBuffer;
    private readonly MemoryWatchdog? _memoryWatchdog;
    private readonly InfoExtractor? _infoExtractor;
    private readonly StateMutationPipeline? _mutationPipeline;
    private readonly EntityCanonicalizer? _entityCanonicalizer;
    private readonly TimelineWriter? _timelineWriter;
    private readonly SceneTransitionHandler? _sceneTransitionHandler;
    private readonly AffectiveTagController? _affectiveTagController;

    public StateInterceptor(ChatDatabase db, TrpgStateCache stateCache, IModContext context, AttentionBuffer? attentionBuffer = null, MemoryWatchdog? memoryWatchdog = null, InfoExtractor? infoExtractor = null, StateMutationPipeline? mutationPipeline = null, EntityCanonicalizer? entityCanonicalizer = null, TimelineWriter? timelineWriter = null, SceneTransitionHandler? sceneTransitionHandler = null, AffectiveTagController? affectiveTagController = null)
    {
        _db = db;
        _stateCache = stateCache;
        _context = context;
        _attentionBuffer = attentionBuffer;
        _memoryWatchdog = memoryWatchdog;
        _infoExtractor = infoExtractor;
        _mutationPipeline = mutationPipeline;
        _entityCanonicalizer = entityCanonicalizer;
        _timelineWriter = timelineWriter;
        _sceneTransitionHandler = sceneTransitionHandler;
        _affectiveTagController = affectiveTagController;
    }

    /// <summary>
    /// 消息现实层分类：判断消息是否为 IC（场内内容）
    /// </summary>
    public static MessageType ClassifyMessage(string content, string speakerType)
    {
        if (string.IsNullOrWhiteSpace(content))
            return MessageType.SYSTEM;

        var trimmed = content.TrimStart();

        if (string.Equals(speakerType, "SYSTEM", StringComparison.OrdinalIgnoreCase))
            return MessageType.SYSTEM;

        if (trimmed.StartsWith("(") || trimmed.StartsWith("（"))
            return MessageType.OOC;

        if (string.Equals(speakerType, "GM", StringComparison.OrdinalIgnoreCase))
        {
            var metaKeywords2 = new[] { "规则", "判定", "检定", "骰子", "属性", "技能", "HP", "MP", "经验", "等级", "升级" };
            var narrativeCues = new[] { "你", "他", "她", "它", "看见", "听见", "感到", "传来", "出现", "走来", "靠近", "拿到", "获得", "失去", "交给", "递给", "收起", "放进", "装备", "使用", "消耗", "疼痛", "鲜血", "恐惧", "愤怒", "沉默" };
            var hasMeta = metaKeywords2.Any(k => content.Contains(k, StringComparison.OrdinalIgnoreCase));
            var hasNarrative = narrativeCues.Any(k => content.Contains(k, StringComparison.OrdinalIgnoreCase));
            return hasMeta && !hasNarrative ? MessageType.META : MessageType.IC;
        }

        if (trimmed.StartsWith("//") || trimmed.StartsWith("【OOC】", StringComparison.OrdinalIgnoreCase))
            return MessageType.OOC;

        return MessageType.IC;

    }

    public async Task InterceptAndUpdateAsync(TrpgScope scope, string characterId, string speakerType, string incomingText)
    {
        // 防线：只有 GM 输入才能触发世界状态更新
        if (!string.Equals(speakerType, "GM", StringComparison.OrdinalIgnoreCase))
        {
            _context.Log(LogLevel.Debug,
                $"[AIMod:TRPG] StateInterceptorAuthorityGuard | speakerType={speakerType} | skipped=true | reason=OnlyGMCanMutateState");
            return;
        }

        using var llmTurnContext = LlmCallTracker.PushAmbientTurnContext(
            BuildTurnId(scope, incomingText),
            BuildSourceMessageId(scope, incomingText),
            BuildSourceSummary(incomingText));

        var groupId = scope.GroupId;
        // Message Reality Filter：只有 IC 类型允许触发状态更新
        var messageType = ClassifyMessage(incomingText, speakerType);
        if (messageType != MessageType.IC)
        {
            _context.Log(LogLevel.Debug, $"[AIMod:TRPG] 跳过 {messageType} 消息的状态拦截");
            return;
        }

        var state = _stateCache.GetOrCreate(scope, characterId);
        var oldScene = state.CurrentSceneId;
        var oldEntities = string.Join(",", state.PresentEntities);
        state.LatestGmNarrative = incomingText;
        state.LatestSituationSummary = BuildFallbackSituationSummary(incomingText);
        state.LatestFacts = new List<string>();
        state.LatestEvents = new List<string>();
        state.LastExtractionAt = DateTime.UtcNow;

        InfoExtractionResult? extractionResult = null;

        // 使用信息提取模型提取结构化信息
        if (_infoExtractor != null && _mutationPipeline != null)
        {
            try
            {
                extractionResult = await GetSharedInfoExtractionAsync(scope, characterId, incomingText);
                state.LatestSituationSummary = extractionResult.Summaries.LastOrDefault()
                    ?? extractionResult.Events.FirstOrDefault()
                    ?? state.LatestSituationSummary;
                state.LatestFacts = extractionResult.Facts.ToList();
                state.LatestEvents = extractionResult.Events.ToList();
                state.LastExtractionAt = DateTime.UtcNow;

                var removedDuplicateEntityChanges = NormalizeIdentityMergeEntityChanges(extractionResult);
                if (removedDuplicateEntityChanges > 0)
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] identity_merge 已移除重复 entity_change {removedDuplicateEntityChanges} 条");
                var newEntityResolution = await ApplyNewEntityResolutionChecksAsync(scope, extractionResult, incomingText);
                if (newEntityResolution.Removed > 0 || newEntityResolution.Mapped > 0)
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] NewNpcResolutionCheck applied: removed={newEntityResolution.Removed}, mapped={newEntityResolution.Mapped}, create_new={newEntityResolution.CreateNew}");

                long? affectiveSourceEventId = null;

                if (extractionResult.InventoryMutations.Count > 0)
                {
                    var snapshotMutations = extractionResult.InventoryMutations
                        .Where(IsInventorySnapshotMutation)
                        .ToList();
                    var regularMutations = extractionResult.InventoryMutations
                        .Where(m => !IsInventorySnapshotMutation(m))
                        .ToList();

                    if (snapshotMutations.Count > 0)
                    {
                        long? snapshotEventId = null;
                        foreach (var mutation in snapshotMutations)
                            snapshotEventId = await AppendInventoryWorldEventAsync(scope, characterId, mutation) ?? snapshotEventId;

                        var evidence = string.Join("；", snapshotMutations
                            .Select(m => m.Evidence)
                            .Where(e => !string.IsNullOrWhiteSpace(e))
                            .Distinct(StringComparer.OrdinalIgnoreCase));
                        await _db.ApplyInventorySnapshotAsync(scope, characterId, snapshotMutations, snapshotEventId, evidence);
                    }

                    foreach (var mutation in regularMutations)
                    {
                        var eventId = await AppendInventoryWorldEventAsync(scope, characterId, mutation);
                        await _db.ApplyInventoryMutationAsync(scope, characterId, mutation, eventId);
                    }
                }

                if (extractionResult.HasContent)
                {
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] 信息提取结果: 场景={extractionResult.SceneSnapshots.Count}, 实体={extractionResult.EntityChanges.Count}, new_entity_check={extractionResult.NewEntityChecks.Count}, 身份合并={extractionResult.IdentityMerges.Count}, 目标={extractionResult.Objectives.Count}, 完成={extractionResult.CompletedObjectives.Count}, 放弃={extractionResult.AbandonedObjectives.Count}, 事件={extractionResult.Events.Count}, 事实={extractionResult.Facts.Count}, 关系={extractionResult.Relationships.Count}");

                    // 构建状态变更事务（直接构建以保留每类标签的全部条目）
                    var transaction = new StateMutationTransaction { SceneId = state.CurrentSceneId };
                    foreach (var scene in extractionResult.SceneSnapshots)
                        transaction.Mutations.Add(new StateMutation { Type = "scene_snapshot", Content = scene, SceneId = state.CurrentSceneId });
                    foreach (var entity in extractionResult.EntityChanges)
                        transaction.Mutations.Add(new StateMutation { Type = "entity_change", Content = entity, SceneId = state.CurrentSceneId });
                    foreach (var alias in newEntityResolution.AliasMutations)
                        transaction.Mutations.Add(new StateMutation { Type = "entity_alias", Content = alias, SceneId = state.CurrentSceneId });
                    foreach (var identityMerge in extractionResult.IdentityMerges)
                        transaction.Mutations.Add(new StateMutation { Type = "identity_merge", Content = identityMerge, SceneId = state.CurrentSceneId });
                    foreach (var objective in extractionResult.Objectives)
                        transaction.Mutations.Add(new StateMutation { Type = "objective", Content = objective, Priority = QuestPriority.Normal, SceneId = state.CurrentSceneId });
                    foreach (var completed in extractionResult.CompletedObjectives)
                        transaction.Mutations.Add(new StateMutation { Type = "complete", Content = completed, SceneId = state.CurrentSceneId });
                    foreach (var abandoned in extractionResult.AbandonedObjectives)
                        transaction.Mutations.Add(new StateMutation { Type = "abandon", Content = abandoned, SceneId = state.CurrentSceneId });
                    foreach (var evt in extractionResult.Events)
                        transaction.Mutations.Add(new StateMutation { Type = "event", Content = evt, SceneId = state.CurrentSceneId });
                    foreach (var fact in extractionResult.Facts)
                        transaction.Mutations.Add(new StateMutation { Type = "fact", Content = fact, SceneId = state.CurrentSceneId });
                    foreach (var relationship in extractionResult.Relationships)
                        transaction.Mutations.Add(new StateMutation { Type = "relationship", Content = relationship, SceneId = state.CurrentSceneId });
                    foreach (var entityProfile in extractionResult.EntityProfiles)
                        transaction.Mutations.Add(new StateMutation { Type = "entity_profile", Content = entityProfile, SceneId = state.CurrentSceneId });
                    // presence_snapshot 直接由 StateInterceptor 消费，不经过 StateMutationPipeline
                    foreach (var ps in extractionResult.PresenceSnapshots)
                        await ApplyPresenceSnapshotAsync(scope, characterId, state, ps);

                    if (transaction.Mutations.Count > 0)
                    {
                        var result = await _mutationPipeline.ExecuteTransactionAsync(scope, transaction, characterId);

                        if (!result.Success)
                        {
                            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 状态变更事务执行失败: {result.ErrorMessage}");
                        }
                        else
                        {
                            affectiveSourceEventId = result.SourceEventId;

                            // 同步更新state对象
                            // 更新场景ID
                            if (extractionResult.SceneSnapshots.Count > 0)
                            {
                                var sceneParts = extractionResult.SceneSnapshots[0].Split('|', StringSplitOptions.RemoveEmptyEntries);
                                if (sceneParts.Length >= 1)
                                {
                                    state.CurrentSceneId = sceneParts[0].Trim();
                                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] 场景ID已更新: {state.CurrentSceneId}");
                                }
                            }

                            // entity_change 只表示实体创建/别名/简介/身份更新，不代表该实体在场
                            // 在场实体只能来自 presence_snapshot / scene_snapshot 第三段 / GM明确"进入/跟随/在场"
                            foreach (var entityChange in extractionResult.EntityChanges)
                            {
                                var entityParts = entityChange.Split('|', StringSplitOptions.RemoveEmptyEntries);
                                if (entityParts.Length >= 1)
                                {
                                    var displayName = entityParts[0].Trim();
                                    _context.Log(LogLevel.Debug,
                                        $"[AIMod:TRPG] ScenePresenceDiagnostics | source=entity_change | action=ignored_for_presence | entity={displayName}");
                                }
                            }
                        }
                    }
                }

                if (_affectiveTagController != null && extractionResult.AffectiveTagCandidates.Count > 0)
                {
                    affectiveSourceEventId ??= await AppendAffectiveObservationWorldEventAsync(
                        scope,
                        characterId,
                        extractionResult.AffectiveTagCandidates,
                        incomingText);

                    await _affectiveTagController.ProcessCandidatesAsync(
                        scope,
                        characterId,
                        extractionResult.AffectiveTagCandidates,
                        affectiveSourceEventId);
                }
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod:TRPG] 信息提取失败: {ex.Message}");
            }
        }

        // 时间轴维护是独立的后台结构化判断：保留专门 LLM 调用，避免把世界书事实提取误当成 L1/L2/L3 层级。
        if (_infoExtractor != null && _timelineWriter != null)
        {
            try
            {
                var currentScene = state.CurrentSceneId ?? "";
                var l1Nodes = await _db.GetTimelineNodesBySceneAsync(scope, characterId, currentScene, TimelineLayer.L1);
                var l2Nodes = await _db.GetTimelineNodesBySceneAsync(scope, characterId, currentScene, TimelineLayer.L2);
                var timelineEvents = BuildTimelineExtractionsFromInfo(extractionResult, incomingText);
                if (ShouldRunStandaloneTimelineExtraction(extractionResult, timelineEvents, incomingText, currentScene, l2Nodes))
                {
                    var extracted = await GetSharedTimelineExtractionAsync(scope, characterId, currentScene, incomingText, l1Nodes, l2Nodes);
                    timelineEvents = timelineEvents
                        .Concat(extracted)
                        .Where(IsMeaningfulTimelineExtraction)
                        .GroupBy(evt => $"{evt.Layer}:{evt.Content}", StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .Take(6)
                        .ToList();
                }
                if (timelineEvents.Count > 0)
                    await _timelineWriter.WriteAsync(scope, characterId, currentScene, timelineEvents);
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] 时间轴提取失败（已隔离）: {ex.Message}");
            }
        }

        try
        {
            // 场景和人物变更现在由信息提取模型维护
            // 正则回退已禁用

            // 更新场景状态
            await UpdateSceneStateAsync(scope, characterId, state, incomingText);

            _stateCache.Upsert(scope, characterId, state);

            var newEntities = string.Join(",", state.PresentEntities);
            _context.Log(LogLevel.Info,
                $"[AIMod:TRPG] StateInterception updated (Group={groupId}, Char={characterId}) Scene: '{oldScene}' -> '{state.CurrentSceneId}', Entities: '{oldEntities}' -> '{newEntities}'");

            // 场景结束检测：触发记忆归纳 + 保存场景快照
            if (!string.Equals(oldScene, state.CurrentSceneId, StringComparison.OrdinalIgnoreCase))
            {
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 场景切换检测：'{oldScene}' -> '{state.CurrentSceneId}'");

                // 保存场景快照
                var sceneDesc = await _db.GetSceneBaseDescAsync(scope, oldScene);
                var snapshot = new SceneSnapshot
                {
                    GroupId = groupId,
                    CharacterId = characterId,
                    SceneId = oldScene,
                    SceneDescription = sceneDesc ?? "",
                    PresentEntities = oldEntities.Split(',').Where(e => !string.IsNullOrWhiteSpace(e)).ToList(),
                    StateProperties = new Dictionary<string, object>
                    {
                        { "player_status", state.PlayerStatus },
                        { "scene_id", oldScene }
                    },
                    SnapshotReason = "scene_change"
                };
                await _db.InsertSceneSnapshotAsync(scope, snapshot);
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] 场景快照已保存：{oldScene}");

                // 触发记忆归纳
                if (_memoryWatchdog != null)
                    await _memoryWatchdog.CheckAndFoldAsync(scope, characterId);

                // 证据衰减：场景结束时衰减所有行为证据
                await _db.DecayBehaviorEvidenceAsync(scope, characterId, decayFactor: 0.5);

                if (_affectiveTagController != null)
                    await _affectiveTagController.DecayStatesAsync(scope, characterId, sceneChanged: true);

                // 清空注意力缓存
                if (_attentionBuffer != null)
                    _attentionBuffer.Clear(scope, characterId);

                // 分层时间轴场景切换处理
                if (_sceneTransitionHandler != null && !string.IsNullOrWhiteSpace(oldScene))
                {
                    try { await _sceneTransitionHandler.HandleSceneTransitionAsync(scope, characterId, oldScene); }
                    catch (Exception ex) { _context.Log(LogLevel.Warn, $"[AIMod:TRPG] SceneTransitionHandler 失败（已隔离）: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] StateInterceptor 内部异常，已隔离: {ex.Message}");
        }
    }

    private async Task<InfoExtractionResult> GetSharedInfoExtractionAsync(TrpgScope scope, string characterId, string incomingText)
    {
        if (_infoExtractor == null)
            return new InfoExtractionResult();

        CleanupSharedCache(SharedInfoExtractionCache);
        var key = $"info:{scope.WorldId}:{scope.GroupId}:{characterId}:{HashText(incomingText)}";
        var created = false;
        var entry = SharedInfoExtractionCache.GetOrAdd(key, _ =>
        {
            created = true;
            return new CachedTask<InfoExtractionResult>(() =>
            {
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] InfoExtractor shared LLM miss (World={scope.WorldId}, Group={scope.GroupId}, Char={characterId})");
                return _infoExtractor.ExtractAsync(scope, characterId, incomingText);
            });
        });

        try
        {
            var result = await entry.Task.Value;
            if (!created)
                _context.Log(LogLevel.Debug, $"[AIMod:TRPG] InfoExtractor shared cache hit (World={scope.WorldId}, Group={scope.GroupId}, Char={characterId})");
            var clone = CloneInfoExtractionResult(result);
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] InfoExtractor clone: inventory_mutations={clone.InventoryMutations.Count}, affective_tags={clone.AffectiveTagCandidates.Count}");
            return clone;
        }
        catch
        {
            SharedInfoExtractionCache.TryRemove(key, out _);
            throw;
        }
    }

    private async Task<List<TimelineEventExtraction>> GetSharedTimelineExtractionAsync(
        TrpgScope scope,
        string characterId,
        string sceneId,
        string incomingText,
        List<TimelineNode> l1Nodes,
        List<TimelineNode> l2Nodes)
    {
        if (_infoExtractor == null)
            return new List<TimelineEventExtraction>();

        CleanupSharedCache(SharedTimelineExtractionCache);
        var nodeDigest = HashText(string.Join("\n",
            l1Nodes.TakeLast(5).Select(n => $"{n.Id}:{n.Content}")
                .Concat(l2Nodes.TakeLast(8).Select(n => $"{n.Id}:{n.Content}"))));
        var key = $"timeline:{scope.WorldId}:{sceneId}:{HashText(incomingText)}:{nodeDigest}";
        var created = false;
        var entry = SharedTimelineExtractionCache.GetOrAdd(key, _ =>
        {
            created = true;
            return new CachedTask<List<TimelineEventExtraction>>(() =>
            {
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] Timeline extractor shared LLM miss (World={scope.WorldId}, Group={scope.GroupId}, Char={characterId}, Scene={sceneId})");
                return _infoExtractor.ExtractTimelineEventsAsync(scope, characterId, sceneId, incomingText, l1Nodes, l2Nodes);
            });
        });

        try
        {
            var result = await entry.Task.Value;
            if (!created)
                _context.Log(LogLevel.Debug, $"[AIMod:TRPG] Timeline extractor shared cache hit (World={scope.WorldId}, Group={scope.GroupId}, Char={characterId}, Scene={sceneId})");
            return result.Select(CloneTimelineEventExtraction).ToList();
        }
        catch
        {
            SharedTimelineExtractionCache.TryRemove(key, out _);
            throw;
        }
    }

    private static InfoExtractionResult CloneInfoExtractionResult(InfoExtractionResult source)
    {
        return new InfoExtractionResult
        {
            SceneSnapshots = source.SceneSnapshots.ToList(),
            EntityChanges = source.EntityChanges.ToList(),
            IdentityMerges = source.IdentityMerges.ToList(),
            Objectives = source.Objectives.ToList(),
            CompletedObjectives = source.CompletedObjectives.ToList(),
            AbandonedObjectives = source.AbandonedObjectives.ToList(),
            Events = source.Events.ToList(),
            Facts = source.Facts.ToList(),
            Relationships = source.Relationships.ToList(),
            Summaries = source.Summaries.ToList(),
            AffectiveTagCandidates = source.AffectiveTagCandidates.Select(CloneAffectiveTagCandidate).ToList(),
            InventoryMutations = source.InventoryMutations.Select(CloneInventoryMutation).ToList(),
            NewEntityChecks = source.NewEntityChecks.Select(CloneNewEntityCheck).ToList(),
            PresenceSnapshots = source.PresenceSnapshots.Select(ClonePresenceSnapshot).ToList(),
            EntityProfiles = source.EntityProfiles.ToList()
        };
    }

    private static NewEntityCheck CloneNewEntityCheck(NewEntityCheck source)
    {
        return new NewEntityCheck
        {
            CandidateName = source.CandidateName,
            PossibleExistingEntityIdOrName = source.PossibleExistingEntityIdOrName,
            Decision = source.Decision,
            Reason = source.Reason
        };
    }

    private static PresenceSnapshot ClonePresenceSnapshot(PresenceSnapshot source)
    {
        return new PresenceSnapshot
        {
            SceneId = source.SceneId,
            PresentEntities = source.PresentEntities.ToList(),
            AbsentEntities = source.AbsentEntities.ToList(),
            IsFullSnapshot = source.IsFullSnapshot,
            Authority = source.Authority,
            Evidence = source.Evidence
        };
    }

    private static AffectiveTagCandidate CloneAffectiveTagCandidate(AffectiveTagCandidate source)
    {
        return new AffectiveTagCandidate
        {
            TagType = source.TagType,
            DisplayName = source.DisplayName,
            SourceKey = source.SourceKey,
            TargetEntityId = source.TargetEntityId,
            IntensityTier = source.IntensityTier,
            EffectKind = source.EffectKind,
            StackPolicyHint = source.StackPolicyHint,
            Novelty = source.Novelty,
            Evidence = source.Evidence,
            Reason = source.Reason
        };
    }

    private static InventoryMutation CloneInventoryMutation(InventoryMutation source)
    {
        return new InventoryMutation
        {
            Operation = source.Operation,
            ItemKey = source.ItemKey,
            DisplayName = source.DisplayName,
            QuantityDelta = source.QuantityDelta,
            QuantitySet = source.QuantitySet,
            Unit = source.Unit,
            NewState = source.NewState,
            TargetEntityId = source.TargetEntityId,
            SourceKind = source.SourceKind,
            AuthorityRank = source.AuthorityRank,
            Confidence = source.Confidence,
            Evidence = source.Evidence,
            IsFullSnapshot = source.IsFullSnapshot
        };
    }

    private static TimelineEventExtraction CloneTimelineEventExtraction(TimelineEventExtraction source)
    {
        return new TimelineEventExtraction
        {
            Layer = source.Layer,
            Content = source.Content,
            ParentKeywords = source.ParentKeywords,
            Importance = source.Importance,
            Foreshadowing = source.Foreshadowing
        };
    }

    private static void CleanupSharedCache<T>(ConcurrentDictionary<string, CachedTask<T>> cache)
    {
        var cutoff = DateTime.UtcNow - SharedExtractionCacheTtl;
        foreach (var kvp in cache)
        {
            if (kvp.Value.CreatedAtUtc < cutoff)
                cache.TryRemove(kvp.Key, out _);
        }
    }

    private static string HashText(string? text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private async Task<long?> AppendInventoryWorldEventAsync(TrpgScope scope, string characterId, InventoryMutation mutation)
    {
        try
        {
            var eventType = (mutation.Operation ?? "").Trim().ToLowerInvariant() switch
            {
                "gain" or "use" or "equip" or "drop" or "transfer" => "inventory_claim_change",
                "consume" => "inventory_quantity_change",
                "correction" => "inventory_gm_correction",
                "snapshot" => "inventory_snapshot_reset",
                _ when mutation.IsFullSnapshot => "inventory_snapshot_reset",
                _ => "inventory_state_change"
            };

            var payloadJson = JsonSerializer.Serialize(mutation);
            var evt = new WorldEvent
            {
                EventType = eventType,
                Actors = new List<string> { characterId },
                Result = $"{mutation.Operation}: {mutation.DisplayName}",
                SourceEntityId = characterId,
                TargetEntityId = string.IsNullOrWhiteSpace(mutation.TargetEntityId) ? null : mutation.TargetEntityId,
                Payload = new Dictionary<string, object>
                {
                    ["group_id"] = scope.GroupId,
                    ["character_id"] = characterId,
                    ["inventory_mutation"] = payloadJson
                },
                Timestamp = DateTime.UtcNow
            };

            return await _db.InsertEventLogAsync(scope, evt);
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] inventory event append skipped: {ex.Message}");
            return null;
        }
    }

    private async Task<long?> AppendAffectiveObservationWorldEventAsync(
        TrpgScope scope,
        string characterId,
        IReadOnlyList<AffectiveTagCandidate> candidates,
        string incomingText)
    {
        try
        {
            var evt = new WorldEvent
            {
                EventType = "affective_observation",
                Actors = new List<string> { characterId },
                Result = $"affective tags: {candidates.Count}",
                SourceEntityId = characterId,
                Payload = new Dictionary<string, object>
                {
                    ["group_id"] = scope.GroupId,
                    ["character_id"] = characterId,
                    ["affective_tags"] = JsonSerializer.Serialize(candidates),
                    ["evidence"] = incomingText ?? ""
                },
                Timestamp = DateTime.UtcNow
            };

            return await _db.InsertEventLogAsync(scope, evt);
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Warn, $"[AIMod:TRPG] affective observation event append skipped: {ex.Message}");
            return null;
        }
    }

    private static bool IsInventorySnapshotMutation(InventoryMutation mutation)
        => mutation.IsFullSnapshot
           || string.Equals(mutation.Operation, "snapshot", StringComparison.OrdinalIgnoreCase);

    private sealed class CachedTask<T>
    {
        public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
        public Lazy<Task<T>> Task { get; }

        public CachedTask(Func<Task<T>> factory)
        {
            Task = new Lazy<Task<T>>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    private static string BuildFallbackSituationSummary(string incomingText)
    {
        if (string.IsNullOrWhiteSpace(incomingText))
            return "";

        var normalized = Regex.Replace(incomingText.Trim(), @"\s+", " ");
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }

    private static List<TimelineEventExtraction> BuildTimelineExtractionsFromInfo(InfoExtractionResult? extractionResult, string incomingText)
    {
        var sourceEvents = extractionResult?.Events
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(TimelineWriter.LooksLikeConcreteNarrativeContent)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList() ?? new List<string>();

        return sourceEvents.Select(evt => new TimelineEventExtraction
        {
            Layer = TimelineLayer.L2,
            Content = evt,
            ParentKeywords = string.Join(" ", evt.Split(new[] { ' ', '，', ',', '。', '、', '：', ':' }, StringSplitOptions.RemoveEmptyEntries).Take(4)),
            Importance = EstimateTimelineImportance(evt),
            Foreshadowing = ContainsForeshadowingCue(evt)
        }).ToList();
    }

    private static bool ShouldRunStandaloneTimelineExtraction(
        InfoExtractionResult? extractionResult,
        List<TimelineEventExtraction> infoEvents,
        string incomingText,
        string currentSceneId,
        List<TimelineNode> existingL2Nodes)
    {
        if (extractionResult?.SceneSnapshots.Count > 0 || extractionResult?.PresenceSnapshots.Count > 0)
            return true;

        if (infoEvents.Count == 0)
            return LooksLikeNarrativeShift(incomingText);

        if (existingL2Nodes.Count == 0)
            return true;

        return false;
    }

    private static bool IsMeaningfulTimelineExtraction(TimelineEventExtraction extraction)
        => extraction != null
           && TimelineWriter.LooksLikeConcreteNarrativeContent(extraction.Content)
           && extraction.Importance >= 4;

    private static bool LooksLikeNarrativeShift(string text)
    {
        var cues = new[] { "发现", "进入", "离开", "倒下", "恢复", "暴露", "揭示", "获得", "失去", "突然", "终于" };
        return cues.Any(cue => text.Contains(cue, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildTurnId(TrpgScope scope, string incomingText)
        => $"turn:{scope.WorldId}:{scope.GroupId}:{HashText(incomingText)}";

    private static string BuildSourceMessageId(TrpgScope scope, string incomingText)
        => $"src:{scope.WorldId}:{scope.GroupId}:{HashText(incomingText)}";

    private static string BuildSourceSummary(string incomingText)
    {
        var normalized = Regex.Replace(incomingText ?? "", @"\s+", " ").Trim();
        return normalized.Length <= 80 ? normalized : normalized[..80];
    }

    private static int EstimateTimelineImportance(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 4;

        var highCues = new[] { "死亡", "核心", "真相", "身份", "背叛", "失踪", "爆炸", "袭击", "揭示" };
        if (highCues.Any(text.Contains))
            return 8;

        var midCues = new[] { "发现", "听到", "看到", "获得", "打开", "进入", "改变", "回应", "低语", "数字" };
        if (midCues.Any(text.Contains))
            return 6;

        return 4;
    }

    private static bool ContainsForeshadowingCue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var cues = new[] { "似乎", "仿佛", "隐约", "不明", "未知", "重复", "低语", "数字", "异常", "模糊" };
        return cues.Any(text.Contains);
    }

    private static int NormalizeIdentityMergeEntityChanges(InfoExtractionResult result)
    {
        if (result == null || result.IdentityMerges.Count == 0 || result.EntityChanges.Count == 0)
            return 0;

        var mergeTargets = result.IdentityMerges
            .Select(GetIdentityMergeTargetName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mergeTargets.Count == 0)
            return 0;

        var before = result.EntityChanges.Count;
        result.EntityChanges.RemoveAll(change =>
        {
            var displayName = GetEntityChangeDisplayName(change);
            return mergeTargets.Any(target => EntityMatchesName(displayName, target));
        });

        return before - result.EntityChanges.Count;
    }

    private async Task<(int Removed, int Mapped, int CreateNew, List<string> AliasMutations)> ApplyNewEntityResolutionChecksAsync(
        TrpgScope scope,
        InfoExtractionResult result,
        string incomingText)
    {
        var aliasMutations = new List<string>();
        if (result.EntityChanges.Count == 0)
            return (0, 0, 0, aliasMutations);

        var checksByName = result.NewEntityChecks
            .Where(c => !string.IsNullOrWhiteSpace(c.CandidateName))
            .GroupBy(c => c.CandidateName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        var mapped = 0;
        var createNew = 0;
        var explicitNewCue = HasExplicitNewEntityCue(incomingText);

        var kept = new List<string>();
        foreach (var change in result.EntityChanges)
        {
            var displayName = GetEntityChangeDisplayName(change);
            checksByName.TryGetValue(displayName, out var check);
            check ??= new NewEntityCheck
            {
                CandidateName = displayName,
                Decision = explicitNewCue ? NewEntityCheckDecision.CreateNew : NewEntityCheckDecision.HoldCandidate,
                Reason = explicitNewCue ? "文本明确提示新人物出现" : "缺少 new_entity_check，保守暂缓"
            };

            switch (check.Decision)
            {
                case NewEntityCheckDecision.CreateNew:
                    kept.Add(change);
                    createNew++;
                    break;
                case NewEntityCheckDecision.MapToExisting:
                    var target = check.PossibleExistingEntityIdOrName;
                    if (!string.IsNullOrWhiteSpace(target) && _entityCanonicalizer != null)
                    {
                        var resolved = await _entityCanonicalizer.ResolveEntityIdAsync(scope, target) ?? target;
                        aliasMutations.Add($"{resolved}|{displayName}|{check.Reason}");
                        mapped++;
                    }
                    removed++;
                    break;
                default:
                    removed++;
                    break;
            }
        }

        result.EntityChanges.Clear();
        result.EntityChanges.AddRange(kept);
        return (removed, mapped, createNew, aliasMutations);
    }

    private static bool HasExplicitNewEntityCue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var cues = new[] { "新人物", "新的角色", "首次登场", "第一次出现", "一个陌生", "一个新", "走进来一个", "出现一个", "进入了一个" };
        return cues.Any(cue => text.Contains(cue, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetIdentityMergeTargetName(string merge)
    {
        if (string.IsNullOrWhiteSpace(merge))
            return "";

        var arrow = merge.IndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0)
            return merge[(arrow + 2)..].Trim();

        arrow = merge.IndexOf("=>", StringComparison.Ordinal);
        if (arrow >= 0)
            return merge[(arrow + 2)..].Trim();

        arrow = merge.IndexOf('→');
        return arrow >= 0 ? merge[(arrow + 1)..].Trim() : "";
    }

    private static string GetEntityChangeDisplayName(string entityChange)
    {
        if (string.IsNullOrWhiteSpace(entityChange))
            return "";

        var separator = entityChange.IndexOf('|');
        return separator >= 0 ? entityChange[..separator].Trim() : entityChange.Trim();
    }

    private static bool EntityMatchesName(string entityIdOrName, string name)
    {
        if (string.IsNullOrWhiteSpace(entityIdOrName) || string.IsNullOrWhiteSpace(name))
            return false;

        return entityIdOrName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || StripNpcPrefix(entityIdOrName).Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripNpcPrefix(string value)
    {
        return value.StartsWith("npc_", StringComparison.OrdinalIgnoreCase) ? value[4..] : value;
    }

    private async Task UpdateSceneStateAsync(TrpgScope scope, string characterId, TrpgRuntimeState state, string incomingText)
    {
        var sceneDesc = await _db.GetSceneBaseDescAsync(scope, state.CurrentSceneId);
        if (string.IsNullOrWhiteSpace(sceneDesc))
            sceneDesc = BuildSceneBaseDesc(incomingText);

        // 初始化场景状态
        if (state.SceneState == null)
        {
            state.SceneState = new SceneState
            {
                SceneId = state.CurrentSceneId,
                Description = sceneDesc ?? "",
                Properties = new Dictionary<string, object>
                {
                    { "entities", state.PresentEntities },
                    { "player_status", state.PlayerStatus }
                }
            };
        }
        else
        {
            // 判断是否需要更新场景状态
            if (state.SceneState.ShouldUpdate(sceneDesc ?? "", state.PresentEntities))
            {
                state.SceneState.Description = sceneDesc ?? "";
                state.SceneState.Properties["entities"] = state.PresentEntities;
                state.SceneState.Properties["player_status"] = state.PlayerStatus;
                state.SceneState.UpdatedAt = DateTime.UtcNow;
                _context.Log(LogLevel.Debug, $"[AIMod:TRPG] 场景状态已更新：{state.CurrentSceneId}");
            }
        }
    }
/*
    private async Task ApplyRegexFallbackAsync(long groupId, string characterId, TrpgRuntimeState state, string incomingText)
    {
        var sceneMatch = SceneRegex.Match(incomingText);
        if (sceneMatch.Success)
        {
            var sceneName = sceneMatch.Groups["scene"].Value.Trim();
            var newSceneId = NormalizeSceneId(sceneName);
            
            // 只在场景ID实际变化时更新PreviousSceneId
            if (!string.Equals(state.CurrentSceneId, newSceneId, StringComparison.OrdinalIgnoreCase))
            {
                state.PreviousSceneId = state.CurrentSceneId;
                state.CurrentSceneId = newSceneId;
            }
            
            await _db.UpsertSceneDictionaryAsync(state.CurrentSceneId, sceneName.Length > 50 ? sceneName.Substring(0, 50) : sceneName);
        }
        else
        {
            var inferredScene = BuildSceneBaseDesc(incomingText);
            if (!string.IsNullOrWhiteSpace(inferredScene))
                await _db.UpsertSceneDictionaryAsync(state.CurrentSceneId, inferredScene);
        }

        var mentionedNames = NameRegex.Matches(incomingText)
            .Cast<Match>()
            .Concat(SelfIntroRegex.Matches(incomingText).Cast<Match>())
            .Select(m => m.Groups["name"].Value.Trim())
            .Where(IsValidEntityName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mentionedNames.Count > 0)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { characterId };
            foreach (var name in mentionedNames)
            {
                var charId = await _db.ResolveCharacterIdByAliasAsync(groupId, name);
                if (string.IsNullOrWhiteSpace(charId))
                {
                    charId = $"npc_{name}";
                    await _db.UpsertCharacterHotMetaAsync(charId, "未标注", name);
                }
                present.Add(charId);
            }
            state.PresentEntities = present.ToList();
        }

        // 已废弃：旧系统的 NPC 锚点更新
        // 新系统通过四层架构标签驱动，由 StateMutationPipeline 处理
        // await UpsertNpcCanonicalAnchorsAsync(groupId, state.PresentEntities, incomingText);
    }
*/
    /// <summary>
    /// 已废弃：旧系统的 NPC 锚点更新
    /// 新系统通过四层架构标签驱动，由 StateMutationPipeline 处理
    /// </summary>
    [Obsolete("请使用四层架构标签驱动，由 StateMutationPipeline 处理")]
    private async Task UpsertNpcCanonicalAnchorsAsync(TrpgScope scope, List<string> presentEntities, string incomingText)
    {
        var groupId = scope.GroupId;
        var gmSnippet = BuildSceneBaseDesc(incomingText);
        foreach (var entityId in presentEntities)
        {
            if (!entityId.StartsWith("npc_", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var displayName = entityId.StartsWith("npc_", StringComparison.OrdinalIgnoreCase)
                    ? entityId.Substring(4)
                    : entityId;

                var canonical = await _db.GetNpcCanonicalStateAsync(scope, entityId) ?? new NpcCanonicalState
                {
                    GroupId = groupId,
                    NpcId = entityId,
                    DisplayName = displayName
                };

                if (string.IsNullOrWhiteSpace(canonical.CoreSummary))
                    canonical.CoreSummary = $"{displayName} 是当前剧情中的在场人物，基础设定待补完。";
                if (string.IsNullOrWhiteSpace(canonical.IdentityState))
                    canonical.IdentityState = "status=alive; phase=unknown";
                if (string.IsNullOrWhiteSpace(canonical.RelationshipState))
                    canonical.RelationshipState = "与玩家关系：未定。";

                if (!string.IsNullOrWhiteSpace(gmSnippet))
                {
                    if (string.IsNullOrWhiteSpace(canonical.KeyEventsDigest))
                        canonical.KeyEventsDigest = gmSnippet;
                    else if (!canonical.KeyEventsDigest.Contains(gmSnippet, StringComparison.OrdinalIgnoreCase))
                        canonical.KeyEventsDigest = (canonical.KeyEventsDigest + " | " + gmSnippet).Trim();

                    if (canonical.KeyEventsDigest.Length > 280)
                        canonical.KeyEventsDigest = canonical.KeyEventsDigest.Substring(canonical.KeyEventsDigest.Length - 280);
                }

                await _db.UpsertNpcCanonicalStateAsync(scope, canonical);

                var relationshipDeltas = InferRelationshipDeltas(incomingText);
                foreach (var delta in relationshipDeltas)
                {
                    var threshold = delta.Key switch
                    {
                        "trust" => 12,
                        "affection" => 18,
                        "respect" => 15,
                        "fear" => 10,
                        _ => 12
                    };

                    var shouldRegenerate = await _db.AccumulateNpcRelationshipDeltaAsync(
                        scope,
                        entityId,
                        delta.Key,
                        delta.Value,
                        threshold,
                        TimeSpan.FromMinutes(10));

                    if (shouldRegenerate)
                    {
                        canonical = await _db.GetNpcCanonicalStateAsync(scope, entityId) ?? canonical;
                        var pending = DeserializePendingDeltas(canonical.PendingRelationshipDeltaJson);
                        if (pending.TryGetValue(delta.Key, out var accumulated))
                        {
                            var direction = accumulated >= 0 ? "↑" : "↓";
                            var snippet = $"{delta.Key}{direction}{Math.Abs(accumulated)}";
                            if (!canonical.RelationshipState.Contains(snippet, StringComparison.OrdinalIgnoreCase))
                            {
                                canonical.RelationshipState = (canonical.RelationshipState + $" [{snippet}]").Trim();
                                if (canonical.RelationshipState.Length > 240)
                                    canonical.RelationshipState = canonical.RelationshipState.Substring(canonical.RelationshipState.Length - 240);
                                canonical.LastSummaryUpdatedAt = DateTime.UtcNow;
                                pending.Remove(delta.Key);
                                canonical.PendingRelationshipDeltaJson = JsonSerializer.Serialize(pending);
                                await _db.UpsertNpcCanonicalStateAsync(scope, canonical);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod:TRPG] NPC canonical 锚点更新失败 ({entityId}): {ex.Message}");
            }
        }
    }

    private static Dictionary<string, int> DeserializePendingDeltas(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); }
        catch { return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// 已废弃：关系推断应通过四层架构标签驱动，由 StateMutationPipeline 处理
    /// </summary>
    [Obsolete("关系推断应通过四层架构标签驱动，由 StateMutationPipeline 处理")]
    private static Dictionary<string, int> InferRelationshipDeltas(string incomingText)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(incomingText)) return result;

        if (incomingText.Contains("救", StringComparison.OrdinalIgnoreCase)
            || incomingText.Contains("保护", StringComparison.OrdinalIgnoreCase))
        {
            result["trust"] = 6;
            result["respect"] = 4;
        }

        if (incomingText.Contains("帮助", StringComparison.OrdinalIgnoreCase)
            || incomingText.Contains("协助", StringComparison.OrdinalIgnoreCase))
        {
            result["trust"] = result.TryGetValue("trust", out var t) ? t + 3 : 3;
        }

        if (incomingText.Contains("背叛", StringComparison.OrdinalIgnoreCase)
            || incomingText.Contains("欺骗", StringComparison.OrdinalIgnoreCase))
        {
            result["trust"] = result.TryGetValue("trust", out var t) ? t - 12 : -12;
            result["fear"] = result.TryGetValue("fear", out var f) ? f + 6 : 6;
        }

        if (incomingText.Contains("攻击", StringComparison.OrdinalIgnoreCase)
            || incomingText.Contains("威胁", StringComparison.OrdinalIgnoreCase))
        {
            result["fear"] = result.TryGetValue("fear", out var f) ? f + 4 : 4;
            result["trust"] = result.TryGetValue("trust", out var t) ? t - 4 : -4;
        }

        return result;
    }

    private static string NormalizeSceneId(string sceneName)
    {
        var normalized = sceneName.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, "[^a-z0-9\\u4e00-\\u9fa5_\\-]", "_");
        normalized = Regex.Replace(normalized, "_+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "scene_default";
        return $"scene_{normalized}";
    }

    private static bool IsValidEntityName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var cleaned = name.Trim();
        if (cleaned.Length < 2 || cleaned.Length > 10) return false;
        if (InvalidEntityTokens.Contains(cleaned)) return false;

        if (cleaned.All(char.IsDigit)) return false;

        var stopChars = new[] { '的', '了', '在', '是', '有', '和', '与', '及', '并' };
        var stopCount = cleaned.Count(ch => stopChars.Contains(ch));
        if (stopCount >= 2) return false;

        return true;
    }

    private static string BuildSceneBaseDesc(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var firstLine = text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.Length > 0 && !x.StartsWith("(") && !x.StartsWith("（")) ?? "";

        if (string.IsNullOrWhiteSpace(firstLine)) return "";

        var sentence = Regex.Split(firstLine, "[。！？!?]")
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.Length > 0) ?? firstLine;

        return sentence.Length <= 80 ? sentence : sentence.Substring(0, 80);
    }

    /// <summary>
    /// 应用 presence_snapshot 更新运行态 PresentEntities
    /// </summary>
    private async Task ApplyPresenceSnapshotAsync(TrpgScope scope, string characterId, TrpgRuntimeState state, PresenceSnapshot ps)
    {
        var presentBefore = string.Join(",", state.PresentEntities);
        var added = new List<string>();
        var removed = new List<string>();

        var presentResolved = new List<string>();
        foreach (var name in ps.PresentEntities)
        {
            var resolved = await (_entityCanonicalizer?.ResolveEntityIdAsync(scope, name) ?? Task.FromResult<string?>(name));
            if (resolved != null) presentResolved.Add(resolved);
        }
        var absentResolved = new List<string>();
        foreach (var name in ps.AbsentEntities)
        {
            var resolved = await (_entityCanonicalizer?.ResolveEntityIdAsync(scope, name) ?? Task.FromResult<string?>(name));
            if (resolved != null) absentResolved.Add(resolved);
        }

        if (ps.IsFullSnapshot && ps.Authority.Equals("GMCorrection", StringComparison.OrdinalIgnoreCase))
        {
            // full snapshot + GMCorrection: 完全替换
            var keptCharacter = presentResolved.Contains(characterId, StringComparer.OrdinalIgnoreCase)
                ? characterId : null;
            added = presentResolved.Where(e => !state.PresentEntities.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
            removed = state.PresentEntities.Where(e => !presentResolved.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
            state.PresentEntities = presentResolved.ToList();
            // 确保当前角色在场（除非 GM 明确其不在）
            if (keptCharacter == null && !absentResolved.Contains(characterId, StringComparer.OrdinalIgnoreCase))
            {
                if (!state.PresentEntities.Contains(characterId, StringComparer.OrdinalIgnoreCase))
                {
                    state.PresentEntities.Add(characterId);
                    added.Add(characterId);
                }
            }
        }
        else if (ps.IsFullSnapshot)
        {
            // full snapshot 非 GMCorrection
            state.PresentEntities = presentResolved.ToList();
            if (!state.PresentEntities.Contains(characterId, StringComparer.OrdinalIgnoreCase)
                && !absentResolved.Contains(characterId, StringComparer.OrdinalIgnoreCase))
            {
                state.PresentEntities.Add(characterId);
            }
            added = presentResolved;
            removed = new List<string>();
        }
        else
        {
            // 增量更新
            foreach (var e in absentResolved)
            {
                if (state.PresentEntities.RemoveAll(p => string.Equals(p, e, StringComparison.OrdinalIgnoreCase)) > 0)
                    removed.Add(e);
            }
            foreach (var e in presentResolved)
            {
                if (!state.PresentEntities.Contains(e, StringComparer.OrdinalIgnoreCase))
                {
                    state.PresentEntities.Add(e);
                    added.Add(e);
                }
            }
        }

        _context.Log(LogLevel.Info,
            $"[AIMod:TRPG] ScenePresenceDiagnostics | source=presence_snapshot | " +
            $"scene_id={ps.SceneId} | is_full_snapshot={ps.IsFullSnapshot} | " +
            $"present_before={presentBefore} | present_after={string.Join(",", state.PresentEntities)} | " +
            $"added={string.Join(",", added)} | removed={string.Join(",", removed)} | " +
            $"authority={ps.Authority} | evidence={ps.Evidence}");
    }
}
