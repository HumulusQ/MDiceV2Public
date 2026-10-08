# AIMod 垃圾节点修复方案

**日期**: 2026-05-29  
**问题**: SemanticDistiller 的 BuildDistillationPrompt() 包含空壳事件，浪费 token 并降低 LLM 质量  
**优先级**: High  
**预计工期**: 2-3小时

---

## 📊 问题诊断

### 症状
```
❌ 31个 narrative 节点进入prompt，全为空壳
   - Actors: []（空数组）
   - Location: ""（空字符串）
   - Result: ""（空字符串）
   - Payload: {}（空对象）

❌ Token浪费
   - 每个垃圾节点: ~25 tokens
   - 合计浪费: 31 × 25 = 775 tokens
   - 占总体的: ~15-20%

❌ LLM质量下降
   - 输入信号中混有噪声
   - LLM需要过滤无意义节点，浪费reasoning能力
```

### 根本原因

**文件**: [SemanticDistiller.cs](SemanticDistiller.cs#L195-L215)  
**方法**: `BuildDistillationPrompt()`

```csharp
// ❌ 问题代码
foreach (var evt in events)
{
    sb.AppendLine($"[Event_{evt.EventId}] 类型: {evt.EventType}");
    sb.AppendLine($"  时间: {evt.Timestamp:yyyy-MM-dd HH:mm}");
    sb.AppendLine($"  参与者: {string.Join(", ", evt.Actors)}");       // 可能为空!
    sb.AppendLine($"  位置: {evt.Location}");                          // 可能为空!
    if (!string.IsNullOrEmpty(evt.Result))
        sb.AppendLine($"  结果: {evt.Result}");
    if (evt.Payload.Count > 0)                                         // 只检查Count，未检查Value
    {
        sb.AppendLine($"  负载: {JsonSerializer.Serialize(evt.Payload)}");
    }
    sb.AppendLine();
}
```

**无过滤逻辑** 导致：
- `narrative` 类型事件（通常为空）直接被发送
- 空字符串/null值被序列化后变成 `""` 或 `null`
- LLM需要处理这些无意义的信息

---

## 🔧 修复方案

### 第一部分：垃圾节点检测

**新增方法** - 在 `SemanticDistiller` 类中添加：

```csharp
/// <summary>
/// 检测是否为垃圾节点
/// 垃圾节点定义：
/// - EventType 为 "narrative" 且无实际内容
/// - 或参与者、位置、结果都为空
/// - 或 Payload 为空或仅含null值
/// </summary>
private bool IsGarbageEvent(WorldEvent evt)
{
    // 规则1: narrative 类型的空壳事件
    if (evt.EventType.Equals("narrative", StringComparison.OrdinalIgnoreCase))
    {
        return string.IsNullOrWhiteSpace(evt.Result) &&
               evt.Actors.Count == 0 &&
               string.IsNullOrWhiteSpace(evt.Location) &&
               evt.Payload.Count == 0;
    }

    // 规则2: 所有关键字段都为空
    if (string.IsNullOrWhiteSpace(evt.Result) &&
        evt.Actors.Count == 0 &&
        string.IsNullOrWhiteSpace(evt.Location) &&
        string.IsNullOrWhiteSpace(evt.SceneId))
    {
        // 但如果 Payload 有意义内容，仍保留
        if (evt.Payload.Count == 0 || 
            evt.Payload.All(p => p.Value == null || 
                                 (p.Value is string s && string.IsNullOrWhiteSpace(s))))
        {
            return true;
        }
    }

    return false;
}

/// <summary>
/// 验证并统计垃圾节点
/// </summary>
private (List<WorldEvent> validEvents, int garbageCount) FilterGarbageEvents(List<WorldEvent> events)
{
    var validEvents = new List<WorldEvent>();
    int garbageCount = 0;

    foreach (var evt in events)
    {
        if (IsGarbageEvent(evt))
        {
            garbageCount++;
            _context.Log(LogLevel.Debug, 
                $"[AIMod:TRPG] 已过滤垃圾节点 | EventId={evt.EventId} | Type={evt.EventType}");
        }
        else
        {
            validEvents.Add(evt);
        }
    }

    if (garbageCount > 0)
    {
        _context.Log(LogLevel.Info, 
            $"[AIMod:TRPG] 垃圾节点过滤完成 | 总数={events.Count} | 有效={validEvents.Count} | 垃圾={garbageCount} | 过滤率={(garbageCount * 100.0 / events.Count):F1}%");
    }

    return (validEvents, garbageCount);
}
```

---

### 第二部分：改进 BuildDistillationPrompt()

**替换现有方法**：

```csharp
private string BuildDistillationPrompt(List<WorldEvent> events)
{
    // ===== 阶段1: 过滤垃圾节点 =====
    var (validEvents, garbageCount) = FilterGarbageEvents(events);
    
    if (validEvents.Count == 0)
    {
        _context.Log(LogLevel.Warn, 
            "[AIMod:TRPG] BuildDistillationPrompt: 无有效事件可处理");
        return "";
    }

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("你是一个叙事分析专家。请分析以下事件列表，为每个事件生成语义元数据。");
    sb.AppendLine();
    sb.AppendLine("输出格式（JSON）：");
    sb.AppendLine("{");
    sb.AppendLine("  \"events\": {");
    sb.AppendLine("    \"事件ID\": {");
    sb.AppendLine("      \"semantic_summary\": \"事件的叙事性总结（1句话，避免技术术语）\",");
    sb.AppendLine("      \"narrative_weight\": 0.0-1.0,");
    sb.AppendLine("      \"narrative_tags\": [\"标签1\", \"标签2\"],");
    sb.AppendLine("      \"emotional_weight\": -1.0-1.0,");
    sb.AppendLine("      \"arc_affinity\": \"剧情弧标识（可选）\"");
    sb.AppendLine("    }");
    sb.AppendLine("  }");
    sb.AppendLine("}");
    sb.AppendLine();
    sb.AppendLine("事件列表：");
    sb.AppendLine("---");

    // ===== 阶段2: 优化事件描述 =====
    foreach (var evt in validEvents)
    {
        // 使用紧凑格式，仅包含非空字段
        sb.AppendLine($"[Event_{evt.EventId}] 类型: {LocalizeEventType(evt.EventType)}");
        sb.AppendLine($"  时间: {evt.Timestamp:yyyy-MM-dd HH:mm}");
        
        // 条件性输出字段（仅当非空时）
        if (evt.Actors.Count > 0)
            sb.AppendLine($"  参与者: {string.Join(", ", evt.Actors)}");
        
        if (!string.IsNullOrWhiteSpace(evt.Location))
            sb.AppendLine($"  位置: {evt.Location}");
        
        if (!string.IsNullOrWhiteSpace(evt.Result))
            sb.AppendLine($"  结果: {evt.Result}");
        
        // 仅当 Payload 有有效内容时才输出
        var validPayload = evt.Payload
            .Where(p => p.Value != null && 
                       !(p.Value is string s && string.IsNullOrWhiteSpace(s)))
            .ToDictionary(p => p.Key, p => p.Value);
        
        if (validPayload.Count > 0)
        {
            sb.AppendLine($"  负载: {JsonSerializer.Serialize(validPayload)}");
        }
        
        sb.AppendLine();
    }

    // ===== 阶段3: 记录统计 =====
    var originalCount = events.Count;
    var finalCount = validEvents.Count;
    var tokenReduction = (garbageCount * 25); // 估计每个垃圾节点25 tokens
    
    _context.Log(LogLevel.Info,
        $"[AIMod:TRPG] Prompt构建完成 | 原始={originalCount} | 有效={finalCount} | " +
        $"过滤={garbageCount} | 估计节省={tokenReduction} tokens");

    return sb.ToString();
}

/// <summary>
/// 本地化事件类型为中文
/// </summary>
private string LocalizeEventType(string eventType)
{
    return eventType.ToLowerInvariant() switch
    {
        "scene_transition" => "场景转换",
        "state_transaction" => "状态事务",
        "narrative" => "叙事",
        "combat" => "战斗",
        "dialogue" => "对话",
        "discovery" => "发现",
        "item_acquisition" => "获得物品",
        "npc_death" => "NPC死亡",
        "relationship_change" => "关系变化",
        "npc_identity_reveal" => "NPC身份揭示",
        "objective_change" => "目标变化",
        "objective_complete" => "目标完成",
        "objective_update" => "目标更新",
        _ => eventType
    };
}
```

---

### 第三部分：修改 DistillEventsAsync()

**更新调用处理**：

```csharp
private async Task DistillEventsAsync(List<WorldEvent> events, long groupId, string characterId)
{
    // ===== 新增: 预先过滤 =====
    var (validEvents, garbageCount) = FilterGarbageEvents(events);
    
    if (validEvents.Count == 0)
    {
        _context.Log(LogLevel.Warn, 
            $"[AIMod:TRPG] DistillEventsAsync: 无有效事件 (总数={events.Count}, 垃圾={garbageCount})");
        
        // 即使没有有效事件，仍然标记为已处理
        foreach (var evt in events.Where(e => IsGarbageEvent(e)))
        {
            evt.IsSemanticallyDistilled = true;
            await _db.UpdateEventSemanticMetadataAsync(evt.EventId, evt);
        }
        
        return;
    }

    _context.Log(LogLevel.Info,
        $"[AIMod:TRPG] 叙事节点 LLM 蒸馏开始 | 事件数={validEvents.Count} | " +
        $"（已过滤{garbageCount}个垃圾节点） | EventIds={string.Join(",", validEvents.Take(3).Select(e => e.EventId))}{(validEvents.Count > 3 ? "..." : "")}");

    var prompt = BuildDistillationPrompt(validEvents);
    
    if (string.IsNullOrEmpty(prompt))
    {
        _context.Log(LogLevel.Error, "[AIMod:TRPG] Prompt 构建失败");
        return;
    }
    
    var response = await _llmCaller(prompt);
    if (string.IsNullOrEmpty(response))
    {
        _context.Log(LogLevel.Warn, "[AIMod:TRPG] 语义蒸馏 LLM 返回空响应");
        return;
    }
    
    _context.Log(LogLevel.Info,
        $"[AIMod:TRPG] 叙事节点 LLM 蒸馏完成 | 事件数={validEvents.Count}");
    
    var distillationResults = ParseDistillationResponse(response);

    // ... 后续处理逻辑保持不变 ...
}
```

---

## 📋 验收标准

- [ ] `IsGarbageEvent()` 方法能正确识别垃圾节点
- [ ] `FilterGarbageEvents()` 能准确统计过滤数量
- [ ] `BuildDistillationPrompt()` 不包含垃圾节点
- [ ] Prompt 中仅包含非空字段
- [ ] 日志显示过滤率和 Token 节省
- [ ] LLM 响应质量提升（语义摘要更精准）
- [ ] 总 Token 消耗降低 15-20%
- [ ] 性能提升 10%+（处理速度快）

---

## 🧪 测试场景

### 场景1：垃圾节点过滤

```
输入: 50个事件 (含 15个垃圾narrative节点)
预期:
- FilterGarbageEvents() 返回 (35个有效, 15个垃圾)
- 日志: "垃圾节点过滤完成 | 总数=50 | 有效=35 | 垃圾=15 | 过滤率=30.0%"
- Token 节省: ~375 tokens
```

### 场景2：Prompt 构建

```
输入: 35个有效事件 (无垃圾)
预期:
- Prompt 中所有字段都非空
- 没有 "参与者: " 后跟空行的情况
- JSON 负载仅包含有值的字段
```

### 场景3：边界情况

```
输入: 全是垃圾节点 (50个narrative空壳)
预期:
- FilterGarbageEvents() 返回 (0个有效, 50个垃圾)
- 日志出现警告: "无有效事件可处理"
- 所有事件仍被标记为 IsSemanticallyDistilled=true
```

---

## 🚀 实装步骤

1. **第一步**: 添加 `IsGarbageEvent()` 和 `FilterGarbageEvents()` 方法
2. **第二步**: 添加 `LocalizeEventType()` 方法
3. **第三步**: 重写 `BuildDistillationPrompt()` 方法
4. **第四步**: 更新 `DistillEventsAsync()` 调用处理

---

## 📈 效果预期

| 指标 | 改进前 | 改进后 | 提升 |
|------|-------|-------|------|
| Prompt Token数 | ~6000 | ~4800 | -20% |
| LLM 处理时间 | 8-10s | 6-8s | -20% |
| 语义摘要质量 | 普通 | 优秀 | ⬆️ |
| 垃圾节点率 | 31% | 0% | -100% |

---

**制作者**: AI Assistant  
**审核状态**: 待审核  
**优先级**: High  
**预计工期**: 2-3 小时
