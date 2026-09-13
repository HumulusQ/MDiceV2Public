using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// State Mutation Pipeline - 状态变更管线
/// 
/// 职责：以事务方式处理桌面事件、角色事实性认知与场景认知缓存变更
/// 
/// 这是防止 AI 把桌面线索误写成幕后真相的关键防线：
/// 1. 接收 AI 输出的标签（建议）
/// 2. 通过 Runtime Validator 验证
/// 3. 构建状态变更事务
/// 4. 执行事务（原子性）
/// 5. 追加事件到 EventLog
/// 6. 触发认知投影更新
/// 
/// 任何一步失败都会回滚，保证角色认知缓存一致性
/// </summary>
public class StateMutationPipeline
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly RuntimeValidator _validator;
    private readonly EventLog _eventLog;
    private readonly EntityCanonicalizer _entityCanonicalizer;
    private readonly ObjectiveLayer _objectiveLayer;
    private readonly WorldStateProjection _projection;

    // 优化的JSON序列化配置：不转义非ASCII字符，减少token消耗
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public StateMutationPipeline(
        IModContext context,
        ChatDatabase db,
        RuntimeValidator validator,
        EventLog eventLog,
        EntityCanonicalizer entityCanonicalizer,
        ObjectiveLayer objectiveLayer,
        WorldStateProjection projection)
    {
        _context = context;
        _db = db;
        _validator = validator;
        _eventLog = eventLog;
        _entityCanonicalizer = entityCanonicalizer;
        _objectiveLayer = objectiveLayer;
        _projection = projection;
    }

    /// <summary>
    /// 执行状态变更事务
    /// </summary>
    public async Task<StateMutationResult> ExecuteTransactionAsync(
        TrpgScope scope,
        StateMutationTransaction transaction,
        string characterId)
    {
        var groupId = scope.GroupId;
        var result = new StateMutationResult
        {
            TransactionId = Guid.NewGuid().ToString(),
            StartedAt = DateTime.UtcNow
        };

        try
        {
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 开始执行状态变更事务: {result.TransactionId}");

            // 1. 验证事务
            var validationResult = await ValidateTransactionAsync(scope, transaction, characterId);
            if (!validationResult.IsValid)
            {
                result.Success = false;
                result.ErrorMessage = validationResult.ErrorMessage;
                result.CompletedAt = DateTime.UtcNow;
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] 事务验证失败: {validationResult.ErrorMessage}");
                return result;
            }

            // 2. 追加事务事件到 EventLog（先创建事件以获取EventId）
            var transactionEvent = new WorldEvent
            {
                EventType = "state_transaction",
                Actors = new List<string> { characterId },
                Location = transaction.SceneId ?? "unknown",
                Result = $"事务 {result.TransactionId} 执行成功",
                WorldChanges = new List<string>(),
                Timestamp = DateTime.UtcNow,
                Payload = new Dictionary<string, object>
                {
                    { "transaction_id", result.TransactionId },
                    { "mutation_count", transaction.Mutations.Count },
                    { "mutations", JsonSerializer.Serialize(transaction.Mutations, JsonOptions) }
                }
            };
            result.SourceEventId = await _eventLog.AppendEventAsync(scope, transactionEvent);

            // 3. 执行事务（按顺序，传递EventId）
            foreach (var mutation in transaction.Mutations)
            {
                var mutationResult = await ExecuteMutationAsync(scope, mutation, characterId, transactionEvent.EventId);
                if (!mutationResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = $"变更失败: {mutation.Type} - {mutationResult.ErrorMessage}";
                    result.CompletedAt = DateTime.UtcNow;
                    _context.Log(LogLevel.Error, $"[AIMod:TRPG] 事务执行失败: {result.ErrorMessage}");
                    return result;
                }
                result.ExecutedMutations.Add(mutation);
            }

            result.Success = true;
            result.CompletedAt = DateTime.UtcNow;
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 事务执行成功: {result.TransactionId}");
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.CompletedAt = DateTime.UtcNow;
            _context.Log(LogLevel.Error, $"[AIMod:TRPG] 事务执行异常: {ex.Message}");
            return result;
        }
    }

    /// <summary>
    /// 验证事务
    /// </summary>
    private async Task<(bool IsValid, string? ErrorMessage)> ValidateTransactionAsync(
        TrpgScope scope,
        StateMutationTransaction transaction,
        string characterId)
    {
        if (transaction.Mutations.Count == 0)
            return (false, "事务不包含任何变更");

        // 验证每个变更
        foreach (var mutation in transaction.Mutations)
        {
            var (isValid, errorMessage) = _validator.ValidateTag(
                scope,
                mutation.Type,
                mutation.Content,
                characterId);

            if (!isValid)
                return (false, $"变更 {mutation.Type} 验证失败: {errorMessage}");
        }

        // 检查变更之间的冲突
        var conflictCheck = CheckMutationConflicts(transaction.Mutations);
        if (!conflictCheck.IsValid)
            return (false, conflictCheck.ErrorMessage);

        return (true, null);
    }

    /// <summary>
    /// 检查变更之间的冲突
    /// </summary>
    private (bool IsValid, string? ErrorMessage) CheckMutationConflicts(List<StateMutation> mutations)
    {
        // 检查是否有冲突的目标操作
        var objectiveMutations = mutations.Where(m => m.Type == "objective" || m.Type == "complete" || m.Type == "abandon").ToList();
        var objectiveContents = objectiveMutations.Select(m => m.Content).ToList();
        
        if (objectiveContents.Distinct(StringComparer.OrdinalIgnoreCase).Count() != objectiveContents.Count)
            return (false, "存在重复的目标操作");

        // 检查是否有冲突的实体合并
        var mergeMutations = mutations.Where(m => m.Type == "identity_merge").ToList();
        if (mergeMutations.Count > 1)
            return (false, "单次事务不能包含多个实体合并");

        return (true, null);
    }

    /// <summary>
    /// 执行单个变更
    /// </summary>
    private async Task<(bool Success, string? ErrorMessage)> ExecuteMutationAsync(
        TrpgScope scope,
        StateMutation mutation,
        string characterId,
        long eventId)
    {
        var groupId = scope.GroupId;
        try
        {
            switch (mutation.Type.ToLower())
            {
                case "objective":
                    await _objectiveLayer.AddObjectiveAsync(scope, characterId, mutation.Content, mutation.Priority ?? QuestPriority.Normal, mutation.SceneId);
                    return (true, null);

                case "complete":
                    await _objectiveLayer.CompleteObjectiveAsync(scope, characterId, mutation.Content, mutation.SceneId);
                    return (true, null);

                case "abandon":
                    await _objectiveLayer.AbandonObjectiveAsync(scope, characterId, mutation.Content, mutation.SceneId);
                    return (true, null);

                case "fact":
                    var factParts = mutation.Content.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    if (factParts.Length >= 2)
                    {
                        var entityName = factParts[0].Trim();
                        var factDescription = factParts[1].Trim();
                        var category = factParts.Length >= 3 ? factParts[2].Trim() : "general";

                        var entityId = await _entityCanonicalizer.ResolveEntityIdAsync(scope, entityName);
                        if (entityId != null)
                        {
                            var entity = await _db.GetEntityCanonicalAsync(scope, entityId);
                            if (entity != null)
                            {
                                // 获取当前FoldCount
                                var memories = await _db.GetAllMemoryNodesAsync(scope, characterId, limit: 1);
                                var currentFoldCount = memories.Count > 0 ? memories[0].FoldCount : 0;

                                // 检查是否已存在相同事实
                                var existingFact = entity.PersistentFacts.FirstOrDefault(f =>
                                    string.Equals(f.Fact, factDescription, StringComparison.OrdinalIgnoreCase));
                                if (existingFact == null)
                                {
                                    entity.PersistentFacts.Add(new PersistentFact
                                    {
                                        Fact = factDescription,
                                        Category = category,
                                        EstablishedAt = DateTime.UtcNow,
                                        RelatedEventId = eventId,
                                        IsActive = true,
                                        EstablishedFoldCount = currentFoldCount,
                                        Salience = 1.0
                                    });
                                    entity.LastUpdated = DateTime.UtcNow;
                                    await _db.UpsertEntityCanonicalAsync(scope, entity);
                                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] 添加永久事实: {entityName} - {factDescription} (FoldCount={currentFoldCount})");
                                }
                            }
                        }
                    }
                    return (true, null);

                case "relationship":
                    var relParts = mutation.Content.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    if (relParts.Length >= 4)
                    {
                        var entityA = relParts[0].Trim();
                        var entityB = relParts[1].Trim();
                        var relType = relParts[2].Trim();
                        var deltaStr = relParts[3].Trim();
                        var isTrauma = relParts.Length >= 5 && relParts[4].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                        var reason = relParts.Length >= 6 ? relParts[5].Trim() : "";

                        if (double.TryParse(deltaStr, out var delta))
                        {
                            var entityIdA = await _entityCanonicalizer.ResolveEntityIdAsync(scope, entityA);
                            if (entityIdA != null)
                            {
                                var entity = await _db.GetEntityCanonicalAsync(scope, entityIdA);
                                if (entity != null)
                                {
                                    var relKey = $"{entityB}_{relType}";
                                    if (!entity.Relationships.ContainsKey(relKey))
                                    {
                                        entity.Relationships[relKey] = new DynamicRelationship();
                                    }
                                    entity.Relationships[relKey].ApplyChange(delta, isTrauma, reason, eventId);
                                    entity.LastUpdated = DateTime.UtcNow;
                                    await _db.UpsertEntityCanonicalAsync(scope, entity);
                                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] 更新关系: {entityA} -> {entityB} ({relType}) {delta:+0;-0} (创伤: {isTrauma})");
                                }
                            }
                        }
                    }
                    return (true, null);

                case "identity_merge":
                    var parts = mutation.Content.Split("->", StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        var fromName = parts[0].Trim();
                        var toName = parts[1].Trim();
                        var toEntity = await _entityCanonicalizer.ResolveEntityIdAsync(scope, toName);

                        // 如果目标实体不存在，先创建它
                        if (toEntity == null)
                        {
                            var newEntity = new EntityCanonicalRecord
                            {
                                EntityId = toName,
                                CurrentDisplayName = toName,
                                Aliases = new List<string>(),
                                IdentityStatus = EntityIdentityStatus.Confirmed,
                                CreatedAt = DateTime.UtcNow,
                                LastUpdated = DateTime.UtcNow,
                                Version = 1
                            };
                            await _db.UpsertEntityCanonicalAsync(scope, newEntity);
                            toEntity = toName;
                            _context.Log(LogLevel.Info, $"[AIMod:TRPG] identity_merge: 创建目标实体 {toName}");
                        }

                        await _entityCanonicalizer.MergeIdentityAsync(scope, fromName, toName, toEntity);
                    }
                    return (true, null);

                case "entity_change":
                    var entityParts = mutation.Content.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    if (entityParts.Length >= 1)
                    {
                        var displayName = entityParts[0].Trim();
                        var alias = entityParts.Length >= 2 ? entityParts[1].Trim() : displayName;
                        
                        // 使用EntityCanonicalizer解析实体ID
                        var resolvedEntityId = await _entityCanonicalizer.ResolveEntityIdAsync(scope, displayName) ?? displayName;
                        
                        // 检查实体是否存在，不存在则创建
                        var existingEntity = await _db.GetEntityCanonicalAsync(scope, resolvedEntityId);
                        if (existingEntity == null)
                        {
                            // 创建新实体
                            var newEntity = new EntityCanonicalRecord
                            {
                                EntityId = resolvedEntityId,
                                CurrentDisplayName = displayName,
                                Aliases = new List<string> { alias },
                                IdentityStatus = EntityIdentityStatus.Tentative,
                                CreatedAt = DateTime.UtcNow,
                                LastUpdated = DateTime.UtcNow,
                                Version = 1
                            };
                            await _db.UpsertEntityCanonicalAsync(scope, newEntity);
                            _context.Log(LogLevel.Info, $"[AIMod:TRPG] 创建新实体: {resolvedEntityId} ({displayName})");
                        }
                        else
                        {
                            // 冲突检测
                            var conflictDetected = false;
                            var conflictReasons = new List<string>();
                            
                            // 检测显示名称冲突
                            if (!string.Equals(existingEntity.CurrentDisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                            {
                                conflictDetected = true;
                                conflictReasons.Add($"显示名称变更: {existingEntity.CurrentDisplayName} -> {displayName}");
                            }
                            
                            // 检测别名冲突
                            var newAliases = alias.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(a => a.Trim())
                                .Where(a => !string.IsNullOrWhiteSpace(a))
                                .ToList();
                            
                            var conflictingAliases = newAliases.Where(a => 
                                !existingEntity.Aliases.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
                            
                            if (conflictingAliases.Count > 0)
                            {
                                conflictDetected = true;
                                conflictReasons.Add($"新增别名: {string.Join(", ", conflictingAliases)}");
                            }
                            
                            if (conflictDetected)
                            {
                                // 版本递增
                                existingEntity.Version++;
                                existingEntity.ConflictReason = string.Join("; ", conflictReasons);
                                existingEntity.LastUpdated = DateTime.UtcNow;
                                
                                // 应用变更
                                existingEntity.CurrentDisplayName = displayName;
                                foreach (var newAlias in newAliases)
                                {
                                    if (!existingEntity.Aliases.Contains(newAlias, StringComparer.OrdinalIgnoreCase))
                                    {
                                        existingEntity.Aliases.Add(newAlias);
                                    }
                                }
                                existingEntity.IdentityStatus = EntityIdentityStatus.Confirmed;
                                
                                await _db.UpsertEntityCanonicalAsync(scope, existingEntity);
                                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] 实体冲突检测: {resolvedEntityId} (版本 {existingEntity.Version}) - {existingEntity.ConflictReason}");
                            }
                            else
                            {
                                // 无冲突，正常更新
                                await _entityCanonicalizer.UpdateDisplayNameAsync(scope, resolvedEntityId, displayName);
                            }
                        }
                    }
                    return (true, null);

                case "entity_alias":
                    var aliasParts = mutation.Content.Split('|', StringSplitOptions.None);
                    if (aliasParts.Length >= 2)
                    {
                        var targetEntityId = aliasParts[0].Trim();
                        var aliasName = aliasParts[1].Trim();
                        if (!string.IsNullOrWhiteSpace(targetEntityId) && !string.IsNullOrWhiteSpace(aliasName))
                        {
                            var resolved = await _entityCanonicalizer.ResolveEntityIdAsync(scope, targetEntityId) ?? targetEntityId;
                            var entity = await _db.GetEntityCanonicalAsync(scope, resolved);
                            if (entity != null && !entity.Aliases.Contains(aliasName, StringComparer.OrdinalIgnoreCase))
                            {
                                entity.Aliases.Add(aliasName);
                                entity.LastUpdated = DateTime.UtcNow;
                                await _db.UpsertEntityCanonicalAsync(scope, entity);
                                _context.Log(LogLevel.Info, $"[AIMod:TRPG] NewNpcResolutionCheck: alias mapped {aliasName} -> {resolved}");
                            }
                        }
                    }
                    return (true, null);

                case "scene_snapshot":
                    // 场景快照由主模型通过标签维护
                    var sceneParts = mutation.Content.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    if (sceneParts.Length >= 1)
                    {
                        var sceneId = sceneParts[0].Trim();
                        var sceneName = sceneParts.Length >= 2 ? sceneParts[1].Trim() : sceneId;
                        var presentList = sceneParts.Length >= 3 ? sceneParts[2].Trim() : "";
                        
                        await _db.UpsertSceneDictionaryAsync(scope, sceneId, sceneName.Length > 50 ? sceneName.Substring(0, 50) : sceneName);

                        // 跳过重复场景切换
                        var latestEvent = await _eventLog.GetLatestEventAsync(scope);
                        var latestSceneId = latestEvent?.Payload.TryGetValue("scene_id", out var latestSceneObj) == true
                            ? latestSceneObj?.ToString()
                            : latestEvent?.Location;
                        if (latestEvent?.EventType == "scene_transition" &&
                            string.Equals(latestSceneId, sceneId, StringComparison.OrdinalIgnoreCase))
                        {
                            _context.Log(LogLevel.Debug, $"[AIMod:TRPG] 跳过重复场景切换事件: SceneId={sceneId}");
                            return (true, null);
                        }

                        // 构建 payload，包含 present_entities
                        var scenePayload = new Dictionary<string, object>
                        {
                            { "scene_id", sceneId },
                            { "scene_name", sceneName }
                        };
                        if (!string.IsNullOrWhiteSpace(presentList))
                        {
                            scenePayload["present_entities"] = presentList;
                            scenePayload["is_full_snapshot"] = true;
                        }

                        var sceneEvent = new WorldEvent
                        {
                            EventType = "scene_transition",
                            Actors = new List<string> { characterId },
                            Location = sceneId,
                            SceneId = sceneId,
                            Result = $"场景切换到: {sceneName}",
                            WorldChanges = new List<string> { $"scene_id={sceneId}" },
                            Timestamp = DateTime.UtcNow,
                            Payload = scenePayload
                        };
                        await _eventLog.AppendEventAsync(scope, sceneEvent);
                        
                        var presentInfo = string.IsNullOrWhiteSpace(presentList) ? "" : $" present_entities={presentList}";
                        _context.Log(LogLevel.Info, $"[AIMod:TRPG] 场景快照事件已记录: SceneId={sceneId}, SceneName={sceneName}{presentInfo}");
                        if (string.IsNullOrWhiteSpace(presentList))
                        {
                            _context.Log(LogLevel.Debug, $"[AIMod:TRPG] ScenePresenceDiagnostics | source=scene_snapshot | warning=present_list_empty | scene_id={sceneId}");
                        }
                    }
                    return (true, null);

                case "presence_snapshot":
                    // 在场快照：记录到 EventLog，由 StateInterceptor 更新 PresentEntities
                    var presenceParts = mutation.Content.Split('|', StringSplitOptions.None);
                    if (presenceParts.Length >= 4)
                    {
                        var presenceSceneId = presenceParts[0].Trim();
                        var presentList = presenceParts[1].Trim();
                        var absentList = presenceParts[2].Trim();
                        var isFullSnapshot = presenceParts[3].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                        var authority = presenceParts.Length >= 5 ? presenceParts[4].Trim() : "NarrativeInference";
                        var evidence = presenceParts.Length >= 6 ? presenceParts[5].Trim() : "";

                        var presenceEvent = new WorldEvent
                        {
                            EventType = "presence_snapshot",
                            Actors = new List<string> { characterId },
                            Location = presenceSceneId,
                            SceneId = presenceSceneId,
                            Result = $"在场快照: present={presentList}; absent={absentList}; full={isFullSnapshot}",
                            WorldChanges = new List<string>(),
                            Timestamp = DateTime.UtcNow,
                            Payload = new Dictionary<string, object>
                            {
                                { "scene_id", presenceSceneId },
                                { "present_entities", presentList },
                                { "absent_entities", absentList },
                                { "is_full_snapshot", isFullSnapshot },
                                { "authority", authority },
                                { "evidence", evidence }
                            }
                        };
                        await _eventLog.AppendEventAsync(scope, presenceEvent);
                        _context.Log(LogLevel.Info,
                            $"[AIMod:TRPG] presence_snapshot recorded | scene={presenceSceneId} | present={presentList} | absent={absentList} | full={isFullSnapshot} | authority={authority}");
                    }
                    return (true, null);

                case "entity_profile":
                    // 实体简介：更新 CoreSummary 和 PersistentFacts
                    var profileParts = mutation.Content.Split('|', StringSplitOptions.None);
                    if (profileParts.Length >= 1)
                    {
                        var entityName = profileParts[0].Trim();
                        var coreSummary = profileParts.Length >= 2 ? profileParts[1].Trim() : "";
                        var factsText = profileParts.Length >= 3 ? profileParts[2].Trim() : "";
                        var statusText = profileParts.Length >= 4 ? profileParts[3].Trim() : "";

                        if (!string.IsNullOrWhiteSpace(entityName))
                        {
                            var entityId = await _entityCanonicalizer.ResolveEntityIdAsync(scope, entityName) ?? entityName;
                            var entity = await _entityCanonicalizer.GetEntityAsync(scope, entityId);
                            if (entity != null)
                            {
                                if (!string.IsNullOrWhiteSpace(coreSummary))
                                    entity.CoreSummary = coreSummary;

                                // 写入稳定事实（去重）
                                if (!string.IsNullOrWhiteSpace(factsText))
                                {
                                    var facts = factsText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                    foreach (var fact in facts)
                                    {
                                        var exists = entity.PersistentFacts.Any(f =>
                                            string.Equals(f.Fact, fact, StringComparison.OrdinalIgnoreCase));
                                        if (!exists)
                                            entity.PersistentFacts.Add(new PersistentFact
                                            {
                                                Fact = fact,
                                                Category = "fact",
                                                IsActive = true,
                                                Salience = 0.7,
                                                EstablishedAt = DateTime.UtcNow
                                            });
                                    }
                                }

                                // 当前状态作为 status fact（去重）
                                if (!string.IsNullOrWhiteSpace(statusText))
                                {
                                    var exists = entity.PersistentFacts.Any(f =>
                                        string.Equals(f.Fact, statusText, StringComparison.OrdinalIgnoreCase));
                                    if (!exists)
                                        entity.PersistentFacts.Add(new PersistentFact
                                        {
                                            Fact = statusText,
                                            Category = "status",
                                            IsActive = true,
                                            Salience = 0.5,
                                            EstablishedAt = DateTime.UtcNow
                                        });
                                }

                                entity.EntityFactSummary = BuildEntityFactSummary(entity, statusText);
                                await _db.UpsertEntityCanonicalAsync(scope, entity);
                                _context.Log(LogLevel.Info,
                                    $"[AIMod:TRPG] entity_profile updated | entity={entityName} | summary_len={coreSummary.Length} | facts={factsText}");

                                // 触发 EntityProfileConsolidator 防止 facts 无限增长
                                var consolidator = new EntityProfileConsolidator(_db, _context);
                                await consolidator.ConsolidateIfNeededAsync(scope, entityId, "entity_profile");
                            }
                        }
                    }
                    return (true, null);

                case "event":
                    var worldEvent = new WorldEvent
                    {
                        EventType = mutation.EventType ?? "narrative",
                        Actors = new List<string> { characterId },
                        Location = mutation.SceneId ?? "unknown",
                        SceneId = mutation.SceneId ?? "",
                        Result = mutation.Content,
                        WorldChanges = new List<string>(),
                        Timestamp = DateTime.UtcNow
                    };
                    await _eventLog.AppendEventAsync(scope, worldEvent);
                    return (true, null);

                default:
                    return (false, $"未知的变更类型: {mutation.Type}");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 从标签字典构建事务
    /// </summary>
    public StateMutationTransaction BuildTransactionFromTags(
        Dictionary<string, string> tags,
        string? sceneId = null)
    {
        var transaction = new StateMutationTransaction
        {
            SceneId = sceneId,
            Mutations = new List<StateMutation>()
        };

        foreach (var (tagType, tagContent) in tags)
        {
            var mutation = new StateMutation
            {
                Type = tagType,
                Content = tagContent,
                SceneId = sceneId
            };

            // 设置优先级（仅对 objective 有效）
            if (tagType.ToLower() == "objective")
            {
                mutation.Priority = QuestPriority.Normal;
            }

            transaction.Mutations.Add(mutation);
        }

        return transaction;
    }

    private static string BuildEntityFactSummary(EntityCanonicalRecord entity, string? latestStatus)
    {
        var summaryParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(entity.CoreSummary))
            summaryParts.Add(entity.CoreSummary.Trim());

        var topFacts = entity.PersistentFacts
            .Where(f => f.IsActive)
            .OrderByDescending(f => f.Category.Equals("status", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.Salience)
            .Select(f => f.Fact.Trim())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        summaryParts.AddRange(topFacts);

        if (!string.IsNullOrWhiteSpace(latestStatus)
            && summaryParts.All(part => !part.Contains(latestStatus, StringComparison.OrdinalIgnoreCase)))
            summaryParts.Add(latestStatus.Trim());

        var combined = string.Join("；", summaryParts.Distinct(StringComparer.OrdinalIgnoreCase));
        return combined.Length <= 220 ? combined : combined[..220];
    }
}

/// <summary>
/// 状态变更事务
/// </summary>
public class StateMutationTransaction
{
    public string TransactionId { get; set; } = Guid.NewGuid().ToString();
    public string? SceneId { get; set; }
    public List<StateMutation> Mutations { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 单个状态变更
/// </summary>
public class StateMutation
{
    public string Type { get; set; } = "";
    public string Content { get; set; } = "";
    public string? SceneId { get; set; }
    public QuestPriority? Priority { get; set; }
    public string? EventType { get; set; }
}

/// <summary>
/// 状态变更结果
/// </summary>
public class StateMutationResult
{
    public string TransactionId { get; set; } = "";
    public long? SourceEventId { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<StateMutation> ExecutedMutations { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
}
