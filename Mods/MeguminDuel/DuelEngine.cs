namespace MeguminDuel;

public sealed class DuelResolution
{
    public DuelWinner Winner { get; init; }
    public int PlayerScore { get; init; }
    public int MeguminScore { get; init; }
    public int? PlayerFourKind { get; init; }
    public int? MeguminFourKind { get; init; }
    public IReadOnlyList<int> PlayerRemaining { get; init; } = [];
    public IReadOnlyList<int> MeguminRemaining { get; init; } = [];
    public IReadOnlyList<int> PlayerFinalDice { get; init; } = [];
    public IReadOnlyList<int> MeguminFinalDice { get; init; } = [];
    public IReadOnlyList<string> Events { get; init; } = [];
}

public static class DuelEngine
{
    public static DuelResolution Resolve(
        IEnumerable<int> playerValues,
        IEnumerable<int> meguminValues,
        int playerBonus = 0,
        int meguminBonus = 0)
    {
        var events = new List<string>();
        var player = CreateDice(playerValues);
        var megumin = CreateDice(meguminValues);
        ClassifyInitial(player, "玩家", events);
        ClassifyInitial(megumin, "惠惠", events);
        ResolveTailMerges(player, megumin, events);

        var playerFour = FindFourKind(player);
        var meguminFour = FindFourKind(megumin);
        var playerScore = Score(player) + playerBonus;
        var meguminScore = Score(megumin) + meguminBonus;
        DuelWinner winner;
        if (playerFour.HasValue || meguminFour.HasValue)
        {
            if (!playerFour.HasValue) winner = DuelWinner.Megumin;
            else if (!meguminFour.HasValue) winner = DuelWinner.Player;
            else winner = playerFour.Value > meguminFour.Value ? DuelWinner.Player : DuelWinner.Megumin;
        }
        else if (playerScore > meguminScore) winner = DuelWinner.Player;
        else if (meguminScore > playerScore) winner = DuelWinner.Megumin;
        else winner = DuelWinner.Tie;

        return new DuelResolution
        {
            Winner = winner,
            PlayerScore = playerScore,
            MeguminScore = meguminScore,
            PlayerFourKind = playerFour,
            MeguminFourKind = meguminFour,
            PlayerRemaining = Active(player).Select(x => x.Value).ToArray(),
            MeguminRemaining = Active(megumin).Select(x => x.Value).ToArray(),
            PlayerFinalDice = DisplayDice(player),
            MeguminFinalDice = DisplayDice(megumin),
            Events = events
        };
    }

    private static List<DuelDie> CreateDice(IEnumerable<int> values) => values
        .Select((value, index) => new DuelDie { Value = NormalizeDieValue(value), Sequence = index })
        .ToList();

    internal static int NormalizeDieValue(int value) => value == 0 ? 100 : value;

    private static void ClassifyInitial(List<DuelDie> dice, string owner, List<string> events)
    {
        foreach (var die in dice.Where(x => x.Value <= 5))
        {
            die.Critical = true;
            events.Add($"{owner}的 [{die.Value}] 成为大成功（200分）");
        }
        foreach (var die in dice.OrderBy(x => x.Sequence))
        {
            if (die.Removed || die.Critical || die.Fumble || die.Value < 96) continue;
            ApplyFumble(dice, die, owner, events);
        }
    }

    private static void ApplyFumble(List<DuelDie> dice, DuelDie die, string owner, List<string> events)
    {
        die.Fumble = true;
        die.Removed = true;
        var next = dice.Where(x => !x.Removed && !x.Critical && !x.Fumble && x.Sequence > die.Sequence)
            .OrderBy(x => x.Sequence).FirstOrDefault();
        if (next is not null) next.Removed = true;
        events.Add(next is null
            ? $"{owner}的 [{die.Value}] 大失败，但后面已没有可销毁的骰子"
            : $"{owner}的 [{die.Value}] 大失败并销毁了后续骰 [{next.Value}]");
    }

    private static void ResolveTailMerges(List<DuelDie> player, List<DuelDie> megumin, List<string> events)
    {
        while (true)
        {
            var playerActive = Active(player).ToArray();
            var meguminActive = Active(megumin).ToArray();
            var field = playerActive.Select(die => (Side: DuelSide.Player, Die: die))
                .Concat(meguminActive.Select(die => (Side: DuelSide.Megumin, Die: die)))
                .OrderBy(entry => entry.Die.Value)
                .ThenBy(entry => entry.Side == DuelSide.Megumin ? 0 : 1)
                .ThenBy(entry => entry.Die.Sequence);

            (DuelSide Side, DuelDie Die, DuelDie Victim)? nextMerge = null;
            foreach (var entry in field)
            {
                // A die is only ever allowed to inspect and consume the opposing field.
                var opposingField = entry.Side == DuelSide.Player ? meguminActive : playerActive;
                var candidateVictim = opposingField.Where(die => Tail(die.Value) == Tail(entry.Die.Value))
                    .OrderByDescending(die => die.Value)
                    .ThenBy(die => die.Sequence)
                    .FirstOrDefault();
                if (candidateVictim is null) continue;
                nextMerge = (entry.Side, entry.Die, candidateVictim);
                break;
            }
            if (nextMerge is null) return;

            var eater = (Side: nextMerge.Value.Side, Die: nextMerge.Value.Die);
            var victim = nextMerge.Value.Victim;
            var oldEater = eater.Die.Value;
            var mergedValue = NormalizeDieValue(Mod100(eater.Die.Value + victim.Value));
            var owningField = eater.Side == DuelSide.Player ? player : megumin;
            var nextSequence = player.Concat(megumin).Select(die => die.Sequence).DefaultIfEmpty(-1).Max() + 1;
            eater.Die.Removed = true;
            victim.Removed = true;
            var mergedDie = new DuelDie { Value = mergedValue, Sequence = nextSequence };
            owningField.Add(mergedDie);
            events.Add($"{(eater.Side == DuelSide.Player ? "玩家" : "惠惠")}的 [{oldEater}] 吞掉 [{victim.Value}]，变为 [{mergedDie.Value}]");

            if (mergedDie.Value <= 5)
            {
                mergedDie.Critical = true;
                events.Add($"新骰 [{mergedDie.Value}] 立即成为大成功（200分）");
            }
            else if (mergedDie.Value >= 96)
            {
                ApplyFumble(owningField, mergedDie,
                    eater.Side == DuelSide.Player ? "玩家" : "惠惠", events);
            }
        }
    }

    private static IEnumerable<DuelDie> Active(IEnumerable<DuelDie> dice) =>
        dice.Where(x => !x.Removed && !x.Critical && !x.Fumble);

    private static int? FindFourKind(IEnumerable<DuelDie> dice) => Active(dice)
        .GroupBy(x => x.Value).Where(x => x.Count() >= 4).Select(x => (int?)x.Key)
        .OrderByDescending(x => x).FirstOrDefault();

    private static int Score(IEnumerable<DuelDie> dice)
    {
        var criticalScore = dice.Count(x => x.Critical && !x.Removed) * 200;
        var grouped = Active(dice).GroupBy(x => x.Value);
        var regularScore = grouped.Sum(group => group.Count() switch
        {
            2 => group.Sum(x => x.Value) * 2,
            3 => group.Sum(x => x.Value) * 4,
            _ => group.Sum(x => x.Value)
        });
        return criticalScore + regularScore;
    }

    private static IReadOnlyList<int> DisplayDice(IEnumerable<DuelDie> dice) => dice
        .Where(die => !die.Removed && !die.Fumble)
        .OrderBy(die => die.Sequence)
        .Select(die => die.Critical ? 200 : die.Value)
        .ToArray();

    private static int Tail(int value) => Math.Abs(value % 10);
    private static int Mod100(int value) => ((value % 100) + 100) % 100;
}
