using FluentAssertions;
using System.Reflection;
using MDiceV2.Interfaces;
using MDiceV2.Interfaces.Mod;
using MeguminDuel;
using Xunit;

namespace MDiceV2.Tests.Unit;

public sealed class MeguminDuelTests
{
    [Fact]
    public void CriticalAndFumble_AreRemovedFromLaterRules()
    {
        var result = DuelEngine.Resolve([1, 96, 50], [10]);
        result.PlayerScore.Should().Be(200);
        result.PlayerRemaining.Should().BeEmpty();
        result.PlayerFinalDice.Should().Equal(200);
        result.Winner.Should().Be(DuelWinner.Player);
    }

    [Fact]
    public void ZeroOnD100_IsNormalizedToOneHundredAndFumbles()
    {
        var result = DuelEngine.Resolve([0, 50, 20], [11]);

        result.Events.Should().Contain(line => line.Contains("玩家的 [100] 大失败并销毁了后续骰 [50]"));
        result.PlayerFinalDice.Should().Equal(20);
        result.PlayerScore.Should().Be(20);
    }

    [Fact]
    public void TailMergeToZero_BecomesOneHundredAndTriggersFumble()
    {
        var result = DuelEngine.Resolve([40, 12], [60, 33]);

        result.Events.Should().Contain(line => line.Contains("玩家的 [40] 吞掉 [60]，变为 [100]"));
        result.Events.Should().Contain(line => line.Contains("玩家的 [100] 大失败，但后面已没有可销毁的骰子"));
        result.PlayerFinalDice.Should().Equal(12);
        result.PlayerScore.Should().Be(12);
        result.MeguminFinalDice.Should().Equal(33);
        result.MeguminScore.Should().Be(33);
    }

    [Fact]
    public void FumbleAndDestroyedDie_AreOmittedFromDisplayWhileCriticalShowsAsTwoHundred()
    {
        var result = DuelEngine.Resolve([1, 98, 20, 51], [96]);

        result.PlayerFinalDice.Should().Equal(200, 51);
        result.MeguminFinalDice.Should().BeEmpty();
        result.PlayerScore.Should().Be(251);
    }

    [Fact]
    public void DestroyedFumble_DoesNotTriggerAnotherFumble()
    {
        var result = DuelEngine.Resolve([98, 99, 50], [11]);

        result.Events.Should().ContainSingle(line => line.Contains("大失败"));
        result.Events.Should().Contain(line => line.Contains("[98]") && line.Contains("[99]"));
        result.PlayerFinalDice.Should().Equal(50);
        result.PlayerScore.Should().Be(50);
    }

    [Fact]
    public void FumbleSkipsAlreadyClassifiedCriticalDiceWhenDestroyingNext()
    {
        var result = DuelEngine.Resolve([98, 1, 50], [11]);

        result.PlayerFinalDice.Should().Equal(200);
        result.PlayerScore.Should().Be(200);
        result.Events.Should().Contain(line => line.Contains("[98]") && line.Contains("[50]"));
        result.Events.Should().NotContain(line => line.Contains("[98]") && line.Contains("[1]"));
    }

    [Fact]
    public void MergedFumble_IsAppendedAfterOriginalDiceAndCannotDestroyCritical()
    {
        var result = DuelEngine.Resolve([28, 5, 18], [78, 65, 64, 67]);

        result.Events.Should().Contain(line => line.Contains("玩家的 [18] 吞掉 [78]，变为 [96]"));
        result.Events.Should().Contain(line => line.Contains("玩家的 [96] 大失败，但后面已没有可销毁的骰子"));
        result.Events.Should().NotContain(line => line.Contains("销毁了后续骰 [5]"));
        result.PlayerFinalDice.Should().Equal(28, 200);
        result.PlayerScore.Should().Be(228);
    }

    [Fact]
    public void SameTail_LowerDieConsumesHigherAndRestartsRules()
    {
        var result = DuelEngine.Resolve([13], [93]);
        result.PlayerRemaining.Should().Equal(6);
        result.MeguminRemaining.Should().BeEmpty();
        result.PlayerScore.Should().Be(6);
    }

    [Fact]
    public void PairAndTriple_UseWholeGroupMultipliers()
    {
        DuelEngine.Resolve([30, 30], [11]).PlayerScore.Should().Be(120);
        DuelEngine.Resolve([30, 30, 30], [11]).PlayerScore.Should().Be(360);
    }

    [Fact]
    public void CompetingFourKinds_UseHigherFace()
    {
        var result = DuelEngine.Resolve([41, 41, 41, 41], [52, 52, 52, 52]);
        result.Winner.Should().Be(DuelWinner.Megumin);
        result.PlayerFourKind.Should().Be(41);
        result.MeguminFourKind.Should().Be(52);
    }

    [Fact]
    public void Leaderboard_IsTopTenBalanceDescendingThenQqAscending()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.MeguminBalance = 0;
        state.MeguminDailyCash = 0;
        var commands = fixture.Mod.GetModManagementCommands().ToDictionary(x => x.Name);
        for (var i = 1; i <= 12; i++)
            commands["user"].Handler($"balance set {i} {i * 10}", new object());
        commands["user"].Handler("balance set 11 120", new object());

        var duel = fixture.Mod.GetCommandHandlers()["duel"];
        var result = duel("list", new FakeMessage { UserId = 99 });

        result.Should().Contain("1.昵称11(11)\n——120厄里斯。");
        result.Should().Contain("2.昵称12(12)\n——120厄里斯。");
        result.Should().NotContain("(1)\n——");
        result!.Split('\n').Count(x => x.StartsWith("——", StringComparison.Ordinal)).Should().Be(10);

        state.Players[11].Nickname.Should().Be("昵称11");
    }

    [Fact]
    public void DebtLeaderboard_ShowsOnlyDebtorsMostNegativeFirstThenQqAscending()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.MeguminBalance = 0;
        state.MeguminDailyCash = 0;
        var commands = fixture.Mod.GetModManagementCommands().ToDictionary(x => x.Name);
        for (var i = 1; i <= 12; i++)
            commands["user"].Handler($"balance set {i} {-i * 10}", new object());
        commands["user"].Handler("balance set 11 -120", new object());
        commands["user"].Handler("balance set 12 -120", new object());
        commands["user"].Handler("balance set 13 500", new object());

        var result = fixture.Mod.GetCommandHandlers()["duel"]("blist", new FakeMessage { UserId = 99 });

        result.Should().StartWith("财产负排名");
        result.Should().Contain("1.昵称11(11)\n——负债120厄里斯。");
        result.Should().Contain("2.昵称12(12)\n——负债120厄里斯。");
        result.Should().NotContain("(13)");
        result!.Split('\n').Count(line => line.StartsWith("——", StringComparison.Ordinal)).Should().Be(10);
    }

    [Fact]
    public void DebtLeaderboard_ReturnsConfiguredEmptyTextWithoutDebtors()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.MeguminBalance = 0;
        state.MeguminDailyCash = 0;

        fixture.Mod.GetCommandHandlers()["duel"]("blist", new FakeMessage { UserId = 99 })
            .Should().Be("负债排行榜暂时没有记录。");
    }

    [Fact]
    public void Leaderboard_MigratesPreviousBuiltInRowTemplate()
    {
        using var fixture = new ModFixture();
        var management = fixture.Mod.GetModManagementCommands().ToDictionary(command => command.Name);
        management["user"].Handler("balance set 42 700", new object());
        management["text"].Handler("set LeaderboardRow {rank}. QQ {qq} — {balance}厄里斯", new object());
        fixture.Mod.OnUnload();

        var reloaded = new MeguminDuelMod(new FakeContext());
        reloaded.SetPortableModContext(new PortableModContext(fixture.DirectoryPath));
        reloaded.OnLoad();
        try
        {
            reloaded.GetModManagementCommands().Single(command => command.Name == "text")
                .Handler("get LeaderboardRow", new object())
                .Should().Be("{rank}.{nickname}({qq})\n——{balance}厄里斯。");
            reloaded.GetCommandHandlers()["duel"]("list", new FakeMessage { UserId = 1 })
                .Should().Contain("昵称42(42)\n——700厄里斯。");
        }
        finally { reloaded.OnUnload(); }
    }

    [Fact]
    public void PortableDataDirectory_PersistsAdminChangesAcrossInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "megumin-duel-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = CreateMod(directory);
            first.GetModManagementCommands().Single(x => x.Name == "user").Handler("balance set 123 987", new object());
            first.OnUnload();
            var second = CreateMod(directory);
            second.GetCommandHandlers()["duel"]("me", new FakeMessage { UserId = 123 }).Should().Contain("余额：987厄里斯");
            second.OnUnload();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void DuelFlow_AllowsTwoCallsOneItemThenConsumesDailyTurnOnSettlement()
    {
        using var fixture = new ModFixture();
        var admin = fixture.Mod.GetModManagementCommands().ToDictionary(x => x.Name);
        admin["user"].Handler("item give 42 game_console 1", new object());
        var duel = fixture.Mod.GetCommandHandlers()["duel"];

        var startReply = duel("", new FakeMessage { UserId = 42 });
        startReply.Should().Contain("本局额度：200");
        var startLines = startReply!.Split('\n');
        startLines[1].Should().Be("今日机会计算:");
        startLines[4].Should().Be("玩家骰：");
        fixture.Mod.OnPrivateMessage(42, "use game_console")!.Reply.Should().Contain("游戏机");
        fixture.Mod.OnPrivateMessage(42, "call")!.Reply.Should().Contain("300");
        fixture.Mod.OnPrivateMessage(42, "call")!.Reply.Should().Contain("400");
        fixture.Mod.OnPrivateMessage(42, "call")!.Reply.Should().Contain("5颗骰");
        fixture.Mod.OnPrivateMessage(42, "duel!!")!.Reply.Should().Contain("开牌");
        duel("", new FakeMessage { UserId = 42 }).Should().Contain("本局额度：").And.Contain("今日机会计算");
    }

    [Fact]
    public void StartAndCall_ShowAvailableItemsAndBareUseWorksInsideFocus()
    {
        using var fixture = new ModFixture();
        var admin = fixture.Mod.GetModManagementCommands().ToDictionary(command => command.Name);
        admin["user"].Handler("item give 42 game_console 1", new object());
        var duel = fixture.Mod.GetCommandHandlers()["duel"];

        var startReply = duel("", new FakeMessage { UserId = 42 });
        startReply.Should().Contain("当前可用道具：").And.Contain("1. (SSR)游戏机 ×1")
            .And.Contain("介绍：立即为玩家额外投出一颗己方骰")
            .And.NotContain("使用方式：use");
        var callReply = fixture.Mod.OnPrivateMessage(42, "call")!.Reply;
        callReply.Should().Contain("1. (SSR)游戏机 ×1")
            .And.NotContain("介绍：")
            .And.NotContain("使用方式：use");
        callReply.Should().Contain("当前可用道具：").And.Contain("1. (SSR)游戏机 ×1");
        callReply.Split('\n').Should().ContainSingle(line => line == "操作：duel 开牌 / use <编号> 使用道具 / call 继续追加");
        fixture.Mod.OnPrivateMessage(42, "use 1")!.Reply.Should().Contain("游戏机");
        duel("", new FakeMessage { UserId = 42 }).Should().Contain("（本局已经使用过道具）");
    }

    [Fact]
    public void ExperiencedPlayer_DuelFeedbackHidesOperationAndMeguminItemDescription()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            DuelTotalCount = 6,
            Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["game_console"] = 1 },
            Session = new DuelSession
            {
                UserId = 42,
                StartingAmount = 200,
                ParticipationAmount = 200,
                PlayerDice = [31, 42, 53],
                MeguminDice = [21, 32, 43],
                MeguminItemId = "face_eraser"
            }
        };

        var reply = fixture.Mod.GetCommandHandlers()["duel"]("", new FakeMessage { UserId = 42 });

        reply.Should().NotContain("操作：")
            .And.Contain("能擦掉骰面的镜子")
            .And.NotContain("降低 20 点")
            .And.Contain("介绍：立即为玩家额外投出一颗己方骰");
    }

    [Fact]
    public void SecondRound_HidesDescriptionsFromPreparedAndTargetSelectionPrompts()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["game_console"] = 1,
                ["axis_order_emblem"] = 1
            },
            Session = new DuelSession
            {
                UserId = 42,
                StartingAmount = 200,
                ParticipationAmount = 300,
                Calls = 1,
                PlayerDice = [31, 42, 53, 64],
                MeguminDice = [21, 32, 43, 54],
                MeguminItemId = "",
                PlayerExtraItemUses = 2
            }
        };

        var prepared = fixture.Mod.OnPrivateMessage(42, "use game_console")!.Reply;
        prepared.Should().Contain("已取出");
        prepared.Should().NotContain("介绍：").And.NotContain("立即为玩家额外投出一颗己方骰");

        var targetPrompt = fixture.Mod.OnPrivateMessage(42, "use axis_order_emblem")!.Reply;
        targetPrompt.Should().Contain("请选择要作用的对方骰子");
        targetPrompt.Should().NotContain("介绍：").And.NotContain("重投它的十位数字");
    }

    [Fact]
    public void DuelStatistics_RecordWinsLossesTiesAndPersistAcrossReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "megumin-duel-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var mod = CreateMod(directory);
            var state = (DuelPersistentState)typeof(MeguminDuelMod)
                .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mod)!;
            state.MeguminBalance = 500;
            state.MeguminDailyCash = 500;
            state.MeguminCashDate = DateTime.Now.ToString("yyyy-MM-dd");
            state.Players[42] = new PlayerState
            {
                Balance = 500,
                Session = new DuelSession
                {
                    UserId = 42,
                    StartingAmount = 100,
                    ParticipationAmount = 100,
                    PlayerDice = [53],
                    MeguminDice = [21],
                    MeguminItemId = ""
                }
            };

            mod.OnPrivateMessage(42, "duel!!")!.Reply.Should().Contain("玩家获胜");
            state.Players[42].Session = new DuelSession
            {
                UserId = 42,
                StartingAmount = 100,
                ParticipationAmount = 100,
                PlayerDice = [21],
                MeguminDice = [53],
                MeguminItemId = "",
                PlayerItems = [new PlayerItemUse { ItemId = "divine_hood" }]
            };
            mod.OnPrivateMessage(42, "duel!!")!.Reply.Should().Contain("实际支付20厄里斯");
            state.Players[42].Session = new DuelSession
            {
                UserId = 42,
                StartingAmount = 100,
                ParticipationAmount = 100,
                PlayerDice = [12, 45],
                MeguminDice = [23, 34],
                MeguminItemId = ""
            };
            mod.OnPrivateMessage(42, "duel!!")!.Reply.Should().Contain("双方同分");

            var profile = mod.GetCommandHandlers()["duel"]("me", new FakeMessage { UserId = 42 });
            profile.Should().Contain("对局统计：3场（胜1/负1/平1）")
                .And.Contain("累计赢得：100厄里斯；累计输掉：20厄里斯");
            mod.OnUnload();

            var reloaded = CreateMod(directory);
            try
            {
                reloaded.GetCommandHandlers()["duel"]("me", new FakeMessage { UserId = 42 })
                    .Should().Contain("对局统计：3场（胜1/负1/平1）")
                    .And.Contain("累计赢得：100厄里斯；累计输掉：20厄里斯");
            }
            finally { reloaded.OnUnload(); }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void TargetedItem_EntersNumberedDiceSelectionFocusBeforeConsumption()
    {
        using var fixture = new ModFixture();
        var admin = fixture.Mod.GetModManagementCommands().ToDictionary(command => command.Name);
        admin["user"].Handler("item give 42 flower 1", new object());
        var duel = fixture.Mod.GetCommandHandlers()["duel"];
        duel("", new FakeMessage { UserId = 42 });

        var prompt = fixture.Mod.OnPrivateMessage(42, "use 1")!.Reply;
        prompt.Should().Contain("已选择「花」").And.Contain("玩家骰：\n[1:").And.Contain("] [2:").And.Contain("惠惠骰：\n[1:");
        duel("me", new FakeMessage { UserId = 42 }).Should().Contain("(R)花 ×1");

        fixture.Mod.OnPrivateMessage(42, "duel!!")!.Reply.Should().Contain("骰子序号无效").And.Contain("玩家骰：\n[1:").And.Contain("] [2:");
        fixture.Mod.OnPrivateMessage(42, "2")!.Reply.Should().Contain("已取出「花」");
        duel("me", new FakeMessage { UserId = 42 }).Should().NotContain("(R)花 ×1").And.Contain("进行中：有");
    }

    [Fact]
    public void ItemRarities_AreLoadedFromJsonAndNewItemsAreListed()
    {
        using var fixture = new ModFixture();
        var list = fixture.Mod.GetModManagementCommands().Single(command => command.Name == "item")
            .Handler("list", new object());

        list.Should().Contain("(SSR)阿克西斯教圣徽")
            .And.Contain("(SR)安乐少女手办")
            .And.Contain("(SR)歧路思义眼")
            .And.Contain("(SSR)开裂骰子")
            .And.Contain("(SR)灌铅骰子")
            .And.Contain("(SR)薛定谔的骰子")
            .And.Contain("(SR)恶魔之心")
            .And.Contain("(SR)神圣兜帽");
    }

    [Fact]
    public void DemonHeart_GrantsTwoMoreItemUsesAndAllSelectedEffectsResolve()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["demon_heart"] = 1,
                ["kneeling_mat"] = 2,
                ["peaceful_girl_figure"] = 1
            },
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [55],
                MeguminDice = [40],
                MeguminItemId = ""
            }
        };

        fixture.Mod.OnPrivateMessage(42, "use demon_heart")!.Reply
            .Should().Contain("额外获得两次道具使用机会").And.Contain("还可使用2件道具");
        fixture.Mod.OnPrivateMessage(42, "use kneeling_mat")!.Reply.Should().Contain("已取出「土下座用的垫子」");
        fixture.Mod.OnPrivateMessage(42, "use peaceful_girl_figure")!.Reply.Should().Contain("已取出「安乐少女手办」");
        fixture.Mod.OnPrivateMessage(42, "use kneeling_mat")!.Reply.Should().Contain("道具使用次数已用完（3/3）");

        var result = fixture.Mod.OnPrivateMessage(42, "duel!")!.Reply;
        result.Should().Contain("土下座")
            .And.Contain("最终分数 +20");
        state.Players[42].Balance.Should().Be(600);
    }

    [Fact]
    public void DivineHood_ReducesOnlyLossPaymentByEightyPercent()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Balance = 500,
            Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["divine_hood"] = 1 },
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 300,
                PlayerDice = [41],
                MeguminDice = [80],
                MeguminItemId = ""
            }
        };
        var initialMeguminBalance = state.MeguminBalance;

        fixture.Mod.OnPrivateMessage(42, "use divine_hood")!.Reply.Should().Contain("神圣兜帽");
        var result = fixture.Mod.OnPrivateMessage(42, "duel!")!.Reply;

        result.Should().Contain("原需支付300厄里斯")
            .And.Contain("实际支付60厄里斯")
            .And.Contain("惠惠获胜");
        state.Players[42].Balance.Should().Be(440);
        state.MeguminBalance.Should().Be(initialMeguminBalance + 60);
    }

    [Fact]
    public void DivineHood_DoesNotReduceTransferWhenPlayerWins()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Balance = 500,
            Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["divine_hood"] = 1 },
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 300,
                PlayerDice = [1],
                MeguminDice = [45],
                MeguminItemId = ""
            }
        };
        var initialMeguminBalance = state.MeguminBalance;

        fixture.Mod.OnPrivateMessage(42, "use divine_hood")!.Reply.Should().Contain("神圣兜帽");
        var result = fixture.Mod.OnPrivateMessage(42, "duel!")!.Reply;

        result.Should().Contain("玩家获胜").And.NotContain("原需支付300厄里斯");
        state.Players[42].Balance.Should().Be(800);
        state.MeguminBalance.Should().Be(initialMeguminBalance - 300);
    }

    [Fact]
    public void CrackedDie_SplitsSelectedDieIntoNearlyEqualDiceWithoutChangingTotal()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["cracked_die"] = 1 },
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [51],
                MeguminDice = [],
                MeguminItemId = ""
            }
        };

        fixture.Mod.OnPrivateMessage(42, "use cracked_die")!.Reply.Should().Contain("请选择要作用的己方骰子");
        fixture.Mod.OnPrivateMessage(42, "1")!.Reply.Should().Contain("已取出「开裂骰子」");
        var result = fixture.Mod.OnPrivateMessage(42, "duel!")!.Reply;

        result.Should().Contain("开裂骰子将己方骰 1 的 [51] 分成了 [26] 和 [25]")
            .And.Contain("玩家最终骰：\n[1:26] [2:25]")
            .And.Contain("得分：51");
    }

    [Fact]
    public void LoadedDie_RerollsAndKeepsTheHigherOfOldAndNewValues()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["loaded_die"] = 1 },
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [80],
                MeguminDice = [],
                MeguminItemId = ""
            }
        };

        fixture.Mod.OnPrivateMessage(42, "use loaded_die")!.Reply.Should().Contain("灌铅骰子");
        fixture.Mod.OnPrivateMessage(42, "1")!.Reply.Should().Contain("已取出「灌铅骰子」");
        var result = fixture.Mod.OnPrivateMessage(42, "duel!")!.Reply;
        var match = System.Text.RegularExpressions.Regex.Match(result,
            @"灌铅骰子重投己方骰 1：\[80\] 与新值 \[(\d+)\] 比较，保留 \[(\d+)\]");

        match.Success.Should().BeTrue(result);
        int.Parse(match.Groups[1].Value).Should().BeInRange(1, 100);
        int.Parse(match.Groups[2].Value).Should().BeGreaterThanOrEqualTo(80);
        int.Parse(match.Groups[2].Value).Should().Be(Math.Max(80, int.Parse(match.Groups[1].Value)));
    }

    [Fact]
    public void SchrodingerDie_RerollsSelectedDieAndUsesTheNewValue()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["schrodinger_die"] = 1 },
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [50],
                MeguminDice = [],
                MeguminItemId = ""
            }
        };

        fixture.Mod.OnPrivateMessage(42, "use schrodinger_die")!.Reply.Should().Contain("薛定谔的骰子");
        fixture.Mod.OnPrivateMessage(42, "1")!.Reply.Should().Contain("已取出「薛定谔的骰子」");
        var result = fixture.Mod.OnPrivateMessage(42, "duel!")!.Reply;
        var match = System.Text.RegularExpressions.Regex.Match(result,
            @"薛定谔的骰子重投己方骰 1：\[50\] 变为 \[(\d+)\]");

        match.Success.Should().BeTrue(result);
        int.Parse(match.Groups[1].Value).Should().BeInRange(1, 100);
    }

    [Fact]
    public void AxisOrderEmblem_TargetsOpponentDieAndRerollsOnlyItsTensDigit()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["axis_order_emblem"] = 1 },
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [40],
                MeguminDice = [57],
                MeguminItemId = ""
            }
        };

        fixture.Mod.OnPrivateMessage(42, "use axis_order_emblem")!.Reply
            .Should().Contain("对方骰子").And.Contain("惠惠骰：");
        fixture.Mod.OnPrivateMessage(42, "1")!.Reply.Should().Contain("已取出「阿克西斯教圣徽」");
        var settlement = fixture.Mod.OnPrivateMessage(42, "duel!!")!.Reply;
        var match = System.Text.RegularExpressions.Regex.Match(settlement, @"惠惠第 1 颗骰的十位：\[57\] → \[(\d+)\]");
        match.Success.Should().BeTrue(settlement);
        var changedValue = int.Parse(match.Groups[1].Value);
        (changedValue % 10).Should().Be(7);
    }

    [Fact]
    public void ForkedPathEye_RevealsMeguminFirstDieImmediatelyAndPersistsForSession()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["forked_path_eye"] = 1 },
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [40],
                MeguminDice = [52],
                MeguminItemId = ""
            }
        };

        fixture.Mod.OnPrivateMessage(42, "use forked_path_eye")!.Reply.Should().Contain("个位数：2");
        fixture.Mod.GetCommandHandlers()["duel"]("", new FakeMessage { UserId = 42 })
            .Should().Contain("惠惠骰：\n[1:52]");
        fixture.Mod.OnPrivateMessage(42, "use forked_path_eye")!.Reply.Should().Contain("本局已经使用过一件道具");
    }

    [Fact]
    public void PeacefulGirlFigure_AddsTwentyToFinalScore()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Inventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["peaceful_girl_figure"] = 1 },
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [20],
                MeguminDice = [11],
                MeguminItemId = ""
            }
        };

        fixture.Mod.OnPrivateMessage(42, "use peaceful_girl_figure")!.Reply.Should().Contain("安乐少女手办");
        fixture.Mod.OnPrivateMessage(42, "duel!!")!.Reply.Should().Contain("最终分数 +20").And.Contain("得分：20(+20)");
    }

    [Fact]
    public void Settlement_ShowsFinalDiceSequenceNumbersOnSingleLine()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Balance = 500,
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [20, 30],
                MeguminDice = [11],
                MeguminItemId = ""
            }
        };

        var result = fixture.Mod.OnPrivateMessage(42, "duel!!")!.Reply;
        System.Text.RegularExpressions.Regex.IsMatch(result, @"玩家最终骰：\r?\n\[1:20\] \[2:30\]").Should().BeTrue();
        System.Text.RegularExpressions.Regex.IsMatch(result, @"惠惠最终骰：\r?\n\[1:11\]").Should().BeTrue();
    }

    [Fact]
    public void Settlement_ShowsBaseScoreAndExplosionBonusSeparately()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Balance = 500,
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [1],
                MeguminDice = [90, 80, 70, 60, 45],
                MeguminItemId = "explosion",
                MeguminItemIds = ["explosion"]
            }
        };

        var result = fixture.Mod.OnPrivateMessage(42, "duel!!")!.Reply;
        var explosion = System.Text.RegularExpressions.Regex.Match(result, @"最终得分追加了 \[(\d+)\] 点");
        explosion.Success.Should().BeTrue(result);
        var bonus = explosion.Groups[1].Value;
        int.Parse(bonus).Should().BeInRange(1, 50);
        result.Should().Contain($"得分：345(+{bonus})").And.Contain("惠惠获胜");
    }

    [Fact]
    public void MeguminMirror_ReducesOneOfTiedHighestDiceByTwoTens()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Balance = 500,
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [80, 80, 20],
                MeguminDice = [11],
                MeguminItemId = "face_eraser",
                MeguminItemIds = ["face_eraser"]
            }
        };

        var result = fixture.Mod.OnPrivateMessage(42, "duel!!")!.Reply;

        System.Text.RegularExpressions.Regex.IsMatch(result, @"镜子削减玩家最大骰的十位数：第 [12] 颗骰从 \[80\] 变为 \[60\]").Should().BeTrue(result);
        fixture.Mod.GetModManagementCommands().Single(command => command.Name == "text")
            .Handler("get ItemDescription_face_eraser", new object())
            .Should().Be("惠惠用镜子将玩家场上点数最高的一颗骰降低 20 点（十位数削减 2），最低保留 1。");
    }

    [Fact]
    public void VanirMask_MasksSecondDieOnlyWhenSelectedAndKeepsItMaskedAfterFirstIsRevealed()
    {
        using var fixture = new ModFixture();
        DuelCatalog.MeguminItems.Should().Contain("vanir_mask");
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [11, 22, 33],
                MeguminDice = [41, 52, 63],
                MeguminItemId = "vanir_mask",
                MeguminItemIds = ["vanir_mask"]
            }
        };
        state.Players[43] = new PlayerState
        {
            Session = new DuelSession
            {
                UserId = 43,
                ParticipationAmount = 100,
                PlayerDice = [11, 22, 33],
                MeguminDice = [41, 52, 63],
                MeguminItemId = "",
                MeguminItemIds = []
            }
        };
        var admin = fixture.Mod.GetModManagementCommands().ToDictionary(command => command.Name);
        admin["user"].Handler("item give 42 forked_path_eye", new object());
        var duel = fixture.Mod.GetCommandHandlers()["duel"];

        var start = duel("", new FakeMessage { UserId = 42 })!;
        start.Should().Contain("巴尼尔的假面：");
        var startLines = start.Split('\n');
        var startDiceLine = startLines[Array.IndexOf(startLines, "惠惠骰：") + 1];
        startDiceLine.Should().MatchRegex(@"^\[1:(?:\d{1,2})?\*\] \[2:(?:\d{1,2})?\*\] \[3:\d{1,3}\]$");

        var unmaskedStatus = duel("", new FakeMessage { UserId = 43 })!;
        unmaskedStatus.Should().Contain("惠惠公开道具：无");
        var unmaskedLines = unmaskedStatus.Split('\n');
        unmaskedLines[Array.IndexOf(unmaskedLines, "惠惠骰：") + 1]
            .Should().Be("[1:4*] [2:52] [3:63]");

        fixture.Mod.OnPrivateMessage(42, "use 1")!.Reply.Should().Contain("首骰的个位数");
        var status = duel("", new FakeMessage { UserId = 42 })!;
        var statusLines = status.Split('\n');
        var statusDiceLine = statusLines[Array.IndexOf(statusLines, "惠惠骰：") + 1];
        statusDiceLine.Should().MatchRegex(@"^\[1:\d{1,3}\] \[2:(?:\d{1,2})?\*\] \[3:\d{1,3}\]$");
    }

    [Fact]
    public void MeguminJar_ReportsWhetherSimulationKeepsOrReplacesLowestDie()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Balance = 500,
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [33, 34, 35],
                MeguminDice = [21, 22, 23],
                MeguminItemId = "megumin_jar",
                MeguminItemIds = ["megumin_jar"]
            }
        };

        var result = fixture.Mod.OnPrivateMessage(42, "duel")!.Reply;
        result.Should().Contain("掏出一颗骰并且投出了");
        (result.Contains("模拟结果认为保留新骰更有利") || result.Contains("模拟后认为更换更有利"))
            .Should().BeTrue(result);
    }

    [Fact]
    public void DuelFocus_OnlyExplicitDuelWithOptionalExclamationMarksSettles()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Balance = 500,
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [20],
                MeguminDice = [11],
                MeguminItemId = ""
            }
        };

        fixture.Mod.OnPrivateMessage(42, "open")!.Reply.Should().Contain("尚未开牌");
        fixture.Mod.OnPrivateMessage(42, "duel?!!")!.Reply.Should().Contain("尚未开牌");
        state.Players[42].Session.Should().NotBeNull();
        state.Players[42].DuelCount.Should().Be(0);

        fixture.Mod.OnPrivateMessage(42, "Duel！！")!.Reply.Should().Contain("开牌！");
        state.Players[42].Session.Should().BeNull();
        state.Players[42].DuelCount.Should().Be(1);
    }

    [Fact]
    public void DuelFocus_IgnoresOutOfCharacterMessagesStartingWithParenthesis()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 100,
                PlayerDice = [20],
                MeguminDice = [11],
                MeguminItemId = ""
            }
        };

        fixture.Mod.OnPrivateMessage(42, "  (这局骰子真多)").Should().BeNull();
        fixture.Mod.OnGroupMessage(123, 42, "（场外吐槽）", false).Should().BeNull();

        state.Players[42].Session.Should().NotBeNull();
        state.Players[42].DuelCount.Should().Be(0);
    }

    [Fact]
    public void Explosion_AdjustsCoreTrustUsingScoreMinusTwentyOverOneHundred()
    {
        using var fixture = new ModFixture();
        fixture.Context.SetTrust(42, 10);

        var reply = fixture.Mod.GetCommandHandlers()["explosion"]("", new FakeMessage { UserId = 42 })!;
        var score = int.Parse(System.Text.RegularExpressions.Regex.Match(reply, @"评分：(\d+)").Groups[1].Value);
        var delta = (score - 20) / 100d;

        fixture.Context.GetUserTrust(42).Should().BeApproximately(10 + delta, 0.000001);
        reply.Should().Contain($"信任度变化：({score}-20)/100={delta.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture)}");
    }

    [Fact]
    public void DuelAllowance_UsesTrustLogWithMinusOneFloorAndDisplaysD3Calculation()
    {
        using var fixture = new ModFixture();
        fixture.Context.SetTrust(42, 0.01);
        var duel = fixture.Mod.GetCommandHandlers()["duel"];
        duel("me", new FakeMessage { UserId = 42 });
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        var player = state.Players[42];
        player.DuelDate = DateTime.Now.ToString("yyyy-MM-dd");
        player.DuelD3Date = player.DuelDate;
        player.DuelD3Roll = 3;

        var profile = duel("me", new FakeMessage { UserId = 42 });

        profile.Should().Contain("今日机会计算:\nlg(信任度)+D3-1=lg(0.1)+3-1=1")
            .And.Contain("已用:0/1")
            .And.NotContain("floor(");

        player.DuelCount = 1;
        duel("me", new FakeMessage { UserId = 42 }).Should().Contain("已用:1/1");
        duel("", new FakeMessage { UserId = 42 })
            .Should().Contain("今天的骰子对决机会已用完。\n今日机会计算:")
            .And.Contain("已用:1/1");
    }

    [Fact]
    public void DuelAllowance_ProvidesAtLeastOneDailyDuelAndMarksTheFallback()
    {
        using var fixture = new ModFixture();
        fixture.Context.SetTrust(42, 6.83);
        var duel = fixture.Mod.GetCommandHandlers()["duel"];
        duel("me", new FakeMessage { UserId = 42 });

        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        var player = state.Players[42];
        player.DuelDate = DateTime.Now.ToString("yyyy-MM-dd");
        player.DuelD3Date = player.DuelDate;
        player.DuelD3Roll = 1;

        var profile = duel("me", new FakeMessage { UserId = 42 });
        profile.Should().Contain("今日机会计算:\nlg(信任度)+D3-1=lg(6.83)+1-1=0\n已用:0/1(保底)");

        duel("", new FakeMessage { UserId = 42 }).Should().Contain("玩家骰：");
        fixture.Mod.OnPrivateMessage(42, "duel")!.Reply.Should().Contain("开牌！");
        duel("", new FakeMessage { UserId = 42 })
            .Should().Contain("今天的骰子对决机会已用完。")
            .And.Contain("已用:1/1(保底)");

        fixture.Context.SetTrust(43, 0.01);
        duel("me", new FakeMessage { UserId = 43 });
        var lowTrustPlayer = state.Players[43];
        lowTrustPlayer.DuelDate = DateTime.Now.ToString("yyyy-MM-dd");
        lowTrustPlayer.DuelD3Date = lowTrustPlayer.DuelDate;
        lowTrustPlayer.DuelD3Roll = 1;
        duel("me", new FakeMessage { UserId = 43 })
            .Should().Contain("lg(信任度)+D3-1=lg(0.1)+1-1=-1")
            .And.Contain("已用:0/1(保底)");
        duel("", new FakeMessage { UserId = 43 }).Should().Contain("玩家骰：");
    }

    [Fact]
    public void Shop_SeparatesEachOfferWithOneBlankLine()
    {
        using var fixture = new ModFixture();
        var reply = fixture.Mod.GetCommandHandlers()["duel"]("shop", new FakeMessage { UserId = 42 });
        var lines = reply!.Split('\n');
        reply.Should().NotContain("使用方式：");

        foreach (var offerNumber in new[] { 1, 2, 3 })
        {
            var offerIndex = Array.FindIndex(lines, line => line.StartsWith($"#{offerNumber}. ", StringComparison.Ordinal));
            offerIndex.Should().BeGreaterThan(0);
            lines[offerIndex + 1].Should().MatchRegex(@"^-\d+ 厄里斯 \[.+\] (已售出|可购买)$");
            lines[offerIndex + 2].Should().StartWith("   介绍：");
            if (offerNumber == 1) continue;
            lines[offerIndex - 1].Should().BeEmpty();
            lines[offerIndex - 2].Should().StartWith("   介绍：");
        }
    }

    [Fact]
    public void ShopRefresh_CostIncreasesByFiftyAndPurchasedSlotsStaySoldOutForTheDay()
    {
        using var fixture = new ModFixture();
        var duel = fixture.Mod.GetCommandHandlers()["duel"];
        var message = new FakeMessage { UserId = 42 };

        duel("shop", message).Should().Contain("下次刷新50厄里斯");
        var purchase = duel("shop buy 1", message)!;
        purchase.Should().Contain("购入");
        var itemPrice = int.Parse(System.Text.RegularExpressions.Regex.Match(purchase, @"支付(\d+)厄里斯").Groups[1].Value);
        var afterFirstRefresh = 500 - itemPrice - 50;
        duel("shop refresh", message)
            .Should().Contain("今日第1次")
            .And.Contain("下次刷新100厄里斯");
        duel("me", message).Should().Contain($"余额：{afterFirstRefresh}厄里斯");
        duel("shop buy 1", message).Should().Contain("已经售空");
        duel("shop refresh", message)
            .Should().Contain("今日第2次")
            .And.Contain("下次刷新150厄里斯");
        duel("me", message).Should().Contain($"余额：{afterFirstRefresh - 100}厄里斯");
    }

    [Fact]
    public void ShopRefresh_ResetsItsPriceSequenceOnTheNextLocalDay()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.Players[42] = new PlayerState
        {
            Balance = 500,
            ShopRefreshDate = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd"),
            ShopRefreshCount = 4
        };

        fixture.Mod.GetCommandHandlers()["duel"]("shop", new FakeMessage { UserId = 42 })
            .Should().Contain("下次刷新50厄里斯");
        state.Players[42].ShopRefreshCount.Should().Be(0);
    }

    [Fact]
    public void TargetedItemSelection_CanBeCancelledWithoutConsumption()
    {
        using var fixture = new ModFixture();
        var admin = fixture.Mod.GetModManagementCommands().ToDictionary(command => command.Name);
        admin["user"].Handler("item give 42 unstable_die 1", new object());
        var duel = fixture.Mod.GetCommandHandlers()["duel"];
        duel("", new FakeMessage { UserId = 42 });
        fixture.Mod.OnPrivateMessage(42, "use 1");

        fixture.Mod.OnPrivateMessage(42, "cancel")!.Reply.Should().Contain("没有消耗道具");
        duel("me", new FakeMessage { UserId = 42 }).Should().Contain("不稳定的骰子 ×1").And.Contain("进行中：有");
    }

    [Fact]
    public void Explosion_IsLimitedOncePerLocalDay()
    {
        using var fixture = new ModFixture();
        var explosion = fixture.Mod.GetCommandHandlers()["explosion"];
        explosion("", new FakeMessage { UserId = 7 }).Should().Contain("评分：");
        explosion("", new FakeMessage { UserId = 7 }).Should().Contain("已经释放过");
    }

    [Fact]
    public void FeedbackRandomSyntax_IsResolvedBeforeReply()
    {
        using var fixture = new ModFixture();
        var result = fixture.Mod.GetCommandHandlers()["duel"]("", new FakeMessage { UserId = 77 });
        result.Should().NotContain("||").And.NotContain("[来吧");
        result.Should().Contain("本局额度：200厄里斯")
            .And.Contain("玩家存款：500厄里斯")
            .And.Contain("惠惠存款：500(0)厄里斯");
        System.Text.RegularExpressions.Regex.IsMatch(result!, @"玩家骰：\r?\n\[1:\d+\] \[2:\d+\] \[3:\d+\]").Should().BeTrue();
        System.Text.RegularExpressions.Regex.IsMatch(result!, @"惠惠骰：\r?\n\[1:(?:\d+)?\*\] \[2:(?:\d{1,2}\*|\d{1,3})\] \[3:\d+\]").Should().BeTrue();
    }

    [Fact]
    public void Use_IsFocusOnlyAndNotRegisteredAsStandaloneCommand()
    {
        using var fixture = new ModFixture();
        fixture.Mod.GetCommandHandlers().Keys.Should().Contain(["duel", "explosion", "iou"]).And.NotContain("lou").And.NotContain("use");
    }

    [Fact]
    public void FocusPrompts_UseBareUseSpellingAndMigrateOldSavedTemplates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "megumin-duel-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new MeguminDuelMod(new FakeContext(isDiceAdministrator: true));
            first.SetPortableModContext(new PortableModContext(directory));
            first.OnLoad();
            var textCommands = first.GetModManagementCommands().Single(command => command.Name == "text");
            textCommands.Handler("set CustomHelp Use .use here", new object());
            first.OnUnload();

            var reloaded = new MeguminDuelMod(new FakeContext(isDiceAdministrator: true));
            reloaded.SetPortableModContext(new PortableModContext(directory));
            reloaded.OnLoad();
            try
            {
                var texts = reloaded.GetModManagementCommands().Single(command => command.Name == "text");
                texts.Handler("get CustomHelp", new object()).Should().Be("Use use here");

                var start = reloaded.GetCommandHandlers()["duel"]("", new FakeMessage { UserId = 42 });
                start.Should().NotContain(".use").And.Contain("/ use <序号>");
                reloaded.OnPrivateMessage(42, "use")!.Reply.Should().Contain("可用道具");
            }
            finally { reloaded.OnUnload(); }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void TailMerges_AreProcessedFromSmallestEligibleValue()
    {
        var result = DuelEngine.Resolve([28, 13], [58, 93]);
        result.Events[0].Should().Contain("[13] 吞掉 [93]");
        result.Events[1].Should().Contain("[28] 吞掉 [58]");
        result.PlayerFinalDice.Should().Equal(6, 86);
    }

    [Fact]
    public void TailMerges_NeverConsumeDiceOnTheSameSide()
    {
        var noOpponentMatch = DuelEngine.Resolve([13, 23], [44]);
        noOpponentMatch.PlayerFinalDice.Should().Equal(13, 23);
        noOpponentMatch.MeguminFinalDice.Should().Equal(44);
        noOpponentMatch.Events.Should().NotContain(line => line.Contains("吞掉"));

        var oneOpponentMatch = DuelEngine.Resolve([13, 23], [93]);
        oneOpponentMatch.PlayerFinalDice.Should().Equal(23, 6);
        oneOpponentMatch.MeguminFinalDice.Should().BeEmpty();
        oneOpponentMatch.Events.Should().ContainSingle(line => line.Contains("吞掉"));
    }

    [Fact]
    public void DuelReset_ClearsOnlyTodaysDuelRecordsAndKeepsEconomy()
    {
        using var fixture = new ModFixture(isDiceAdministrator: true);
        var admin = fixture.Mod.GetModManagementCommands().ToDictionary(x => x.Name);
        admin["user"].Handler("balance set 42 999", new object());
        admin["megumin"].Handler("balance set 777", new object());
        fixture.Mod.GetCommandHandlers()["duel"]("", new FakeMessage { UserId = 42 });

        fixture.Mod.GetCommandHandlers()["duel"]("reset", new FakeMessage { UserId = 1 }).Should().Contain("余额、库存、欠条");
        var profile = fixture.Mod.GetCommandHandlers()["duel"]("me", new FakeMessage { UserId = 42 });
        profile.Should().Contain("余额：999厄里斯").And.Contain("进行中：无").And.Contain("今日机会计算");
        profile.Should().Contain("已用:0/");
        fixture.Mod.GetCommandHandlers()["duel"]("list", new FakeMessage { UserId = 1 }).Should().Contain("昵称42(42)\n——999厄里斯。");
        admin["megumin"].Handler("balance get", new object()).Should().Contain("777");
    }

    [Fact]
    public void DuelGet_GrantsDiceAdminFiveHundredErisAndRejectsOtherUsers()
    {
        using (var regularUserFixture = new ModFixture())
        {
            var duel = regularUserFixture.Mod.GetCommandHandlers()["duel"];
            duel("get", new FakeMessage { UserId = 42 }).Should().Contain("权限不足");
            duel("me", new FakeMessage { UserId = 42 }).Should().Contain("余额：500厄里斯");
        }

        using (var adminFixture = new ModFixture(isDiceAdministrator: true))
        {
            var duel = adminFixture.Mod.GetCommandHandlers()["duel"];
            duel("get", new FakeMessage { UserId = 7 }).Should().Contain("500厄里斯").And.Contain("当前余额：1000厄里斯");
            duel("get", new FakeMessage { UserId = 7 }).Should().Contain("当前余额：1500厄里斯");
            duel("me", new FakeMessage { UserId = 7 }).Should().Contain("余额：1500厄里斯");
        }
    }

    [Fact]
    public void MeguminWithoutDailyCash_IssuesCollectibleIouInsteadOfCreatingMoney()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.MeguminBalance = 0;
        state.MeguminDailyCash = 0;
        state.MeguminCashDate = DateTime.Now.ToString("yyyy-MM-dd");
        state.Players[42] = new PlayerState
        {
            Balance = 500,
            DuelDate = DateTime.Now.ToString("yyyy-MM-dd"),
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 200,
                PlayerDice = [1, 2, 3],
                MeguminDice = [50],
                MeguminItemId = "megumin_jar"
            }
        };

        fixture.Mod.OnPrivateMessage(42, "duel!!")!.Reply.Should().Contain("200厄里斯的欠条");
        fixture.Mod.GetCommandHandlers()["iou"]("", new FakeMessage { UserId = 42 })
            .Should().Contain("惠惠欠条收藏（1张）").And.Contain("200厄里斯");
        fixture.Mod.GetCommandHandlers()["duel"]("me", new FakeMessage { UserId = 42 }).Should().Contain("余额：500厄里斯");
    }

    [Fact]
    public void IouTextSelection_UsesConfiguredWeights()
    {
        var candidates = new[]
        {
            new IouText { Id = "one", Weight = 1 },
            new IouText { Id = "three", Weight = 3 }
        };
        var selector = typeof(MeguminDuelMod).GetMethod("SelectWeightedIouText", BindingFlags.Static | BindingFlags.NonPublic)!;

        ((IouText)selector.Invoke(null, [candidates, 0L])!).Id.Should().Be("one");
        ((IouText)selector.Invoke(null, [candidates, 1L])!).Id.Should().Be("three");
        ((IouText)selector.Invoke(null, [candidates, 3L])!).Id.Should().Be("three");
    }

    [Fact]
    public void IouTextManagement_CanSetAndListWeight()
    {
        using var fixture = new ModFixture();
        var iouText = fixture.Mod.GetModManagementCommands().Single(command => command.Name == "iou-text");

        iouText.Handler("add rich 500 0 从500厄里斯开始无限适用", new object()).Should().Contain("500+（无限）");
        iouText.Handler("weight generic 7", new object()).Should().Contain("权重为 7");
        iouText.Handler("list", new object()).Should().Contain("generic: 0+（无限） weight=7")
            .And.Contain("rich: 500+（无限） weight=1");
        iouText.Handler("weight generic 0", new object()).Should().Contain("大于 0 的整数");
    }

    [Fact]
    public void IouText_DurationZeroMatchesEveryAmountFromItsStart()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.IouTexts = [new IouText { Id = "unlimited", Start = 500, Duration = 0, Weight = 1, Text = "无限文本" }];
        var selector = typeof(MeguminDuelMod).GetMethod("SelectIouTextLocked", BindingFlags.Instance | BindingFlags.NonPublic)!;

        selector.Invoke(fixture.Mod, [500]).Should().Be("无限文本");
        selector.Invoke(fixture.Mod, [50_000]).Should().Be("无限文本");
        selector.Invoke(fixture.Mod, [499]).Should().Be(state.Texts["IouFallback"]);
    }

    [Fact]
    public void MeguminDailyCash_IsSharedStateWithinConfiguredRangeAndCappedByBalance()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.MeguminBalance = 10_000;
        state.MeguminCashDate = null;
        fixture.Mod.GetCommandHandlers()["duel"]("me", new FakeMessage { UserId = 1 });
        state.MeguminDailyCash.Should().BeInRange(1000, 2000);

        state.MeguminBalance = 600;
        state.MeguminCashDate = null;
        fixture.Mod.GetCommandHandlers()["duel"]("me", new FakeMessage { UserId = 2 });
        state.MeguminDailyCash.Should().Be(600);
    }

    [Fact]
    public void MeguminBalance_DisplaysCashAndSavingsAndLeaderboardUsesTheirTotal()
    {
        using var fixture = new ModFixture();
        var state = (DuelPersistentState)typeof(MeguminDuelMod)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Mod)!;
        state.MeguminBalance = 7_200;
        state.MeguminDailyCash = 2_000;
        state.MeguminCashDate = DateTime.Now.ToString("yyyy-MM-dd");
        state.Players[42] = new PlayerState
        {
            Balance = 500,
            Session = new DuelSession
            {
                UserId = 42,
                ParticipationAmount = 300,
                PlayerDice = [11],
                MeguminDice = [30],
                MeguminItemId = ""
            }
        };

        var result = fixture.Mod.OnPrivateMessage(42, "duel!!")!.Reply;
        result.Should().Contain("惠惠存款：2000(5500)厄里斯");
        state.MeguminBalance.Should().Be(7_500);
        state.MeguminDailyCash.Should().Be(2_000);
        fixture.Mod.GetCommandHandlers()["duel"]("list", new FakeMessage { UserId = 42 })
            .Should().Contain("惠惠(骰娘)\n——7500厄里斯。");
    }

    [Fact]
    public void ManagementTextSet_PreservesCaseAndDecodesNewlines()
    {
        using var fixture = new ModFixture();
        var text = fixture.Mod.GetModManagementCommands().Single(x => x.Name == "text");
        text.Handler("set LeaderboardTitle MiXeD\\nSecondLine", new object());
        text.Handler("get LeaderboardTitle", new object()).Should().Be("MiXeD\nSecondLine");
    }

    [Fact]
    public void DuelResetText_ReloadsPackagedJsonWithoutChangingEconomy()
    {
        using var fixture = new ModFixture(isDiceAdministrator: true);
        var management = fixture.Mod.GetModManagementCommands().ToDictionary(command => command.Name);
        management["text"].Handler("set LeaderboardTitle CachedValue", new object());
        management["iou-text"].Handler("add CachedIouText", new object());
        management["user"].Handler("balance set 42 987", new object());

        fixture.Mod.GetCommandHandlers()["duel"]("reset text", new FakeMessage { UserId = 1 })
            .Should().Contain("已重新读取 default-texts.json");
        management["text"].Handler("get LeaderboardTitle", new object()).Should().NotBe("CachedValue");
        management["iou-text"].Handler("list", new object()).Should().NotContain("CachedIouText");
        fixture.Mod.GetCommandHandlers()["duel"]("me", new FakeMessage { UserId = 42 }).Should().Contain("余额：987厄里斯");
    }

    private static MeguminDuelMod CreateMod(string directory)
    {
        var mod = new MeguminDuelMod(new FakeContext());
        mod.SetPortableModContext(new PortableModContext(directory));
        mod.OnLoad();
        return mod;
    }

    private sealed class ModFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "megumin-duel-tests", Guid.NewGuid().ToString("N"));
        public MeguminDuelMod Mod { get; }
        public FakeContext Context { get; }
        public string DirectoryPath => _directory;
        public ModFixture(bool isDiceAdministrator = false)
        {
            Context = new FakeContext(isDiceAdministrator);
            var mod = new MeguminDuelMod(Context);
            mod.SetPortableModContext(new PortableModContext(_directory));
            mod.OnLoad();
            Mod = mod;
        }
        public void Dispose() { Mod.OnUnload(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }

    private sealed class FakeMessage { public long UserId { get; set; } }
    private sealed class FakeContext(bool isDiceAdministrator = false) : IModContext
    {
        private readonly Dictionary<long, double> _trust = [];
        public void SetTrust(long userId, double value) => _trust[userId] = value;
        public bool IsSimulationMode => true;
        public void SendGroupMessage(long groupId, string content) { }
        public void SendPrivateMessage(long userId, string content) { }
        public (long UserId, string Nickname) GetUserInfo(long userId) => (userId, $"昵称{userId}");
        public void Log(LogLevel level, string message) { }
        public INavigationPanelRegistry? GetNavigationPanelRegistry() => null;
        public void ExecuteCommand(long groupId, long userId, string command) { }
        public void RegisterCommandReplyListener(Action<long, long, string> listener) { }
        public int? GetUserAuthLevel(long userId) => 0;
        public bool IsBotEnabled(long groupId) => true;
        public bool IsDiceAdministrator(long userId) => isDiceAdministrator;
        public double GetUserTrust(long userId) => _trust.TryGetValue(userId, out var trust) ? trust : 1000;
        public double AdjustUserTrust(long userId, double delta)
        {
            var updated = GetUserTrust(userId) + delta;
            _trust[userId] = updated;
            return updated;
        }
    }
}
