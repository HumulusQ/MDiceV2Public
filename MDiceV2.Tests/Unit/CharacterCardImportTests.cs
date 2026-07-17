using FluentAssertions;
using MDiceV2.Models;
using MDiceV2.Models.CharacterCards;
using Xunit;

namespace MDiceV2.Tests.Unit;

public class CharacterCardImportTests
{
    private readonly HtmlCharacterCardParser _parser = new();
    private readonly CharacterCardMapper _mapper = new();

    [Fact]
    public void Parser_accepts_reordered_single_quoted_script_and_json_escape()
    {
        var json = DocumentJson().Replace("\"name\":\"调查员\"", "\"name\":\"A\\u003cB\"");
        var result = _parser.Parse(Wrap(json, "type='application/json' data-x='1' id='embedded-investigator'"));

        result.Success.Should().BeTrue();
        result.Document!.Profile.Name.Should().Be("A<B");
    }

    [Theory]
    [InlineData("", "文件内容为空")]
    [InlineData("<html><script>alert(1)</script></html>", "未找到 embedded-investigator")]
    public void Parser_rejects_empty_and_non_card_html(string html, string expectedMessage)
    {
        var result = _parser.Parse(html);
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain(expectedMessage);
    }

    [Theory]
    [InlineData("other-schema", 1, "schema")]
    [InlineData("tott-coc7e-investigator", 2, "版本")]
    public void Parser_validates_schema_and_version(string schema, int version, string expectedMessage)
    {
        var result = _parser.Parse(Wrap(DocumentJson($"\"schema\":\"{schema}\",\"version\":{version}")));
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain(expectedMessage);
    }

    [Fact]
    public void Mapper_maps_attributes_resources_dynamic_skills_and_specialties()
    {
        var parsed = _parser.Parse(Wrap(DocumentJson()));
        var mapped = _mapper.Map(parsed.Document!);

        mapped.Success.Should().BeTrue();
        var skills = mapped.CharacterSheet!.Skills;
        skills["力量"].Should().Be(40);
        skills["体质"].Should().Be(50);
        skills["体型"].Should().Be(60);
        skills["敏捷"].Should().Be(70);
        skills["外貌"].Should().Be(80);
        skills["智力"].Should().Be(90);
        skills["意志"].Should().Be(55);
        skills["教育"].Should().Be(75);
        skills["幸运"].Should().Be(42, "luckCurrent overrides the characteristic");
        skills["生命"].Should().Be(11);
        skills["魔法"].Should().Be(8);
        skills["理智"].Should().Be(44);
        skills["闪避"].Should().Be(45); // DEX/2 plus 10 occupation
        skills["母语"].Should().Be(80); // EDU plus 5 interest
        skills["射击（手枪）"].Should().Be(100); // clamped after all point sources
        mapped.ConflictCount.Should().Be(1);
        skills["侦查"].Should().Be(60, "duplicate skills retain the higher value");
    }

    [Fact]
    public void Import_renames_existing_card_and_sets_current_card()
    {
        var processor = new MessageProcessor();
        var first = processor.ImportCharacterCard(987654321, NewSheet("调查员"));
        var second = processor.ImportCharacterCard(987654321, NewSheet("调查员"));

        first.Success.Should().BeTrue();
        second.FinalCharacterName.Should().Be("调查员 (2)");
        processor.GetCurrentCharacterCardName(987654321).Should().Be("调查员 (2)");
        processor.GetCharacterCard(987654321, "调查员").Should().NotBeNull();
        processor.GetCharacterCard(987654321, "调查员 (2)").Should().NotBeNull();
    }

    [Theory]
    [InlineData("card.mdice", true)]
    [InlineData("card.mdice.html", true)]
    [InlineData("card.html", false)]
    [InlineData("card.htm", false)]
    [InlineData("notes.txt", false)]
    public void Candidate_filter_only_accepts_supported_extensions(string name, bool expected)
    {
        CharacterCardFileImportCoordinator.IsCandidateFile(new OneBotFileInfo { FileName = name }).Should().Be(expected);
    }

    [Fact]
    public void Coc_card_updater_uses_a_dedicated_data_folder()
    {
        var service = new CocCardUpdateService();

        CocCardUpdateService.AssetName.Should().Be("portable_CoC7e_charactercard.html");
        service.LocalFilePath.Should().Contain("CharacterCards");
        service.LocalFileName.Should().Be(Path.GetFileName(service.LocalFilePath));
    }

    [Theory]
    [InlineData("portable_CoC7e_charactercard.html", true)]
    [InlineData("TOTT_portable_CoC7e_investigator_v213.html", true)]
    [InlineData("TOTT_portable_CoC7e_investigator_v2.html", true)]
    [InlineData("TOTT_portable_CoC7e_investigator_vNext.html", false)]
    [InlineData("TOTT_portable_CoC7e_investigator_v213.htm", false)]
    public void Coc_card_updater_accepts_versioned_release_asset_names(string name, bool expected)
    {
        CocCardUpdateService.IsSupportedAssetName(name).Should().Be(expected);
    }

    private static MessageProcessor.CharacterSheet NewSheet(string name) => new() { Name = name, Skills = { ["侦查"] = 50 } };

    private static string Wrap(string json, string attributes = "id=\"embedded-investigator\" type=\"application/json\"") =>
        $"<html><body><script {attributes}>{json}</script><script>throw new Error('must not run')</script></body></html>";

    private static string DocumentJson(string? overrides = null)
    {
        var prefix = "\"schema\":\"tott-coc7e-investigator\",\"version\":1";
        if (!string.IsNullOrWhiteSpace(overrides))
        {
            if (overrides.Contains("\"schema\"")) prefix = overrides;
            else prefix += "," + overrides;
        }
        return $$"""
        {
          {{prefix}},
          "profile":{"name":"调查员","player":"P","occupation":"记者","era":"1920s"},
          "characteristics":{"str":40,"con":50,"siz":60,"dex":70,"app":80,"int":90,"pow":55,"edu":75,"luck":35},
          "resources":{"hpCurrent":11,"mpCurrent":8,"sanCurrent":44,"luckCurrent":42,"hpMax":12},
          "skills":{
            "dodge":{"id":"dodge","name":"闪避","base":{"type":"dodge"},"occupation":10},
            "language":{"id":"lang","name":"母语","baseMode":"ownLanguage","interest":5},
            "pistol":{"id":"pistol","name":"射击","specialty":"手枪","base":20,"occupation":70,"interest":20,"growth":20,"misc":20},
            "spot1":{"id":"spot1","name":"侦查","base":25,"occupation":20},
            "spot2":{"id":"spot2","name":"侦查","base":30,"occupation":30}
          }
        }
        """;
    }
}
