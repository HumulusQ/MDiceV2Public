using System.Text.Json.Serialization;

namespace AIMod.Trpg;

/// <summary>
/// 状态增量：AI 提取的场景与人物变动（仅包含变更，不包含完整状态）
/// </summary>
public class StateDelta
{
    /// <summary>
    /// 场景是否发生变更
    /// </summary>
    [JsonPropertyName("location_updated")]
    public bool LocationUpdated { get; set; }

    /// <summary>
    /// 新场景名称（若 location_updated=true，否则为 null）
    /// </summary>
    [JsonPropertyName("new_location")]
    public string? NewLocation { get; set; }

    /// <summary>
    /// 进入场景的人物/实体名称列表（仅包含变更）
    /// </summary>
    [JsonPropertyName("entities_enter")]
    public List<string> EntitiesEnter { get; set; } = new();

    /// <summary>
    /// 离开场景的人物/实体名称列表（仅包含变更）
    /// </summary>
    [JsonPropertyName("entities_exit")]
    public List<string> EntitiesExit { get; set; } = new();
}
