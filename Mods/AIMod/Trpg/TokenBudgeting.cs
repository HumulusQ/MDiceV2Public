using System;
using System.Collections.Generic;
using System.Linq;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// Token Budgeting - 动态 Token 预算
/// 
/// 职责：动态管理 Prompt 的 Token 分配，确保在有限 Token 内提供最相关信息
/// 
/// 核心思想：
/// - 不是堆叠所有内容
/// - 而是选择当前最重要内容
/// 
/// 预算分配策略：
/// - 当前世界状态: 30%
/// - 故事脊柱: 20%
/// - 场景相关事件: 25%
/// - NPC 状态: 15%
/// - 长期伏笔: 10%
/// </summary>
public class TokenBudgeting
{
    private readonly IModContext _context;
    private readonly ChatDatabase _db;
    private readonly EventLog _eventLog;
    private readonly WorldStateProjection _worldStateProjection;
    private readonly HierarchicalTimeline _hierarchicalTimeline;

    public TokenBudgeting(
        IModContext context,
        ChatDatabase db,
        EventLog eventLog,
        WorldStateProjection worldStateProjection,
        HierarchicalTimeline hierarchicalTimeline)
    {
        _context = context;
        _db = db;
        _eventLog = eventLog;
        _worldStateProjection = worldStateProjection;
        _hierarchicalTimeline = hierarchicalTimeline;
    }

    /// <summary>
    /// Token 预算分配
    /// </summary>
    public class TokenBudget
    {
        public int TotalTokens { get; set; } = 4000;
        public int WorldStateTokens { get; set; } = 1200;  // 30%
        public int SceneEventsTokens { get; set; } = 1000; // 25%
        public int NpcStateTokens { get; set; } = 600;     // 15%
        public int ForeshadowTokens { get; set; } = 400;   // 10%
    }

    /// <summary>
    /// 生成预算优化的 Prompt
    /// </summary>
    public async Task<string> GenerateBudgetedPromptAsync(TrpgScope scope, string characterId, TokenBudget? budget = null)
    {
        budget ??= new TokenBudget();
        
        var sb = new System.Text.StringBuilder();
        
        // 1. 当前世界状态 (30%)
        var worldState = await _worldStateProjection.ProjectCurrentStateAsync(scope, characterId);
        sb.AppendLine(TruncateToTokens(worldState.ToPromptString(), budget.WorldStateTokens));

        // 2. 场景相关事件 (25%)
        var sceneEvents = await _eventLog.QueryEventsBySceneAsync(scope, worldState.CurrentSceneId);
        var sceneEventsText = FormatEvents(sceneEvents);
        sb.AppendLine(TruncateToTokens(sceneEventsText, budget.SceneEventsTokens));
        
        // 4. NPC 状态 (15%)
        var npcStateText = FormatNpcStates(scope, worldState.PresentEntities, characterId);
        sb.AppendLine(TruncateToTokens(npcStateText, budget.NpcStateTokens));
        
        // 5. 长期伏笔 (10%)
        var foreshadowText = await FormatForeshadowsAsync(scope, characterId);
        sb.AppendLine(TruncateToTokens(foreshadowText, budget.ForeshadowTokens));
        
        return sb.ToString();
    }

    /// <summary>
    /// 格式化事件列表
    /// </summary>
    private string FormatEvents(List<WorldEvent> events)
    {
        if (events.Count == 0)
            return "无场景事件";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【场景事件】");
        sb.AppendLine("========================");

        foreach (var evt in events.TakeLast(10))
        {
            sb.AppendLine($"[{evt.EventType}] {evt.Result}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化 NPC 状态
    /// </summary>
    private string FormatNpcStates(TrpgScope scope, List<string> presentEntities, string characterId)
    {
        if (presentEntities.Count == 0)
            return "无在场 NPC";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【NPC 状态】");
        sb.AppendLine("========================");

        foreach (var entityId in presentEntities.TakeLast(5))
        {
            var entity = _db.GetEntityCanonicalAsync(scope, entityId).Result;
            if (entity != null)
            {
                sb.AppendLine($"- {entity.CurrentDisplayName} ({entity.IdentityStatus})");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化伏笔
    /// </summary>
    private async Task<string> FormatForeshadowsAsync(TrpgScope scope, string characterId)
    {
        var allEvents = await _eventLog.ReplayEventsAsync(scope, 0, null);
        
        // 查找 foreshadow 类型的事件
        var foreshadowEvents = allEvents
            .Where(e => e.EventType == "discovery" || e.EventType == "objective_change")
            .TakeLast(5)
            .ToList();

        if (foreshadowEvents.Count == 0)
            return "无长期伏笔";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========================");
        sb.AppendLine("【长期伏笔】");
        sb.AppendLine("========================");

        foreach (var evt in foreshadowEvents)
        {
            sb.AppendLine($"- {evt.Result}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 截断到指定 Token 数（简化版：按字符数估算）
    /// </summary>
    private string TruncateToTokens(string text, int maxTokens)
    {
        // 简化估算：1 Token ≈ 4 字符（英文）或 2 字符（中文）
        const int charsPerToken = 3;
        var maxChars = maxTokens * charsPerToken;
        
        if (text.Length <= maxChars)
            return text;

        return text.Substring(0, maxChars) + "... (已截断)";
    }

    /// <summary>
    /// 自适应预算调整
    /// 根据系统负载动态调整预算分配
    /// </summary>
    public TokenBudget AdjustBudget(TokenBudget budget, int systemLoad)
    {
        // systemLoad: 0-100，100 表示高负载
        if (systemLoad > 80)
        {
            // 高负载：减少总 Token
            budget.TotalTokens = 2000;
            budget.WorldStateTokens = 600;
            budget.SceneEventsTokens = 500;
            budget.NpcStateTokens = 300;
            budget.ForeshadowTokens = 200;
        }
        else if (systemLoad > 50)
        {
            // 中等负载：中等 Token
            budget.TotalTokens = 3000;
            budget.WorldStateTokens = 900;
            budget.SceneEventsTokens = 750;
            budget.NpcStateTokens = 450;
            budget.ForeshadowTokens = 300;
        }
        else
        {
            // 低负载：完整 Token
            budget.TotalTokens = 4000;
            budget.WorldStateTokens = 1200;
            budget.SceneEventsTokens = 1000;
            budget.NpcStateTokens = 600;
            budget.ForeshadowTokens = 400;
        }

        return budget;
    }

    /// <summary>
    /// 估算 Prompt 的 Token 数
    /// </summary>
    public int EstimateTokens(string text)
    {
        // 简化估算
        return text.Length / 3;
    }

    /// <summary>
    /// 获取预算使用统计
    /// </summary>
    public BudgetUsageStats GetBudgetUsage(TokenBudget budget, string generatedPrompt)
    {
        var estimatedTokens = EstimateTokens(generatedPrompt);
        
        return new BudgetUsageStats
        {
            TotalBudget = budget.TotalTokens,
            UsedTokens = estimatedTokens,
            Utilization = (double)estimatedTokens / budget.TotalTokens,
            IsOverBudget = estimatedTokens > budget.TotalTokens
        };
    }
}

/// <summary>
/// 预算使用统计
/// </summary>
public class BudgetUsageStats
{
    /// <summary>
    /// 总预算
    /// </summary>
    public int TotalBudget { get; set; }

    /// <summary>
    /// 已使用 Token
    /// </summary>
    public int UsedTokens { get; set; }

    /// <summary>
    /// 利用率
    /// </summary>
    public double Utilization { get; set; }

    /// <summary>
    /// 是否超预算
    /// </summary>
    public bool IsOverBudget { get; set; }
}
