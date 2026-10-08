using System.Reflection;
using System.Text.Json;
using MDiceV2.Models;
using Xunit;

namespace MDiceV2.Tests.Unit;

public sealed class CardNameCommandTests
{
    private const long TestUserId = 20002;
    private const long TestGroupId = 10001;
    private const string CocTemplate = "SAN:{理智} HP:{生命}/[({体质}+{体型})/10] DEX:{敏捷}";

    private static readonly MethodInfo HandleCardNameCommandMethod =
        typeof(MessageProcessor).GetMethod("HandleCardNameCommand", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("HandleCardNameCommand not found.");

    private static readonly MethodInfo ReplaceCardNamePlaceholdersMethod =
        typeof(MessageProcessor).GetMethod("ReplaceCardNamePlaceholders", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ReplaceCardNamePlaceholders not found.");

    [Fact]
    public void CocPreset_InitializesMissingHpAndPersistsTemplateAndCard()
    {
        using var database = new TemporaryDatabase();
        var processor = CreateProcessor(database.DataIO);
        var replies = AttachReplyCapture(processor);
        var card = NewCard("调查员", dex: 70, con: 50, siz: 61, san: 55);
        processor.ImportCharacterCard(TestUserId, card);

        HandleSet(processor, "set coc");

        Assert.Equal(11, card.Skills["生命"]);
        Assert.Contains(CocTemplate, Assert.Single(replies));
        Assert.Contains("SAN:55 HP:11/11 DEX:70", replies[0]);

        string persisted = database.DataIO.ReadData("UserData", TestUserId.ToString())
            ?? throw new InvalidOperationException("User data was not persisted.");
        using var document = JsonDocument.Parse(persisted);
        Assert.Equal(CocTemplate, document.RootElement.GetProperty("CardNameTemplate").GetString());
        Assert.Equal(11, document.RootElement.GetProperty("CharacterSheets")
            .GetProperty("调查员").GetProperty("Skills").GetProperty("生命").GetInt32());
    }

    [Fact]
    public void CocPreset_PreservesExistingCurrentHp()
    {
        var processor = new MessageProcessor();
        AttachReplyCapture(processor);
        var card = NewCard("调查员", dex: 70, con: 50, siz: 61, san: 55, hp: 4);
        processor.ImportCharacterCard(TestUserId, card);

        HandleSet(processor, "set coc");

        Assert.Equal(4, card.Skills["生命"]);
    }

    [Fact]
    public void CocPreset_TreatsMissingHpAttributesAsZero()
    {
        var processor = new MessageProcessor();
        AttachReplyCapture(processor);
        var card = new MessageProcessor.CharacterSheet { Name = "调查员" };
        processor.ImportCharacterCard(TestUserId, card);

        HandleSet(processor, "set coc");

        Assert.Equal(0, card.Skills["生命"]);
    }

    [Fact]
    public void ExistingCocHpCalculator_FloorsTheMaximumHp()
    {
        var card = NewCard("调查员", dex: 70, con: 50, siz: 61, san: 55);

        Assert.Equal(11, card.COCHPCalculator());
    }

    [Fact]
    public void Formula_UsesCurrentCardWithPrecedenceParenthesesAndFlooring()
    {
        var processor = new MessageProcessor();
        processor.ImportCharacterCard(TestUserId, NewCard("旧卡", dex: 15, con: 10, siz: 20, san: 30));
        var currentCard = NewCard("当前卡", dex: 70, con: 50, siz: 61, san: 55, strength: 40);
        processor.ImportCharacterCard(TestUserId, currentCard);

        string resolved = Resolve(processor,
            "A=[({敏捷}+{力量})*2/3] B=[{敏捷}+{力量}*2] HP=[({体质}+{体型})/10]");

        Assert.Equal("A=73 B=150 HP=11", resolved);
    }

    [Fact]
    public void Formula_ReturnsNaForAnyDivisionByZero()
    {
        var processor = new MessageProcessor();
        processor.ImportCharacterCard(TestUserId,
            NewCard("调查员", dex: 70, con: 50, siz: 61, san: 55, strength: 40));

        string resolved = Resolve(processor, "HP=[({体质}+{体型})/10] BAD=[1/(1-{敏捷}/70)]");

        Assert.Equal("HP=11 BAD=NA", resolved);
    }

    private static MessageProcessor CreateProcessor(DataIO dataIO)
    {
        var processor = new MessageProcessor();
        typeof(MessageProcessor).GetProperty(nameof(MessageProcessor.DataIO))!
            .SetValue(processor, dataIO);
        return processor;
    }

    private static List<string> AttachReplyCapture(MessageProcessor processor)
    {
        var replies = new List<string>();
        var distribution = new MessageDistribution();
        distribution.OnReplySent += (content, _) => replies.Add(content);
        distribution.MessageProcessor = processor;
        processor.MessageDistribution = distribution;
        return replies;
    }

    private static void HandleSet(MessageProcessor processor, string args)
    {
        var message = new Msg(TestGroupId, TestUserId, $".cn {args}", MessageSource.group);
        HandleCardNameCommandMethod.Invoke(processor, new object[] { args, message });
    }

    private static string Resolve(MessageProcessor processor, string template)
    {
        return (string)(ReplaceCardNamePlaceholdersMethod.Invoke(processor, new object[] { template, TestUserId })
            ?? throw new InvalidOperationException("Resolved card name was null."));
    }

    private static MessageProcessor.CharacterSheet NewCard(
        string name,
        int dex,
        int con,
        int siz,
        int san,
        int? hp = null,
        int strength = 0)
    {
        var card = new MessageProcessor.CharacterSheet { Name = name };
        card.Skills["敏捷"] = dex;
        card.Skills["体质"] = con;
        card.Skills["体型"] = siz;
        card.Skills["理智"] = san;
        card.Skills["力量"] = strength;
        if (hp.HasValue)
            card.Skills["生命"] = hp.Value;
        return card;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mdice-cn-tests-{Guid.NewGuid():N}");

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            DataIO = new DataIO(Path.Combine(_directory, "test.db"));
        }

        public DataIO DataIO { get; }

        public void Dispose()
        {
            DataIO.Close();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}
