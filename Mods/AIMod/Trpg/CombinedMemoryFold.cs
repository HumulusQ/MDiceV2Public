using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AIMod.Trpg;

public sealed class CombinedMemoryFoldRequest
{
    public string Prompt { get; init; } = "";

    public static List<ChatMessage> BuildMessages(string contextView)
    {
        var prompt = $$"""
你正在执行一次合并的记忆折叠请求，必须同时完成：
1. 当前角色 IC 语义记忆候选；
2. 同团共享 PL 桌面记忆候选；
3. 时间线摘要；
4. 桌面事件提取；
5. 当前目标生命周期更新。

输出严格 JSON，不要 markdown，不要自然语言解释。字段必须完整：
{
  "character_ic_memory_candidates": [
    {
      "summary": "",
      "keywords": "",
      "node_type": "fact|event|emotion|relationship|item|scene|other",
      "importance": 0.0,
      "confidence": 0.0,
      "source_message_ids": [],
      "raw_excerpt": "",
      "ic_evidence": "",
      "character_id": ""
    }
  ],
  "player_table_memory_candidates": [
    {
      "summary": "",
      "keywords": "",
      "node_type": "table_event|pl_context|identity_hint|scene|other",
      "importance": 0.0,
      "confidence": 0.0,
      "source_message_ids": [],
      "raw_excerpt": "",
      "ic_availability_note": ""
    }
  ],
  "timeline_summary": [
    {
      "level": "L1|L2|L3",
      "summary": "",
      "parent_hint": "",
      "importance": 0,
      "foreshadowing": false,
      "source_message_ids": []
    }
  ],
  "objective_updates": [
    {
      "action": "add|complete|abandon|supersede|touch",
      "match": "",
      "description": "",
      "priority": "low|normal|high|urgent",
      "reason": ""
    }
  ],
  "table_event_candidates": [
    {
      "event_type": "scene_transition|combat|dialogue|discovery|item_acquisition|npc_death|relationship_change|other",
      "actors": "",
      "location": "",
      "result": "",
      "table_changes": "",
      "source_message_ids": []
    }
  ]
}

约束和IC/PL判定优先级表：
【IC/PL判定优先级（从高到低）】
1. OOC/PL/其他角色私有视角 → player_table_memory_candidates
2. GM对当前折叠角色的第二人称叙述（"你"） → character_ic_memory_candidates
3. GM对当前折叠角色行动的反馈 → character_ic_memory_candidates
4. 当前折叠角色自己的IC行动/台词 → character_ic_memory_candidates
5. 当前角色亲眼可见的公开场景事实 → character_ic_memory_candidates
6. 只有无法判断受众且不是当前角色直接经历时，才写player_table_memory_candidates

【内容质量约束】
- character_ic_memory_candidates 只能写当前角色 IC 视角能知道的内容。
- 无法确认是否为当前角色 IC 可用时，写入 player_table_memory_candidates。
- table_event_candidates 使用 table_changes/session_changes 语义，不要输出 world_truth/world_changes。
- 不允许因为合并而只输出一种结果。
- timeline_summary 必须写具体剧情推进、明确结果、明确状态变化或关键信息揭示。
- objective_updates 只处理本折叠窗口里真正新增、完成、放弃、被替代或再次被触碰的目标。

【严格禁止项】
- 不要整段原文复制到summary中。summary必须是20~80字的语义摘要。
- 不要在summary中包含多条聊天前缀（[GM-]、[PL-]、[OOC-]、[角色名]：等）。
- 如果折叠窗口同时包含 OOC 和 IC，不要整段降级为 PL；必须拆分：IC 部分进入 character_ic_memory_candidates，OOC/PL 部分进入 player_table_memory_candidates。
- raw_excerpt / source_message_ids 用于保存来源，不能为空；summary 用于语义提取，必须有内容。
- timeline_summary 禁止输出“全员可行动阶段”“等待反应”“继续行动”“所有人可以行动”“场景推进”等低信息流程句。

上下文：
{{contextView}}
""";
        return new List<ChatMessage>
        {
            new("system", AimodPromptPrefixes.BackendCommonPrefixV1),
            new("user", prompt)
        };
    }
}

public sealed class CombinedMemoryFoldResult
{
    [JsonPropertyName("character_ic_memory_candidates")]
    public List<CharacterIcMemoryCandidate> CharacterIcMemoryCandidates { get; set; } = new();

    [JsonPropertyName("player_table_memory_candidates")]
    public List<PlayerTableMemoryCandidate> PlayerTableMemoryCandidates { get; set; } = new();

    [JsonPropertyName("timeline_summary")]
    public List<TimelineSummaryCandidate> TimelineSummary { get; set; } = new();

    [JsonPropertyName("objective_updates")]
    public List<ObjectiveUpdate> ObjectiveUpdates { get; set; } = new();

    [JsonPropertyName("table_event_candidates")]
    public List<TableEventCandidate> TableEventCandidates { get; set; } = new();
}

public sealed class CharacterIcMemoryCandidate
{
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("keywords")] public string Keywords { get; set; } = "";
    [JsonPropertyName("node_type")] public string NodeType { get; set; } = "event";
    [JsonPropertyName("importance")] public double Importance { get; set; } = 0.5;
    [JsonPropertyName("confidence")] public double Confidence { get; set; } = 0.7;
    [JsonPropertyName("source_message_ids")] public List<string> SourceMessageIds { get; set; } = new();
    [JsonPropertyName("raw_excerpt")] public string RawExcerpt { get; set; } = "";
    [JsonPropertyName("ic_evidence")] public string IcEvidence { get; set; } = "";
    [JsonPropertyName("character_id")] public string CharacterId { get; set; } = "";
}

public sealed class PlayerTableMemoryCandidate
{
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("keywords")] public string Keywords { get; set; } = "";
    [JsonPropertyName("node_type")] public string NodeType { get; set; } = "pl_context";
    [JsonPropertyName("importance")] public double Importance { get; set; } = 0.5;
    [JsonPropertyName("confidence")] public double Confidence { get; set; } = 0.7;
    [JsonPropertyName("source_message_ids")] public List<string> SourceMessageIds { get; set; } = new();
    [JsonPropertyName("raw_excerpt")] public string RawExcerpt { get; set; } = "";
    [JsonPropertyName("ic_availability_note")] public string IcAvailabilityNote { get; set; } = "";
}

public sealed class TimelineSummaryCandidate
{
    [JsonPropertyName("level")] public string Level { get; set; } = "L3";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("parent_hint")] public string ParentHint { get; set; } = "";
    [JsonPropertyName("importance")] public int Importance { get; set; } = 5;
    [JsonPropertyName("foreshadowing")] public bool Foreshadowing { get; set; }
    [JsonPropertyName("source_message_ids")] public List<string> SourceMessageIds { get; set; } = new();
}

public sealed class TableEventCandidate
{
    [JsonPropertyName("event_type")] public string EventType { get; set; } = "other";
    [JsonPropertyName("actors")] public string Actors { get; set; } = "";
    [JsonPropertyName("location")] public string Location { get; set; } = "";
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("table_changes")] public string TableChanges { get; set; } = "";
    [JsonPropertyName("source_message_ids")] public List<string> SourceMessageIds { get; set; } = new();
}

public sealed class ObjectiveUpdate
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("match")] public string Match { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("priority")] public string Priority { get; set; } = "normal";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public static class CombinedMemoryFoldParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    public static bool TryParse(string? response, out CombinedMemoryFoldResult result, out string error)
    {
        result = new CombinedMemoryFoldResult();
        error = "";
        if (string.IsNullOrWhiteSpace(response))
        {
            error = "empty response";
            return false;
        }

        var json = ExtractJson(response);
        try
        {
          result = JsonSerializer.Deserialize<CombinedMemoryFoldResult>(json, JsonOptions) ?? new CombinedMemoryFoldResult();
          if (result.CharacterIcMemoryCandidates.Count == 0
            && result.PlayerTableMemoryCandidates.Count == 0
            && result.TimelineSummary.Count == 0
            && result.ObjectiveUpdates.Count == 0
            && result.TableEventCandidates.Count == 0)
          {
            if (TryParseWithFieldCompatibility(json, out var fallback))
              result = fallback;
          }

          return result.CharacterIcMemoryCandidates.Count > 0
               || result.PlayerTableMemoryCandidates.Count > 0
               || result.TimelineSummary.Count > 0
               || result.ObjectiveUpdates.Count > 0
               || result.TableEventCandidates.Count > 0;
        }
        catch (Exception ex)
        {
          error = ex.Message;
          return false;
        }
    }

      private static bool TryParseWithFieldCompatibility(string json, out CombinedMemoryFoldResult result)
      {
        result = new CombinedMemoryFoldResult();
        try
        {
          var root = JsonNode.Parse(json) as JsonObject;
          if (root == null)
            return false;

          if (root["character_ic_memory_candidates"] is JsonArray icArray)
          {
            foreach (var node in icArray)
            {
              if (node is not JsonObject obj)
                continue;
              result.CharacterIcMemoryCandidates.Add(new CharacterIcMemoryCandidate
              {
                Summary = GetString(obj, "summary", "content"),
                Keywords = GetString(obj, "keywords"),
                NodeType = GetString(obj, "node_type", "type", "memory_type") ?? "event",
                Importance = GetDouble(obj, "importance", 0.5),
                Confidence = GetDouble(obj, "confidence", 0.7),
                SourceMessageIds = GetStringArray(obj, "source_message_ids"),
                RawExcerpt = GetString(obj, "raw_excerpt") ?? "",
                IcEvidence = GetString(obj, "ic_evidence") ?? "",
                CharacterId = GetString(obj, "character_id", "character", "owner_character_id") ?? ""
              });
            }
          }

          if (root["player_table_memory_candidates"] is JsonArray plArray)
          {
            foreach (var node in plArray)
            {
              if (node is not JsonObject obj)
                continue;
              result.PlayerTableMemoryCandidates.Add(new PlayerTableMemoryCandidate
              {
                Summary = GetString(obj, "summary", "content"),
                Keywords = GetString(obj, "keywords"),
                NodeType = GetString(obj, "node_type", "type", "memory_type") ?? "pl_context",
                Importance = GetDouble(obj, "importance", 0.5),
                Confidence = GetDouble(obj, "confidence", 0.7),
                SourceMessageIds = GetStringArray(obj, "source_message_ids"),
                RawExcerpt = GetString(obj, "raw_excerpt") ?? "",
                IcAvailabilityNote = GetString(obj, "ic_availability_note") ?? ""
              });
            }
          }

          if (root["timeline_summary"] is JsonArray timelineArray)
          {
            foreach (var node in timelineArray)
            {
              if (node is not JsonObject obj)
                continue;
              result.TimelineSummary.Add(new TimelineSummaryCandidate
              {
                Level = GetString(obj, "level") ?? "L3",
                Summary = GetString(obj, "summary", "content"),
                ParentHint = GetString(obj, "parent_hint") ?? "",
                Importance = (int)GetDouble(obj, "importance", 5),
                Foreshadowing = GetBool(obj, "foreshadowing"),
                SourceMessageIds = GetStringArray(obj, "source_message_ids")
              });
            }
          }

          if (root["objective_updates"] is JsonArray objectiveArray)
          {
            foreach (var node in objectiveArray)
            {
              if (node is not JsonObject obj)
                continue;
              result.ObjectiveUpdates.Add(new ObjectiveUpdate
              {
                Action = GetString(obj, "action"),
                Match = GetString(obj, "match", "target", "old_objective"),
                Description = GetString(obj, "description", "new_objective"),
                Priority = GetString(obj, "priority") ?? "normal",
                Reason = GetString(obj, "reason")
              });
            }
          }

          if (root["table_event_candidates"] is JsonArray evtArray)
          {
            foreach (var node in evtArray)
            {
              if (node is not JsonObject obj)
                continue;
              result.TableEventCandidates.Add(new TableEventCandidate
              {
                EventType = GetString(obj, "event_type", "type") ?? "other",
                Actors = GetString(obj, "actors") ?? "",
                Location = GetString(obj, "location") ?? "",
                Result = GetString(obj, "result") ?? "",
                TableChanges = GetString(obj, "table_changes", "session_changes") ?? "",
                SourceMessageIds = GetStringArray(obj, "source_message_ids")
              });
            }
          }

          return result.CharacterIcMemoryCandidates.Count > 0
               || result.PlayerTableMemoryCandidates.Count > 0
               || result.TimelineSummary.Count > 0
               || result.ObjectiveUpdates.Count > 0
               || result.TableEventCandidates.Count > 0;
        }
        catch
        {
          result = new CombinedMemoryFoldResult();
          return false;
        }
      }

      private static string GetString(JsonObject obj, params string[] keys)
      {
        foreach (var key in keys)
        {
          if (obj[key] is JsonValue value && value.TryGetValue<string>(out var str))
            return str ?? "";
        }
        return "";
      }

      private static double GetDouble(JsonObject obj, string key, double fallback)
      {
        if (obj[key] is JsonValue value)
        {
          if (value.TryGetValue<double>(out var d))
            return d;
          if (value.TryGetValue<float>(out var f))
            return f;
          if (value.TryGetValue<int>(out var i))
            return i;
          if (value.TryGetValue<string>(out var s) && double.TryParse(s, out var parsed))
            return parsed;
        }
        return fallback;
      }

      private static bool GetBool(JsonObject obj, string key)
      {
        if (obj[key] is JsonValue value)
        {
          if (value.TryGetValue<bool>(out var b))
            return b;
          if (value.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed))
            return parsed;
        }
        return false;
      }

      private static List<string> GetStringArray(JsonObject obj, string key)
      {
        if (obj[key] is JsonArray arr)
        {
          return arr
            .Select(x => x?.ToString() ?? "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        }
        return new List<string>();
      }

    private static string ExtractJson(string response)
    {
        var text = response.Trim();
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            text = text[7..].Trim();
        else if (text.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            text = text[3..].Trim();
        if (text.EndsWith("```", StringComparison.OrdinalIgnoreCase))
            text = text[..^3].Trim();

        var match = Regex.Match(text, @"\{.*\}", RegexOptions.Singleline);
        return match.Success ? match.Value : text;
    }
}
