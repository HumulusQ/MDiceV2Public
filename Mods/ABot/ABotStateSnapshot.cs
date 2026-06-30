using System;
using System.Collections.Generic;

namespace ABot;

/// <summary>
/// ABot 游戏状态快照
/// 
/// 用途：
/// ====
/// 1. 保存用户离线时的完整游戏状态
/// 2. 用于 LRU 驱逐时的持久化
/// 3. 支持状态恢复（数据库读取后重新加载）
/// 
/// 内容包含：
/// =========
/// - 角色信息（基本属性、技能、状态）
/// - 回合管理器状态（当前回合、战斗日志、状态）
/// - 时间戳和元数据
/// 
/// 序列化格式：
/// ===========
/// 阶段 3：JSON（方便调试和传输）
/// 阶段 4：二进制（性能和存储优化）
/// 阶段 5：SQLite 存储
/// </summary>
public class ABotStateSnapshot
{
    /// <summary>
    /// 快照创建时的时间戳
    /// 用于追踪状态的年代
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 用户 ID（用于快照识别）
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// 角色基本信息（JSON 格式）
    /// 例如："{\"name\":\"Hero\",\"hp\":100,\"mp\":50}"
    /// 从 abot_get_character_basic_info() 导出
    /// 【已弃用】改用 Characters 数组以支持多角色战斗
    /// </summary>
    [Obsolete("Use Characters array instead for multi-character battles")]
    public string? CharacterBasicInfo { get; set; }

    /// <summary>
    /// 【新增】所有参战角色的 JSON 数组
    /// 用于保存多角色战斗场景中的所有参与者
    /// 格式：[{角色1完整数据}, {角色2完整数据}, ...]
    /// 从 abot_serialize_all_characters_json() 导出
    /// 例如：[{"name":"Hero1","camp":1,"hp":100,...},{"name":"Hero2","camp":2,"hp":80,...}]
    /// </summary>
    public string? Characters { get; set; }

    /// <summary>
    /// 角色技能信息（JSON 格式）
    /// 例如："{\"skills\":[{\"name\":\"slash\",\"power\":10}]}"
    /// 从 abot_get_character_skills_info() 导出
    /// </summary>
    [Obsolete("Skills are now included in Characters array")]
    public string? CharacterSkillsInfo { get; set; }

    /// <summary>
    /// 角色状态信息（JSON 格式）
    /// 例如："{\"states\":[{\"name\":\"poison\",\"duration\":3}]}"
    /// 从 abot_get_character_states_info() 导出
    /// </summary>
    [Obsolete("States are now included in Characters array")]
    public string? CharacterStatesInfo { get; set; }

    /// <summary>
    /// 回合管理器状态（JSON 格式）
    /// 包含：当前回合数、是否运行、是否完成等
    /// 从 abot_round_manager_get_status() 导出
    /// 例如："{\"currentRound\":5,\"isRunning\":false,\"isFinished\":true}"
    /// </summary>
    public string? RoundManagerStatus { get; set; }

    /// <summary>
    /// 回合执行日志（JSON 格式）
    /// 记录所有回合的执行过程、伤害值、技能触发等
    /// 从 abot_round_manager_get_log() 导出
    /// </summary>
    public string? RoundManagerLog { get; set; }

    /// <summary>
    /// 技能触发日志（JSON 格式）
    /// 详细的技能触发事件记录
    /// 从 abot_round_manager_get_skill_trigger_log() 导出
    /// </summary>
    public string? SkillTriggerLog { get; set; }

    /// <summary>
    /// 最后的错误信息（如果导出时发生错误）
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// 导出时的版本号（用于兼容性检查）
    /// </summary>
    public string? ABotVersion { get; set; }

    /// <summary>
    /// 快照大小估算（字节）
    /// 用于监控内存使用
    /// </summary>
    public int EstimatedSizeBytes => CalculateSize();

    // ============ 方法 ============

    /// <summary>
    /// 计算快照的内存占用
    /// </summary>
    private int CalculateSize()
    {
        int size = 0;
        size += CharacterBasicInfo?.Length * 2 ?? 0;
        size += Characters?.Length * 2 ?? 0;  // 新增：Characters 数组
        size += CharacterSkillsInfo?.Length * 2 ?? 0;
        size += CharacterStatesInfo?.Length * 2 ?? 0;
        size += RoundManagerStatus?.Length * 2 ?? 0;
        size += RoundManagerLog?.Length * 2 ?? 0;
        size += SkillTriggerLog?.Length * 2 ?? 0;
        size += LastError?.Length * 2 ?? 0;
        size += ABotVersion?.Length * 2 ?? 0;
        // 加上固定元数据大小
        size += 100;
        return size;
    }

    /// <summary>
    /// 快照是否有效（至少包含用户ID和创建时间）
    /// </summary>
    public bool IsValid => UserId > 0;

    /// <summary>
    /// 用于调试的快照摘要
    /// </summary>
    public override string ToString()
    {
        return $"ABotStateSnapshot(UserId={UserId}, CreatedAt={CreatedAt:yyyy-MM-dd HH:mm:ss}, " +
               $"BasicInfo={(!string.IsNullOrEmpty(CharacterBasicInfo) ? "✓" : "✗")}, " +
               $"RoundLog={(!string.IsNullOrEmpty(RoundManagerLog) ? "✓" : "✗")}, " +
               $"Size≈{EstimatedSizeBytes / 1024}KB)";
    }
}
