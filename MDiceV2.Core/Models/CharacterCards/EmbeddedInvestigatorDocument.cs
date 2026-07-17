using System.Text.Json;
using System.Text.Json.Serialization;

namespace MDiceV2.Models.CharacterCards;

/// <summary>Data stored in the portable CoC 7 investigator export's JSON script element.</summary>
public sealed class EmbeddedInvestigatorDocument
{
    public string Schema { get; set; } = string.Empty;
    public int Version { get; set; }
    public InvestigatorProfile Profile { get; set; } = new();
    public InvestigatorCharacteristics Characteristics { get; set; } = new();
    public InvestigatorResources Resources { get; set; } = new();

    // The portable exporter writes a dictionary keyed by skill id.  Keeping this as
    // JsonElement also lets us accept older list-based exports without a converter.
    public JsonElement Skills { get; set; }
    public JsonElement Weapons { get; set; }
    public JsonElement Gear { get; set; }
    public JsonElement Wealth { get; set; }
    public JsonElement Backstory { get; set; }
}

public sealed class InvestigatorProfile
{
    public string Name { get; set; } = string.Empty;
    public string Player { get; set; } = string.Empty;
    public string Occupation { get; set; } = string.Empty;
    public string Era { get; set; } = string.Empty;
}

public sealed class InvestigatorCharacteristics
{
    public int Str { get; set; }
    public int Con { get; set; }
    public int Siz { get; set; }
    public int Dex { get; set; }
    public int App { get; set; }
    public int Int { get; set; }
    public int Idea { get; set; }
    public int Intelligence { get; set; }
    public int Pow { get; set; }
    public int Willpower { get; set; }
    public int Edu { get; set; }
    public int Luck { get; set; }

    public int EffectiveInt => Int != 0 ? Int : (Idea != 0 ? Idea : Intelligence);
    public int EffectivePow => Pow != 0 ? Pow : Willpower;
}

public sealed class InvestigatorResources
{
    public int? HpCurrent { get; set; }
    public int? MpCurrent { get; set; }
    public int? SanCurrent { get; set; }
    public int? LuckCurrent { get; set; }
    public int? HpMax { get; set; }
    public int? MpMax { get; set; }
    public int? SanMax { get; set; }
    public int? LuckMax { get; set; }
}

public sealed class InvestigatorSkill
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Specialization { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public JsonElement Base { get; set; }
    public string BaseMode { get; set; } = string.Empty;
    public int Occupation { get; set; }
    public int Interest { get; set; }
    public int Growth { get; set; }
    public int Misc { get; set; }
    public bool Enabled { get; set; } = true;
}

internal static class CharacterCardJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}
