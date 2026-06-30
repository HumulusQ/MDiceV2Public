using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;
using static MDiceV2.Models.Dice;

namespace MDiceV2.Models;

public partial class MessageProcessor : ObservableObject
{
    /// <summary>
    /// 当前人物姓名字典
    /// Key: 用户ID, Value: 人物名
    /// </summary>
    private ConcurrentDictionary<long, string> CurrentCharacterNames = new();

    /// <summary>
    /// 人物卡数据结构（可序列化，包含技能与扩展字段）
    /// </summary>
    public class CharacterSheet
    {
        /// <summary>
        /// 人物卡名称（与 characterSkills 中的键一致，用于展示和存储）
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// 技能字典（线程安全，运行时使用）
        /// Key: 技能名, Value: 技能值
        /// </summary>
        public ConcurrentDictionary<string, int> Skills { get; set; } = new();

        /// <summary>
        /// 人物类型（例如 coc / et / dnd 等），可选，默认 coc
        /// </summary>
        public string? CharacterType { get; set; } = "coc";

        /// <summary>
        /// CoC 伤害加值缓存
        /// </summary>
        public string? DB_COC { get; set; }

        /// <summary>
        /// CoC 人物卡详情自定义格式
        /// </summary>
        public string? COCCharacterDetailsCustomFormat { get; set; }

        /// <summary>
        /// 额外元数据，预留扩展
        /// </summary>
        public Dictionary<string, string> ExtraMeta { get; set; } = new();

        /// <summary>
        /// 从旧格式（仅技能字典）快速构建
        /// </summary>
        public static CharacterSheet FromLegacySkills(Dictionary<string, int> skills, string? name = null)
        {
            var sheet = new CharacterSheet
            {
                Name = name
            };
            if (skills != null)
            {
                foreach (var kv in skills)
                {
                    sheet.Skills[kv.Key] = kv.Value;
                }
            }
            return sheet;
        }

        /// <summary>
        /// 为序列化提供简单视图（可选，用于调试/兼容）
        /// </summary>
        public Dictionary<string, object> ToDebugView()
        {
            return new Dictionary<string, object>
            {
                ["Name"] = Name ?? "",
                ["Skills"] = Skills.ToDictionary(kv => kv.Key, kv => kv.Value),
                ["CharacterType"] = CharacterType ?? "",
                ["COCCharacterDetailsCustomFormat"] = COCCharacterDetailsCustomFormat ?? "",
                ["ExtraMeta"] = ExtraMeta
            };
        }

        /// <summary>
        /// 统一的人物详情文本入口
        /// </summary>
        public string CharacterDetails()
        {
            if ((CharacterType ?? "coc").Equals("coc", StringComparison.OrdinalIgnoreCase))
            {
                return COCCharacterDetails();
            }

            return "目前暂不支持此规则的角色卡展示";
        }

        /// <summary>
        /// CoC 角色详情输出：
        /// DB、SAN 数值和可视化条、HP 数值和可视化条。
        /// </summary>
        public string COCCharacterDetails()
        {
            COCDBBuilder();

            // HP
            int hp = Skills.GetValueOrDefault("生命", 0);
            int maxHp = COCHPCalculator();
            string visibleHp = BuildBar(hp, maxHp);

            // SAN（理智）
            int san = Skills.GetValueOrDefault("理智", 0);
            // CoC 规则下常用 99 作为理论上限，此处可根据需求调整为可配置
            int maxSan = 99;
            string visibleSan = BuildBar(san, maxSan);

            string db = DB_COC ?? "ERROR";

            // 扩展 COCCharacterDetails 模板：
            // {0}=DB, {1}=SAN, {2}=HP, {3}=VisibleHp, {4}=VisibleSan
            var format = COCCharacterDetailsCustomFormat ?? GlobalFeedbackMessages.FeedbackTemplates["COCCharacterDetails"];
            return SafeFormatString(format, db, san.ToString(), hp.ToString(), visibleHp, visibleSan);
        }

        /// <summary>
        /// 构建 10 格可视化条（█ / ░）。max<=0 返回 "ERROR"。
        /// </summary>
        private static string BuildBar(int value, int max)
        {
            if (max <= 0)
                return "ERROR";

            if (value < 0) value = 0;
            if (value > max) value = max;

            int filled = (int)Math.Round((value / (double)max) * 10, MidpointRounding.AwayFromZero);
            if (filled < 0) filled = 0;
            if (filled > 10) filled = 10;

            var chars = new char[10];
            for (int i = 0; i < 10; i++)
            {
                chars[i] = (i < filled) ? '█' : '░';
            }
            return new string(chars);
        }

        public int COCHPCalculator()
        {
            int size = Skills.GetValueOrDefault("体型", 0);
            int cons = Skills.GetValueOrDefault("体质", 0);
            int sum = cons + size;
            return (int)Math.Ceiling(sum / 10.0);
        }

        public void COCDBBuilder()
        {
            int power = Skills.GetValueOrDefault("力量", 0);
            int size = Skills.GetValueOrDefault("体型", 0);
            int sum1 = power + size;
            DB_COC = sum1 switch
            {
                <= 64 => "-2",
                <= 84 => "-1",
                <= 124 => "0",
                <= 164 => "1D4",
                <= 204 => "1D6",
                <= 284 => "2D6",
                <= 364 => "3D6",
                <= 444 => "4D6",
                _ => "5D6"
            };

            // 自动补全 HP / 理智（仅在缺失时）
            if (!Skills.ContainsKey("生命"))
            {
                int hp = COCHPCalculator();
                Skills["生命"] = hp;
            }
            if (!Skills.ContainsKey("理智"))
            {
                Skills["理智"] = Skills.GetValueOrDefault("意志", 0);
            }
        }
    }


    /// <summary>
    /// 人物技能/人物卡总表
    /// Key: 用户ID
    /// Value: (Key: 人物名, Value: CharacterSheet)
    /// </summary>
    private ConcurrentDictionary<long, ConcurrentDictionary<string, CharacterSheet>> characterSkills
        = new();


    /// <summary>
    /// 获取或创建用户的人物卡名称，并确保对应的 CharacterSheet 存在。
    /// </summary>
    /// <param name="userId">用户ID。</param>
    /// <param name="characterNameFromCommand">从指令中解析出的人物卡名称，如果指令中没有指定，则传入空字符串。</param>
    /// <param name="isSimulationMode">是否为模拟模式。</param>
    /// <param name="msg">消息对象，用于回复和日志。</param>
    /// <returns>最终确定的人物卡名称。如果发生错误且无法确定人物卡名称，则返回 null。</returns>
    private string GetOrCreateCharacterName(long userId, string characterNameFromCommand, bool isSimulationMode, Msg msg)
    {
        // 获取或创建用户的人物卡集合
        var userCharacters = characterSkills.GetOrAdd(userId, _ => new ConcurrentDictionary<string, CharacterSheet>());

        string characterName;

        if (!string.IsNullOrEmpty(characterNameFromCommand))
        {
            characterName = characterNameFromCommand;
        }
        else if (CurrentCharacterNames.TryGetValue(userId, out var existingName))
        {
            characterName = existingName;
        }
        else
        {
            // 使用 GetReasonableSenderName 的逻辑获取人物卡名称（skipCurrentCharacter=true 避免总是用现有人物卡）
            // 这样会依次尝试：持久化显示名 → 群名片 → QQ昵称 → QQ ID
            characterName = GetReasonableSenderName(userId, isSimulationMode, skipCurrentCharacter: true);
            
            if (string.IsNullOrEmpty(characterName))
            {
                Log.Error("未能获取有效的人物卡名称。");
                Reply("未能获取有效的人物卡名称，请先使用 .name 设置显示名或确保有群昵称。", msg);
                return null!;
            }
        }

        // 确保对应的 CharacterSheet 存在
        userCharacters.GetOrAdd(characterName, _ => new CharacterSheet());

        // 记录当前角色名称（并发安全：索引器在 ConcurrentDictionary 上会自动执行添加/更新）
        CurrentCharacterNames[userId] = characterName;
        return characterName;
    }

    /// <summary>
    /// 获取指定用户当前选择的人物卡名称，只获取不创建。
    /// </summary>
    /// <param name="userId">用户ID。</param>
    /// <returns>当前人物卡名称，如果没有则返回 null。</returns>
    private string? TryGetCurrentCharacterName(long userId)
    {
        if (userId <= 0)
            return null;

        if (CurrentCharacterNames.TryGetValue(userId, out var characterName))
        {
            // 验证这个人物卡是否真的存在
            if (characterSkills.TryGetValue(userId, out var userCharacters)
                && userCharacters.ContainsKey(characterName))
            {
                return characterName;
            }
        }

        return null;
    }

    /// <summary>
    /// 获取指定用户/人物的 CharacterSheet；不存在则返回 null，不创建。
    /// 供调用方安全地取得技能字典等。
    /// </summary>
    private CharacterSheet? TryGetCharacterSheet(long userId, string characterName)
    {
        if (characterSkills.TryGetValue(userId, out var userCharacters)
            && userCharacters.TryGetValue(characterName, out var sheet))
        {
            return sheet;
        }
        return null;
    }

    /// <summary>
    /// 保存人物技能数据：统一写入 UserData（与显示名、好感度同表）。
    /// </summary>
    private void SaveCharacterSkills()
    {
        // 统一调用 UserData 持久化，避免多表分散
        SaveUserData();
    }

    /// <summary>
    /// 删除指定用户的人物卡
    /// </summary>
    public bool DeleteCharacterCard(long userId, string cardName)
    {
        try
        {
            if (!characterSkills.TryGetValue(userId, out var userCharacters))
            {
                Log.Warn($"[DeleteCharacterCard] 用户 {userId} 没有任何人物卡");
                return false;
            }

            if (!userCharacters.TryRemove(cardName, out _))
            {
                Log.Warn($"[DeleteCharacterCard] 未找到人物卡: {cardName}");
                return false;
            }

            // 如果当前人物卡被删除，需要清除当前选择
            if (CurrentCharacterNames.TryGetValue(userId, out var currentName) && currentName == cardName)
            {
                CurrentCharacterNames.TryRemove(userId, out _);
                Log.InfoFormat($"[DeleteCharacterCard] 已清除用户 {userId} 的当前人物卡选择");
            }

            // 保存到持久化存储
            SaveCharacterSkills();
            Log.InfoFormat($"[DeleteCharacterCard] 人物卡 '{cardName}' 已成功删除 (用户: {userId})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[DeleteCharacterCard] 删除人物卡时出错: {ex.Message}");
            return false;
        }
    }
}