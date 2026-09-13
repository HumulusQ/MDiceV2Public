using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ABot;

/// <summary>
/// 战斗执行器 - 简化的战斗演示工具
/// 
/// 用途：
/// 在ABotPanel UI中快速验证战斗逻辑
/// 
/// 功能：
/// - 解析简单的战斗场景配置
/// - 创建测试角色
/// - 执行战斗模拟
/// - 返回可读的结果报告
/// </summary>
public class BattleExecutor
{
    private ABotInterpreter? _interpreter;
    private List<string> _battleLog = new();

    public BattleExecutor()
    {
        try
        {
            _interpreter = new ABotInterpreter();
        }
        catch (Exception ex)
        {
            _battleLog.Add($"[ERROR] Failed to initialize interpreter: {ex.Message}");
        }
    }

    /// <summary>
    /// 执行战斗演示
    /// 
    /// 输入格式示例：
    /// Hero vs Zombie
    /// Hero: name=Hero, camp=1, hp=100, atk=20, dfs=5, dr=0.1, dmg=10-15-20-25
    /// Zombie: name=Zombie, camp=2, hp=80, atk=15, dfs=3, dr=0, dmg=8-12-16-20
    /// </summary>
    public string ExecuteBattle(string battleDescription)
    {
        _battleLog.Clear();
        
        try
        {
            if (string.IsNullOrEmpty(battleDescription))
            {
                return "[ERROR] Battle description is empty";
            }

            _battleLog.Add("[INFO] ========== BATTLE SIMULATOR ==========");
            _battleLog.Add($"[INFO] Input: {battleDescription.Replace("\n", " | ")}");
            _battleLog.Add("");

            // 解析战斗场景
            if (!ParseBattleDescription(battleDescription, out var characters))
            {
                return string.Join("\n", _battleLog);
            }

            _battleLog.Add("[INFO] ========== BATTLE START ==========");
            _battleLog.Add("");

            // 执行逻辑战斗模拟
            SimulateBattle(characters);

            _battleLog.Add("");
            _battleLog.Add("[INFO] ========== BATTLE RESULT ==========");
            _battleLog.Add("");

            return string.Join("\n", _battleLog);
        }
        catch (Exception ex)
        {
            _battleLog.Add($"[ERROR] Battle execution failed: {ex.Message}");
            return string.Join("\n", _battleLog);
        }
    }

    /// <summary>
    /// 解析战斗场景描述
    /// </summary>
    private bool ParseBattleDescription(string description, out List<CharacterInfo> characters)
    {
        characters = new List<CharacterInfo>();

        try
        {
            var lines = description.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                
                // 跳过空行和标题行
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.Contains("vs"))
                    continue;

                // 解析角色行：Name: param1=value1, param2=value2, ...
                if (trimmedLine.Contains(":"))
                {
                    var parts = trimmedLine.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var charName = parts[0].Trim();
                        var paramString = parts[1].Trim();
                        
                        var character = ParseCharacter(charName, paramString);
                        if (character != null)
                        {
                            characters.Add(character);
                            _battleLog.Add($"[DEBUG] Parsed character: {character.Name} (Camp {character.Camp})");
                        }
                    }
                }
            }

            if (characters.Count < 2)
            {
                _battleLog.Add("[ERROR] At least 2 characters required for battle");
                return false;
            }

            // 验证必须有不同的camp
            var camps = characters.Select(c => c.Camp).Distinct().Count();
            if (camps < 2)
            {
                _battleLog.Add("[ERROR] Characters must belong to different camps");
                return false;
            }

            _battleLog.Add($"[INFO] Loaded {characters.Count} characters in {camps} camps");
            return true;
        }
        catch (Exception ex)
        {
            _battleLog.Add($"[ERROR] Parse failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 解析单个角色参数
    /// </summary>
    private CharacterInfo? ParseCharacter(string name, string paramString)
    {
        try
        {
            var character = new CharacterInfo { Name = name };

            var paramPairs = paramString.Split(',');
            foreach (var pair in paramPairs)
            {
                var trimmed = pair.Trim();
                if (!trimmed.Contains("=")) continue;

                var kv = trimmed.Split('=', 2);
                var key = kv[0].Trim().ToLower();
                var value = kv[1].Trim();

                switch (key)
                {
                    case "name":
                        character.Name = value;
                        break;
                    case "camp":
                        character.Camp = int.TryParse(value, out var camp) ? camp : 1;
                        break;
                    case "hp":
                        character.HP = int.TryParse(value, out var hp) ? hp : 100;
                        break;
                    case "atk":
                        character.ATK = int.TryParse(value, out var atk) ? atk : 10;
                        break;
                    case "dfs":
                        character.DFS = int.TryParse(value, out var dfs) ? dfs : 0;
                        break;
                    case "dr":
                        character.DR = float.TryParse(value, out var dr) ? dr : 0.0f;
                        break;
                    case "aggro":
                        character.Aggro = int.TryParse(value, out var aggro) ? aggro : 1;
                        break;
                    case "dmg":
                        // 格式：min-low-high-max
                        var dmgParts = value.Split('-');
                        if (dmgParts.Length == 4)
                        {
                            character.DmgMin = int.TryParse(dmgParts[0], out var d1) ? d1 : 5;
                            character.DmgLow = int.TryParse(dmgParts[1], out var d2) ? d2 : 10;
                            character.DmgHigh = int.TryParse(dmgParts[2], out var d3) ? d3 : 15;
                            character.DmgMax = int.TryParse(dmgParts[3], out var d4) ? d4 : 20;
                        }
                        break;
                }
            }

            return character;
        }
        catch (Exception ex)
        {
            _battleLog.Add($"[ERROR] Failed to parse character {name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 模拟战斗
    /// </summary>
    private void SimulateBattle(List<CharacterInfo> characters)
    {
        Random rand = new Random();
        int round = 0;
        const int maxRounds = 100;

        while (round < maxRounds && IsGameActive(characters))
        {
            round++;
            _battleLog.Add($"[ROUND {round}]");

            // 选择行动者（ATK最高）
            var actor = characters.Where(c => c.IsAlive).OrderByDescending(c => c.ATK).FirstOrDefault();
            if (actor == null) break;

            // 选择目标（敌方且活着）
            var targets = characters.Where(c => c.IsAlive && c.Camp != actor.Camp).ToList();
            if (targets.Count == 0) break;

            // 加权随机选择（基于aggro）
            var totalWeight = targets.Sum(t => Math.Max(1, t.Aggro));
            var roll = rand.Next(totalWeight);
            int accumulated = 0;
            CharacterInfo? target = null;

            foreach (var t in targets)
            {
                accumulated += Math.Max(1, t.Aggro);
                if (roll < accumulated)
                {
                    target = t;
                    break;
                }
            }

            if (target == null) target = targets[0];

            // 计算伤害
            int damageIndex = rand.Next(4);
            int[] damages = { actor.DmgMin, actor.DmgLow, actor.DmgHigh, actor.DmgMax };
            int baseDamage = damages[damageIndex];
            int afterArmor = Math.Max(0, baseDamage - actor.DFS);
            int finalDamage = (int)(afterArmor * (1.0f - target.DR));
            finalDamage = Math.Max(0, finalDamage);

            target.HP -= finalDamage;
            if (target.HP < 0) target.HP = 0;

            _battleLog.Add($"  {actor.Name} attacks {target.Name}");
            _battleLog.Add($"    Base DMG: {baseDamage}, After Armor: {afterArmor}, Final: {finalDamage}");
            _battleLog.Add($"    {target.Name} HP: {target.HP + finalDamage} → {target.HP}");

            if (target.HP <= 0)
            {
                target.IsAlive = false;
                _battleLog.Add($"    *** {target.Name} defeated! ***");
            }

            _battleLog.Add("");
        }

        _battleLog.Add("[INFO] Battle ended");
        _battleLog.Add("");

        // 显示最终结果
        var survivors = characters.Where(c => c.IsAlive).ToList();
        if (survivors.Count > 0)
        {
            var victorCamp = survivors.First().Camp;
            _battleLog.Add($"[VICTORY] Camp {victorCamp} wins!");
            foreach (var survivor in survivors)
            {
                _battleLog.Add($"  {survivor.Name}: HP = {survivor.HP}");
            }
        }
        else
        {
            _battleLog.Add("[DRAW] All characters defeated!");
        }
    }

    private bool IsGameActive(List<CharacterInfo> characters)
    {
        var aliveCamps = characters.Where(c => c.IsAlive).Select(c => c.Camp).Distinct().Count();
        return aliveCamps >= 2;
    }

    /// <summary>
    /// 内部角色信息结构
    /// </summary>
    internal class CharacterInfo
    {
        public string Name { get; set; } = "Unknown";
        public int Camp { get; set; } = 1;
        public int HP { get; set; } = 100;
        public int ATK { get; set; } = 10;
        public int DFS { get; set; } = 0;
        public float DR { get; set; } = 0.0f;
        public int Aggro { get; set; } = 1;
        public int DmgMin { get; set; } = 5;
        public int DmgLow { get; set; } = 10;
        public int DmgHigh { get; set; } = 15;
        public int DmgMax { get; set; } = 20;
        public bool IsAlive { get; set; } = true;
    }

    /// <summary>
    /// 清理资源
    /// </summary>
    public void Dispose()
    {
        _interpreter?.Dispose();
    }
}
