using System.Collections.Generic;

namespace MDiceV2.Core.GameBattle
{
    /// <summary>
    /// 游戏状态类，用于存储游戏的完整状态
    /// </summary>
    public class GameState
    {
        // 对战双方
        public Player Player1 { get; set; } = null!; // 机器人（魔王军）
        public Player Player2 { get; set; } = null!; // 人类玩家（人类方）

        // 玩家ID绑定（暂时Player1为机器人，无需绑定）
        public string Player2Id { get; set; } = null!; // Player2的用户ID

        // 当前回合数
        public int CurrentTurn { get; set; } = 1;

        // 当前天气
        public string CurrentWeather { get; set; } = "Clear"; // 默认晴天

        // 当前场地效果
        public FieldEffect? CurrentFieldEffect { get; set; } // 场地效果为单一存在的

        // 卡牌牌堆
        public List<Card> CardDeck { get; set; } = new List<Card>();

        // 等待玩家决策的卡牌
        public Card? PendingCard { get; set; }

        // 游戏是否结束
        public bool IsGameOver { get; set; } = false;

        // 胜利者
        public int Winner { get; set; } // 1 for Player1, 2 for Player2, 0 for ongoing

        // 手牌系统相关属性
        public const int MAX_HAND_SIZE = 3; // 最大手牌数量
        public bool IsProcessingHandAction { get; set; } = false; // 是否正在处理手牌操作

        /// <summary>
        /// 最近一次活跃时间（例如用户通过 .duel 进入/继续游戏的时间）
        /// 用于在保存时判断长时间未活动的游戏是否需要被清理。
        /// </summary>
        public DateTime LastActiveTime { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 根据用户ID获取玩家
        /// </summary>
        public Player? GetPlayerByUserId(string userId)
        {
            if (Player2Id == userId) return Player2;
            return null; // Player1为机器人
        }

        /// <summary>
        /// 根据用户ID获取玩家索引 (1或2)
        /// </summary>
        public int GetPlayerIndexByUserId(string userId)
        {
            if (Player2Id == userId) return 2;
            return 0;
        }

        /// <summary>
        /// 尝试设置新的场地效果
        /// 低强度等级的效果可以覆盖高强度等级的效果
        /// </summary>
        public bool TrySetFieldEffect(string tag, int intensity, int duration, int currentTurn)
        {
            // 如果当前没有效果，直接设置
            if (CurrentFieldEffect == null)
            {
                CurrentFieldEffect = new FieldEffect
                {
                    Tag = tag,
                    Intensity = intensity,
                    StartTurn = currentTurn,
                    ExpiryTurn = currentTurn + duration
                };
                return true;
            }

            // 如果新效果强度更低或相同，覆盖旧效果
            if (intensity <= CurrentFieldEffect.Intensity)
            {
                CurrentFieldEffect = new FieldEffect
                {
                    Tag = tag,
                    Intensity = intensity,
                    StartTurn = currentTurn,
                    ExpiryTurn = currentTurn + duration
                };
                return true;
            }

            // 新效果强度更高，不覆盖
            return false;
        }

        /// <summary>
        /// 检查并清除过期的场地效果
        /// </summary>
        public bool CheckAndRemoveExpiredFieldEffect(int currentTurn)
        {
            if (CurrentFieldEffect != null && currentTurn >= CurrentFieldEffect.ExpiryTurn)
            {
                CurrentFieldEffect = null;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 移除当前场地效果
        /// </summary>
        public void RemoveCurrentFieldEffect()
        {
            CurrentFieldEffect = null;
        }
    }

    /// <summary>
    /// 玩家类
    /// </summary>
    public class Player
    {
        public string Name { get; set; }

        // 场地三维属性累计
        public int TotalPower { get; set; } = 0; // 武力
        public int TotalWealth { get; set; } = 0; // 财力
        public int TotalFame { get; set; } = 0; // 名声

        // 角色三维属性累计（每回合增加）
        public int CharacterPower { get; set; } = 0; // 武力
        public int CharacterWealth { get; set; } = 0; // 财力
        public int CharacterFame { get; set; } = 0; // 名声

        // 场地管理器
        public FieldManager FieldManager { get; set; }

        // 在场角色
        public List<Character> FieldCharacters { get; set; } = new List<Character>();

        // 手牌系统
        public List<Card> HandCards { get; set; } = new List<Card>(); // 玩家手牌

        public Player()
        {
            // 默认构造函数用于JSON反序列化
            Name = "";
            FieldManager = new FieldManager(6, 6, 6, 18); // 每个场地6个位置，总共最多18个角色
        }

        public Player(string name, int frontLimit, int middleLimit, int backLimit)
        {
            Name = name;
            FieldManager = new FieldManager(frontLimit, middleLimit, backLimit, frontLimit + middleLimit + backLimit);
        }

        /// <summary>
        /// 获取当前三维属性的总和
        /// </summary>
        public int GetTotalPower() => TotalPower;
        public int GetTotalWealth() => TotalWealth;
        public int GetTotalFame() => TotalFame;

        /// <summary>
        /// 添加角色到场地
        /// </summary>
        public bool AddCharacterToField(Character character, FieldType fieldType)
        {
            if (FieldManager.AddCharacterToField(character, fieldType))
            {
                FieldCharacters.Add(character);
                
                // 放置时：将角色的三维属性都添加到玩家的Total*，对应场地的属性翻倍
                // 并记录每回合长期回复到Character*字段
                switch (fieldType)
                {
                    case FieldType.Front:
                        // Front 场地：Power双倍，Wealth和Fame保持原值
                        TotalPower += character.Power * 2;         // Power翻倍
                        TotalWealth += character.Wealth;           // Wealth保持
                        TotalFame += character.Fame;               // Fame保持
                        CharacterPower += character.PerTurnPower;   // 记录每回合武力贡献
                        CharacterWealth += character.PerTurnWealth; // 记录每回合财力贡献
                        CharacterFame += character.PerTurnFame;     // 记录每回合名声贡献
                        break;
                    case FieldType.Middle:
                        // Middle 场地：Wealth双倍，Power和Fame保持原值
                        TotalPower += character.Power;             // Power保持
                        TotalWealth += character.Wealth * 2;       // Wealth翻倍
                        TotalFame += character.Fame;               // Fame保持
                        CharacterPower += character.PerTurnPower;   // 记录每回合武力贡献
                        CharacterWealth += character.PerTurnWealth; // 记录每回合财力贡献
                        CharacterFame += character.PerTurnFame;     // 记录每回合名声贡献
                        break;
                    case FieldType.Back:
                        // Back 场地：Fame双倍，Power和Wealth保持原值
                        TotalPower += character.Power;             // Power保持
                        TotalWealth += character.Wealth;           // Wealth保持
                        TotalFame += character.Fame * 2;           // Fame翻倍
                        CharacterPower += character.PerTurnPower;   // 记录每回合武力贡献
                        CharacterWealth += character.PerTurnWealth; // 记录每回合财力贡献
                        CharacterFame += character.PerTurnFame;     // 记录每回合名声贡献
                        break;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// 从场地移除角色
        /// </summary>
        public bool RemoveCharacterFromField(Character character, FieldType fieldType, ISkillContext? skillContext = null)
        {
            if (FieldManager.RemoveCharacterFromField(character, fieldType))
            {
                FieldCharacters.Remove(character);
                // 移除时：只移除角色，不反向扣除属性
                // 基础属性加成在移除后不再计入下的回合
                // 每回合贡献会在下一回合的AppendCharacterStatsToFields自动调整（因为角色已不在场地）
                
                // 触发角色的OnRemoved委托（亡语技能）
                if (character.OnRemoved != null && skillContext != null)
                {
                    character.OnRemoved(skillContext);
                }
                
                return true;
            }
            return false;
        }

        public string CurrentState()
        {
            return $"{Name} P：{GetTotalPower()}(+{CharacterPower})，W：{GetTotalWealth()}(+{CharacterWealth})，F：{GetTotalFame()}(+{CharacterFame})";
        }

        /// <summary>
        /// 添加卡牌到手牌，如果手牌已满则替换最早的一张
        /// </summary>
        public bool AddCardToHand(Card card)
        {
            if (HandCards.Count >= GameState.MAX_HAND_SIZE)
            {
                // 手牌已满，移除最早的一张卡牌
                var oldestCard = HandCards[0];
                HandCards.RemoveAt(0);
                // Console.WriteLine($"{Name} 的手牌已满，替换最早的手牌：{oldestCard.Name} -> {card.Name}");
            }
            
            HandCards.Add(card);
            // Console.WriteLine($"{Name} 添加手牌：{card.Name}，当前手牌数：{HandCards.Count}");
            return true;
        }

        /// <summary>
        /// 使用手牌中的卡牌
        /// </summary>
        public Card UseCardFromHand(int cardIndex)
        {
            if (cardIndex < 0 || cardIndex >= HandCards.Count)
            {
                return null!;
            }

            var card = HandCards[cardIndex];
            HandCards.RemoveAt(cardIndex);
            // Console.WriteLine($"{Name} 使用手牌：{card.Name}，剩余手牌数：{HandCards.Count}");
            return card;
        }

        /// <summary>
        /// 跳过当前回合（不使用手牌）
        /// </summary>
        public bool SkipTurn()
        {
            // Console.WriteLine($"{Name} 跳过当前回合");
            return true;
        }

        /// <summary>
        /// 获取手牌信息显示
        /// </summary>
        public string GetHandInfo()
        {
            if (HandCards.Count == 0)
            {
                return $"{Name} 的手牌：空";
            }

            var cardInfos = new List<string>();
            for (int i = 0; i < HandCards.Count; i++)
            {
                var card = HandCards[i];
                string cardInfo;
                if (card is CharacterCard characterCard)
                {
                    cardInfo = $"{i + 1}.{characterCard.Name}(角色卡)";
                }
                else if (card is SpecialCard specialCard)
                {
                    cardInfo = $"{i + 1}.{specialCard.Name}(特殊卡)";
                }
                else
                {
                    cardInfo = $"{i + 1}.{card.Name}";
                }
                cardInfos.Add(cardInfo);
            }

            return $"{Name} 的手牌：{string.Join("，", cardInfos)}";
        }
    }

    /// <summary>
    /// 场地效果类
    /// </summary>
    public class FieldEffect
    {
        /// <summary>
        /// 效果标签（如 "Halloween", "Rain" 等）
        /// </summary>
        public string Tag { get; set; } = "";

        /// <summary>
        /// 强度等级（低强度可以覆盖高强度）
        /// 例如：1 = 低, 2 = 中, 3 = 高
        /// </summary>
        public int Intensity { get; set; } = 3;

        /// <summary>
        /// 效果开始的回合数
        /// </summary>
        public int StartTurn { get; set; }

        /// <summary>
        /// 效果过期的回合数（包含此回合则视为过期）
        /// </summary>
        public int ExpiryTurn { get; set; }
    }

    /// <summary>
    /// 游戏状态工具类
    /// </summary>
    public static class GameStateUtils
    {
        /// <summary>
        /// 获取当前游戏状态
        /// </summary>
        public static string GetGameStatus(GameState gameState)
        {
            return $"[当前游戏状态]\n" +
                   $"{gameState.Player1.CurrentState()}\n" +
                   $"{gameState.Player2.CurrentState()}";
        }
    }
}