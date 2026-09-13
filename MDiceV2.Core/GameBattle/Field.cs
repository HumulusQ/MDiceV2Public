using System.Collections.Generic;

namespace MDiceV2.Core.GameBattle
{
    /// <summary>
    /// 场地类，包含该场地上的角色列表和上限
    /// </summary>
    public class Field
    {
        public FieldType Type { get; set; }
        public int MaxCharacters { get; set; } // 角色数量上限
        public List<Character> Characters { get; set; } = new List<Character>();
    }

    /// <summary>
    /// 游戏中的场地管理器
    /// </summary>
    public class FieldManager
    {
        public Field FrontField { get; set; } // 前场
        public Field MiddleField { get; set; } // 中场
        public Field BackField { get; set; } // 后场
        // 整体场地上限（前中后三个位置合计）
        public int CombinedMax { get; set; }

        public FieldManager(int frontLimit, int middleLimit, int backLimit, int combinedMax = 18)
        {
            FrontField = new Field { Type = FieldType.Front, MaxCharacters = frontLimit };
            MiddleField = new Field { Type = FieldType.Middle, MaxCharacters = middleLimit };
            BackField = new Field { Type = FieldType.Back, MaxCharacters = backLimit };
            CombinedMax = combinedMax;
        }

        /// <summary>
        /// 获取指定类型的场地
        /// </summary>
        public Field? GetField(FieldType type)
        {
            return type switch
            {
                FieldType.Front => FrontField,
                FieldType.Middle => MiddleField,
                FieldType.Back => BackField,
                _ => null
            };
        }

        /// <summary>
        /// 检查是否可以添加角色到指定场地
        /// </summary>
        public bool CanAddCharacterToField(FieldType type)
        {
            var field = GetField(type);
            if (field == null) return false;

            // 检查单个场地是否已达上限
            if (field.Characters.Count >= field.MaxCharacters) return false;

            // 检查前中后合计是否已达合并上限
            int total = FrontField.Characters.Count + MiddleField.Characters.Count + BackField.Characters.Count;
            if (total >= CombinedMax) return false;

            return true;
        }

        /// <summary>
        /// 添加角色到指定场地
        /// </summary>
        public bool AddCharacterToField(Character character, FieldType type)
        {
            if (!CanAddCharacterToField(type))
                return false;

            GetField(type).Characters.Add(character);
            return true;
        }

        /// <summary>
        /// 从指定场地移除角色
        /// </summary>
        public bool RemoveCharacterFromField(Character character, FieldType type)
        {
            var field = GetField(type);
            if (field == null) return false;

            return field.Characters.Remove(character);
        }

        /// <summary>
        /// 检查游戏是否结束（任一方的任一场地达到上限）
        /// </summary>
        public bool IsGameOver()
        {
            // 游戏不再单纯以某一子场达到上限为结束判定，保留为当合计达到上限时返回true
            int total = FrontField.Characters.Count + MiddleField.Characters.Count + BackField.Characters.Count;
            return total >= CombinedMax;
        }
    }
}