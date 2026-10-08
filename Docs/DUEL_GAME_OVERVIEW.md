# .duel 指令小游戏系统详尽总结

## 一、系统概述

`.duel` 是MDiceV2中内置的对战卡牌小游戏，允许玩家与AI进行回合制对战。游戏通过收集卡牌（角色卡和特殊卡）来增长三维属性（武力、财力、名声），最终在达到回合上限时根据属性差距决定胜负。

### 核心特征
- **游戏类型**：回合制卡牌对战游戏
- **对战方式**：玩家(Player2) vs AI机器人(Player1/魔王军)
- **游戏长度**：最多20回合（在EndTurn时检查）
- **结束条件**：达到第20回合时结算，或任一方某项属性≤-10时立即败北
- **游戏状态**：使用内存持久化 + JSON快照持久化

---

## 二、回合计算方式

### 2.1 回合流程图

```
玩家调用 .duel
   ↓
检查权限和日限
   ↓
加载/创建游戏状态
   ↓
执行 TurnManager.StartTurn()
   ├─ 为双方抽卡各一张加入手牌（考虑阵营限制）
   ├─ AI自动处理手牌（ExecuteTurn）
   └─ 人类玩家进入手牌操作阶段（IsProcessingHandAction=true）
   ↓
等待玩家卡牌决策
   └─ 使用手牌编号.操作的格式（如 1.1, 2.y, 3.n 或输入 0 跳过）
   ↓
执行 TurnManager.EndTurn()
   ├─ 为双方场地追加每回合属性累计
   ├─ 结算所有在场技能
   ├─ 检查游戏结束条件
   └─ 如未结束，当前回合数++ 并开始新回合
   ↓
检查游戏是否结束
   └─ 若结束，执行 SettleGame() 结算胜负
```

### 2.2 回合开始（StartTurn）

**触发时机**：
- 新游戏初始化时（CurrentTurn=1）
- 上一回合正常结束后（递归调用）

**执行步骤**：

1. **为双方抽卡**：
   ```csharp
   DrawOneCardForHand(Player1, messages);  // AI
   DrawOneCardForHand(Player2, messages);  // 人类玩家
   ```
   - 调用 `DrawCardForPlayer(playerIndex)` 根据玩家阵营抽卡
   - Player1（魔王军）：从魔王军角色池和特殊卡池中随机抽取
   - Player2（人类方）：从人类阵营角色池和特殊卡池中随机抽取
   - 50% 概率抽角色卡，50% 概率抽特殊卡（权重系统）

2. **AI自动处理**：
   ```csharp
   var aiMessages = _aiController.ExecuteTurn(null);
   ```
   - AI随机选择1-2张手牌进行使用
   - 角色卡：根据 FieldPreference 选择最合适的场地放置
   - 特殊卡：如果存在，执行特殊卡的立即技能

3. **人类玩家手牌操作**：
   ```csharp
   _gameState.IsProcessingHandAction = true;
   ```
   - 进入手牌决策模式，等待玩家输入
   - 显示手牌信息：`GetHandInfo()`
   - 提示操作格式

### 2.3 玩家手牌操作

**格式**：`手牌编号.操作`

**角色卡操作**：
- `1.1` - 将第1张卡放到前场（武力场）
- `1.2` - 将第1张卡放到中场（财力场）
- `1.3` - 将第1张卡放到后场（名声场）

**特殊卡操作**：
- `2.y` - 使用第2张特殊卡
- `2.n` - 弃置第2张特殊卡（不使用）

**跳过操作**：
- `0` - 跳过剩余回合，保留所有手牌

**处理流程**（UseCardFromHand）**：
1. 验证命令格式和卡牌索引
2. 根据卡牌类型分别处理：
   - **CharacterCard**：调用 `PlaceCharacterCard()` 放置到指定场地
   - **SpecialCard**：调用 `PlaySpecialCard()` 或直接弃置
3. 从手牌列表移除已处理卡牌
4. 检查是否还有手牌：
   - 若有：继续等待下一张卡牌操作
   - 若无：立即进入 EndTurn 阶段

### 2.4 回合结束（EndTurn）

**执行步骤**：

1. **追加每回合属性累计**：
   ```csharp
   void AppendCharacterStatsToFields(Player player)
   {
       TotalPower += FieldManager.FrontField.Characters.Sum(c => c.PerTurnPower);
       TotalWealth += FieldManager.MiddleField.Characters.Sum(c => c.PerTurnWealth);
       TotalFame += FieldManager.BackField.Characters.Sum(c => c.PerTurnFame);
   }
   ```
   - 对前场（Front）：累计所有角色的 PerTurnPower
   - 对中场（Middle）：累计所有角色的 PerTurnWealth
   - 对后场（Back）：累计所有角色的 PerTurnFame

2. **结算技能效果**（ProcessSkills）：
   - **在场技能（Field）**：概率触发，对在场所有角色执行
   - **连携技能（Chain）**：在场角色按条件触发
   - **事件技能（Event）**：每回合从三维属性最高的场地随机选一个角色触发
   - **回合结束技能（TurnEnd）**：所有在场角色在回合结束时触发

3. **显示游戏状态**：
   ```csharp
   GameStateUtils.GetGameStatus(_gameState)
   // 输出：Player1和Player2的当前三维属性
   ```

4. **检查游戏结束条件（每个回合都检查）**：
   ```csharp
   if (CheckGameEndCondition())
   {
       _gameState.IsGameOver = true;
       var settlementMessages = SettleGame();  // 执行最终结算
   }
   else
   {
       _gameState.CurrentTurn++;  // 进入下一回合
   }
   ```
   - **立即败北判定**（优先级最高）：每个EndTurn时检查任一属性是否 ≤ -10
   - **回合上限判定**：达到第20回合时结束游戏
   - 满足任一条件即进入 SettleGame() 进行最终结算

### 2.5 游戏结算（SettleGame）

**触发条件**：
- 任一方的某项属性 ≤ -10（立即败北，在EndTurn检查）
- 达到第20回合（在EndTurn检查）

**结算流程**：

1. **收集最终属性**：
   ```csharp
   int player1Power = Player1.GetTotalPower();
   int player1Wealth = Player1.GetTotalWealth();
   int player1Fame = Player1.GetTotalFame();
   // Player2 同理
   ```

2. **立即败北判定**（优先级最高，在EndTurn时已检查）：
   ```csharp
   if (player1某属性 <= -10 || player2某属性 <= -10) {
       触发该方立即败北
       // 此时SettleGame仅需确认败者并返回结果
   }
   ```
   - 若在SettleGame被调用时已确认立即败北，直接返回败北结果
   - 无需再进行属性对比

3. **属性对比结算**（仅当无立即败北）：
   - 计算三维属性的差距
   - 找出差距最大的属性（决定性属性）
   - 比较该属性高低，决定胜负

**胜利判定**：
```
立即败北条件：任一属性 ≤ -10 时直接判负

属性对比条件（仅当到达第20回合且无立即败北）：
决定性属性 = max(|武力差|, |财力差|, |名声差|) 对应的属性
胜者 = 该属性值更高的一方
```

---

## 三、人物卡（Character Card）系统

### 3.1 人物卡数据结构

```csharp
public class Character
{
    public string Name { get; set; }              // 角色名称
    
    // 基础属性（放置时立即生效）
    public int Power { get; set; }                // 武力
    public int Wealth { get; set; }               // 财力
    public int Fame { get; set; }                 // 名声
    
    // 每回合长期贡献（在EndTurn时每回合累计）
    public int PerTurnPower { get; set; }         // 每回合武力贡献
    public int PerTurnWealth { get; set; }        // 每回合财力贡献
    public int PerTurnFame { get; set; }          // 每回合名声贡献
    
    // 场地偏向（用于AI决策）
    public FieldType FieldPreference { get; set; } // Front/Middle/Back
    
    // 技能系统
    public List<LuaSkill> LuaSkills { get; set; } // Lua技能列表
    public List<SkillAction> Skills { get; set; } // 传统委托技能（兼容旧系统）
}
```

### 3.2 人物卡的作用

**放置阶段（PlaceCharacterCard）**：

1. **立即生效的基础属性**：
   ```csharp
   if (fieldType == Front) {
       player.TotalPower += character.Power;      // 立即加入武力
   } else if (fieldType == Middle) {
       player.TotalWealth += character.Wealth;    // 立即加入财力
   } else if (fieldType == Back) {
       player.TotalFame += character.Fame;        // 立即加入名声
   }
   ```

2. **记录每回合长期贡献**：
   ```csharp
   player.CharacterPower += character.PerTurnPower;     // 记录供EndTurn使用
   player.CharacterWealth += character.PerTurnWealth;
   player.CharacterFame += character.PerTurnFame;
   ```

3. **触发登场技能**：
   ```csharp
   var entranceSkills = character.GetSkillsByTrigger(SkillTrigger.Entrance);
   foreach (var luaSkill in entranceSkills) {
       luaSkill.Execute(context);  // 执行Lua代码
   }
   ```

**回合进行阶段**：

- **在场技能（Field）**：每个EndTurn时可能触发，产生随机效果
- **连携技能（Chain）**：需要满足特定条件才触发
- **事件技能（Event）**：每回合从三维最高的场地随机选择一个角色触发
- **回合结束技能（TurnEnd）**：每个回合结束时自动触发

### 3.3 场地系统

```csharp
public class FieldManager
{
    public Field FrontField { get; set; }    // 前场（武力属性）
    public Field MiddleField { get; set; }   // 中场（财力属性）
    public Field BackField { get; set; }     // 后场（名声属性）
    public int CombinedMax { get; set; }     // 前中后合计上限
}
```

**场地特性**：
- 每个场地有独立的角色数量上限
- 前中后场合计有全局上限（通常为6）
- 角色类型与场地绑定，放置后不能更换场地
- 不同场地的角色贡献不同类型的属性

### 3.4 人物卡加载机制

**数据来源**：

1. **内置角色**（embedded resources）：
   - 路径：`MDiceV2.Core.GameBattle.Data.characters.json`
   - 在 `BuiltinCharacterProvider.GetBuiltinCharacters()` 中加载

2. **扩展角色**（external files）：
   - 位置：`%ApplicationDirectory%/Duel/Extension/Character/`
   - Lua脚本：`%ApplicationDirectory%/Duel/Extension/Character/skills.lua`
   - JSON数据：`*.json` 文件自动合并

**加载流程**（GameLoader.Initialize）**：

```csharp
1. 扫描 Extension/Character/ 目录下的 *.json 文件
2. 合并 JsonCharacters（内置）和 ExtensionCharacters（扩展）
3. 加载 skills.lua 中的 Lua 技能定义
4. 为每个角色创建 LuaSkill 对象，绑定对应的 Lua 函数
5. 按阵营（Human/Demon）分类存储到内存
```

**阵营限制**：
- Player1（魔王军）只能抽取 Faction=Demon 的卡牌
- Player2（人类方）只能抽取 Faction=Human 的卡牌

---

## 四、外部脚本（Lua）与游戏系统的沟通

### 4.1 Lua技能系统架构

**核心概念**：
- 所有游戏逻辑（技能、特殊卡效果）都可以用 Lua 编写
- 通过 MoonSharp 库将 Lua 集成到 C# 环境中
- 每个 LuaSkill 持有一个 MoonSharp.Interpreter.Script 对象

### 4.2 LuaSkill 数据结构

```csharp
public class LuaSkill
{
    public string SkillId { get; set; }              // 技能唯一ID
    public string Name { get; set; }                 // 技能名称（显示用）
    public string Description { get; set; }          // 技能描述
    public SkillTrigger Trigger { get; set; }        // 触发时机
    public string LuaFunctionName { get; set; }      // Lua中的函数名
    public Dictionary<string, object> Parameters { get; set; } // 技能参数
    public Script LuaScript { get; set; }            // 关联的 Lua 脚本对象
}
```

### 4.3 技能执行上下文（ISkillContext）

Lua脚本通过 `context` 对象与C#系统交互：

```csharp
public interface ISkillContext
{
    GameState GameState { get; }                          // 游戏状态
    Character? CurrentCharacter { get; }                  // 执行技能的角色
    Character? OpponentCharacter { get; }                 // 对手角色（可能为null）
    Player CurrentPlayer { get; }                         // 当前玩家
    Player OpponentPlayer { get; }                        // 对手玩家
    
    void LogMessage(string message);                      // 向游戏消息队列添加消息
    int GetRandomInt(int min, int max);                   // 生成随机数
    string GetSkillNarrative(string skillId, string trigger); // 获取技能叙述文本
    
    // 掷骰接口（供Lua调用）
    DiceResult RollDice(string expr);                     // 掷骰表达式（如 "3d6+2"）
    
    // 抽卡接口（供Lua调用）
    Card? DrawOneCardToCurrentPlayer();                   // 为当前玩家抽一张卡牌
}
```

### 4.4 Lua脚本编写规范

**文件位置**：
```
%ApplicationDirectory%/Duel/Extension/Character/skills.lua
```

**基本结构**：

```lua
-- 定义技能函数
function skill_id_entrance(context)
    -- context 是 C# 传入的 ISkillContext 实现
    -- 获取游戏状态
    local gameState = context:GameState()
    local player = context:CurrentPlayer()
    local opponent = context:OpponentPlayer()
    
    -- 输出消息
    context:LogMessage("技能触发！")
    
    -- 掷骰
    local result = context:RollDice("2d6+3")
    context:LogMessage("骰子结果: " .. result.Total)
    
    -- 修改玩家属性
    player:AddDiceToCurrentAttribute(result.Total)
    
    -- 为玩家抽卡
    local card = context:DrawOneCardToCurrentPlayer()
    if card then
        context:LogMessage("抽到卡牌: " .. card.Name)
    end
end

-- 定义另一个技能
function skill_id_field(context)
    -- 在场技能逻辑
    ...
end
```

### 4.5 JSON 配置与 Lua 关联

**JSON 格式（characters.json）**：

```json
{
    "characters": [
        {
            "name": "某角色",
            "power": 10,
            "wealth": 5,
            "fame": 8,
            "perTurnPower": 2,
            "perTurnWealth": 1,
            "perTurnFame": 1,
            "fieldPreference": "Front",
            "faction": "Human",
            "skills": [
                {
                    "skillId": "char_skill_1",
                    "name": "登场技能",
                    "trigger": "Entrance",
                    "luaFunctionName": "char_skill_1_entrance"
                },
                {
                    "skillId": "char_skill_2",
                    "name": "在场技能",
                    "trigger": "Field",
                    "luaFunctionName": "char_skill_2_field"
                }
            ]
        }
    ]
}
```

**对应 Lua 函数**：

```lua
function char_skill_1_entrance(context)
    -- 登场时执行
    context:LogMessage("角色登场了！")
end

function char_skill_2_field(context)
    -- 在场技能每回合可能触发
    local rand = context:GetRandomInt(1, 100)
    if rand <= 50 then
        context:LogMessage("触发在场技能")
        local result = context:RollDice("1d10")
        context:LogMessage("效果: " .. result.Total)
    end
end
```

### 4.6 消息系统

**消息流向**：

```
Lua脚本 (context:LogMessage)
   ↓
SkillExecutionContext.Messages (List<string>)
   ↓
TurnManager 回合方法 (List<string>)
   ↓
MessageProcessor.Reply(combined_messages, msg)
   ↓
发送给玩家（QQ消息、Discord等）
```

**消息精细化处理**：

```csharp
void LogMessage(string message)
{
    var processor = MessageProcessor.Instance;
    if (processor != null) {
        var msg = new Msg(...);
        refined = processor.RefineMsg(message, msg);  // 进行消息处理
    }
    Messages.Add(refined);
}
```

### 4.7 Lua 与游戏状态的交互示例

**示例 1：简单的武力加成技能**

```lua
function warrior_skill(context)
    local player = context:CurrentPlayer()
    local power_boost = context:GetRandomInt(5, 15)
    
    -- 直接修改玩家的TotalPower
    player.TotalPower = player.TotalPower + power_boost
    
    context:LogMessage("战士技能激活，增加" .. power_boost .. "点武力！")
end
```

**示例 2：掷骰决定效果的技能**

```lua
function lucky_skill(context)
    local roll = context:RollDice("1d20")
    local player = context:CurrentPlayer()
    
    if roll.Total >= 15 then
        context:LogMessage("大成功！")
        player.TotalFame = player.TotalFame + 20
    elseif roll.Total >= 10 then
        context:LogMessage("成功！")
        player.TotalFame = player.TotalFame + 10
    else
        context:LogMessage("失败...")
    end
end
```

**示例 3：为玩家抽卡的技能**

```lua
function draw_skill(context)
    local player = context:CurrentPlayer()
    local card = context:DrawOneCardToCurrentPlayer()
    
    if card then
        context:LogMessage("摸到了：" .. card.Name)
        if card.Type == "Character" then
            context:LogMessage("这是一张角色卡")
        else
            context:LogMessage("这是一张特殊卡")
        end
    else
        context:LogMessage("摸不到卡了...")
    end
end
```

### 4.8 特殊卡的立即技能

**特殊卡数据结构**：

```csharp
public class SpecialCard : Card
{
    public SpecialCardType SpecialType { get; set; }  // 卡牌分类
    public string Effect { get; set; }                // 效果描述
    public LuaSkill ImmediateSkill { get; set; }      // 立即执行的技能
}
```

**执行流程**（PlaySpecialCard）**：

```csharp
1. 创建 SkillExecutionContext
2. 执行 card.ImmediateSkill.Execute(context)
3. 收集所有消息到 messages 列表
4. 返回给玩家
```

**示例：特殊卡的 JSON 配置**

```json
{
    "specialCards": [
        {
            "name": "幸运符",
            "specialType": "Blessing",
            "effect": "增加随机属性",
            "faction": "Human",
            "immediateSkill": {
                "skillId": "lucky_charm",
                "name": "幸运符",
                "trigger": "Immediate",
                "luaFunctionName": "lucky_charm_effect"
            }
        }
    ]
}
```

**对应 Lua 代码**：

```lua
function lucky_charm_effect(context)
    local player = context:CurrentPlayer()
    local attrs = {"TotalPower", "TotalWealth", "TotalFame"}
    local chosen = attrs[context:GetRandomInt(1, 3)]
    local boost = context:GetRandomInt(5, 15)
    
    if chosen == "TotalPower" then
        player.TotalPower = player.TotalPower + boost
    elseif chosen == "TotalWealth" then
        player.TotalWealth = player.TotalWealth + boost
    else
        player.TotalFame = player.TotalFame + boost
    end
    
    context:LogMessage("幸运符生效！" .. chosen .. " +" .. boost)
end
```

---

## 五、游戏状态持久化

### 5.1 内存存储

```csharp
private ConcurrentDictionary<string, GameState> gameStates;
// userId -> GameState 映射
```

**特点**：
- 快速访问
- 游戏期间保持状态
- 服务器重启后丢失（需要持久化）

### 5.2 文件持久化

**保存机制**（SaveAllGameStates）**：

```csharp
// 基于最后活跃时间过滤（默认保留7天）
var cutoff = now.AddDays(-gameStateRetentionDays);
foreach (var kvp in gameStates) {
    if (kvp.Value.LastActiveTime >= cutoff) {
        // 转换为快照格式保存
        snapshotDict[kvp.Key] = GameStateSnapshotMapper.ToSnapshot(kvp.Value);
    }
}
// 保存到 GameRuleData 二进制 JSON 文件
GameRuleDataStore.Save(ruleData);
```

**加载机制**（LoadAllGameStates）**：

```csharp
1. 从 GameRuleData 加载所有用户游戏状态快照
2. 使用 GameStateSnapshotMapper 转换为 GameState 对象
3. 填充到内存 ConcurrentDictionary
```

### 5.3 游戏状态快照

用于跨会话持久化的序列化格式：

```csharp
public class GameStateSnapshot
{
    public string Player1Name { get; set; }
    public string Player2Name { get; set; }
    public string Player2Id { get; set; }
    
    public int CurrentTurn { get; set; }
    public string CurrentWeather { get; set; }
    
    public PlayerSnapshot Player1Snapshot { get; set; }
    public PlayerSnapshot Player2Snapshot { get; set; }
    
    public bool IsGameOver { get; set; }
    public int Winner { get; set; }
    
    // ... 其他字段
}
```

---

## 六、权限和限制

### 6.1 duel 指令权限检查

```csharp
bool HasDuelPermission(Msg msg)
```

- 检查用户是否有执行 duel 指令的权限
- 由群组配置决定

### 6.2 每日回合上限

```csharp
bool IsDuelTurnLimited(long userId)
{
    return runtime.DuelTurnsToday >= duelDailyTurnLimit;
}
```

**特点**：
- 每日限制回合数（防止滥用）
- 白名单用户不受限制
- 在StartTurn时计数 `IncrementDuelTurn(userId)`

### 6.3 好感度扣减（娱乐功能）

```csharp
void ApplyDuelPenalty(long userId)
{
    // 扣减用户好感度
}
```

---

## 七、游戏阶段管理

### 7.1 GamePhase 枚举

```csharp
public enum GamePhase
{
    NotStarted,           // 未开始
    WaitingForDecision,   // 等待玩家决策
    Processing,           // 处理中
    Finished              // 已结束
}
```

### 7.2 阶段判定

```csharp
GamePhase GetCurrentGamePhase(string userId)
{
    var gameState = LoadUserGameState(userId);
    
    if (gameState == null || gameState.IsGameOver)
        return GamePhase.Finished;
    
    if (gameState.PendingCard != null || gameState.IsProcessingHandAction)
        return GamePhase.WaitingForDecision;
    
    // ...
}
```

---

## 八、卡牌系统详解

### 8.1 卡牌抽取机制

**混合池抽取**：

```csharp
Card? DrawCardForPlayer(int playerIndex)
{
    Faction faction = playerIndex == 1 ? Faction.Demon : Faction.Human;
    
    // 获取阵营的角色卡和特殊卡
    var characterPool = GameLoader.GetCharacterPoolByFaction(faction);
    var specialCardPool = GameLoader.GetSpecialCardPoolByFaction(faction);
    
    // 合并为混合池
    var combinedPool = new List<Card>();
    combinedPool.AddRange(characterCards);
    combinedPool.AddRange(specialCards);
    
    // 从混合池随机抽取
    return combinedPool[GlobalRandom.Next(combinedPool.Count)];
}
```

**特点**：
- 支持权重系统（DrawWeight）
- 可重复抽取（不移除卡牌）
- 考虑阵营限制

### 8.2 手牌管理

**手牌容量**：

```csharp
public const int MAX_HAND_SIZE = 3;
```

**手牌容量超限处理**：

```csharp
bool AddCardToHand(Card card)
{
    if (HandCards.Count >= MAX_HAND_SIZE) {
        // 移除最早的卡牌
        HandCards.RemoveAt(0);
    }
    HandCards.Add(card);
    return true;
}
```

**手牌显示**：

```csharp
string GetHandInfo()
{
    // 输出格式：1.张三(角色卡)，2.幸运符(特殊卡)
}
```

---

## 九、AI 控制逻辑

### 9.1 AI 手牌处理

```csharp
List<string> ProcessAIHand()
{
    // 1. 随机选择 1-2 张卡牌使用
    int cardsToPlay = Math.Min(HandCards.Count, Random(1-2));
    
    // 2. 对每张卡牌进行处理
    for (int i = 0; i < cardsToPlay; i++) {
        if (card is CharacterCard) {
            HandleCharacterCard(card);  // 根据偏向放置
        } else if (card is SpecialCard) {
            HandleSpecialCard(card);    // 使用特殊卡
        }
    }
}
```

### 9.2 AI 角色卡放置策略

```csharp
List<string> HandleCharacterCard(CharacterCard card)
{
    var characterData = GameLoader.GetCharacterByName(card.Character.Name);
    FieldType preferredField = characterData.FieldPreference;
    
    // 尝试放到偏向的场地，失败则尝试其他场地
    if (!CanPlaceInField(preferredField)) {
        // 寻找可用场地
        preferredField = GetAvailableField();
    }
    
    PlaceCharacterCard(1, card, preferredField);
}
```

---

## 十、API 总结

### 10.1 主要类和接口

| 类/接口 | 作用 |
|---------|------|
| `GameState` | 游戏状态容器 |
| `Player` | 玩家信息（属性、场地、手牌） |
| `Character` | 角色（属性、技能） |
| `CharacterCard` / `SpecialCard` | 卡牌类型 |
| `TurnManager` | 回合控制引擎 |
| `FieldManager` / `Field` | 场地管理 |
| `LuaSkill` | Lua技能定义 |
| `ISkillContext` | 技能执行上下文 |
| `GameLoader` | 游戏数据加载 |
| `AIController` | AI逻辑 |

### 10.2 主要方法

**TurnManager**：
- `InitializeGame()` - 初始化游戏
- `StartTurn()` - 开始回合
- `PlaceCharacterCard()` - 放置角色卡
- `PlaySpecialCard()` - 使用特殊卡
- `UseCardFromHand()` - 处理手牌命令
- `EndTurn()` - 结束回合
- `SettleGame()` - 结算游戏

**GameLoader**：
- `Initialize()` - 初始化（加载数据和Lua脚本）
- `GetCharacterPoolByFaction()` - 获取角色池
- `GetSpecialCardPoolByFaction()` - 获取特殊卡池
- `GetCharacterByName()` - 根据名称获取角色

**Player**：
- `AddCharacterToField()` - 添加角色到场地
- `AddCardToHand()` - 添加卡牌到手牌
- `UseCardFromHand()` - 使用手牌
- `GetHandInfo()` - 获取手牌信息

---

## 十一、扩展指南

### 11.1 添加新角色

1. **在 JSON 中定义角色**（`Duel/Extension/Character/characters.json`）
2. **在 Lua 中实现技能**（`Duel/Extension/Character/skills.lua`）
3. **重启游戏以加载新数据**

### 11.2 添加新特殊卡

1. **在 JSON 中定义特殊卡**（`Duel/Extension/Character/specialCards.json`）
2. **为特殊卡实现立即技能**（在 skills.lua 中）
3. **确保 immediateSkill 字段正确引用 Lua 函数**

### 11.3 修改游戏规则

- **回合上限**：修改 `CheckGameEndCondition()` 中的条件
- **场地上限**：修改 `FieldManager` 的初始化参数
- **属性结算**：修改 `EndTurn()` 和 `SettleGame()` 的逻辑

---

## 十二、故障排除

### 常见问题

**游戏加载失败**：
- 检查 `Duel/Extension/` 目录是否存在
- 验证 JSON 文件格式是否正确
- 查看日志中的 GameLoader 错误信息

**技能不执行**：
- 确认 Lua 函数名与 JSON 中的 `luaFunctionName` 匹配
- 检查技能的 `Trigger` 值是否正确
- 查看消息中是否有 "[技能系统]" 前缀的错误信息

**属性异常**：
- 检查角色的 PerTurn* 值是否被正确累计
- 验证 PlaceCharacterCard 时的基础属性加成
- 查看 AppendCharacterStatsToFields 的逻辑

---

## 十三、总结

**.duel 小游戏**是一个完整的回合制卡牌对战系统，具有以下特点：

1. **灵活的回合机制**：支持多回合对战，每回合有明确的阶段划分
2. **丰富的卡牌系统**：支持角色卡和特殊卡，可通过 Lua 脚本扩展效果
3. **智能的 AI 系统**：AI 自动处理手牌，拥有决策逻辑
4. **高度可扩展**：通过 JSON 配置和 Lua 脚本，支持无限添加新内容
5. **完善的持久化**：支持游戏状态在内存和文件中的保存与恢复
6. **细致的消息系统**：所有游戏事件都会转化为文本消息反馈给玩家

通过理解本文档的内容，开发者可以有效地维护、扩展和调试这个游戏系统。
