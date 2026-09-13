using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AIMod.Trpg;

public class TrpgStateCache
{
    private readonly ConcurrentDictionary<(string WorldId, long GroupId, string CharacterId), TrpgRuntimeState> _states = new();

    public bool TryGet(TrpgScope scope, string characterId, out TrpgRuntimeState state)
    {
        return _states.TryGetValue((scope.WorldId, scope.GroupId, characterId), out state!);
    }

    public TrpgRuntimeState GetOrCreate(TrpgScope scope, string characterId)
    {
        return _states.GetOrAdd((scope.WorldId, scope.GroupId, characterId), _ => new TrpgRuntimeState
        {
            CurrentSceneId = "scene_default",
            PresentEntities = new List<string> { characterId },
            PlayerStatus = "状态未知",
            UpdatedAt = DateTime.UtcNow
        });
    }

    public void Upsert(TrpgScope scope, string characterId, TrpgRuntimeState state)
    {
        state.UpdatedAt = DateTime.UtcNow;
        _states[(scope.WorldId, scope.GroupId, characterId)] = state;
    }

    public void RemoveEntries(IEnumerable<string> worldIds, long groupId, IEnumerable<string> characterIds)
    {
        var worldSet = new HashSet<string>(worldIds, StringComparer.OrdinalIgnoreCase);
        var characterSet = new HashSet<string>(characterIds, StringComparer.OrdinalIgnoreCase);
        if (worldSet.Count == 0 || characterSet.Count == 0)
            return;

        foreach (var worldId in worldSet)
        {
            foreach (var characterId in characterSet)
            {
                _states.TryRemove((worldId, groupId, characterId), out _);
            }
        }
    }
}
