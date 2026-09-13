using System;
using System.Collections.Generic;
using System.Linq;
using MDiceV2.Models;

namespace MDiceV2.Core.GameBattle
{
    /// <summary>
    /// AI控制器，负责控制机器人玩家的游戏决策
    /// </summary>
    public class AIController
    {
        private readonly GameState _gameState;
        private readonly TurnManager _turnManager;

        public AIController(GameState gameState, TurnManager turnManager)
        {
            _gameState = gameState;
            _turnManager = turnManager;
        }

        /// <summary>
        /// AI执行回合行动 - 处理AI玩家的手牌
        /// </summary>
        public List<string> ExecuteTurn(Card? drawnCard)
        {
            var messages = new List<string>();
            var aiPlayer = _gameState.Player1; // AI是Player1

            // 处理AI的手牌
            var handMessages = ProcessAIHand();
            messages.AddRange(handMessages);

            return messages;
        }

        /// <summary>
        /// 处理AI玩家的手牌
        /// </summary>
        private List<string> ProcessAIHand()
        {
            var messages = new List<string>();
            var aiPlayer = _gameState.Player1;

            if (aiPlayer.HandCards.Count == 0)
            {
                messages.Add($"{aiPlayer.Name} 没有手牌，跳过回合。");
                return messages;
            }

            // AI处理手牌：随机选择1-2张卡牌使用
            int cardsToPlay = Math.Min(aiPlayer.HandCards.Count, GlobalRandom.Next(1, Math.Min(3, aiPlayer.HandCards.Count + 1)));
            
            for (int i = 0; i < cardsToPlay && aiPlayer.HandCards.Count > 0; i++)
            {
                // 随机选择一张手牌使用
                int cardIndex = GlobalRandom.Next(aiPlayer.HandCards.Count);
                var card = aiPlayer.HandCards[cardIndex];
                aiPlayer.HandCards.RemoveAt(cardIndex);

                if (card is CharacterCard characterCard)
                {
                    // 使用角色卡
                    var placeMessages = HandleCharacterCard(characterCard);
                    messages.AddRange(placeMessages);
                }
                else if (card is SpecialCard specialCard)
                {
                    // 使用特殊卡
                    var useMessages = HandleSpecialCard(specialCard);
                    messages.AddRange(useMessages);
                }
            }

            if (aiPlayer.HandCards.Count > 0)
            {
                messages.Add($"{aiPlayer.Name} 保留 {aiPlayer.HandCards.Count} 张手牌。");
            }

            return messages;
        }

        /// <summary>
        /// 处理角色卡 - 根据角色偏向选择最佳场地
        /// </summary>
        private List<string> HandleCharacterCard(CharacterCard card)
        {
            var messages = new List<string>();

            // 获取角色偏向（从GameLoader获取角色数据）
            if (card?.Character?.Name == null)
                return PlaceCharacterRandomly(card);
            var characterData = GameLoader.GetCharacterByName(card.Character.Name);
            if (characterData == null)
            {
                // 如果找不到角色数据，使用随机放置
                return PlaceCharacterRandomly(card);
            }

            // 根据角色偏向选择场地
            FieldType preferredField = characterData.FieldPreference;

            // 检查偏好场地是否可放置
            if (_gameState.Player1.FieldManager.CanAddCharacterToField(preferredField))
            {
                // 优先使用偏好场地
                var placeMessages = _turnManager.PlaceCharacterCard(1, card, preferredField);
                messages.AddRange(placeMessages);
            }
            else
            {
                // 偏好场地已满，寻找其他可用场地
                var availableFields = GetAvailableFields();
                if (availableFields.Count > 0)
                {
                    // 随机选择一个可用场地
                    var selectedField = availableFields[GlobalRandom.Next(availableFields.Count)];
                    var placeMessages = _turnManager.PlaceCharacterCard(1, card, selectedField);
                    messages.AddRange(placeMessages);
                }
                else
                {
                    messages.Add("AI无法放置角色卡：所有场地已满");
                }
            }

            return messages;
        }

        /// <summary>
        /// 处理特殊卡 - 随机决定是否使用
        /// </summary>
        private List<string> HandleSpecialCard(SpecialCard card)
        {
            var messages = new List<string>();

            // 50%概率使用特殊卡
            bool shouldUse = GlobalRandom.Next(2) == 0;

            if (shouldUse)
            {
                var useMessages = _turnManager.PlaySpecialCard(1, card);
                messages.AddRange(useMessages);
            }
            else
            {
                messages.Add($"{_gameState.Player1.Name} 选择弃置特殊卡：{card.Name}");
            }

            return messages;
        }

        /// <summary>
        /// 随机放置角色卡（备用方案）
        /// </summary>
        private List<string> PlaceCharacterRandomly(CharacterCard card)
        {
            var messages = new List<string>();
            var availableFields = GetAvailableFields();

            if (availableFields.Count > 0)
            {
                var selectedField = availableFields[GlobalRandom.Next(availableFields.Count)];
                var placeMessages = _turnManager.PlaceCharacterCard(1, card, selectedField);
                messages.AddRange(placeMessages);
            }
            else
            {
                messages.Add("AI无法放置角色卡：所有场地已满");
            }

            return messages;
        }

        /// <summary>
        /// 获取AI可用的场地列表
        /// </summary>
        private List<FieldType> GetAvailableFields()
        {
            var availableFields = new List<FieldType>();

            if (_gameState.Player1.FieldManager.CanAddCharacterToField(FieldType.Front))
                availableFields.Add(FieldType.Front);

            if (_gameState.Player1.FieldManager.CanAddCharacterToField(FieldType.Middle))
                availableFields.Add(FieldType.Middle);

            if (_gameState.Player1.FieldManager.CanAddCharacterToField(FieldType.Back))
                availableFields.Add(FieldType.Back);

            return availableFields;
        }

        /// <summary>
        /// AI决策是否在当前回合使用特殊卡（更复杂的决策逻辑）
        /// </summary>
        private bool ShouldUseSpecialCard(SpecialCard card)
        {
            // 基础实现：简单随机
            // 可以扩展为基于游戏状态的更复杂决策
            return GlobalRandom.Next(2) == 0;
        }

        /// <summary>
        /// 根据游戏状态优化场地选择
        /// </summary>
        private FieldType ChooseOptimalField(Character character)
        {
            // 基础实现：使用角色偏向
            // 可以扩展为基于当前游戏状态的策略选择
            return character.FieldPreference;
        }
    }
}