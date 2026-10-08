using System.Text.Json.Serialization;

namespace MeguminDuel;

public enum DuelSide { Player, Megumin }
public enum DuelWinner { Tie, Player, Megumin }

public sealed class DuelDie
{
    public int Value { get; set; }
    public int Sequence { get; set; }
    public bool Removed { get; set; }
    public bool Critical { get; set; }
    public bool Fumble { get; set; }
}

public sealed class DuelSession
{
    public long UserId { get; set; }
    public List<int> PlayerDice { get; set; } = [];
    public List<int> MeguminDice { get; set; } = [];
    public int StartingAmount { get; set; }
    public int ParticipationAmount { get; set; }
    public int Calls { get; set; }
    public string MeguminItemId { get; set; } = "megumin_jar";
    public List<string> MeguminItemIds { get; set; } = [];
    public string? ExtraMeguminItemId { get; set; }
    public bool PlayerUsedItem { get; set; }
    public int PlayerItemUseCount { get; set; }
    public int PlayerExtraItemUses { get; set; }
    public List<PlayerItemUse> PlayerItems { get; set; } = [];
    // Kept to migrate unfinished sessions saved by older releases.
    public string? PlayerItemId { get; set; }
    public int? PlayerItemTarget { get; set; }
    public bool PlayerItemTargetOnMegumin { get; set; }
    public string? PendingPlayerItemId { get; set; }
    public bool PendingPlayerItemTargetOnMegumin { get; set; }
    public bool MeguminFirstDieRevealed { get; set; }
    public int PlayerFinalBonus { get; set; }
    public int MeguminFinalBonus { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PlayerItemUse
{
    public string ItemId { get; set; } = "";
    public int? Target { get; set; }
    public bool TargetOnMegumin { get; set; }
}

public sealed class ShopOffer
{
    public string ItemId { get; set; } = "";
    public int BasePrice { get; set; }
    public int Price { get; set; }
    public string EventLabel { get; set; } = "原价";
    public bool Purchased { get; set; }
}

public sealed class DailyShop
{
    public string Date { get; set; } = "";
    public List<ShopOffer> Offers { get; set; } = [];
    public List<int> PurchasedSlots { get; set; } = [];
}

public sealed class PlayerState
{
    public string Nickname { get; set; } = "";
    public int Balance { get; set; } = 500;
    public long DuelTotalCount { get; set; }
    public long DuelWins { get; set; }
    public long DuelLosses { get; set; }
    public long DuelTies { get; set; }
    public long DuelWonAmount { get; set; }
    public long DuelLostAmount { get; set; }
    public Dictionary<string, int> Inventory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DuelDate { get; set; }
    public int DuelCount { get; set; }
    public int? DuelD3Roll { get; set; }
    public string? DuelD3Date { get; set; }
    public int? DuelDailyLimit { get; set; }
    public double? DuelTrustAtLimit { get; set; }
    public double? DuelLogTerm { get; set; }
    public string? ExplosionDate { get; set; }
    public int ExplosionCount { get; set; }
    public DailyShop? Shop { get; set; }
    public string? ShopRefreshDate { get; set; }
    public int ShopRefreshCount { get; set; }
    public DuelSession? Session { get; set; }
    public List<MeguminIou> Ious { get; set; } = [];
}

public sealed class MeguminIou
{
    public string Id { get; set; } = "";
    public int Amount { get; set; }
    public string IssuedDate { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class ExplosionText
{
    public string Id { get; set; } = "";
    public int Start { get; set; }
    public int Duration { get; set; }
    public string Text { get; set; } = "";
    [JsonIgnore] public int End => Start + Duration;
}

public sealed class IouText
{
    public string Id { get; set; } = "";
    public int Start { get; set; }
    public int Duration { get; set; }
    public int Weight { get; set; } = 1;
    public string Text { get; set; } = "";
    [JsonIgnore] public long End => Duration == 0 ? long.MaxValue : (long)Start + Duration;
}

public sealed class DuelConfig
{
    public int InitialBalance { get; set; } = 500;
    public int InitialMeguminBalance { get; set; } = 500;
    public int BaseAmount { get; set; } = 200;
    public int CallDiceCap { get; set; } = 5;
    public int ExplosionDailyLimit { get; set; } = 1;
    public int MeguminDailyCashMin { get; set; } = 1000;
    public int MeguminDailyCashMax { get; set; } = 2000;
    public int ExplosionRewardMin { get; set; } = 100;
    public int ExplosionRewardMax { get; set; } = 200;
    public double PickupChance { get; set; } = 0.10;
    public double RWeight { get; set; } = 0.70;
    public double SrWeight { get; set; } = 0.20;
    public double SsrWeight { get; set; } = 0.10;
    public int RPriceMin { get; set; } = 50;
    public int RPriceMax { get; set; } = 200;
    public int SrPriceMin { get; set; } = 150;
    public int SrPriceMax { get; set; } = 400;
    public int SsrPriceMin { get; set; } = 300;
    public int SsrPriceMax { get; set; } = 800;
    public double SuperDiscountChance { get; set; } = 0.05;
    public double PriceEventChance { get; set; } = 0.20;
}

public sealed class DuelPersistentState
{
    public int TextSchemaVersion { get; set; }
    public DuelConfig Config { get; set; } = new();
    public int MeguminBalance { get; set; } = 500;
    public string? MeguminCashDate { get; set; }
    public int MeguminDailyCash { get; set; }
    public Dictionary<long, PlayerState> Players { get; set; } = [];
    public Dictionary<string, string> Texts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ExplosionText> ExplosionTexts { get; set; } = [];
    public List<IouText> IouTexts { get; set; } = [];
    public bool IouTextsInitialized { get; set; }
}

public sealed class PackagedDefaultConfiguration
{
    public Dictionary<string, string> Texts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ItemRarities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ExplosionText> ExplosionTexts { get; set; } = [];
    public List<IouText> IouTexts { get; set; } = [];
}

public sealed record ItemDefinition(string Id, string Name, string Rarity, int Priority, string Usage, string Description);

public static class DuelCatalog
{
    public static readonly IReadOnlyDictionary<string, ItemDefinition> Items = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        ["kneeling_mat"] = new("kneeling_mat", "土下座用的垫子", "R", 200, "use <编号>", "铺好垫子行礼，触发一段特殊演出；不改变骰子或分数。"),
        ["flower"] = new("flower", "花", "R", 10, "use <编号>（随后选择己方骰号）", "选择一颗己方骰。结算时有 30% 概率重新投掷它，未触发时只播放未能奏效的文本。"),
        ["unstable_die"] = new("unstable_die", "不稳定的骰子", "R", 30, "use <编号>（随后选择己方骰号）", "选择一颗己方骰，使其点数减少 10；修改后的点数会正常参与大成功、大失败和后续规则。"),
        ["white_panties"] = new("white_panties", "不知道哪里来的纯白胖次", "R", 30, "use <编号>", "结算时有 60% 概率让惠惠额外使用一件不同的作弊道具；否则玩家最终得分增加 50 分。"),
        ["vanishing_ink"] = new("vanishing_ink", "会消失的墨水", "SR", 20, "use <编号>（随后选择己方骰号）", "选择一颗己方骰，将其十位数字擦除、保留个位；修改后的点数会正常参与后续规则。"),
        ["talking_cabbage"] = new("talking_cabbage", "很会说话的卷心菜", "SR", 25, "use <编号>", "卷心菜会努力夸奖惠惠；有 20% 概率令玩家最终得分增加 50 分。"),
        ["game_console"] = new("game_console", "游戏机", "SSR", 200, "use <编号>", "立即为玩家额外投出一颗己方骰；该骰会和其他骰一样参与本局结算。"),
        ["axis_order_emblem"] = new("axis_order_emblem", "阿克西斯教圣徽", "SSR", 200, "use <编号>（随后选择对方骰号）", "选择对方场上一颗骰，重新投掷其十位数并保留个位数；新点数正常参与开牌结算。"),
        ["cracked_die"] = new("cracked_die", "开裂骰子", "SSR", 200, "use <编号>（随后选择己方骰号）", "选择一颗己方骰，将点数尽量均分为两颗骰，总点数保持不变；奇数时一颗多 1 点。"),
        ["loaded_die"] = new("loaded_die", "灌铅骰子", "SR", 20, "use <编号>（随后选择己方骰号）", "选择一颗己方骰并重投一次，保留原点数与新点数中的较高值。"),
        ["schrodinger_die"] = new("schrodinger_die", "薛定谔的骰子", "SR", 20, "use <编号>（随后选择己方骰号）", "选择一颗己方骰并重投一次，直接采用新点数。"),
        ["demon_heart"] = new("demon_heart", "恶魔之心", "SR", 20, "use <编号>", "使用后消耗本次道具使用机会，并使本局额外获得两次道具使用机会。"),
        ["divine_hood"] = new("divine_hood", "神圣兜帽", "SR", 20, "use <编号>", "本局若玩家落败，最终需支付的本局额度减少 80%；平局或获胜时不触发。"),
        ["peaceful_girl_figure"] = new("peaceful_girl_figure", "安乐少女手办", "SR", 20, "use <编号>", "开牌结算时令自己的最终分数增加 20 分。"),
        ["forked_path_eye"] = new("forked_path_eye", "歧路思义眼", "SR", 20, "use <编号>", "使用时立即揭露惠惠首骰的个位数，不必等待开牌。")
    };

    public static readonly string[] MeguminItems = ["megumin_jar", "explosion", "face_eraser", "vanir_mask"];
    public static readonly IReadOnlyDictionary<string, string> MeguminItemNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["megumin_jar"] = "惠惠的秘藏骰子罐",
        ["explosion"] = "Explosion",
        ["face_eraser"] = "能擦掉骰面的镜子",
        ["vanir_mask"] = "巴尼尔的假面"
    };
}
