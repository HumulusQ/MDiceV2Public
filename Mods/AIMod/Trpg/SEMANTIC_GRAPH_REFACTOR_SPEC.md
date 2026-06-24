# AIMod TRPG 语义联想图与即时内心状态重构规格

> 目标读者：Codex / 后续实现者  
> 范围：`Mods/AIMod/Trpg` 以及 `Mods/AIMod/AIMod.cs` 初始化链路  
> 目标：用一个统一的“语义联想图 + 即时心理/情感自由文本状态”替代当前多套重复且相互割裂的长期记忆、实体、因果、叙事节点与目标/情绪硬状态系统。

---

## 0. 总体结论

当前 AIMod 已经有许多 graph-like 零件，但没有真正的“语义联想图”。现有系统的问题不是某一个模块坏了，而是长期层被拆成多套互相重复的结构：

- `LongTermMemory` / `MemoryNode`：语义节点索引，但在主 ActionContext 中不稳定可见。
- `CharacterMemory`：角色 IC 记忆真相层，但与 `MemoryNode` 没有强绑定。
- `NarrativeMemoryNode`：事件蒸馏产生的叙事节点，又是一套节点。
- `TimelineNodes`：层级时间轴，常生成过多低价值 L2。
- `CausalGraph`：事件之间的因果边表，看起来基本未接入主链。
- `EventLog` / `WorldStateProjection`：事件流 + 投影层，当前作为状态源，但不解决语义联想。
- `EntityCanonical` / `EntityCanonicalizer`：硬实体/别名/关系层，依赖精确匹配，不能处理跑团中的称谓漂移。
- `Quest` / `ObjectiveLayer`：程序化目标表，不适合作为角色“当前想法”。
- `AffectiveTagState` / `AffectiveTagController`：数值化情感标签，不适合作为高自由度情感叙述。

本次重构的目标是：

1. 将长期记忆统一为 `SemanticGraph`。
2. 将记忆召回从“线性 token / embedding 搜索”改为“节点激活扩散”。
3. 将称谓漂移（王奶奶 / 王阿姨 / 王杰辉 / 老王）交给图中的弱连接处理，而不是硬实体合并。
4. 将程序化目标和数值化情绪替换为两个由 AI 维护的即时自由文本状态：
   - 即时心理活动 `ThoughtText`
   - 即时情感叙述 `EmotionText`
5. 清理旧路径，保证只有一个长期记忆生成入口。

---

## 1. 当前代码中必须参考的关键位置

实现前请先阅读以下文件。这里列出的是需要参考、替换或删除根须的代码点。

### 1.1 初始化链路

文件：`Mods/AIMod/AIMod.cs`

重点方法：

- `InitializeTrpgComponents()`
- `CreateCharacterSession(...)`

当前初始化中创建了这些组件：

```csharp
var eventLog = new EventLog(_context, _trpgDb);
var semanticDistiller = new SemanticDistiller(...);
_memoryWatchdog = new MemoryWatchdog(... semanticDistiller ...);
_contextPipeline = new TrpgContextPipeline(...);
var entityCanonicalizer = new EntityCanonicalizer(...);
var objectiveLayer = new ObjectiveLayer(...);
var mutationPipeline = new StateMutationPipeline(... entityCanonicalizer, objectiveLayer ...);
var infoExtractor = new InfoExtractor(... entityCanonicalizer, objectiveLayer ...);
var archiveToGraph = new ArchiveToGraph(...);
var sceneTransitionHandler = new SceneTransitionHandler(... archiveToGraph ...);
var timelineWriter = new TimelineWriter(...);
var affectiveTagController = EnableAffectiveTags ? new AffectiveTagController(...) : null;
_stateInterceptor = new StateInterceptor(... infoExtractor, mutationPipeline, entityCanonicalizer, timelineWriter, sceneTransitionHandler, affectiveTagController);
```

重构后应新增并注入：

```csharp
var semanticGraph = new SemanticGraphRepository(_trpgDb, _context);
var semanticGraphWriter = new SemanticGraphWriter(semanticGraph, _context, _llmCallTracker, messages => CallTrpgApiWithFallbackAsync(messages));
var semanticGraphRecall = new SemanticGraphRecallService(semanticGraph, _context);
var innerStateStore = new CharacterInnerStateStore(_trpgDb, _context);
var thoughtMaintainer = new ThoughtStateMaintainer(innerStateStore, _context, _llmCallTracker, messages => CallTrpgApiWithFallbackAsync(messages));
var emotionMaintainer = new EmotionStateMaintainer(innerStateStore, _context, _llmCallTracker, messages => CallTrpgApiWithFallbackAsync(messages));
```

同时逐步移除或停用：

```csharp
EventLog
SemanticDistiller
ArchiveToGraph
SceneTransitionHandler 的 ArchiveToGraph 依赖
EntityCanonicalizer
ObjectiveLayer
AffectiveTagController
CausalGraph
```

注意：为了降低一次性破坏风险，可以先保留类文件，但不得让新主流程继续依赖它们。

---

### 1.2 当前主行动 prompt 注入点

文件：`Mods/AIMod/Trpg/StructuredActionContextRenderer.cs`

当前渲染内容大致是：

```csharp
AppendLineBlock(sb, "当前场景", pack.CurrentSceneText);
AppendTimeline(sb, "活跃时间线", pack.ActiveTimelineSkeleton.Take(6));
AppendCharacterMemories(sb, "角色 IC 记忆", pack.CharacterICMemory.Take(5));
AppendLineBlock(sb, "角色事实性认知", ...);
AppendEntities(sb, "当前实体", pack.PresentEntities.Take(4));
AppendMemory(sb, "PL 桌面记忆...", pack.PlayerTableMemory.Take(3));
AppendLineBlock(sb, "当前目标", pack.CurrentObjectives);
AppendLineBlock(sb, "当前物品/稳定认知", pack.InventoryState);
AppendLineBlock(sb, "当前情感框架", pack.AffectiveState);
AppendLineBlock(sb, "未解决线索/最近尝试结果", ...);
AppendHistory(sb, "最近原文", pack.RecentActiveHistory.TakeLast(8));
```

重构后必须改为明确注入：

```text
【本轮联想记忆】
...

【即时心理活动】
...

【即时情感叙述】
...
```

并逐步移除：

```text
当前目标
当前情感框架
PL 桌面记忆
EntityCanonical 生成的当前实体硬摘要
NarrativeMemoryNode 生成的叙事节点
旧 RecalledMemoryVar fallback
```

---

### 1.3 当前 recall 被短路的问题

文件：`Mods/AIMod/Trpg/PromptAssembler.cs`

重点方法：

```csharp
private string BuildBoundaryContextBlock(TrpgScope scope, string characterId, TrpgPromptContext trpgContext)
```

当前逻辑：

```csharp
if (!string.IsNullOrWhiteSpace(trpgContext.StructuredActionContextVar)
    && !string.Equals(trpgContext.StructuredActionContextVar.Trim(), "无", StringComparison.OrdinalIgnoreCase))
{
    return trpgContext.StructuredActionContextVar.Trim();
}
```

这会导致 `RecalledMemoryVar`、手动 `<recall>` retry 结果，以及旧 semantic recall 结果被 `StructuredActionContextVar` 短路。

新系统禁止再把召回结果塞进 `RecalledMemoryVar` 期待 fallback 生效。必须把图召回结果写入：

```csharp
TrpgAgentContextPack.GraphRecallEvidence
```

然后在 `StructuredActionContextRenderer` 中固定渲染。

---

### 1.4 当前语义节点生成与重复路径

文件：

- `Mods/AIMod/Trpg/MemoryWatchdog.cs`
- `Mods/AIMod/Trpg/CombinedMemoryFold.cs`

当前路径：

1. `MemoryWatchdog.CheckAndFoldAsync(...)`
2. `CombinedMemoryFoldRequest.BuildMessages(...)`
3. `CombinedMemoryFoldParser.TryParse(...)`
4. `PersistCombinedFoldResultAsync(...)`
5. 同时写入：
   - `CharacterMemory`
   - `LongTermMemory` / `MemoryNode`
   - `PlayerTableMemoryNode`
   - `TimelineNode`
   - `Quest`
   - `EventLog`

其中 IC candidate 当前会同时写：

```csharp
await _db.InsertCharacterMemoryAsync(...);
await _db.InsertCharacterMemoryNodeAsync(...);
```

PL candidate 当前写：

```csharp
await _db.InsertPlayerTableMemoryNodeAsync(...);
```

timeline candidate 当前直接写：

```csharp
await _db.InsertTimelineNodeAsync(scope, node);
```

IC-only repair：

```csharp
TryIcOnlyRepairAsync(...)
```

会再次请求 LLM 生成 `character_ic_memory_candidates`，再合并回 `foldResult`。

旧残留：

```csharp
ParseSemanticNodes(...)
LegacyDisabledTimelineRollupNodeAsync(...)
LegacyDisabledWorldEventsAsync(...)
LegacyDisabledTimelineSummaryAsync(...)
```

重构要求：

- 删除或停用 `CombinedMemoryFoldRequest` 的多产物输出。
- 删除或停用 `TryIcOnlyRepairAsync` 作为独立记忆生成路径。
- 删除或迁移 `ParseSemanticNodes` 和所有 `LegacyDisabled*`。
- 新增唯一长期写入入口：`SemanticGraphFoldExtractor`。

---

### 1.5 当前实体系统的问题

文件：`Mods/AIMod/Trpg/EntityCanonicalizer.cs`

当前解析逻辑：

```csharp
return entities.FirstOrDefault(e =>
    string.Equals(e.CurrentDisplayName, name, StringComparison.OrdinalIgnoreCase) ||
    e.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))?.EntityId;
```

它只能做显示名/别名精确匹配。

这无法解决跑团中的称谓漂移：

```text
王奶奶 / 王阿姨 / 王杰辉 / 老王
```

重构后：

- 不再依赖 `EntityCanonicalizer` 做弱联想。
- 所有称谓作为 `SemanticGraphNode(NodeKind=Name)`。
- 弱关系通过图边 `ALIAS_HINT`、`CO_OCCURS`、`SAME_SCENE`、`ABOUT` 等表达。
- 不要把弱联想写成硬身份事实。

---

### 1.6 当前 graph-like 系统状态

#### `CausalGraph.cs`

它定义了事件之间的因果边：

```text
Before / After / Simultaneous / Causes / Reveals / Enables / Blocks / Foreshadows / SameEntity / SameTopic / SameLocation
```

但它看起来没有被主流程实例化，不是当前实际运行中的语义联想图。

处理策略：

- 不要在新系统中继续扩展 `CausalGraph`。
- 新建 `SemanticGraphEdge` 替代它。
- 等新系统接入后删除 `CausalGraph.cs` 和 `CausalGraph` 表操作。

#### `ArchiveToGraph.cs`

名字误导。它不是图系统，而是：

```text
TimelineNode -> EpisodicMemory / NarrativeMemoryNode
```

处理策略：

- 删除或改写为 `SemanticGraphArchiver`。
- 不再写 `NarrativeMemoryNode`。

#### `EventLog.cs` / `WorldStateProjection.cs`

当前 Event Sourcing 事实源。新系统目标是完全替代，但建议分阶段：

- 第一阶段保留 `EventLog` 物理表与写入以避免破坏现有状态流。
- 新代码不要再以 `EventLog` 作为语义召回核心。
- 第二阶段将世界状态/事实改为图节点与边。
- 第三阶段移除 `WorldStateProjection`、`EventLog.GenerateEventsSummaryString`、`CausalGraph` 相关后果链。

#### `NarrativeMemoryNode`

当前由 `SemanticDistiller` 从事件蒸馏生成，召回逻辑在 `TrpgContextPipeline.BuildNarrativeMemoryLinesAsync`。

处理策略：

- `SemanticDistiller` 不再创建 `NarrativeMemoryNode`。
- `NarrativeMemoryNode` 表和相关 scorer 应被替换为图召回。
- `BuildNarrativeMemoryLinesAsync` 应删除或改为调用 `SemanticGraphRecallService`。

---

## 2. 新系统核心概念

### 2.1 语义联想图不是事实断言图

图可以表达：

```text
老王 与 王奶奶相关记忆有弱联想。
```

但绝不能表达成：

```text
老王就是王奶奶。
```

除非 GM 明确确认。弱联想只用于召回扩展，不能写成角色 IC 硬事实。

---

### 2.2 节点类型

建议最小节点类型：

```csharp
public static class SemanticGraphNodeKind
{
    public const string Memory = "memory";       // 具体记忆卡片
    public const string Token = "token";         // 关键词
    public const string Name = "name";           // 表层称呼
    public const string Topic = "topic";         // 主题 / 线索 / 谜团
    public const string Scene = "scene";         // 场景/地点
    public const string EntityAnchor = "entity_anchor"; // 弱实体锚点，不等于确认身份
}
```

其中 `Memory` 是最终注入 prompt 的主节点；其他节点用于激活扩散。

---

### 2.3 边类型

建议第一版只实现基础边，避免污染：

```csharp
public static class SemanticGraphEdgeKind
{
    public const string Mentions = "MENTIONS";       // memory -> token/name
    public const string About = "ABOUT";             // memory -> topic
    public const string InScene = "IN_SCENE";         // memory -> scene
    public const string CoOccurs = "CO_OCCURS";       // token/name/topic 共同出现
    public const string Speaker = "SPEAKER";         // memory -> name
    public const string AliasHint = "ALIAS_HINT";     // name -> name/entity_anchor，弱联想
    public const string SameScene = "SAME_SCENE";     // memory/topic/name 同场景联动
}
```

后续再考虑高级边：

```text
CAUSES / REVEALS / CONTRADICTS / CORRECTS / ENABLES / BLOCKS
```

第一版不要让 AI 自由生成高级边。

---

## 3. 新数据库结构

建议新建专门 partial 文件：

```text
Mods/AIMod/Trpg/ChatDatabase.SemanticGraph.cs
Mods/AIMod/Trpg/ChatDatabase.InnerState.cs
```

不要继续把所有新 SQL 塞进当前巨大的 `ChatDatabase.cs`。

### 3.1 `SemanticGraphNode`

```sql
CREATE TABLE IF NOT EXISTS SemanticGraphNode (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    WorldId TEXT NOT NULL,
    GroupId INTEGER NOT NULL,
    CharacterId TEXT NOT NULL DEFAULT '',

    NodeKind TEXT NOT NULL,
    Text TEXT NOT NULL,
    Summary TEXT NOT NULL DEFAULT '',

    Importance REAL NOT NULL DEFAULT 0,
    AssignedImportance REAL NOT NULL DEFAULT 0,

    SourceScope TEXT NOT NULL DEFAULT '',
    SourceMessageIds TEXT NOT NULL DEFAULT '[]',
    RawExcerpt TEXT NOT NULL DEFAULT '[]',
    Metadata TEXT NOT NULL DEFAULT '{}',

    CreatedAt TEXT NOT NULL,
    LastActivatedAt TEXT NULL,
    ActivationCount INTEGER NOT NULL DEFAULT 0,
    IsDeleted INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_sgn_world_group_kind_text
    ON SemanticGraphNode(WorldId, GroupId, NodeKind, Text);

CREATE INDEX IF NOT EXISTS idx_sgn_world_group_char_kind
    ON SemanticGraphNode(WorldId, GroupId, CharacterId, NodeKind, IsDeleted);

CREATE INDEX IF NOT EXISTS idx_sgn_importance
    ON SemanticGraphNode(WorldId, GroupId, Importance DESC);
```

说明：

- `Text` 用于 token/name/topic/scene 的规范文本，memory 节点可放短标题或 summary 前缀。
- `Summary` 对 memory 节点必须是“带来源的命题”。例如：
  - 好：`王奶奶声称河伯祭祀由来已久，每年都会出人命。`
  - 坏：`河伯祭祀每年都会出人命。`
- `Importance = CurrentKillFloor + AssignedImportance`。
- 不实现 pin。核心节点靠高初始重要性自然长期保留。

---

### 3.2 `SemanticGraphEdge`

```sql
CREATE TABLE IF NOT EXISTS SemanticGraphEdge (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    WorldId TEXT NOT NULL,
    GroupId INTEGER NOT NULL,
    CharacterId TEXT NOT NULL DEFAULT '',

    SourceNodeId INTEGER NOT NULL,
    TargetNodeId INTEGER NOT NULL,
    EdgeKind TEXT NOT NULL,
    Weight REAL NOT NULL DEFAULT 1.0,

    Evidence TEXT NOT NULL DEFAULT '',
    SourceMessageIds TEXT NOT NULL DEFAULT '[]',
    Metadata TEXT NOT NULL DEFAULT '{}',

    CreatedAt TEXT NOT NULL,
    LastReinforcedAt TEXT NULL,
    ReinforceCount INTEGER NOT NULL DEFAULT 0,

    UNIQUE(WorldId, GroupId, CharacterId, SourceNodeId, TargetNodeId, EdgeKind)
);

CREATE INDEX IF NOT EXISTS idx_sge_source
    ON SemanticGraphEdge(WorldId, GroupId, CharacterId, SourceNodeId);

CREATE INDEX IF NOT EXISTS idx_sge_target
    ON SemanticGraphEdge(WorldId, GroupId, CharacterId, TargetNodeId);

CREATE INDEX IF NOT EXISTS idx_sge_kind
    ON SemanticGraphEdge(WorldId, GroupId, CharacterId, EdgeKind);
```

写边时如果唯一键冲突，不新建，增强：

```sql
ON CONFLICT(...) DO UPDATE SET
    Weight = MIN(1.0, Weight + @reinforceDelta),
    LastReinforcedAt = @now,
    ReinforceCount = ReinforceCount + 1
```

---

### 3.3 `SemanticTokenStats`

```sql
CREATE TABLE IF NOT EXISTS SemanticTokenStats (
    WorldId TEXT NOT NULL,
    GroupId INTEGER NOT NULL,
    TokenText TEXT NOT NULL,
    NodeCount INTEGER NOT NULL DEFAULT 0,
    UpdatedAt TEXT NOT NULL,
    PRIMARY KEY(WorldId, GroupId, TokenText)
);

CREATE INDEX IF NOT EXISTS idx_token_stats_token
    ON SemanticTokenStats(WorldId, GroupId, TokenText);
```

计算稀有度：

```csharp
static double RarityWeight(int nodeCount)
{
    if (nodeCount <= 0) return 1.0;
    return 1.0 / Math.Sqrt(nodeCount);
}
```

不要维护硬编码负 token。高频 token 自然低权重。

---

### 3.4 `SemanticGraphMeta`

```sql
CREATE TABLE IF NOT EXISTS SemanticGraphMeta (
    WorldId TEXT NOT NULL,
    GroupId INTEGER NOT NULL,
    Key TEXT NOT NULL,
    Value TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    PRIMARY KEY(WorldId, GroupId, Key)
);
```

用于存储：

```text
KillFloor
LastGraphPruneAt
GraphSchemaVersion
```

---

### 3.5 `CharacterInnerState`

```sql
CREATE TABLE IF NOT EXISTS CharacterInnerState (
    WorldId TEXT NOT NULL,
    GroupId INTEGER NOT NULL,
    CharacterId TEXT NOT NULL,
    ThoughtText TEXT NOT NULL DEFAULT '',
    EmotionText TEXT NOT NULL DEFAULT '',
    UpdatedAt TEXT NOT NULL,
    PRIMARY KEY(WorldId, GroupId, CharacterId)
);
```

不需要 history，不需要 revision，不回滚。

程序只做：

```text
读取旧文本 -> 提交给 AI 维护器 -> 整体替换保存 -> 注入主行动 AI
```

不要拆成 JSON 字段，不要标签化，不要数值化。

---

## 4. 新类与职责

### 4.1 `SemanticGraphRepository`

文件建议：

```text
Mods/AIMod/Trpg/SemanticGraph/SemanticGraphRepository.cs
```

职责：数据库读写。

核心方法：

```csharp
Task<long> UpsertNodeAsync(TrpgScope scope, SemanticGraphNode node);
Task<long> UpsertSurfaceNodeAsync(TrpgScope scope, string nodeKind, string text, string characterId = "");
Task UpsertEdgeAsync(TrpgScope scope, long sourceId, long targetId, string edgeKind, double weight, string evidence, string characterId = "");
Task<List<SemanticGraphNode>> FindSurfaceNodesAsync(TrpgScope scope, IEnumerable<string> texts, IEnumerable<string> kinds);
Task<List<SemanticGraphEdge>> GetOutgoingEdgesAsync(TrpgScope scope, long sourceNodeId, string characterId, int limit);
Task<List<SemanticGraphEdge>> GetIncomingEdgesAsync(TrpgScope scope, long targetNodeId, string characterId, int limit);
Task<Dictionary<string, int>> GetTokenNodeCountsAsync(TrpgScope scope, IEnumerable<string> tokens);
Task IncrementTokenStatsAsync(TrpgScope scope, IEnumerable<string> tokens);
Task DecrementTokenStatsAsync(TrpgScope scope, IEnumerable<string> tokens);
Task<double> GetKillFloorAsync(TrpgScope scope);
Task SetKillFloorAsync(TrpgScope scope, double value);
Task<int> PruneBelowKillFloorAsync(TrpgScope scope);
```

---

### 4.2 `SemanticGraphFoldExtractor`

替代：

```text
CombinedMemoryFoldRequest
CombinedMemoryFoldParser
TryIcOnlyRepairAsync
ParseSemanticNodes
LegacyDisabled*
```

职责：从待折叠历史生成 graph memory candidates。

输入：

```csharp
TrpgScope scope
string characterId
List<ChatHistoryEntry> toFold
string currentSceneText
string currentSceneId
string recentContext
```

输出 JSON：

```json
{
  "memory_candidates": [
    {
      "summary": "王奶奶声称河伯祭祀由来已久，每年都会出人命。",
      "surface_tokens": ["河伯祭祀", "人命"],
      "name_tokens": ["王奶奶"],
      "topic_tokens": ["河伯祭祀传闻"],
      "scene_tokens": ["村子"],
      "assigned_importance": 70,
      "source_message_ids": ["123", "124"],
      "raw_excerpt": "“我们村有河伯祭祀的历史，由来已久，但每年都会出人命”王奶奶说。",
      "stance": "王奶奶声称；未由 GM 旁白确认为客观事实"
    }
  ]
}
```

Prompt 要求：

```text
- summary 必须保留信息来源：GM确认 / NPC声称 / 角色怀疑 / PL讨论。
- token 只取可用于回忆的核心词，不取泛词。
- 每条 memory 最多 3 surface_tokens、2 name_tokens、2 topic_tokens、1 scene_tokens。
- assigned_importance 0-100。
- 不输出目标更新。
- 不输出情感标签。
- 不输出时间线 L1/L2/L3。
- 不输出实体合并判断。
```

重要性建议：

```text
0-10: 闲聊或重复低价值信息，通常不要生成节点。
10-30: 普通行动/短期细节。
30-55: 可用于后续判断的线索、NPC说法、地点/物品信息。
55-80: 主线线索、死亡、身份、谜团、关键物品、重大关系变化。
80-100: 战役核心设定或几乎不可遗忘的核心揭示。
```

实现原则：

- 如果 parse 失败，不删除历史。
- 如果输出为空，但折叠窗口显然包含剧情信息，不删除历史。
- 不再使用 IC-only repair；如要 repair，只允许修 JSON 格式，不允许重新总结。

---

### 4.3 `SemanticGraphWriter`

职责：把 candidates 写成图。

伪代码：

```csharp
async Task WriteCandidatesAsync(TrpgScope scope, string characterId, List<GraphMemoryCandidate> candidates)
{
    var killFloor = await repo.GetKillFloorAsync(scope);

    foreach (var c in candidates)
    {
        var assigned = Math.Clamp(c.AssignedImportance, 0, 100);
        if (assigned <= 0 || string.IsNullOrWhiteSpace(c.Summary)) continue;

        var memoryNode = new SemanticGraphNode
        {
            NodeKind = SemanticGraphNodeKind.Memory,
            CharacterId = characterId,
            Text = BuildMemoryTitle(c.Summary),
            Summary = c.Summary.Trim(),
            AssignedImportance = assigned,
            Importance = killFloor + assigned,
            SourceScope = "GraphFold",
            SourceMessageIds = JsonSerializer.Serialize(c.SourceMessageIds),
            RawExcerpt = JsonSerializer.Serialize(new [] { c.RawExcerpt }),
            Metadata = JsonSerializer.Serialize(new { c.Stance })
        };

        var memoryId = await repo.UpsertNodeAsync(scope, memoryNode);

        foreach (var name in c.NameTokens.Take(2))
        {
            var nameId = await repo.UpsertSurfaceNodeAsync(scope, "name", name, characterId);
            await repo.UpsertEdgeAsync(scope, memoryId, nameId, "MENTIONS", 0.90, c.RawExcerpt, characterId);
            await repo.UpsertEdgeAsync(scope, memoryId, nameId, "SPEAKER", 0.70, c.RawExcerpt, characterId);
        }

        foreach (var token in c.SurfaceTokens.Take(3))
        {
            var tokenId = await repo.UpsertSurfaceNodeAsync(scope, "token", token, characterId);
            await repo.UpsertEdgeAsync(scope, memoryId, tokenId, "MENTIONS", 0.80, c.RawExcerpt, characterId);
        }

        foreach (var topic in c.TopicTokens.Take(2))
        {
            var topicId = await repo.UpsertSurfaceNodeAsync(scope, "topic", topic, characterId);
            await repo.UpsertEdgeAsync(scope, memoryId, topicId, "ABOUT", 0.95, c.RawExcerpt, characterId);
        }

        foreach (var scene in c.SceneTokens.Take(1))
        {
            var sceneId = await repo.UpsertSurfaceNodeAsync(scope, "scene", scene, characterId);
            await repo.UpsertEdgeAsync(scope, memoryId, sceneId, "IN_SCENE", 0.60, c.RawExcerpt, characterId);
        }

        await CreateCoOccurrenceEdges(scope, characterId, c);
        await repo.IncrementTokenStatsAsync(scope, AllSurfaceTexts(c));
    }
}
```

`CreateCoOccurrenceEdges` 第一版只在同一条 memory 的 name/topic/token 之间建立低权重 `CO_OCCURS`：

```text
name --CO_OCCURS--> topic
name --CO_OCCURS--> token
topic --CO_OCCURS--> token
```

不要第一版就做复杂 alias merge。

---

### 4.4 `SemanticGraphRecallService`

职责：本轮 query token -> 图激活扩散 -> evidence pack。

输入：

```csharp
TrpgScope scope
string characterId
string latestText
List<ChatHistoryEntry> recentHistory
int maxResults = 8
```

输出：

```csharp
GraphRecallResult
{
    List<GraphRecallHit> Hits;
    string ToPromptString();
}
```

#### Query token 提取

第一版可以用规则 + 可选 LLM。

规则提取：

- 从最近 3-8 条对话中取文本。
- 保留中文连续词、专名、引号内词、明显人名/地名/物品名。
- 不硬编码负 token。
- 过短 token `<2` 丢弃。
- 每轮最多 12 个 query tokens。

可选 LLM 提取器：

```text
你只负责从最近对话中提取可用于回忆的关键词。输出 JSON 数组。
不要输出泛词，不要输出情绪形容词，不要输出“事情/感觉/这里”之类。
最多 12 个。
```

若 LLM 失败，回退规则提取。

#### 激活扩散伪代码

```csharp
async Task<List<GraphRecallHit>> RecallAsync(...)
{
    var queryTokens = ExtractQueryTokens(latestText, recentHistory);
    var tokenCounts = await repo.GetTokenNodeCountsAsync(scope, queryTokens);

    var scores = new Dictionary<long, GraphRecallAccumulator>();
    var frontier = new PriorityQueue<ActivationState>();

    foreach (var token in queryTokens)
    {
        var rarity = 1.0 / Math.Sqrt(Math.Max(1, tokenCounts.GetValueOrDefault(token, 1)));
        var starts = await repo.FindSurfaceNodesAsync(scope, new [] { token }, new [] { "token", "name", "topic", "scene" });
        foreach (var start in starts)
        {
            frontier.Enqueue(new ActivationState
            {
                NodeId = start.Id,
                Score = 1.0 * rarity,
                Depth = 0,
                Path = token
            });
        }
    }

    var visitedBest = new Dictionary<long, double>();

    while (frontier.Count > 0 && explored < 200)
    {
        var state = frontier.Dequeue();
        if (state.Depth > 2) continue;
        if (visitedBest.TryGetValue(state.NodeId, out var best) && best >= state.Score) continue;
        visitedBest[state.NodeId] = state.Score;

        var node = await repo.GetNodeAsync(scope, state.NodeId);
        if (node.NodeKind == "memory")
        {
            AddMemoryScore(scores, node, state);
            continue;
        }

        var edges = await repo.GetEdgesAroundAsync(scope, state.NodeId, characterId, limit: 12);
        foreach (var edge in edges)
        {
            var nextId = edge.Other(state.NodeId);
            var degreePenalty = 1.0 / Math.Sqrt(Math.Max(1, edge.OtherNodeDegree));
            var edgeKindWeight = EdgeKindWeight(edge.EdgeKind);
            var nextScore = state.Score * edge.Weight * edgeKindWeight * degreePenalty * DepthDecay(state.Depth + 1);
            if (nextScore < 0.02) continue;
            frontier.Enqueue(new ActivationState
            {
                NodeId = nextId,
                Score = nextScore,
                Depth = state.Depth + 1,
                Path = state.Path + " → " + DescribeEdge(edge)
            });
        }
    }

    return scores.Values
        .Select(acc => acc.ToHit())
        .OrderByDescending(hit => hit.FinalScore)
        .Take(maxResults)
        .ToList();
}
```

#### 最终评分

```csharp
FinalScore =
    ActivationScore
  + Math.Log10(1 + Importance) * 0.25
  + MultiPathBonus
  + RecentActivationBonus;
```

其中：

- `ActivationScore`：所有路径累加。
- `MultiPathBonus`：同一个 memory 被多个 query token / 多条路径命中时增加。
- `Importance`：节点长期重要性。
- 不使用硬编码负 token。
- 高频词通过 degree penalty 和 token rarity 自然降权。

#### Prompt 输出格式

```text
【本轮联想记忆】
- 王奶奶声称河伯祭祀由来已久，每年都会出人命。
  命中路径：河伯祭祀 → 同主题；人命 → 同结果；老王 → 王姓称呼弱联想 → 王奶奶
  重要性：86；匹配度：0.72
  注意：弱联想路径不代表身份已被确认。
```

限制：

- 最多 5-8 条。
- 每条 summary 最多 120 字。
- 每条最多显示 2 条路径。
- 永远保留“弱联想不等于事实确认”的提示。

---

### 4.5 `GraphPruner`

职责：斩杀线维护。

规则：

```csharp
async Task AdvanceKillFloorAsync(TrpgScope scope)
{
    var floor = await repo.GetKillFloorAsync(scope);
    floor += 0.03; // 初始建议值，后续可配置
    await repo.SetKillFloorAsync(scope, floor);
}

async Task PruneAsync(TrpgScope scope)
{
    var floor = await repo.GetKillFloorAsync(scope);
    var deletedMemoryNodes = await repo.DeleteMemoryNodesWhereImportanceBelowAsync(scope, floor);
    await repo.DeleteEdgesAttachedToDeletedNodesAsync(scope);
    await repo.DeleteOrphanSurfaceNodesAsync(scope);
    await repo.RebuildOrRepairTokenStatsAsync(scope); // 可定期做
}
```

新节点 importance：

```csharp
storedImportance = currentKillFloor + assignedImportance;
```

不实现 pin。

核心设定只要 `assignedImportance` 接近 100，就会自然长期存活。

---

## 5. 即时心理活动与即时情感叙述

### 5.1 替代目标

要替代：

- `ObjectiveLayer` / `Quest`
- `AffectiveTagController` / `AffectiveTagState`

新状态只存当前文本：

```text
ThoughtText：当前心理活动
EmotionText：当前情感叙述
```

二者必须分开，不能合并成另一个上下文摘要。

---

### 5.2 `ThoughtStateMaintainer`

维护心理活动：

```text
我现在想做什么？
我为什么想这么做？
上一轮已经尝试过什么？
接下来应该避免重复什么？
哪些只是猜测，不能当成事实？
```

Prompt 草案：

```text
你正在维护一个 TRPG 角色的“即时心理活动”文本区。

这是角色当前未说出口的思考、行动倾向、自我提醒和短期判断。
它不是世界事实，不能替代 GM 叙述。

输入：
1. 旧心理活动
2. 最新 GM/PL/AI 相关消息
3. 角色刚才的行动或发言
4. 本轮联想记忆摘要
5. 当前场景

请整体重写新的心理活动。
规则：
- 只输出自然语言段落，不要 JSON。
- 可以自由删除过时想法。
- 可以合并重复想法。
- 可以新增新的判断、计划和自我提醒。
- 不要把未经 GM 确认的猜测写成事实。
- 不要写情绪描写；情绪交给情感叙述维护器。
- 控制在 80-240 字。
```

---

### 5.3 `EmotionStateMaintainer`

维护情感叙述：

```text
我现在以什么情绪面对局面？
我对某人/某事的态度残留是什么？
这些情绪如何影响语气、犹豫、警觉、亲近、回避？
哪些情绪已经淡化？
```

Prompt 草案：

```text
你正在维护一个 TRPG 角色的“即时情感叙述”文本区。

这是角色当前情绪、态度残留、压力、警觉、信任/怀疑等复杂感受。
它不是世界事实，不能替代 GM 叙述。

输入：
1. 旧情感叙述
2. 最新 GM/PL/AI 相关消息
3. 角色刚才的行动或发言
4. 当前心理活动，可作为参考
5. 本轮联想记忆摘要

请整体重写新的情感叙述。
规则：
- 只输出自然语言段落，不要 JSON。
- 不要使用数值、标签或强度等级。
- 可以自由删除已经淡化的情绪。
- 可以合并重复情绪。
- 可以加入新的情绪残留和表达倾向。
- 不要把怀疑、恐惧、亲近等情绪写成已确认事实。
- 不要写行动计划；行动计划交给心理活动维护器。
- 控制在 80-240 字。
```

---

### 5.4 调用时机

建议两次机会：

1. GM/PL 消息进入后，主行动前更新。
2. AI 响应完成后，基于自己的行动再更新一次。

第一版可先只做第 1 次，避免调用成本过高。

接入点：`AiCharacterSession.RespondAsync(...)`

当前流程：

```csharp
await _memoryWatchdog.CheckAndFoldAsync(...);
var trpgContext = await _contextPipeline.BuildContextAsync(...);
var messages = await _promptAssembler.BuildAsync(...);
var response = await CallAiAsync(...);
```

新流程：

```csharp
await _memoryWatchdog.CheckAndFoldAsync(...); // 改为 GraphFold
var graphRecall = await _semanticGraphRecall.BuildEvidencePackAsync(...);
await _thoughtMaintainer.UpdateAsync(scope, characterId, graphRecall, latestText, recentHistory);
await _emotionMaintainer.UpdateAsync(scope, characterId, graphRecall, latestText, recentHistory);
var trpgContext = await _contextPipeline.BuildContextAsync(...);
trpgContext.AgentContextPack.GraphRecallEvidence = graphRecall.ToPromptString();
trpgContext.AgentContextPack.ThoughtText = innerState.ThoughtText;
trpgContext.AgentContextPack.EmotionText = innerState.EmotionText;
var messages = await _promptAssembler.BuildAsync(...);
```

---

## 6. `TrpgAgentContextPack` 修改

文件：`Mods/AIMod/Trpg/TrpgAgentContextPack.cs`

新增字段：

```csharp
public string GraphRecallEvidence { get; set; } = "无";
public string ThoughtText { get; set; } = "无";
public string EmotionText { get; set; } = "无";
```

`ForActionContextView()` 仍调用 `StructuredActionContextRenderer`。

`StructuredActionContextRenderer.Render(...)` 新顺序建议：

```csharp
AppendLineBlock(sb, "当前场景", pack.CurrentSceneText);
AppendLineBlock(sb, "本轮联想记忆", pack.GraphRecallEvidence);
AppendLineBlock(sb, "即时心理活动", pack.ThoughtText);
AppendLineBlock(sb, "即时情感叙述", pack.EmotionText);
AppendLineBlock(sb, "当前物品/稳定认知", pack.InventoryState);
AppendHistory(sb, "最近原文", pack.RecentActiveHistory.TakeLast(8));
AppendLineBlock(sb, "边界", "联想记忆和即时内心状态只影响行动倾向，不替代 GM 最新叙述；弱联想不代表身份确认。")
```

逐步移除或降级：

```csharp
ActiveTimelineSkeleton
CharacterICMemory
PlayerTableMemory
CurrentObjectives
AffectiveState
EntityCanonicalRecords / PresentEntities 里的实体硬摘要
IdentityHints
```

第一阶段可保留若干旧字段以防 prompt 过瘦，但新主路径应依赖：

```text
当前场景 + 本轮联想记忆 + 即时心理活动 + 即时情感叙述 + 最近原文 + 物品硬状态
```

---

## 7. 替代旧系统的具体删除/停用清单

### 7.1 立即停止新增依赖

不要再写新代码调用：

```text
CausalGraph
ArchiveToGraph
SemanticDistiller
NarrativeMemoryNode scorer
EntityCanonicalizer.ResolveEntityIdAsync 作为弱别名解析
ObjectiveLayer.GenerateActionableObjectivesStringAsync
AffectiveTagController.FormatForPrompt
SearchMemoryNodesBySimilarityAsync 作为主召回
```

---

### 7.2 `MemoryWatchdog` 清理

文件：`Mods/AIMod/Trpg/MemoryWatchdog.cs`

要替换：

```text
CombinedMemoryFoldRequest.BuildMessages
PersistCombinedFoldResultAsync
TryIcOnlyRepairAsync
ParseSemanticNodes
LegacyDisabledTimelineRollupNodeAsync
LegacyDisabledWorldEventsAsync
LegacyDisabledTimelineSummaryAsync
```

新职责：

```text
历史达到折叠阈值
→ 调用 SemanticGraphFoldExtractor
→ SemanticGraphWriter 写图
→ GraphPruner 抬斩杀线并清理
→ 成功后删除旧 ChatHistory
```

伪代码：

```csharp
public async Task<bool> CheckAndFoldAsync(TrpgScope scope, string characterId)
{
    var activeEntries = await _db.GetActiveHistoryAsync(scope, characterId);
    if (!ShouldFold(activeEntries)) return false;

    var toFold = activeEntries.OrderBy(x => x.CreatedAt).Take(_config.HistoryFoldCount).ToList();

    var candidates = await _graphFoldExtractor.ExtractAsync(scope, characterId, toFold);
    if (candidates.ParseFailed) return false;
    if (candidates.MemoryCandidates.Count == 0 && ContainsNarrativeMaterial(toFold)) return false;

    await _semanticGraphWriter.WriteCandidatesAsync(scope, characterId, candidates.MemoryCandidates);
    await _graphPruner.AdvanceKillFloorAsync(scope);
    await _graphPruner.PruneAsync(scope);

    await _db.DeleteHistoryEntriesAsync(scope, toFold.Select(x => x.Id).ToList());
    return true;
}
```

---

### 7.3 `TrpgContextPipeline` 清理

文件：`Mods/AIMod/Trpg/TrpgContextPipeline.cs`

要替换/删除：

```text
SearchMemoryNodesBySimilarityAsync
FilterSemanticRecallNodes
BuildSemanticIndexWithTruthAsync
BuildSemanticIndexLines
BuildRawExcerptLines
BuildNarrativeMemoryLinesAsync
QueryNarrativeMemoryNodesAsync 召回链
SearchPlayerTableMemoryNodesAsync
```

新增：

```csharp
var graphRecall = await _semanticGraphRecall.BuildEvidencePackAsync(scope, aiChar.CharacterId, queryText, activeHistory);
var innerState = await _innerStateStore.GetAsync(scope, aiChar.CharacterId);
```

写入 context pack：

```csharp
contextPack.GraphRecallEvidence = graphRecall.ToPromptString();
contextPack.ThoughtText = innerState.ThoughtText;
contextPack.EmotionText = innerState.EmotionText;
```

---

### 7.4 `InfoExtractor` / `StateInterceptor` 清理

文件：

- `Mods/AIMod/Trpg/InfoExtractor.cs`
- `Mods/AIMod/Trpg/StateInterceptor.cs`
- `Mods/AIMod/Trpg/StateMutationPipeline.cs`

当前 `InfoExtractor` 会提取：

```text
scene_snapshot
entity_change
new_entity_check
identity_merge
objective
complete
abandon
event
fact
relationship
summary
presence_snapshot
entity_profile
affective_tag
inventory_mutation
```

重构原则：

- 不再提取 `objective / complete / abandon` 写 Quest。
- 不再提取 `affective_tag` 写 AffectiveTagState。
- 不再用 `entity_change / identity_merge / relationship` 维护硬实体关系作为召回来源。
- 第一阶段可以保留 `scene_snapshot / presence_snapshot / inventory_mutation`，因为它们关系到当前场景与物品硬状态。
- 所有“事实/关系/身份线索”应进入 `SemanticGraphFoldExtractor` 或 `SemanticGraphWriter`，不再由 `StateMutationPipeline` 分散写入。

如果仍需要即时处理 GM 明确纠正，可写成高重要性 Graph memory：

```text
GM 明确纠正：X 不是 Y。
Tokens: X / Y / 纠正
Importance: CurrentKillFloor + 90
EdgeKind: CONTRADICTS / CORRECTS  // 可第二阶段实现
```

---

### 7.5 `CausalGraph` 删除路径

文件：

```text
Mods/AIMod/Trpg/CausalGraph.cs
```

新系统稳定后：

- 删除该文件。
- 删除 `ChatDatabase` 中 `CausalGraph` 表 CRUD：
  - `InsertCausalEdgeAsync`
  - `GetCausalEdgesBySourceAsync`
  - `GetCausalEdgesByTargetAsync`
  - `GetAllCausalEdgesAsync`
  - `DeleteCausalEdgeAsync`
  - `UpdateCausalEdgeWeightAsync`
- 删除 `EventLog.LinkCausalChainAsync` 对 `Consequences` 的依赖。
- `EventLog.Consequences` 不再作为 graph 结构。

如需要因果关系，使用：

```text
SemanticGraphEdge.EdgeKind = CAUSES / FORESHADOWS / REVEALS
```

但第一版可暂不实现高级因果边。

---

### 7.6 `NarrativeMemoryNode` 删除路径

文件/表：

```text
NarrativeMemoryNode table
SemanticDistiller.cs
NarrativeMemoryRecallScorer
TrpgContextPipeline.BuildNarrativeMemoryLinesAsync
ChatDatabase.QueryNarrativeMemoryNodesAsync
ChatDatabase.InsertNarrativeMemoryNodeAsync
ChatDatabase.ResolveNarrativeMemoryNodesByEventAsync
```

替代：

```text
SemanticGraphNode(NodeKind=memory)
SemanticGraphEdge
SemanticGraphRecallService
```

---

### 7.7 `EntityCanonical` 删除/降级路径

文件：

```text
EntityCanonicalizer.cs
EntityProfileConsolidator.cs
EntitySalienceService.cs
```

表：

```text
EntityCanonical
CharacterHotMeta
EntitySalience
NpcCanonicalState
BehaviorEvidence
```

注意：这个部分风险最大。建议分两步：

第一阶段：

- 不再把 `EntityCanonical` 内容注入 ActionContext。
- 不再依赖 `ResolveEntityIdAsync` 做召回。
- Graph 中建立 `Name` / `EntityAnchor` 节点来处理称谓。

第二阶段：

- 删除或迁移 `EntityCanonical` 表。
- 如果需要确认身份，用 graph 边：

```text
NameNode(老王) --CONFIRMED_ALIAS--> EntityAnchorNode(王杰辉)
NameNode(王奶奶) --ALIAS_HINT--> EntityAnchorNode(王姓老人称呼簇)
```

区别：

- `ALIAS_HINT`：弱联想，只召回，不确认。
- `CONFIRMED_ALIAS`：GM 明确确认后才可当硬身份。

---

### 7.8 `Quest` / `AffectiveTag` 删除路径

表：

```text
Quest
AffectiveTagState
AffectiveTagEvent
```

类：

```text
ObjectiveLayer.cs
AffectiveTags.cs / AffectiveTagController
```

替代：

```text
CharacterInnerState.ThoughtText
CharacterInnerState.EmotionText
```

如果短期需要兼容旧字段：

- `CurrentObjectives` 渲染为 `ThoughtText`。
- `AffectiveState` 渲染为 `EmotionText`。

但不要再维护 Quest / AffectiveTag。

---

## 8. 迁移策略

### 8.1 阶段 A：Shadow Graph

目标：不改变主行动结果，只写图并打日志。

任务：

1. 加表。
2. 加 `SemanticGraphRepository`。
3. 加 `SemanticGraphFoldExtractor`。
4. 在 `MemoryWatchdog.CheckAndFoldAsync` 成功折叠前写 graph，但暂不注入 prompt。
5. 记录 debug：query tokens、候选节点、激活路径、top hits。

验收：

- 不破坏现有回复。
- 新表有节点和边。
- 对“老王 / 河伯祭祀 / 人命”这种 query 能召回“王奶奶声称...”类节点。

---

### 8.2 阶段 B：Prompt 注入

目标：图召回进入 ActionAgent。

任务：

1. `TrpgAgentContextPack` 增加 `GraphRecallEvidence / ThoughtText / EmotionText`。
2. `StructuredActionContextRenderer` 增加三个区块。
3. `<recall>` retry 改用 `SemanticGraphRecallService`，不要写 `RecalledMemoryVar`。
4. 修复 `PromptAssembler` 里 `StructuredActionContextVar` 短路导致召回丢失的问题。

验收：

- 完整 prompt 日志中能看到 `【本轮联想记忆】`。
- 手动 `<recall>` 后 retry prompt 中也能看到图召回结果。

---

### 8.3 阶段 C：即时心理/情感状态

任务：

1. 新增 `CharacterInnerState` 表。
2. 新增 `ThoughtStateMaintainer`。
3. 新增 `EmotionStateMaintainer`。
4. 在主行动前更新并注入。
5. 停用 `ObjectiveLayer.GenerateActionableObjectivesStringAsync` 与 `AffectiveTagController.FormatForPrompt`。

验收：

- prompt 中有 `【即时心理活动】` 与 `【即时情感叙述】`。
- 数据库只保存当前文本，无 history。
- 不再新增 `Quest` 和 `AffectiveTagState` 记录。

---

### 8.4 阶段 D：停用旧记忆生成

任务：

1. `CombinedMemoryFoldRequest` 替换为 `SemanticGraphFoldExtractor`。
2. 删除 IC-only repair。
3. 删除 legacy semantic parser。
4. 停用 `SemanticDistiller` 生成 `NarrativeMemoryNode`。
5. 停用 `LongTermMemory` 新写入。

验收：

- 新折叠不再新增 `LongTermMemory`、`CharacterMemory`、`NarrativeMemoryNode`、`Quest`、`AffectiveTagState`。
- 新长期信息只写入 `SemanticGraphNode/Edge/TokenStats`。

---

### 8.5 阶段 E：删除旧根须

在前面阶段稳定后删除：

```text
CausalGraph.cs
ArchiveToGraph.cs
SemanticDistiller.cs
ObjectiveLayer.cs
AffectiveTags.cs
NarrativeMemoryRecallScorer.cs
旧 LongTermMemory 召回 CRUD
EntityCanonicalizer 弱召回用途
WorldStateProjection 事件投影用途
```

数据库不要破坏性 drop。旧表可以保留 unused，或者提供显式迁移命令，避免用户数据直接丢失。

---

## 9. 测试用例

### 9.1 Token 稀有度

准备节点：

```text
节点 A: 河伯祭祀 / 王奶奶 / 人命
节点 B: 河伯祭祀 / 村长 / 祭品
节点 C: 村子 / 事情 / 感觉
大量节点包含“村子”
```

query：

```text
王奶奶提到河伯祭祀会死人
```

期望：

- A 排第一。
- “村子”不应因高频泛化导致 C 排高。

---

### 9.2 称谓漂移弱联想

输入历史：

```text
“我们村有河伯祭祀的历史，由来已久，但每年都会出人命”王奶奶说。
老王又提到河伯祭祀不是第一次死人了。
```

期望：

- 生成两个 memory。
- `王奶奶`、`老王` 都是 NameNode。
- 它们通过 `河伯祭祀` / `人命` / `村子` 等共同节点形成弱联想。
- query `老王 河伯祭祀 人命` 能召回王奶奶那条。
- prompt 显示“弱联想”，不声明同一人。

---

### 9.3 核心节点长期存活

设置：

```text
KillFloor = 500
AssignedImportance = 95
StoredImportance = 595
```

斩杀线逐步上升时，该节点不会短期被删。

---

### 9.4 低价值节点自然删除

设置：

```text
KillFloor = 500
低价值节点 StoredImportance = 505
KillFloor 上升到 506
```

期望：

- 节点被删除。
- 相关边删除。
- token stats 修正。

---

### 9.5 召回进 prompt

在调试日志中确认：

```text
【本轮联想记忆】
【即时心理活动】
【即时情感叙述】
```

三者都出现在最终 `MainCharacterResponse` prompt 中。

---

## 10. 明确禁止事项

1. 不要再新增一套孤立 memory 表。
2. 不要让 `EntityCanonical` 继续承担弱别名联想。
3. 不要让 `Quest` 继续作为主行动目标来源。
4. 不要让 `AffectiveTagState` 继续作为主行动情绪来源。
5. 不要把图中的弱联想写成事实。
6. 不要硬编码大规模负 token 表。
7. 不要用 pin；核心节点靠高初始重要性保留。
8. 不要破坏性 drop 旧表。
9. 不要让召回结果只写 `RecalledMemoryVar`。
10. 不要让 AI 自由生成复杂因果边作为第一版。

---

## 11. 最小可交付版本

如果实现时间有限，最小版本只做：

1. `SemanticGraphNode / Edge / TokenStats / Meta` 表。
2. `SemanticGraphFoldExtractor` 从折叠窗口生成 memory + tokens。
3. `SemanticGraphWriter` 写 memory、token/name/topic/scene 节点和基础边。
4. `SemanticGraphRecallService` 做 1-2 层激活扩散。
5. `StructuredActionContextRenderer` 注入 `【本轮联想记忆】`。
6. `CharacterInnerState` 表。
7. `ThoughtText / EmotionText` 注入 prompt。

旧系统可以暂时不删，但必须停止作为新主路径继续扩展。

---

## 12. 示例：完整数据流

原文：

```text
“我们村有河伯祭祀的历史，由来已久，但每年都会出人命”王奶奶说。
```

AI 折叠输出：

```json
{
  "memory_candidates": [
    {
      "summary": "王奶奶声称河伯祭祀由来已久，每年都会出人命。",
      "surface_tokens": ["河伯祭祀", "人命"],
      "name_tokens": ["王奶奶"],
      "topic_tokens": ["河伯祭祀传闻"],
      "scene_tokens": ["村子"],
      "assigned_importance": 70,
      "source_message_ids": ["123"],
      "raw_excerpt": "“我们村有河伯祭祀的历史，由来已久，但每年都会出人命”王奶奶说。",
      "stance": "NPC 王奶奶声称，未由 GM 旁白确认为客观事实"
    }
  ]
}
```

写图：

```text
MemoryNode: 王奶奶声称河伯祭祀由来已久，每年都会出人命。
NameNode: 王奶奶
TopicNode: 河伯祭祀传闻
TokenNode: 河伯祭祀
TokenNode: 人命
SceneNode: 村子

Memory --SPEAKER--> 王奶奶
Memory --ABOUT--> 河伯祭祀传闻
Memory --MENTIONS--> 河伯祭祀
Memory --MENTIONS--> 人命
Memory --IN_SCENE--> 村子
王奶奶 --CO_OCCURS--> 河伯祭祀传闻
河伯祭祀 --CO_OCCURS--> 人命
```

后续 query：

```text
老王又提到河伯祭祀会死人。
```

激活：

```text
老王
河伯祭祀
死人
```

可能路径：

```text
河伯祭祀 -> Memory
死人 -> 人命 -> Memory
老王 -> 共同主题/同场景弱联想 -> 王奶奶 -> Memory
```

prompt 注入：

```text
【本轮联想记忆】
- 王奶奶声称河伯祭祀由来已久，每年都会出人命。
  命中路径：河伯祭祀；死人→人命；老王→同主题弱联想→王奶奶
  重要性：570；匹配度：0.74
  注意：弱联想不代表“老王就是王奶奶”。
```

---

## 13. 完成标准

实现完成后应满足：

- 新长期记忆主路径只有 `SemanticGraph`。
- 主行动 prompt 一定能看到图召回 evidence pack。
- 心理活动和情感叙述是两个分离的自由文本状态。
- 低价值节点会被 KillFloor 自然删除。
- 高频泛 token 通过稀有度/度数自然降权。
- 称谓不统一通过图中弱联想改善，而不是强制实体合并。
- 旧系统不再新增数据，不再作为主行动依据。
