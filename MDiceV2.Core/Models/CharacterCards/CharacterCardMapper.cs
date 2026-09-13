using System.Globalization;
using System.Text.Json;

namespace MDiceV2.Models.CharacterCards;

public sealed record CharacterSheetMappingResult(
    bool Success,
    string ErrorMessage,
    MessageProcessor.CharacterSheet? CharacterSheet,
    int SkillCount,
    int ConflictCount,
    IReadOnlyList<string> Warnings)
{
    public static CharacterSheetMappingResult Fail(string message) => new(false, message, null, 0, 0, Array.Empty<string>());
}

public sealed class CharacterCardMapper
{
    private const int MaxOptionalJsonLength = 64 * 1024;
    private const int MaxOptionalMetaLength = 256 * 1024;

    public CharacterSheetMappingResult Map(EmbeddedInvestigatorDocument source)
    {
        if (source is null)
            return CharacterSheetMappingResult.Fail("人物卡数据为空。");

        var sheet = new MessageProcessor.CharacterSheet
        {
            Name = NormalizeName(source.Profile?.Name),
            CharacterType = "coc"
        };
        var warnings = new List<string>();
        AddCharacteristics(sheet, source.Characteristics ?? new InvestigatorCharacteristics());
        AddResources(sheet, source.Resources ?? new InvestigatorResources());
        var (skillCount, conflicts) = AddSkills(sheet, source.Skills, source.Characteristics ?? new InvestigatorCharacteristics(), warnings);

        sheet.ExtraMeta["sourceSchema"] = source.Schema;
        sheet.ExtraMeta["sourceVersion"] = source.Version.ToString(CultureInfo.InvariantCulture);
        sheet.ExtraMeta["importSource"] = "embedded-investigator-html";
        sheet.ExtraMeta["importedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        AddIfNotEmpty(sheet, "occupationName", source.Profile?.Occupation);
        AddIfNotEmpty(sheet, "playerName", source.Profile?.Player);
        AddIfNotEmpty(sheet, "era", source.Profile?.Era);
        AddOptionalMetadata(sheet, source);

        return new CharacterSheetMappingResult(true, string.Empty, sheet, skillCount, conflicts, warnings);
    }

    private static void AddCharacteristics(MessageProcessor.CharacterSheet sheet, InvestigatorCharacteristics c)
    {
        sheet.Skills["力量"] = c.Str;
        sheet.Skills["体质"] = c.Con;
        sheet.Skills["体型"] = c.Siz;
        sheet.Skills["敏捷"] = c.Dex;
        sheet.Skills["外貌"] = c.App;
        sheet.Skills["智力"] = c.EffectiveInt;
        sheet.Skills["意志"] = c.EffectivePow;
        sheet.Skills["教育"] = c.Edu;
        sheet.Skills["幸运"] = c.Luck;
    }

    private static void AddResources(MessageProcessor.CharacterSheet sheet, InvestigatorResources resources)
    {
        if (resources.HpCurrent.HasValue) sheet.Skills["生命"] = Math.Max(0, resources.HpCurrent.Value);
        if (resources.MpCurrent.HasValue) sheet.Skills["魔法"] = Math.Max(0, resources.MpCurrent.Value);
        if (resources.SanCurrent.HasValue) sheet.Skills["理智"] = Math.Max(0, resources.SanCurrent.Value);
        if (resources.LuckCurrent.HasValue) sheet.Skills["幸运"] = Math.Max(0, resources.LuckCurrent.Value);

        AddNullableMeta(sheet, "hpMax", resources.HpMax);
        AddNullableMeta(sheet, "mpMax", resources.MpMax);
        AddNullableMeta(sheet, "sanMax", resources.SanMax);
        AddNullableMeta(sheet, "luckMax", resources.LuckMax);
    }

    private static (int SkillCount, int Conflicts) AddSkills(
        MessageProcessor.CharacterSheet sheet,
        JsonElement skillsElement,
        InvestigatorCharacteristics characteristics,
        List<string> warnings)
    {
        var count = 0;
        var conflicts = 0;
        foreach (var skillElement in EnumerateSkills(skillsElement))
        {
            InvestigatorSkill? skill;
            try
            {
                skill = JsonSerializer.Deserialize<InvestigatorSkill>(skillElement.GetRawText(), CharacterCardJson.Options);
            }
            catch (JsonException)
            {
                warnings.Add("跳过了一项无法解析的技能。");
                continue;
            }

            if (skill is null || !skill.Enabled)
                continue;

            var name = BuildSkillDisplayName(skill);
            if (string.IsNullOrWhiteSpace(name))
            {
                warnings.Add("跳过了一项没有名称的技能。");
                continue;
            }

            var baseValue = ResolveSkillBase(skill, characteristics, warnings);
            var value = Math.Clamp(baseValue + skill.Occupation + skill.Interest + skill.Growth + skill.Misc, 0, 100);
            if (sheet.Skills.TryGetValue(name, out var previous))
            {
                sheet.Skills[name] = Math.Max(previous, value);
                conflicts++;
                warnings.Add($"重复技能“{name}”已保留较高数值。");
            }
            else
            {
                sheet.Skills[name] = value;
                count++;
            }
        }
        return (count, conflicts);
    }

    private static IEnumerable<JsonElement> EnumerateSkills(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
                if (value.ValueKind == JsonValueKind.Object) yield return value;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Object) yield return property.Value;
        }
    }

    private static int ResolveSkillBase(InvestigatorSkill skill, InvestigatorCharacteristics c, List<string> warnings)
    {
        var element = skill.Base;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numeric)) return numeric;
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            return int.TryParse(text, out var numericText) ? numericText : ResolveBaseToken(text, c, warnings);
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var type = GetFirstString(element, "type", "mode", "kind");
            if (type.Equals("fixed", StringComparison.OrdinalIgnoreCase))
                return GetFirstInt32(element, "value", "base") ?? 0;
            return ResolveBaseToken(type, c, warnings);
        }

        return string.IsNullOrWhiteSpace(skill.BaseMode) ? 0 : ResolveBaseToken(skill.BaseMode, c, warnings);
    }

    private static int ResolveBaseToken(string? token, InvestigatorCharacteristics c, List<string> warnings)
    {
        var normalized = token?.Trim().Replace("_", string.Empty).ToLowerInvariant();
        return normalized switch
        {
            "dodge" => c.Dex / 2,
            "ownlanguage" or "motherlanguage" => c.Edu,
            "fixed" or "" => 0,
            _ => WarnUnknownBase(token, warnings)
        };
    }

    private static int WarnUnknownBase(string? token, List<string> warnings)
    {
        warnings.Add($"未知的动态技能基础值“{token}”已按 0 处理。");
        return 0;
    }

    private static string BuildSkillDisplayName(InvestigatorSkill skill)
    {
        var name = skill.Name?.Trim() ?? string.Empty;
        var specialization = !string.IsNullOrWhiteSpace(skill.Specialization)
            ? skill.Specialization.Trim()
            : skill.Specialty?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(specialization) ? name : $"{name}（{specialization}）";
    }

    private static void AddOptionalMetadata(MessageProcessor.CharacterSheet sheet, EmbeddedInvestigatorDocument source)
    {
        var total = sheet.ExtraMeta.Sum(x => x.Key.Length + x.Value.Length);
        foreach (var (key, element) in new[]
        {
            ("weaponsJson", source.Weapons), ("gearJson", source.Gear),
            ("wealthJson", source.Wealth), ("backstoryJson", source.Backstory)
        })
        {
            if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
            var json = element.GetRawText();
            if (json.Length <= MaxOptionalJsonLength && total + key.Length + json.Length <= MaxOptionalMetaLength)
            {
                sheet.ExtraMeta[key] = json;
                total += key.Length + json.Length;
            }
        }
    }

    private static string NormalizeName(string? name) => string.IsNullOrWhiteSpace(name) ? "未命名调查员" : name.Trim();
    private static void AddIfNotEmpty(MessageProcessor.CharacterSheet sheet, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) sheet.ExtraMeta[key] = value.Trim();
    }
    private static void AddNullableMeta(MessageProcessor.CharacterSheet sheet, string key, int? value)
    {
        if (value.HasValue) sheet.ExtraMeta[key] = value.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static string GetFirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
        return string.Empty;
    }
    private static int? GetFirstInt32(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGet(element, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
            }
        return null;
    }
    private static bool TryGet(JsonElement element, string name, out JsonElement result)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                result = property.Value;
                return true;
            }
        result = default;
        return false;
    }
}
