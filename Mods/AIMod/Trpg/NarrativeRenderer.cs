using System;
using System.Collections.Generic;
using System.Linq;

namespace AIMod.Trpg;

/// <summary>
/// 叙事渲染器（Narrative Renderer）
/// 
/// 职责：将 Canonical Event 转换为 Narrative Sentence
/// 
/// 设计原则：
/// - 隐藏系统内部结构（EventId, EventType）
/// - 输出叙事本体而非机器本体
/// - 使用自然语言而非技术术语
/// - 支持半模板化渲染，无需全 LLM
/// </summary>
public interface INarrativeRenderer
{
    /// <summary>
    /// 判断是否支持该事件类型
    /// </summary>
    bool CanRender(string eventType);

    /// <summary>
    /// 将事件渲染为叙事句子
    /// </summary>
    string Render(WorldEvent evt);
}

/// <summary>
/// 叙事渲染器注册表
/// </summary>
public class NarrativeRendererRegistry
{
    private readonly List<INarrativeRenderer> _renderers = new();

    public NarrativeRendererRegistry()
    {
        RegisterDefaultRenderers();
    }

    /// <summary>
    /// 注册自定义渲染器
    /// </summary>
    public void RegisterRenderer(INarrativeRenderer renderer)
    {
        _renderers.Add(renderer);
    }

    /// <summary>
    /// 渲染事件为叙事句子
    /// </summary>
    public string RenderEvent(WorldEvent evt)
    {
        // 优先使用语义摘要（如果已蒸馏）
        if (evt.IsSemanticallyDistilled && !string.IsNullOrEmpty(evt.SemanticSummary))
            return evt.SemanticSummary;

        // 降级到模板渲染
        var renderer = _renderers.FirstOrDefault(r => r.CanRender(evt.EventType));
        if (renderer != null)
            return renderer.Render(evt);

        // 最终降级：简单描述
        return RenderFallback(evt);
    }

    /// <summary>
    /// 计算事件的叙事得分（用于排序）
    /// </summary>
    public double CalculateNarrativeScore(WorldEvent evt, DateTime currentTime)
    {
        // recency: 时间衰减（越近越高）
        var hoursSince = (currentTime - evt.Timestamp).TotalHours;
        var recency = Math.Exp(-hoursSince / 24.0); // 24小时半衰期

        // narrativeWeight: 叙事权重（已蒸馏则使用，否则默认中等）
        var narrativeWeight = evt.IsSemanticallyDistilled ? evt.NarrativeWeight : 0.5;

        // emotionalWeight: 情绪强度（绝对值）
        var emotionalImpact = Math.Abs(evt.EmotionalWeight);

        // 综合得分
        return recency * 0.3 + narrativeWeight * 0.5 + emotionalImpact * 0.2;
    }

    private void RegisterDefaultRenderers()
    {
        _renderers.Add(new SceneTransitionRenderer());
        _renderers.Add(new RelationshipChangeRenderer());
        _renderers.Add(new NpcDeathRenderer());
        _renderers.Add(new ObjectiveCompleteRenderer());
        _renderers.Add(new ObjectiveUpdateRenderer());
        _renderers.Add(new CombatRenderer());
        _renderers.Add(new DiscoveryRenderer());
        _renderers.Add(new BetrayalRenderer());
        _renderers.Add(new FactionShiftRenderer());
        _renderers.Add(new DialogueRenderer());
    }

    private string RenderFallback(WorldEvent evt)
    {
        var source = !string.IsNullOrEmpty(evt.SourceEntityId) ? evt.SourceEntityId : "某人";
        var location = !string.IsNullOrEmpty(evt.Location) ? $"在{evt.Location}" : "";

        if (!string.IsNullOrEmpty(evt.Result))
            return $"{source}{location}发生了{evt.Result}";

        return $"{source}{location}执行了{evt.EventType}";
    }
}

// ═══════════════════════════════════════════
//  具体渲染器实现
// ═══════════════════════════════════════════

/// <summary>
/// 场景切换渲染器
/// </summary>
public class SceneTransitionRenderer : INarrativeRenderer
{
    public bool CanRender(string eventType) => eventType.Equals("scene_transition", StringComparison.OrdinalIgnoreCase);

    public string Render(WorldEvent evt)
    {
        var sceneName = ExtractSceneName(evt);
        var actors = FormatActors(evt.Actors);

        if (string.IsNullOrEmpty(sceneName))
            return $"{actors}转移到了新地点";

        return $"{actors}抵达{sceneName}";
    }

    private string ExtractSceneName(WorldEvent evt)
    {
        if (evt.Payload.TryGetValue("scene_name", out var name) && name != null)
            return name.ToString() ?? "";

        if (!string.IsNullOrEmpty(evt.Location))
            return evt.Location;

        return "";
    }

    private string FormatActors(List<string> actors)
    {
        if (actors.Count == 0) return "队伍";
        if (actors.Count == 1) return actors[0];
        if (actors.Count == 2) return $"{actors[0]}和{actors[1]}";
        return $"{string.Join("、", actors.Take(actors.Count - 1))}等人";
    }
}

/// <summary>
/// 关系变化渲染器
/// </summary>
public class RelationshipChangeRenderer : INarrativeRenderer
{
    public bool CanRender(string eventType) => eventType.Equals("relationship_change", StringComparison.OrdinalIgnoreCase);

    public string Render(WorldEvent evt)
    {
        var source = evt.SourceEntityId ?? "某人";
        var target = evt.TargetEntityId ?? "某人";
        var change = ExtractChange(evt);

        if (change > 0)
            return $"{source}对{target}的态度有所改善";
        if (change < 0)
            return $"{source}对{target}的态度明显恶化";

        return $"{source}与{target}的关系发生变化";
    }

    private int ExtractChange(WorldEvent evt)
    {
        if (evt.Payload.TryGetValue("change", out var changeObj) && int.TryParse(changeObj?.ToString(), out var change))
            return change;

        return 0;
    }
}

/// <summary>
/// NPC 死亡渲染器
/// </summary>
public class NpcDeathRenderer : INarrativeRenderer
{
    public bool CanRender(string eventType) => eventType.Equals("npc_death", StringComparison.OrdinalIgnoreCase);

    public string Render(WorldEvent evt)
    {
        var target = evt.TargetEntityId ?? "某人";
        var location = !string.IsNullOrEmpty(evt.Location) ? $"在{evt.Location}" : "";
        var cause = ExtractCause(evt);

        if (!string.IsNullOrEmpty(cause))
            return $"{target}{location}因{cause}而死亡";

        return $"{target}{location}死亡";
    }

    private string ExtractCause(WorldEvent evt)
    {
        if (evt.Payload.TryGetValue("cause", out var cause) && cause != null)
            return cause.ToString() ?? "";

        return "";
    }
}

/// <summary>
/// 目标完成渲染器
/// </summary>
public class ObjectiveCompleteRenderer : INarrativeRenderer
{
    public bool CanRender(string eventType) => eventType.Equals("objective_complete", StringComparison.OrdinalIgnoreCase);

    public string Render(WorldEvent evt)
    {
        var objective = ExtractObjective(evt);
        var actors = FormatActors(evt.Actors);

        if (!string.IsNullOrEmpty(objective))
            return $"{actors}完成了{objective}";

        return $"{actors}达成了某个目标";
    }

    private string ExtractObjective(WorldEvent evt)
    {
        if (evt.Payload.TryGetValue("objective", out var obj) && obj != null)
            return obj.ToString() ?? "";

        if (!string.IsNullOrEmpty(evt.Result))
            return evt.Result;

        return "";
    }

    private string FormatActors(List<string> actors)
    {
        if (actors.Count == 0) return "队伍";
        if (actors.Count == 1) return actors[0];
        return $"{string.Join("、", actors)}";
    }
}

/// <summary>
/// 目标更新渲染器
/// </summary>
public class ObjectiveUpdateRenderer : INarrativeRenderer
{
    public bool CanRender(string eventType) => eventType.Equals("objective_update", StringComparison.OrdinalIgnoreCase);

    public string Render(WorldEvent evt)
    {
        var objective = ExtractObjective(evt);
        var actors = FormatActors(evt.Actors);

        if (!string.IsNullOrEmpty(objective))
            return $"{actors}接到了新任务：{objective}";

        return $"{actors}的目标发生了变化";
    }

    private string ExtractObjective(WorldEvent evt)
    {
        if (evt.Payload.TryGetValue("objective", out var obj) && obj != null)
            return obj.ToString() ?? "";

        if (!string.IsNullOrEmpty(evt.Result))
            return evt.Result;

        return "";
    }

    private string FormatActors(List<string> actors)
    {
        if (actors.Count == 0) return "队伍";
        if (actors.Count == 1) return actors[0];
        return $"{string.Join("、", actors)}";
    }
}

/// <summary>
/// 战斗渲染器
/// </summary>
public class CombatRenderer : INarrativeRenderer
{
    public bool CanRender(string eventType) => eventType.Equals("combat", StringComparison.OrdinalIgnoreCase);

    public string Render(WorldEvent evt)
    {
        var location = !string.IsNullOrEmpty(evt.Location) ? $"在{evt.Location}" : "";
        var result = ExtractResult(evt);

        if (!string.IsNullOrEmpty(result))
            return $"发生了一场战斗{location}，{result}";

        return $"发生了一场战斗{location}";
    }

    private string ExtractResult(WorldEvent evt)
    {
        if (evt.Payload.TryGetValue("result", out var res) && res != null)
            return res.ToString() ?? "";

        if (!string.IsNullOrEmpty(evt.Result))
            return evt.Result;

        return "";
    }
}

/// <summary>
/// 发现渲染器
/// </summary>
public class DiscoveryRenderer : INarrativeRenderer
{
    public bool CanRender(string eventType) => eventType.Equals("discovery", StringComparison.OrdinalIgnoreCase);

    public string Render(WorldEvent evt)
    {
        var actors = FormatActors(evt.Actors);
        var discovered = ExtractDiscovered(evt);

        if (!string.IsNullOrEmpty(discovered))
            return $"{actors}发现了{discovered}";

        return $"{actors}有了新的发现";
    }

    private string ExtractDiscovered(WorldEvent evt)
    {
        if (evt.Payload.TryGetValue("discovered", out var disc) && disc != null)
            return disc.ToString() ?? "";

        if (!string.IsNullOrEmpty(evt.Result))
            return evt.Result;

        return "";
    }

    private string FormatActors(List<string> actors)
    {
        if (actors.Count == 0) return "队伍";
        if (actors.Count == 1) return actors[0];
        return $"{string.Join("、", actors)}";
    }
}

/// <summary>
/// 背叛渲染器
/// </summary>
public class BetrayalRenderer : INarrativeRenderer
{
    public bool CanRender(string eventType) => eventType.Equals("betrayal", StringComparison.OrdinalIgnoreCase);

    public string Render(WorldEvent evt)
    {
        var source = evt.SourceEntityId ?? "某人";
        var target = evt.TargetEntityId ?? "某人";

        return $"{source}背叛了{target}";
    }
}

/// <summary>
/// 阵营变化渲染器
/// </summary>
public class FactionShiftRenderer : INarrativeRenderer
{
    public bool CanRender(string eventType) => eventType.Equals("faction_shift", StringComparison.OrdinalIgnoreCase);

    public string Render(WorldEvent evt)
    {
        var entity = evt.SourceEntityId ?? "某人";
        var fromFaction = ExtractFromFaction(evt);
        var toFaction = ExtractToFaction(evt);

        if (!string.IsNullOrEmpty(fromFaction) && !string.IsNullOrEmpty(toFaction))
            return $"{entity}从{fromFaction}转投{toFaction}";

        if (!string.IsNullOrEmpty(toFaction))
            return $"{entity}加入了{toFaction}";

        return $"{entity}的阵营发生了变化";
    }

    private string ExtractFromFaction(WorldEvent evt)
    {
        if (evt.Payload.TryGetValue("from_faction", out var from) && from != null)
            return from.ToString() ?? "";

        return "";
    }

    private string ExtractToFaction(WorldEvent evt)
    {
        if (evt.Payload.TryGetValue("to_faction", out var to) && to != null)
            return to.ToString() ?? "";

        return "";
    }
}

/// <summary>
/// 对话渲染器
/// </summary>
public class DialogueRenderer : INarrativeRenderer
{
    public bool CanRender(string eventType) => eventType.Equals("dialogue", StringComparison.OrdinalIgnoreCase);

    public string Render(WorldEvent evt)
    {
        var actors = FormatActors(evt.Actors);
        var topic = ExtractTopic(evt);

        if (!string.IsNullOrEmpty(topic))
            return $"{actors}讨论了{topic}";

        return $"{actors}进行了对话";
    }

    private string ExtractTopic(WorldEvent evt)
    {
        if (evt.Payload.TryGetValue("topic", out var topic) && topic != null)
            return topic.ToString() ?? "";

        if (!string.IsNullOrEmpty(evt.Result))
            return evt.Result;

        return "";
    }

    private string FormatActors(List<string> actors)
    {
        if (actors.Count == 0) return "某人";
        if (actors.Count == 1) return actors[0];
        if (actors.Count == 2) return $"{actors[0]}与{actors[1]}";
        return $"{string.Join("、", actors)}";
    }
}
