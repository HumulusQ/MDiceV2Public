using System;
using System.Collections.Generic;
using System.Linq;
using MDiceV2.Models;
using MDiceV2.Core.GameBattle;

namespace MDiceV2.Core.GameBattle
{
    /// <summary>
    /// 回合管理器，负责管理游戏回合的推进和逻辑
    /// </summary>
    public class TurnManager
    {
        private readonly GameState _gameState;
        private readonly AIController _aiController;

        public TurnManager(GameState gameState)
        {
            _gameState = gameState;
            _aiController = new AIController(gameState, this);
        }

        /// <summary>
        /// 开始新游戏，初始化游戏状态
        /// </summary>
        public void InitializeGame()
        {
            // 清空牌堆，因为我们使用重复抽取的方式
            _gameState.CardDeck.Clear();
            // 不需要初始化牌堆，抽卡时直接从池中随机抽取
        }

        /// <summary>
        /// 抽卡（从角色池和特殊卡池中随机抽取，可重复）
        /// </summary>
        public Card DrawCard()
        {
            // 50% 概率抽角色卡，50% 概率抽特殊卡
            bool drawCharacter = GlobalRandom.Next(2) == 0;

            if (drawCharacter)
            {
                // 随机抽取角色卡（默认为人类阵营）
                return DrawCharacterCard(Faction.Human);
            }
            else
            {
                // 随机抽取特殊卡（默认为人类阵营）
                return DrawSpecialCard(Faction.Human);
            }
        }

        /// <summary>
        /// 抽取角色卡（根据阵营混合权重）
        /// </summary>
        private Card? DrawCharacterCard(Faction faction)
        {
            var pool = GameLoader.GetCharacterPoolByFaction(faction);
            if (pool.Count == 0) return null;

            var character = pool[GlobalRandom.Next(pool.Count)];
            return new CharacterCard
            {
                Name = character.Name,
                Character = character
            };
        }

        /// <summary>
        /// 抽取特殊卡（混合权重）
        /// </summary>
        private Card? DrawSpecialCard(Faction faction)
        {
            var pool = GameLoader.GetSpecialCardPoolByFaction(faction);
            if (pool.Count == 0) return null;

            var specialCard = pool[GlobalRandom.Next(pool.Count)];
            return new SpecialCard
            {
                Name = specialCard.Name,
                SpecialType = specialCard.SpecialType,
                Effect = specialCard.Effect,
                ImmediateSkill = specialCard.ImmediateSkill
            };
        }

        /// <summary>
        /// 为指定玩家抽卡（考虑阵营限制）
        /// </summary>
        private Card? DrawCardForPlayer(int playerIndex)
        {
            Faction faction = playerIndex == 1 ? Faction.Demon : Faction.Human;

            // 获取角色卡和特殊卡的混合池
            var characterPool = GameLoader.GetCharacterPoolByFaction(faction);
            var specialCardPool = GameLoader.GetSpecialCardPoolByFaction(faction);

            var combinedPool = new List<Card>();

            // 将角色卡和特殊卡加入混合池
            combinedPool.AddRange(characterPool.Select(c => new CharacterCard
            {
                Name = c.Name,
                Character = c
            }));

            combinedPool.AddRange(specialCardPool.Select(s => new SpecialCard
            {
                Name = s.Name,
                SpecialType = s.SpecialType,
                Effect = s.Effect,
                ImmediateSkill = s.ImmediateSkill
            }));

            if (combinedPool.Count == 0) return null;

            // 从混合池中随机抽取一张卡
            return combinedPool[GlobalRandom.Next(combinedPool.Count)];
        }

        /// <summary>
        /// 获取玩家的场地状态信息（仅显示角色名称）
        /// </summary>
        private List<string> GetFieldStatusInfo(Player player)
        {
            var fieldInfo = new List<string>();
            fieldInfo.Add($"{player.Name}：");

            // 前场
            var frontCharacters = player.FieldManager.FrontField.Characters;
            string frontDisplay = frontCharacters.Count > 0 
                ? string.Join(", ", frontCharacters.Select(c => c.Name))
                : "空";
            fieldInfo.Add($"  前场: {frontDisplay}");

            // 中场
            var middleCharacters = player.FieldManager.MiddleField.Characters;
            string middleDisplay = middleCharacters.Count > 0
                ? string.Join(", ", middleCharacters.Select(c => c.Name))
                : "空";
            fieldInfo.Add($"  中场: {middleDisplay}");

            // 后场
            var backCharacters = player.FieldManager.BackField.Characters;
            string backDisplay = backCharacters.Count > 0
                ? string.Join(", ", backCharacters.Select(c => c.Name))
                : "空";
            fieldInfo.Add($"  后场: {backDisplay}");

            return fieldInfo;
        }

        /// <summary>
        /// 回合开始，为双方玩家掷d3并根据结果抽卡
        /// </summary>
        public List<string> StartTurn()
        {
            var messages = new List<string>();
            messages.Add($"=====第 {_gameState.CurrentTurn} 回合=====");
            
            // 添加双方场地状态
            messages.AddRange(GetFieldStatusInfo(_gameState.Player1));
            messages.AddRange(GetFieldStatusInfo(_gameState.Player2));
            
            // 为双方玩家各掷d3并根据结果抽卡
            int player1DrawCount = GlobalRandom.Next(1, 4); // d3: 滚动结果 1-3
            int player2DrawCount = GlobalRandom.Next(1, 4); // d3: 滚动结果 1-3
            
            // 诊断日志：记录骰子值和随机数生成器统计信息
            var stats = GlobalRandom.GetStatistics();
            System.Diagnostics.Debug.WriteLine($"[RNG Diagnostic] Turn {_gameState.CurrentTurn}: Player1 rolled {player1DrawCount}, Player2 rolled {player2DrawCount} | Stats: {stats}");
            
            // 处理玩家1的抽卡
            messages.Add($">{_gameState.Player1.Name} 掷出 d3 = {player1DrawCount}，抽取 {player1DrawCount} 张卡牌");
            var player1DrawnCards = new List<string>();
            for (int i = 0; i < player1DrawCount; i++)
            {
                var cardName = DrawOneCardForHandReturningName(_gameState.Player1);
                if (cardName != null)
                {
                    player1DrawnCards.Add(cardName);
                }
            }
            if (player1DrawnCards.Count > 0)
            {
                messages.Add($">{_gameState.Player1.Name} 抽到手牌：{string.Join("、", player1DrawnCards)}");
            }
            
            // 处理玩家2的抽卡
            messages.Add($">{_gameState.Player2.Name} 掷出 d3 = {player2DrawCount}，抽取 {player2DrawCount} 张卡牌");
            var player2DrawnCards = new List<string>();
            for (int i = 0; i < player2DrawCount; i++)
            {
                var cardName = DrawOneCardForHandReturningName(_gameState.Player2);
                if (cardName != null)
                {
                    player2DrawnCards.Add(cardName);
                }
            }
            if (player2DrawnCards.Count > 0)
            {
                messages.Add($">{_gameState.Player2.Name} 抽到手牌：{string.Join("、", player2DrawnCards)}");
            }
            
            // AI自动处理其回合
            var aiMessages = _aiController.ExecuteTurn(null); // AI会处理自己的手牙
            messages.AddRange(aiMessages);
            
            // 人类玩家进入手牙操作阶段
            if (_gameState.Player2.HandCards.Count > 0)
            {
                _gameState.IsProcessingHandAction = true;
                _gameState.PendingCard = null; // 清除之前的pendingCard
                
                // 显示玩家手牌信息
                var handInfo = _gameState.Player2.GetHandInfo();
                messages.Add(handInfo);
                messages.Add("请选择要使用的手牌");
            }
            else
            {
                // 没有手牌，直接跳过
                messages.Add($"{_gameState.Player2.Name} 没有手牌，跳过当前回合。");
                var endTurnMessages = EndTurn();
                messages.AddRange(endTurnMessages);
                
                // 如果游戏没有结束，开始新回合
                if (!_gameState.IsGameOver)
                {
                    var startTurnMessages = StartTurn();
                    messages.AddRange(startTurnMessages);
                }
            }

            return messages;
        }

        /// <summary>
        /// 为玩家抽一张手牌，返回卡牌名称
        /// </summary>
        private string? DrawOneCardForHandReturningName(Player player)
        {
            var card = DrawCardForPlayer(player == _gameState.Player1 ? 1 : 2);
            if (card != null)
            {
                player.AddCardToHand(card);
                
                if (card is CharacterCard characterCard)
                {
                    // 只返回角色名称（简化格式）
                    return characterCard.Character.Name;
                }
                else
                {
                    return card.Name;
                }
            }
            return null;
        }

        /// <summary>
        /// 为玩家抽一张手牌
        /// </summary>
        private void DrawOneCardForHand(Player player, List<string> messages)
        {
            var card = DrawCardForPlayer(player == _gameState.Player1 ? 1 : 2);
            if (card != null)
            {
                player.AddCardToHand(card);
                
                if (card is CharacterCard characterCard)
                {
                    messages.Add($">{player.Name} 抽到手牌：{FormatCharacterInfo(characterCard.Character)}");
                }
                else
                {
                    messages.Add($">{player.Name} 抽到手牌：{card.Name}");
                }
            }
            else
            {
                messages.Add($"{player.Name} 没有抽到手牌。");
            }
        }

        /// <summary>
        /// 处理玩家放置角色卡并执行回合结束
        /// </summary>
        public List<string> PlaceCharacterCardAndEndTurn(int playerIndex, CharacterCard card, FieldType fieldType)
        {
            var messages = new List<string>();

            // 放置角色卡
            var placeMessages = PlaceCharacterCard(playerIndex, card, fieldType);
            messages.AddRange(placeMessages);

            // 清除等待的卡牧
            _gameState.PendingCard = null;

            // 执行回合结束阶段
            var endTurnMessages = EndTurn();
            messages.AddRange(endTurnMessages);

            // 如果游戏没有结束，开始新回合
            if (!_gameState.IsGameOver)
            {
                var startTurnMessages = StartTurn();
                messages.AddRange(startTurnMessages);
            }

            return messages;
        }

        /// <summary>
        /// 处理玩家放置角色卡（基础方法）
        /// </summary>
        public List<string> PlaceCharacterCard(int playerIndex, CharacterCard card, FieldType fieldType)
        {
            var messages = new List<string>();
            Player player = playerIndex == 1 ? _gameState.Player1 : _gameState.Player2;

            if (player.AddCharacterToField(card.Character, fieldType))
            {
                messages.Add($"{player.Name} 将 {card.Character.Name} 放置到 {GetFieldName(fieldType)}");

                // 检查角色是否有登场技能
                var entranceSkills = card.Character.GetSkillsByTrigger(SkillTrigger.Entrance).ToList();
                var totalEntranceSkills = entranceSkills.Count + card.Character.Skills.Count(s => s != null); // 包括Lua技能和传统委托

                // 触发登场技能（优先使用Lua技能）
                foreach (var luaSkill in entranceSkills)
                {
                    var context = new SkillExecutionContext(_gameState, card.Character, player, player == _gameState.Player1 ? _gameState.Player2 : _gameState.Player1, () => DrawCardForPlayer(player == _gameState.Player1 ? 1 : 2), fieldType);
                    messages.Add($"[技能系统] 触发登场技能: {luaSkill.Name}");
                    luaSkill.Execute(context);
                    // 添加技能执行产生的消息
                    messages.AddRange(context.Messages);
                }
            }
            else
            {
                messages.Add($"无法将角色放置到 {GetFieldName(fieldType)}，已达到上限！");
            }

            return messages;
        }

        /// <summary>
        /// 处理特殊卡并执行回合结束
        /// </summary>
        public List<string> PlaySpecialCardAndEndTurn(int playerIndex, SpecialCard card, bool useCard)
        {
            var messages = new List<string>();

            if (useCard)
            {
                var playMessages = PlaySpecialCard(playerIndex, card);
                messages.AddRange(playMessages);
            }
            else
            {
                Player player = playerIndex == 1 ? _gameState.Player1 : _gameState.Player2;
                messages.Add($"{player.Name} 选择弃置特殊卡：{card.Name}");
            }

            // 清除等待的卡牌
            _gameState.PendingCard = null;

            // 执行回合结束阶段
            var endTurnMessages = EndTurn();
            messages.AddRange(endTurnMessages);

            // 如果游戏没有结束，开始新回合
            if (!_gameState.IsGameOver)
            {
                var startTurnMessages = StartTurn();
                messages.AddRange(startTurnMessages);
            }

            return messages;
        }

        /// <summary>
        /// 处理特殊卡（基础方法）
        /// </summary>
        public List<string> PlaySpecialCard(int playerIndex, SpecialCard card)
        {
            var messages = new List<string>();
            Player player = playerIndex == 1 ? _gameState.Player1 : _gameState.Player2;
            Player opponent = playerIndex == 1 ? _gameState.Player2 : _gameState.Player1;

            messages.Add($"{player.Name} 打出特殊卡：{card.Name}");

            // 执行立即技能（所有特殊卡必须有技能）
            if (card.ImmediateSkill == null)
            {
                messages.Add($"[错误] 特殊卡 '{card.Name}' 没有配置技能，无法执行。");
                return messages;
            }

            var context = new SkillExecutionContext(_gameState, null, player, opponent, () => DrawCardForPlayer(player == _gameState.Player1 ? 1 : 2));
            messages.Add($"[技能系统] 触发特殊卡技能: {card.ImmediateSkill.Name}");
            card.ImmediateSkill.Execute(context);
            messages.AddRange(context.Messages);

            return messages;
        }

        /// <summary>
        /// 处理手牌使用（跳过回合）
        /// </summary>
        public List<string> SkipTurnWithHand()
        {
            var messages = new List<string>();
            var player = _gameState.Player2; // 人类玩家

            messages.Add($"{player.Name} 选择跳过当前回合，保留所有手牌。");

            // 清除手牌操作状态
            _gameState.IsProcessingHandAction = false;

            // 执行回合结束阶段
            var endTurnMessages = EndTurn();
            messages.AddRange(endTurnMessages);

            // 如果游戏没有结束，开始新回合
            if (!_gameState.IsGameOver)
            {
                var startTurnMessages = StartTurn();
                messages.AddRange(startTurnMessages);
            }

            return messages;
        }

        /// <summary>
        /// 处理手牌使用命令（格式：手牌编号.操作）
        /// </summary>
        /// <summary>
        /// 处理单个出卡命令（内部方法，不移除卡牌）
        /// </summary>
        private (bool isValid, Card? card, string? cardName, string? action) ParseCardCommand(string command)
        {
            var player = _gameState.Player2;

            // 解析命令：格式为 "编号.操作"
            string[] parts = command.Split('.');
            if (parts.Length < 2)
            {
                return (false, null, null, null);
            }

            if (!int.TryParse(parts[0], out int cardIndex) || cardIndex < 1 || cardIndex > player.HandCards.Count)
            {
                return (false, null, null, null);
            }

            cardIndex--;
            var card = player.HandCards[cardIndex];
            string cardName = card.Name;
            string action = parts[1].ToLower();

            return (true, card, cardName, action);
        }

        /// <summary>
        /// 验证单个出卡命令的有效性
        /// </summary>
        private (bool isValid, string? errorMsg) ValidateCardCommand(Card card, string action)
        {
            if (card is CharacterCard)
            {
                // 角色卡：操作应为场地编号 1,2,3
                if (!int.TryParse(action, out int fieldNumber) || fieldNumber < 1 || fieldNumber > 3)
                {
                    return (false, "角色卡操作错误！请使用 1（前场）、2（中场）或 3（后场）");
                }
            }
            else if (card is SpecialCard)
            {
                // 特殊卡：操作应为 y/n
                if (action != "y" && action != "n")
                {
                    return (false, "特殊卡操作错误！请使用 y（使用）或 n（不使用）");
                }
            }

            return (true, null);
        }

        /// <summary>
        /// 根据卡牌名称查找并返回卡牌（支持同名卡牌）
        /// 返回第一个匹配的卡牌和其索引
        /// </summary>
        private (Card? card, int index) FindCardByName(string cardName)
        {
            var player = _gameState.Player2;
            for (int i = 0; i < player.HandCards.Count; i++)
            {
                if (player.HandCards[i].Name == cardName)
                {
                    return (player.HandCards[i], i);
                }
            }
            return (null, -1);
        }

        /// <summary>
        /// 执行单个出卡操作（根据卡牌名称）
        /// 注意：此方法不移除卡牌，卡牌移除在批量处理完成后统一进行
        /// </summary>
        private List<string> ExecuteCardPlay(string cardName, string action, out int cardIndexToRemove)
        {
            var messages = new List<string>();
            var player = _gameState.Player2;
            cardIndexToRemove = -1;

            // 根据卡牌名称查找卡牌
            var (card, cardIndex) = FindCardByName(cardName);
            if (card == null || cardIndex < 0)
            {
                messages.Add($"无法找到卡牌 '{cardName}' 在你的手牌中");
                return messages;
            }

            cardIndexToRemove = cardIndex;

            if (card is CharacterCard characterCard)
            {
                if (!int.TryParse(action, out int fieldNumber))
                {
                    messages.Add("角色卡操作错误！");
                    return messages;
                }

                FieldType fieldType = fieldNumber switch
                {
                    1 => FieldType.Front,
                    2 => FieldType.Middle,
                    3 => FieldType.Back,
                    _ => FieldType.Front
                };

                var placeMessages = PlaceCharacterCard(2, characterCard, fieldType);
                messages.AddRange(placeMessages);
            }
            else if (card is SpecialCard specialCard)
            {
                bool useCard = action == "y";

                List<string> useMessages;
                if (useCard)
                {
                    useMessages = PlaySpecialCard(2, specialCard);
                }
                else
                {
                    useMessages = new List<string> { $"{player.Name} 选择弃置特殊卡：{specialCard.Name}" };
                }
                messages.AddRange(useMessages);
            }

            return messages;
        }

        public List<string> UseCardFromHand(string command)
        {
            var messages = new List<string>();
            var player = _gameState.Player2;

            // 按空格分隔，支持批量出卡
            string[] commands = command.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (commands.Length == 0)
            {
                // 格式错误时返回特殊标记消息，由调用方处理
                messages.Add("[CARD_FORMAT_ERROR]");
                return messages;
            }

            // 【第一步】预处理和验证所有命令，同时记录卡牌名称
            var cardOperations = new List<(int originalIndex, Card card, string cardName, string action)>();

            foreach (var cmd in commands)
            {
                var (isValid, card, cardName, action) = ParseCardCommand(cmd);
                if (!isValid || card == null || cardName == null || action == null)
                {
                    // 格式错误时返回特殊标记消息，由调用方处理
                    messages.Add("[CARD_FORMAT_ERROR]");
                    return messages;
                }

                // 验证操作有效性
                var (validAction, errorMsg) = ValidateCardCommand(card, action);
                if (!validAction)
                {
                    // 命令无效时返回特殊标记消息，由调用方处理
                    messages.Add("[CARD_FORMAT_ERROR]");
                    return messages;
                }

                cardOperations.Add((card.GetHashCode(), card, cardName, action));
            }

            // 【第二步】执行所有出卡操作，使用卡牌名称而非索引，并收集待移除的卡牌索引
            // 注意：收集索引后再统一移除，避免集合在枚举期间被修改的错误
            var indicesToRemove = new List<int>();

            foreach (var (_, originalCard, cardName, action) in cardOperations)
            {
                var playMessages = ExecuteCardPlay(cardName, action, out int indexToRemove);
                messages.AddRange(playMessages);
                
                // 只记录成功执行的卡牌索引
                if (indexToRemove >= 0)
                {
                    indicesToRemove.Add(indexToRemove);
                }
            }

            // 【第二步B】按降序移除卡牌，避免移除导致的索引变化
            // 必须按降序移除，否则移除前面的卡牌会导致后续索引失效
            indicesToRemove.Sort((a, b) => b.CompareTo(a));
            foreach (var index in indicesToRemove)
            {
                if (index >= 0 && index < player.HandCards.Count)
                {
                    player.HandCards.RemoveAt(index);
                }
            }

            // 【第三步】处理出卡后的状态
            if (player.HandCards.Count > 0)
            {
                // 还有手牌，继续处理
                var handInfo = player.GetHandInfo();
                messages.Add(handInfo);
                messages.Add("请继续选择要使用的手牌，或回复 0 跳过剩余回合。");
                _gameState.IsProcessingHandAction = true;
            }
            else
            {
                // 没有更多手牌，进入回合结束
                _gameState.IsProcessingHandAction = false;

                var endTurnMessages = EndTurn();
                messages.AddRange(endTurnMessages);

                // 如果游戏没有结束，开始新回合
                if (!_gameState.IsGameOver)
                {
                    var startTurnMessages = StartTurn();
                    messages.AddRange(startTurnMessages);
                }
            }

            return messages;
        }

        /// <summary>
        /// 回合结束，追加角色三维并结算技能
        /// </summary>
        public List<string> EndTurn()
        {
            var messages = new List<string>();

            // 检查并移除过期的场地效果
            if (_gameState.CheckAndRemoveExpiredFieldEffect(_gameState.CurrentTurn))
            {
                var currentEffect = _gameState.CurrentFieldEffect;
                if (currentEffect != null)
                {
                    messages.Add($"场地效果「{currentEffect.Tag}」已过期，消失了。");
                }
            }

            // 为双方场地三维追加角色三维累计
            AppendCharacterStatsToFields(_gameState.Player1, messages);
            AppendCharacterStatsToFields(_gameState.Player2, messages);

            // 结算技能效果
            var skillMessages = ProcessSkills();
            messages.AddRange(skillMessages);

            // 统一展示双方状态
            messages.Add(GameStateUtils.GetGameStatus(_gameState));

            // 检查立即败北条件（任一属性 <= -10）
            bool player1Defeated = _gameState.Player1.TotalPower <= -10 || 
                                   _gameState.Player1.TotalWealth <= -10 || 
                                   _gameState.Player1.TotalFame <= -10;
            
            bool player2Defeated = _gameState.Player2.TotalPower <= -10 || 
                                   _gameState.Player2.TotalWealth <= -10 || 
                                   _gameState.Player2.TotalFame <= -10;

            if (player1Defeated || player2Defeated)
            {
                // 触发立即败北判定
                _gameState.IsGameOver = true;
                messages.Add("游戏结束！开始结算...");
                var settlementMessages = SettleGame();
                messages.AddRange(settlementMessages);
            }
            // 检查回合上限条件（达到第20回合）
            else if (CheckGameEndCondition())
            {
                _gameState.IsGameOver = true;
                messages.Add("游戏结束！开始结算...");
                var settlementMessages = SettleGame();
                messages.AddRange(settlementMessages);
            }
            else
            {
                // 游戏继续，进入下一回合
                _gameState.CurrentTurn++;
            }

            return messages;
        }

        /// <summary>
        /// 为场地三维追加角色三维累计
        /// </summary>
        private void AppendCharacterStatsToFields(Player player, List<string> messages)
        {
            // 前场（武力） - 使用每回合贡献/恢复值
            int frontPower = player.FieldManager.FrontField.Characters.Sum(c => c.PerTurnPower);
            player.TotalPower += frontPower;

            // 中场（财力）
            int middleWealth = player.FieldManager.MiddleField.Characters.Sum(c => c.PerTurnWealth);
            player.TotalWealth += middleWealth;

            // 后场（名声）
            int backFame = player.FieldManager.BackField.Characters.Sum(c => c.PerTurnFame);
            player.TotalFame += backFame;
        }

        /// <summary>
        /// 结算技能效果
        /// </summary>
        private List<string> ProcessSkills()
        {
            var messages = new List<string>();

            // 处理在场技能
            var fieldSkillMessages = ProcessFieldSkills();
            messages.AddRange(fieldSkillMessages);

            // 处理连携技能
            var chainSkillMessages = ProcessChainSkills();
            messages.AddRange(chainSkillMessages);

            // 处理事件技能
            var eventSkillMessages = ProcessEventSkills();
            messages.AddRange(eventSkillMessages);

            // 处理回合结束技能
            var turnEndSkillMessages = ProcessTurnEndSkills();
            messages.AddRange(turnEndSkillMessages);

            return messages;
        }

        /// <summary>
        /// 处理在场技能（概率触发）
        /// </summary>
        private List<string> ProcessFieldSkills()
        {
            var messages = new List<string>();

            foreach (var player in new[] { _gameState.Player1, _gameState.Player2 })
            {
                foreach (var character in player.FieldCharacters.ToList())
                {
                    // 执行Lua在场技能
                    var fieldSkills = character.GetSkillsByTrigger(SkillTrigger.Field);
                    foreach (var luaSkill in fieldSkills)
                    {
                        var context = new SkillExecutionContext(_gameState, character, player, player == _gameState.Player1 ? _gameState.Player2 : _gameState.Player1, () => DrawCardForPlayer(player == _gameState.Player1 ? 1 : 2));
                        luaSkill.Execute(context);
                        messages.AddRange(context.Messages);
                    }

                    // 执行传统技能委托
                    foreach (var skillAction in character.Skills)
                    {
                        var context = new SkillExecutionContext(_gameState, character, player, player == _gameState.Player1 ? _gameState.Player2 : _gameState.Player1, () => DrawCardForPlayer(player == _gameState.Player1 ? 1 : 2));
                        skillAction(context);
                        messages.AddRange(context.Messages);
                    }
                }
            }

            return messages;
        }

        /// <summary>
        /// 处理连携技能
        /// </summary>
        private List<string> ProcessChainSkills()
        {
            var messages = new List<string>();

            foreach (var player in new[] { _gameState.Player1, _gameState.Player2 })
            {
                foreach (var character in player.FieldCharacters.ToList())
                {
                    // 执行Lua连携技能
                    var chainSkills = character.GetSkillsByTrigger(SkillTrigger.Chain);
                    foreach (var luaSkill in chainSkills)
                    {
                        var context = new SkillExecutionContext(_gameState, character, player, player == _gameState.Player1 ? _gameState.Player2 : _gameState.Player1, () => DrawCardForPlayer(player == _gameState.Player1 ? 1 : 2));
                        luaSkill.Execute(context);
                        messages.AddRange(context.Messages);
                    }
                }
            }

            return messages;
        }

        /// <summary>
        /// 处理事件技能（每回合在当前三维属性最高的场地中随机抽取一个角色触发）
        /// </summary>
        private List<string> ProcessEventSkills()
        {
            var messages = new List<string>();

            foreach (var player in new[] { _gameState.Player1, _gameState.Player2 })
            {
                // 找到当前三维属性最高的场地
                var frontTotal = player.GetTotalPower();
                var middleTotal = player.GetTotalWealth();
                var backTotal = player.GetTotalFame();

                var maxValue = Math.Max(Math.Max(frontTotal, middleTotal), backTotal);
                var highestFields = new List<FieldType>();

                if (frontTotal == maxValue) highestFields.Add(FieldType.Front);
                if (middleTotal == maxValue) highestFields.Add(FieldType.Middle);
                if (backTotal == maxValue) highestFields.Add(FieldType.Back);

                // 随机选择一个最高场地
                var selectedField = highestFields[GlobalRandom.Next(highestFields.Count)];
                var field = player.FieldManager.GetField(selectedField);

                if (field.Characters.Count > 0)
                {
                    var randomCharacter = field.Characters[GlobalRandom.Next(field.Characters.Count)];

                    // 执行Lua事件技能
                    var eventSkills = randomCharacter.GetSkillsByTrigger(SkillTrigger.Event);
                    if (eventSkills.Any())
                    {
                        messages.Add($"[技能系统] {randomCharacter.Name} 在{GetFieldName(selectedField)}触发事件技能");
                    }
                    foreach (var luaSkill in eventSkills)
                    {
                        var context = new SkillExecutionContext(_gameState, randomCharacter, player, player == _gameState.Player1 ? _gameState.Player2 : _gameState.Player1, () => DrawCardForPlayer(player == _gameState.Player1 ? 1 : 2));
                        luaSkill.Execute(context);
                        messages.AddRange(context.Messages);
                    }

                    // 执行传统事件技能委托
                    foreach (var skillAction in randomCharacter.Skills)
                    {
                        var context = new SkillExecutionContext(_gameState, randomCharacter, player, player == _gameState.Player1 ? _gameState.Player2 : _gameState.Player1, () => DrawCardForPlayer(player == _gameState.Player1 ? 1 : 2));
                        skillAction(context);
                        messages.AddRange(context.Messages);
                    }
                }
            }

            return messages;
        }


        /// <summary>
        /// 处理回合结束技能
        /// </summary>
        private List<string> ProcessTurnEndSkills()
        {
            var messages = new List<string>();

            foreach (var player in new[] { _gameState.Player1, _gameState.Player2 })
            {
                foreach (var character in player.FieldCharacters.ToList())
                {
                    // 执行Lua回合结束技能
                    var turnEndSkills = character.GetSkillsByTrigger(SkillTrigger.TurnEnd);
                    foreach (var luaSkill in turnEndSkills)
                    {
                        var context = new SkillExecutionContext(_gameState, character, player, player == _gameState.Player1 ? _gameState.Player2 : _gameState.Player1, () => DrawCardForPlayer(player == _gameState.Player1 ? 1 : 2));
                        luaSkill.Execute(context);
                        messages.AddRange(context.Messages);
                    }
                }
            }

            return messages;
        }

        /// <summary>
        /// 技能执行上下文
        /// </summary>
        private class SkillExecutionContext : ISkillContext
        {
            public GameState GameState { get; }
            public Character? CurrentCharacter { get; }
            public Character? OpponentCharacter { get; }
            public Player CurrentPlayer { get; }
            public Player OpponentPlayer { get; }
            public int? AssignedFieldType { get; }
            public List<string> Messages { get; } = new List<string>();

            private readonly Func<Card?> _drawForCurrentPlayer;

            public SkillExecutionContext(GameState gameState, Character? currentCharacter, Player currentPlayer, Player opponentPlayer, Func<Card?> drawForCurrentPlayer, FieldType? assignedFieldType = null)
            {
                GameState = gameState;
                CurrentCharacter = currentCharacter;
                CurrentPlayer = currentPlayer;
                OpponentPlayer = opponentPlayer;
                // 转换 FieldType (0-2) 为 Lua 期望的格式 (1-3)
                AssignedFieldType = assignedFieldType.HasValue ? (int)assignedFieldType.Value + 1 : null;
                // 简化：对手角色设为null，技能中可以通过其他方式获取
                OpponentCharacter = null;
                _drawForCurrentPlayer = drawForCurrentPlayer;
            }

            public void LogMessage(string message)
            {
                // 尝试获取当前MessageProcessor实例
                var processor = MDiceV2.Models.MessageProcessor.Instance;
                string refined = message;
                if (processor != null && CurrentPlayer != null)
                {
                    // Player没有UserId字段，无法精确绑定用户ID，使用0
                    var msg = new MDiceV2.Models.Msg(
                        0, // groupId
                        0, // userId
                        message,
                        MDiceV2.Models.MessageSource.privatechat,
                        false, // isSimulationMode
                        false, // isAted
                        false  // shouldIgnore
                    );
                    refined = processor.RefineMsg(message, msg);
                }
                Messages.Add(refined);
                // 同时记录到日志
                Log.InfoFormat($"技能日志: {refined}");
            }

            public int GetRandomInt(int min, int max)
            {
                return GlobalRandom.Next(min, max);
            }

            /// <summary>
            /// 使用工程中的 Dice 工具计算掷骰表达式并返回 DiceResult（包含明细与总和）
            /// 供 Lua 脚本通过 context:RollDice(expr) 调用
            /// </summary>
            /// <param name="expr">例如 "3d10" 或复杂表达式 "2d6+3"</param>
            /// <returns>DiceResult 对象</returns>
            public DiceResult RollDice(string expr)
            {
                try
                {
                    return Dice.CalculateExpression(expr ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Log.Error($"RollDice error for expr '{expr}': {ex.Message}");
                    return new DiceResult { Rolls = new List<int>(), Total = -1, Detail = "[DiceError]", Success = false };
                }
            }

            /// <summary>
            /// 为当前执行上下文的玩家抽取一张手牌（考虑阵营），并返回抽取的 Card 对象
            /// 该方法会将卡牌添加到 CurrentPlayer.HandCards
            /// </summary>
            /// <returns>抽到的 Card，可能为 null</returns>
            public Card? DrawOneCardToCurrentPlayer()
            {
                if (_drawForCurrentPlayer == null) return null;
                try
                {
                    var card = _drawForCurrentPlayer();
                    if (card != null)
                    {
                        CurrentPlayer.AddCardToHand(card);
                    }
                    return card;
                }
                catch (Exception ex)
                {
                    Log.Error($"DrawOneCardToCurrentPlayer failed: {ex.Message}");
                    return null;
                }
            }

            public string GetSkillNarrative(string skillId, string trigger)
            {
                return GameLoader.GetSkillNarrative(skillId, ParseSkillTriggerString(trigger));
            }

            /// <summary>
            /// 从对方场地移除角色
            /// </summary>
            /// <param name="character">要移除的角色</param>
            /// <param name="fieldType">场地类型（1:Front, 2:Middle, 3:Back）</param>
            /// <returns>是否成功移除</returns>
            public bool RemoveCharacterFromOpponent(Character character, int fieldType)
            {
                FieldType actualFieldType = fieldType switch
                {
                    1 => FieldType.Front,
                    2 => FieldType.Middle,
                    3 => FieldType.Back,
                    _ => FieldType.Front
                };

                return OpponentPlayer.RemoveCharacterFromField(character, actualFieldType, this);
            }

            public bool RemoveCharacterFromCurrentPlayer(Character character, int fieldType)
            {
                FieldType actualFieldType = fieldType switch
                {
                    1 => FieldType.Front,
                    2 => FieldType.Middle,
                    3 => FieldType.Back,
                    _ => FieldType.Front
                };

                return CurrentPlayer.RemoveCharacterFromField(character, actualFieldType, this);
            }

            /// <summary>
            /// 设置场地效果，遵循强度等级规则（强度越低优先级越高）
            /// </summary>
            /// <param name="tag">效果标签（如 "Halloween"）</param>
            /// <param name="intensity">强度等级（1-3，1最低优先级最高）</param>
            /// <param name="durationTurns">持续回合数</param>
            /// <returns>是否成功设置</returns>
            public bool SetFieldEffect(string tag, int intensity, int durationTurns)
            {
                return GameState.TrySetFieldEffect(tag, intensity, durationTurns, GameState.CurrentTurn);
            }

            /// <summary>
            /// 获取当前场地效果
            /// </summary>
            /// <returns>当前的 FieldEffect 对象，或 null 如果没有效果</returns>
            public FieldEffect? GetCurrentFieldEffect()
            {
                return GameState.CurrentFieldEffect;
            }

            /// <summary>
            /// 移除当前场地效果
            /// </summary>
            public void RemoveFieldEffect()
            {
                GameState.RemoveCurrentFieldEffect();
            }

            private static SkillTrigger ParseSkillTriggerString(string triggerString)
            {
                return triggerString switch
                {
                    "Entrance" => SkillTrigger.Entrance,
                    "TurnEnd" => SkillTrigger.TurnEnd,
                    "Field" => SkillTrigger.Field,
                    "Chain" => SkillTrigger.Chain,
                    "Event" => SkillTrigger.Event,
                    "Immediate" => SkillTrigger.Immediate,
                    _ => SkillTrigger.Field
                };
            }
        }

        /// <summary>
        /// 检查游戏结束条件
        /// </summary>
        private bool CheckGameEndCondition()
        {
            // 仅在达到回合上限时结算胜负，其余情况继续游戏
            return _gameState.CurrentTurn >= 20;
        }

        /// <summary>
        /// 结算游戏
        /// </summary>

        private List<string> SettleGame()
        {
            var messages = new List<string>();

            int player1Power = _gameState.Player1.GetTotalPower();
            int player1Wealth = _gameState.Player1.GetTotalWealth();
            int player1Fame = _gameState.Player1.GetTotalFame();

            int player2Power = _gameState.Player2.GetTotalPower();
            int player2Wealth = _gameState.Player2.GetTotalWealth();
            int player2Fame = _gameState.Player2.GetTotalFame();

            messages.Add($"{_gameState.Player1.Name} 最终属性：武力{player1Power}，财力{player1Wealth}，名声{player1Fame}");
            messages.Add($"{_gameState.Player2.Name} 最终属性：武力{player2Power}，财力{player2Wealth}，名声{player2Fame}");

            // 优先检查立即败北条件（任一属性 < -10）
            bool p1Critical = player1Power < -10 || player1Wealth < -10 || player1Fame < -10;
            bool p2Critical = player2Power < -10 || player2Wealth < -10 || player2Fame < -10;

            if (p1Critical || p2Critical)
            {
                if (p1Critical && p2Critical)
                {
                    _gameState.Winner = 0; // 双方同时异常，视为平局
                    messages.Add("双方均触发属性降至 -10 以下，判定为平局。");
                }
                else if (p1Critical)
                {
                    _gameState.Winner = 2;
                    messages.Add($"{_gameState.Player1.Name} 的某项属性降至 -10 以下，判定败北。{_gameState.Player2.Name} 获胜！");
                }
                else
                {
                    _gameState.Winner = 1;
                    messages.Add($"{_gameState.Player2.Name} 的某项属性降至 -10 以下，判定败北。{_gameState.Player1.Name} 获胜！");
                }

                return messages;
            }

            // 非立即败北：使用原有的回合结算规则（差距最大的属性为决定性属性）
            var differences = new[]
            {
                Math.Abs(player1Power - player2Power),
                Math.Abs(player1Wealth - player2Wealth),
                Math.Abs(player1Fame - player2Fame)
            };

            var maxDifferenceIndex = Array.IndexOf(differences, differences.Max());
            string decisiveAttribute = maxDifferenceIndex switch
            {
                0 => "武力",
                1 => "财力",
                2 => "名声",
                _ => "未知"
            };

            messages.Add($"决定性属性：{decisiveAttribute}（差距：{differences[maxDifferenceIndex]}）");

            bool player1Wins = false;
            switch (maxDifferenceIndex)
            {
                case 0: // 武力
                    player1Wins = player1Power > player2Power;
                    break;
                case 1: // 财力
                    player1Wins = player1Wealth > player2Wealth;
                    break;
                case 2: // 名声
                    player1Wins = player1Fame > player2Fame;
                    break;
            }

            if (player1Wins)
            {
                _gameState.Winner = 1;
                messages.Add($"{_gameState.Player1.Name} 获胜！");
            }
            else
            {
                _gameState.Winner = 2;
                messages.Add($"{_gameState.Player2.Name} 获胜！");
            }

            return messages;
        }

        /// <summary>
        /// 获取场地名称
        /// </summary>
        private string GetFieldName(FieldType fieldType)
        {
            return fieldType switch
            {
                FieldType.Front => "前场",
                FieldType.Middle => "中場",
                FieldType.Back => "后场",
                _ => "未知场地"
            };
        }

        /// <summary>
        /// 格式化角色信息显示
        /// </summary>
        private string FormatCharacterInfo(Character character)
        {
            var skillNames = new List<string>();
            
            // 获取Lua技能名称
            foreach (var skill in character.LuaSkills)
            {
                if (!string.IsNullOrEmpty(skill.Name))
                {
                    skillNames.Add(skill.Name);
                }
            }
            
            // 也包含传统技能委托的名称（如果有的话）
            if (character.Skills.Any())
            {
                skillNames.Add("传统技能");
            }
            
            var skillsText = skillNames.Any() ? string.Join("、", skillNames) : "无";

            // 根据稀有度添加星标
            string rarityStars = character.Rarity switch
            {
                Rarity.Common => "(★)",
                Rarity.Rare => "(★★)",
                Rarity.Epic => "(★★★)",
                Rarity.Legendary => "(★★★★)",
                Rarity.Named => "(★★★★★)",
                _ => ""
            };

            // 显示为：国王(★)-8(1)/6(1)/4(1) | 技能:xxx
            string statText = $"{character.Name}{rarityStars}-{character.Power}({character.PerTurnPower})/" +
                              $"{character.Wealth}({character.PerTurnWealth})/" +
                              $"{character.Fame}({character.PerTurnFame}) | 技能:{skillsText}";
            return statText;
        }
    }
}