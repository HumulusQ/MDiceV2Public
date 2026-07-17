using System;
using System.Collections.Generic;
using System.Text.Json;

#nullable enable
namespace MDiceV2.Models;

/// <summary>
/// 全局反馈消息类
/// 管理机器人的各种反馈消息模板
/// </summary>
public static class GlobalFeedbackMessages
{
    /// <summary>
    /// 反馈字典类
    /// 支持默认值回退
    /// </summary>
    public class FeedbackDictionary : Dictionary<string, string>
    {
        private readonly Dictionary<string, string> _defaults;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="defaults">默认值字典</param>
        /// <param name="initial">初始值字典</param>
        public FeedbackDictionary(Dictionary<string, string>? defaults, Dictionary<string, string>? initial = null)
        {
            _defaults = defaults ?? new Dictionary<string, string>();
            if (initial != null)
            {
                foreach (var kvp in initial)
                    base.Add(kvp.Key, kvp.Value);
            }
            else if (defaults != null)
            {
                foreach (var kvp in defaults)
                    base.Add(kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// 索引器
        /// </summary>
        /// <param name="key">键</param>
        /// <returns>值，如果不存在则返回默认值</returns>
        public new string this[string key]
        {
            get
            {
                string? value;
                if (base.TryGetValue(key, out value!))
                    return value ?? string.Empty;
                if (_defaults.TryGetValue(key, out value!))
                    return value ?? string.Empty;
                
                return string.Empty;
                
            }
            set => base[key] = value;
        }

        /// <summary>
        /// 尝试获取值
        /// </summary>
        /// <param name="key">键</param>
        /// <param name="value">值</param>
        /// <returns>是否成功获取</returns>
        public new bool TryGetValue(string key, out string value)
        {
            string? baseValue;
            string? defaultValue;
            if (base.TryGetValue(key, out baseValue!))
            {
                value = baseValue ?? string.Empty;
                return true;
            }
            if (_defaults.TryGetValue(key, out defaultValue!))
            {
                value = defaultValue ?? string.Empty;
                return true;
            }
            value = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// 默认反馈模板
    /// </summary>
    private static readonly Dictionary<string, string> defaultFeedbackTemplates = new()
    {
        // 掷骰指令反馈
        { "RollResult", "<name>执行了掷骰，结果为{0}" },
        { "RollParamOutOfRange", "掷骰参数超出范围，x应为1-99，y应为1-9999: {0}" },
        { "RollUnknownFormat", "未识别到掷骰表达式格式，自动按1d100执行。结果为{0}" },
        { "RollPickModeFormatError", "奖励骰/惩罚骰格式错误。可用示例：\n.r.b\n.r.p\n.r.b3 d20\n.r.p3 d20\n.r.b d20+2\n.r.p3 2d6+3" },
        { "RollPickModeExplicitDiceRequired", ".r 通用掷骰的奖励骰/惩罚骰模式不支持省略 d。\n请使用：\n.r.b3 d20\n.r.p3 d20\n.r.b d20+2\n.r.p d20+2" },

        // 暗骰
        { "HiddenRollPublic", "<name>执行了暗骰，结果已私聊发送。" },
        { "HiddenRollPrivatePrefix", "[暗骰结果]\n{0}" },

        // Bot指令反馈
        { "BotOn", "机器人已开启。" },
        { "BotOff", "机器人已关闭。" },
        { "BotStatus", "MdiceV2掷骰机器人。版本：{1}。\n当前状态：{0}\n当前信任度：{2}\n使用 .bot on/off 开启或关闭。" },
        { "BotGroupOnly", "此指令仅在群聊中可用。" },
        { "BotUnknownCommand", "未识别的 .bot 指令。请使用 .bot on, .bot off 或单独的 .bot。" },
        { "BotDisabledIgnoreCommand", "机器人已关闭，忽略指令: {0}" },
        { "BotAlreadyOn", "已经处于开启状态！" },
        { "BotAlreadyOff", "已经处于关闭状态！" },
        { "BotCMDNotGroupAdmin", "只有群管理员可以使用 .bot 指令。" },

        // .as 代执行
        { "AsProxyReceipt", "为{0}执行的代投：" },

        // Log指令反馈
        { "LogCommandGroupOnly", "此指令仅在群聊中可用。" },
        { "LogEnabled", "跑团日志记录已开启。" },
        { "LogEnabledWithName", "跑团日志 '{0}' 已开启。" },
        { "LogDisabled", "跑团日志记录已关闭。" },
        { "LogCommandInvalid", "未识别的 .log 指令。可用指令有 .log on [名称]/.log off/.log get[名称]/.log list/.log review[名称]/.logreplay[名称] [页数]/.logreply/.logcmt 条目数 内容。" },
        { "LogNameRequired", "开启跑团日志需要指定日志名称。请使用 .log on [日志名称]。" },
        { "LogList", "可用的日志文件：\n{0}" },
        { "LogListEmpty", "当前没有可用的日志文件。" },
        { "LogReplayNoState", "当前群没有进行过log回放，请先使用 .logreplay [日志名称]。" },
        { "LogReplayNoMorePages", "日志 '{0}' 没有更多页了。" },
        { "LogCmtFormatError", "格式: .logcmt 条目数 内容。例: .logcmt 3 这是一个备注" },
        { "LogCmtSuccess", "已为日志 '{0}' 第{1}页第{2}条添加备注。" },
        { "LogCmtNoState", "当前群没有进行过log回放，请先使用 .logreplay [日志名称]。" },

        // 技能插入指令反馈
        {"SkillInsertFormatError", "技能插入指令格式错误，请使用 .st{人物名}技能:数值 技能:数值... 格式，技能和数值之间必须有空格或冒号分隔。" },
        {"CharacterNameEmpty", "人物名不能为空。" },
        {"CharacterCardLimitExceeded", "您的人物卡数量已达到上限（最多6张），无法创建新人物卡。" },
        {"CharacterEmptyAndApplied", "当前无可用人物卡。已为你随机载入其中一张。" },
        { "SkillInsertNoSkills", "未识别到任何技能和数值对。" },
        {"RollError", "掷骰表达式 '{0}' 无效，请检查格式。" },
        {"SkillValueFormatError", "技能 '{0}' 的数值 '{1}' 格式无效，请使用数字或掷骰表达式。" },
        {"SkillValueOutOfRange", "技能 '{0}' 的数值 '{1}' 超出范围，应为0-9999，已重置为{1}。" },
        {"SkillValueNotApplicable", "技能 '{0}' 的数值 '{1}' 不适用，应为0-9999" },
        { "SkillInsertSuccess", "人物卡 '{0}' 的技能已更新：{1}" },
        {"SkillInsertNoValidSkills", "未找到有效技能进行更新。" },
        {"SkillInsertNoName", "请提供要插入的技能名称。" },
        {"SkillInsertDuplicate", "技能 '{0}' 已存在，未插入重复项。" },
        {"SkillInsertInvalid", "技能名称 '{0}' 无效，插入失败。" },
        {"SkillInsertError", "插入技能时发生错误: {0}" },

        // 检定反馈
        {"CoCFormatError", "指令格式错误。请使用 .cc{{模式}}(人物卡) [主干部分] [_附指令] [@目标]\n附指令: _l(循环) _h(暗骰) _p(惩罚骰) _b(奖励骰) _v(对抗)\n对抗检定: .cc 技能名 _v @[CQ:at,qq=123456] 或 .cc 技能名 _v @123456" },
        {"UnsupportedCheckMode", "不支持的模式: {0}。目前仅支持 coc7 和 et。" },
        {"CharacterNotFound", "人物卡 '{0}' 不存在或没有技能。" },
        {"InternalError", "内部错误：未能获取用户人物卡集合。" },
        {"MainPartFormatError", "{0} 指令主干部分格式错误或为空。" },
        {"SkillNotFound", "技能 '{0}' 不存在于角色字典中" },
        {"DiceRollError", "掷骰失败: {0}" },
        {"CoCCheckResult", "<name>的{4}检定:D100={0}/{1}{2} -> {3}" },
        {"ETCheckResult", "{0}检定:{1}->{2} \n检定数值:{3}{4}"},
        
        // CoC7 检定结果个性化文本
        {"CoCExMessageSuccess", "哦——成功了啊" },
        {"CoCExMessageFailure", "" },
        {"CoCExMessageHardSuccess", "" },
        {"CoCExMessageExtremeSuccess", "" },
        {"CoCExMessageCriticalSuccess", "" },
        {"CoCExMessageCriticalFailure", "" },
        
        // ET 检定结果个性化文本
        {"ETExMessageSuccess", "" },
        {"ETExMessageFailure", "" },
        {"ETExMessageCriticalSuccess", "" },
        {"ETExMessageCriticalFailure", "" },

        {"CustomCheckEXMessage", "{0}\n"},

        // 群加入同意和好友同意通知
        {"GroupJoinApproved", "已同意用户 {0} 加入群 {1}。申请留言：{2}" },
        {"FriendRequestApproved", "已同意用户 {0} 的好友请求。申请留言：{1}" },
        {"FriendRequestApprovedReport", "[好友申请已自动同意]\n用户ID: {0}\n申请留言: {1}" },

        // Duel 指令反馈
        {"DuelNoTurnsAvailable", "您今日可用的duel次数已达上限（当前好感度{0}，今日可用次数为0）。请明日再来！"},
        {"DuelNew", "吼，想要挑战我吗，一般来说在你把街上的其他冒险者全部打倒之前我可是不会接受的，但是今天心情不错~\n{3}"},
        {"DuelContinue", "你走进屋子，桌上的棋局收拾的干干净净，昨天的对局丝毫未动，对方等你很久了。\n{3}"},

        // Help指令反馈
        {"HelpDefaultMessage", "请在输入.help[关键词]以查询内容，请注意help反馈中提及的所有方括号格式均为方便理解的格式符号，使用时需去除，其他诸如花括号和普通括号的格式必须在指令中保留\n查询指令示例： .help roll" }, // 占位符，避免空字典
        // 人物卡输出格式
        {"COCCharacterDetails", "DB:{0} san:{1}\n HP:{2}<{3}>" },
        // 角色属性生成默认消息
        {"GCDefaultMessage", "【CoC 角色属性生成 - 共 {0} 行】\n{1}" }
        // Team消息
        ,{"TeamCallMessage", "队伍: {0} ，集合咯：\n{1}" }

        // 先攻列表指令反馈
        ,{ "InitiativeFormatError", "先攻指令格式错误。使用方式：\n.ri+d20+修正 人物名   (投掷d20加修正)\n.ri+修正 人物名       (投掷d20加修正，无表达式)\n.rid20 人物名         (直接投掷d20)\n.ri表达式 人物名      (直接投掷表达式，无d20)\n.ri#1-9+表达式 人物名 (投掷多次)\n.ri.b                (奖励骰：投掷 d20 2次取高)\n.ri.p                (惩罚骰：投掷 d20 2次取低)\n.ri.b3 20            (奖励骰：投掷 d20 3次取高)\n.ri.p3 20            (惩罚骰：投掷 d20 3次取低)\n.ri.b +2             (奖励骰：投掷 d20+2 2次取高)\n.ri.p3+2             (惩罚骰：投掷 d20+2 3次取低)\n.ri.b3 d20+5 张三    (奖励骰：投掷 d20+5 3次取高，添加为张三)" }
        ,{ "InitiativeExpressionError", "先攻表达式 '{0}' 无效。请使用数字或骰式表达式（如：20、5、d20、d20+3）。" }
        ,{ "InitiativeGroupOnly", "此指令仅在群聊中可用。" }
        ,{ "InitiativeRollResult", "⚔️ {ManName} 投掷先攻:\n{RollDetail}\n✓ [{InitValue}] 已加入先攻列表\n\n【当前先攻列表】\n{ListDisplay}" }
        ,{ "InitiativeListEntryFormat", "{Rank}. {Name} - {Value}" }
        ,{ "InitiativeMultiRollResult", "⚔️ {ManName} 投掷先攻 x{Times}:\n{RollsDetail}\n\n【当前先攻列表】\n{ListDisplay}" }
        ,{ "InitiativeRollItem", "  {Index}. {RollDetail} = {InitValue}" }
        ,{ "InitiativeNameDuplicate", "（已存在相同名称，记录为 '{ActualName}'）" }
        ,{ "InitiativeListEmpty", "（先攻列表为空）" }
        ,{ "InitiativeValueInvalid", "先攻值无效。计算结果为 {Value}，应为正整数。" },

        //群退出消息
        { "LeaveGroupMessage", "走啦走啦，拜拜！" },

        // .ww 双重十字掷骰超过15轮上限时的彩蛋
        { "WwRollLimitReached", "骰子都快被你扔光了，回路都快过载了！适可而止啊！#ﾟÅﾟ）⊂彡☆))ﾟДﾟ)･∵" }

    };
    /// <summary>
    /// 临时疯狂效果表（总结疯狂表）
    /// </summary>
    public static readonly Dictionary<string, string> TempInsanityTable = new()
        {
            { "失忆（Amnesia）", "回过神来，调查员们发现自己身处一个陌生的地方，并忘记了自己是谁。记忆会随时间恢复。" },

            { "被窃（Robbed）", "调查员在<dice 1d10>小时后恢复清醒，发觉自己被盗，身体毫发无损。如果调查员携带着宝贵之物（见调查员背景），做幸运检定来决定其是否被盗。所有有价值的东西无需检定自动消失。" },

            { "遍体鳞伤（Battered）", "调查员在<dice 1d10>小时后恢复清醒，发现自己身上满是拳痕和瘀伤。生命值减少到疯狂前的一半，但这不会造成重伤。调查员没有被窃。这种伤害如何持续到现在由守秘人决定。" },

            { "暴力倾向（Violence）", "调查员陷入强烈的暴力与破坏欲之中。调查员回过神来可能会理解自己做了什么也可能毫无印象。调查员对谁或何物施以暴力，他们是杀人还是仅仅造成了伤害，由守秘人决定。" },

            { "极端信念（Ideology/Beliefs）", "查看调查员背景中的思想信念，调查员会采取极端和疯狂的表现手段展示他们的思想信念之一。比如一个信教者会在地铁上高声布道。" },

            { "重要之人（Significant People）", "考虑调查员背景中的重要之人，及其重要的原因。在<dice 1d10>小时或更久的时间中，调查员将不顾一切地接近那个人，并为他们之间的关系做出行动。" },

            { "被收容（Institutionalized）", "调查员在精神病院病房或警察局牢房中回过神来，他们可能会慢慢回想起导致自己被关在这里的事情。" },
            
            { "逃避行为（Flee in panic）", "调查员恢复清醒时发现自己在很远的地方，也许迷失在荒郊野岭，或是在驶向远方的列车或长途汽车上。" },

            { "恐惧（Phobia）", "调查员患上一个新的恐惧症状。在表Ⅸ：恐惧症状表上骰D100来决定症状，或由守秘人选择一个。调查员在<dice 1d10>小时后回过神来，并开始为避开恐惧源而采取任何措施。" },

            { "狂躁（Mania）", "调查员患上一个新的狂躁症状。在表Ⅹ：狂躁症状表上骰D100来决定症状，或由守秘人选择一个。调查员会在<dice 1d10>小时后恢复理智。在这次疯狂发作中，调查员将完全沉浸于其新的狂躁症状。这症状是否会表现给旁人则取决于守秘人和此调查员。" }
        };

    /// <summary>
    /// 即时疯狂发作表（表Ⅶ）
    /// </summary>
    public static readonly Dictionary<string, string> InstantInsanityTable = new()
        {
            { "1-失忆", "<dice 1d10>轮后，调查员发现自己只记得最后身处的安全地点，却没有任何来到这里的记忆。例如，调查员前一刻还在家中吃着早饭，下一刻就已经直面着不知名的怪物。" },

            { "2-假性残疾", "调查员陷入了心理性的失明、失聪或躯体缺失感中，持续<dice 1d10>轮。" },

            { "3-暴力倾向", "调查员陷入了六亲不认的暴力行为中，对周围的敌人与友方进行着无差别的攻击，持续<dice 1d10>轮。" },

            { "4-偏执", "调查员陷入了严重的偏执妄想之中，持续<dice 1d10>轮。所有人都想要伤害他们；没有人可以信任；他们正在被监视；有人背叛了他们；他们见到的都是诡计。" },

            { "5-人际依赖", "守秘人适当参考调查员的背景中重要之人的条目，调查员因为一些原因而将他人误认为了他重要的人，考虑他们的关系性质，调查员会据此行动，持续<dice 1d10>轮。" },

            { "6-昏厥", "调查员当场昏倒，并需要<dice 1d10>轮才能苏醒。" },

            { "7-逃避行为", "调查员会用任何的手段试图逃离现在所处的位置，即使这意味着开走唯一一辆交通工具并将其它人抛诸脑后，调查员会试图逃离<dice 1d10>轮。" },

            { "8-竭嘶底里", "调查员表现出大笑、哭泣、嘶吼、害怕等的极端情绪表现，持续<dice 1d10>轮。" },

            { "9-恐惧", "调查员投一个D100或者由守秘人选择，来从恐惧症状表中选择一个恐惧源，就算这一恐惧源并不存在，调查员也会在接下来的<dice 1d10>轮内想象它存在。" },

            { "10-躁狂", "调查员投一个D100或者由守秘人选择，来从躁狂症状表中选择一个躁狂的诱因，在接下来的<dice 1d10>轮内，调查员会渴望沉溺于他新的躁狂症中。" }
        };


    /// <summary>
    /// 默认帮助模板
    /// </summary>
    private static readonly Dictionary<string, string> defaultHelpTemplates = new()
    {
        // 总览（除 help）：所有指令均以 "." 开头，由 [`MessageProcessor.OnHandleMessage()`](MDiceV2.Core/Models/MessageProcessor_CommandHandlers.cs:28) 统一读取消息文本、检查是否应忽略(@检测/Bot状态)，再按前缀表 bot/r/st/sc/cc/log/rule/dismiss/name/com 等进行最长匹配，命中后将去除前缀与点号后的剩余参数传入对应 HandleXXX 方法处理，实现集中路由、分发到各功能指令。
        { "default", "指令总览（除.help）：统一以\".\"+前缀触发，由核心路由匹配前缀并调用对应处理函数。当前支持：.r 掷骰表达式；.st 创建/更新人物卡与技能；.sc 理智检定并自动扣减SAN；.cc 在CoC7/ET等模式下做通用检定与循环检定；.log 管理跑团日志文件；.name 绑定用户显示名；.com 查看当前人物卡与模式；.as 以指定成员身份执行指令；()与{}为语义参数，需按示例保留。示例：.r 1d100；.st(惠惠)侦查70 聆听60；.sc(惠惠)1/1d6 60；.cc{coc7}(惠惠)侦查80-l；.log on 惠惠本；.name 惠惠；.com coc。" },

        // .r 掷骰
        { "roll", "【.r 掷骰】：以 .r 开头，后接标准掷骰表达式或算式（如 1d100、3d6+2、1d100<=50 等），由核心掷骰与表达式解析模块计算并返回详解结果，用于基础检定、伤害或判定。支持暗骰模式：.rh 或 .r h，结果通过私聊发送。示例：.r 1d100；.r 2d6+3；.r 1d100<=50；.rh 1d100；.r h 1d100。" },

        // .st 人物卡与技能
        { "st", "【.st 人物卡与技能】：识别 .st(可选人物名) 起始，先解析紧随的 {type:xxx}{cocformat:xxx} 等配置块，再从左到右读取“技能名+数值/骰式”对。技能名不得含数字，数值可为整数或XdY并支持前缀+/-作为相对变动；若写掷骰表达式则自动掷骰取结果后入库。内部通过用户ID与人物名定位或创建人物卡，并更新 Skills 字典。示例：.st(惠惠){type:coc7}侦查70 聆听60 信用80；.st侦查+5 聆听1d10。" },

        // .sc 理智检定
        { "sc", "【.sc 理智检定】：匹配 .sc(可选人物名) 掷骰1/掷骰2 [可选临时SAN]。从人物卡读取“理智”值，若缺失则尝试用“意志”同步为理智；进行 1d100 检定，成功时按前式表达式结算损失，失败按后式表达式结算，并自动回写新的理智值。支持直接指定临时SAN覆盖当前上限，用于短期疯狂场景。示例：.sc(惠惠)1/1d6 60；.sc 1/1d10。" },

        // .cc 通用检定（CoC/ET）
        { "cc", "【.cc 通用检定】：(CoC/ET）】：识别 .cc{模式}(人物卡)[主干部分][-附指令]，模式当前支持 coc7/et，缺省则默认 coc7。主干部分通过解析“技能/数值表达式”等元素，对应人物卡技能值执行单次或多次检定。支持 -l 循环模式按顺序处理多段表达式，也支持续写上一次检定上下文，便于复杂连检。示例：.cc{coc7}(惠惠)侦查80 聆听60；.cc{et}力量；.cc{coc7}(惠惠)侦查80-l。" },

        // .bot 机器人控制
        { "bot", "【.bot 骰娘控制】：群聊与私聊均可使用。以 .bot 开头，识别 on/off/空指令：.bot on 开启当前会话（群或私聊）的指令响应，.bot off 关闭，关闭状态下除 .bot 本身外其他指令会被忽略并记录原因；单独 .bot 输出当前状态、版本与当前信任度。示例：.bot on；.bot off；.bot。" },

        // .log 跑团日志
        { "log", "【.log 日志】：在群聊使用 .log 开头。支持 .log on [日志名] 开启并绑定当前群的跑团日志、.log off 关闭记录、.log get[日志名] 获取或上传日志文件、.log list 列出可用日志（含本群日志与你的个人日志）、.log review[日志名] 回顾最近50条日志记录（支持跨群索引你的日志）。日志写入时会清理CQ码并记录玩家/GM 名称，便于导出复盘。示例：.log on 惠惠本；.log off；.log get 惠惠本；.log list；.log review 惠惠本。" },

        // .rule 规则查询（保留入口）
        { "rule", "【.rule 规则入口】：以 .rule 前缀识别规则查询/说明类子命令（如按规则书关键字检索、解释专用术语等），当前作为预留扩展点，实际行为视版本实现，可统一挂载多规则模块。示例：.rule coc7 闪避；.rule dnd 优势。" },

        // .dismiss 终止/解除（保留入口）
        { "dismiss", "【.dismiss 终止/解除】：匹配 .dismiss 前缀，用于结束特定会话、关闭功能或撤销部分绑定等控制性操作，避免与业务指令混用，具体子语义由实际实现决定。示例：.dismiss log；.dismiss com。" },

        // .name 用户显示名
        { "name", "【.name 名称绑定】：以 .name 开头。无参数时查询当前账号绑定名称；传入文本则记录；使用 reset/clear/off 时清除绑定（保存为空）。用于在日志、人物卡输出中统一显示称呼。示例：.name 惠惠；.name reset；.name。" },

        // .com 模式与人物卡查看
        { "com", "【.com 模式/人物卡查看】：匹配 .com 后可选 coc/et/dnd。内部获取当前人物卡，若无参数则直接输出人物卡详情；指定模式关键字时作为模式意图校验入口并输出当前卡数据，为后续按模式切换人物卡视图和扩展多规则支持做准备。示例：.com；.com coc；.com et。" },

        // .as 代执行
        { "as", "【.as 代执行】：以 .as 开头，后接目标成员的@CQ码与实际指令，按目标成员身份执行后续指令（仅管理权限可用）。格式：.as [CQ:at,qq=123456] [.指令]。示例：.as [CQ:at,qq=123456] .r 1d100。" },

        // .gc 角色属性生成
        { "gc", "【.gc 角色属性生成】：以 .gc 开头，后接可选的模式参数（coc/dnd/et）与行数。格式为 .gc [模式] [行数]，其中模式默认为 coc，行数为 1-20 间的整数（默认1）。" }
    };

    /// <summary>
    /// 数据IO管理器
    /// </summary>
    private static DataIO? _dataIO;

    /// <summary>
    /// 初始化数据IO
    /// </summary>
    /// <param name="dataIO">数据IO实例</param>
    public static void InitializeDataIO(DataIO dataIO)
    {
        _dataIO = dataIO;
        LogSender.InfoFormat($"[GlobalFeedbackMessages初始化] ========== 开始初始化 ==========");
        LoadTemplates();
        Log.Warn("[GlobalFeedbackMessages] DataIO initialized");
        LoadHelpTemplates();
        LogSender.InfoFormat($"[GlobalFeedbackMessages初始化] 开始加载BasicSettings");
        LoadBasicSettings();
        LogSender.InfoFormat($"[GlobalFeedbackMessages初始化] ========== 初始化完成 ==========");
            
        // 【修复】触发初始化完成事件，让UI重新加载已保存的配置
        try
        {
            OnInitializationComplete?.Invoke();
            LogSender.InfoFormat($"[GlobalFeedbackMessages初始化] 【修复】已触发OnInitializationComplete事件");
        }
        catch (Exception ex)
        {
            LogSender.Error($"[GlobalFeedbackMessages初始化] 【修复】触发OnInitializationComplete失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从数据库加载模板
    /// </summary>
    public static void LoadTemplates()
    {
        if (_dataIO == null)
        {
            Log.Warn("DataIO is null, cannot load templates");
            return;
        }

        try
        {
            var savedTemplatesJson = _dataIO.ReadData("BinaryJsonData", "FeedbackTemplate");
            //Log.InfoFormat($"[GlobalFeedbackMessages] Raw JSON from database: {savedTemplatesJson ?? "null"}");

            if (!string.IsNullOrEmpty(savedTemplatesJson))
            {
                // 尝试反序列化之前先验证JSON格式
                try
                {
                    var savedTemplates = JsonSerializer.Deserialize<Dictionary<string, string>>(savedTemplatesJson);
                    if (savedTemplates != null && savedTemplates.Count > 0)
                    {
                        //Log.InfoFormat($"[GlobalFeedbackMessages] Successfully deserialized {savedTemplates.Count} saved templates");

                        // 逐一检测默认反馈模板中的键，使用savedTemplates中的值，如果不存在则使用默认值
                        var mergedTemplates = new Dictionary<string, string>();
                        int loadedCount = 0;
                        int defaultCount = 0;

                        foreach (var kvp in defaultFeedbackTemplates)
                        {
                            if (savedTemplates.TryGetValue(kvp.Key, out var savedValue) && !string.IsNullOrEmpty(savedValue))
                            {
                                mergedTemplates[kvp.Key] = IsLegacyDefaultTemplate(kvp.Key, savedValue)
                                    ? kvp.Value
                                    : savedValue;
                                //Log.InfoFormat($"[GlobalFeedbackMessages] Using saved template for key: {kvp.Key} = '{savedValue}'");
                                loadedCount++;
                            }
                            else
                            {
                                mergedTemplates[kvp.Key] = kvp.Value;
                                //Log.InfoFormat($"[GlobalFeedbackMessages] Using default template for key: {kvp.Key}");
                                defaultCount++;
                            }
                        }

                        FeedbackTemplates = new FeedbackDictionary(defaultFeedbackTemplates, mergedTemplates);
                        //Log.InfoFormat("[GlobalFeedbackMessages] Loaded {loadedCount} saved templates and {defaultCount} default templates from database");
                        RaiseFeedbackTemplatesLoaded();
                        return;
                    }
                    else
                    {
                        //Log.Warn("[GlobalFeedbackMessages] Deserialized template dictionary is null or empty");
                    }
                }
                catch (JsonException jsonEx)
                {
                    Log.Error($"[GlobalFeedbackMessages] JSON deserialization failed: {jsonEx.Message}");
                    Log.Error($"[GlobalFeedbackMessages] Failed JSON content: {savedTemplatesJson}");
                }
            }
            else
            {
                Log.InfoFormat("[GlobalFeedbackMessages] No saved template data found in database, using defaults");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[GlobalFeedbackMessages] Failed to load feedback message templates from database: {ex.Message}\n{ex.StackTrace}");
        }

        // 如果加载失败，使用默认反馈模板
        FeedbackTemplates = new FeedbackDictionary(defaultFeedbackTemplates);
        Log.InfoFormat("[GlobalFeedbackMessages] Using default feedback message templates due to load failure");

        Log.Error("[GlobalFeedbackMessages]ERRRRRORRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRR");
        // 触发UI更新事件
        RaiseFeedbackTemplatesLoaded();
    }

    /// <summary>
    /// Reloads both template collections from the active database without restarting the bot.
    /// </summary>
    public static void ReloadTemplatesFromDatabase()
    {
        if (_dataIO == null)
        {
            Log.Warn("DataIO is null, cannot reload templates");
            return;
        }

        LoadTemplates();
        LoadHelpTemplates();
        Log.Normal("[GlobalFeedbackMessages] Templates reloaded from database");
    }

    private static bool IsLegacyDefaultTemplate(string key, string value)
    {
        if (key != "CoCCheckResult")
        {
            return false;
        }

        return value == "{4}的检定:D100={0}/{1}{2} -> {3}"
               || value == "{4}鐨勬瀹?D100={0}/{1}{2} -> {3}";
    }

    // 添加UI更新事件 - 使用通用Action，避免循环依赖
    public static event Action? FeedbackTemplatesLoaded;
    public static event Action? HelpTemplatesLoaded;

    /// <summary>
    /// 触发Feedback Templates UI更新
    /// </summary>
    private static void RaiseFeedbackTemplatesLoaded()
    {
        FeedbackTemplatesLoaded?.Invoke();
    }

    /// <summary>
    /// 触发Help Templates UI更新
    /// </summary>
    private static void RaiseHelpTemplatesLoaded()
    {
        HelpTemplatesLoaded?.Invoke();
    }

    /// <summary>
    /// 从数据库加载帮助模板
    /// </summary>
    private static void LoadHelpTemplates()
    {
        if (_dataIO == null)
        {
            Log.Warn("DataIO is null, cannot load help templates");
            return;
        }

        try
        {
            var savedHelpTemplatesJson = _dataIO.ReadData("BinaryJsonData", "HelpTemplates");
            //Log.InfoFormat($"[GlobalFeedbackMessages] Raw JSON from database for help templates: {savedHelpTemplatesJson ?? "null"}");

            if (!string.IsNullOrEmpty(savedHelpTemplatesJson))
            {
                // 尝试反序列化之前先验证JSON格式
                try
                {
                    var savedHelpTemplates = JsonSerializer.Deserialize<Dictionary<string, string>>(savedHelpTemplatesJson);
                    if (savedHelpTemplates != null && savedHelpTemplates.Count > 0)
                    {
                        Log.InfoFormat($"[GlobalFeedbackMessages] Successfully deserialized {savedHelpTemplates.Count} saved help templates");

                        // 逐一检测默认帮助模板中的键，使用savedHelpTemplates中的值，如果不存在则使用默认值
                        var mergedHelpTemplates = new Dictionary<string, string>();
                        int loadedCount = 0;
                        int defaultCount = 0;

                        foreach (var kvp in defaultHelpTemplates)
                        {
                            if (savedHelpTemplates.TryGetValue(kvp.Key, out var savedValue) && !string.IsNullOrEmpty(savedValue))
                            {
                                mergedHelpTemplates[kvp.Key] = savedValue;
                                Log.InfoFormat($"[GlobalFeedbackMessages] Using saved help template for key: {kvp.Key} = '{savedValue}'");
                                loadedCount++;
                            }
                            else
                            {
                                mergedHelpTemplates[kvp.Key] = kvp.Value;
                                Log.InfoFormat($"[GlobalFeedbackMessages] Using default help template for key: {kvp.Key}");
                                defaultCount++;
                            }
                        }

                        HelpTemplates = new FeedbackDictionary(defaultHelpTemplates, mergedHelpTemplates);
                        Log.InfoFormat($"[GlobalFeedbackMessages] Loaded {loadedCount} saved help templates and {defaultCount} default help templates from database");
                        RaiseHelpTemplatesLoaded();
                        return;
                    }
                    else
                    {
                        Log.Warn("[GlobalFeedbackMessages] Deserialized help template dictionary is null or empty");
                    }
                }
                catch (JsonException jsonEx)
                {
                    Log.Error($"[GlobalFeedbackMessages] JSON deserialization failed for help templates: {jsonEx.Message}");
                    Log.Error($"[GlobalFeedbackMessages] Failed JSON content: {savedHelpTemplatesJson}");
                }
            }
            else
            {
                Log.InfoFormat("[GlobalFeedbackMessages] No saved help template data found in database, using defaults");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[GlobalFeedbackMessages] Failed to load help feedback message templates from database: {ex.Message}\n{ex.StackTrace}");
        }

        // 如果加载失败，使用默认帮助模板
        HelpTemplates = new FeedbackDictionary(defaultHelpTemplates);
        Log.InfoFormat("[GlobalFeedbackMessages] Using default help feedback message templates due to load failure");

        // 触发UI更新事件
    }

    /// <summary>
    /// 保存模板到数据库
    /// </summary>
    public static void SaveTemplates()
    {
        Log.InfoFormat("[GlobalFeedbackMessages] 开始保存反馈消息模板...");

        if (_dataIO == null)
        {
            Log.Error("[GlobalFeedbackMessages] 无法保存反馈消息模板: DataIO 为 null");
            return;
        }

        try
        {
            // 保存所有反馈模板，包括自定义和默认的（为了完整性）
            var allTemplates = new Dictionary<string, string>();
            foreach (var kvp in FeedbackTemplates)
            {
                allTemplates[kvp.Key] = kvp.Value;
                Log.InfoFormat($"[GlobalFeedbackMessages] 准备保存模板: {kvp.Key} = {kvp.Value}");
            }

            Log.InfoFormat($"[GlobalFeedbackMessages] 准备保存 {allTemplates.Count} 条反馈消息模板");
            var templatesJson = JsonSerializer.Serialize(allTemplates);

            // 在保存前先读取当前值进行对比
            var currentJson = _dataIO.ReadData("BinaryJsonData", "FeedbackTemplate");
            Log.InfoFormat($"[GlobalFeedbackMessages] 当前数据库中的模板: {(string.IsNullOrEmpty(currentJson) ? "无" : "存在")}");

            _dataIO.SaveData("BinaryJsonData", "FeedbackTemplate", templatesJson);

            // 验证保存是否成功
            var savedJson = _dataIO.ReadData("BinaryJsonData", "FeedbackTemplate");
            if (savedJson == templatesJson)
            {
                Log.InfoFormat($"[GlobalFeedbackMessages] 成功保存 {allTemplates.Count} 条反馈消息模板到数据库");
            }
            else
            {
                Log.Error("[GlobalFeedbackMessages] 保存后的数据验证失败，可能未正确保存");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[GlobalFeedbackMessages] 保存反馈消息模板时发生错误: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 保存帮助模板到数据库
    /// </summary>
    public static void SaveHelpTemplates()
    {
        Log.InfoFormat("[GlobalFeedbackMessages] 开始保存帮助消息模板...");

        if (_dataIO == null)
        {
            Log.Error("[GlobalFeedbackMessages] 无法保存帮助消息模板: DataIO 为 null");
            return;
        }

        try
        {
            // 保存所有帮助模板，包括自定义和默认的
            var allHelpTemplates = new Dictionary<string, string>();
            foreach (var kvp in HelpTemplates)
            {
                allHelpTemplates[kvp.Key] = kvp.Value;
            }

            Log.InfoFormat($"[GlobalFeedbackMessages] 准备保存 {allHelpTemplates.Count} 条帮助消息模板");
            var helpTemplatesJson = JsonSerializer.Serialize(allHelpTemplates);

            // 在保存前先读取当前值进行对比
            var currentJson = _dataIO.ReadData("BinaryJsonData", "HelpTemplates");
            Log.InfoFormat($"[GlobalFeedbackMessages] 当前数据库中的帮助模板: {(string.IsNullOrEmpty(currentJson) ? "无" : "存在")}");

            _dataIO.SaveData("BinaryJsonData", "HelpTemplates", helpTemplatesJson);

            // 验证保存是否成功
            var savedJson = _dataIO.ReadData("BinaryJsonData", "HelpTemplates");
            if (savedJson == helpTemplatesJson)
            {
                Log.InfoFormat($"[GlobalFeedbackMessages] 成功保存 {allHelpTemplates.Count} 条帮助消息模板到数据库");
            }
            else
            {
                Log.Error("[GlobalFeedbackMessages] 保存后的帮助模板验证失败，可能未正确保存");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[GlobalFeedbackMessages] 保存帮助消息模板时发生错误: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 反馈模板字典
    /// </summary>
    public static FeedbackDictionary FeedbackTemplates { get; set; } = new(defaultFeedbackTemplates);

    /// <summary>
    /// 帮助模板字典
    /// </summary>
    public static FeedbackDictionary HelpTemplates { get; set; } = new(defaultHelpTemplates);

    /// <summary>
    /// 获取默认反馈模板
    /// </summary>
    /// <returns>默认反馈模板字典的副本</returns>
    public static Dictionary<string, string> GetDefaultFeedbackTemplates() => new(defaultFeedbackTemplates);

    /// <summary>
    /// 获取默认帮助模板
    /// </summary>
    /// <returns>默认帮助模板字典的副本</returns>
    public static Dictionary<string, string> GetDefaultHelpTemplates() => new(defaultHelpTemplates);

    /// <summary>
    /// 重置为默认值
    /// </summary>
    public static void ResetToDefault()
    {
        FeedbackTemplates = new(defaultFeedbackTemplates);
        HelpTemplates = new(defaultHelpTemplates);
    }

    // ============ 基础设置管理 ============

    /// <summary>
    /// 基础设置字典
    /// Key为设置名称，Value为设置值
    /// </summary>
    // 基础设置键值采用不区分大小写的比较器，避免 UI 端大小写不一致导致读取失败
    private static Dictionary<string, string> _basicSettings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> defaultBasicSettings = new()
    {
        ["Url"] = "ws://localhost:8080",
        ["Master"] = "",
        ["MasterGroup"] = "",
        ["ApproveFriendJoinRequest"] = "false",
        ["ApproveGroupJoinRequest"] = "false",
        ["SendGroupJoinReport"] = "false",
        ["SendFriendJoinReport"] = "false"
    };

    // 规范化基础设置键：去掉空格并统一大小写比较
    private static string NormalizeBasicKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        return key.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// 获取基础设置值 - 如果内存中为空，尝试从数据库重新加载
    /// </summary>
    /// <param name="key">设置键</param>
    /// <returns>设置值，如果不存在则返回空字符串</returns>
    public static string GetBasicSetting(string key)
    {
        var normalizedKey = NormalizeBasicKey(key);

        string? value;
        if (_basicSettings.TryGetValue(normalizedKey, out value))
        {
            // 如果值为空但在数据库中有值，则从数据库加载
            if (string.IsNullOrEmpty(value) && _dataIO != null)
            {
                try
                {
                    var dbValue = _dataIO.ReadData("BasicSetting", normalizedKey);
                    if (!string.IsNullOrEmpty(dbValue))
                    {
                        _basicSettings[normalizedKey] = dbValue;
                        Log.InfoFormat($"[GetBasicSetting] 从数据库恢复 '{normalizedKey}' = '{dbValue}'");
                        return dbValue;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[GetBasicSetting] 尝试从数据库加载失败: {ex.Message}");
                }
            }
            return value;
        }

        // 如果内存中不存在，尝试从数据库加载
        if (_dataIO != null)
        {
            try
            {
                var dbValue = _dataIO.ReadData("BasicSetting", normalizedKey);
                if (!string.IsNullOrEmpty(dbValue))
                {
                    _basicSettings[normalizedKey] = dbValue;
                    Log.InfoFormat($"[GetBasicSetting] 从数据库新加载 '{normalizedKey}' = '{dbValue}'");
                    return dbValue;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[GetBasicSetting] 从数据库加载失败: {ex.Message}");
            }
        }

        // 如果数据库中也没有值，回退到默认值
        if (defaultBasicSettings.TryGetValue(normalizedKey, out var defaultValue))
        {
            Log.InfoFormat($"[GetBasicSetting] 使用默认值 '{normalizedKey}' = '{defaultValue}'");
            return defaultValue;
        }

        return string.Empty;
    }

    /// <summary>
    /// 设置基础设置值 - 同时同步到数据库以确保数据一致性
    /// </summary>
    /// <param name="key">设置键</param>
    /// <param name="value">设置值</param>
    public static void SetBasicSetting(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var normalizedKey = NormalizeBasicKey(key);
        _basicSettings[normalizedKey] = value ?? string.Empty;

        // 立即同步到数据库，避免内存与数据库不一致
        if (_dataIO != null)
        {
            try
            {
                _dataIO.SaveData("BasicSetting", normalizedKey, value ?? string.Empty);
                Log.InfoFormat($"[SetBasicSetting] 已同步 '{normalizedKey}' = '{value}' 到数据库");
            }
            catch (Exception ex)
            {
                Log.Warn($"[SetBasicSetting] 同步到数据库失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 获取所有基础设置
    /// </summary>
    /// <returns>基础设置字典的副本</returns>
    public static Dictionary<string, string> GetAllBasicSettings()
    {
        // 以规范化后的键返回副本，避免外部代码因大小写或空格差异拿到重复键
        return new Dictionary<string, string>(_basicSettings, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从数据库加载基础设置
    /// </summary>
    private static void LoadBasicSettings()
    {
        LogSender.InfoFormat("[BasicConfig数据库加载] ========== 开始从数据库加载BasicSettings ==========");
        if (_dataIO == null) 
        {
            LogSender.Warn("[BasicConfig数据库加载] _dataIO 为空，无法加载");
            return;
        }

        try
        {
            var savedSettings = _dataIO.ReadAllData("BasicSetting");
            LogSender.Normal($"[BasicConfig数据库加载] 从数据库读取 {savedSettings.Count} 条记录");
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in savedSettings)
            {
                var normalizedKey = NormalizeBasicKey(kvp.Key);
                
                // 跳过旧的 wsUrl 键（直接忽略，不加载到内存）
                if (normalizedKey == "wsurl")
                {
                    Log.InfoFormat($"Skipped deprecated basic setting from database: {kvp.Key} (old wsUrl key)");
                    continue;
                }
                
                // 只加载非空值，避免空值覆盖已有的有效值
                if (!string.IsNullOrWhiteSpace(kvp.Value))
                {
                    Log.InfoFormat($"Loaded basic setting from database: {kvp.Key} -> {normalizedKey} = {kvp.Value}");
                    normalized[normalizedKey] = kvp.Value;
                }
                else
                {
                    Log.InfoFormat($"Skipped empty basic setting from database: {kvp.Key}");
                }
            }

            // 如果已经有内存中的值，先合并，避免丢失未保存的更改
            if (_basicSettings != null && _basicSettings.Count > 0)
            {
                foreach (var kvp in _basicSettings)
                {
                    if (!normalized.ContainsKey(kvp.Key))
                    {
                        normalized[kvp.Key] = kvp.Value;
                        Log.InfoFormat($"Preserved in-memory setting: {kvp.Key} = {kvp.Value}");
                    }
                }
            }

            _basicSettings = normalized;
            LogSender.InfoFormat($"[BasicConfig数据库加载] 规范化后 {_basicSettings.Count} 条记录已加载到内存");

            // 确保必要的键存在，如果不存在则设置默认值
            EnsureDefaultBasicSettings();

            // 迁移旧的 wsUrl 键到 Url
            MigrateWsUrlToUrl();

            // 输出最终结果以供调试
            LogSender.InfoFormat("[BasicConfig数据库加载] 最终加载的设置:");
            foreach (var kvp in _basicSettings)
            {
                LogSender.InfoFormat($"  {kvp.Key} = {kvp.Value}");
            }
            
            LogSender.InfoFormat("[BasicConfig数据库加载] ========== 数据库加载完成 ==========");
        }
        catch (Exception ex)
        {
            LogSender.Error($"[BasicConfig数据库加载] 加载失败: {ex.Message}");
            if (_basicSettings == null)
            {
                _basicSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            EnsureDefaultBasicSettings();
        }
    }

    /// <summary>
    /// 确保必要的默认基础设置存在
    /// </summary>
    private static void EnsureDefaultBasicSettings()
    {
        foreach (var kvp in defaultBasicSettings)
        {
            var normalizedKey = NormalizeBasicKey(kvp.Key);
            if (!_basicSettings.ContainsKey(normalizedKey))
            {
                _basicSettings[normalizedKey] = kvp.Value;
            }
        }
    }

    /// <summary>
    /// 迁移旧的 wsUrl 键到 Url（数据库键重复修复）
    /// 检查是否存在旧的 "wsUrl" 键，如果存在且 "Url" 为默认值，则将其值迁移过来
    /// </summary>
    private static void MigrateWsUrlToUrl()
    {
        if (_dataIO == null) return;

        try
        {
            // 尝试从数据库读取旧的 wsUrl 键
            var oldWsUrlValue = _dataIO.ReadData("BasicSetting", "wsUrl");

            if (!string.IsNullOrEmpty(oldWsUrlValue))
            {
                var currentUrlValue = GetBasicSetting("Url");
                var isCurrentUrlDefault = currentUrlValue == "ws://localhost:8080" || currentUrlValue == "ws://localhost:8080111";

                Log.InfoFormat($"[MigrateWsUrlToUrl] 检测到旧的 wsUrl 键: '{oldWsUrlValue}'");
                Log.InfoFormat($"[MigrateWsUrlToUrl] 当前 Url 值: '{currentUrlValue}' (是默认值: {isCurrentUrlDefault})");

                // 如果新键的值是错误的或为默认值，使用旧键的值
                if (isCurrentUrlDefault && oldWsUrlValue != "ws://localhost:8080111")
                {
                    Log.InfoFormat($"[MigrateWsUrlToUrl] 将 wsUrl 的值迁移到 Url: {oldWsUrlValue}");
                    SetBasicSetting("Url", oldWsUrlValue);
                }

                // 清除旧的 wsUrl 键（通过设置为空值）
                Log.InfoFormat("[MigrateWsUrlToUrl] 清除旧的 wsUrl 键");
                try
                {
                    _dataIO.SaveData("BasicSetting", "wsUrl", "");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[MigrateWsUrlToUrl] 清除旧键失败: {ex.Message}");
                }

                // 同时从内存的 _basicSettings 中移除
                _basicSettings.Remove("wsUrl");
                _basicSettings.Remove("wsurl");

                Log.InfoFormat("[MigrateWsUrlToUrl] 迁移完成");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[MigrateWsUrlToUrl] 迁移失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取默认基础设置
    /// </summary>
    public static Dictionary<string, string> GetDefaultBasicSettings()
    {
        var defaults = new Dictionary<string, string>(defaultBasicSettings);
        LogSender.InfoFormat($"[GetDefaultBasicSettings] 返回默认值，共{defaults.Count}项");
        foreach (var kvp in defaults)
        {
            LogSender.InfoFormat($"  {kvp.Key} = {kvp.Value}");
        }
        return defaults;
    }

    /// <summary>
    /// 调试方法：检查数据库中的模板数据
    /// </summary>
    public static void DebugDatabaseTemplates()
    {
        if (_dataIO == null)
        {
            Log.Warn("DataIO is null, cannot debug database templates");
            return;
        }

        try
        {
            // 检查反馈模板
            var feedbackJson = _dataIO.ReadData("BinaryJsonData", "FeedbackTemplate");
            Log.InfoFormat("[DEBUG] Feedback template JSON from database:");
            Log.InfoFormat("[DEBUG] " + (feedbackJson ?? "null"));

            if (!string.IsNullOrEmpty(feedbackJson))
            {
                try
                {
                    var feedbackTemplates = JsonSerializer.Deserialize<Dictionary<string, string>>(feedbackJson);
                    if (feedbackTemplates != null)
                    {
                        Log.InfoFormat("[DEBUG] Deserialized " + feedbackTemplates.Count + " feedback templates from database");
                        foreach (var kvp in feedbackTemplates)
                        {
                            Log.InfoFormat("[DEBUG] Saved template: '" + kvp.Key + "' = '" + kvp.Value + "'");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[DEBUG] Failed to deserialize feedback templates: " + ex.Message);
                }
            }

            // 检查帮助模板
            var helpJson = _dataIO.ReadData("BinaryJsonData", "HelpTemplates");
            Log.InfoFormat("[DEBUG] Help template JSON from database:");
            Log.InfoFormat("[DEBUG] " + (helpJson ?? "null"));

            if (!string.IsNullOrEmpty(helpJson))
            {
                try
                {
                    var helpTemplates = JsonSerializer.Deserialize<Dictionary<string, string>>(helpJson);
                    if (helpTemplates != null)
                    {
                        Log.InfoFormat("[DEBUG] Deserialized " + helpTemplates.Count + " help templates from database");
                        foreach (var kvp in helpTemplates)
                        {
                            Log.InfoFormat("[DEBUG] Saved help template: '" + kvp.Key + "' = '" + kvp.Value + "'");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[DEBUG] Failed to deserialize help templates: " + ex.Message);
                }
            }

            // 检查默认模板
            Log.InfoFormat("[DEBUG] Default feedback templates count: " + defaultFeedbackTemplates.Count);
            Log.InfoFormat("[DEBUG] Default help templates count: " + defaultHelpTemplates.Count);
        }
        catch (Exception ex)
        {
            Log.Error("[DEBUG] Debug database templates failed: " + ex.Message);
        }
    }

    /// <summary>
    /// 检查GlobalFeedbackMessages是否已初始化
    /// </summary>
    public static bool IsInitialized()
    {
        return _dataIO != null;
    }

    /// <summary>
    /// 在初始化完成后，通知UI重新加载已保存的配置
    /// 用于解决 MainViewModel 先于 GlobalFeedbackMessages 初始化的问题
    /// </summary>
    public static Action? OnInitializationComplete { get; set; }

    /// <summary>
    /// 保存基础设置到数据库
    /// </summary>
    public static void SaveBasicSettings()
    {
        Log.InfoFormat("[GlobalFeedbackMessages] 开始保存基础设置...");

        if (_dataIO == null)
        {
            Log.Error("[GlobalFeedbackMessages] 无法保存基础设置: DataIO 为 null");
            return;
        }

        try
        {
            Log.InfoFormat($"[GlobalFeedbackMessages] 准备保存 {_basicSettings.Count} 条基础设置");

            foreach (var setting in _basicSettings)
            {
                var currentValue = _dataIO.ReadData("BasicSetting", setting.Key);
                Log.InfoFormat($"[GlobalFeedbackMessages] 保存设置 '{setting.Key}': 当前值='{currentValue}' -> 新值='{setting.Value}'");

                _dataIO.SaveData("BasicSetting", setting.Key, setting.Value);

                // 验证保存
                var savedValue = _dataIO.ReadData("BasicSetting", setting.Key);
                if (savedValue != setting.Value)
                {
                    Log.Error($"[GlobalFeedbackMessages] 设置 '{setting.Key}' 保存验证失败");
                }
            }

            Log.InfoFormat($"[GlobalFeedbackMessages] 成功保存 {_basicSettings.Count} 条基础设置到数据库");
        }
        catch (Exception ex)
        {
            Log.Error($"[GlobalFeedbackMessages] 保存基础设置时发生错误: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
