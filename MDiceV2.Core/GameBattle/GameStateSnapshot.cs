using System;
using System.Collections.Generic;
using MDiceV2.Models;

namespace MDiceV2.Core.GameBattle
{
    /// <summary>
    /// 轻量化的游戏状态持久化快照，仅包含可重建数据。
    /// </summary>
    public class GameStateSnapshot
    {
        public string Player2Id { get; set; }
        public int CurrentTurn { get; set; }
        public string CurrentWeather { get; set; }
        public bool IsGameOver { get; set; }
        public int Winner { get; set; }
        public bool IsProcessingHandAction { get; set; }
        public DateTime LastActiveTime { get; set; }
        public CardSnapshot PendingCard { get; set; }
        public PlayerSnapshot Player1 { get; set; }
        public PlayerSnapshot Player2 { get; set; }
    }

    /// <summary>
    /// 玩家持久化快照。
    /// </summary>
    public class PlayerSnapshot
    {
        public string Name { get; set; }
        public int TotalPower { get; set; }
        public int TotalWealth { get; set; }
        public int TotalFame { get; set; }
        public int CharacterPower { get; set; }
        public int CharacterWealth { get; set; }
        public int CharacterFame { get; set; }
        public List<CardSnapshot> HandCards { get; set; } = new();
        public FieldSnapshot FrontField { get; set; } = new();
        public FieldSnapshot MiddleField { get; set; } = new();
        public FieldSnapshot BackField { get; set; } = new();
    }

    /// <summary>
    /// 场地持久化快照，仅记录角色名称序列。
    /// </summary>
    public class FieldSnapshot
    {
        public List<string> Characters { get; set; } = new();
    }

    /// <summary>
    /// 卡牌持久化快照，通过名称 + 类型恢复定义。
    /// </summary>
    public class CardSnapshot
    {
        public string Name { get; set; }
        public CardType Type { get; set; }
        public SpecialCardType? SpecialType { get; set; }
    }

    /// <summary>
    /// GameState 与持久化快照互转帮助类。
    /// </summary>
    public static class GameStateSnapshotMapper
    {
        public static GameStateSnapshot ToSnapshot(GameState state)
        {
            if (state == null) return null;

            return new GameStateSnapshot
            {
                Player2Id = state.Player2Id,
                CurrentTurn = state.CurrentTurn,
                CurrentWeather = state.CurrentWeather,
                IsGameOver = state.IsGameOver,
                Winner = state.Winner,
                IsProcessingHandAction = state.IsProcessingHandAction,
                LastActiveTime = state.LastActiveTime == default ? DateTime.UtcNow : state.LastActiveTime,
                PendingCard = ToCardSnapshot(state.PendingCard),
                Player1 = ToPlayerSnapshot(state.Player1),
                Player2 = ToPlayerSnapshot(state.Player2)
            };
        }

        public static GameState FromSnapshot(GameStateSnapshot snapshot)
        {
            if (snapshot == null) return null;

            var gameState = new GameState
            {
                Player2Id = snapshot.Player2Id,
                CurrentTurn = snapshot.CurrentTurn,
                CurrentWeather = snapshot.CurrentWeather,
                IsGameOver = snapshot.IsGameOver,
                Winner = snapshot.Winner,
                IsProcessingHandAction = snapshot.IsProcessingHandAction,
                LastActiveTime = snapshot.LastActiveTime == default ? DateTime.UtcNow : snapshot.LastActiveTime,
                PendingCard = FromCardSnapshot(snapshot.PendingCard)
            };

            gameState.Player1 = FromPlayerSnapshot(snapshot.Player1);
            gameState.Player2 = FromPlayerSnapshot(snapshot.Player2);

            return gameState;
        }

        private static PlayerSnapshot ToPlayerSnapshot(Player player)
        {
            if (player == null) return null;

            return new PlayerSnapshot
            {
                Name = player.Name,
                TotalPower = player.TotalPower,
                TotalWealth = player.TotalWealth,
                TotalFame = player.TotalFame,
                CharacterPower = player.CharacterPower,
                CharacterWealth = player.CharacterWealth,
                CharacterFame = player.CharacterFame,
                HandCards = ToCardSnapshotList(player.HandCards),
                FrontField = ToFieldSnapshot(player.FieldManager?.FrontField),
                MiddleField = ToFieldSnapshot(player.FieldManager?.MiddleField),
                BackField = ToFieldSnapshot(player.FieldManager?.BackField)
            };
        }

        private static Player FromPlayerSnapshot(PlayerSnapshot snapshot)
        {
            if (snapshot == null) return null;

            var player = new Player();
            player.Name = snapshot.Name;

            // 直接设置累积数值，避免重放逻辑。
            player.TotalPower = snapshot.TotalPower;
            player.TotalWealth = snapshot.TotalWealth;
            player.TotalFame = snapshot.TotalFame;
            player.CharacterPower = snapshot.CharacterPower;
            player.CharacterWealth = snapshot.CharacterWealth;
            player.CharacterFame = snapshot.CharacterFame;

            // 重建场地与在场角色
            RestoreField(player, player.FieldManager?.FrontField, snapshot.FrontField);
            RestoreField(player, player.FieldManager?.MiddleField, snapshot.MiddleField);
            RestoreField(player, player.FieldManager?.BackField, snapshot.BackField);

            // 手牌
            player.HandCards = FromCardSnapshotList(snapshot.HandCards);

            return player;
        }

        private static FieldSnapshot ToFieldSnapshot(Field field)
        {
            var snap = new FieldSnapshot();
            if (field?.Characters != null)
            {
                foreach (var c in field.Characters)
                {
                    if (!string.IsNullOrWhiteSpace(c?.Name))
                    {
                        snap.Characters.Add(c.Name);
                    }
                }
            }
            return snap;
        }

        private static void RestoreField(Player player, Field field, FieldSnapshot snapshot)
        {
            if (field == null || snapshot?.Characters == null) return;

            int successCount = 0;
            int failureCount = 0;

            foreach (var name in snapshot.Characters)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                
                try
                {
                    var character = GameLoader.GetCharacterByName(name);
                    if (character == null)
                    {
                        Log.Warn($"[Snapshot] ⚠ 无法通过名称重建角色: {name}（可能已被删除或改名）");
                        failureCount++;
                        continue;
                    }
                    field.Characters.Add(character);
                    player.FieldCharacters.Add(character);
                    successCount++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Snapshot] ⚠ 场地角色 {name} 恢复失败: {ex.Message}");
                    failureCount++;
                }
            }

            if (failureCount > 0)
            {
                Log.InfoFormat($"[Snapshot] 场地角色恢复: 成功 {successCount} 个，失败 {failureCount} 个");
            }
        }

        private static List<CardSnapshot> ToCardSnapshotList(List<Card> cards)
        {
            var list = new List<CardSnapshot>();
            if (cards == null) return list;
            foreach (var card in cards)
            {
                var snap = ToCardSnapshot(card);
                if (snap != null)
                {
                    list.Add(snap);
                }
            }
            return list;
        }

        private static List<Card> FromCardSnapshotList(List<CardSnapshot> snapshots)
        {
            var list = new List<Card>();
            if (snapshots == null) return list;
            
            int successCount = 0;
            int failureCount = 0;

            foreach (var snap in snapshots)
            {
                try
                {
                    var card = FromCardSnapshot(snap);
                    if (card != null)
                    {
                        list.Add(card);
                        successCount++;
                    }
                    else
                    {
                        failureCount++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Snapshot] ⚠ 卡牌恢复异常 {snap?.Name}: {ex.Message}");
                    failureCount++;
                }
            }

            if (failureCount > 0)
            {
                Log.InfoFormat($"[Snapshot] 卡牌恢复: 成功 {successCount} 张，失败 {failureCount} 张");
            }

            return list;
        }

        private static CardSnapshot ToCardSnapshot(Card card)
        {
            if (card == null) return null;

            if (card is SpecialCard sc)
            {
                return new CardSnapshot
                {
                    Name = sc.Name,
                    Type = CardType.Special,
                    SpecialType = sc.SpecialType
                };
            }

            return new CardSnapshot
            {
                Name = card.Name,
                Type = CardType.Character
            };
        }

        private static Card FromCardSnapshot(CardSnapshot snapshot)
        {
            if (snapshot == null) return null;

            try
            {
                if (snapshot.Type == CardType.Special)
                {
                    var def = GameLoader.GetSpecialCardByName(snapshot.Name);
                    if (def == null)
                    {
                        Log.Warn($"[Snapshot] ⚠ 特殊卡不存在（可能已被删除或改名）: {snapshot.Name}");
                        return null;
                    }
                    return new SpecialCard
                    {
                        Name = def.Name,
                        SpecialType = def.SpecialType,
                        Effect = def.Effect,
                        ImmediateSkill = def.ImmediateSkill
                    };
                }
                else
                {
                    var def = GameLoader.GetCharacterByName(snapshot.Name);
                    if (def == null)
                    {
                        Log.Warn($"[Snapshot] ⚠ 角色卡不存在（可能已被删除或改名）: {snapshot.Name}");
                        return null;
                    }
                    return new CharacterCard
                    {
                        Name = def.Name,
                        Character = def
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[Snapshot] ⚠ 卡牌 {snapshot?.Name} 恢复过程异常: {ex.Message}");
                return null;
            }
        }
    }
}
