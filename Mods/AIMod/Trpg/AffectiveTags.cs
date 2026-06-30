using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

public class AffectiveTagCandidate
{
    public string TagType { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string? TargetEntityId { get; set; }
    public string IntensityTier { get; set; } = "Mild";
    public string EffectKind { get; set; } = "ApplyOrRefresh";
    public string StackPolicyHint { get; set; } = "";
    public string Novelty { get; set; } = "Medium";
    public string Evidence { get; set; } = "";
    public string Reason { get; set; } = "";
}

public class AffectiveTagState
{
    public long Id { get; set; }
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public string TagType { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string? TargetEntityId { get; set; }
    public string IntensityTier { get; set; } = "Mild";
    public double Charge { get; set; }
    public double ChargeCap { get; set; } = 1.0;
    public int RepetitionCount { get; set; }
    public double AdaptationLevel { get; set; }
    public string Status { get; set; } = "Active";
    public string LastEvidence { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int LastAppliedFoldCount { get; set; }
    public string ExpirePolicy { get; set; } = "Scene";
    public string Metadata { get; set; } = "{}";
}

public class AffectiveTagEvent
{
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public long? SourceEventId { get; set; }
    public string TagType { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string? TargetEntityId { get; set; }
    public string EffectKind { get; set; } = "ApplyOrRefresh";
    public string IntensityTier { get; set; } = "Mild";
    public string Novelty { get; set; } = "Medium";
    public string Evidence { get; set; } = "";
    public int FoldCount { get; set; }
    public string Metadata { get; set; } = "{}";
}

internal sealed class AffectiveTagDefinition
{
    public string TagType { get; init; } = "";
    public string DefaultDisplayName { get; init; } = "";
    public string StackPolicy { get; init; } = "Saturating";
    public string ExpirePolicy { get; init; } = "Scene";
    public double ChargeCap { get; init; } = 1.0;
    public string ExpressionHints { get; init; } = "";
    public string ProhibitedExpressions { get; init; } = "";
}

public class AffectiveTagController
{
    private static readonly Dictionary<string, AffectiveTagDefinition> Registry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fear.Ambient"] = new() { TagType = "Fear.Ambient", DefaultDisplayName = "ambient unease", StackPolicy = "RefreshOnly", ExpirePolicy = "Scene", ChargeCap = 0.45, ExpressionHints = "low vigilance, shorter sentences, scanning the environment", ProhibitedExpressions = "panic, screaming, collapse without a new direct threat" },
        ["Fear.DirectThreat"] = new() { TagType = "Fear.DirectThreat", DefaultDisplayName = "direct threat fear", StackPolicy = "Saturating", ExpirePolicy = "Scene", ChargeCap = 0.85, ExpressionHints = "protective posture, urgent attention, tactical caution", ProhibitedExpressions = "omniscient certainty about unknown causes" },
        ["Fear.Shock"] = new() { TagType = "Fear.Shock", DefaultDisplayName = "shock", StackPolicy = "Escalating", ExpirePolicy = "Scene", ChargeCap = 1.0, ExpressionHints = "brief startle, hesitation, disrupted rhythm", ProhibitedExpressions = "long-term trauma unless later encoded as memory" },
        ["Alertness.EnvironmentalThreat"] = new() { TagType = "Alertness.EnvironmentalThreat", DefaultDisplayName = "environmental alertness", StackPolicy = "Saturating", ExpirePolicy = "Scene", ChargeCap = 0.65, ExpressionHints = "checks exits, listens carefully, asks concrete questions", ProhibitedExpressions = "inventing threats" },
        ["Trust.Damage"] = new() { TagType = "Trust.Damage", DefaultDisplayName = "residual distrust", StackPolicy = "Escalating", ExpirePolicy = "Relationship", ChargeCap = 1.0, ExpressionHints = "reserved tone, verification, guarded questions", ProhibitedExpressions = "declaring betrayal as fact without confirmed memory" },
        ["Suspicion.Entity"] = new() { TagType = "Suspicion.Entity", DefaultDisplayName = "entity suspicion", StackPolicy = "Saturating", ExpirePolicy = "Relationship", ChargeCap = 0.85, ExpressionHints = "keeps options open, asks follow-up questions", ProhibitedExpressions = "presenting suspicion as confirmed fact" },
        ["Anger.Suppressed"] = new() { TagType = "Anger.Suppressed", DefaultDisplayName = "suppressed anger", StackPolicy = "Saturating", ExpirePolicy = "Scene", ChargeCap = 0.75, ExpressionHints = "controlled words, cold phrasing, clipped replies", ProhibitedExpressions = "sudden violence without permission or escalation" },
        ["Anger.Open"] = new() { TagType = "Anger.Open", DefaultDisplayName = "open anger", StackPolicy = "Escalating", ExpirePolicy = "Scene", ChargeCap = 1.0, ExpressionHints = "direct confrontation, raised intensity", ProhibitedExpressions = "overriding explicit character constraints" },
        ["Sadness.Loss"] = new() { TagType = "Sadness.Loss", DefaultDisplayName = "loss sadness", StackPolicy = "Saturating", ExpirePolicy = "Arc", ChargeCap = 0.9, ExpressionHints = "quiet focus, lowered energy, reluctance", ProhibitedExpressions = "forgetting the objective" },
        ["Shame.Exposed"] = new() { TagType = "Shame.Exposed", DefaultDisplayName = "exposure shame", StackPolicy = "Saturating", ExpirePolicy = "Scene", ChargeCap = 0.7, ExpressionHints = "deflection, self-protection, guardedness", ProhibitedExpressions = "confessing hidden facts not in memory" },
        ["Affection.Warmth"] = new() { TagType = "Affection.Warmth", DefaultDisplayName = "warm affection", StackPolicy = "RefreshOnly", ExpirePolicy = "Relationship", ChargeCap = 0.65, ExpressionHints = "softened tone, patience, protective attention", ProhibitedExpressions = "instant trust reversal" },
        ["Stress.Pressure"] = new() { TagType = "Stress.Pressure", DefaultDisplayName = "pressure stress", StackPolicy = "Saturating", ExpirePolicy = "Scene", ChargeCap = 0.75, ExpressionHints = "prioritizes, compresses speech, seeks clarity", ProhibitedExpressions = "panic from repeated ambience alone" },
        ["NeedForReassurance"] = new() { TagType = "NeedForReassurance", DefaultDisplayName = "need for reassurance", StackPolicy = "RefreshOnly", ExpirePolicy = "Scene", ChargeCap = 0.55, ExpressionHints = "asks for confirmation, seeks steadiness", ProhibitedExpressions = "helplessness unless character profile supports it" },
        ["CombatReadiness"] = new() { TagType = "CombatReadiness", DefaultDisplayName = "combat readiness", StackPolicy = "RefreshOnly", ExpirePolicy = "Scene", ChargeCap = 0.7, ExpressionHints = "positions carefully, watches hands and exits", ProhibitedExpressions = "attacking without cause" }
    };

    private readonly ChatDatabase _db;
    private readonly IModContext _context;

    public AffectiveTagController(ChatDatabase db, IModContext context)
    {
        _db = db;
        _context = context;
    }

    public async System.Threading.Tasks.Task ProcessCandidatesAsync(
        TrpgScope scope,
        string characterId,
        IEnumerable<AffectiveTagCandidate> candidates,
        long? sourceEventId = null)
    {
        var groupId = scope.GroupId;
        var foldCount = await _db.GetCurrentFoldCountAsync(scope, characterId);
        foreach (var raw in candidates)
        {
            var candidate = NormalizeCandidate(raw);
            if (candidate == null)
            {
                _context.Log(LogLevel.Debug, $"[AIMod:TRPG] Affective tag ignored: unknown tag {raw.TagType}/{raw.SourceKey}");
                continue;
            }

            await _db.InsertAffectiveTagEventAsync(scope, new AffectiveTagEvent
            {
                WorldId = scope.WorldId,
                GroupId = groupId,
                CharacterId = characterId,
                SourceEventId = sourceEventId,
                TagType = candidate.TagType,
                DisplayName = candidate.DisplayName,
                SourceKey = candidate.SourceKey,
                TargetEntityId = candidate.TargetEntityId,
                EffectKind = candidate.EffectKind,
                IntensityTier = candidate.IntensityTier,
                Novelty = candidate.Novelty,
                Evidence = candidate.Evidence,
                FoldCount = foldCount
            });

            var existing = await _db.FindAffectiveTagStateAsync(scope, characterId, candidate.TagType, candidate.SourceKey, candidate.TargetEntityId);
            var updated = Apply(existing, candidate, foldCount);
            updated.WorldId = scope.WorldId;
            updated.GroupId = groupId;
            updated.CharacterId = characterId;
            await _db.UpsertAffectiveTagStateAsync(scope, updated);
            _context.Log(LogLevel.Info, $"[AIMod:TRPG] Affective tag {candidate.EffectKind}: {candidate.TagType}/{candidate.SourceKey} => {updated.IntensityTier}, charge={updated.Charge:F2}, repeat={updated.RepetitionCount}");
        }
    }

    public async System.Threading.Tasks.Task DecayStatesAsync(
        TrpgScope scope,
        string characterId,
        bool sceneChanged = false,
        int? currentFoldCount = null)
    {
        var foldCount = currentFoldCount ?? await _db.GetCurrentFoldCountAsync(scope, characterId);
        var states = await _db.GetAffectiveTagStatesAsync(scope, characterId, 64);

        foreach (var state in states)
        {
            if (state.Status is "Expired" or "Resolved")
                continue;

            var policy = string.IsNullOrWhiteSpace(state.ExpirePolicy) ? "Default" : state.ExpirePolicy.Trim();
            var foldsSinceApplied = currentFoldCount.HasValue
                ? Math.Max(0, foldCount - state.LastAppliedFoldCount)
                : 0;
            var wasActive = string.Equals(state.Status, "Active", StringComparison.OrdinalIgnoreCase);
            var wasFading = string.Equals(state.Status, "Fading", StringComparison.OrdinalIgnoreCase);
            var decayAmount = CalculateDecayAmount(policy, sceneChanged, foldsSinceApplied);

            if (decayAmount <= 0 && !(sceneChanged && policy.Equals("Scene", StringComparison.OrdinalIgnoreCase)))
                continue;

            state.Charge = Math.Max(0, state.Charge - decayAmount);
            if (currentFoldCount.HasValue && foldsSinceApplied > 0)
                state.LastAppliedFoldCount = foldCount;

            if (policy.Equals("Scene", StringComparison.OrdinalIgnoreCase))
            {
                if (sceneChanged)
                {
                    if (wasFading)
                        state.Status = "Expired";
                    else if (wasActive)
                        state.Status = "Fading";
                }
                else if (wasFading && state.Charge < 0.15)
                {
                    state.Status = "Expired";
                }
            }
            else if (policy.Equals("Relationship", StringComparison.OrdinalIgnoreCase))
            {
                if (state.Charge <= 0.05 && wasFading)
                    state.Status = "Expired";
                else if (state.Charge < 0.10)
                    state.Status = "Fading";
            }
            else if (policy.Equals("Arc", StringComparison.OrdinalIgnoreCase))
            {
                if (state.Charge <= 0.03 && wasFading)
                    state.Status = "Expired";
                else if (state.Charge <= 0.05)
                    state.Status = "Fading";
            }
            else
            {
                if (state.Charge <= 0.05)
                    state.Status = "Expired";
                else if (state.Charge < 0.15)
                    state.Status = "Fading";
            }

            state.IntensityTier = ChargeToTier(state.Charge);
            state.UpdatedAt = DateTime.UtcNow;
            await _db.UpdateAffectiveTagStateAsync(scope, state);
        }

        var active = states.Count(s => string.Equals(s.Status, "Active", StringComparison.OrdinalIgnoreCase));
        var fading = states.Count(s => string.Equals(s.Status, "Fading", StringComparison.OrdinalIgnoreCase));
        var expired = states.Count(s => string.Equals(s.Status, "Expired", StringComparison.OrdinalIgnoreCase));
        _context.Log(LogLevel.Info, $"[AIMod:TRPG] Affective decay: active={active}, fading={fading}, expired={expired}");
    }

    public static string FormatForPrompt(IEnumerable<AffectiveTagState> states)
    {
        var active = states
            .Where(s => string.Equals(s.Status, "Active", StringComparison.OrdinalIgnoreCase) || string.Equals(s.Status, "Fading", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Charge)
            .ThenByDescending(s => s.UpdatedAt)
            .Take(8)
            .ToList();

        if (active.Count == 0)
            return "";

        var sb = new StringBuilder();
        foreach (var state in active)
        {
            var target = string.IsNullOrWhiteSpace(state.TargetEntityId) ? "" : $"，对象={state.TargetEntityId}";
            var adaptation = state.AdaptationLevel > 0.2 ? "，重复来源已适应" : "";
            var fading = string.Equals(state.Status, "Fading", StringComparison.OrdinalIgnoreCase) ? "，状态=弱化" : "";
            sb.AppendLine($"- {state.DisplayName}（{state.TagType}/{state.IntensityTier}{fading}）：来源={state.SourceKey}{target}{adaptation}；表现={PromptHintFor(state.TagType)}；限制={PromptLimitFor(state.TagType)}。");
        }
        return sb.ToString().TrimEnd();
    }

    private static AffectiveTagCandidate? NormalizeCandidate(AffectiveTagCandidate raw)
    {
        if (raw == null || string.IsNullOrWhiteSpace(raw.TagType))
            return null;

        if (!Registry.TryGetValue(raw.TagType.Trim(), out var def))
            return null;

        var sourceKey = string.IsNullOrWhiteSpace(raw.SourceKey)
            ? BuildFallbackSourceKey(def.TagType, raw.TargetEntityId, raw.Evidence, raw.DisplayName)
            : raw.SourceKey.Trim();

        return new AffectiveTagCandidate
        {
            TagType = def.TagType,
            DisplayName = string.IsNullOrWhiteSpace(raw.DisplayName) ? def.DefaultDisplayName : raw.DisplayName.Trim(),
            SourceKey = sourceKey,
            TargetEntityId = string.IsNullOrWhiteSpace(raw.TargetEntityId) ? null : raw.TargetEntityId.Trim(),
            IntensityTier = NormalizeTier(raw.IntensityTier),
            EffectKind = NormalizeEffectKind(raw.EffectKind),
            StackPolicyHint = string.IsNullOrWhiteSpace(raw.StackPolicyHint) ? def.StackPolicy : raw.StackPolicyHint.Trim(),
            Novelty = NormalizeNovelty(raw.Novelty),
            Evidence = raw.Evidence?.Trim() ?? "",
            Reason = raw.Reason?.Trim() ?? ""
        };
    }

    private static string PromptHintFor(string tagType)
    {
        if (tagType.StartsWith("Fear.", StringComparison.OrdinalIgnoreCase))
            return "保持警觉、节奏略收紧，注意力回到威胁来源";
        if (tagType.StartsWith("Alertness.", StringComparison.OrdinalIgnoreCase))
            return "确认环境、出口和异常细节，提出具体问题";
        if (tagType.StartsWith("Trust.", StringComparison.OrdinalIgnoreCase) || tagType.StartsWith("Suspicion.", StringComparison.OrdinalIgnoreCase))
            return "态度保留，先核实信息，再决定是否信任";
        if (tagType.StartsWith("Anger.", StringComparison.OrdinalIgnoreCase))
            return "语气更冷或更克制，动作仍受当前情境约束";
        if (tagType.StartsWith("Sadness.", StringComparison.OrdinalIgnoreCase))
            return "反应略慢，保留失落感";
        if (tagType.StartsWith("Affection.", StringComparison.OrdinalIgnoreCase))
            return "语气柔和，但不越过已确认关系边界";
        if (tagType.StartsWith("Stress.", StringComparison.OrdinalIgnoreCase) || tagType.Equals("NeedForReassurance", StringComparison.OrdinalIgnoreCase))
            return "压力下寻求确认，避免过度崩溃";
        if (tagType.Equals("CombatReadiness", StringComparison.OrdinalIgnoreCase))
            return "进入防御和行动准备";
        return "只轻微影响语气、注意力和身体反应";
    }

    private static string PromptLimitFor(string tagType)
    {
        if (tagType.StartsWith("Trust.", StringComparison.OrdinalIgnoreCase) || tagType.StartsWith("Suspicion.", StringComparison.OrdinalIgnoreCase))
            return "不要把怀疑说成已确认事实";
        if (tagType.StartsWith("Fear.", StringComparison.OrdinalIgnoreCase))
            return "没有新威胁时不要升级成恐慌或创伤";
        return "不要重置、解决或升级长期情感状态";
    }

    private static string BuildFallbackSourceKey(string tagType, string? targetEntityId, string evidence, string displayName)
    {
        var prefix = NormalizeSourceKey(tagType);
        var seed = FirstNonEmpty(targetEntityId, evidence, displayName, tagType);
        var normalizedSeed = NormalizeSourceKey(seed);
        if (string.IsNullOrWhiteSpace(prefix))
            prefix = "affect";
        if (string.IsNullOrWhiteSpace(normalizedSeed))
            normalizedSeed = "scene";
        return $"{prefix}_{normalizedSeed}";
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";

    private static string NormalizeSourceKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var sb = new StringBuilder();
        var lastWasSeparator = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                sb.Append('_');
                lastWasSeparator = true;
            }

            if (sb.Length >= 64)
                break;
        }

        return sb.ToString().Trim('_');
    }

    private static AffectiveTagState Apply(AffectiveTagState? existing, AffectiveTagCandidate candidate, int foldCount)
    {
        var def = Registry[candidate.TagType];
        var state = existing ?? new AffectiveTagState
        {
            GroupId = 0,
            CharacterId = "",
            TagType = candidate.TagType,
            DisplayName = candidate.DisplayName,
            SourceKey = candidate.SourceKey,
            TargetEntityId = candidate.TargetEntityId,
            ChargeCap = def.ChargeCap,
            ExpirePolicy = def.ExpirePolicy,
            CreatedAt = DateTime.UtcNow
        };

        state.TagType = candidate.TagType;
        state.DisplayName = candidate.DisplayName;
        state.SourceKey = candidate.SourceKey;
        state.TargetEntityId = candidate.TargetEntityId;
        state.ChargeCap = def.ChargeCap;
        state.ExpirePolicy = def.ExpirePolicy;
        state.LastEvidence = candidate.Evidence;
        state.LastAppliedFoldCount = foldCount;
        state.UpdatedAt = DateTime.UtcNow;

        if (candidate.EffectKind is "Resolve" or "Suppress" or "Release")
        {
            state.Status = "Resolved";
            state.Charge = Math.Max(0, state.Charge * 0.25);
            return state;
        }

        state.Status = "Active";
        state.RepetitionCount++;
        state.AdaptationLevel = Math.Min(1.0, state.AdaptationLevel + (IsLowNovelty(candidate.Novelty) ? 0.12 : 0.04));

        var incoming = TierToCharge(candidate.IntensityTier);
        var noveltyBoost = candidate.Novelty switch
        {
            "High" => 0.18,
            "Critical" => 0.3,
            "Medium" => 0.08,
            _ => 0.0
        };

        var policy = string.IsNullOrWhiteSpace(candidate.StackPolicyHint) ? def.StackPolicy : candidate.StackPolicyHint;
        if (policy.Equals("RefreshOnly", StringComparison.OrdinalIgnoreCase) || IsLowNovelty(candidate.Novelty))
            state.Charge = Math.Max(state.Charge, Math.Min(def.ChargeCap, incoming));
        else if (policy.Equals("Escalating", StringComparison.OrdinalIgnoreCase) || candidate.EffectKind == "Escalate")
            state.Charge = Math.Min(def.ChargeCap, Math.Max(state.Charge, incoming) + noveltyBoost);
        else
            state.Charge = Math.Min(def.ChargeCap, Math.Max(state.Charge, incoming) + noveltyBoost * (1.0 - state.AdaptationLevel * 0.5));

        state.IntensityTier = ChargeToTier(state.Charge);
        return state;
    }

    private static double CalculateDecayAmount(string expirePolicy, bool sceneChanged, int foldsSinceApplied)
    {
        if (expirePolicy.Equals("Scene", StringComparison.OrdinalIgnoreCase))
            return sceneChanged ? 0.25 : 0.0;
        if (expirePolicy.Equals("Relationship", StringComparison.OrdinalIgnoreCase))
            return foldsSinceApplied > 0 ? 0.03 * foldsSinceApplied : 0.0;
        if (expirePolicy.Equals("Arc", StringComparison.OrdinalIgnoreCase))
            return foldsSinceApplied > 0 ? 0.01 * foldsSinceApplied : 0.0;
        return sceneChanged || foldsSinceApplied > 0 ? 0.05 : 0.0;
    }

    private static bool IsLowNovelty(string novelty)
        => novelty.Equals("None", StringComparison.OrdinalIgnoreCase)
           || novelty.Equals("Low", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTier(string? tier) => (tier ?? "").Trim().ToLowerInvariant() switch
    {
        "trace" => "Trace",
        "medium" or "moderate" => "Moderate",
        "strong" => "Strong",
        "extreme" => "Extreme",
        _ => "Mild"
    };

    private static string NormalizeNovelty(string? novelty) => (novelty ?? "").Trim().ToLowerInvariant() switch
    {
        "none" => "None",
        "low" => "Low",
        "high" => "High",
        "critical" => "Critical",
        _ => "Medium"
    };

    private static string NormalizeEffectKind(string? effectKind) => (effectKind ?? "").Trim().ToLowerInvariant() switch
    {
        "maintain" => "Maintain",
        "escalate" => "Escalate",
        "resolve" => "Resolve",
        "suppress" => "Suppress",
        "release" => "Release",
        _ => "ApplyOrRefresh"
    };

    private static double TierToCharge(string tier) => tier switch
    {
        "Trace" => 0.12,
        "Mild" => 0.35,
        "Moderate" => 0.6,
        "Strong" => 0.82,
        "Extreme" => 1.0,
        _ => 0.35
    };

    private static string ChargeToTier(double charge)
    {
        if (charge >= 0.9) return "Extreme";
        if (charge >= 0.75) return "Strong";
        if (charge >= 0.5) return "Moderate";
        if (charge >= 0.2) return "Mild";
        return "Trace";
    }
}
