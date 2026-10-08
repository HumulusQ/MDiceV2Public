using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MDiceV2.Interfaces.Mod;

namespace MeguminDuel;

public sealed class MeguminDuelMod : IModPlugin, ICommandProvider, IModManagementCommandProvider, IPortableModContextReceiver
{
    private const string Id = "com.mdicev2.megumin-duel";
    private readonly object _sync = new();
    private readonly IModContext _context;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private Dictionary<string, ItemDefinition> _items = DuelCatalog.Items.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    private string _dataDirectory = "";
    private string _statePath = "";
    private DuelPersistentState _state = new();
    private Dictionary<string, string> _defaultTexts = BuiltInDefaultTexts();
    private List<ExplosionText> _defaultExplosionTexts = BuiltInDefaultExplosionTexts();
    private List<IouText> _defaultIouTexts = BuiltInDefaultIouTexts();

    public MeguminDuelMod(IModContext context) => _context = context;

    public string ModId => Id;
    public string ModName => "惠惠骰子对决";
    public string Version => PortableBuildInfo.Version;
    public string Author => "MDiceV2 Team";
    public string Description => "每日骰子对决、爆裂、道具商店、经济、财富排行榜与 .iou 欠条收藏。";

    public void SetPortableModContext(PortableModContext context)
    {
        _dataDirectory = context.DataDirectory;
        _statePath = Path.Combine(_dataDirectory, "state.json");
    }

    public void OnLoad()
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_dataDirectory))
            {
                _dataDirectory = Path.Combine(AppContext.BaseDirectory, "mods", ".portable", "duel", "data");
                _statePath = Path.Combine(_dataDirectory, "state.json");
            }
            Directory.CreateDirectory(_dataDirectory);
            LoadPackagedDefaultsLocked();
            LoadLocked();
            MergeDefaultsLocked();
            NormalizeMeguminDailyLocked();
            SaveLocked();
        }
        _context.Log(LogLevel.Info, "惠惠骰子对决已加载。所有状态保存在便携 Mod 稳定数据目录中。");
    }

    public void OnEnable() => _context.Log(LogLevel.Info, "惠惠骰子对决已启用。");
    public void OnDisable() { lock (_sync) SaveLocked(); }
    public void OnUnload() { lock (_sync) SaveLocked(); }

    public Dictionary<string, Func<string, object, string?>> GetCommandHandlers() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["duel"] = (args, message) => HandleDuel(args, GetUserId(message)),
        ["explosion"] = (_, message) => HandleExplosion(GetUserId(message)),
        ["iou"] = (_, message) => GetIouCollection(GetUserId(message))
    };

    public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted) =>
        HandlePendingMessage(userId, content);

    public ModMessageResult? OnPrivateMessage(long userId, string content) => HandlePendingMessage(userId, content);

    private ModMessageResult? HandlePendingMessage(long userId, string content)
    {
        var selectingItemTarget = false;
        lock (_sync)
        {
            if (!TryGetPlayerLocked(userId, out var player) || player.Session is null) return null;
            selectingItemTarget = player.Session.PendingPlayerItemId is not null;
        }

        var text = Normalize(content);
        if (text.StartsWith('(') || text.StartsWith('（')) return null;
        string response;
        if (selectingItemTarget) response = HandleItemTargetSelection(text, userId);
        else if (text.Equals("call", StringComparison.OrdinalIgnoreCase)) response = HandleCall(userId);
        else if (IsCommand(text, "use", out var useArgs)) response = HandleUse(useArgs, userId);
        else if (IsFocusAction(text, "use", out useArgs)) response = HandleUse(useArgs, userId);
        else if (IsDuelOpenAction(text)) response = Settle(userId);
        else if (IsCommand(text, "duel", out var duelArgs)) response = HandleDuel(duelArgs, userId);
        else response = Text("DuelFocusReminder");
        return ModMessageResult.Intercept(response, modId: Id);
    }

    private string HandleDuel(string args, long userId)
    {
        var input = (args ?? "").Trim();
        if (input.Equals("rule", StringComparison.OrdinalIgnoreCase)) return Text("Rule");
        if (input.Equals("reset text", StringComparison.OrdinalIgnoreCase)) return ResetLocalTexts(userId);
        if (input.Equals("reset", StringComparison.OrdinalIgnoreCase)) return ResetDailyDuels(userId);
        if (input.Equals("get", StringComparison.OrdinalIgnoreCase)) return GrantDiceAdminAllowance(userId);
        if (input.Equals("me", StringComparison.OrdinalIgnoreCase)) return GetProfile(userId);
        if (input.Equals("list", StringComparison.OrdinalIgnoreCase)) return GetLeaderboard();
        if (input.Equals("blist", StringComparison.OrdinalIgnoreCase)) return GetDebtLeaderboard();
        if (input.Equals("shop", StringComparison.OrdinalIgnoreCase)) return ShowShop(userId);
        if (input.StartsWith("shop ", StringComparison.OrdinalIgnoreCase))
        {
            var shopArgs = input[5..].Trim();
            if (shopArgs.Equals("refresh", StringComparison.OrdinalIgnoreCase)) return RefreshShop(userId);
            if (shopArgs.StartsWith("buy ", StringComparison.OrdinalIgnoreCase) && int.TryParse(shopArgs[4..].Trim(), out var slot))
                return BuyShop(userId, slot);
            return "格式：.duel shop、.duel shop refresh 或 .duel shop buy <1|2|3>";
        }

        var useAllIn = input.Equals("allin", StringComparison.OrdinalIgnoreCase);
        int? requestedAmount = null;
        if (input.Length > 0 && !useAllIn)
        {
            if (!int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedAmount))
                return "格式：.duel [不少于100的额度|allin]；也可使用 .duel rule / me / list / blist / shop / get / reset。";
            if (parsedAmount < 100) return Text("DuelAmountTooLow");
            requestedAmount = parsedAmount;
        }

        lock (_sync)
        {
            var player = GetOrCreatePlayerLocked(userId);
            var duelAllowance = NormalizeDailyLocked(player, userId);
            NormalizeMeguminDailyLocked();
            if (player.Session is not null) return SessionStatus(player.Session, player);
            if (player.DuelCount >= duelAllowance.Limit)
                return BuildDuelDailyLimitReply(player, duelAllowance);

            var startingAmount = useAllIn ? player.Balance : requestedAmount ?? _state.Config.BaseAmount;
            if (startingAmount < 100) return Text(useAllIn ? "AllInAmountTooLow" : "DuelAmountTooLow");
            var meguminItemCount = startingAmount >= 1000 ? 3 : startingAmount >= 500 ? 2 : startingAmount >= 200 ? 1 : 0;
            var meguminItems = DuelCatalog.MeguminItems.OrderBy(_ => Random.Shared.Next()).Take(meguminItemCount).ToList();
            var session = new DuelSession
            {
                UserId = userId,
                StartingAmount = startingAmount,
                ParticipationAmount = startingAmount,
                PlayerDice = RollDice(3),
                MeguminDice = RollDice(3),
                MeguminItemId = meguminItems.FirstOrDefault() ?? "",
                MeguminItemIds = meguminItems
            };
            player.Session = session;
            SaveLocked();
            var reply = ApplySessionText("DuelStart", player,
                "操作：call来加骰 / use <序号>来使用道具 / 输入 duel 开牌；可用 .duel <额度> 或 .duel allin 开局",
                ("playerDice", NumberedDiceText(session.PlayerDice, maskFirstDie: false)), ("meguminDice", NumberedDiceText(session.MeguminDice, maskFirstDie: !session.MeguminFirstDieRevealed, maskSecondDie: HasVanirMask(session))),
                ("amount", session.ParticipationAmount.ToString()), ("meguminItem", MeguminItemsText(session, ShouldShowMeguminItemDescriptions(player, session))),
                ("playerBalance", player.Balance.ToString()), ("meguminBalance", MeguminBalanceDisplayLocked()),
                ("duelAllowance", FormatDuelAllowance(player, duelAllowance)));
            return AppendAvailableItems(AppendDuelAllowance(reply, "DuelStart", player, duelAllowance), player, session);
        }
    }

    private string GrantDiceAdminAllowance(long userId)
    {
        if (!_context.IsDiceAdministrator(userId)) return "权限不足：仅骰子管理员可以使用 .duel get。";
        lock (_sync)
        {
            var player = GetOrCreatePlayerLocked(userId);
            player.Balance += 500;
            SaveLocked();
            return $"你从惠惠手里抢来500厄里斯，当前余额：{player.Balance}厄里斯。";
        }
    }

    private string ResetDailyDuels(long userId)
    {
        if (!_context.IsDiceAdministrator(userId)) return "权限不足：仅骰子管理员可以重置当日 Duel 记录。";
        lock (_sync)
        {
            var today = Today();
            foreach (var (playerId, player) in _state.Players)
            {
                NormalizeDailyLocked(player, playerId);
                player.DuelDate = today;
                player.DuelCount = 0;
                player.Session = null;
            }
            SaveLocked();
            return "已清除所有用户的当日 Duel 次数和未完成对局；余额、库存、欠条、Explosion 与商店记录均未改变。";
        }
    }

    private string ResetLocalTexts(long userId)
    {
        if (!_context.IsDiceAdministrator(userId)) return "权限不足：仅骰子管理员可以重置 Duel 本地文本。";
        lock (_sync)
        {
            if (!LoadPackagedDefaultsLocked())
                return "重置失败：当前安装版本的 default-texts.json 缺失或格式无效，原有文本未改变。";
            _state.Texts = DefaultTexts();
            _state.ExplosionTexts = DefaultExplosionTexts();
            _state.IouTexts = DefaultIouTexts();
            _state.IouTextsInitialized = true;
            _state.TextSchemaVersion = 1;
            SaveLocked();
            return "已重新读取 default-texts.json，并重置普通文本、爆裂文本和欠条语句；余额及游戏进度未改变。";
        }
    }

    private string HandleCall(long userId)
    {
        lock (_sync)
        {
            var player = GetOrCreatePlayerLocked(userId);
            var session = player.Session;
            if (session is null) return Text("NoSession");
            if (session.PlayerDice.Count >= _state.Config.CallDiceCap || session.MeguminDice.Count >= _state.Config.CallDiceCap)
                return Text("CallCap");
            NormalizeSessionLocked(session);
            var callAmount = session.StartingAmount / 2 + session.StartingAmount % 2;
            if (session.ParticipationAmount > int.MaxValue - callAmount) return Text("CallAmountOverflow");
            session.PlayerDice.Add(Roll());
            session.MeguminDice.Add(Roll());
            session.Calls++;
            session.ParticipationAmount += callAmount;
            SaveLocked();
            var reply = ApplySessionText("CallSuccess", player,
                "操作：duel 开牌 / use <编号> 使用道具 / call 继续追加",
                ("amount", session.ParticipationAmount.ToString()),
                ("playerDice", NumberedDiceText(session.PlayerDice, maskFirstDie: false)), ("meguminDice", NumberedDiceText(session.MeguminDice, maskFirstDie: !session.MeguminFirstDieRevealed, maskSecondDie: HasVanirMask(session))));
            return AppendAvailableItems(reply, player, session);
        }
    }

    private string HandleUse(string args, long userId)
    {
        lock (_sync)
        {
            var player = GetOrCreatePlayerLocked(userId);
            var session = player.Session;
            if (session is null) return Text("NoSession");
            NormalizeSessionLocked(session);
            var inventory = InventoryRows(player).ToArray();
            if (string.IsNullOrWhiteSpace(args)) return BuildUseMenu(inventory, session);
            if (!CanUsePlayerItem(session))
                return session.PlayerExtraItemUses > 0
                    ? Apply(Text("ItemUsesExhausted"), ("used", session.PlayerItemUseCount.ToString()),
                        ("limit", (1 + session.PlayerExtraItemUses).ToString()))
                    : Text("ItemAlreadyUsed");

            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var itemId = ResolveInventoryItem(parts[0], inventory);
            if (itemId is null || !player.Inventory.TryGetValue(itemId, out var count) || count <= 0)
                return Text("ItemNotOwned");
            var definition = _items[itemId];
            if (itemId is "flower" or "unstable_die" or "vanishing_ink" or "axis_order_emblem" or
                "cracked_die" or "loaded_die" or "schrodinger_die")
            {
                session.PendingPlayerItemId = itemId;
                session.PendingPlayerItemTargetOnMegumin = itemId == "axis_order_emblem";
                SaveLocked();
                return BuildItemTargetPrompt(session, definition.Name);
            }
            if (itemId == "forked_path_eye")
            {
                if (session.MeguminDice.Count == 0) return Text("ItemTargetGone");
                ConsumeInventoryItemLocked(player, itemId);
                RecordPlayerItemUse(session);
                session.MeguminFirstDieRevealed = true;
                SaveLocked();
                var digit = Math.Abs(session.MeguminDice[0] % 10).ToString(CultureInfo.InvariantCulture);
                return Apply(Text("ForkedWayEyeApplied"), ("digit", digit));
            }
            if (itemId == "demon_heart")
            {
                ConsumeInventoryItemLocked(player, itemId);
                RecordPlayerItemUse(session);
                session.PlayerExtraItemUses += 2;
                SaveLocked();
                var remaining = (1 + session.PlayerExtraItemUses - session.PlayerItemUseCount)
                    .ToString(CultureInfo.InvariantCulture);
                var reply = Apply(Text("DemonHeartApplied"), ("remaining", remaining));
                var availableItems = BuildAvailableItemRows(InventoryRows(player).ToArray(), session, includeDescriptions: session.Calls == 0);
                return reply + "\n" + availableItems;
            }
            ConsumeAndPrepareItemLocked(player, session, itemId, null);
            SaveLocked();
            return BuildItemPreparedReply(itemId, definition.Name, session);
        }
    }

    private string HandleItemTargetSelection(string input, long userId)
    {
        lock (_sync)
        {
            var player = GetOrCreatePlayerLocked(userId);
            var session = player.Session;
            if (session?.PendingPlayerItemId is not { } itemId) return Text("NoSession");
            NormalizeSessionLocked(session);
            var definition = _items[itemId];
            var targetOnMegumin = session.PendingPlayerItemTargetOnMegumin;
            var targetDice = targetOnMegumin ? session.MeguminDice : session.PlayerDice;
            if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase) || input.Equals("取消", StringComparison.OrdinalIgnoreCase))
            {
                session.PendingPlayerItemId = null;
                session.PendingPlayerItemTargetOnMegumin = false;
                SaveLocked();
                return Text("ItemTargetCancelled");
            }
            if (!int.TryParse(input, out var number) || number < 1 || number > targetDice.Count)
                return Apply(Text("ItemTargetInvalid"), ("range", targetDice.Count.ToString())) + "\n" + BuildItemTargetPrompt(session, definition.Name);
            if (!player.Inventory.TryGetValue(itemId, out var count) || count <= 0)
            {
                session.PendingPlayerItemId = null;
                session.PendingPlayerItemTargetOnMegumin = false;
                SaveLocked();
                return Text("ItemNotOwned");
            }
            ConsumeAndPrepareItemLocked(player, session, itemId, number - 1, targetOnMegumin);
            SaveLocked();
            return BuildItemPreparedReply(itemId, definition.Name, session);
        }
    }

    private static void ConsumeInventoryItemLocked(PlayerState player, string itemId)
    {
        var count = player.Inventory[itemId];
        player.Inventory[itemId] = count - 1;
        if (player.Inventory[itemId] <= 0) player.Inventory.Remove(itemId);
    }

    private static void ConsumeAndPrepareItemLocked(PlayerState player, DuelSession session, string itemId, int? target, bool targetOnMegumin = false)
    {
        ConsumeInventoryItemLocked(player, itemId);
        RecordPlayerItemUse(session);
        session.PlayerItems.Add(new PlayerItemUse { ItemId = itemId, Target = target, TargetOnMegumin = targetOnMegumin });
        session.PendingPlayerItemId = null;
        session.PendingPlayerItemTargetOnMegumin = false;
    }

    private static bool CanUsePlayerItem(DuelSession session) =>
        session.PlayerItemUseCount < 1 + Math.Max(0, session.PlayerExtraItemUses);

    private static void RecordPlayerItemUse(DuelSession session)
    {
        session.PlayerItemUseCount++;
        session.PlayerUsedItem = true;
    }

    private string BuildItemPreparedReply(string itemId, string itemName, DuelSession session)
    {
        var descriptionVisible = session.Calls == 0;
        var template = Text("ItemPrepared");
        if (!descriptionVisible) template = WithoutItemDescription(template);
        var hasDescriptionPlaceholder = template.Contains("{description}", StringComparison.Ordinal);
        var reply = Apply(template, ("item", itemName), ("description", descriptionVisible ? ItemDescription(itemId) : ""));
        return hasDescriptionPlaceholder || !descriptionVisible ? reply : reply + "\n介绍：" + ItemDescription(itemId);
    }

    private string BuildItemTargetPrompt(DuelSession session, string itemName)
    {
        var itemId = session.PendingPlayerItemId ?? "";
        var targetOnMegumin = session.PendingPlayerItemTargetOnMegumin;
        var targetDice = targetOnMegumin ? session.MeguminDice : session.PlayerDice;
        var descriptionVisible = session.Calls == 0;
        var template = Text("ItemTargetPrompt");
        if (!descriptionVisible) template = WithoutItemDescription(template);
        var hasDescriptionPlaceholder = template.Contains("{description}", StringComparison.Ordinal);
        var prompt = Apply(template,
            ("item", itemName), ("range", targetDice.Count.ToString()), ("side", targetOnMegumin ? "对方" : "己方"),
            ("description", descriptionVisible ? ItemDescription(itemId) : ""),
            ("playerDice", NumberedDiceText(session.PlayerDice, maskFirstDie: false)), ("meguminDice", NumberedDiceText(session.MeguminDice, maskFirstDie: !session.MeguminFirstDieRevealed, maskSecondDie: HasVanirMask(session))));
        return hasDescriptionPlaceholder || !descriptionVisible ? prompt : prompt + "\n介绍：" + ItemDescription(itemId);
    }

    private static string WithoutItemDescription(string template) =>
        string.Join("\n", template.Split('\n').Where(line => !line.TrimStart().StartsWith("介绍：", StringComparison.Ordinal) &&
                                                               !line.TrimStart().StartsWith("介绍:", StringComparison.Ordinal)));

    private bool TextHasPlaceholder(string key, string placeholder) =>
        _state.Texts.TryGetValue(key, out var template) && template.Contains("{" + placeholder + "}", StringComparison.Ordinal);

    private string Settle(long userId)
    {
        lock (_sync)
        {
            var player = GetOrCreatePlayerLocked(userId);
            var session = player.Session;
            if (session is null) return Text("NoSession");
            NormalizeMeguminDailyLocked();
            var itemEvents = ApplyItemsLocked(session);
            NormalizeSessionLocked(session);
            var resolution = DuelEngine.Resolve(session.PlayerDice, session.MeguminDice, session.PlayerFinalBonus, session.MeguminFinalBonus);
            var playerDebt = false;
            MeguminIou? iou = null;
            var paidByMegumin = 0;
            var playerLossAmount = 0;
            if (resolution.Winner == DuelWinner.Player)
            {
                paidByMegumin = Math.Min(session.ParticipationAmount, Math.Max(0, _state.MeguminDailyCash));
                _state.MeguminDailyCash -= paidByMegumin;
                _state.MeguminBalance -= paidByMegumin;
                player.Balance += paidByMegumin;
                var unpaid = session.ParticipationAmount - paidByMegumin;
                if (unpaid > 0)
                {
                    var id = $"LOU-{DateTime.Now:yyyyMMdd}-{Guid.NewGuid():N}"[..21];
                    var flavor = SelectIouTextLocked(unpaid);
                    iou = new MeguminIou
                    {
                        Id = id,
                        Amount = unpaid,
                        IssuedDate = Today(),
                        Text = Apply(flavor, ("id", id), ("amount", unpaid.ToString()), ("paid", paidByMegumin.ToString()), ("qq", userId.ToString()))
                    };
                    player.Ious.Add(iou);
                }
            }
            else if (resolution.Winner == DuelWinner.Megumin)
            {
                var lossAmount = session.ParticipationAmount;
                if (session.PlayerItems.Any(item => item.ItemId.Equals("divine_hood", StringComparison.OrdinalIgnoreCase)))
                {
                    lossAmount = ReduceHoodedLoss(session.ParticipationAmount);
                    itemEvents.Add(Apply(Text("DivineHoodApplied"),
                        ("original", session.ParticipationAmount.ToString(CultureInfo.InvariantCulture)),
                        ("amount", lossAmount.ToString(CultureInfo.InvariantCulture)),
                        ("saved", (session.ParticipationAmount - lossAmount).ToString(CultureInfo.InvariantCulture))));
                }
                playerLossAmount = lossAmount;
                playerDebt = player.Balance < lossAmount;
                player.Balance -= lossAmount;
                _state.MeguminBalance = (int)Math.Min(int.MaxValue, (long)_state.MeguminBalance + lossAmount);
                _state.MeguminDailyCash = (int)Math.Min(MeguminDailyCashCapLocked(), (long)_state.MeguminDailyCash + lossAmount);
            }
            NormalizeDailyLocked(player, userId);
            player.DuelCount++;
            RecordDuelStatistics(player, resolution.Winner, session.ParticipationAmount, playerLossAmount);
            player.Session = null;
            SaveLocked();

            var resultKey = resolution.Winner switch
            {
                DuelWinner.Player => "PlayerWin",
                DuelWinner.Megumin => "MeguminWin",
                _ => "Tie"
            };
            var builder = new StringBuilder();
            if (itemEvents.Count > 0) builder.AppendLine(string.Join("\n", itemEvents));
            if (resolution.Events.Count > 0) builder.AppendLine(string.Join("\n", resolution.Events));
            builder.AppendLine(Apply(Text("Settlement"),
                ("playerDice", NumberedFinalDiceText(resolution.PlayerFinalDice)), ("meguminDice", NumberedFinalDiceText(resolution.MeguminFinalDice)),
                ("playerScore", FormatScore(resolution.PlayerScore, session.PlayerFinalBonus)),
                ("meguminScore", FormatScore(resolution.MeguminScore, session.MeguminFinalBonus)),
                ("amount", session.ParticipationAmount.ToString())));
            builder.Append(Apply(Text(resultKey), ("balance", player.Balance.ToString()), ("meguminBalance", MeguminBalanceDisplayLocked())));
            builder.Append("\n").Append(Text(resultKey + "Taunt"));
            if (iou is not null)
                builder.Append("\n").Append(Apply(Text("IouIssued"), ("id", iou.Id), ("amount", iou.Amount.ToString()), ("paid", paidByMegumin.ToString()), ("text", iou.Text)));
            if (playerDebt) builder.Append("\n").Append(Text("PlayerDebtMock"));
            return builder.ToString();
        }
    }

    private List<string> ApplyItemsLocked(DuelSession session)
    {
        NormalizeSessionLocked(session);
        var events = new List<string>();
        var actions = new List<(int Priority, int Order, Action Action)>();
        var order = 0;
        foreach (var playerItem in session.PlayerItems)
        {
            if (playerItem.ItemId.Equals("white_panties", StringComparison.OrdinalIgnoreCase))
            {
                if (Random.Shared.NextDouble() < .60)
                {
                    session.ExtraMeguminItemId = DuelCatalog.MeguminItems
                        .Where(x => !x.Equals("vanir_mask", StringComparison.OrdinalIgnoreCase) && !x.Equals(session.MeguminItemId, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(_ => Random.Shared.Next()).First();
                    events.Add(Text("PantiesMegumin"));
                }
                else
                {
                    session.PlayerFinalBonus += 50;
                    events.Add(Text("PantiesPlayer"));
                }
            }
            if (!_items.TryGetValue(playerItem.ItemId, out var definition)) continue;
            actions.Add((definition.Priority, order++, () => ApplyPlayerItem(session, playerItem, events)));
        }
        foreach (var itemId in CurrentMeguminItems(session).OrderBy(itemId => itemId.Equals("megumin_jar", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            actions.Add((100, order++, () => ApplyMeguminItem(session, itemId, events)));
        foreach (var action in actions.OrderByDescending(x => x.Priority).ThenBy(x => x.Order)) action.Action();
        return events;
    }

    private void ApplyPlayerItem(DuelSession session, PlayerItemUse item, List<string> events)
    {
        var itemId = item.ItemId;
        var target = item.Target ?? 0;
        var targetsPlayerDie = itemId is "flower" or "unstable_die" or "vanishing_ink" or
            "cracked_die" or "loaded_die" or "schrodinger_die";
        var targetsMeguminDie = item.TargetOnMegumin && itemId == "axis_order_emblem";
        if ((targetsPlayerDie && (target < 0 || target >= session.PlayerDice.Count)) ||
            (targetsMeguminDie && (target < 0 || target >= session.MeguminDice.Count)))
        {
            events.Add(Text("ItemTargetGone"));
            return;
        }
        switch (itemId)
        {
            case "kneeling_mat": events.Add(Text("KneelingMat")); break;
            case "divine_hood": events.Add(Text("DivineHoodEquipped")); break;
            case "flower":
                if (Random.Shared.NextDouble() < .30)
                {
                    var old = session.PlayerDice[target]; session.PlayerDice[target] = Roll();
                    events.Add(Apply(Text("FlowerSuccess"), ("target", (target + 1).ToString()),
                        ("old", old.ToString()), ("value", session.PlayerDice[target].ToString())));
                }
                else events.Add(Text("FlowerMiss"));
                break;
            case "unstable_die":
                session.PlayerDice[target] = DuelEngine.NormalizeDieValue(session.PlayerDice[target] - 10);
                events.Add(Apply(Text("UnstableDieApplied"), ("target", (target + 1).ToString()),
                    ("value", session.PlayerDice[target].ToString())));
                break;
            case "vanishing_ink":
                var value = session.PlayerDice[target];
                session.PlayerDice[target] = DuelEngine.NormalizeDieValue(value - Math.Abs(value / 10 % 10) * 10);
                events.Add(Apply(Text("VanishingInkApplied"), ("target", (target + 1).ToString()),
                    ("old", value.ToString()), ("value", session.PlayerDice[target].ToString())));
                break;
            case "cracked_die":
                var crackedValue = session.PlayerDice[target];
                var firstHalf = Math.Max(1, (crackedValue + 1) / 2);
                var secondHalf = Math.Max(1, crackedValue - firstHalf);
                session.PlayerDice[target] = firstHalf;
                session.PlayerDice.Insert(target + 1, secondHalf);
                foreach (var queuedItem in session.PlayerItems)
                {
                    if (ReferenceEquals(queuedItem, item) || queuedItem.TargetOnMegumin || queuedItem.Target is not { } queuedTarget)
                        continue;
                    if (queuedTarget > target) queuedItem.Target++;
                }
                events.Add(Apply(Text("CrackedDieApplied"), ("target", (target + 1).ToString()),
                    ("old", crackedValue.ToString()), ("first", firstHalf.ToString()), ("second", secondHalf.ToString())));
                break;
            case "loaded_die":
                var loadedOld = session.PlayerDice[target];
                var loadedRoll = Roll();
                session.PlayerDice[target] = Math.Max(loadedOld, loadedRoll);
                events.Add(Apply(Text("LoadedDieApplied"), ("target", (target + 1).ToString()),
                    ("old", loadedOld.ToString()), ("roll", loadedRoll.ToString()), ("value", session.PlayerDice[target].ToString())));
                break;
            case "schrodinger_die":
                var schrodingerOld = session.PlayerDice[target];
                var schrodingerRoll = Roll();
                session.PlayerDice[target] = schrodingerRoll;
                events.Add(Apply(Text("SchrodingerDieApplied"), ("target", (target + 1).ToString()),
                    ("old", schrodingerOld.ToString()), ("value", schrodingerRoll.ToString())));
                break;
            case "axis_order_emblem":
                var oldMeguminDie = session.MeguminDice[target];
                var onesDigit = Math.Abs(oldMeguminDie % 10);
                var rerolledMeguminDie = DuelEngine.NormalizeDieValue(Random.Shared.Next(0, 10) * 10 + onesDigit);
                session.MeguminDice[target] = rerolledMeguminDie;
                events.Add(Apply(Text("AxisOrderEmblemApplied"), ("target", (target + 1).ToString()),
                    ("old", oldMeguminDie.ToString()), ("value", rerolledMeguminDie.ToString())));
                break;
            case "talking_cabbage":
                if (Random.Shared.NextDouble() < .20) { session.PlayerFinalBonus += 50; events.Add(Text("CabbageSuccess")); }
                else events.Add(Text("CabbageMiss"));
                break;
            case "peaceful_girl_figure":
                session.PlayerFinalBonus += 20;
                events.Add(Text("PeacefulGirlFigureApplied"));
                break;
            case "game_console":
                session.PlayerDice.Add(Roll());
                events.Add(Apply(Text("GameConsoleApplied"), ("target", session.PlayerDice.Count.ToString()),
                    ("value", session.PlayerDice[^1].ToString())));
                break;
        }
    }

    private void ApplyMeguminItem(DuelSession session, string itemId, List<string> events)
    {
        switch (itemId)
        {
            case "megumin_jar":
                ApplyMeguminJar(session, events);
                break;
            case "explosion":
                var explosionBonus = Random.Shared.Next(1, 51);
                session.MeguminFinalBonus += explosionBonus;
                events.Add(Apply(Text("MeguminExplosionBonus"), ("value", explosionBonus.ToString())));
                break;
            case "face_eraser":
                if (session.PlayerDice.Count == 0) break;
                var highestDie = session.PlayerDice.Max();
                var tiedHighestDice = session.PlayerDice.Select((value, index) => (Value: value, Index: index))
                    .Where(entry => entry.Value == highestDie).ToArray();
                var remove = tiedHighestDice[Random.Shared.Next(tiedHighestDice.Length)];
                var reducedDie = Math.Max(1, remove.Value - 20);
                session.PlayerDice[remove.Index] = reducedDie;
                events.Add(Apply(Text("MeguminMirrorApplied"), ("target", (remove.Index + 1).ToString()),
                    ("old", remove.Value.ToString()), ("value", reducedDie.ToString())));
                break;
        }
    }

    private void ApplyMeguminJar(DuelSession session, List<string> events)
    {
        var newDie = Roll();
        events.Add(Apply(Text("MeguminJarApplied"), ("value", newDie.ToString(CultureInfo.InvariantCulture))));
        if (session.MeguminDice.Count == 0)
        {
            session.MeguminDice.Add(newDie);
            events.Add(Text("MeguminJarKept"));
            return;
        }

        var keepDice = session.MeguminDice.Append(newDie).ToList();
        var replaceDice = session.MeguminDice.ToList();
        var replaceIndex = replaceDice.IndexOf(replaceDice.Min());
        var replacedDie = replaceDice[replaceIndex];
        replaceDice[replaceIndex] = newDie;

        var keepOutcome = DuelEngine.Resolve(session.PlayerDice, keepDice, session.PlayerFinalBonus, session.MeguminFinalBonus);
        var replaceOutcome = DuelEngine.Resolve(session.PlayerDice, replaceDice, session.PlayerFinalBonus, session.MeguminFinalBonus);
        if (IsMeguminOutcomeBetter(replaceOutcome, replaceDice.Count, keepOutcome, keepDice.Count))
        {
            session.MeguminDice[replaceIndex] = newDie;
            events.Add(Apply(Text("MeguminJarReplaced"),
                ("target", (replaceIndex + 1).ToString(CultureInfo.InvariantCulture)),
                ("old", replacedDie.ToString(CultureInfo.InvariantCulture)),
                ("value", newDie.ToString(CultureInfo.InvariantCulture))));
            return;
        }

        session.MeguminDice.Add(newDie);
        events.Add(Text("MeguminJarKept"));
    }

    private static bool IsMeguminOutcomeBetter(
        DuelResolution candidate,
        int candidateDiceCount,
        DuelResolution current,
        int currentDiceCount)
    {
        static int WinnerRank(DuelWinner winner) => winner switch
        {
            DuelWinner.Megumin => 2,
            DuelWinner.Tie => 1,
            _ => 0
        };

        var comparison = WinnerRank(candidate.Winner).CompareTo(WinnerRank(current.Winner));
        if (comparison != 0) return comparison > 0;

        comparison = candidate.MeguminFourKind.HasValue.CompareTo(current.MeguminFourKind.HasValue);
        if (comparison != 0) return comparison > 0;
        if (candidate.MeguminFourKind.HasValue && current.MeguminFourKind.HasValue)
        {
            comparison = candidate.MeguminFourKind.Value.CompareTo(current.MeguminFourKind.Value);
            if (comparison != 0) return comparison > 0;
        }

        var candidateMargin = (long)candidate.MeguminScore - candidate.PlayerScore;
        var currentMargin = (long)current.MeguminScore - current.PlayerScore;
        comparison = candidateMargin.CompareTo(currentMargin);
        if (comparison != 0) return comparison > 0;

        // If both simulations are equally favorable, prefer the option with more dice.
        return candidateDiceCount > currentDiceCount;
    }

    private string HandleExplosion(long userId)
    {
        lock (_sync)
        {
            var player = GetOrCreatePlayerLocked(userId);
            NormalizeDailyLocked(player, userId);
            if (player.ExplosionCount >= _state.Config.ExplosionDailyLimit) return Text("ExplosionDailyLimit");
            var score = Random.Shared.Next(0, 101);
            var trustDelta = (score - 20) / 100d;
            var reward = Random.Shared.Next(_state.Config.ExplosionRewardMin, _state.Config.ExplosionRewardMax + 1);
            var matches = _state.ExplosionTexts.Where(x => score >= x.Start && score <= x.End).ToArray();
            var narrative = matches.Length == 0 ? Text("ExplosionFallback") : matches[Random.Shared.Next(matches.Length)].Text;
            player.Balance += reward;
            player.ExplosionCount++;
            string pickup = "";
            if (Random.Shared.NextDouble() < _state.Config.PickupChance)
            {
                var rItems = _items.Values.Where(item => item.Rarity == "R").ToArray();
                if (rItems.Length > 0)
                {
                    var item = rItems[Random.Shared.Next(rItems.Length)];
                    AddInventory(player, item.Id, 1);
                    pickup = "\n" + Apply(Text("Pickup"), ("item", item.Name));
                }
            }
            var trust = _context.AdjustUserTrust(userId, trustDelta);
            SaveLocked();
            var reply = Apply(Text("ExplosionResult"), ("score", score.ToString()), ("text", narrative),
                ("reward", reward.ToString()), ("balance", player.Balance.ToString()),
                ("trustDelta", FormatSigned(trustDelta)), ("trust", FormatTrust(trust)));
            if (!TextHasPlaceholder("ExplosionResult", "trustDelta"))
                reply += $"\n信任度变化：({score}-20)/100={FormatSigned(trustDelta)}，当前信任度：{FormatTrust(trust)}。";
            return reply + pickup;
        }
    }

    private string ShowShop(long userId)
    {
        lock (_sync)
        {
            var player = GetOrCreatePlayerLocked(userId);
            var shop = EnsureShopLocked(player);
            return FormatShopLocked(player, shop);
        }
    }

    private string RefreshShop(long userId)
    {
        lock (_sync)
        {
            var player = GetOrCreatePlayerLocked(userId);
            var currentShop = EnsureShopLocked(player);
            NormalizeShopPurchasesLocked(currentShop);
            var cost = GetNextShopRefreshCost(player);
            if (cost > int.MaxValue) return "今日刷新次数过多，暂时无法继续刷新商店。";
            var nextBalance = (long)player.Balance - cost;
            if (nextBalance < int.MinValue) return "余额已达到数值下限，无法支付刷新费用。";

            player.Balance = (int)nextBalance;
            player.ShopRefreshCount++;
            player.Shop = CreateDailyShopLocked(Today(), currentShop.PurchasedSlots);
            SaveLocked();
            var result = Apply(Text("ShopRefreshed"),
                ("price", cost.ToString(CultureInfo.InvariantCulture)),
                ("count", player.ShopRefreshCount.ToString(CultureInfo.InvariantCulture)),
                ("balance", player.Balance.ToString(CultureInfo.InvariantCulture)));
            return result + "\n" + FormatShopLocked(player, player.Shop);
        }
    }

    private string FormatShopLocked(PlayerState player, DailyShop shop)
    {
        var nextRefreshCost = GetNextShopRefreshCost(player);
        var refreshCostText = nextRefreshCost > int.MaxValue ? "今日不可再刷新" : $"下次刷新{nextRefreshCost}厄里斯";
        var lines = new List<string>
        {
            $"{Text("ShopTitle")}（购买：.duel shop buy <1|2|3>；刷新：.duel shop refresh，{refreshCostText}）"
        };
        for (var i = 0; i < shop.Offers.Count; i++)
        {
            if (i > 0) lines.Add("");
            var offer = shop.Offers[i]; var item = _items[offer.ItemId];
            lines.Add($"#{i + 1}. ({item.Rarity}){item.Name}");
            lines.Add($"-{offer.Price} 厄里斯 [{offer.EventLabel}] {(offer.Purchased ? "已售出" : "可购买")}");
            lines.Add($"   介绍：{ItemDescription(item.Id)}");
        }
        return string.Join("\n", lines);
    }

    private string BuyShop(long userId, int slot)
    {
        lock (_sync)
        {
            var player = GetOrCreatePlayerLocked(userId);
            var shop = EnsureShopLocked(player);
            NormalizeShopPurchasesLocked(shop);
            if (slot < 1 || slot > shop.Offers.Count) return "商品编号必须是 1、2 或 3。";
            var offer = shop.Offers[slot - 1];
            if (offer.Purchased) return Text("ShopSold");
            offer.Purchased = true;
            if (!shop.PurchasedSlots.Contains(slot)) shop.PurchasedSlots.Add(slot);
            player.Balance -= offer.Price;
            AddInventory(player, offer.ItemId, 1);
            SaveLocked();
            return Apply(Text("ShopBought"), ("item", _items[offer.ItemId].Name),
                ("price", offer.Price.ToString()), ("balance", player.Balance.ToString()));
        }
    }

    private DailyShop EnsureShopLocked(PlayerState player)
    {
        var today = Today();
        var refreshDateChanged = NormalizeShopRefreshLocked(player, today);
        if (player.Shop?.Date == today)
        {
            NormalizeShopPurchasesLocked(player.Shop);
            if (refreshDateChanged) SaveLocked();
            return player.Shop;
        }
        player.Shop = CreateDailyShopLocked(today, []);
        SaveLocked();
        return player.Shop;
    }

    private DailyShop CreateDailyShopLocked(string today, IEnumerable<int> purchasedSlots)
    {
        var purchased = purchasedSlots.Where(slot => slot is >= 1 and <= 3).Distinct().ToHashSet();
        var available = _items.Values.ToList();
        var offers = new List<ShopOffer>();
        while (offers.Count < 3)
        {
            var rarity = RollRarity(available);
            var pool = available.Where(x => x.Rarity == rarity).ToArray();
            if (pool.Length == 0) continue;
            var item = pool[Random.Shared.Next(pool.Length)];
            available.Remove(item);
            var (min, max) = item.Rarity switch
            {
                "SR" => (_state.Config.SrPriceMin, _state.Config.SrPriceMax),
                "SSR" => (_state.Config.SsrPriceMin, _state.Config.SsrPriceMax),
                _ => (_state.Config.RPriceMin, _state.Config.RPriceMax)
            };
            var basePrice = Random.Shared.Next(min, max + 1);
            var multiplier = 1d; var label = "原价";
            if (Random.Shared.NextDouble() < _state.Config.SuperDiscountChance) { multiplier = .01; label = "99% off"; }
            else if (Random.Shared.NextDouble() < _state.Config.PriceEventChance)
            {
                (multiplier, label) = Random.Shared.Next(3) switch { 0 => (.5, "五折"), 1 => (.7, "七折"), _ => (2, "翻倍") };
            }
            var slot = offers.Count + 1;
            offers.Add(new ShopOffer
            {
                ItemId = item.Id,
                BasePrice = basePrice,
                Price = Math.Max(1, (int)Math.Ceiling(basePrice * multiplier)),
                EventLabel = label,
                Purchased = purchased.Contains(slot)
            });
        }
        return new DailyShop { Date = today, Offers = offers, PurchasedSlots = purchased.Order().ToList() };
    }

    private static bool NormalizeShopRefreshLocked(PlayerState player, string today)
    {
        if (player.ShopRefreshDate == today)
        {
            player.ShopRefreshCount = Math.Max(0, player.ShopRefreshCount);
            return false;
        }
        player.ShopRefreshDate = today;
        player.ShopRefreshCount = 0;
        return true;
    }

    private static long GetNextShopRefreshCost(PlayerState player) =>
        ((long)Math.Max(0, player.ShopRefreshCount) + 1) * 50;

    private static void NormalizeShopPurchasesLocked(DailyShop shop)
    {
        shop.PurchasedSlots ??= [];
        for (var index = 0; index < shop.Offers.Count; index++)
            if (shop.Offers[index].Purchased && !shop.PurchasedSlots.Contains(index + 1))
                shop.PurchasedSlots.Add(index + 1);
        shop.PurchasedSlots = shop.PurchasedSlots.Where(slot => slot >= 1 && slot <= shop.Offers.Count)
            .Distinct().Order().ToList();
        for (var index = 0; index < shop.Offers.Count; index++)
            shop.Offers[index].Purchased = shop.PurchasedSlots.Contains(index + 1);
    }

    private string RollRarity(List<ItemDefinition> available)
    {
        var roll = Random.Shared.NextDouble();
        var desired = roll < _state.Config.RWeight ? "R" : roll < _state.Config.RWeight + _state.Config.SrWeight ? "SR" : "SSR";
        if (available.Any(x => x.Rarity == desired)) return desired;
        return available.GroupBy(x => x.Rarity).OrderBy(_ => Random.Shared.Next()).First().Key;
    }

    private string GetProfile(long userId)
    {
        lock (_sync)
        {
            var player = GetOrCreatePlayerLocked(userId); var duelAllowance = NormalizeDailyLocked(player, userId);
            var inventory = InventoryRows(player).Select((x, i) => $"{i + 1}. ({x.Definition.Rarity}){x.Definition.Name} ×{x.Count}").ToArray();
            NormalizeMeguminDailyLocked();
            var duelStatistics = Apply(Text("DuelStatistics"),
                ("total", player.DuelTotalCount.ToString(CultureInfo.InvariantCulture)),
                ("wins", player.DuelWins.ToString(CultureInfo.InvariantCulture)),
                ("losses", player.DuelLosses.ToString(CultureInfo.InvariantCulture)),
                ("ties", player.DuelTies.ToString(CultureInfo.InvariantCulture)),
                ("won", player.DuelWonAmount.ToString(CultureInfo.InvariantCulture)),
                ("lost", player.DuelLostAmount.ToString(CultureInfo.InvariantCulture)));
            return $"QQ {userId}\n余额：{player.Balance}厄里斯\n{FormatDuelAllowance(player, duelAllowance)}\n{duelStatistics}\n今日爆裂：{player.ExplosionCount}/{_state.Config.ExplosionDailyLimit}\n惠惠余额：{MeguminBalanceDisplayLocked()}厄里斯（总资产{_state.MeguminBalance}厄里斯）\n收藏欠条：{player.Ious.Count}张\n进行中：{(player.Session is null ? "无" : "有")}\n库存：\n{(inventory.Length == 0 ? "（空）" : string.Join("\n", inventory))}";
        }
    }

    private string GetIouCollection(long userId)
    {
        lock (_sync)
        {
            var player = GetOrCreatePlayerLocked(userId);
            if (player.Ious.Count == 0) return Text("IouCollectionEmpty");
            var lines = new List<string> { Apply(Text("IouCollectionTitle"), ("count", player.Ious.Count.ToString())) };
            lines.AddRange(player.Ious.Select((iou, index) => Apply(Text("IouCollectionRow"),
                ("index", (index + 1).ToString()), ("id", iou.Id), ("amount", iou.Amount.ToString()),
                ("date", iou.IssuedDate), ("text", iou.Text))));
            return string.Join("\n", lines);
        }
    }

    private string GetLeaderboard() => GetBalanceLeaderboard(debtOnly: false);

    private string GetDebtLeaderboard() => GetBalanceLeaderboard(debtOnly: true);

    private string GetBalanceLeaderboard(bool debtOnly)
    {
        (long UserId, int Balance, string Nickname, bool IsMegumin)[] rows;
        lock (_sync)
        {
            var candidates = _state.Players
                .Select(x => (UserId: x.Key, Balance: x.Value.Balance, Nickname: x.Value.Nickname ?? "", IsMegumin: false))
                .Append((UserId: long.MinValue, Balance: _state.MeguminBalance, Nickname: "惠惠", IsMegumin: true))
                .Where(row => !debtOnly || row.Balance < 0);
            var ordered = debtOnly
                ? candidates.OrderBy(row => row.Balance).ThenBy(row => row.UserId)
                : candidates.OrderByDescending(row => row.Balance).ThenBy(row => row.UserId);
            rows = ordered.Take(10).ToArray();
        }

        // User-info lookup may involve OneBot I/O, so never perform it while holding _sync.
        var resolvedNicknames = new Dictionary<long, string>();
        foreach (var row in rows.Where(row => !row.IsMegumin))
        {
            var nickname = NormalizeLeaderboardNickname(row.Nickname);
            if (nickname is null)
            {
                try { nickname = NormalizeLeaderboardNickname(_context.GetUserInfo(row.UserId).Nickname); }
                catch (Exception ex) { _context.Log(LogLevel.Warn, $"无法获取排行榜用户 {row.UserId} 的昵称：{ex.Message}"); }
            }
            resolvedNicknames[row.UserId] = nickname ?? "QQ用户";
        }

        lock (_sync)
        {
            var stateChanged = false;
            foreach (var (userId, nickname) in resolvedNicknames)
            {
                if (nickname == "QQ用户" || !_state.Players.TryGetValue(userId, out var player)) continue;
                if (NormalizeLeaderboardNickname(player.Nickname) is not null) continue;
                player.Nickname = nickname;
                stateChanged = true;
            }
            if (stateChanged) SaveLocked();

            var lines = new List<string> { Text(debtOnly ? "DebtLeaderboardTitle" : "LeaderboardTitle") };
            if (rows.Length == 0)
                return Text("DebtLeaderboardEmpty");
            lines.AddRange(rows.Select((row, index) => row.IsMegumin
                ? Apply(Text(debtOnly ? "DebtLeaderboardMeguminRow" : "LeaderboardMeguminRow"),
                    ("rank", (index + 1).ToString()),
                    (debtOnly ? "debt" : "balance", debtOnly ? (-(long)row.Balance).ToString() : row.Balance.ToString()))
                : Apply(Text(debtOnly ? "DebtLeaderboardRow" : "LeaderboardRow"),
                    ("rank", (index + 1).ToString()),
                    ("qq", row.UserId.ToString()),
                    (debtOnly ? "debt" : "balance", debtOnly ? (-(long)row.Balance).ToString() : row.Balance.ToString()),
                    ("nickname", resolvedNicknames[row.UserId]))));
            return string.Join("\n", lines);
        }
    }

    private static string? NormalizeLeaderboardNickname(string? nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname)) return null;
        var normalized = new string(nickname.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        if (normalized.Length == 0 || normalized.Equals("未知用户", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("User_", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("用户_", StringComparison.OrdinalIgnoreCase)) return null;
        return normalized;
    }

    public IReadOnlyCollection<ModManagementCommand> GetModManagementCommands() =>
    [
        new("config", "config list|get|set|reset", HandleConfigCommand),
        new("text", "text list|get|set|reset", HandleTextCommand),
        new("explosion-text", "爆裂区间文本 list|add|set|remove|reset", HandleExplosionTextCommand),
        new("iou-text", "list|add/set <ID> <起始面值> <持续面值> <文本>|weight <ID> <正整数>|remove <ID>|reset（持续面值0表示从起始面值起无限适用）", HandleIouTextCommand),
        new("user", "用户余额、库存、每日状态、商店及会话管理", HandleUserCommand),
        new("megumin", "惠惠全局余额 get|set|add", HandleMeguminCommand),
        new("item", "道具目录 list|show", HandleItemCommand)
    ];

    private string? HandleConfigCommand(string args, object _) { lock (_sync) return ConfigCommandLocked(args); }
    private string? HandleTextCommand(string args, object _) { lock (_sync) return TextCommandLocked(args); }
    private string? HandleExplosionTextCommand(string args, object _) { lock (_sync) return ExplosionTextCommandLocked(args); }
    private string? HandleIouTextCommand(string args, object _) { lock (_sync) return IouTextCommandLocked(args); }
    private string? HandleUserCommand(string args, object _) { lock (_sync) return UserCommandLocked(args); }
    private string? HandleMeguminCommand(string args, object _) { lock (_sync) return MeguminCommandLocked(args); }
    private string? HandleItemCommand(string args, object _) { lock (_sync) return ItemCommandLocked(args); }

    private string ConfigCommandLocked(string args)
    {
        var (action, rest) = SplitFirst(args);
        var properties = typeof(DuelConfig).GetProperties(BindingFlags.Instance | BindingFlags.Public).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        if (action.Equals("list", StringComparison.OrdinalIgnoreCase)) return string.Join("\n", properties.Values.OrderBy(x => x.Name).Select(x => $"{x.Name}={x.GetValue(_state.Config)}"));
        var (key, value) = SplitFirst(rest);
        if (action.Equals("reset", StringComparison.OrdinalIgnoreCase) && key.Equals("all", StringComparison.OrdinalIgnoreCase)) { _state.Config = new(); SaveLocked(); return "已重置全部配置。"; }
        if (!properties.TryGetValue(key, out var property)) return "未知配置键。使用 config list 查看。";
        if (action.Equals("get", StringComparison.OrdinalIgnoreCase)) return $"{property.Name}={property.GetValue(_state.Config)}";
        if (action.Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            var oldValue = property.GetValue(_state.Config);
            try { property.SetValue(_state.Config, Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture)); ValidateConfig(_state.Config); SaveLocked(); return $"已设置 {property.Name}={property.GetValue(_state.Config)}"; }
            catch (Exception ex) { property.SetValue(_state.Config, oldValue); return $"配置值无效：{ex.Message}"; }
        }
        if (action.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            var defaults = new DuelConfig(); property.SetValue(_state.Config, property.GetValue(defaults)); SaveLocked(); return $"已重置 {property.Name}";
        }
        return "格式：config list|get <key>|set <key> <value>|reset <key|all>";
    }

    private string TextCommandLocked(string args)
    {
        var (action, rest) = SplitFirst(args); var (key, value) = SplitFirst(rest);
        if (action.Equals("list", StringComparison.OrdinalIgnoreCase)) return string.Join("\n", _state.Texts.Keys.Where(x => rest.Length == 0 || x.StartsWith(rest, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x));
        if (action.Equals("get", StringComparison.OrdinalIgnoreCase)) return _state.Texts.TryGetValue(key, out var text) ? text : "未找到文本键。";
        if (action.Equals("set", StringComparison.OrdinalIgnoreCase) && key.Length > 0) { _state.Texts[key] = Decode(value); SaveLocked(); return $"已保存文本 {key}。"; }
        if (action.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            var defaults = DefaultTexts();
            if (key.Equals("all", StringComparison.OrdinalIgnoreCase)) _state.Texts = defaults;
            else if (defaults.TryGetValue(key, out var defaultText)) _state.Texts[key] = defaultText;
            else return "默认文本中不存在该键。";
            SaveLocked(); return "文本已重置。";
        }
        return "格式：text list [前缀]|get <key>|set <key> <模板>|reset <key|all>";
    }

    private string ExplosionTextCommandLocked(string args)
    {
        var (action, rest) = SplitFirst(args);
        if (action.Equals("list", StringComparison.OrdinalIgnoreCase)) return string.Join("\n", _state.ExplosionTexts.Select(x => $"{x.Id}: {x.Start}-{x.End} {x.Text}"));
        if (action.Equals("reset", StringComparison.OrdinalIgnoreCase)) { _state.ExplosionTexts = DefaultExplosionTexts(); SaveLocked(); return "爆裂文本已重置。"; }
        if (action.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            var removed = _state.ExplosionTexts.RemoveAll(x => x.Id.Equals(rest.Trim(), StringComparison.OrdinalIgnoreCase)); SaveLocked(); return removed > 0 ? "已删除。" : "未找到该ID。";
        }
        var tokens = rest.Split(' ', action.Equals("set", StringComparison.OrdinalIgnoreCase) ? 4 : 3, StringSplitOptions.RemoveEmptyEntries);
        var offset = action.Equals("set", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (tokens.Length < 3 + offset || !int.TryParse(tokens[offset], out var start) || !int.TryParse(tokens[offset + 1], out var duration) || start < 0 || duration < 0 || start + duration > 100)
            return "格式：add <开始0-100> <持续> <文本>；set <ID> <开始> <持续> <文本>。";
        var entry = action.Equals("set", StringComparison.OrdinalIgnoreCase) ? _state.ExplosionTexts.FirstOrDefault(x => x.Id.Equals(tokens[0], StringComparison.OrdinalIgnoreCase)) : null;
        if (action.Equals("set", StringComparison.OrdinalIgnoreCase) && entry is null) return "未找到该ID。";
        if (!action.Equals("add", StringComparison.OrdinalIgnoreCase) && !action.Equals("set", StringComparison.OrdinalIgnoreCase)) return "未知操作。";
        entry ??= new ExplosionText { Id = Guid.NewGuid().ToString("N")[..8] };
        entry.Start = start; entry.Duration = duration; entry.Text = Decode(tokens[offset + 2]);
        if (!_state.ExplosionTexts.Contains(entry)) _state.ExplosionTexts.Add(entry);
        SaveLocked(); return $"已保存 {entry.Id}。";
    }

    private string IouTextCommandLocked(string args)
    {
        var (action, rest) = SplitFirst(args);
        if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
            return _state.IouTexts.Count == 0 ? "欠条文本列表为空。" : string.Join("\n", _state.IouTexts.Select(text =>
                $"{text.Id}: {FormatIouTextRange(text)} weight={text.Weight} {text.Text}"));
        if (action.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            _state.IouTexts = DefaultIouTexts();
            _state.IouTextsInitialized = true;
            SaveLocked();
            return "欠条文本及面值范围已按 default-texts.json 重置。";
        }
        if (action.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            var removed = _state.IouTexts.FirstOrDefault(entry => entry.Id.Equals(rest.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed is null) return "未找到该ID。";
            if (removed.Start == 0 && removed.Duration == 0 && _state.IouTexts.Count(entry => entry.Start == 0 && entry.Duration == 0) == 1)
                return "不能删除最后一个 0,0 无限通用条目。";
            _state.IouTexts.Remove(removed);
            SaveLocked();
            return "已删除欠条文本。";
        }
        if (action.Equals("weight", StringComparison.OrdinalIgnoreCase))
        {
            var (id, weightText) = SplitFirst(rest);
            var entry = _state.IouTexts.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return "未找到该ID。";
            if (!int.TryParse(weightText, NumberStyles.None, CultureInfo.InvariantCulture, out var weight) || weight <= 0)
                return "权重必须是大于 0 的整数。";
            entry.Weight = weight;
            SaveLocked();
            return $"已设置欠条文本 {entry.Id} 的权重为 {weight}。";
        }
        if (!action.Equals("add", StringComparison.OrdinalIgnoreCase) && !action.Equals("set", StringComparison.OrdinalIgnoreCase))
            return "格式：iou-text list|add <ID> <开始面值> <持续面值> <文本>|set <ID> <开始面值> <持续面值> <文本>|weight <ID> <正整数>|remove <ID>|reset；持续面值设为0表示从开始面值起无限适用。";

        var tokens = rest.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4 || !int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out var duration) ||
            start < 0 || duration < 0 || (duration > 0 && (long)start + duration > int.MaxValue) || string.IsNullOrWhiteSpace(tokens[3]))
            return "格式：iou-text add <ID> <开始面值> <持续面值> <文本>；持续面值设为0表示从开始面值起无限适用。set 使用相同参数。";

        var existing = _state.IouTexts.FirstOrDefault(entry => entry.Id.Equals(tokens[0], StringComparison.OrdinalIgnoreCase));
        if (action.Equals("add", StringComparison.OrdinalIgnoreCase) && existing is not null) return "该ID已存在。";
        if (action.Equals("set", StringComparison.OrdinalIgnoreCase) && existing is null) return "未找到该ID。";
        var updated = existing ?? new IouText { Id = tokens[0] };
        var removesFallback = existing is { Start: 0, Duration: 0 } && (start != 0 || duration != 0) &&
            _state.IouTexts.Count(entry => entry.Start == 0 && entry.Duration == 0) == 1;
        if (removesFallback) return "必须至少保留一个 start=0、duration=0 的无限通用条目。";
        updated.Start = start;
        updated.Duration = duration;
        updated.Text = Decode(tokens[3]);
        if (existing is null) _state.IouTexts.Add(updated);
        SaveLocked();
        return $"已保存欠条文本 {updated.Id}（{FormatIouTextRange(updated)}）。";
    }

    private static string FormatIouTextRange(IouText entry) =>
        entry.Duration == 0 ? $"{entry.Start}+（无限）" : $"{entry.Start}-{entry.End}";

    private string UserCommandLocked(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        const string help = "格式：user show <QQ>|balance set|add <QQ> <值>|item give|take <QQ> <ID> [数量]|daily reset <QQ> <范围>|shop reset <QQ>|session cancel <QQ>";
        if (parts.Length >= 2 && parts[0].Equals("show", StringComparison.OrdinalIgnoreCase) && long.TryParse(parts[1], out var shownUserId)) return GetProfile(shownUserId);
        if (parts.Length >= 4 && parts[0].Equals("balance", StringComparison.OrdinalIgnoreCase) && long.TryParse(parts[2], out var balanceUserId) && int.TryParse(parts[3], out var amount))
        {
            var player = GetOrCreatePlayerLocked(balanceUserId);
            if (parts[1].Equals("set", StringComparison.OrdinalIgnoreCase)) player.Balance = amount;
            else if (parts[1].Equals("add", StringComparison.OrdinalIgnoreCase)) player.Balance += amount;
            else return "balance 仅支持 set/add。";
            SaveLocked(); return $"QQ {balanceUserId} 余额={player.Balance}";
        }
        if (parts.Length >= 4 && parts[0].Equals("item", StringComparison.OrdinalIgnoreCase) && long.TryParse(parts[2], out var itemUserId) && _items.ContainsKey(parts[3]))
        {
            var player = GetOrCreatePlayerLocked(itemUserId);
            var count = parts.Length >= 5 && int.TryParse(parts[4], out var parsed) ? Math.Max(1, parsed) : 1;
            if (!parts[1].Equals("give", StringComparison.OrdinalIgnoreCase) && !parts[1].Equals("take", StringComparison.OrdinalIgnoreCase)) return help;
            AddInventory(player, parts[3], parts[1].Equals("take", StringComparison.OrdinalIgnoreCase) ? -count : count); SaveLocked(); return "库存已更新。";
        }
        if (parts.Length >= 3 && parts[0].Equals("daily", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("reset", StringComparison.OrdinalIgnoreCase) && long.TryParse(parts[2], out var dailyUserId))
        {
            var player = GetOrCreatePlayerLocked(dailyUserId); var scope = parts.Length >= 4 ? parts[3].ToLowerInvariant() : "all";
            if (scope is "duel" or "all") { NormalizeDailyLocked(player, dailyUserId); player.DuelDate = Today(); player.DuelCount = 0; }
            if (scope is "explosion" or "all") { player.ExplosionDate = Today(); player.ExplosionCount = 0; }
            SaveLocked(); return "每日状态已重置。";
        }
        if (parts.Length >= 3 && parts[0].Equals("shop", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("reset", StringComparison.OrdinalIgnoreCase) && long.TryParse(parts[2], out var shopUserId))
        {
            var shopPlayer = GetOrCreatePlayerLocked(shopUserId);
            shopPlayer.Shop = null;
            shopPlayer.ShopRefreshDate = null;
            shopPlayer.ShopRefreshCount = 0;
            SaveLocked();
            return "商店已重置。";
        }
        if (parts.Length >= 3 && parts[0].Equals("session", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("cancel", StringComparison.OrdinalIgnoreCase) && long.TryParse(parts[2], out var sessionUserId)) { GetOrCreatePlayerLocked(sessionUserId).Session = null; SaveLocked(); return "未完成对局已取消。"; }
        return help;
    }

    private string MeguminCommandLocked(string args)
    {
        var (action, rest) = SplitFirst(args);
        if (action.Equals("balance", StringComparison.OrdinalIgnoreCase)) (action, rest) = SplitFirst(rest);
        NormalizeMeguminDailyLocked();
        if (action.Equals("get", StringComparison.OrdinalIgnoreCase)) return MeguminBalanceManagementSummaryLocked();
        if ((action.Equals("set", StringComparison.OrdinalIgnoreCase) || action.Equals("add", StringComparison.OrdinalIgnoreCase)) && int.TryParse(rest, out var value))
        { _state.MeguminBalance = action.Equals("set", StringComparison.OrdinalIgnoreCase) ? value : (int)Math.Clamp((long)_state.MeguminBalance + value, int.MinValue, int.MaxValue); NormalizeMeguminDailyLocked(); SaveLocked(); return MeguminBalanceManagementSummaryLocked(); }
        return "格式：megumin balance get|set <值>|add <值>";
    }

    private string ItemCommandLocked(string args)
    {
        var (action, rest) = SplitFirst(args);
        if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
            return string.Join("\n", _items.Values.Select(x => $"{x.Id}: ({x.Rarity}){x.Name} P{x.Priority}")
                .Concat(DuelCatalog.MeguminItemNames.Select(x => $"{x.Key}: (惠惠专属) {x.Value}")));
        if (action.Equals("show", StringComparison.OrdinalIgnoreCase) && _items.TryGetValue(rest, out var item)) return $"{item.Id}\n({item.Rarity}){item.Name} P{item.Priority}\n{ItemDescription(item.Id)}";
        if (action.Equals("show", StringComparison.OrdinalIgnoreCase) && DuelCatalog.MeguminItemNames.TryGetValue(rest, out var meguminItemName))
            return $"{rest}\n惠惠专属：{meguminItemName}\n{ItemDescription(rest)}";
        return "格式：item list|show <道具ID>";
    }

    private PlayerState GetOrCreatePlayerLocked(long userId)
    {
        if (!_state.Players.TryGetValue(userId, out var player))
        {
            player = new PlayerState { Balance = _state.Config.InitialBalance };
            _state.Players[userId] = player;
        }
        return player;
    }

    private bool TryGetPlayerLocked(long userId, out PlayerState player) => _state.Players.TryGetValue(userId, out player!);
    private (double Trust, int D3, double LogTerm, int RawLimit, int Limit) NormalizeDailyLocked(PlayerState player, long userId)
    {
        var today = Today();
        var shouldPersist = false;
        if (player.DuelDate != today) { player.DuelDate = today; player.DuelCount = 0; shouldPersist = true; }
        if (player.ExplosionDate != today) { player.ExplosionDate = today; player.ExplosionCount = 0; }
        if (player.DuelD3Date != today || player.DuelD3Roll is < 1 or > 3)
        {
            player.DuelD3Date = today;
            player.DuelD3Roll = Random.Shared.Next(1, 4);
            shouldPersist = true;
        }

        var trust = _context.GetUserTrust(userId);
        if (!double.IsFinite(trust)) trust = 0;
        var logTerm = trust <= 0 ? -1 : Math.Max(-1, Math.Log10(Math.Max(0.1, trust)));
        var rawLimit = (int)Math.Floor(logTerm + player.DuelD3Roll!.Value - 1);
        var limit = Math.Max(1, rawLimit);
        player.DuelDailyLimit = limit;
        player.DuelTrustAtLimit = trust;
        player.DuelLogTerm = logTerm;
        if (shouldPersist) SaveLocked();
        return (trust, player.DuelD3Roll.Value, logTerm, rawLimit, limit);
    }

    private string FormatDuelAllowance(PlayerState player, (double Trust, int D3, double LogTerm, int RawLimit, int Limit) allowance) =>
        Apply(Text("DuelAllowance"),
            ("trustForLog", FormatTrust(Math.Max(0.1, allowance.Trust))),
            ("d3", allowance.D3.ToString(CultureInfo.InvariantCulture)),
            ("used", player.DuelCount.ToString(CultureInfo.InvariantCulture)),
            ("rawLimit", allowance.RawLimit.ToString(CultureInfo.InvariantCulture)),
            ("limit", allowance.Limit.ToString(CultureInfo.InvariantCulture)),
            ("fallback", allowance.RawLimit < 1 ? "(保底)" : ""));

    private static string FormatTrust(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string FormatSigned(double value) => value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);

    private string AppendDuelAllowance(string reply, string textKey, PlayerState player, (double Trust, int D3, double LogTerm, int RawLimit, int Limit) allowance)
    {
        var formattedAllowance = FormatDuelAllowance(player, allowance);
        if (textKey.Equals("DuelStart", StringComparison.OrdinalIgnoreCase))
        {
            var content = RemoveMultilineBlock(reply, formattedAllowance);
            var firstLineBreak = content.IndexOf('\n');
            if (firstLineBreak < 0) return content.TrimEnd() + "\n" + formattedAllowance;

            var opening = content[..firstLineBreak].TrimEnd('\r');
            var remainder = content[(firstLineBreak + 1)..].TrimStart('\r', '\n');
            return string.IsNullOrEmpty(remainder)
                ? opening + "\n" + formattedAllowance
                : opening + "\n" + formattedAllowance + "\n" + remainder;
        }

        return TextHasPlaceholder(textKey, "duelAllowance") ? reply : reply + "\n" + formattedAllowance;
    }

    private static string RemoveMultilineBlock(string content, string block)
    {
        var lines = content.Split('\n').ToList();
        var blockLines = block.Split('\n');
        for (var i = 0; i <= lines.Count - blockLines.Length;)
        {
            var matches = true;
            for (var j = 0; j < blockLines.Length; j++)
            {
                if (!lines[i + j].TrimEnd('\r').Equals(blockLines[j], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches) lines.RemoveRange(i, blockLines.Length);
            else i++;
        }

        return string.Join("\n", lines).Replace(block, "", StringComparison.Ordinal);
    }

    private string BuildDuelDailyLimitReply(PlayerState player, (double Trust, int D3, double LogTerm, int RawLimit, int Limit) allowance)
    {
        var message = Apply(Text("DuelDailyLimit"), ("duelAllowance", "")).TrimEnd();
        return message + "\n" + FormatDuelAllowance(player, allowance);
    }

    private void NormalizeMeguminDailyLocked()
    {
        var today = Today();
        var cashCap = MeguminDailyCashCapLocked();
        if (_state.MeguminCashDate == today)
        {
            _state.MeguminDailyCash = Math.Clamp(_state.MeguminDailyCash, 0, Math.Min(cashCap, Math.Max(0, _state.MeguminBalance)));
            return;
        }
        _state.MeguminCashDate = today;
        var minimum = Math.Min(cashCap, Math.Max(0, _state.Config.MeguminDailyCashMin));
        var dailyAllowance = (int)Random.Shared.NextInt64(minimum, (long)cashCap + 1);
        _state.MeguminDailyCash = _state.MeguminBalance <= 0 ? 0 : Math.Min(_state.MeguminBalance, dailyAllowance);
    }

    private int MeguminDailyCashCapLocked() => Math.Clamp(_state.Config.MeguminDailyCashMax, 0, 2000);

    private string MeguminBalanceDisplayLocked()
    {
        var carried = Math.Max(0, _state.MeguminDailyCash);
        var savings = Math.Max(0L, (long)_state.MeguminBalance - carried);
        return $"{carried.ToString(CultureInfo.InvariantCulture)}({savings.ToString(CultureInfo.InvariantCulture)})";
    }

    private string MeguminBalanceManagementSummaryLocked()
    {
        var carried = Math.Max(0, _state.MeguminDailyCash);
        var savings = Math.Max(0L, (long)_state.MeguminBalance - carried);
        return $"惠惠随身={carried}；存款={savings}；总资产={_state.MeguminBalance}";
    }

    private IEnumerable<(string Id, ItemDefinition Definition, int Count)> InventoryRows(PlayerState player) => player.Inventory
        .Where(x => x.Value > 0 && _items.ContainsKey(x.Key)).OrderBy(x => _items[x.Key].Rarity).ThenBy(x => _items[x.Key].Name)
        .Select(x => (x.Key, _items[x.Key], x.Value));

    private static string? ResolveInventoryItem(string token, (string Id, ItemDefinition Definition, int Count)[] rows)
    {
        if (int.TryParse(token, out var slot) && slot >= 1 && slot <= rows.Length) return rows[slot - 1].Id;
        return rows.FirstOrDefault(x => x.Id.Equals(token, StringComparison.OrdinalIgnoreCase)).Id;
    }

    private static void AddInventory(PlayerState player, string itemId, int amount)
    {
        player.Inventory.TryGetValue(itemId, out var current); var next = Math.Max(0, current + amount);
        if (next == 0) player.Inventory.Remove(itemId); else player.Inventory[itemId] = next;
    }

    private string BuildUseMenu((string Id, ItemDefinition Definition, int Count)[] inventory, DuelSession session)
    {
        if (!CanUsePlayerItem(session)) return session.PlayerExtraItemUses > 0
            ? Apply(Text("ItemUsesExhausted"), ("used", session.PlayerItemUseCount.ToString()),
                ("limit", (1 + session.PlayerExtraItemUses).ToString()))
            : "本局已经使用过道具。";
        if (inventory.Length == 0) return "当前没有可用道具。";
        var remaining = 1 + session.PlayerExtraItemUses - session.PlayerItemUseCount;
        var heading = session.PlayerExtraItemUses > 0
            ? $"本局还可使用{remaining}件道具："
            : "本局可使用一件道具：";
        return heading + "\n" + BuildAvailableItemRows(inventory, session, includeDescriptions: session.Calls == 0);
    }

    private string AppendAvailableItems(string reply, PlayerState player, DuelSession session)
    {
        var rows = InventoryRows(player).ToArray();
        var availableItems = BuildAvailableItemRows(rows, session, includeDescriptions: session.Calls == 0);
        return reply.Contains("{availableItems}", StringComparison.Ordinal)
            ? reply.Replace("{availableItems}", availableItems, StringComparison.Ordinal)
            : reply.TrimEnd() + "\n" + availableItems;
    }

    private string BuildAvailableItemRows((string Id, ItemDefinition Definition, int Count)[] inventory, DuelSession session, bool includeDescriptions)
    {
        if (!CanUsePlayerItem(session)) return session.PlayerExtraItemUses > 0
            ? Apply(Text("ItemUsesExhausted"), ("used", session.PlayerItemUseCount.ToString()),
                ("limit", (1 + session.PlayerExtraItemUses).ToString()))
            : "（本局已经使用过道具）";
        if (inventory.Length == 0) return "（无可用道具）";
        var rows = string.Join("\n", inventory.SelectMany((item, index) => includeDescriptions
            ? new[] { $"{index + 1}. ({item.Definition.Rarity}){item.Definition.Name} ×{item.Count}", $"   介绍：{ItemDescription(item.Id)}" }
            : new[] { $"{index + 1}. ({item.Definition.Rarity}){item.Definition.Name} ×{item.Count}" }));
        return session.PlayerExtraItemUses > 0
            ? $"（本局还可使用{1 + session.PlayerExtraItemUses - session.PlayerItemUseCount}件道具）\n{rows}"
            : rows;
    }

    private string SessionStatus(DuelSession session, PlayerState player)
    {
        if (session.PendingPlayerItemId is { } pendingItem && _items.TryGetValue(pendingItem, out var definition))
            return BuildItemTargetPrompt(session, definition.Name);
        NormalizeSessionLocked(session);
        return AppendAvailableItems(ApplySessionText("DuelStatus", player,
            "操作：call / use <编号> / 输入 duel 开牌",
            ("playerDice", NumberedDiceText(session.PlayerDice, maskFirstDie: false)), ("meguminDice", NumberedDiceText(session.MeguminDice, maskFirstDie: !session.MeguminFirstDieRevealed, maskSecondDie: HasVanirMask(session))),
            ("amount", session.ParticipationAmount.ToString()), ("meguminItem", MeguminItemsText(session, ShouldShowMeguminItemDescriptions(player, session))),
            ("playerBalance", player.Balance.ToString()), ("meguminBalance", MeguminBalanceDisplayLocked())), player, session);
    }
    private static string NumberedFinalDiceText(IEnumerable<int> dice) => string.Join(" ", dice.Select((value, index) => $"[{index + 1}:{value}]"));
    private static string FormatScore(int totalScore, int bonus)
    {
        if (bonus == 0) return totalScore.ToString(CultureInfo.InvariantCulture);
        var bonusText = bonus > 0 ? $"+{bonus.ToString(CultureInfo.InvariantCulture)}" : bonus.ToString(CultureInfo.InvariantCulture);
        return $"{(totalScore - bonus).ToString(CultureInfo.InvariantCulture)}({bonusText})";
    }

    private static int ReduceHoodedLoss(int participationAmount) =>
        (int)Math.Max(1L, ((long)Math.Max(0, participationAmount) * 20 + 99) / 100);

    private static string NumberedDiceText(IEnumerable<int> dice, bool maskFirstDie, bool maskSecondDie = false) => string.Join(" ", dice.Select((value, index) =>
    {
        var face = (index == 0 && maskFirstDie) || (index == 1 && maskSecondDie)
            ? MaskLastDigit(value)
            : value.ToString(CultureInfo.InvariantCulture);
        return $"[{index + 1}:{face}]";
    }));
    private static string MaskLastDigit(int value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.Length == 1 ? "*" : text[..^1] + "*";
    }
    private static List<int> RollDice(int count) => Enumerable.Range(0, count).Select(_ => Roll()).ToList();
    private static int Roll() => Random.Shared.Next(1, 101);
    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Normalize(string text) { var value = (text ?? "").Trim(); return value.StartsWith('。') ? "." + value[1..] : value; }
    private static bool IsCommand(string text, string command, out string args)
    {
        var prefix = "." + command;
        if (text.Equals(prefix, StringComparison.OrdinalIgnoreCase)) { args = ""; return true; }
        if (text.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)) { args = text[(prefix.Length + 1)..]; return true; }
        args = ""; return false;
    }
    private static bool IsFocusAction(string text, string action, out string args)
    {
        if (text.Equals(action, StringComparison.OrdinalIgnoreCase)) { args = ""; return true; }
        if (text.StartsWith(action + " ", StringComparison.OrdinalIgnoreCase)) { args = text[(action.Length + 1)..]; return true; }
        args = ""; return false;
    }
    private static bool IsDuelOpenAction(string text)
    {
        if (text.Length < 4 || !text[..4].Equals("duel", StringComparison.OrdinalIgnoreCase)) return false;
        return text[4..].All(character => character is '!' or '！');
    }
    private static long GetUserId(object message) => Convert.ToInt64(message.GetType().GetProperty("UserId")?.GetValue(message) ?? 0, CultureInfo.InvariantCulture);
    private static (string First, string Remainder) SplitFirst(string? value)
    {
        var text = value?.Trim() ?? ""; var index = text.IndexOfAny([' ', '\t', '\r', '\n']);
        return index < 0 ? (text, "") : (text[..index], text[(index + 1)..].TrimStart());
    }
    private static string Decode(string value) => value.Replace("\\n", "\n");
    private string Text(string key) => ProcessRandomSelections(_state.Texts.TryGetValue(key, out var value) ? value : $"[{key}]");

    // Keep the portable package independent from Core while supporting the host's
    // commonly used nested [option||option<weight 2>] feedback-template syntax.
    private static string ProcessRandomSelections(string content)
    {
        for (var guard = 0; guard < 50; guard++)
        {
            var matches = Regex.Matches(content, @"\[([^\[\]]+)\]");
            var match = matches.Cast<Match>().FirstOrDefault(x => x.Groups[1].Value.Contains("||", StringComparison.Ordinal));
            if (match is null) break;

            var choices = match.Groups[1].Value.Split("||", StringSplitOptions.None)
                .Select(option =>
                {
                    var weightMatch = Regex.Match(option, @"<weight\s+(\d+)>", RegexOptions.IgnoreCase);
                    var weight = weightMatch.Success && int.TryParse(weightMatch.Groups[1].Value, out var parsed) && parsed > 0 ? parsed : 1;
                    return (Text: Regex.Replace(option, @"<weight\s+\d+>", "", RegexOptions.IgnoreCase).Trim(), Weight: weight);
                })
                .ToArray();
            var roll = Random.Shared.Next(choices.Sum(x => x.Weight));
            var selected = choices[^1].Text;
            foreach (var choice in choices)
            {
                if (roll < choice.Weight) { selected = choice.Text; break; }
                roll -= choice.Weight;
            }
            content = content.Remove(match.Index, match.Length).Insert(match.Index, selected);
        }
        return content;
    }
    private static string Apply(string template, params (string Key, string Value)[] values)
    { foreach (var (key, value) in values) template = template.Replace("{" + key + "}", value, StringComparison.Ordinal); return template; }
    private static string MeguminItemName(string id) => DuelCatalog.MeguminItemNames.TryGetValue(id, out var name) ? name : id;

    private string MeguminItemsText(DuelSession session, bool includeDescriptions)
    {
        var items = CurrentMeguminItems(session);
        return items.Count == 0
            ? "无"
            : string.Join("\n", items.Select(id => includeDescriptions
                ? $"{MeguminItemName(id)}：{ItemDescription(id)}"
                : MeguminItemName(id)));
    }

    private static bool ShouldShowMeguminItemDescriptions(PlayerState player, DuelSession session) =>
        player.DuelTotalCount <= 5 && session.Calls == 0;

    private string ApplySessionText(string key, PlayerState player, string operationPrompt, params (string Key, string Value)[] values)
    {
        var template = Text(key);
        if (player.DuelTotalCount > 5)
        {
            template = string.Join("\n", template.Split('\n').Where(line =>
                !line.Trim().Equals("{operationPrompt}", StringComparison.Ordinal) &&
                !line.TrimStart().StartsWith("操作：", StringComparison.Ordinal)));
        }
        var allValues = new (string Key, string Value)[values.Length + 1];
        allValues[0] = ("operationPrompt", player.DuelTotalCount > 5 ? "" : operationPrompt);
        Array.Copy(values, 0, allValues, 1, values.Length);
        return Apply(template, allValues);
    }

    private static long SaturatingAdd(long current, long value)
    {
        current = Math.Max(0, current);
        if (value <= 0) return Math.Max(0, current);
        return current > long.MaxValue - value ? long.MaxValue : current + value;
    }

    private static void RecordDuelStatistics(PlayerState player, DuelWinner winner, int playerWonAmount, int playerLostAmount)
    {
        player.DuelTotalCount = SaturatingAdd(player.DuelTotalCount, 1);
        switch (winner)
        {
            case DuelWinner.Player:
                player.DuelWins = SaturatingAdd(player.DuelWins, 1);
                player.DuelWonAmount = SaturatingAdd(player.DuelWonAmount, Math.Max(0, playerWonAmount));
                break;
            case DuelWinner.Megumin:
                player.DuelLosses = SaturatingAdd(player.DuelLosses, 1);
                player.DuelLostAmount = SaturatingAdd(player.DuelLostAmount, Math.Max(0, playerLostAmount));
                break;
            default:
                player.DuelTies = SaturatingAdd(player.DuelTies, 1);
                break;
        }
    }

    private static bool HasVanirMask(DuelSession session) =>
        CurrentMeguminItems(session).Contains("vanir_mask", StringComparer.OrdinalIgnoreCase);

    private static List<string> CurrentMeguminItems(DuelSession session)
    {
        var items = new List<string>();
        if (session.MeguminItemIds is { Count: > 0 }) items.AddRange(session.MeguminItemIds);
        else if (!string.IsNullOrWhiteSpace(session.MeguminItemId)) items.Add(session.MeguminItemId);
        if (!string.IsNullOrWhiteSpace(session.ExtraMeguminItemId)) items.Add(session.ExtraMeguminItemId);
        return items;
    }

    private static void NormalizeSessionLocked(DuelSession session)
    {
        if (session.StartingAmount <= 0) session.StartingAmount = Math.Max(100, session.ParticipationAmount);
        for (var i = 0; i < session.PlayerDice.Count; i++)
            session.PlayerDice[i] = DuelEngine.NormalizeDieValue(session.PlayerDice[i]);
        for (var i = 0; i < session.MeguminDice.Count; i++)
            session.MeguminDice[i] = DuelEngine.NormalizeDieValue(session.MeguminDice[i]);
        session.MeguminItemIds ??= [];
        if (session.MeguminItemIds.Count == 0 && !string.IsNullOrWhiteSpace(session.MeguminItemId))
            session.MeguminItemIds.Add(session.MeguminItemId);
        session.PlayerItems ??= [];
        session.PlayerExtraItemUses = Math.Max(0, session.PlayerExtraItemUses);
        if (session.PlayerItems.Count == 0 && !string.IsNullOrWhiteSpace(session.PlayerItemId))
            session.PlayerItems.Add(new PlayerItemUse
            {
                ItemId = session.PlayerItemId,
                Target = session.PlayerItemTarget,
                TargetOnMegumin = session.PlayerItemTargetOnMegumin
            });
        if (session.PlayerItemUseCount < session.PlayerItems.Count)
            session.PlayerItemUseCount = session.PlayerItems.Count;
        if (session.PlayerUsedItem && session.PlayerItemUseCount == 0)
            session.PlayerItemUseCount = 1;
        session.PlayerUsedItem = session.PlayerItemUseCount > 0;
    }

    private string SelectIouTextLocked(int amount)
    {
        var matching = _state.IouTexts.Where(entry =>
            amount >= entry.Start && (entry.Duration == 0 || amount <= entry.End)).ToArray();
        var candidates = matching.Length > 0
            ? matching
            : _state.IouTexts.Where(entry => entry.Start == 0 && entry.Duration == 0).ToArray();
        var totalWeight = candidates.Where(entry => entry.Weight > 0).Sum(entry => (long)entry.Weight);
        var selected = totalWeight > 0
            ? SelectWeightedIouText(candidates, Random.Shared.NextInt64(totalWeight))
            : null;
        return selected is null ? Text("IouFallback") : ProcessRandomSelections(selected.Text);
    }

    private static IouText? SelectWeightedIouText(IReadOnlyList<IouText> candidates, long roll)
    {
        long cumulativeWeight = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Weight <= 0) continue;
            cumulativeWeight += candidate.Weight;
            if (roll < cumulativeWeight) return candidate;
        }
        return null;
    }

    private string ItemDescription(string itemId)
    {
        var key = "ItemDescription_" + itemId;
        return _state.Texts.TryGetValue(key, out var configured)
            ? ProcessRandomSelections(configured)
            : _items.TryGetValue(itemId, out var item) ? item.Description
            : itemId switch
            {
                "megumin_jar" => "惠惠额外投出一颗骰子，该骰会正常参与本局结算。",
                "explosion" => "惠惠释放爆裂魔法，最终得分额外增加 1–50 点。",
                "face_eraser" => "惠惠用镜子将玩家场上点数最高的一颗骰降低 20 点，最低保留 1。",
                "vanir_mask" => "本局惠惠若选中此道具，则第2颗骰的个位数也会隐藏；首骰原本就会隐藏，并可被道具揭露。",
                _ => "暂无介绍。"
            };
    }

    private void LoadLocked()
    {
        if (!File.Exists(_statePath)) { _state = CreateDefaults(); return; }
        try
        {
            var stateJson = MigrateLegacyIouTextEntries(File.ReadAllText(_statePath));
            _state = JsonSerializer.Deserialize<DuelPersistentState>(stateJson, _json) ?? CreateDefaults();
        }
        catch (Exception ex) { _context.Log(LogLevel.Error, $"状态文件读取失败，已使用默认状态：{ex.Message}"); _state = CreateDefaults(); }
    }

    private static string MigrateLegacyIouTextEntries(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return json;
        var property = root.FirstOrDefault(pair => pair.Key.Equals(nameof(DuelPersistentState.IouTexts), StringComparison.OrdinalIgnoreCase));
        if (property.Value is not JsonArray entries || !entries.Any(entry => entry is JsonValue value && value.TryGetValue<string>(out _))) return json;

        var migrated = new JsonArray();
        var legacyIndex = 0;
        foreach (var entry in entries)
        {
            if (entry is JsonValue value && value.TryGetValue<string>(out var text))
            {
                migrated.Add(new JsonObject
                {
                    [nameof(IouText.Id)] = $"legacy-{++legacyIndex}",
                    [nameof(IouText.Start)] = 0,
                    [nameof(IouText.Duration)] = 0,
                    [nameof(IouText.Text)] = text
                });
            }
            else migrated.Add(entry?.DeepClone());
        }
        root[property.Key] = migrated;
        return root.ToJsonString();
    }

    private void SaveLocked()
    {
        if (string.IsNullOrWhiteSpace(_statePath)) return;
        Directory.CreateDirectory(_dataDirectory);
        var temp = _statePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, _json));
        File.Move(temp, _statePath, true);
    }

    private void MergeDefaultsLocked()
    {
        _state.Config ??= new DuelConfig(); _state.Players ??= []; _state.Texts ??= new(StringComparer.OrdinalIgnoreCase); _state.ExplosionTexts ??= []; _state.IouTexts ??= [];
        _state.Config.MeguminDailyCashMax = Math.Clamp(_state.Config.MeguminDailyCashMax, 0, 2000);
        _state.Config.MeguminDailyCashMin = Math.Clamp(_state.Config.MeguminDailyCashMin, 0, _state.Config.MeguminDailyCashMax);
        foreach (var player in _state.Players.Values)
        {
            player.Ious ??= [];
            player.DuelTotalCount = Math.Max(0, player.DuelTotalCount);
            player.DuelWins = Math.Max(0, player.DuelWins);
            player.DuelLosses = Math.Max(0, player.DuelLosses);
            player.DuelTies = Math.Max(0, player.DuelTies);
            player.DuelWonAmount = Math.Max(0, player.DuelWonAmount);
            player.DuelLostAmount = Math.Max(0, player.DuelLostAmount);
        }
        var defaults = DefaultTexts();
        if (_state.TextSchemaVersion < 1)
        {
            foreach (var key in new[] { "Rule", "DuelStart", "DuelStatus", "CallSuccess", "Settlement", "PlayerWin", "MeguminWin", "Tie" })
                _state.Texts[key] = defaults[key];
            foreach (var key in _state.Texts.Keys.Where(key => key.EndsWith("V2", StringComparison.OrdinalIgnoreCase) || key.EndsWith("V3", StringComparison.OrdinalIgnoreCase)).ToArray())
                _state.Texts.Remove(key);
            _state.TextSchemaVersion = 1;
        }
        foreach (var entry in defaults) _state.Texts.TryAdd(entry.Key, entry.Value);
        const string previousLeaderboardRow = "{rank}. QQ {qq} — {balance}厄里斯";
        const string previousNicknameLeaderboardRow = "{rank}.{nickname}({qq})——{balance}厄里斯";
        if (_state.Texts.TryGetValue("LeaderboardRow", out var leaderboardRow) &&
            leaderboardRow is previousLeaderboardRow or previousNicknameLeaderboardRow)
            _state.Texts["LeaderboardRow"] = defaults["LeaderboardRow"];
        const string previousItemTargetPrompt = "已选择「{item}」。\n介绍：{description}\n请选择要作用的己方骰子（1-{range}）。\n玩家骰：\n{playerDice}\n惠惠骰：\n{meguminDice}\n请直接输入骰子序号；输入 cancel 可以取消。";
        if (_state.Texts.TryGetValue("ItemTargetPrompt", out var itemTargetPrompt) && itemTargetPrompt == previousItemTargetPrompt)
            _state.Texts["ItemTargetPrompt"] = defaults["ItemTargetPrompt"];
        const string previousFaceEraserDescription = "擦除玩家场上点数最高的一颗骰；并列时优先擦除可能吞掉惠惠骰子的那颗。";
        if (_state.Texts.TryGetValue("ItemDescription_face_eraser", out var faceEraserDescription) &&
            faceEraserDescription is previousFaceEraserDescription or "擦除玩家场上点数最高的一颗骰；若最高点数并列，则随机擦除其中一颗。")
            _state.Texts["ItemDescription_face_eraser"] = defaults["ItemDescription_face_eraser"];
        const string previousMeguminJarDescription = "惠惠额外投出一颗骰子，该骰会正常参与本局结算。";
        if (_state.Texts.TryGetValue("ItemDescription_megumin_jar", out var meguminJarDescription) && meguminJarDescription == previousMeguminJarDescription)
            _state.Texts["ItemDescription_megumin_jar"] = defaults["ItemDescription_megumin_jar"];
        const string previousMeguminExplosionDescription = "惠惠释放爆裂魔法，最终得分额外增加一次 1–100 的 D100 点数。";
        if (_state.Texts.TryGetValue("ItemDescription_explosion", out var meguminExplosionDescription) && meguminExplosionDescription == previousMeguminExplosionDescription)
            _state.Texts["ItemDescription_explosion"] = defaults["ItemDescription_explosion"];
        const string previousMeguminMirrorText = "惠惠用镜子擦掉了玩家的第 {target} 颗骰：[{value}]。";
        if (_state.Texts.TryGetValue("MeguminFaceEraserApplied", out var previousMeguminMirrorTextValue) && previousMeguminMirrorTextValue == previousMeguminMirrorText)
            _state.Texts.Remove("MeguminFaceEraserApplied");
        const string previousDefaultRule = "骰子对决规则：双方从3颗d100开始，可用call追加至5颗。可用 .duel <额度> 指定起始额度（至少100厄里斯），.duel allin 使用当前余额；裸 .duel 使用默认配置额度。每次call追加起始额度的50%（不足整数时向上取整）。起始额度达到200/500/1000时，惠惠分别携带1/2/3件公开作弊道具。1–5固定计200分；96–100计0并销毁自身及后续一个骰；同尾数从双方场上点数最小的可行动骰开始，只能吞掉对方同尾数最大骰并对100取余；对子整组×2，三同整组×4，四同直接胜利。等待时输入 use <编号> 使用道具，输入 duel 或 duel! 开牌。每日机会按 max(1, floor(max(-1, lg(max(信任度, 0.1))) + d3 - 1)) 计算，因此每天至少有1次。";
        if (_state.Texts.TryGetValue("Rule", out var ruleText) && ruleText == previousDefaultRule)
            _state.Texts["Rule"] = defaults["Rule"];
        const string previousPermanentMaskRule = "骰子对决规则：双方从3颗d100开始，可用call追加至5颗。可用 .duel <额度> 指定起始额度（至少100厄里斯），.duel allin 使用当前余额；裸 .duel 使用默认配置额度。每次call追加起始额度的50%（不足整数时向上取整）。起始额度达到200/500/1000时，惠惠分别携带1/2/3件公开作弊道具；她常驻佩戴巴尼尔的假面，首两颗骰的个位数会被隐藏（首骰可被道具揭露）。1–5固定计200分；96–100计0并销毁自身及后续一个骰；同尾数从双方场上点数最小的可行动骰开始，只能吞掉对方同尾数最大骰并对100取余；对子整组×2，三同整组×4，四同直接胜利。维兹商店可用 .duel shop refresh 刷新，费用每日从50厄里斯起、每次增加50厄里斯。等待时输入 use <编号> 使用道具，输入 duel 或 duel! 开牌。每日机会按 max(1, floor(max(-1, lg(max(信任度, 0.1))) + d3 - 1)) 计算，因此每天至少有1次。";
        if (_state.Texts.TryGetValue("Rule", out ruleText) && ruleText == previousPermanentMaskRule)
            _state.Texts["Rule"] = defaults["Rule"];
        const string previousPermanentMaskDescription = "惠惠常驻佩戴此面具；每局开始时隐藏她前两颗骰的个位数，首骰仍可被道具揭露。";
        if (_state.Texts.TryGetValue("ItemDescription_vanir_mask", out var vanirMaskDescription) && vanirMaskDescription == previousPermanentMaskDescription)
            _state.Texts["ItemDescription_vanir_mask"] = defaults["ItemDescription_vanir_mask"];
        const string previousDuelAllowance = "今日机会计算：信任度 {trust}；lg项=max(-1, lg(max(信任度, 0.1)))={logTerm}；D3={d3}；max(0, floor({logTerm}+{d3}-1))=max(0, floor({raw}))={limit} 次。";
        if (_state.Texts.TryGetValue("DuelAllowance", out var duelAllowance) && duelAllowance == previousDuelAllowance)
            _state.Texts["DuelAllowance"] = defaults["DuelAllowance"];
        const string previousCompactDuelAllowance = "今日机会计算:\nlg(信任度)+D3-1=lg({trustForLog})+{d3}-1={limit}\n已用:{used}/{limit}";
        if (_state.Texts.TryGetValue("DuelAllowance", out duelAllowance) && duelAllowance == previousCompactDuelAllowance)
            _state.Texts["DuelAllowance"] = defaults["DuelAllowance"];
        // Older local text overrides used the standalone-command spelling. Normalize
        // every saved template so all prompts consistently teach focus-mode `use`.
        foreach (var key in _state.Texts.Keys.ToArray())
        {
            var normalized = Regex.Replace(_state.Texts[key], @"\.use\b", "use", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            normalized = normalized.Replace("({score}-80)/100", "({score}-20)/100", StringComparison.Ordinal);
            normalized = normalized.Replace("输入其他内容开牌", "输入 duel 开牌", StringComparison.Ordinal);
            normalized = normalized.Replace(
                "等待时输入 use <编号> 使用道具。",
                "等待时输入 use <编号> 使用道具，输入 duel 或 duel! 开牌。每日机会按信任度和 D3 计算。",
                StringComparison.Ordinal);
            if (key.Equals("DuelStart", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("DuelStatus", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("CallSuccess", StringComparison.OrdinalIgnoreCase))
                normalized = NormalizeSessionPromptTemplate(key, normalized);
            if (!string.Equals(normalized, _state.Texts[key], StringComparison.Ordinal))
                _state.Texts[key] = normalized;
        }
        foreach (var entry in DefaultExplosionTexts())
        {
            if (_state.ExplosionTexts.All(existing => !existing.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)))
                _state.ExplosionTexts.Add(entry);
        }
        if (!_state.IouTextsInitialized)
        {
            foreach (var text in DefaultIouTexts())
            {
                if (_state.IouTexts.All(existing => !existing.Id.Equals(text.Id, StringComparison.OrdinalIgnoreCase)))
                    _state.IouTexts.Add(Clone(text));
            }
            _state.IouTextsInitialized = true;
        }
    }

    private static string NormalizeSessionPromptTemplate(string key, string template)
    {
        var lines = template.Split('\n');
        var normalizedLines = new List<string>(lines.Length + 1);
        var operationPromptSeen = false;
        foreach (var line in lines)
        {
            if (line.TrimEnd('\r').TrimStart().StartsWith("操作：", StringComparison.Ordinal) ||
                line.TrimEnd('\r').Trim().Equals("{operationPrompt}", StringComparison.Ordinal))
            {
                if (operationPromptSeen) continue;
                normalizedLines.Add("{operationPrompt}");
                operationPromptSeen = true;
                continue;
            }
            normalizedLines.Add(line);
        }

        if (!operationPromptSeen && key.Equals("CallSuccess", StringComparison.OrdinalIgnoreCase))
        {
            var itemHeaderIndex = normalizedLines.FindIndex(line =>
                line.TrimEnd('\r').Equals("当前可用道具：", StringComparison.Ordinal));
            if (itemHeaderIndex >= 0) normalizedLines.Insert(itemHeaderIndex, "{operationPrompt}");
        }

        return string.Join("\n", normalizedLines);
    }

    private static void ValidateConfig(DuelConfig config)
    {
        if (config.BaseAmount < 100 || config.CallDiceCap < 3 || config.ExplosionDailyLimit < 1) throw new ArgumentOutOfRangeException(nameof(config), "默认起始额度不能低于100，骰数上限至少为3，每日爆裂次数至少为1。");
        if (config.MeguminDailyCashMin < 0 || config.MeguminDailyCashMax > 2000 || config.MeguminDailyCashMin > config.MeguminDailyCashMax || config.ExplosionRewardMin > config.ExplosionRewardMax || config.RPriceMin > config.RPriceMax || config.SrPriceMin > config.SrPriceMax || config.SsrPriceMin > config.SsrPriceMax) throw new ArgumentException("最小值不能大于最大值；惠惠每日随身额度上限为2000厄里斯。");
        foreach (var probability in new[] { config.PickupChance, config.RWeight, config.SrWeight, config.SsrWeight, config.SuperDiscountChance, config.PriceEventChance }) if (probability is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(config), "概率必须在0到1之间。");
    }

    private DuelPersistentState CreateDefaults() => new()
    {
        TextSchemaVersion = 1, Config = new DuelConfig(), MeguminBalance = 500, Texts = DefaultTexts(), ExplosionTexts = DefaultExplosionTexts(), IouTexts = DefaultIouTexts(), IouTextsInitialized = true
    };

    private Dictionary<string, string> DefaultTexts() => new(_defaultTexts, StringComparer.OrdinalIgnoreCase);

    private List<ExplosionText> DefaultExplosionTexts() => _defaultExplosionTexts
        .Select(entry => new ExplosionText { Id = entry.Id, Start = entry.Start, Duration = entry.Duration, Text = entry.Text })
        .ToList();

    private List<IouText> DefaultIouTexts() => _defaultIouTexts.Select(Clone).ToList();

    private static IouText Clone(IouText entry) => new()
    {
        Id = entry.Id,
        Start = entry.Start,
        Duration = entry.Duration,
        Weight = entry.Weight,
        Text = entry.Text
    };

    private bool LoadPackagedDefaultsLocked()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(MeguminDuelMod).Assembly.Location)
            ?? throw new InvalidOperationException("无法确定 MeguminDuel.dll 所在目录。");
        var path = Path.Combine(assemblyDirectory, "default-texts.json");
        if (!File.Exists(path))
        {
            _context.Log(LogLevel.Warn, $"未找到编译前文本配置 {path}，使用代码内置默认值。");
            return false;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var configured = JsonSerializer.Deserialize<PackagedDefaultConfiguration>(File.ReadAllText(path), options)
                ?? throw new InvalidDataException("JSON 内容为空。");
            if (configured.Texts.Count == 0) throw new InvalidDataException("texts 不能为空。");
            configured.ItemRarities ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rarityOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (itemId, rarityValue) in configured.ItemRarities)
            {
                if (!DuelCatalog.Items.ContainsKey(itemId))
                    throw new InvalidDataException($"itemRarities 包含未知道具 ID：{itemId}");
                var rarity = (rarityValue ?? "").Trim().ToUpperInvariant();
                if (rarity is not ("R" or "SR" or "SSR"))
                    throw new InvalidDataException($"道具 {itemId} 的稀有度必须是 R、SR 或 SSR。");
                rarityOverrides[itemId] = rarity;
            }
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in configured.ExplosionTexts)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id))
                    throw new InvalidDataException($"explosionTexts 包含空 ID 或重复 ID：{entry.Id}");
                if (entry.Start < 0 || entry.Duration < 0 || entry.Start + entry.Duration > 100)
                    throw new InvalidDataException($"爆裂文本 {entry.Id} 的区间必须位于 0–100。");
                if (string.IsNullOrWhiteSpace(entry.Text))
                    throw new InvalidDataException($"爆裂文本 {entry.Id} 的 text 不能为空。");
            }
            var iouIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in configured.IouTexts)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || !iouIds.Add(entry.Id))
                    throw new InvalidDataException($"iouTexts 包含空 ID 或重复 ID：{entry.Id}");
                if (entry.Start < 0 || entry.Duration < 0 || (entry.Duration > 0 && entry.End > int.MaxValue))
                    throw new InvalidDataException($"欠条文本 {entry.Id} 的面值范围无效。");
                if (entry.Weight <= 0)
                    throw new InvalidDataException($"欠条文本 {entry.Id} 的 weight 必须是大于 0 的整数。");
                if (string.IsNullOrWhiteSpace(entry.Text))
                    throw new InvalidDataException($"欠条文本 {entry.Id} 的 text 不能为空。");
            }
            if (configured.IouTexts.Count == 0 || configured.IouTexts.All(entry => entry.Start != 0 || entry.Duration != 0))
                throw new InvalidDataException("iouTexts 至少需要一个 start=0 且 duration=0 的无限通用条目。");
            _items = DuelCatalog.Items.ToDictionary(
                item => item.Key,
                item => rarityOverrides.TryGetValue(item.Key, out var rarity) ? item.Value with { Rarity = rarity } : item.Value,
                StringComparer.OrdinalIgnoreCase);
            _defaultTexts = new Dictionary<string, string>(configured.Texts, StringComparer.OrdinalIgnoreCase);
            _defaultExplosionTexts = configured.ExplosionTexts
                .Select(entry => new ExplosionText { Id = entry.Id, Start = entry.Start, Duration = entry.Duration, Text = entry.Text })
                .ToList();
            _defaultIouTexts = configured.IouTexts.Select(Clone).ToList();
            _context.Log(LogLevel.Info, $"已读取编译前默认配置：{path}");
            return true;
        }
        catch (Exception ex)
        {
            _context.Log(LogLevel.Error, $"default-texts.json 无效，使用代码内置默认值：{ex.Message}");
            return false;
        }
    }

    private static Dictionary<string, string> BuiltInDefaultTexts() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PlayerWinTaunt"] = "[这点程度，还难不倒我。||惠惠，下一局可别再放水了。]",
        ["MeguminWinTaunt"] = "[哼哼，这就是红魔族首屈一指的实力！||想赢我？明天再多带一点气势来吧。]",
        ["TieTaunt"] = "[这次算你有点本事。||平局？下一次一定要分出胜负！]",
        ["Rule"] = "骰子对决规则：双方从3颗d100开始，可用call追加至5颗。可用 .duel <额度> 指定起始额度（至少100厄里斯），.duel allin 使用当前余额；裸 .duel 使用默认配置额度。每次call追加起始额度的50%（不足整数时向上取整）。起始额度达到200/500/1000时，惠惠分别从道具池携带1/2/3件公开道具；若开局选中巴尼尔的假面，则首骰之外第二颗骰的个位也会隐藏（首骰可被道具揭露）。1–5固定计200分；96–100计0并销毁自身及后续一个骰；同尾数从双方场上点数最小的可行动骰开始，只能吞掉对方同尾数最大骰并对100取余；对子整组×2，三同整组×4，四同直接胜利。维兹商店可用 .duel shop refresh 刷新，费用每日从50厄里斯起、每次增加50厄里斯。等待时输入 use <编号> 使用道具，输入 duel 或 duel! 开牌。每日机会按 max(1, floor(max(-1, lg(max(信任度, 0.1))) + d3 - 1)) 计算，因此每天至少有1次。",
        ["DuelStart"] = "[来吧，让我看看你的气势！||这次我可是有备而来。]\n{duelAllowance}\n玩家骰：\n{playerDice}\n惠惠骰：\n{meguminDice}\n本局额度：{amount}厄里斯\n惠惠公开道具：{meguminItem}\n玩家存款：{playerBalance}厄里斯\n惠惠存款：{meguminBalance}厄里斯\n{operationPrompt}\n当前可用道具：",
        ["DuelStatus"] = "玩家骰：\n{playerDice}\n惠惠骰：\n{meguminDice}\n本局额度：{amount}厄里斯\n惠惠公开道具：{meguminItem}\n玩家存款：{playerBalance}厄里斯\n惠惠存款：{meguminBalance}厄里斯\n{operationPrompt}",
        ["DuelStatistics"] = "对局统计：{total}场（胜{wins}/负{losses}/平{ties}）\n累计赢得：{won}厄里斯；累计输掉：{lost}厄里斯",
        ["DuelDailyLimit"] = "今天的骰子对决机会已用完。\n{duelAllowance}",
        ["DuelAllowance"] = "今日机会计算:\nlg(信任度)+D3-1=lg({trustForLog})+{d3}-1={rawLimit}\n已用:{used}/{limit}{fallback}",
        ["DuelFocusReminder"] = "本局尚未开牌。输入 duel、duel! 或 duel！！ 开牌；也可输入 call 追加骰子，或输入 use 使用道具。",
        ["DuelAmountTooLow"] = "起始额度至少为100厄里斯。格式：.duel <额度> 或 .duel allin。",
        ["AllInAmountTooLow"] = "当前余额不足100厄里斯，无法用 allin 开始对决。",
        ["NoSession"] = "当前没有进行中的骰子对决，请先使用 .duel。",
        ["CallCap"] = "普通追加已经达到双方5颗骰的上限。",
        ["CallSuccess"] = "双方各追加一颗骰，本局额度变为{amount}厄里斯。\n玩家骰：\n{playerDice}\n惠惠骰：\n{meguminDice}\n{operationPrompt}\n当前可用道具：",
        ["CallAmountOverflow"] = "追加后额度超出可处理范围，本次没有追加骰子。",
        ["ItemAlreadyUsed"] = "本局已经使用过一件道具。",
        ["ItemUsesExhausted"] = "本局道具使用次数已用完（{used}/{limit}）。",
        ["ItemNotOwned"] = "库存中没有这件道具。",
        ["ItemPrepared"] = "已取出「{item}」，它会按优先级在开牌时生效。\n介绍：{description}",
        ["ItemTargetPrompt"] = "已选择「{item}」。\n介绍：{description}\n请选择要作用的{side}骰子（1-{range}）。\n玩家骰：\n{playerDice}\n惠惠骰：\n{meguminDice}\n请直接输入骰子序号；输入 cancel 可以取消。",
        ["ItemTargetInvalid"] = "骰子序号无效，请输入 1-{range}。",
        ["ItemTargetCancelled"] = "已取消选择道具，本局没有消耗道具。",
        ["ItemTargetGone"] = "指定骰子已先被移除，道具未能产生效果。",
        ["Settlement"] = "开牌！\n玩家最终骰：\n{playerDice}\n得分：{playerScore}\n惠惠最终骰：\n{meguminDice}\n得分：{meguminScore}\n本局额度：{amount}厄里斯",
        ["PlayerWin"] = "玩家获胜。\n玩家存款：{balance}厄里斯\n惠惠存款：{meguminBalance}厄里斯",
        ["MeguminWin"] = "惠惠获胜。\n玩家存款：{balance}厄里斯\n惠惠存款：{meguminBalance}厄里斯",
        ["Tie"] = "双方同分，本局不发生额度转移。\n玩家存款：{balance}厄里斯\n惠惠存款：{meguminBalance}厄里斯",
        ["PlayerDebtMock"] = "[连这点零钱都拿不出来吗？先记在账上好了。||哼哼，欠条我可收好了。]",
        ["IouFallback"] = "惠惠认真写下了这张欠条。",
        ["IouIssued"] = "惠惠今日带出的厄里斯不足，本次支付{paid}厄里斯，并交付一张{amount}厄里斯的欠条（{id}）。\n{text}",
        ["IouCollectionTitle"] = "惠惠欠条收藏（{count}张）",
        ["IouCollectionRow"] = "{index}. [{id}] {amount}厄里斯｜{date}\n{text}",
        ["IouCollectionEmpty"] = "你还没有收藏到惠惠的欠条。",
        ["ExplosionDailyLimit"] = "今天已经释放过爆裂魔法了。",
        ["ExplosionFallback"] = "爆裂的余波散去，四周一片寂静。",
        ["ExplosionResult"] = "Explosion！评分：{score}\n{text}\n获得{reward}厄里斯，当前余额{balance}。\n信任度变化：({score}-20)/100={trustDelta}，当前信任度：{trust}。",
        ["Pickup"] = "回家路上捡到了「{item}」。",
        ["ShopTitle"] = "维兹的商店·今日货架",
        ["ShopSold"] = "这件商品今天已经买过了。",
        ["ShopBought"] = "购入「{item}」，支付{price}厄里斯，当前余额{balance}。",
        ["ShopRefreshed"] = "支付{price}厄里斯刷新了货架（今日第{count}次），当前余额{balance}厄里斯。",
        ["LeaderboardTitle"] = "全服财富排行榜·前十名",
        ["LeaderboardRow"] = "{rank}.{nickname}({qq})\n——{balance}厄里斯。",
        ["LeaderboardMeguminRow"] = "{rank}.惠惠(骰娘)\n——{balance}厄里斯。",
        ["LeaderboardEmpty"] = "财富排行榜暂时没有记录。",
        ["DebtLeaderboardTitle"] = "财产负排名·前十名",
        ["DebtLeaderboardRow"] = "{rank}.{nickname}({qq})\n——负债{debt}厄里斯。",
        ["DebtLeaderboardMeguminRow"] = "{rank}.惠惠(骰娘)\n——负债{debt}厄里斯。",
        ["DebtLeaderboardEmpty"] = "负债排行榜暂时没有记录。",
        ["PantiesMegumin"] = "惠惠气急败坏，决定额外掏出一件道具。",
        ["PantiesPlayer"] = "玩家把纯白胖次戴在头上，获得最终分数+50。",
        ["DemonHeartApplied"] = "恶魔之心迸发力量，本局额外获得两次道具使用机会；当前还可使用{remaining}件。",
        ["DivineHoodEquipped"] = "你戴上神圣兜帽；若本局落败，最终需支付的额度将减少80%。",
        ["DivineHoodApplied"] = "神圣兜帽替你挡下大部分冲击：原需支付{original}厄里斯，减少80%后按厄里斯向上取整，实际支付{amount}厄里斯（少支付{saved}厄里斯）。",
        ["KneelingMat"] = "玩家熟练地铺好垫子土下座，气氛一时非常微妙。",
        ["FlowerSuccess"] = "花奏效了：己方骰 {target} 从 [{old}] 重投为 [{value}]。",
        ["FlowerMiss"] = "花送到了，但重投机会没有出现。",
        ["UnstableDieApplied"] = "不稳定的骰子让己方骰 {target} 变为 [{value}]。",
        ["VanishingInkApplied"] = "墨水擦掉十位：己方骰 {target} 从 [{old}] 变为 [{value}]。",
        ["CrackedDieApplied"] = "开裂骰子将己方骰 {target} 的 [{old}] 分成了 [{first}] 和 [{second}]。",
        ["LoadedDieApplied"] = "灌铅骰子重投己方骰 {target}：[{old}] 与新值 [{roll}] 比较，保留 [{value}]。",
        ["SchrodingerDieApplied"] = "薛定谔的骰子重投己方骰 {target}：[{old}] 变为 [{value}]。",
        ["AxisOrderEmblemApplied"] = "阿克西斯教圣徽改变了惠惠第 {target} 颗骰的十位：[{old}] → [{value}]。",
        ["PeacefulGirlFigureApplied"] = "安乐少女手办为你带来平静，最终分数 +20。",
        ["ForkedWayEyeApplied"] = "歧路思义眼立即映出惠惠首骰的个位数：{digit}。",
        ["GameConsoleApplied"] = "游戏机追加并投出第 {target} 颗己方骰：[{value}]。",
        ["MeguminJarApplied"] = "惠惠从秘藏骰子罐掏出一颗骰并且投出了：[{value}]。",
        ["MeguminJarKept"] = "惠惠模拟结果认为保留新骰更有利，因此将它加入了骰场。",
        ["MeguminJarReplaced"] = "惠惠模拟后认为更换更有利，将最低骰第 {target} 颗从 [{old}] 换成 [{value}]。",
        ["MeguminMirrorApplied"] = "惠惠用镜子削减玩家最大骰的十位数：第 {target} 颗骰从 [{old}] 变为 [{value}]。",
        ["CabbageSuccess"] = "卷心菜把惠惠夸得飘飘然，玩家最终分数+50。",
        ["CabbageMiss"] = "卷心菜说得天花乱坠，可惠惠似乎没有上当。",
        ["MeguminExplosionBonus"] = "Explosion！惠惠释放爆裂魔法，最终得分追加了 [{value}] 点。",
        ["ItemDescription_kneeling_mat"] = "铺好垫子行礼，触发一段特殊演出；不改变骰子或分数。",
        ["ItemDescription_flower"] = "选择一颗己方骰。结算时有 30% 概率重新投掷它，未触发时只播放未能奏效的文本。",
        ["ItemDescription_unstable_die"] = "选择一颗己方骰，使其点数减少 10；修改后的点数会正常参与大成功、大失败和后续规则。",
        ["ItemDescription_white_panties"] = "结算时有 60% 概率让惠惠额外使用一件不同的作弊道具；否则玩家最终得分增加 50 分。",
        ["ItemDescription_vanishing_ink"] = "选择一颗己方骰，将其十位数字擦除、保留个位；修改后的点数会正常参与后续规则。",
        ["ItemDescription_talking_cabbage"] = "卷心菜会努力夸奖惠惠；有 20% 概率令玩家最终得分增加 50 分。",
        ["ItemDescription_game_console"] = "立即为玩家额外投出一颗己方骰；该骰会和其他骰一样参与本局结算。",
        ["ItemDescription_cracked_die"] = "选择一颗己方骰，将点数尽量均分为两颗骰，总点数保持不变；奇数时一颗多 1 点。点数为 1 时两颗骰均为 1。",
        ["ItemDescription_loaded_die"] = "选择一颗己方骰并重投一次，保留原点数与新点数中的较高值。",
        ["ItemDescription_schrodinger_die"] = "选择一颗己方骰并重投一次，直接采用新点数。",
        ["ItemDescription_demon_heart"] = "使用后消耗本次道具使用机会，并使本局额外获得两次道具使用机会；可在当前 focus 中继续使用库存道具。",
        ["ItemDescription_divine_hood"] = "套在头上挨打。本局若玩家落败，最终需要支付的本局额度减少80%；折减后按厄里斯向上取整。平局或获胜时不触发。",
        ["ItemDescription_megumin_jar"] = "惠惠投出一颗新骰，并模拟比较保留新骰或替换场上最低骰的结果，采用更有利于惠惠的方案。",
        ["ItemDescription_explosion"] = "惠惠释放爆裂魔法，最终得分额外增加 1–50 点。",
        ["ItemDescription_face_eraser"] = "惠惠用镜子将玩家场上点数最高的一颗骰降低 20 点（十位数削减 2），最低保留 1。",
        ["ItemDescription_vanir_mask"] = "本局惠惠若选中此道具，则第2颗骰的个位数也会隐藏；首骰原本就会隐藏，并可被道具揭露。",
        ["ItemDescription_axis_order_emblem"] = "选择一颗对方场上的骰，重投它的十位数字并保留个位；修改后的点数正常参与大成功、大失败和后续结算。",
        ["ItemDescription_peaceful_girl_figure"] = "开牌结算时令自己的最终分数增加 20 分。",
        ["ItemDescription_forked_path_eye"] = "使用时立即揭露惠惠首骰的个位数字，不必等待开牌；此效果会持续显示在本局骰面中。"
    };

    private static List<ExplosionText> BuiltInDefaultExplosionTexts() =>
    [
        new() { Id = "quiet", Start = 0, Duration = 20, Text = "只有一阵可疑的烟冒了出来。" },
        new() { Id = "normal", Start = 20, Duration = 40, Text = "漂亮的爆裂在远处炸开。" },
        new() { Id = "great", Start = 60, Duration = 30, Text = "冲击波卷过荒野，惠惠露出了满意的笑容。" },
        new() { Id = "legend", Start = 90, Duration = 10, Text = "天地为之一震——这是值得铭记的爆裂！" }
    ];

    private static List<IouText> BuiltInDefaultIouTexts() =>
    [
        new() { Id = "small-debt", Start = 1, Duration = 498, Text = "惠惠郑重声明：等下一次讨伐任务结算后一定偿还。" },
        new() { Id = "medium-debt", Start = 500, Duration = 1499, Text = "这张纸上还残留着爆裂魔法的焦味。" },
        new() { Id = "large-debt", Start = 2000, Duration = 7999, Text = "白纸，上面用胶带粘了一撮头发，不知道是什么红魔族的传统。" },
        new() { Id = "generic", Start = 0, Duration = 0, Text = "惠惠按下红魔族首屈一指魔法师的手印，保证这不是普通废纸。" }
    ];
}
