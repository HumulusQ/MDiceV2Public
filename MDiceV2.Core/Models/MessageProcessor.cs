using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;
using MDiceV2.Core.Mod;
using MDiceV2.Core.Infrastructure;
using static MDiceV2.Models.Dice;
using System.Net.WebSockets;

namespace MDiceV2.Models;

/// <summary>
/// 脚本函数执行器委托
/// 用于处理自定义脚本函数调用（如 &lt;func&gt; 标签）
/// </summary>
/// <param name="funcSpec">函数规范字符串，格式如 "FunctionName()"</param>
/// <param name="msg">当前消息对象</param>
/// <returns>函数执行结果字符串</returns>
public delegate string ScriptFunctionExecutor(string funcSpec, Msg msg);

/// <summary>
/// 基本配置类
/// 存储机器人的基本配置信息
/// </summary>
public class BasicConfig
{   
    public string Url { get; set; } = "ws://localhost:8080";
    /// <summary>
    /// Master用户ID
    /// </summary>
    public string Master { get; set; } = string.Empty;

    /// <summary>
    /// Master群ID
    /// </summary>
    public string MasterGroup { get; set; } = string.Empty;

    /// <summary>
    /// 是否自动同意好友请求
    /// </summary>
    public bool ApproveFriendJoinRequest { get; set; }

    /// <summary>
    /// 是否自动同意群请求
    /// </summary>
    public bool ApproveGroupJoinRequest { get; set; }

    /// <summary>
    /// 是否发送群加入报告
    /// </summary>
    public bool SendGroupJoinReport { get; set; }

    /// <summary>
    /// 是否发送好友加入报告
    /// </summary>
    public bool SendFriendJoinReport { get; set; }

}

/// <summary>
/// 消息处理器
/// 负责处理用户指令和消息逻辑
/// 支持依赖注入和单例兼容性
/// </summary>
public partial class MessageProcessor : ObservableObject
{
    /// <summary>
    /// 单例实例（用于向后兼容，新代码应使用DI注入）
    /// </summary>
    public static MessageProcessor? Instance { get; private set; }

    private static readonly object _instanceLock = new object();

    /// <summary>
    /// 数据输入输出管理器
    /// </summary>
    public DataIO? DataIO { get; private set; }

    /// <summary>
    /// 规则数据输入输出管理器
    /// </summary>
    public RuleDataIO? RuleDataIO { get; private set; }

    /// <summary>
    /// UI调度器（通过依赖注入注入）
    /// </summary>
    private MDiceV2.Abstractions.IDispatcher? _dispatcher;



    /// <summary>
    /// 日志启用状态字典
    /// Key: 群ID, Value: 是否启用
    /// </summary>
    public ConcurrentDictionary<long, bool> _logEnabledStates = new();

    /// <summary>
    /// 日志回放状态字典
    /// Key: 群ID, Value: 回放状态（日志名称和页数）
    /// </summary>
    public ConcurrentDictionary<long, LogReplayState> _logReplayStates = new();

    /// <summary>
    /// 日志回放状态
    /// </summary>
    public class LogReplayState
    {
        public string LogName { get; set; } = "";
        public int Page { get; set; } = 1;
    }

    /// <summary>
    /// 当前规则书名称字典
    /// Key: 用户ID, Value: 规则书名
    /// </summary>
    private ConcurrentDictionary<long, string> currentRulebookNames = new();

    /// <summary>
    /// Mod全局变量存储
    /// Key: ModId, Value: 该Mod的Key-Value存储字典
    /// 每个Mod可通过RefineMsg中的<read:key>和<write:key,value>操作来访问
    /// </summary>
    private ConcurrentDictionary<string, Dictionary<string, string>> modStorages = new();

    /// <summary>
    /// 用户自定义显示名称（.name 指令）缓存
    /// Key: 用户ID, Value: 自定义名称
    /// 以开机加载、关机保存的方式与 DataIO 同步，避免频繁 IO。
    /// </summary>
    private ConcurrentDictionary<long, string> userDisplayNames = new();
    /// <summary>
    /// 群先攻列表缓存
    /// Key: 群ID, Value: 该群的先攻列表
    /// </summary>
    private ConcurrentDictionary<long, InitiativeList> groupInitiativeLists = new();
    /// <summary>
    /// 用户好感度（信任度）缓存
    /// Key: 用户ID, Value: 好感度分值
    /// </summary>
    private ConcurrentDictionary<long, double> userTrust = new();

    /// <summary>
    /// 用户白名单（0=白名单，1=默认/受限）
    /// Key: 用户ID, Value: 标志位
    /// </summary>
    // userWhitelist已合并到UserDataRecord中的IsWhitelisted字段

    /// <summary>
    /// 个人授权白名单（仅用于高频校验的内存缓存）
    /// Key: 用户ID
    /// </summary>
    private ConcurrentDictionary<long, byte> personAuth = new();

    /// <summary>
    /// 用户自定义指令缓存
    /// Key: 用户ID, Value: 自定义指令字典(Key: 指令名, Value: 指令内容)
    /// 用于存储用户通过.diy设置的自定义指令
    /// </summary>
    private ConcurrentDictionary<long, Dictionary<string, string>> userCustomCommands = new();

    /// <summary>
    /// 用户仿名片模板缓存（.cn 指令）
    /// Key: 用户ID, Value: 仿名片模板文本
    /// </summary>
    private ConcurrentDictionary<long, string?> cardNameTemplates = new();

    /// <summary>
    /// 用户仿名片开关缓存（.cn 指令）
    /// Key: "UserId_GroupId", Value: 开关状态 (true=启用，false=禁用)
    /// </summary>
    private ConcurrentDictionary<string, bool> cardNameSwitches = new();

    /// <summary>
    /// 群授权白名单（仅用于高频校验的内存缓存）
    /// Key: 群ID（int）
    /// </summary>
    private ConcurrentDictionary<int, byte> groupAuth = new();

    /// <summary>
    /// 群数据记录（包括Bot启用状态和群白名单授权等级）
    /// Key: 群ID, Value: 群数据记录
    /// </summary>
    private ConcurrentDictionary<long, GroupDataRecord> groupDataRecords = new();

    /// <summary>
    /// 群管理员缓存
    /// Key: "(groupId, userId)", Value: 是否为管理员/群主
    /// 用于在 EnsureMsgAuthInfo 中快速判断用户是否为群管理员
    /// </summary>
    private ConcurrentDictionary<(long groupId, long userId), bool> groupAdminCache = new();

    /// <summary>
    /// 用户默认检定模式缓存
    /// </summary>
    private ConcurrentDictionary<long, string> defaultCheckModes = new();

    private static readonly HashSet<string> supportedCheckModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "coc7",
        "et"
    };

    /// <summary>
    /// 每日计数缓存：好感度当日增量、当日 duel 回合数
    /// Key: 用户ID, Value: 当日计数状态
    /// </summary>
    private ConcurrentDictionary<long, DailyRuntimeState> dailyRuntimeStates = new();

    /// <summary>
    /// 每日使用限额追踪
    /// 用于 CustomizedReply 的每日次数限制功能
    /// Key: "(ruleId_userId)" 或 "(ruleId_*)" 格式的复合键，Value: (当日日期, 触发次数)
    /// </summary>
    private ConcurrentDictionary<string, (DateOnly Date, int Count)> dailyLimitTracking = new();

    /// <summary>
    /// 冷却时间追踪
    /// 用于 CustomizedReply 的时间限制（冷却）功能
    /// Key: "(ruleId_userId)" 或 "(ruleId_*)" 格式的复合键，Value: (最后触发时间, 冷却时长-秒)
    /// </summary>
    private ConcurrentDictionary<string, (DateTime LastTrigger, int CooldownSeconds)> cooldownTracking = new();

    // 好感度与娱乐扣减的参数
    private const double TrustNormalIncrement = 0.1;     // 每次正常指令增加
    private const double TrustDailyGainLimit = 2.0;      // 每日增加上限
    private const double TrustDuelPenalty = 0.2;         // 每次娱乐（duel）扣减

    /// <summary>
    /// 基本配置数据（直接使用GlobalFeedbackMessages管理）
    /// </summary>
    public BasicConfig basicConfigData;

    /// <summary>
    /// 配置锁
    /// </summary>
    private readonly object configLock = new();

    /// <summary>
    /// 周期性保存定时器（每1小时自动保存一次所有数据）
    /// </summary>
    private Timer? _autoSaveTimer;

    /// <summary>
    /// 队伍数据类
    /// 存储队伍的相关信息
    /// </summary>
    private class TeamInfo
    {
        /// <summary>
        /// 队伍名称
        /// </summary>
        public string TeamName { get; set; } = string.Empty;

        /// <summary>
        /// 队伍创建者ID
        /// </summary>
        public long CreatorId { get; set; }

        /// <summary>
        /// 队伍成员ID列表
        /// </summary>
        public List<long> Members { get; set; } = new();

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 群数据持久化结构
    /// 统一管理群的所有持久化数据：Bot启用状态、群白名单授权等级、队伍数据等
    /// </summary>
    private class GroupDataRecord
    {
        /// <summary>
        /// 群ID
        /// </summary>
        public long GroupId { get; set; }

        /// <summary>
        /// Bot启用状态（true=启用，false=禁用）
        /// 默认值为true
        /// </summary>
        public bool BotEnabled { get; set; } = true;

        /// <summary>
        /// 群授权等级 (0=完全授权/白名单, 1=部分授权, 2=受限, 3=无授权, null/未设置=使用默认值)
        /// </summary>
        public int? AuthLevel { get; set; }

        /// <summary>
        /// 群中的队伍字典 (Key: 队伍名, Value: TeamInfo)
        /// 用于存储所有在该群中创建的队伍
        /// </summary>
        public Dictionary<string, TeamInfo>? Teams { get; set; }

        /// <summary>
        /// 用户在本群的默认队伍名 (Key: 用户ID, Value: 队伍名)
        /// 用于记录每个用户在该群的默认使用队伍
        /// </summary>
        public Dictionary<long, string>? UserDefaultTeams { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 群临时牌堆字典 (Key: 牌堆名, Value: 牌堆内容列表)
        /// 用于存储抽出不放回的临时牌堆
        /// </summary>
        public Dictionary<string, List<string>> TemporaryDecks { get; set; } = new Dictionary<string, List<string>>();

        /// <summary>
        /// 群欢迎语（当新成员加入时自动发送）
        /// 为空或null表示未设置欢迎语
        /// </summary>
        public string? Welcome { get; set; }

        /// <summary>
        /// 是否启用入群欢迎语自动发送
        /// </summary>
        public bool WelcomeEnabled { get; set; }
    }

    /// <summary>
    /// 用户数据持久化结构
    /// 统一管理用户的所有持久化数据：显示名、人物卡、好感度、授权等级、默认检定模式
    /// </summary>
    private class UserDataRecord
    {
        /// <summary>
        /// 用户ID
        /// </summary>
        public long UserId { get; set; }

        /// <summary>
        /// 用户自定义显示名
        /// </summary>
        public string? DisplayName { get; set; }

        /// <summary>
        /// 人物卡字典 (Key: 人物名, Value: CharacterSheet)
        /// </summary>
        public Dictionary<string, CharacterSheet>? CharacterSheets { get; set; }

        /// <summary>
        /// 好感度值
        /// </summary>
        public double Trust { get; set; }

        /// <summary>
        /// 授权等级 (0=完全授权, 1=部分授权, 2=受限, 3=无授权, null/未设置=使用默认值)
        /// 合并了原来的PersonAuth和白名单逻辑
        /// </summary>
        public int? AuthLevel { get; set; }

        /// <summary>
        /// 默认检定模式 (coc7/et等)
        /// </summary>
        public string? DefaultCheckMode { get; set; }

        /// <summary>
        /// 白名单标志 (0=在白名单, 1=不在白名单, null/未设置=使用默认值)
        /// 合并了原来的UserWhitelist
        /// </summary>
        public int? IsWhitelisted { get; set; }

        /// <summary>
        /// 用户自定义指令字典 (Key: 指令名, Value: 指令内容)
        /// 用于存储用户通过.diy设置的自定义指令
        /// 使用/[指令名]触发，会将指令内容展开并拼接用户的后续参数
        /// </summary>
        public Dictionary<string, string>? CustomCommands { get; set; }

        /// <summary>
        /// 用户仿名片模板（.cn 指令）
        /// 存储用户设置的群名片模板文本
        /// </summary>
        public string? CardNameTemplate { get; set; }

        /// <summary>
        /// 用户仿名片开关字典（.cn 指令）
        /// Key: "UserId_GroupId", Value: 开关状态 (true=启用，false=禁用)
        /// </summary>
        public Dictionary<string, bool>? CardNameSwitches { get; set; }

        /// <summary>
        /// 默认骰子面数（当掷骰表达式中省略面数时使用）
        /// 例如：.r d 则使用该值作为面数，即 1dDefaultDice
        /// 默认值为100
        /// </summary>
        public int DefaultDice { get; set; } = 100;

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 每日计数状态（内存缓存，不持久化）
    /// </summary>
    private class DailyRuntimeState
    {
        public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
        public double TrustGainToday { get; set; }
        public int DuelTurnsToday { get; set; }
        /// <summary>
        /// 该用户当日掷骰的 D3 结果缓存。
        /// 仅缓存 D3 值（1-3），回合上限公式在每次调用时重新计算，使其随好感度动态变化。
        /// </summary>
        public int? DuelD6CachedToday { get; set; }
    }

    /// <summary>
    /// 消息分发器
    /// </summary>
    public MessageDistribution? MessageDistribution { get; set; }

    /// <summary>
    /// Mod事件分发网桥 - 负责将消息分发给已启用的Mod
    /// </summary>
    private ModEventBridge? _modEventBridge;

    /// <summary>
    /// TRPG日志管理器
    /// </summary>
    private TRPGLogManager? _trpgLogManager;

    /// <summary>
    /// 公共访问TRPG日志管理器
    /// </summary>
    public TRPGLogManager? TrpgLogManager => _trpgLogManager;

    /// <summary>
    /// 配置同步派发器（gRPC共享基础设施）
    /// </summary>
    private ConfigSyncDispatcher? _configSyncDispatcher;

    /// <summary>
    /// gRPC服务器主机（gRPC共享基础设施）
    /// </summary>
    private GrpcServerHost? _grpcServerHost;

    /// <summary>
    /// 同步配置管理器（gRPC共享基础设施）
    /// </summary>
    private SyncConfigManager? _syncConfigManager;

    /// <summary>
    /// 游戏状态保留天数（超过该天数未活跃的游戏状态在保存时会被清理）
    /// 默认 15 天，可通过基础设置键 "GameStateRetentionDays" 覆盖。
    /// </summary>
    private int gameStateRetentionDays = 15;

    /// <summary>
    /// MainViewModel引用，用于模拟模式回复
    /// </summary>
    public MDiceV2.Core.UI.ViewModels.MainViewModel? MainViewModel { get; set; }

    /// <summary>
    /// 脚本函数执行器委托
    /// 由Mod（如CustomizedReply）设置，用于处理自定义脚本函数调用
    /// 例如：处理 &lt;func&gt; 标签的展开和执行
    /// </summary>
    public ScriptFunctionExecutor? scriptFunctionExecutor { get; set; }

    /// <summary>
    /// 执行回复操作
    /// </summary>

    /// <summary>
    /// 安全格式化字符串：替代 string.Format，对外来参数中的敏感结构进行转义
    /// 转义字符：\x01=<, \x02=>, \x03=[, \x04=], \x05={, \x06=}
    /// 转义在 RefineMsg 最后阶段复原
    /// </summary>
    public static string SafeFormatString(string format, params string[] args)
    {
        if (string.IsNullOrEmpty(format))
            return format ?? string.Empty;

        var escapedArgs = args.Select(a => a?
            .Replace("{", "\x05").Replace("}", "\x06")
            .Replace("<", "\x01").Replace(">", "\x02")
            .Replace("[", "\x03").Replace("]", "\x04")
            ?? string.Empty).ToArray();

        return string.Format(format, escapedArgs);
    }

    /// <summary>
    /// 复原 SafeFormatString 产生的转义字符
    /// </summary>
    private static string RestoreEscapedChars(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        return content
            .Replace("\x01", "<").Replace("\x02", ">")
            .Replace("\x03", "[").Replace("\x04", "]")
            .Replace("\x05", "{").Replace("\x06", "}");
    }

    /// <summary>
    /// 处理文本替换逻辑：搜索被<>包围的文本并进行替换
    /// 新标签语法（贴近左侧<的标签词 + 空格 + 值）：
    /// <name>: 替换为用户显示名
    /// <id>: 替换为用户QQid
    /// <time>: 替换为xx:xx格式的当前时间
    /// <dice 表达式>: 执行掷骰表达式并替换为结果
    /// <deck 键值>: 从默认牌堆中随机选择对应键的列表项
    /// <read key>: 读取Mod全局存储
    /// <write key="a" value="b">: 写入Mod全局存储
    /// <func FunctionName()>: 调用脚本函数
    /// 
    /// 随机选择格式：[选项1||选项2||选项3]
    /// 支持嵌套，程序从最内层开始解析
    /// </summary>
    public string RefineMsg(string content, Msg msg, string modId = "MainProgram")
    {
        if (string.IsNullOrEmpty(content))
            return content;

        // 如果msg.ModId不为null，使用msg.ModId作为modId
        if (!string.IsNullOrEmpty(msg.ModId))
        {
            modId = msg.ModId;
        }

        // Phase 1: 处理[]包围的随机选择（内→外，过滤条件、权重计算）
        content = ProcessBracketSelections(content, msg);

        // Phase 2: 处理<>标签（内→外）
        content = ProcessAngleTagLayers(content, msg, modId);

        // Phase 3: 复原 SafeFormatString 产生的转义字符
        content = RestoreEscapedChars(content);

        return content;
    }

    /// <summary>
    /// 处理<>标签，支持嵌套（内→外逐层处理）
    /// 每次处理最内层的标签，直到没有更多可处理的标签
    /// </summary>
    private string ProcessAngleTagLayers(string content, Msg msg, string modId)
    {
        var deferredWrites = new List<(string Key, string Value)>();
        bool anyProcessed = true;

        while (anyProcessed)
        {
            anyProcessed = false;
            var matches = Regex.Matches(content, @"<([^<>]+)>");
            if (matches.Count == 0) break;

            // 从后向前处理，避免位置偏移
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var match = matches[i];
                string placeholder = match.Groups[1].Value.Trim();
                var (replacement, isWrite, writeKey, writeValue) = ProcessSingleTag(placeholder, msg, modId);

                if (replacement == null)
                {
                    // 未知标签，保持原样
                    continue;
                }

                if (isWrite)
                {
                    deferredWrites.Add((writeKey, writeValue));
                }

                content = content.Remove(match.Index, match.Length).Insert(match.Index, replacement);
                anyProcessed = true;
                break; // 每次只处理一个，重新扫描（因为替换可能产生新标签）
            }
        }

        // 执行延迟的写入操作
        foreach (var (key, value) in deferredWrites)
        {
            SetModStorageValue(modId, key, value);
        }

        return content;
    }

    /// <summary>
    /// 处理单个<>标签内容，返回(replacement, isWrite, writeKey, writeValue)
    /// replacement为null表示未知标签（保持原样）
    /// </summary>
    private (string? replacement, bool isWrite, string writeKey, string writeValue) ProcessSingleTag(string placeholder, Msg msg, string modId)
    {
        // 解析标签词和参数：标签词为第一个空格前的内容，空格后为参数
        string tagWord;
        string tagArg = string.Empty;

        int spaceIndex = placeholder.IndexOf(' ');
        if (spaceIndex > 0)
        {
            tagWord = placeholder.Substring(0, spaceIndex).Trim();
            tagArg = placeholder.Substring(spaceIndex + 1).Trim();
        }
        else
        {
            tagWord = placeholder.Trim();
        }

        string tagWordLower = tagWord.ToLower();

        // <dice 表达式>
        if (tagWordLower == "dice" && !string.IsNullOrEmpty(tagArg))
        {
            try
            {
                var rollResult = Dice.CalculateExpression(tagArg);
                return (rollResult.Success ? rollResult.Total.ToString() : rollResult.Detail, false, "", "");
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 掷骰表达式 '{tagArg}' 执行失败: {ex.Message}");
                return ("[DiceError]", false, "", "");
            }
        }

        // <deck 键值>
        if (tagWordLower == "deck" && !string.IsNullOrEmpty(tagArg))
        {
            if (DeckSet.defaultPublicDeck.TryGetValue(tagArg, out var publicDeck) && publicDeck.Count > 0)
            {
                int randomIndex = GlobalRandom.Next(publicDeck.Count); // 公共牌堆的随机索引（放回抽取）
                string rawCard = publicDeck[randomIndex]; // 牌堆原始内容（可能包含{%...}结构）
                string refinedCard = ExpandDeckPlaceholders(rawCard, msg); // 解析并替换牌堆占位符
                return (refinedCard, false, "", "");
            }
            return ("[DeckError]", false, "", "");
        }

        // <read key>
        if (tagWordLower == "read" && !string.IsNullOrEmpty(tagArg))
        {
            return (GetModStorageValue(modId, tagArg), false, "", "");
        }

        // <write key="a" value="b">
        if (tagWordLower == "write")
        {
            var keyMatch = Regex.Match(tagArg, @"key=""([^""]*)""", RegexOptions.IgnoreCase);
            var valueMatch = Regex.Match(tagArg, @"value=""([^""]*)""", RegexOptions.IgnoreCase);
            if (keyMatch.Success && valueMatch.Success)
            {
                return (string.Empty, true, keyMatch.Groups[1].Value, valueMatch.Groups[1].Value);
            }
            // 兼容旧格式 <write key,value>
            if (!keyMatch.Success && tagArg.Contains(','))
            {
                string[] parts = tagArg.Split(',', 2);
                if (parts.Length >= 2)
                {
                    return (string.Empty, true, parts[0].Trim(), parts[1].Trim());
                }
            }
            return ("[WriteError]", false, "", "");
        }

        // <func FunctionName()>
        if (tagWordLower == "func" && scriptFunctionExecutor != null)
        {
            try
            {
                string result = scriptFunctionExecutor(tagArg, msg);
                return (result, false, "", "");
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 脚本函数执行失败: {ex.Message}");
                return ("[FuncError]", false, "", "");
            }
        }

        // 无参数标签
        switch (tagWordLower)
        {
            case "name":
            {
                string replacement = string.Empty;
                try
                {
                    var cachedName = GetPersistentUserDisplayName(msg.UserId);
                    if (!string.IsNullOrEmpty(cachedName))
                        return (cachedName, false, "", "");
                }
                catch (Exception ex)
                {
                    Log.Error($"[MessageProcessor] 从缓存获取持久化名称失败: {ex.Message}");
                }
                Log.Warn("[MessageProcessor] 未找到持久化名称，尝试获取人物卡名称...");
                try
                {
                    var characterName = CurrentCharacterNames.TryGetValue(msg.UserId, out var name) ? name : null;
                    if (!string.IsNullOrEmpty(characterName))
                        return (characterName, false, "", "");
                }
                catch (Exception ex)
                {
                    Log.Error($"[MessageProcessor] 获取人物卡名称用于<name>时异常: {ex.Message}");
                    return (msg.UserId > 0 ? msg.UserId.ToString() : "[NameError]", false, "", "");
                }
                Log.Warn("[MessageProcessor] 未找到人物卡名称，尝试获取QQ昵称...");
                var nickname = GetUserNickname(msg.UserId, msg.IsSimulationMode);
                if (!string.IsNullOrEmpty(nickname))
                    return (nickname, false, "", "");
                return (msg.UserId > 0 ? msg.UserId.ToString() : "[IdError]", false, "", "");
            }

            case "id":
                return (msg.UserId > 0 ? msg.UserId.ToString() : "[IdError]", false, "", "");

            case "time":
                return (DateTime.Now.ToString("HH:mm"), false, "", "");

            default:
                // 未知标签，返回null表示保持原样
                return (null, false, "", "");
        }
    }

    /// <summary>
    /// 解析并替换牌堆占位符，支持 {牌堆名} 放回抽取 与 {%牌堆名} 不放回抽取（仅本次解析）
    /// </summary>
    private string ExpandDeckPlaceholders(string content, Msg msg)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var oneShotDecks = new Dictionary<string, List<string>>(); // 单次解析用的临时牌堆副本
        GroupDataRecord? groupRecord = null; // 当前群的数据记录
        groupDataRecords.TryGetValue(msg.GroupId, out groupRecord);

        int loopCount = 0; // 防止无限嵌套的循环计数器
        while (loopCount < 50)
        {
            var match = Regex.Match(content, @"\{(%?)([^{}]+)\}"); // 匹配 {牌堆名} 或 {%牌堆名}
            if (!match.Success) break;

            bool useOneShot = match.Groups[1].Value == "%"; // 是否使用不放回抽取
            string subDeckName = match.Groups[2].Value; // 被引用的子牌堆名称
            string subCard = string.Empty; // 子牌堆抽取结果文本
            List<string>? sourceDeck = null; // 子牌堆的来源列表

            if (groupRecord?.TemporaryDecks != null
                && groupRecord.TemporaryDecks.TryGetValue(subDeckName, out var subTempDeck)
                && subTempDeck.Count > 0)
            {
                sourceDeck = subTempDeck; // 优先使用当前群临时牌堆
            }
            else if (DeckSet.defaultPublicDeck.TryGetValue(subDeckName, out var subPubDeck) && subPubDeck.Count > 0)
            {
                sourceDeck = subPubDeck; // 回退到公共牌堆
            }

            if (sourceDeck == null)
            {
                subCard = $"[空或未知:{subDeckName}]"; // 牌堆为空或不存在时的占位文本
            }
            else if (!useOneShot)
            {
                subCard = sourceDeck[GlobalRandom.Next(sourceDeck.Count)]; // 放回抽取
            }
            else
            {
                if (!oneShotDecks.TryGetValue(subDeckName, out var tempDeck))
                {
                    tempDeck = new List<string>(sourceDeck); // 创建一次性临时牌堆副本
                    oneShotDecks[subDeckName] = tempDeck;
                }

                if (tempDeck.Count == 0)
                {
                    subCard = $"[空或未知:{subDeckName}]"; // 一次性牌堆耗尽时提示
                }
                else
                {
                    int pickIndex = GlobalRandom.Next(tempDeck.Count); // 一次性牌堆随机索引
                    subCard = tempDeck[pickIndex]; // 不放回抽取结果
                    tempDeck.RemoveAt(pickIndex); // 移除已抽取的牌
                }
            }

            var regex = new Regex(Regex.Escape(match.Value)); // 仅替换当前匹配到的占位符
            content = regex.Replace(content, subCard, 1); // 只替换一次以确保多次独立抽取
            loopCount++;
        }

        return content;
    }

    /// <summary>
    /// 处理随机选择：解析[]包围的文本，按条件过滤后进行加权随机选择
    /// 
    /// 格式说明：
    /// - 基础格式：[选项1<weight 2>||选项2||选项3<weight 3>]
    /// - 条件过滤：[选项1<id 111>||选项2<id 222>||选项3]
    /// - 组合格式：[选项1<id 111><weight 2>||选项2<id 222>||选项3<weight 3>]
    /// 
    /// 处理流程（内→外）：
    /// 1. 找到最内层不含嵌套[]的[...]块
    /// 2. 检查是否包含||，若不包含则保留原样
    /// 3. 解析选项和条件标签，过滤后加权随机选择
    /// 4. 循环直到没有更多可处理的块
    /// </summary>
    private string ProcessBracketSelections(string content, Msg msg)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        bool anyProcessed = true;
        while (anyProcessed)
        {
            anyProcessed = false;
            // 匹配最内层的[]：内部不含其他[]
            var matches = Regex.Matches(content, @"\[([^\[\]]+)\]");

            foreach (Match match in matches)
            {
                string optionsText = match.Groups[1].Value;

                // 检查是否包含||，不包含则不是有效的多选一结构，保留原样
                if (!optionsText.Contains("||"))
                    continue;

                var options = optionsText.Split(new string[] { "||" }, StringSplitOptions.None);

                // 第一阶段：解析、过滤和计算权重
                var filteredOptions = new List<(string Text, int Weight)>();

                foreach (var option in options)
                {
                    string trimmedOption = option.Trim();
                    
                    // 解析条件标签（如<id 111>、<group 123>等）
                    if (!CheckAllFilterConditions(trimmedOption, msg))
                    {
                        continue;
                    }

                    // 条件满足，提取权重和清理文本
                    var weightMatch = Regex.Match(trimmedOption, @"<weight\s+(\d+)>");
                    int weight = 1;
                    string cleanText = trimmedOption;

                    if (weightMatch.Success)
                    {
                        if (int.TryParse(weightMatch.Groups[1].Value, out int parsedWeight) && parsedWeight > 0)
                        {
                            weight = parsedWeight;
                        }
                        cleanText = Regex.Replace(trimmedOption, @"<weight\s+\d+>", "").Trim();
                    }

                    // 移除所有条件标签（<id ...>、<group ...>等）
                    cleanText = Regex.Replace(cleanText, @"<(id|group|time|level)\s+[^>]*>", "").Trim();

                    filteredOptions.Add((cleanText, weight));
                }

                // 第二阶段：加权随机选择（如果还有剩余选项）
                if (filteredOptions.Count > 0)
                {
                    int totalWeight = filteredOptions.Sum(opt => opt.Weight);
                    
                    if (totalWeight > 0)
                    {
                        int randomValue = GlobalRandom.Next(totalWeight);
                        int cumulativeWeight = 0;

                        foreach (var (text, weight) in filteredOptions)
                        {
                            cumulativeWeight += weight;
                            if (randomValue < cumulativeWeight)
                            {
                                content = content.Replace(match.Value, text);
                                anyProcessed = true;
                                break;
                            }
                        }
                    }
                }
                else
                {
                    // 所有选项都被过滤，替换为空
                    content = content.Replace(match.Value, "");
                    anyProcessed = true;
                }

                // 每次只处理一个最内层块，然后重新扫描
                if (anyProcessed)
                    break;
            }
        }

        return content;
    }

    /// <summary>
    /// 检查选项中的所有过滤条件是否都满足（AND逻辑）
    /// 支持新语法：<id 111>、<group 123> 和旧语法 <id:111>、<group:123>
    /// </summary>
    private bool CheckAllFilterConditions(string optionText, Msg msg)
    {
        // 检查<id ...>条件（用户ID限制）- 新语法
        var idMatch = Regex.Match(optionText, @"<id\s+([^>]+)>");
        if (idMatch.Success)
        {
            if (!CheckIdCondition(msg.UserId, idMatch.Groups[1].Value))
            {
                return false;
            }
        }
        // 兼容旧语法 <id:...>
        else
        {
            var idMatchOld = Regex.Match(optionText, @"<id:([^>]+)>");
            if (idMatchOld.Success)
            {
                if (!CheckIdCondition(msg.UserId, idMatchOld.Groups[1].Value))
                {
                    return false;
                }
            }
        }

        // 检查<group ...>条件（群号限制）- 新语法
        var groupMatch = Regex.Match(optionText, @"<group\s+([^>]+)>");
        if (groupMatch.Success)
        {
            if (!CheckGroupCondition(msg.GroupId, groupMatch.Groups[1].Value))
            {
                return false;
            }
        }
        // 兼容旧语法 <group:...>
        else
        {
            var groupMatchOld = Regex.Match(optionText, @"<group:([^>]+)>");
            if (groupMatchOld.Success)
            {
                if (!CheckGroupCondition(msg.GroupId, groupMatchOld.Groups[1].Value))
                {
                    return false;
                }
            }
        }

        // 可在此扩展其他条件类型（如<level ...>、<time ...>等）

        return true;
    }

    /// <summary>
    /// 检查用户ID条件
    /// 格式支持：<id:111> 或 <id:111,222,333>（表示OR）
    /// </summary>
    private bool CheckIdCondition(long userId, string idPattern)
    {
        if (string.IsNullOrWhiteSpace(idPattern))
            return true;

        var ids = idPattern.Split(',');
        foreach (var idStr in ids)
        {
            if (long.TryParse(idStr.Trim(), out long id) && id == userId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 检查群ID条件
    /// 格式支持：<group:123> 或 <group:123,456,789>（表示OR）
    /// </summary>
    private bool CheckGroupCondition(long groupId, string groupPattern)
    {
        if (string.IsNullOrWhiteSpace(groupPattern))
            return true;

        var groups = groupPattern.Split(',');
        foreach (var groupStr in groups)
        {
            if (long.TryParse(groupStr.Trim(), out long gid) && gid == groupId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取/刷新指定用户的当日计数状态（好感度当日增量、当日 duel 回合数）。
    /// </summary>
    private DailyRuntimeState GetDailyRuntimeState(long userId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return dailyRuntimeStates.AddOrUpdate(userId,
            _ =>
            {
                Log.InfoFormat("[Duel] GetDailyRuntimeState 创建新用户 {0} 的状态（日期: {1}）", userId, today);
                return new DailyRuntimeState { Date = today };
            },
            (_, existing) =>
            {
                if (existing.Date != today)
                {
                    Log.InfoFormat("[Duel] GetDailyRuntimeState 用户 {0} 日期变更（{1} -> {2}），重置 DuelTurnsToday", userId, existing.Date, today);
                    existing.Date = today;
                    existing.TrustGainToday = 0;
                    existing.DuelTurnsToday = 0;
                    existing.DuelD6CachedToday = null;  // 新的一天，清空D6缓存
                }
                else
                {
                    Log.InfoFormat("[Duel] GetDailyRuntimeState 用户 {0} 日期未变更，DuelTurnsToday: {1}", userId, existing.DuelTurnsToday);
                }
                return existing;
            });
    }

    /// <summary>
    /// 增加一次正常指令的好感度，受每日上限限制。
    /// </summary>
    private void AddTrustForNormalUse(long userId)
    {
        if (userId <= 0) return;

        var runtime = GetDailyRuntimeState(userId);
        double available = TrustDailyGainLimit - runtime.TrustGainToday;
        if (available <= 0) return;

        double delta = Math.Min(TrustNormalIncrement, available);
        userTrust.AddOrUpdate(userId, delta, (_, current) => current + delta);
        runtime.TrustGainToday += delta;
    }

    /// <summary>
    /// 娱乐功能（duel）扣减好感度，不受上限限制。
    /// </summary>
    private void ApplyDuelPenalty(long userId)
    {
        if (userId <= 0) return;
        userTrust.AddOrUpdate(userId, -TrustDuelPenalty, (_, current) => current - TrustDuelPenalty);
    }

    /// <summary>
    /// 是否为白名单用户（authLevel=0表示白名单/完全授权）。
    /// </summary>
    private bool IsUserWhitelisted(long userId)
    {
        if (userId <= 0) return false;
        // 检查用户授权等级（已合并到UserDataRecord）
        if (personAuth.TryGetValue(userId, out var authLevel) && authLevel == 0)
        {
            return true; // authLevel=0表示在白名单内（完全授权）
        }
        return false; // 默认不在白名单
    }

    /// <summary>
    /// 获取用户的权限等级（公开接口，供 Mod 查询）
    /// </summary>
    /// <param name="userId">用户QQ号</param>
    /// <returns>
    /// - null：用户未设置权限等级（使用默认权限）
    /// - 0：用户在白名单中（完全授权）
    /// - 1-9：逐级降低的权限等级
    /// </returns>
    public int? GetUserAuthLevel(long userId)
    {
        if (userId <= 0) return null;
        if (personAuth.TryGetValue(userId, out var authLevel))
        {
            return authLevel;
        }
        return null;
    }

    /// <summary>
    /// 计算用户当日可用的 duel 回合数。
    /// <summary>
    /// 计算用户当日可用的 duel 回合数（带缓存）。
    /// 公式：D3 + lg(|好感度|) - 1，最小为 0
    /// 对于 -1 ~ 1 的好感度，统一视为 0（跳过计算）
    /// 对于 < -1 的好感度，使用 -lg(|好感度|)（负数对数为负）
    /// 注：仅缓存 D3 值，好感度变化时上限会动态变化
    /// </summary>
    private int GetDuelDailyTurnLimit(long userId)
    {
        var runtime = GetDailyRuntimeState(userId);
        
        // 获取缓存的 D6 值，如果不存在则掷骰并缓存
        int _2d6Roll;
        if (runtime.DuelD6CachedToday.HasValue)
        {
            _2d6Roll = runtime.DuelD6CachedToday.Value;
        }
        else
        {
            // 首次调用时掷 D6（1-6）并缓存
            _2d6Roll = GlobalRandom.Next(1, 7)+GlobalRandom.Next(1, 7); // 2D6
            runtime.DuelD6CachedToday = _2d6Roll;
        }
        
        // 获取用户好感度
        var userTrustValue = userTrust.TryGetValue(userId, out var trust) ? trust : 0;
        
        // 计算对数，处理不同好感度范围
        double logTrust;
        if (userTrustValue >= -1 && userTrustValue <= 1)
        {
            // -1 ~ 1 之间统一视为 0，跳过计算
            logTrust = 0;
        }
        else if (userTrustValue < -1)
        {
            // < -1 的情况：使用 -lg(|好感度|)，结果为负数
            logTrust = -Math.Log10(Math.Abs(userTrustValue));
        }
        else
        {
            // > 1 的情况：使用 lg(好感度)
            logTrust = Math.Log10(userTrustValue);
        }
        
        // 计算最终值：2D6 - 2 + 3×lg(好感度)，最小为 0
        int limit = Math.Max(0, (int)(_2d6Roll - 2 + 3 * logTrust));
        
        return limit;
    }

    /// <summary>
    /// 检查当日 duel 回合是否超限。
    /// </summary>
    private bool IsDuelTurnLimited(long userId)
    {
        var duelLimit = GetDuelDailyTurnLimit(userId);
        var runtime = GetDailyRuntimeState(userId);
        bool limited = runtime.DuelTurnsToday >= duelLimit;
        Log.InfoFormat("[Duel] IsDuelTurnLimited 用户 {0}: DuelTurnsToday={1}, duelLimit={2}, limited={3}", userId, runtime.DuelTurnsToday, duelLimit, limited);
        return limited;
    }

    /// <summary>
    /// 获取用户当日剩余的 duel 回合数。
    /// </summary>
    private int GetDuelTurnsRemaining(long userId)
    {
        var duelLimit = GetDuelDailyTurnLimit(userId);
        var runtime = GetDailyRuntimeState(userId);
        return Math.Max(0, duelLimit - runtime.DuelTurnsToday);
    }

    /// <summary>
    /// 记录一次 duel 回合。
    /// </summary>
    private void IncrementDuelTurn(long userId)
    {
        var runtime = GetDailyRuntimeState(userId);
        runtime.DuelTurnsToday++;
        Log.InfoFormat("[Duel] IncrementDuelTurn 用户 {0}: DuelTurnsToday 增加为 {1}", userId, runtime.DuelTurnsToday);

        // 检查是否已经达到或超过每日回合限制，如果达到则立即强制终止游戏
        var duelLimit = GetDuelDailyTurnLimit(userId);
        if (runtime.DuelTurnsToday >= duelLimit)
        {
            Log.InfoFormat("[Duel] 用户 {0} 达到回合限制（已用: {1}, 限制: {2}），立即强制终止游戏", userId, runtime.DuelTurnsToday, duelLimit);
            
            // 查找并强制终止游戏
            string userIdStr = userId.ToString();
            var gameState = LoadUserGameState(userIdStr);
            if (gameState != null)
            {
                gameState.IsGameOver = true;
                Log.InfoFormat("[Duel] 游戏已标记为结束");
            }
        }
    }

    /// <summary>
    /// 公共方法：记录一次 duel 回合（用于外部调用，如卡牌决策处理）。
    /// </summary>
    public void RecordDuelTurn(long userId)
    {
        IncrementDuelTurn(userId);
    }

    /// <summary>
    /// 生成详细的 duel 回合限制信息（用于显示计算过程）。
    /// </summary>
    private string GetDuelLimitDetailedInfo(long userId)
    {
        var userTrustValue = userTrust.TryGetValue(userId, out var trust) ? trust : 0;
        var runtime = GetDailyRuntimeState(userId);
        var duelLimit = GetDuelDailyTurnLimit(userId);
        var turnsRemaining = GetDuelTurnsRemaining(userId);
        
        Log.InfoFormat("[Duel] GetDuelLimitDetailedInfo 用户 {0}: runtime.DuelTurnsToday={1}, duelLimit={2}", userId, runtime.DuelTurnsToday, duelLimit);
        
        // 计算对数部分（用于展示）
        double logTrust;
        string logDescription;
        if (userTrustValue >= -1 && userTrustValue <= 1)
        {
            logTrust = 0;
            logDescription = "（范围 -1~1，统一为 0）";
        }
        else if (userTrustValue < -1)
        {
            logTrust = -Math.Log10(Math.Abs(userTrustValue));
            logDescription = $"（负数对数: -lg|{userTrustValue:F1}| = {logTrust:F2}）";
        }
        else
        {
            logTrust = Math.Log10(userTrustValue);
            logDescription = $"（正数对数: lg({userTrustValue:F1}) = {logTrust:F2}）";
        }
        
        // 构建详细信息
        var info = new System.Text.StringBuilder();
        info.AppendLine($"每日上限: 2D6 - 2 + 3×lg(好感) = {duelLimit}");
        info.Append($"已使用: {runtime.DuelTurnsToday}/{duelLimit}");
        return info.ToString();
    }

    /// <summary>
    /// 读取用户白名单。
    /// 白名单已合并到UserDataRecord中，通过personAuth（AuthLevel=0）表示白名单用户。
    /// </summary>
    private void LoadWhitelist()
    {
        // 白名单数据已在LoadUserData中加载（通过personAuth）
        Log.InfoFormat("[MessageProcessor] 白名单数据已集成到UserData加载流程中");
    }

    /// <summary>
    /// 保存用户白名单。
    /// 白名单已合并到UserDataRecord中，通过personAuth（AuthLevel=0）保存。
    /// </summary>
    private void SaveWhitelist()
    {
        // 白名单数据已在SaveUserData中保存（通过personAuth）
        Log.InfoFormat("[MessageProcessor] 白名单已集成到UserData保存流程中");
    }

    /// <summary>
    /// 加载Mod全局变量存储
    /// 每个Mod可独立维护一个Key-Value字典，通过RefineMsg中的<read:>和<write:>操作来访问
    /// </summary>
    private void LoadModStorages()
    {
        if (DataIO == null)
        {
            Log.Warn("[MessageProcessor] DataIO 未初始化，跳过加载 ModStorages。");
            return;
        }

        modStorages.Clear();

        var allStorages = DataIO.ReadAllData("ModStorages");
        foreach (var kvp in allStorages)
        {
            try
            {
                // kvp.Key是ModId，kvp.Value是JSON格式的字典
                var storage = JsonSerializer.Deserialize<Dictionary<string, string>>(kvp.Value);
                if (storage != null)
                {
                    modStorages[kvp.Key] = storage;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 加载ModStorage[{kvp.Key}]失败: {ex.Message}");
            }
        }

        Log.InfoFormat("[MessageProcessor] 已加载Mod全局存储，共 {0} 个Mod", modStorages.Count);
    }

    /// <summary>
    /// 保存Mod全局变量存储
    /// </summary>
    private void SaveModStorages()
    {
        if (DataIO == null)
        {
            Log.Warn("[MessageProcessor] DataIO 未初始化，跳过保存 ModStorages。");
            return;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        int count = 0;
        foreach (var kvp in modStorages)
        {
            try
            {
                string json = JsonSerializer.Serialize(kvp.Value, jsonOptions);
                DataIO.SaveData("ModStorages", kvp.Key, json);
                count++;
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 保存ModStorage[{kvp.Key}]失败: {ex.Message}");
            }
        }

        Log.InfoFormat("[MessageProcessor] 已保存 {0} 个Mod全局存储。", count);
    }

    /// <summary>
    /// 加载每日限额追踪数据
    /// </summary>
    private void LoadDailyLimitTracking()
    {
        if (DataIO == null)
        {
            Log.Warn("[MessageProcessor] DataIO 未初始化，跳过加载 DailyLimitTracking。");
            return;
        }

        dailyLimitTracking.Clear();

        var allData = DataIO.ReadAllData("DailyLimitTracking");
        foreach (var kvp in allData)
        {
            try
            {
                // kvp.Value 格式: "2024-02-16,5" (日期,计数)
                var parts = kvp.Value.Split(',');
                if (parts.Length >= 2 &&
                    DateOnly.TryParse(parts[0], out var date) &&
                    int.TryParse(parts[1], out var count))
                {
                    dailyLimitTracking[kvp.Key] = (date, count);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 加载DailyLimitTracking[{kvp.Key}]失败: {ex.Message}");
            }
        }

        Log.InfoFormat("[MessageProcessor] 已加载每日限额追踪，共 {0} 条记录", dailyLimitTracking.Count);
    }

    /// <summary>
    /// 保存每日限额追踪数据
    /// </summary>
    private void SaveDailyLimitTracking()
    {
        if (DataIO == null)
        {
            Log.Warn("[MessageProcessor] DataIO 未初始化，跳过保存 DailyLimitTracking。");
            return;
        }

        int count = 0;
        foreach (var kvp in dailyLimitTracking)
        {
            try
            {
                string data = $"{kvp.Value.Date:yyyy-MM-dd},{kvp.Value.Count}";
                DataIO.SaveData("DailyLimitTracking", kvp.Key, data);
                count++;
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 保存DailyLimitTracking[{kvp.Key}]失败: {ex.Message}");
            }
        }

        Log.InfoFormat("[MessageProcessor] 已保存 {0} 条每日限额追踪记录。", count);
    }

    /// <summary>
    /// 加载冷却时间追踪数据
    /// </summary>
    private void LoadCooldownTracking()
    {
        if (DataIO == null)
        {
            Log.Warn("[MessageProcessor] DataIO 未初始化，跳过加载 CooldownTracking。");
            return;
        }

        cooldownTracking.Clear();

        var allData = DataIO.ReadAllData("CooldownTracking");
        foreach (var kvp in allData)
        {
            try
            {
                // kvp.Value 格式: "2024-02-16T10:30:45,300" (时间戳,冷却秒数)
                var parts = kvp.Value.Split(',');
                if (parts.Length >= 2 &&
                    DateTime.TryParse(parts[0], out var lastTrigger) &&
                    int.TryParse(parts[1], out var cooldownSeconds))
                {
                    cooldownTracking[kvp.Key] = (lastTrigger, cooldownSeconds);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 加载CooldownTracking[{kvp.Key}]失败: {ex.Message}");
            }
        }

        Log.InfoFormat("[MessageProcessor] 已加载冷却时间追踪，共 {0} 条记录", cooldownTracking.Count);
    }

    /// <summary>
    /// 保存冷却时间追踪数据
    /// </summary>
    private void SaveCooldownTracking()
    {
        if (DataIO == null)
        {
            Log.Warn("[MessageProcessor] DataIO 未初始化，跳过保存 CooldownTracking。");
            return;
        }

        int count = 0;
        foreach (var kvp in cooldownTracking)
        {
            try
            {
                string data = $"{kvp.Value.LastTrigger:O},{kvp.Value.CooldownSeconds}";
                DataIO.SaveData("CooldownTracking", kvp.Key, data);
                count++;
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 保存CooldownTracking[{kvp.Key}]失败: {ex.Message}");
            }
        }

        Log.InfoFormat("[MessageProcessor] 已保存 {0} 条冷却时间追踪记录。", count);
    }

    /// <summary>
    /// 从指定Mod的全局存储中读取值
    /// 用于RefineMsg中的<read:key>操作  
    /// </summary>
    /// <param name="modId">Mod的ID</param>
    /// <param name="key">存储的键</param>
    /// <returns>存储的值，如果不存在返回空字符串</returns>
    public string GetModStorageValue(string modId, string key)
    {
        if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(key))
            return string.Empty;

        if (modStorages.TryGetValue(modId, out var storage))
        {
            if (storage.TryGetValue(key, out var value))
            {
                return value ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 向指定Mod的全局存储中写入值
    /// 用于RefineMsg中的<write:key,value>操作
    /// </summary>
    /// <param name="modId">Mod的ID</param>
    /// <param name="key">存储的键</param>
    /// <param name="value">存储的值</param>
    public void SetModStorageValue(string modId, string key, string value)
    {
        if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(key))
            return;

        // 如果Mod的存储不存在，创建一个新的
        if (!modStorages.TryGetValue(modId, out var storage))
        {
            storage = new Dictionary<string, string>();
            modStorages[modId] = storage;
        }

        // 设置或更新值
        storage[key] = value ?? string.Empty;
    }

    /// <summary>
    /// 检查每日限额是否未超限
    /// </summary>
    /// <param name="ruleId">规则ID</param>
    /// <param name="userId">用户ID（如果为0表示全局模式不需要此参数）</param>
    /// <param name="scope">作用域："按用户" 或 "全局"</param>
    /// <param name="limitCount">每日限额数</param>
    /// <returns>true 表示未超限，false 表示已超限</returns>
    public bool CheckDailyLimit(string ruleId, long userId, string scope, int limitCount)
    {
        if (string.IsNullOrEmpty(ruleId) || limitCount <= 0)
            return true;

        string key = scope == "全局" ? $"{ruleId}_*" : $"{ruleId}_{userId}";
        
        if (dailyLimitTracking.TryGetValue(key, out var tracking))
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            
            // 如果日期不同，重置计数
            if (tracking.Date != today)
            {
                dailyLimitTracking[key] = (today, 0);
                return true; // 新的一天，未超限
            }
            
            // 检查是否超限
            return tracking.Count < limitCount;
        }

        return true; // 首次触发，未超限
    }

    /// <summary>
    /// 递增每日计数器
    /// </summary>
    /// <param name="ruleId">规则ID</param>
    /// <param name="userId">用户ID</param>
    /// <param name="scope">作用域："按用户" 或 "全局"</param>
    public void IncrementDailyCount(string ruleId, long userId, string scope)
    {
        if (string.IsNullOrEmpty(ruleId))
            return;

        string key = scope == "全局" ? $"{ruleId}_*" : $"{ruleId}_{userId}";
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        dailyLimitTracking.AddOrUpdate(key,
            (today, 1),
            (_, existing) =>
            {
                if (existing.Date != today)
                {
                    return (today, 1); // 新的一天，重置计数
                }
                return (today, existing.Count + 1);
            });
    }

    /// <summary>
    /// 检查冷却时间是否已过期
    /// </summary>
    /// <param name="ruleId">规则ID</param>
    /// <param name="userId">用户ID</param>
    /// <param name="scope">作用域："按用户" 或 "全局"</param>
    /// <param name="cooldownSeconds">冷却时长（秒）</param>
    /// <returns>true 表示冷却时间已过，false 表示仍在冷却中</returns>
    public bool CheckCooldown(string ruleId, long userId, string scope, int cooldownSeconds)
    {
        if (string.IsNullOrEmpty(ruleId) || cooldownSeconds <= 0)
            return true;

        string key = scope == "全局" ? $"{ruleId}_*" : $"{ruleId}_{userId}";

        if (cooldownTracking.TryGetValue(key, out var tracking))
        {
            var elapsed = DateTime.UtcNow - tracking.LastTrigger;
            return elapsed.TotalSeconds >= tracking.CooldownSeconds;
        }

        return true; // 首次触发，冷却时间已过
    }

    /// <summary>
    /// 更新冷却时间戳
    /// </summary>
    /// <param name="ruleId">规则ID</param>
    /// <param name="userId">用户ID</param>
    /// <param name="scope">作用域："按用户" 或 "全局"</param>
    /// <param name="cooldownSeconds">冷却时长（秒）</param>
    public void UpdateCooldownTimestamp(string ruleId, long userId, string scope, int cooldownSeconds)
    {
        if (string.IsNullOrEmpty(ruleId) || cooldownSeconds <= 0)
            return;

        string key = scope == "全局" ? $"{ruleId}_*" : $"{ruleId}_{userId}";
        cooldownTracking[key] = (DateTime.UtcNow, cooldownSeconds);
    }

    /// <summary>
    /// 读取群数据（Bot启用状态和群白名单）。
    /// </summary>
    private void LoadGroupData()
    {
        if (DataIO == null)
        {
            Log.Warn("[MessageProcessor] DataIO 未初始化，跳过加载 GroupData。");
            return;
        }

        groupDataRecords.Clear();
        groupAuth.Clear();

        var data = DataIO.ReadAllData("GroupData");
        foreach (var kvp in data)
        {
            if (!long.TryParse(kvp.Key, out var groupId))
                continue;

            try
            {
                var record = JsonSerializer.Deserialize<GroupDataRecord>(kvp.Value);
                if (record == null) continue;

                record.GroupId = groupId;
                groupDataRecords[groupId] = record;

                // 同步到 groupAuth 用于高频校验（如果有授权等级）
                if (record.AuthLevel.HasValue)
                {
                    groupAuth[(int)groupId] = (byte)record.AuthLevel.Value;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 解析 GroupData[{kvp.Key}] 失败: {ex.Message}");
            }
        }

        Log.InfoFormat("[MessageProcessor] 已加载群数据，当前记录数: {0}", groupDataRecords.Count);
    }

    /// <summary>
    /// 保存群数据（Bot启用状态和群白名单）。
    /// </summary>
    private void SaveGroupData(long? targetGroupId = null)
    {
        if (DataIO == null)
        {
            Log.Warn("[MessageProcessor] DataIO 未初始化，跳过保存 GroupData。");
            return;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        IEnumerable<long> groupIds;
        if (targetGroupId.HasValue)
        {
            groupIds = new[] { targetGroupId.Value };
        }
        else
        {
            groupIds = groupDataRecords.Keys;
        }

        int count = 0;
        foreach (var groupId in groupIds)
        {
            if (!groupDataRecords.TryGetValue(groupId, out var record))
            {
                // 如果没有记录，创建一个默认的
                record = new GroupDataRecord
                {
                    GroupId = groupId,
                    BotEnabled = true
                };
            }

            record.UpdatedAt = DateTime.UtcNow;
            string json = JsonSerializer.Serialize(record, jsonOptions);
            DataIO.SaveData("GroupData", groupId.ToString(), json);
            count++;
        }

        Log.InfoFormat("[MessageProcessor] 已保存 {0} 条 GroupData 记录。", count);
    }

    private string? GetDefaultCheckMode(long userId)
    {
        if (defaultCheckModes.TryGetValue(userId, out var mode) && IsSupportedCheckMode(mode))
        {
            return mode;
        }

        return null;
    }

    private bool TrySetDefaultCheckMode(long userId, string mode)
    {
        var normalized = mode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || !IsSupportedCheckMode(normalized))
        {
            return false;
        }

        defaultCheckModes[userId] = normalized;
        return true;
    }

    private bool IsSupportedCheckMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return false;
        }

        return supportedCheckModes.Contains(mode);
    }

    /// <summary>
    /// 读取用户档案（显示名、人物卡、好感度、授权等级、默认检定模式）。
    /// </summary>
    private void LoadUserData()
    {
        if (DataIO == null)
        {
            Log.Warn("[MessageProcessor] DataIO 未初始化，跳过加载 UserData。");
            return;
        }

        userDisplayNames.Clear();
        characterSkills.Clear();
        userTrust.Clear();
        personAuth.Clear();
        defaultCheckModes.Clear();
        cardNameTemplates.Clear();
        cardNameSwitches.Clear();

        var all = DataIO.ReadAllData("UserData");
        foreach (var kvp in all)
        {
            if (!long.TryParse(kvp.Key, out var userId))
                continue;

            try
            {
                var record = JsonSerializer.Deserialize<UserDataRecord>(kvp.Value);
                if (record == null) continue;

                // 加载显示名
                if (!string.IsNullOrWhiteSpace(record.DisplayName))
                {
                    userDisplayNames[userId] = record.DisplayName.Trim();
                }

                // 加载人物卡
                if (record.CharacterSheets != null)
                {
                    var cc = new ConcurrentDictionary<string, CharacterSheet>();
                    foreach (var sheetEntry in record.CharacterSheets)
                    {
                        var sheet = sheetEntry.Value;
                        if (sheet == null) continue;
                        sheet.Skills ??= new ConcurrentDictionary<string, int>();
                        cc[sheetEntry.Key] = sheet;
                    }
                    characterSkills[userId] = cc;
                }

                // 加载好感度
                userTrust[userId] = record.Trust;

                // 加载授权等级
                if (record.AuthLevel.HasValue)
                {
                    personAuth[userId] = (byte)record.AuthLevel.Value;
                }

                // 加载默认检定模式
                if (!string.IsNullOrWhiteSpace(record.DefaultCheckMode))
                {
                    var mode = record.DefaultCheckMode.Trim().ToLowerInvariant();
                    if (IsSupportedCheckMode(mode))
                    {
                        defaultCheckModes[userId] = mode;
                    }
                }

                // 加载自定义指令
                if (record.CustomCommands != null && record.CustomCommands.Count > 0)
                {
                    userCustomCommands[userId] = new Dictionary<string, string>(record.CustomCommands);
                }

                // 加载仿名片模板
                if (!string.IsNullOrWhiteSpace(record.CardNameTemplate))
                {
                    cardNameTemplates[userId] = record.CardNameTemplate.Trim();
                }

                // 加载仿名片开关
                if (record.CardNameSwitches != null && record.CardNameSwitches.Count > 0)
                {
                    foreach (var switchEntry in record.CardNameSwitches)
                    {
                        cardNameSwitches[switchEntry.Key] = switchEntry.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 解析 UserData[{kvp.Key}] 失败: {ex.Message}");
            }
        }

        //Log.InfoFormat("[MessageProcessor] 已加载 UserData：显示名 {0} 条，人物卡 {1} 条，好感度 {2} 条，授权等级 {3} 条，默认检定模式 {4} 条，自定义指令 {5} 条。", 
        //    userDisplayNames.Count, characterSkills.Count, userTrust.Count, personAuth.Count, defaultCheckModes.Count, userCustomCommands.Count);
    }

    /// <summary>
    /// 保存用户档案（显示名、人物卡、好感度、授权等级、默认检定模式）。
    /// </summary>
    private void SaveUserData(long? targetUserId = null)
    {
        if (DataIO == null)
        {
            Log.Warn("[MessageProcessor] DataIO 未初始化，跳过保存 UserData。");
            return;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        IEnumerable<long> userIds;
        if (targetUserId.HasValue)
        {
            userIds = new[] { targetUserId.Value };
        }
        else
        {
            var set = new HashSet<long>(userDisplayNames.Keys);
            foreach (var id in characterSkills.Keys) set.Add(id);
            foreach (var id in userTrust.Keys) set.Add(id);
            foreach (var id in personAuth.Keys) set.Add(id);
            foreach (var id in defaultCheckModes.Keys) set.Add(id);
            foreach (var id in userCustomCommands.Keys) set.Add(id);
            foreach (var id in cardNameTemplates.Keys) set.Add(id);
            // 从 cardNameSwitches 中提取所有用户ID（格式："UserId_GroupId"）
            foreach (var switchKey in cardNameSwitches.Keys)
            {
                var parts = switchKey.Split('_');
                if (parts.Length == 2 && long.TryParse(parts[0], out var userId))
                {
                    set.Add(userId);
                }
            }
            userIds = set;
        }

        int count = 0;
        foreach (var userId in userIds)
        {
            userDisplayNames.TryGetValue(userId, out var name);
            characterSkills.TryGetValue(userId, out var sheets);
            userTrust.TryGetValue(userId, out var trustValue);
            bool hasAuthLevel = personAuth.TryGetValue(userId, out var authLevel);
            defaultCheckModes.TryGetValue(userId, out var defaultMode);
            userCustomCommands.TryGetValue(userId, out var customCommands);
            cardNameTemplates.TryGetValue(userId, out var cardNameTemplate);

            // 收集该用户的所有仿名片开关
            var userCardNameSwitches = new Dictionary<string, bool>();
            foreach (var switchEntry in cardNameSwitches)
            {
                var parts = switchEntry.Key.Split('_');
                if (parts.Length == 2 && long.TryParse(parts[0], out var switchUserId) && switchUserId == userId)
                {
                    userCardNameSwitches[switchEntry.Key] = switchEntry.Value;
                }
            }

            var record = new UserDataRecord
            {
                UserId = userId,
                DisplayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                CharacterSheets = sheets?.ToDictionary(k => k.Key, v => v.Value),
                Trust = trustValue,
                AuthLevel = hasAuthLevel ? (int?)authLevel : null,
                DefaultCheckMode = string.IsNullOrWhiteSpace(defaultMode) ? null : defaultMode,
                CustomCommands = customCommands != null && customCommands.Count > 0 ? new Dictionary<string, string>(customCommands) : null,
                CardNameTemplate = string.IsNullOrWhiteSpace(cardNameTemplate) ? null : cardNameTemplate.Trim(),
                CardNameSwitches = userCardNameSwitches.Count > 0 ? userCardNameSwitches : null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            string json = JsonSerializer.Serialize(record, jsonOptions);
            DataIO.SaveData("UserData", userId.ToString(), json);
            count++;
        }

        Log.InfoFormat("[MessageProcessor] 已保存 {0} 条 UserData 记录。", count);
    }

    /// <summary>
    /// 利用ID解析获取合理的发送者名称
    /// 优先级：持久化显示名 > 人物卡名称 > QQ昵称 > QQ ID
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="isSimulationMode">是否为模拟模式</param>
    /// <returns>发送者名称</returns>
    private string GetReasonableSenderName(long userId, bool isSimulationMode = false, bool skipCurrentCharacter = false)
    {
        // 1. 先尝试获取持久化显示名
        var persistentName = GetPersistentUserDisplayName(userId);
        if (!string.IsNullOrEmpty(persistentName))
        {
            return persistentName;
        }

        // 2. 尝试获取现有人物卡名称（只获取，不创建）
        // skipCurrentCharacter 为 true 时跳过此检查（用于创建新人物卡时）
        if (!skipCurrentCharacter)
        {
            var currentCharacterName = TryGetCurrentCharacterName(userId);
            if (!string.IsNullOrEmpty(currentCharacterName))
            {
                return currentCharacterName;
            }
        }

        Log.Warn("[MessageProcessor] 未找到现有人物卡，尝试获取QQ群名片...");
        // 3. 尝试获取QQ群名片名称
        var groupCardName = GetGroupCardName(userId, isSimulationMode);
        if (!string.IsNullOrEmpty(groupCardName))
        {
            return groupCardName;
        }

        Log.Warn("[MessageProcessor] 未找到QQ群名片，尝试获取QQ昵称...");
        // 4. 尝试获取QQ昵称
        var nickname = GetUserNickname(userId, isSimulationMode);
        if (!string.IsNullOrEmpty(nickname))
        {
            return nickname;
        }

        Log.Warn("[MessageProcessor] 未找到QQ昵称，使用QQ ID...");
        // 5. 最后使用QQ ID
        return userId > 0 ? userId.ToString() : "[IdError]";
    }

    /// <summary>
    /// 获取用户昵称
    /// </summary>
    /// <summary>
    /// 获取QQ群名片名称（通过 MessageDistribution 获取）
    /// </summary>
    private string? GetGroupCardName(long userId, bool isSimulationMode = false)
    {
        try
        {
            // 尝试从MessageDistribution获取群名片
            if (MessageDistribution != null)
            {
                var groupCard = MessageDistribution.GetGroupCardName(userId, isSimulationMode);
                return groupCard;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[MessageProcessor] 获取群名片失败: {ex.Message}");
        }
        
        return null;
    }

    private string? GetUserNickname(long userId, bool isSimulationMode = false)
    {
        try
        {
            // 尝试从MessageDistribution获取用户信息
            if (MessageDistribution != null)
            {
                var userInfo = MessageDistribution.GetUserInfo(userId, isSimulationMode);
                return userInfo?.Nickname;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[MessageProcessor] 获取用户昵称失败: {ex.Message}");
        }
        
        // 如果无法获取昵称，返回null
        return null;
    }

    /// <summary>
    /// 获取指定用户的持久化名称（仅访问内存缓存）
    /// </summary>
    private string? GetPersistentUserDisplayName(long userId)
    {
        if (userId <= 0)
            return null;

        if (userDisplayNames.TryGetValue(userId, out var name))
        {
            var trimmed = name?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }

    /// <summary>
    /// 设置/更新指定用户的持久化名称（仅更新内存缓存）
    /// 为空或空白视为“未设置”。
    /// 实际持久化由 SaveUserData 在整体保存时完成。
    /// </summary>
    private void SetPersistentUserDisplayName(long userId, string? name)
    {
        if (userId <= 0)
            return;

        var trimmed = name?.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            userDisplayNames.AddOrUpdate(userId, string.Empty, (_, __) => string.Empty);
        }
        else
        {
            userDisplayNames.AddOrUpdate(userId, trimmed, (_, __) => trimmed);
        }
    }

    /// <summary>
    /// 回复委托
    /// </summary>
    private Action<string, Msg>? Reply
    {
        get
        {
            if (MessageDistribution != null)
            {
                return MessageDistribution.Reply;
            }
            return null;
        }
    }

    /// <summary>
    /// 构造函数（公开以支持依赖注入）
    /// </summary>
    /// <param name="dispatcher">UI调度器实现，若为null则使用控制台调度器</param>
    public MessageProcessor(MDiceV2.Abstractions.IDispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher;
        
        // 初始化配置
        basicConfigData = new BasicConfig
        {
            Url = "ws://localhost:8080",
            ApproveFriendJoinRequest = false,
            ApproveGroupJoinRequest = false,
            SendGroupJoinReport = false,
            SendFriendJoinReport = false
        };
        
        // 为向后兼容性，设置静态单例实例
        // （新代码应使用注入的实例，而非这个静态属性）
        lock (_instanceLock)
        {
            if (Instance == null)
            {
                Instance = this;
            }
        }
    }

    /// <summary>
    /// <summary>
    /// 设置UI调度器（用于GetInstance()创建的单例实例）
    /// </summary>
    public void SetDispatcher(MDiceV2.Abstractions.IDispatcher? dispatcher)
    {
        _dispatcher = dispatcher;
        LogSender.Normal($"[MessageProcessor] SetDispatcher已执行，_dispatcher设置为 {(dispatcher != null ? "不为null" : "为null")}");
    }

    /// 获取单例实例（已弃用，仅用于向后兼容）
    /// 新代码应通过依赖注入使用MessageProcessor
    /// </summary>
    /// <returns>MessageProcessor实例</returns>
    [Obsolete("应使用依赖注入而非GetInstance()")]
    public static MessageProcessor GetInstance()
    {
        if (Instance == null)
        {
            lock (_instanceLock)
            {
                if (Instance == null)
                {
                    Instance = new MessageProcessor();
                    // 延迟初始化，等待UI准备就绪
                }
            }
        }
        return Instance;
    }

    /// <summary>
    /// 手动触发初始化（在UI准备就绪后调用）
    /// 【修复】在headless模式中确保Instance被创建和初始化
    /// </summary>
    public static void EnsureInitialized()
    {
        // 先确保实例存在（headless模式中可能还未创建）
        if (Instance == null)
        {
            lock (_instanceLock)
            {
                if (Instance == null)
                {
                    Instance = new MessageProcessor();
                    Log.InfoFormat("[MessageProcessor] EnsureInitialized创建了新实例");
                }
            }
        }

        // 然后确保初始化
        if (!Instance._isInitialized)
        {
            Instance.Initialize();
            Log.InfoFormat("[MessageProcessor] EnsureInitialized已初始化实例");
        }
    }

    /// <summary>
    /// 初始化方法（只执行一次）
    /// 支持外部调用（用于DI场景），所以改为公开
    /// </summary>
    public void Initialize()
    {
        lock (_instanceLock)
        {
            if (_isInitialized)
                return;

            try
            {
                Log.InfoFormat("[MessageProcessor] 开始初始化数据管理器...");

                // 初始化数据管理器
                try
                {
                    DataIO = new DataIO();
                    Log.InfoFormat("[MessageProcessor] DataIO已创建");
                }
                catch (Exception ex)
                {
                    Log.Error($"[MessageProcessor] DataIO创建失败: {ex.Message}");
                    Log.Error($"[MessageProcessor] 堆栈跟踪: {ex.StackTrace}");
                    throw;
                }

                try
                {
                    // 在此处加载自定义牌堆（使用当前工作目录定位到 MDiceV2_Debug\Resources\Deck）
                    string deckDirPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Resources", "Deck"); // 牌堆目录路径
                    DeckSet.LoadCustomDecks(deckDirPath); // 加载并覆盖自定义牌堆
                    Log.InfoFormat($"[MessageProcessor] 自定义牌堆已加载: {deckDirPath}");
                    
                    RuleDataIO = new RuleDataIO();
                    Log.InfoFormat("[MessageProcessor] RuleDataIO已创建");

                    // 从资源文件夹加载规则数据
                    Log.InfoFormat("[MessageProcessor] 开始加载规则数据...");
                    RuleDataLoader.LoadRulesFromResourceFolder(RuleDataIO);
                    Log.InfoFormat("[MessageProcessor] 规则数据加载完成");
                }
                catch (Exception ex)
                {
                    Log.Error($"[MessageProcessor] RuleDataIO创建失败: {ex.Message}");
                    Log.Error($"[MessageProcessor] 堆栈跟踪: {ex.StackTrace}");
                    throw;
                }

                // 初始化全局反馈消息
                Log.InfoFormat("[MessageProcessor] 正在初始化全局反馈消息...");
                try
                {
                    GlobalFeedbackMessages.InitializeDataIO(DataIO);
                    Log.InfoFormat("[MessageProcessor] GlobalFeedbackMessages已初始化");
                }
                catch (Exception ex)
                {
                    Log.Error($"[MessageProcessor] GlobalFeedbackMessages初始化失败: {ex.Message}");
                    Log.Error($"[MessageProcessor] 堆栈跟踪: {ex.StackTrace}");
                    throw;
                }

                // 读取基础设置并输出日志
                var allSettings = GlobalFeedbackMessages.GetAllBasicSettings();
                foreach (var setting in allSettings)
                {
                    //Log.InfoFormat($"[MessageProcessor] 已加载基础设置: {setting.Key} = {setting.Value}");
                }

                // 同步本地 Master / MasterGroup 配置 - 使用规范化后的键
                string masterValue = GlobalFeedbackMessages.GetBasicSetting("Master");
                string masterGroupValue = GlobalFeedbackMessages.GetBasicSetting("MasterGroup");
                
                basicConfigData.Master = masterValue;
                basicConfigData.MasterGroup = masterGroupValue;

                Log.InfoFormat($"[MessageProcessor] 已从数据库加载Master: '{masterValue}', MasterGroup: '{masterGroupValue}'");

                // DataIO实例化后加载基础设置到UI（若存在UI）
                // 改为异步调用以支持UI线程，Console模式下跳过
                LogSender.Normal($"[MessageProcessor] MainViewModel = {(MainViewModel != null ? "不为null" : "为null")}, _dispatcher = {(_dispatcher != null ? "不为null" : "为null")}");
                if (MainViewModel != null && _dispatcher != null)
                {
                    LogSender.Normal($"[MessageProcessor] 即将调用 dispatcher.Post(() => MainViewModel.LoadBasicSettingsFromGlobal())");
                    _dispatcher.Post(() => MainViewModel.LoadBasicSettingsFromGlobal());
                    LogSender.Normal($"[MessageProcessor] dispatcher.Post 已提交");
                }
                else
                {
                    LogSender.Warn($"[MessageProcessor] 跳过LoadBasicSettingsFromGlobal，因为MainViewModel或_dispatcher为null");
                }
                Log.InfoFormat("[MessageProcessor] 全局反馈消息已初始化");

                // 初始化TRPG日志管理器
                _trpgLogManager = TRPGLogManager.GetInstance();
                Log.InfoFormat("[MessageProcessor] TRPG日志管理器已初始化");

                // 确保存在 MessageDistribution 实例并初始化引用
                if (MessageDistribution == null)
                {
                    MessageDistribution = MessageDistribution.GetInstance();
                    Log.InfoFormat("[MessageProcessor] 获取了MessageDistribution单例实例");
                }
                MessageDistribution.MessageProcessor = this;
                InitializeQqUpdatePackageReceiver();
                
                // 从基础设置加载URL
                string configuredUrl = GlobalFeedbackMessages.GetBasicSetting("Url");
                if (!string.IsNullOrEmpty(configuredUrl))
                {
                    basicConfigData.Url = configuredUrl;
                    Log.InfoFormat($"[MessageProcessor] 已从基础设置加载 URL: {configuredUrl}");
                }
                else
                {
                    Log.InfoFormat("[MessageProcessor] 未找到保存的URL配置，使用默认值");
                }

                // 从基础设置加载游戏状态保留天数（可选配置项）
                string retentionDaysSetting = GlobalFeedbackMessages.GetBasicSetting("GameStateRetentionDays");
                if (!string.IsNullOrWhiteSpace(retentionDaysSetting) &&
                    int.TryParse(retentionDaysSetting, out int parsedDays) && parsedDays > 0)
                {
                    gameStateRetentionDays = parsedDays;
                    Log.InfoFormat($"[MessageProcessor] 已从基础设置加载游戏状态保留天数: {gameStateRetentionDays} 天");
                }
                else
                {
                    Log.InfoFormat($"[MessageProcessor] 使用默认游戏状态保留天数: {gameStateRetentionDays} 天");
                }

                // 注：duel 每日回合上限现在动态计算：D3 + lg(好感度) - 1，最小为 0

                // 加载数据
                Log.InfoFormat("[MessageProcessor] 开始加载所有数据...");
                try
                {
                    LoadAllData();
                    Log.InfoFormat("[MessageProcessor] LoadAllData完成");
                }
                catch (Exception ex)
                {
                    Log.Error($"[MessageProcessor] LoadAllData失败: {ex.Message}");
                    Log.Error($"[MessageProcessor] 堆栈跟踪: {ex.StackTrace}");
                    throw;
                }
                
                // 应用基础设置的 Master 与 MasterGroup 为 0 级白名单
                try
                {
                    ApplyBasicWhitelistOverrides();
                    Log.InfoFormat("[MessageProcessor] ApplyBasicWhitelistOverrides完成");
                }
                catch (Exception ex)
                {
                    Log.Error($"[MessageProcessor] ApplyBasicWhitelistOverrides失败: {ex.Message}");
                    Log.Error($"[MessageProcessor] 堆栈跟踪: {ex.StackTrace}");
                    throw;
                }
                
                try
                {
                    LoadCurrentRulebookNames();
                    Log.InfoFormat("[MessageProcessor] LoadCurrentRulebookNames完成");
                }
                catch (Exception ex)
                {
                    Log.Error($"[MessageProcessor] LoadCurrentRulebookNames失败: {ex.Message}");
                    Log.Error($"[MessageProcessor] 堆栈跟踪: {ex.StackTrace}");
                    throw;
                }

                // 加载游戏状态到内存
                Log.InfoFormat("[MessageProcessor] 开始加载游戏状态到内存...");
                try
                {
                    LoadAllGameStates();
                    Log.InfoFormat("[MessageProcessor] LoadAllGameStates完成");
                }
                catch (Exception ex)
                {
                    Log.Error($"[MessageProcessor] LoadAllGameStates失败: {ex.Message}");
                    Log.Error($"[MessageProcessor] 堆栈跟踪: {ex.StackTrace}");
                    throw;
                }
                
                // 加载完成后验证重要配置
                WSconnection.wsUrl = GlobalFeedbackMessages.GetBasicSetting("Url");
                Log.InfoFormat($"[MessageProcessor] 当前WebSocket URL配置: {WSconnection.wsUrl}");
                
                var masterGroup = GlobalFeedbackMessages.GetBasicSetting("MasterGroup");
                Log.InfoFormat($"[MessageProcessor] 当前Master群组配置: {masterGroup}");
                
                Log.InfoFormat("[MessageProcessor] 所有数据加载完成");

                // 在所有设置加载完成后自动建立WebSocket连接
                if (MessageDistribution?.WSconnection != null)
                {
                    Log.InfoFormat("[MessageProcessor] 开始自动建立WebSocket连接...");
                    try
                    {
                        Task.Run(async () =>
                        {
                            try
                            {
                                await MessageDistribution.WSconnection.StartConnection();
                                Log.InfoFormat("[MessageProcessor] WebSocket连接已成功建立");
                            }
                            catch (Exception connEx)
                            {
                                Log.Error($"[MessageProcessor] WebSocket自动连接失败: {connEx.Message}");
                            }
                        });
                    }
                    catch (Exception taskEx)
                    {
                        Log.Error($"[MessageProcessor] 启动自动连接任务失败: {taskEx.Message}");
                    }
                }
                else
                {
                    Log.Warn("[MessageProcessor] WSconnection实例为空，无法自动建立连接");
                }

                // 订阅群成员增加事件，当机器人加入群时发送欢迎消息
                if (MessageDistribution != null)
                {
                    MessageDistribution.OnGroupIncrease += HandleGroupIncrease;
                    Log.InfoFormat("[MessageProcessor] 已订阅 OnGroupIncrease 事件");

                    // 订阅群管理员变动事件，维护群管理员缓存
                    MessageDistribution.OnGroupAdmin += HandleGroupAdmin;
                    Log.InfoFormat("[MessageProcessor] 已订阅 OnGroupAdmin 事件");
                }

                // 【新增】初始化gRPC基础设施（Console和UI版本都需要）
                Log.InfoFormat("[MessageProcessor] 开始初始化gRPC基础设施...");
                try
                {
                    InitializeGrpcInfrastructure();
                    Log.InfoFormat("[MessageProcessor] gRPC基础设施初始化成功");
                }
                catch (Exception grpcEx)
                {
                    Log.Warn($"[MessageProcessor] gRPC基础设施初始化失败，应用继续运行: {grpcEx.Message}");
                    // 继续运行，不影响主要功能
                }

                // 【新增】初始化周期性保存定时器（每1小时自动保存一次）
                Log.InfoFormat("[MessageProcessor] 开始初始化周期性保存定时器（1小时周期）...");
                try
                {
                    // 延迟60秒后开始，然后每3600秒（1小时）执行一次
                    _autoSaveTimer = new Timer(
                        callback: PerformAutoSave,
                        state: null,
                        dueTime: TimeSpan.FromSeconds(60),
                        period: TimeSpan.FromSeconds(3600)
                    );
                    Log.InfoFormat("[MessageProcessor] 周期性保存定时器已初始化");
                }
                catch (Exception timerEx)
                {
                    Log.Error($"[MessageProcessor] 周期性保存定时器初始化失败: {timerEx.Message}");
                    // 继续运行，不影响主要功能
                }

                _isInitialized = true;
                Log.InfoFormat("MessageProcessor initialized successfully");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to initialize MessageProcessor: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// 设置ModEventBridge（由App在加载Mods后调用）
    /// </summary>
    /// <param name="modEventBridge">ModEventBridge实例</param>
    public void SetModEventBridge(ModEventBridge modEventBridge)
    {
        var oldBridge = _modEventBridge;
        if (oldBridge != null)
        {
            oldBridge.CommandProvidersChanged -= OnModCommandProvidersChanged;
        }

        _modEventBridge = modEventBridge ?? throw new ArgumentNullException(nameof(modEventBridge));
        _modEventBridge.CommandProvidersChanged += OnModCommandProvidersChanged;
        InvalidateCommandHandlers("SetModEventBridge");

        Log.InfoFormat(
            "[MessageProcessor] ✓ ModEventBridge已成功设置，Mod消息处理已启用 bridgeId={0}",
            GetObjectId(_modEventBridge));
    }

    private void OnModCommandProvidersChanged()
    {
        InvalidateCommandHandlers("ModCommandProvidersChanged");
    }

    private void InvalidateCommandHandlers(string reason)
    {
        commandHandlers = null;
        Log.InfoFormat("[CommandInit] commandHandlers invalidated reason={0} bridgeId={1}", reason, GetObjectId(_modEventBridge));
    }

    private static string GetObjectId(object? instance)
    {
        return instance is null ? "null" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance).ToString();
    }

    private bool _isInitialized = false;
    // Track whether Dispose has been executed to avoid duplicate flush/close operations
    private bool _isDisposed = false;

    /// <summary>
    /// 周期性自动保存回调方法（由定时器调用）
    /// 调用 SaveAllData() 来保存所有游戏状态和配置数据
    /// </summary>
    private void PerformAutoSave(object? state)
    {
        try
        {
            Log.InfoFormat("[自动保存] 开始周期性数据保存...");
            SaveAllData();
            Log.InfoFormat("[自动保存] ✓ 周期性数据保存完成");
        }
        catch (Exception ex)
        {
            Log.Error($"[自动保存] 周期性保存失败: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 初始化gRPC基础设施（Console和UI版本共用）
    /// 仅负责创建基础设施，处理器注册由Console版本完成
    /// </summary>
    private void InitializeGrpcInfrastructure()
    {
        try
        {
            // 1. 创建ConfigSyncDispatcher和SyncConfigManager
            _configSyncDispatcher = GrpcBootstrapper.CreateDispatcher();
            _syncConfigManager = GrpcBootstrapper.CreateSyncManager();
            Log.InfoFormat("[MessageProcessor] ConfigSyncDispatcher 和 SyncConfigManager 已创建");

            // 2. 注册Console版本的处理器（无UI更新，只更新本地数据）
            RegisterConsoleConfigHandlers();
            Log.InfoFormat("[MessageProcessor] Console版本的处理器已注册");

            // 3. 异步启动gRPC服务器
            _ = InitializeGrpcServerAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[MessageProcessor] 初始化gRPC基础设施失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 注册Console版本的配置处理器
    /// 这些处理器直接调用MessageProcessor的现有方法，不涉及UI
    /// </summary>
    private void RegisterConsoleConfigHandlers()
    {
        if (_configSyncDispatcher == null) return;

        // 注册"basic"类别处理器 - 更新基本配置
        _configSyncDispatcher.RegisterCategory("basic", async (key, value) =>
        {
            try
            {
                // 提取配置项名称（去掉"basic."前缀）
                string configName = key.StartsWith("basic.", StringComparison.OrdinalIgnoreCase)
                    ? key.Substring(6)
                    : key;

                // 调用现有的UpdateBasicConfig方法
                UpdateBasicConfig(configName, value);
                Log.InfoFormat($"[MessageProcessor] Basic config applied: {configName} = {value}");
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 应用basic配置失败: {ex.Message}");
            }
        });

        // 注册"feedback"类别处理器 - 更新反馈消息
        _configSyncDispatcher.RegisterCategory("feedback", async (key, value) =>
        {
            try
            {
                string templateKey = key.StartsWith("feedback.", StringComparison.OrdinalIgnoreCase)
                    ? key.Substring(9)
                    : key;

                GlobalFeedbackMessages.FeedbackTemplates[templateKey] = value;
                Log.InfoFormat($"[MessageProcessor] Feedback template applied: {templateKey}");
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 应用feedback配置失败: {ex.Message}");
            }
        });

        // 注册"help"类别处理器 - 更新帮助消息
        _configSyncDispatcher.RegisterCategory("help", async (key, value) =>
        {
            try
            {
                string helpKey = key.StartsWith("help.", StringComparison.OrdinalIgnoreCase)
                    ? key.Substring(5)
                    : key;

                GlobalFeedbackMessages.HelpTemplates[helpKey] = value;
                Log.InfoFormat($"[MessageProcessor] Help template applied: {helpKey}");
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 应用help配置失败: {ex.Message}");
            }
        });

        // 注册"mod"类别处理器 - 处理Mod配置
        _configSyncDispatcher.RegisterCategory("mod", async (key, value) =>
        {
            try
            {
                Log.InfoFormat($"[MessageProcessor] Mod config received (Console版本): {key}");
                // Console版本可在此添加Mod配置处理逻辑
                // 目前仅记录日志
            }
            catch (Exception ex)
            {
                Log.Error($"[MessageProcessor] 处理mod配置失败: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 异步初始化并启动gRPC服务器
    /// </summary>
    private async Task InitializeGrpcServerAsync()
    {
        try
        {
            if (_syncConfigManager == null || _configSyncDispatcher == null)
            {
                Log.Error("[MessageProcessor] SyncConfigManager或ConfigSyncDispatcher为null，无法启动gRPC服务器");
                return;
            }

            // 创建配置导出函数（Console版本从GlobalFeedbackMessages导出）
            var configProvider = () => ExportConfigForGrpc();

            // 使用GrpcBootstrapper创建服务器
            _grpcServerHost = GrpcBootstrapper.CreateServer(
                _syncConfigManager.LocalKey,
                _syncConfigManager,
                configProvider
            );

            // 初始化并启动服务器
            await GrpcBootstrapper.InitializeServerAsync(
                _grpcServerHost,
                _configSyncDispatcher,
                5001,  // 默认监听端口
                () => Log.InfoFormat("[MessageProcessor] gRPC服务器已启动，监听端口5001")
            );
        }
        catch (Exception ex)
        {
            Log.Error($"[MessageProcessor] gRPC服务器启动失败: {ex.Message}");
            // 不影响程序继续运行
        }
    }

    /// <summary>
    /// 为gRPC导出配置
    /// Console版本从GlobalFeedbackMessages导出配置
    /// </summary>
    private Dictionary<string, string> ExportConfigForGrpc()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 导出基本配置
            var basicSettings = GlobalFeedbackMessages.GetAllBasicSettings();
            foreach (var kvp in basicSettings)
            {
                result[$"basic.{kvp.Key.ToLower()}"] = kvp.Value;
            }

            // 导出反馈模板
            foreach (var kvp in GlobalFeedbackMessages.FeedbackTemplates)
            {
                result[$"feedback.{kvp.Key.ToLower()}"] = kvp.Value;
            }

            // 导出帮助消息
            foreach (var kvp in GlobalFeedbackMessages.HelpTemplates)
            {
                result[$"help.{kvp.Key.ToLower()}"] = kvp.Value;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[MessageProcessor] 导出配置失败: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 加载当前规则书名称
    /// </summary>
    private void LoadCurrentRulebookNames()
    {
        var data = DataIO.ReadAllData("CurrentRulebook");
        foreach (var kvp in data)
        {
            if (long.TryParse(kvp.Key, out long userId))
            {
                currentRulebookNames[userId] = kvp.Value;
            }
        }
    }

    /// <summary>
    /// 保存当前规则书名称
    /// </summary>
    private void SaveCurrentRulebookName(long userId, string rulebook)
    {
        currentRulebookNames[userId] = rulebook;
        DataIO.SaveData("CurrentRulebook", userId.ToString(), rulebook);
    }

    /// <summary>
    /// 初始化配置
    /// </summary>
    public async Task InitalAsync()
    {
        Log.InfoFormat("[MessageProcessor] 开始初始化...");

        // 从全局设置获取URL
        string configuredUrl = GlobalFeedbackMessages.GetBasicSetting("Url");
        Log.InfoFormat($"[MessageProcessor] 从基础设置加载的 URL: {configuredUrl}");

        // 设置WebSocket URL并等待连接完成
        if (MessageDistribution?.WSconnection != null)
        {
            string urlToUse;
            if (!string.IsNullOrEmpty(configuredUrl))
            {
                urlToUse = configuredUrl;
                Log.InfoFormat($"[MessageProcessor] 使用配置的 URL: {configuredUrl}");
            }
            else
            {
                urlToUse = basicConfigData.Url ?? "ws://localhost:8080";
                Log.InfoFormat($"[MessageProcessor] 使用默认 URL: {urlToUse}");
            }

            WSconnection.wsUrl = urlToUse;

            // 不要在启动时保存URL，只在用户主动改变时保存
            // GlobalFeedbackMessages.SetBasicSetting("Url", urlToUse);
            // GlobalFeedbackMessages.SaveBasicSettings();

            await MessageDistribution.WSconnection.StartConnection();
        }
        else
        {
            Log.Error("[MessageProcessor] MessageDistribution 或 WSconnection 为空");
        }
    }

    /// <summary>
    /// 更新基本配置（直接使用GlobalFeedbackMessages）
    /// </summary>
    private void UpdateBasicConfig(string name, object value)
    {
        Log.InfoFormat($"[MessageProcessor] 开始更新基本配置: {name} = {value}");

        try
        {
            // 直接更新GlobalFeedbackMessages
            string stringValue = value?.ToString() ?? string.Empty;
            if (value is bool boolValue)
            {
                stringValue = boolValue.ToString();
            }

            GlobalFeedbackMessages.SetBasicSetting(name, stringValue ?? string.Empty);

            // 立即保存更改
            GlobalFeedbackMessages.SaveBasicSettings();
            Log.InfoFormat($"[MessageProcessor] 配置 {name} 已更新并保存到数据库");

            // 同时更新本地basicConfigData以保持兼容性
            lock (configLock)
            {
                switch (name)
                {
                    case "Master":
                        basicConfigData.Master = value as string ?? "";
                        break;
                    case "MasterGroup":
                        basicConfigData.MasterGroup = value as string ?? "";
                        break;
                    case "ApproveFriendJoinRequest":
                        if (value is bool approveFriend)
                            basicConfigData.ApproveFriendJoinRequest = approveFriend;
                        break;
                    case "ApproveGroupJoinRequest":
                        if (value is bool approveGroup)
                            basicConfigData.ApproveGroupJoinRequest = approveGroup;
                        break;
                    case "SendGroupJoinReport":
                        if (value is bool sendGroupReport)
                            basicConfigData.SendGroupJoinReport = sendGroupReport;
                        break;
                    case "SendFriendJoinReport":
                        if (value is bool sendFriendReport)
                            basicConfigData.SendFriendJoinReport = sendFriendReport;
                        break;
                    case "Url":
                        basicConfigData.Url = (value as string) ?? "";
                        Log.InfoFormat($"[MessageProcessor] WebSocket URL 已更新为: {value}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[MessageProcessor] 更新配置 {name} 时发生错误: {ex.Message}");
        }
    }

    /// <summary>
    /// WebSocket URL变化处理（更新本地配置）
    /// </summary>
    public void OnWsUrlChanged(string newText)
    {
        GlobalFeedbackMessages.SetBasicSetting("Url", newText);
        GlobalFeedbackMessages.SaveBasicSettings();

        lock (configLock)
        {
            basicConfigData.Url = newText;
        }

        Log.InfoFormat($"[MessageProcessor] WebSocket URL已更新: {newText}");
    }

    /// <summary>
    /// 获取基本配置（从GlobalFeedbackMessages读取）
    /// </summary>
    public BasicConfig GetBasicConfig()
    {
        var settings = GlobalFeedbackMessages.GetAllBasicSettings();

        // 从GlobalFeedbackMessages读取所有设置，保持本地basicConfigData同步
        lock (configLock)
        {
            basicConfigData.Master = settings.GetValueOrDefault("Master", basicConfigData.Master);
            basicConfigData.MasterGroup = settings.GetValueOrDefault("MasterGroup", basicConfigData.MasterGroup);
            basicConfigData.ApproveFriendJoinRequest = bool.TryParse(settings.GetValueOrDefault("ApproveFriendJoinRequest", "false"), out bool friendJoin) ? friendJoin : basicConfigData.ApproveFriendJoinRequest;
            basicConfigData.ApproveGroupJoinRequest = bool.TryParse(settings.GetValueOrDefault("ApproveGroupJoinRequest", "false"), out bool groupJoin) ? groupJoin : basicConfigData.ApproveGroupJoinRequest;
            basicConfigData.SendGroupJoinReport = bool.TryParse(settings.GetValueOrDefault("SendGroupJoinReport", "false"), out bool sendGroup) ? sendGroup : basicConfigData.SendGroupJoinReport;
            basicConfigData.SendFriendJoinReport = bool.TryParse(settings.GetValueOrDefault("SendFriendJoinReport", "false"), out bool sendFriend) ? sendFriend : basicConfigData.SendFriendJoinReport;
            basicConfigData.Url = settings.GetValueOrDefault("Url", basicConfigData.Url);

            return new BasicConfig
            {
                Master = basicConfigData.Master,
                MasterGroup = basicConfigData.MasterGroup,
                ApproveFriendJoinRequest = basicConfigData.ApproveFriendJoinRequest,
                ApproveGroupJoinRequest = basicConfigData.ApproveGroupJoinRequest,
                SendGroupJoinReport = basicConfigData.SendGroupJoinReport,
                SendFriendJoinReport = basicConfigData.SendFriendJoinReport,
                Url = basicConfigData.Url,
            };
        }
    }


    /// <summary>
    /// 检查Bot是否启用
    /// </summary>
    public bool IsBotEnabled(long groupId)
    {
        if (groupDataRecords.TryGetValue(groupId, out var record))
        {
            return record.BotEnabled;
        }
        return true; // 默认启用
    }

    /// <summary>
    /// 检查日志是否启用
    /// </summary>
    public bool IsLogEnabled(long groupId)
    {
        return _logEnabledStates.GetOrAdd(groupId, false); // 默认禁用
    }

    /// <summary>
    /// 加载所有数据
    /// </summary>
    private void LoadAllData()
    {
        // 加载群数据（Bot启用状态和群授权白名单已集成）
        LoadGroupData();

        // 加载日志状态
        var logStates = DataIO.ReadAllData("LogStates");
        foreach (var kvp in logStates)
        {
            if (long.TryParse(kvp.Key, out long groupId))
            {
                _logEnabledStates[groupId] = kvp.Value.ToLower() == "true";
            }
        }

        // 加载日志回放状态
        var replayStatesData = DataIO.ReadAllData("LogReplayStates");
        foreach (var kvp in replayStatesData)
        {
            if (long.TryParse(kvp.Key, out long gid))
            {
                var state = System.Text.Json.JsonSerializer.Deserialize<LogReplayState>(kvp.Value);
                if (state != null) _logReplayStates[gid] = state;
            }
        }

        // 加载用户档案（显示名 / 人物卡 / 好感度 / 授权等级 / 默认检定模式）
        LoadUserData();

        // 加载白名单
        LoadWhitelist();

        // 加载Mod全局存储
        LoadModStorages();

        // 加载每日限额追踪
        LoadDailyLimitTracking();

        // 加载冷却时间追踪
        LoadCooldownTracking();
    }

    /// <summary>
    /// 确保 Master 账号与 MasterGroup 群被强制加入 0 级白名单（覆盖已有值）。
    /// </summary>
    private void ApplyBasicWhitelistOverrides()
    {
        if (DataIO == null)
        {
            Log.Warn("[MessageProcessor] DataIO 未初始化，跳过 Master/MasterGroup 白名单覆盖。");
            return;
        }

        var masterText = GlobalFeedbackMessages.GetBasicSetting("Master");
        if (long.TryParse(masterText, out var masterId) && masterId > 0)
        {
            personAuth[masterId] = 0;
            Log.InfoFormat("[MessageProcessor] 已将 Master 账号 {0} 设为 0 级白名单", masterId);
        }

        var masterGroupText = GlobalFeedbackMessages.GetBasicSetting("MasterGroup");
        if (int.TryParse(masterGroupText, out var masterGroupId) && masterGroupId > 0)
        {
            // 更新 groupDataRecords 中的记录
            var groupId = (long)masterGroupId;
            if (groupDataRecords.TryGetValue(groupId, out var record))
            {
                record.AuthLevel = 0;
            }
            else
            {
                record = new GroupDataRecord
                {
                    GroupId = groupId,
                    AuthLevel = 0
                };
                groupDataRecords[groupId] = record;
            }
            
            // 同时更新 groupAuth 用于高频校验
            groupAuth[masterGroupId] = 0;
            Log.InfoFormat("[MessageProcessor] 已将 MasterGroup {0} 设为 0 级白名单", masterGroupId);
        }
    }

    /// <summary>
    /// 保存Bot状态
    /// </summary>
    /// <summary>
    /// 保存日志状态
    /// </summary>
    private void SaveLogStates()
    {
        foreach (var entry in _logEnabledStates)
        {
            DataIO.SaveData("LogStates", entry.Key.ToString(), entry.Value.ToString().ToLower());
        }
    }

    /// <summary>
    /// 保存日志回放状态
    /// </summary>
    private void SaveLogReplayStates()
    {
        foreach (var entry in _logReplayStates)
        {
            string json = System.Text.Json.JsonSerializer.Serialize(entry.Value);
            DataIO.SaveData("LogReplayStates", entry.Key.ToString(), json);
        }
    }

    /// <summary>
    /// 清理资源
    /// </summary>
    /// <param name="skipSave">是否跳过保存（当同步模式启用时为 true）</param>
    public void Dispose(bool skipSave = false)
    {
        // Make dispose idempotent: protect against multiple calls from different shutdown hooks
        if (_isDisposed)
        {
            Console.WriteLine("MessageProcessor.Dispose() called but already disposed - skipping.");
            return;
        }

        Console.WriteLine("Disposing MessageProcessor resources...");
        try
        {
            if (!skipSave)
            {
                SaveAllData();
                // LastSaveUtc marker已经在SaveAllData中保存，这里不需要重复保存
                Log.InfoFormat("[MessageProcessor] 数据已在SaveAllData中保存，跳过重复保存");
            }
            else
            {
                Log.InfoFormat("[MessageProcessor] 同步模式已启用，跳过保存数据");
            }

            DataIO?.Close();
            RuleDataIO?.Close();
            _trpgLogManager?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Error during MessageProcessor.Dispose: {ex.Message}");
        }
        finally
        {
            _isDisposed = true;
        }
    }

    /// <summary>
    /// 公共方法：保存所有配置数据（用于同步连接时备份本地配置）
    /// </summary>
    public void SaveAllConfiguration()
    {
        LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MessageProcessor] ===== 保存本地所有配置 (同步备份) 开始 =====");
        SaveAllData();
        LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [MessageProcessor] ===== 保存本地所有配置 (同步备份) 完成 =====");
    }

    /// <summary>
    /// 保存所有数据
    /// </summary>
    private void SaveAllData()
    {
        Log.InfoFormat("[MessageProcessor] 开始保存所有数据...");
        try
        {
            var startTime = DateTime.Now;
            
            Log.InfoFormat("[MessageProcessor] 保存群数据（Bot启用状态和群授权白名单）...");
            SaveGroupData();
            
            Log.InfoFormat("[MessageProcessor] 保存日志状态...");
            SaveLogStates();

            Log.InfoFormat("[MessageProcessor] 保存日志回放状态...");
            SaveLogReplayStates();

            Log.InfoFormat("[MessageProcessor] 保存用户档案（含授权等级和检定模式）...");
            SaveUserData();

            Log.InfoFormat("[MessageProcessor] 保存用户白名单...");
            SaveWhitelist();

            Log.InfoFormat("[MessageProcessor] 保存Mod全局存储...");
            SaveModStorages();

            Log.InfoFormat("[MessageProcessor] 保存每日限额追踪...");
            SaveDailyLimitTracking();

            Log.InfoFormat("[MessageProcessor] 保存冷却时间追踪...");
            SaveCooldownTracking();

             Log.InfoFormat("[MessageProcessor] 保存游戏状态到数据库...");
             SaveAllGameStates();

             Log.InfoFormat("[MessageProcessor] 保存反馈消息模板...");
             GlobalFeedbackMessages.SaveTemplates();

             Log.InfoFormat("[MessageProcessor] 保存帮助消息模板...");
             GlobalFeedbackMessages.SaveHelpTemplates();

             Log.InfoFormat("[MessageProcessor] 保存基础设置...");
             GlobalFeedbackMessages.SaveBasicSettings();
            
            // 保存时间戳用于调试（使用UTC时间确保一致性）
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
            DataIO?.SaveData("Metadata", "LastSaveUtc", timestamp);
            Log.InfoFormat($"[MessageProcessor] 保存完成时间戳(UTC): {timestamp}");
            
            var duration = DateTime.Now - startTime;
            Log.InfoFormat($"[MessageProcessor] 所有数据保存完成，耗时: {duration.TotalMilliseconds:F2}ms");
        }
        catch (Exception ex)
        {
            Log.Error($"[MessageProcessor] 保存数据时发生错误: {ex.Message}\n{ex.StackTrace}");
        }
    }


    /// <summary>
    /// 处理掷骰指令
    /// </summary>
    private void HandleRoll(string args, Msg msg)
    {
        var perfMonitor = new PerformanceMonitor($"Roll_{msg.UserId}");

        string trimmedArgs = args.Trim();
        perfMonitor.MarkStage(7, "RollParsing_Start");

        // 检测是否为暗骰模式（.rh 或 .r h）
        bool isHiddenMode = false;
        if (trimmedArgs.StartsWith("h", StringComparison.OrdinalIgnoreCase))
        {
            isHiddenMode = true;
            trimmedArgs = trimmedArgs.Substring(1).Trim();
        }

        if (!TryParseRollCommandPrefixes(trimmedArgs, out int repeatCount, out var pickMode, out int pickCount, out string remaining))
        {
            Reply(GlobalFeedbackMessages.FeedbackTemplates["RollPickModeFormatError"], msg);
            perfMonitor.MarkStage(8, "ExpressionScan_Failed");
            perfMonitor.Complete();
            return;
        }

        if (pickMode != RollPickMode.None)
        {
            perfMonitor.MarkStage(8, "PickMode_Handled");
            HandleRollPickMode(repeatCount, pickMode, pickCount, remaining, msg, isHiddenMode);
            perfMonitor.Complete();
            return;
        }

        perfMonitor.MarkStage(8, "ExpressionScan_Start");
        SplitRollExpressionAndExtraContent(remaining, out string diceExpression, out string extraContent);
        perfMonitor.MarkStage(8, "ExpressionScan_Complete");
        
        // 如果表达式为空，设置为 "d" 而不是返回错误
        if (string.IsNullOrWhiteSpace(diceExpression))
        {
            diceExpression = "d";
        }
        
        // 获取用户的默认骰子面数（稍后在需要时使用）
        int userDefaultDice = 100;
        // TODO: 从用户数据中获取DefaultDice，暂时使用硬编码默认值
        
        // 执行掷骰
        string rollResulttText = string.Empty;
        perfMonitor.MarkStage(9, "DiceCalculation_Start");
        for (int i = 0; i < repeatCount; i++)
        {
            var rollResult = Dice.CalculateExpression(diceExpression, userDefaultDice);
            perfMonitor.CheckpointInStage(9, $"Calculation_{i+1}");
            
            if (!rollResult.Success)
            {
                Reply(rollResult.Detail, msg);
                perfMonitor.MarkStage(9, "DiceCalculation_Failed");
                perfMonitor.Complete();
                return;
            }

            rollResulttText +="\n" + rollResult.Detail;
        }
        perfMonitor.MarkStage(9, "DiceCalculation_Complete");
        
        perfMonitor.MarkStage(10, "ResponseFormat_Start");

        // 先对 RollResult 模板进行 RefineMsg 处理，替换 <name> 等标签
        string refinedRollTemplate = RefineMsg(GlobalFeedbackMessages.FeedbackTemplates["RollResult"], msg);
        string finalReply = SafeFormatString(refinedRollTemplate, rollResulttText, extraContent);

        perfMonitor.MarkStage(10, "ResponseFormat_Complete");

        perfMonitor.MarkStage(11, "ReplySend_Start");
        SendRollReply(finalReply, msg, isHiddenMode);

        perfMonitor.MarkStage(11, "ReplySend_Complete");

        perfMonitor.Complete();
    }

    /// <summary>
    /// 处理Bot指令
    /// </summary>
    private void HandleBot(string args, Msg msg)
    {
        if(!msg.IsGroupAdmin)
        {
            Reply(GlobalFeedbackMessages.FeedbackTemplates["BotCMDNotGroupAdmin"], msg);
            return;
        }

        string trimmedArgs = args.Trim().ToLower();
        long botStateKey = msg.Source == MessageSource.group ? msg.GroupId : -msg.UserId;
        userTrust.TryGetValue(msg.UserId, out double trustValue);
        string trustDisplay = trustValue.ToString("0.##");

        if (string.IsNullOrWhiteSpace(trimmedArgs))
        {
            // 显示状态
            string status = IsBotEnabled(botStateKey) ? "开启" : "关闭";
            string version = GetApplicationVersion();
            Reply(SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["BotStatus"], status, version, trustDisplay), msg);
        }
        else if (trimmedArgs == "on")
        {
            // 获取或创建群数据记录
            if (!groupDataRecords.TryGetValue(botStateKey, out var record))
            {
                record = new GroupDataRecord { GroupId = botStateKey, BotEnabled = true };
                groupDataRecords[botStateKey] = record;
            }
            if (record.BotEnabled)
            {
                Reply(GlobalFeedbackMessages.FeedbackTemplates["BotAlreadyOn"], msg);
                return;
            }
            record.BotEnabled = true;
            SaveGroupData(botStateKey);
            Reply(GlobalFeedbackMessages.FeedbackTemplates["BotOn"], msg);
        }
        else if (trimmedArgs == "off")
        {
            // 获取或创建群数据记录
            if (!groupDataRecords.TryGetValue(botStateKey, out var record))
            {
                record = new GroupDataRecord { GroupId = botStateKey, BotEnabled = false };
                groupDataRecords[botStateKey] = record;
            }
            if (!record.BotEnabled)
            {
                Reply(GlobalFeedbackMessages.FeedbackTemplates["BotAlreadyOff"], msg);
                return;
            }
            record.BotEnabled = false;
            SaveGroupData(botStateKey);
            Reply(GlobalFeedbackMessages.FeedbackTemplates["BotOff"], msg);
        }
        else if (trimmedArgs == "switch")
        {
            // 获取或创建群数据记录
            if (!groupDataRecords.TryGetValue(botStateKey, out var record))
            {
                record = new GroupDataRecord { GroupId = botStateKey, BotEnabled = true };
                groupDataRecords[botStateKey] = record;
            }
            record.BotEnabled = !record.BotEnabled;
            SaveGroupData(botStateKey);
            if (record.BotEnabled)
            {
                Reply(GlobalFeedbackMessages.FeedbackTemplates["BotOn"], msg);
            }
            else
            {
                Reply(GlobalFeedbackMessages.FeedbackTemplates["BotOff"], msg);
            }
        }
        else
        {
            Reply(GlobalFeedbackMessages.FeedbackTemplates["BotUnknownCommand"], msg);
        }
    }



    /// <summary>
    /// 处理日志指令
    /// </summary>
    private void SendLogReplay(long groupId, string logName, long userId, int page, (List<LogEntry> Entries, int TotalCount, int TotalPages, int Page) result, Msg msg)
    {
        var entries = new List<(string, long, string, string)>();
        string header = $"[日志: {logName} 第{result.Page}页/共{result.TotalPages}页]";
        entries.Add(("", 0, "", header));
        
        foreach (var entry in result.Entries)
        {
            string display = $"{entry.PageLocalIndex}. [{entry.Timestamp}] {entry.SenderName}: {entry.Content}";
            foreach (var cmt in entry.Comments)
                display += $"\n   💬 [{cmt.CommentTime}] {cmt.CommenterName}: {cmt.Content}";
            entries.Add((entry.Timestamp, entry.UserId, entry.SenderName, display));
        }
        
        MessageDistribution?.ReplyForward(entries, msg);
    }

    /// <summary>
    /// 处理 .log 指令（跑团日志系统）
    /// 子命令：on, off, list, get, review, replay, cmt, del, ins
    /// 统一格式：.log 子命令 参数（子命令与参数间空格可省略，如 .logon日志名）
    /// </summary>
    private void HandleLog(string args, Msg msg)
    {
        if (msg.Source != MessageSource.group)
        {
            Reply(GlobalFeedbackMessages.FeedbackTemplates["LogCommandGroupOnly"], msg);
            return;
        }

        long groupId = msg.GroupId;
        string trimmedArgs = args.Trim();

        // 提取子命令（字母序列）和剩余参数，空格可省略
        var match = Regex.Match(trimmedArgs, @"^([a-zA-Z]+)\s*(.*)$");
        string command = match.Success ? match.Groups[1].Value.ToLower() : "";
        string logName = match.Success ? match.Groups[2].Value.Trim() : "";

        if (command == "on")
        {
            if (string.IsNullOrEmpty(logName))
            {
                Reply(GlobalFeedbackMessages.FeedbackTemplates["LogNameRequired"], msg);
                return;
            }

            var startResult = _trpgLogManager?.StartLog(groupId, msg.UserId, logName) ?? LogStartResult.Failed;
            switch (startResult)
            {
                case LogStartResult.AlreadyRecording:
                    string currentLogName = _trpgLogManager?.GetActiveLogName(groupId) ?? "未知";
                    Reply($"当前已在记录日志 '{currentLogName}'，请先使用 .log off 关闭后再开启新日志。", msg);
                    return;
                case LogStartResult.Appended:
                    _logEnabledStates.AddOrUpdate(groupId, true, (key, oldValue) => true);
                    SaveLogStates();
                    {
                        var modAppendix = BuildLogLifecycleModAppendix("on", logName, msg);
                        var replyText = $"跑团日志 '{logName}' 续写已开启。";
                        if (!string.IsNullOrWhiteSpace(modAppendix))
                            replyText += "\n" + modAppendix;
                        Reply(replyText, msg);
                    }
                    Log.InfoFormat($"群 {groupId} 续写跑团日志，名称：{logName}");
                    return;
                case LogStartResult.Created:
                    _logEnabledStates.AddOrUpdate(groupId, true, (key, oldValue) => true);
                    SaveLogStates();
                    {
                        var modAppendix = BuildLogLifecycleModAppendix("on", logName, msg);
                        var replyText = SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["LogEnabledWithName"], logName);
                        if (!string.IsNullOrWhiteSpace(modAppendix))
                            replyText += "\n" + modAppendix;
                        Reply(replyText, msg);
                    }
                    Log.InfoFormat($"群 {groupId} 开启了跑团日志记录，名称：{logName}");
                    return;
                default:
                    Reply("开启跑团日志失败。", msg);
                    return;
            }
        }
        else if (command == "off")
        {
            // 首先检查是否有日志在记录
            if (_trpgLogManager == null || !_trpgLogManager.IsLogRecording(groupId))
            {
                Reply("当前没有正在记录的日志。", msg);
                return;
            }

            // 检查是否是开启者（同步检查）
            bool isStarter = _trpgLogManager.IsLogStarter(groupId, msg.UserId);
            if (isStarter)
            {
                _logEnabledStates.AddOrUpdate(groupId, false, (key, oldValue) => false);
                SaveLogStates();
                _trpgLogManager.StopLog(groupId);
                {
                    var modAppendix = BuildLogLifecycleModAppendix("off", string.Empty, msg);
                    var replyText = GlobalFeedbackMessages.FeedbackTemplates["LogDisabled"];
                    if (!string.IsNullOrWhiteSpace(modAppendix))
                        replyText += "\n" + modAppendix;
                    Reply(replyText, msg);
                }
                Log.InfoFormat($"群 {groupId} 关闭了跑团日志记录");
                return;
            }

            // 检查是否是群管理员（异步检查）
            MessageDistribution?.CheckLogClosePermission(groupId, msg.UserId, (hasPermission) => {
                if (!hasPermission)
                {
                    Reply("只有日志开启者或群管理员才能关闭日志。", msg);
                    return;
                }

                _logEnabledStates.AddOrUpdate(groupId, false, (key, oldValue) => false);
                SaveLogStates();
                _trpgLogManager?.StopLog(groupId);
                {
                    var modAppendix = BuildLogLifecycleModAppendix("off", string.Empty, msg);
                    var replyText = GlobalFeedbackMessages.FeedbackTemplates["LogDisabled"];
                    if (!string.IsNullOrWhiteSpace(modAppendix))
                        replyText += "\n" + modAppendix;
                    Reply(replyText, msg);
                }
                Log.InfoFormat($"群 {groupId} 关闭了跑团日志记录");
            });
        }
        else if (command == "list")
        {
            var logsLists = _trpgLogManager?.GetLogList(groupId, msg.UserId);
            if (logsLists != null && (logsLists.Value.GroupLogs.Count > 0 || logsLists.Value.UserLogs.Count > 0))
            {
                string reply = "";
                if (logsLists.Value.GroupLogs.Count > 0)
                {
                    string groupList = string.Join("\n", logsLists.Value.GroupLogs.Select((entry, idx) => $"{idx + 1}. {entry.LogName}\n-{entry.LastRecordTime ?? "未知"}"));
                    reply += $"本群的log:\n{groupList}\n";
                }
                else
                {
                    reply += "本群当前没有历史日志。\n";
                }
                
                if (logsLists.Value.UserLogs.Count > 0)
                {
                    string userList = string.Join("\n", logsLists.Value.UserLogs.Select((entry, idx) => $"{idx + 1}. {entry.LogName}\n-{entry.LastRecordTime ?? "未知"}"));
                    reply += $"你的log:\n{userList}\n";
                }
                
                Reply(reply.TrimEnd(), msg);
                Log.InfoFormat($"群 {groupId} 查询日志列表，群日志：{logsLists.Value.GroupLogs.Count}，用户日志：{logsLists.Value.UserLogs.Count}");
            }
            else
            {
                Reply(GlobalFeedbackMessages.FeedbackTemplates["LogListEmpty"], msg);
                Log.InfoFormat($"群 {groupId} 查询日志列表，未找到任何日志文件。");
            }
        }
        else if (command == "get")
        {
            if (string.IsNullOrEmpty(logName))
            {
                Reply("请指定要获取的日志名称。", msg);
                return;
            }

            string logPath = _trpgLogManager?.GetLogPath(groupId, logName, msg.UserId) ?? "";
            if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
            {
                Log.InfoFormat($"群 {groupId} 获取日志 '{logName}' 路径: {logPath}");
                MessageDistribution?.UploadGroupFile(groupId, logPath, $"{logName}.html");
                Reply($"日志文件 {logName}.html 已上传到群文件。", msg);
            }
            else
            {
                Reply($"未找到名为 '{logName}' 的日志文件。", msg);
                Log.InfoFormat($"群 {groupId} 获取日志 '{logName}' 未找到。");
            }
        }
        else if (command == "review")
        {
            if (string.IsNullOrEmpty(logName))
            {
                Reply("请指定要查看的日志名称。", msg);
                return;
            }

            var entries = _trpgLogManager?.GetLastNLogEntries(groupId, logName, msg.UserId, 50);
            if (entries != null && entries.Count > 0)
            {
                MessageDistribution?.ReplyForward(entries, msg);
                Log.InfoFormat($"群 {groupId} 查看日志 '{logName}' 的最后 {entries.Count} 条记录。");
            }
            else
            {
                Reply($"未找到名为 '{logName}' 的日志文件或日志为空。", msg);
                Log.InfoFormat($"群 {groupId} 查看日志 '{logName}' 未找到或为空。");
            }
        }
        else if (command == "replay")
        {
            // .logreplay [logName] [page] 或 .logreplay (无参数时翻页)
            string[] replayParts = logName.Split(new[]{' '}, 2);
            string actualLogName = replayParts[0];
            int page = 1;
            if (replayParts.Length > 1 && int.TryParse(replayParts[1], out int p))
                page = p;
            
            if (string.IsNullOrEmpty(actualLogName))
            {
                // 无参数 - 翻到下一页
                if (_logReplayStates.TryGetValue(groupId, out var state) && !string.IsNullOrEmpty(state.LogName))
                {
                    int nextPage = state.Page + 1;
                    var flipResult = _trpgLogManager?.GetPaginatedLogEntries(groupId, state.LogName, msg.UserId, nextPage, 50);
                    if (flipResult != null && flipResult.Value.Entries.Count > 0)
                    {
                        SendLogReplay(groupId, state.LogName, msg.UserId, nextPage, flipResult.Value, msg);
                        state.Page = nextPage;
                        SaveLogReplayStates();
                    }
                    else
                        Reply($"日志 '{state.LogName}' 没有更多页了。", msg);
                }
                else
                    Reply("当前群没有进行过log回放，请先使用 .logreplay [日志名称]。", msg);
                return;
            }
            
            var result = _trpgLogManager?.GetPaginatedLogEntries(groupId, actualLogName, msg.UserId, page, 50);
            if (result != null && result.Value.Entries.Count > 0)
            {
                SendLogReplay(groupId, actualLogName, msg.UserId, page, result.Value, msg);
                
                // 更新群replay状态
                _logReplayStates.AddOrUpdate(groupId, 
                    new LogReplayState { LogName = actualLogName, Page = page },
                    (k, old) => { old.LogName = actualLogName; old.Page = page; return old; });
                SaveLogReplayStates();
            }
            else
                Reply($"未找到名为 '{actualLogName}' 的日志文件或日志为空。", msg);
        }
        else if (command == "cmt")
        {
            // .logcmt 条目数 内容
            string[] cmtParts = logName.Split(new[]{' '}, 2);
            if (cmtParts.Length < 2 || !int.TryParse(cmtParts[0], out int localIndex))
            {
                Reply("格式: .logcmt 条目数 内容。例: .logcmt 3 这是一个备注", msg);
                return;
            }
            
            string cmtContent = cmtParts[1].Trim();
            
            if (!_logReplayStates.TryGetValue(groupId, out var state) || string.IsNullOrEmpty(state.LogName))
            {
                Reply("当前群没有进行过log回放，请先使用 .logreplay [日志名称]。", msg);
                return;
            }
            
            int globalIndex = (state.Page - 1) * 50 + localIndex;
            string commenterName = GetReasonableSenderName(msg.UserId, msg.IsSimulationMode);
            bool success = _trpgLogManager?.AddComment(groupId, state.LogName, globalIndex, cmtContent, msg.UserId, commenterName, msg.UserId) ?? false;
            if (success)
            {
                Reply($"已为日志 '{state.LogName}' 第{state.Page}页第{localIndex}条添加备注。", msg);
                // 自动发送当前页 replay
                var replayResult = _trpgLogManager?.GetPaginatedLogEntries(groupId, state.LogName, msg.UserId, state.Page, 50);
                if (replayResult != null && replayResult.Value.Entries.Count > 0)
                {
                    SendLogReplay(groupId, state.LogName, msg.UserId, state.Page, replayResult.Value, msg);
                }
            }
            else
                Reply($"添加备注失败，请检查条目序号是否正确。", msg);
        }
        else if (command == "del")
        {
            // .logdel 1 或 .logdel 1-5
            if (!_logReplayStates.TryGetValue(groupId, out var state) || string.IsNullOrEmpty(state.LogName))
            {
                Reply("当前群没有进行过log回放，请先使用 .logreplay [日志名称]。", msg);
                return;
            }

            var indices = new List<int>();
            if (logName.Contains("-"))
            {
                var rangeParts = logName.Split('-');
                if (rangeParts.Length == 2 && int.TryParse(rangeParts[0], out int start) && int.TryParse(rangeParts[1], out int end))
                {
                    if (start < 1 || end > 50 || start > end)
                    {
                        Reply("格式错误。范围应为 1-50，且起始数字不大于结束数字。", msg);
                        return;
                    }
                    for (int i = start; i <= end; i++)
                    {
                        indices.Add((state.Page - 1) * 50 + i);
                    }
                }
                else
                {
                    Reply("格式错误。使用: .logdel 1 或 .logdel 1-5", msg);
                    return;
                }
            }
            else if (int.TryParse(logName, out int singleIndex))
            {
                if (singleIndex < 1 || singleIndex > 50)
                {
                    Reply("格式错误。序号应为 1-50 之间的数字。", msg);
                    return;
                }
                indices.Add((state.Page - 1) * 50 + singleIndex);
            }
            else
            {
                Reply("格式错误。使用: .logdel 1 或 .logdel 1-5", msg);
                return;
            }

            bool success = _trpgLogManager?.DeleteEntries(groupId, state.LogName, indices, msg.UserId) ?? false;
            if (success)
            {
                Reply($"已删除日志 '{state.LogName}' 中的 {indices.Count} 条条目。", msg);
                // 自动发送当前页 replay
                var replayResult = _trpgLogManager?.GetPaginatedLogEntries(groupId, state.LogName, msg.UserId, state.Page, 50);
                if (replayResult != null && replayResult.Value.Entries.Count > 0)
                {
                    SendLogReplay(groupId, state.LogName, msg.UserId, state.Page, replayResult.Value, msg);
                }
            }
            else
                Reply($"删除条目失败。", msg);
        }
        else if (command == "ins")
        {
            // .logins 1 玩家名 内容
            var insParts = logName.Split(new[]{' '}, 3);
            if (insParts.Length < 3 || !int.TryParse(insParts[0], out int insertIndex))
            {
                Reply("格式: .logins 序号 玩家名 内容。例: .logins 1 Alice 测试内容", msg);
                return;
            }

            if (insertIndex < 1 || insertIndex > 50)
            {
                Reply("序号应为 1-50 之间的数字。", msg);
                return;
            }

            if (!_logReplayStates.TryGetValue(groupId, out var state) || string.IsNullOrEmpty(state.LogName))
            {
                Reply("当前群没有进行过log回放，请先使用 .logreplay [日志名称]。", msg);
                return;
            }

            string targetPlayerName = insParts[1].Trim();
            string insertContent = insParts[2].Trim();
            int globalIndex = (state.Page - 1) * 50 + insertIndex;

            // 获取当前页的条目以查找玩家格式
            var pageResult = _trpgLogManager?.GetPaginatedLogEntries(groupId, state.LogName, msg.UserId, state.Page, 50);
            long senderId = msg.UserId;
            string senderName = GetReasonableSenderName(msg.UserId, msg.IsSimulationMode);

            if (pageResult != null && pageResult.Value.Entries.Count > 0)
            {
                // 模糊匹配玩家名
                var matchedEntry = pageResult.Value.Entries.FirstOrDefault(e => 
                    e.SenderName.Contains(targetPlayerName, StringComparison.OrdinalIgnoreCase) || 
                    targetPlayerName.Contains(e.SenderName, StringComparison.OrdinalIgnoreCase));
                
                if (matchedEntry != null)
                {
                    senderId = matchedEntry.UserId;
                    senderName = matchedEntry.SenderName;
                }
            }

            bool success = _trpgLogManager?.InsertEntry(groupId, state.LogName, globalIndex, insertContent, senderId, senderName, msg.UserId) ?? false;
            if (success)
            {
                Reply($"已在日志 '{state.LogName}' 第{state.Page}页第{insertIndex}条前插入内容（以 {senderName} 名义）。", msg);
                // 自动发送当前页 replay
                var replayResult = _trpgLogManager?.GetPaginatedLogEntries(groupId, state.LogName, msg.UserId, state.Page, 50);
                if (replayResult != null && replayResult.Value.Entries.Count > 0)
                {
                    SendLogReplay(groupId, state.LogName, msg.UserId, state.Page, replayResult.Value, msg);
                }
            }
            else
                Reply($"插入条目失败。", msg);
        }
        else
        {
            // 查询 Mod 注册的 .log 子指令
            if (_modEventBridge != null)
            {
                var providers = _modEventBridge.GetSubcommandProviders();
                Log.InfoFormat("[SubcommandDispatch] parent=log sub={0} providers={1} types={2}", command, providers.Count, string.Join(",", providers.Select(p => p.GetType().FullName ?? p.GetType().Name)));
                foreach (var provider in providers)
                {
                    var result = provider.HandleSubcommand("log", command, logName, msg);
                    if (result != null)
                    {
                        Log.InfoFormat("[SubcommandDispatch] parent=log sub={0} provider={1} found=true", command, provider.GetType().FullName ?? provider.GetType().Name);
                        Reply(result, msg);
                        return;
                    }
                }
            }
            Reply(GlobalFeedbackMessages.FeedbackTemplates["LogCommandInvalid"], msg);
        }
    }

    private string BuildLogLifecycleModAppendix(string subcommand, string args, Msg msg)
    {
        if (_modEventBridge == null)
            return string.Empty;

        var lines = new List<string>();
        foreach (var provider in _modEventBridge.GetSubcommandProviders())
        {
            try
            {
                var result = provider.HandleSubcommand("log", subcommand, args, msg);
                if (!string.IsNullOrWhiteSpace(result))
                    lines.Add(result.Trim());
            }
            catch (Exception ex)
            {
                Log.Warn($"[LogHook] Mod log lifecycle hook failed: {ex.Message}");
            }
        }

        if (lines.Count == 0)
            return string.Empty;

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 处理规则指令
    /// </summary>
    private void HandleRule(string args, Msg msg)
    {
        string trimmedArgs = args.Trim();

        // 解析指令格式："(规则书名称)键" 或 "键"
        string? rulebook = null;
        string key = trimmedArgs;

        var match = Regex.Match(trimmedArgs, @"^\((.*?)\)\s*(.*)$");
        if (match.Success)
        {
            rulebook = match.Groups[1].Value.Trim();
            key = match.Groups[2].Value.Trim();
        }

        if (string.IsNullOrEmpty(key))
        {
            Reply("指令格式：.rule(规则书名称)键 或 .rule键", msg);
            return;
        }

        // 确定使用的规则书
        string effectiveRulebook;
        if (!string.IsNullOrEmpty(rulebook))
        {
            effectiveRulebook = rulebook;
        }
        else if (currentRulebookNames.TryGetValue(msg.UserId, out string? savedRulebook))
        {
            effectiveRulebook = savedRulebook ?? "default_rule";
        }
        else
        {
            effectiveRulebook = "default_rule";
        }

        string? value = RuleDataIO.ReadData(effectiveRulebook, key);
        if (!string.IsNullOrEmpty(value))
        {
            Reply($"{effectiveRulebook} {key}: {value}", msg);

            // 如果指令中指定了规则书，更新currentRulebook
            if (!string.IsNullOrEmpty(rulebook))
            {
                SaveCurrentRulebookName(msg.UserId, rulebook);
            }
        }
        else
        {
            Reply($"未找到 {effectiveRulebook} 中的 {key}", msg);
        }
    }

    /// <summary>
    /// 处理群请求
    /// </summary>
    public void HandleGroupRequest(long groupId, long userId, string comment, string flag)
    {
        var config = GetBasicConfig();
        if (config.ApproveGroupJoinRequest)
        {
            MessageDistribution.ApproveGroupRequest(flag);

            // 发送通知消息
            if (config.SendGroupJoinReport && !string.IsNullOrEmpty(config.Master))
            {
                string message = SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["GroupJoinApproved"], userId.ToString(), groupId.ToString(), comment);
                MessageDistribution.WSconnection.SendPrivateMessage(long.Parse(config.Master), message);
            }
            if (config.SendGroupJoinReport && !string.IsNullOrEmpty(config.MasterGroup))
            {
                string message = SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["GroupJoinApproved"], userId.ToString(), groupId.ToString(), comment);
                MessageDistribution.WSconnection.SendGroupMessage(long.Parse(config.MasterGroup), message);
            }
        }
    }

    /// <summary>
    /// 处理好友请求
    /// </summary>
    public void HandleFriendRequest(long userId, string comment, string flag)
    {
        var config = GetBasicConfig();
        if (config.ApproveFriendJoinRequest)
        {
            MessageDistribution.ApproveFriendRequest(flag);

            // 发送通知消息
            if (config.SendFriendJoinReport && !string.IsNullOrEmpty(config.Master))
            {
                string message = SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["FriendRequestApproved"], userId.ToString(), comment);
                MessageDistribution.WSconnection.SendPrivateMessage(long.Parse(config.Master), message);
            }
            if (config.SendFriendJoinReport && !string.IsNullOrEmpty(config.MasterGroup))
            {
                string message = SafeFormatString(GlobalFeedbackMessages.FeedbackTemplates["FriendRequestApproved"], userId.ToString(), comment);
                MessageDistribution.WSconnection.SendGroupMessage(long.Parse(config.MasterGroup), message);
            }
        }
    }

    /// <summary>
    /// 处理退出指令
    /// </summary>
    private void HandleDismiss(string args, Msg msg)
    {
        if (msg.Source != MessageSource.group)
        {
            Reply("此指令仅在群聊中可用。", msg);
            return;
        }
        Reply(GlobalFeedbackMessages.FeedbackTemplates["LeaveGroupMessage"], msg);
        MessageDistribution.LeaveGroup(msg.GroupId);
    }

    /// <summary>
    /// 处理群成员增加事件（用于在机器人加入群时发送欢迎消息）
    /// </summary>
    /// <summary>
    /// 处理群成员增加事件 - 当新成员加入群时发送欢迎消息
    /// 仅当 WelcomeEnabled 为 true 时发送
    /// </summary>
    private void HandleGroupIncrease(long groupId, long userId, string subType)
    {
        try
        {
            // 获取机器人自身信息，用于判断新成员是否是机器人自己
            var selfInfo = MessageDistribution?.GetSelfInfo();
            
            // 如果加入的是机器人自己，跳过欢迎消息（机器人进群不需要欢迎）
            if (selfInfo != null && selfInfo.UserId > 0 && userId == selfInfo.UserId)
            {
                Log.InfoFormat($"[HandleGroupIncrease] 机器人自身加入群 {groupId}，跳过欢迎消息");
                return;
            }

            // 检查群是否启用欢迎语
            if (!groupDataRecords.TryGetValue(groupId, out var groupRecord))
            {
                Log.InfoFormat($"[HandleGroupIncrease] 群 {groupId} 没有数据记录，跳过欢迎语");
                return;
            }

            // 检查欢迎语是否启用
            if (groupRecord.WelcomeEnabled != true || string.IsNullOrWhiteSpace(groupRecord.Welcome))
            {
                Log.InfoFormat($"[HandleGroupIncrease] 群 {groupId} 未启用欢迎语或未设置内容，跳过");
                return;
            }

            // 获取新成员的昵称
            string nickname = GetReasonableSenderName(userId, false);
            
            Log.InfoFormat($"[HandleGroupIncrease] 群 {groupId} 新成员 {userId} ({nickname}) 加入，准备发送欢迎语");

            // 处理欢迎语中的占位符 {at} 和 {nickname}
            string welcomeMessage = groupRecord.Welcome
                .Replace("{at}", $"[CQ:at,qq={userId}]")
                .Replace("{nickname}", nickname);

            // 发送欢迎消息
            if (MessageDistribution?.WSconnection != null && MessageDistribution.WSconnection.IsWsConnected)
            {
                MessageDistribution.WSconnection.SendGroupMessage(groupId, welcomeMessage);
                Log.InfoFormat($"[HandleGroupIncrease] 欢迎消息已发送到群 {groupId}");
            }
            else
            {
                Log.Warn($"[HandleGroupIncrease] WebSocket 连接不可用，无法发送欢迎消息");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[HandleGroupIncrease] 处理群成员增加事件时出错: {ex.Message}");
            Log.Error($"[HandleGroupIncrease] 堆栈跟踪: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// 处理群管理员变动事件
    /// 维护群管理员缓存，用于 EnsureMsgAuthInfo 中快速判断用户是否为群管理员
    /// </summary>
    /// <param name="groupId">群ID</param>
    /// <param name="userId">用户ID</param>
    /// <param name="isAdmin">是否为管理员/群主</param>
    private void HandleGroupAdmin(long groupId, long userId, bool isAdmin)
    {
        try
        {
            var key = (groupId, userId);
            if (isAdmin)
            {
                groupAdminCache[key] = true;
                Log.InfoFormat($"[HandleGroupAdmin] 群 {groupId} 用户 {userId} 被设为管理员/群主，已更新缓存");
            }
            else
            {
                // 移除缓存（不再是管理员）
                groupAdminCache.TryRemove(key, out _);
                Log.InfoFormat($"[HandleGroupAdmin] 群 {groupId} 用户 {userId} 不再是管理员/群主，已从缓存移除");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[HandleGroupAdmin] 处理群管理员变动时出错: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查用户是否为群管理员/群主
    /// </summary>
    /// <param name="groupId">群ID</param>
    /// <param name="userId">用户ID</param>
    /// <returns>true 表示是管理员/群主，false 表示不是</returns>
    private bool IsGroupAdmin(long groupId, long userId)
    {
        var key = (groupId, userId);
        return groupAdminCache.TryGetValue(key, out bool isAdmin) && isAdmin;
    }

    /// <summary>
    /// 确保群管理员信息已加载（从API获取并更新缓存）
    /// 当缓存中查不到该用户时，通过API获取群成员信息并更新缓存
    /// </summary>
    /// <param name="groupId">群ID</param>
    /// <param name="userId">用户ID</param>
    /// <returns>true 表示是管理员/群主，false 表示不是</returns>
    private bool EnsureGroupAdminFromApi(long groupId, long userId)
    {
        if (groupId <= 0 || userId <= 0)
        {
            return false;
        }

        try
        {
            // 检查是否已经缓存过该用户（可能在之前的检查中已更新）
            var key = (groupId, userId);
            if (groupAdminCache.TryGetValue(key, out bool cachedResult))
            {
                return cachedResult;
            }

            // 通过API获取群成员信息
            if (MessageDistribution?.WSconnection != null && 
                MessageDistribution.WSconnection.IsWsConnected)
            {
                var result = MessageDistribution.WSconnection.GetGroupMemberInfoAsync(groupId, userId).Result;
                if (result.HasValue)
                {
                    // 检查 API 返回状态码
                    if (result.Value.TryGetProperty("retcode", out var retcodeProperty))
                    {
                        int retcode = retcodeProperty.GetInt32();
                        if (retcode != 0)
                        {
                            Log.Warn($"[EnsureGroupAdminFromApi] 获取群成员信息失败: retcode={retcode}, groupId={groupId}, userId={userId}");
                            return false;
                        }
                    }

                    // 从 data 对象中提取角色信息
                    if (result.Value.TryGetProperty("data", out var dataElement) &&
                        dataElement.TryGetProperty("role", out var roleProperty))
                    {
                        var role = roleProperty.GetString();
                        bool isAdmin = role == "owner" || role == "admin";
                        
                        // 更新缓存
                        groupAdminCache[key] = isAdmin;
                        Log.InfoFormat($"[EnsureGroupAdminFromApi] 从API获取管理员状态: groupId={groupId}, userId={userId}, role={role}, isAdmin={isAdmin}");
                        return isAdmin;
                    }
                }
            }
            else
            {
                Log.Warn($"[EnsureGroupAdminFromApi] WebSocket未连接，无法获取群成员信息: groupId={groupId}, userId={userId}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[EnsureGroupAdminFromApi] 获取群成员信息异常: groupId={groupId}, userId={userId}, error={ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// 清理消息内容
    /// </summary>
    public static string CleanMessageContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        // 替换图片CQ码为[图片]
        content = Regex.Replace(content, @"\[CQ:image[^\]]*\]", "[图片]");

        // 可以在这里添加其他CQ码的替换逻辑
        content = Regex.Replace(content, @"\[CQ:record[^\]]*\]", "[语音]");
        content = Regex.Replace(content, @"\[CQ:video[^\]]*\]", "[视频]");

        return content;
    }

    /// <summary>
    /// 获取应用版本号（独立于UI，用于无头模式）
    /// </summary>
    public static string GetApplicationVersion()
    {
        try
        {
            // 尝试从程序集获取版本号
            var assembly = typeof(MessageProcessor).Assembly;
            var infoVersion = assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute;

            if (infoVersion != null && !string.IsNullOrWhiteSpace(infoVersion.InformationalVersion))
            {
                return infoVersion.InformationalVersion;
            }

            var fileVersion = assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyFileVersionAttribute), false)
                .FirstOrDefault() as System.Reflection.AssemblyFileVersionAttribute;

            if (fileVersion != null && !string.IsNullOrWhiteSpace(fileVersion.Version))
            {
                return fileVersion.Version;
            }

            var version = assembly.GetName().Version;
            if (version != null && version.ToString() != "0.0.0.0")
            {
                return version.ToString();
            }

            return "Unknown";
        }
        catch (Exception ex)
        {
            Log.Warn($"[GetApplicationVersion] 获取版本号失败: {ex.Message}");
            return "Unknown";
        }
    }
}
