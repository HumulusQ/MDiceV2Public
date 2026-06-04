using System;

namespace AIMod.Trpg;

public sealed class CharacterInventoryItem
{
    public long Id { get; set; }
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string CharacterId { get; set; } = "";
    public string ItemKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double Quantity { get; set; } = 1;
    public string Unit { get; set; } = "";
    public string State { get; set; } = "carried";

    public string Description { get; set; } = "";
    public string LocationHint { get; set; } = "";
    public string OwnerEntityId { get; set; } = "";

    public string SourceKind { get; set; } = "InitialSeed";
    public int AuthorityRank { get; set; } = 70;
    public double Confidence { get; set; } = 1.0;

    public bool IsAssumed { get; set; }
    public bool IsContradicted { get; set; }
    public bool NeedsReview { get; set; }

    public long? SourceEventId { get; set; }
    public long? LastEventId { get; set; }
    public string LastEvidence { get; set; } = "";

    public bool IsVisibleToCharacter { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public string Metadata { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class InventoryMutation
{
    public string Operation { get; set; } = "";
    public string ItemKey { get; set; } = "";
    public string DisplayName { get; set; } = "";

    public double QuantityDelta { get; set; }
    public double? QuantitySet { get; set; }
    public string Unit { get; set; } = "";

    public string NewState { get; set; } = "";
    public string TargetEntityId { get; set; } = "";

    public string SourceKind { get; set; } = "PlayerDeclared";
    public int AuthorityRank { get; set; } = 30;
    public double Confidence { get; set; } = 0.7;
    public string Evidence { get; set; } = "";

    public bool IsFullSnapshot { get; set; }
}
