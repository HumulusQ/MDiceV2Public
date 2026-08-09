namespace AIMod.Trpg;

public sealed record TrpgScope
{
    public string WorldId { get; init; } = "";
    public long OwnerUserId { get; init; }
    public long GroupId { get; init; }
    public string TeamName { get; init; } = "";
    public string CampaignName { get; init; } = "";

    public static TrpgScope Create(
        long ownerUserId,
        long groupId,
        string teamName,
        string? campaignName = null,
        string? worldId = null)
    {
        var safeTeam = string.IsNullOrWhiteSpace(teamName) ? "default" : teamName.Trim();
        var safeCampaign = string.IsNullOrWhiteSpace(campaignName) ? "default" : campaignName.Trim();

        return new TrpgScope
        {
            OwnerUserId = ownerUserId,
            GroupId = groupId,
            TeamName = safeTeam,
            CampaignName = safeCampaign,
            WorldId = string.IsNullOrWhiteSpace(worldId)
                ? $"world:{ownerUserId}:{groupId}:{safeTeam}:{safeCampaign}"
                : worldId.Trim()
        };
    }

    public static TrpgScope FromCharacter(AiCharacterEntry character, string? campaignName = null)
    {
        return Create(
            character.OwnerUserId,
            character.GroupId,
            character.TeamName,
            campaignName,
            string.IsNullOrWhiteSpace(character.WorldId) ? null : character.WorldId);
    }
}
