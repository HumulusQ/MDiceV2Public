using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MDiceV2.Interfaces;
using MDiceV2.Interfaces.Mod;
using MDiceV2.Models;
using Avalonia.Controls;
using AIMod.UI;
using AIMod.Trpg;
using Polly;

namespace AIMod
{
    public class AIMod : IModPlugin, IConfigurable, INavigationPanelProvider, ISubcommandProvider, ICommandProvider
    {
        public string ModId => "com.humulus.aimod";
        public string ModName => "AI Mod";
        public string Version => "1.0.0";
        public string Author => "Humulus";
        public string Description => "An AI-powered chat mod using Gemini.";

        private readonly IModContext _context;
        private AiConfig _config = null!;
        private readonly Dictionary<long, List<string>> _groupContexts = new();
        private static readonly HttpClient _httpClient = new HttpClient();

        // TRPG Player 组件
        private ChatDatabase? _trpgDb;
        private TeamDataProvider? _teamDataProvider;
        private MessageRouter? _messageRouter;
        private PromptAssembler? _promptAssembler;
        private PostProcessor? _postProcessor;
        private MemoryWatchdog? _memoryWatchdog;
        private AttentionBuffer? _attentionBuffer;
        private TrpgStateCache? _stateCache;
        private StateInterceptor? _stateInterceptor;
        private TrpgContextPipeline? _contextPipeline;
        private LlmCallTracker? _llmCallTracker;

        // 活跃 AI 角色会话 (GroupId -> List<AiCharacterSession>)
        private readonly Dictionary<long, List<AiCharacterSession>> _activeSessions = new();
        private readonly ConcurrentDictionary<long, ActiveGroupApiContext> _activeGroupApiContexts = new();
        private readonly ConcurrentDictionary<long, DateTime> _apiWarningCooldown = new();
        private readonly ConcurrentDictionary<long, UserApiSetting> _userApiSettings = new();
        private readonly ConcurrentDictionary<long, ModelSelectionState> _modelSelectionStates = new();
        private readonly AsyncLocal<long?> _trpgApiGroupScope = new();
        private readonly AsyncLocal<long?> _trpgApiCurrentUserId = new();
        private readonly AsyncLocal<LlmActualUsage?> _lastTrpgActualUsage = new();
        private string _userApiSettingsPath = "";
        private const long TokenWarningStep = 1_000_000;
        private const string ApiSourceUserPrimary = "user-primary";
        private const string ApiSourceUserSub = "user-sub";
        private const string ApiSourceDefaultPrimary = "default-primary";
        private const string ApiSourceDefaultSecondary = "default-secondary";

        private static readonly IReadOnlyList<AIProvider> AvailableAiProviders = new List<AIProvider>
        {
            new AIProvider
            {
                Id = "gemini",
                DisplayName = "Google Gemini",
                Endpoint = "",
                Models = new List<AIModel>
                {
                    new AIModel { DisplayName = "Gemini 2.5 Flash", ModelId = "gemini-2.5-flash" },
                    new AIModel { DisplayName = "Gemini 2.0 Flash", ModelId = "gemini-2.0-flash" }
                }
            },
            new AIProvider
            {
                Id = "zhipu",
                DisplayName = "ZhipuAI",
                Endpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions",
                Models = new List<AIModel>
                {
                    new AIModel { DisplayName = "GLM-4.7 Flash", ModelId = "glm-4.7-flash" }
                }
            },
            new AIProvider
            {
                Id = "siliconflow",
                DisplayName = "SiliconFlow",
                Endpoint = "https://api.siliconflow.cn/v1/chat/completions",
                Models = new List<AIModel>
                {
                    new AIModel { DisplayName = "Qwen3 8B", ModelId = "Qwen/Qwen3-8B" }
                }
            },
            new AIProvider
            {
                Id = "deepseek",
                DisplayName = "DeepSeek",
                Endpoint = "https://api.deepseek.com/v1/chat/completions",
                Models = new List<AIModel>
                {
                    new AIModel { DisplayName = "DeepSeek Chat", ModelId = "deepseek-chat" }
                }
            }
        };

        // Polly 指数退避重试策略
        private static readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy =
            Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .OrResult(r => (int)r.StatusCode >= 500)
                .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

        public AIMod(IModContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            LoadConfig();
            InitializeUserApiSettingsStore();
            LoadUserApiSettings();
            if (_config != null)
            {
                _httpClient.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
            }
        }

        public void OnLoad()
        {
            try
            {
                _context.Log(LogLevel.Info, "[AIMod] OnLoad() started");
                RegisterNavigationPanel();

                // TRPG 组件初始化（指令如 .team/.ai/.logon 需要，不依赖当前模式）
                InitializeTrpgComponents();

                _context.Log(LogLevel.Info, "[AIMod] OnLoad() completed successfully");
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod] Error during OnLoad: {ex.Message}");
            }
        }

        public void OnEnable() { }
        public void OnDisable() { }

        // ── ISubcommandProvider ──

        public string? HandleSubcommand(string parentCommand, string subcommand, string args, object msgObj)
        {
            if (parentCommand == "team")
            {
                return subcommand switch
                {
                    "addai" => HandleTeamAddAi(args, msgObj),
                    "removeai" => HandleTeamRemoveAi(args, msgObj),
                    "listai" => HandleTeamListAi(args, msgObj),
                    _ => null
                };
            }

            if (parentCommand == "log")
            {
                return subcommand switch
                {
                    "on" => HandleLogonCommand(args, msgObj),
                    "off" => HandleLogLifecycleOff(args, msgObj),
                    _ => null
                };
            }

            return null;
        }

        // ── ICommandProvider ──

        public Dictionary<string, Func<string, object, string?>> GetCommandHandlers()
        {
            return new Dictionary<string, Func<string, object, string?>>
            {
                { "ai", HandleAiCommand }
            };
        }

        private string? HandleLogLifecycleOff(string args, object msgObj)
        {
            var result = HandleLogoffCommand(args, msgObj);
            if (result == "当前没有活跃的AI角色。")
                return null;
            return result;
        }

        // ═══════════════════════════════════════════
        //  .team 子指令实现
        // ═══════════════════════════════════════════

        /// <summary>
        /// .team addai [角色名] — 将AI角色添加到当前队伍
        /// 仅GM（队伍创建者）可用
        /// </summary>
        private string? HandleTeamAddAi(string args, object msgObj)
        {
            var msg = (MDiceV2.Models.Msg)msgObj;
            var characterName = args.Trim();
            if (string.IsNullOrEmpty(characterName))
                return "格式：.team addai 角色名";

            if (_teamDataProvider == null || _trpgDb == null)
            {
                InitializeTrpgComponents();
                if (_teamDataProvider == null || _trpgDb == null)
                    return "AI模块未初始化，无法执行此操作。";
            }

            // 获取用户的默认队伍
            var teamName = _teamDataProvider.GetUserDefaultTeamName(msg.GroupId, msg.UserId);
            if (string.IsNullOrEmpty(teamName))
                return "您还没有加入任何队伍。请先使用 .team join 队伍名 加入队伍。";

            var team = _teamDataProvider.GetTeamForGroup(msg.GroupId, teamName);
            if (team == null)
                return $"队伍 '{teamName}' 不存在。";

            // 权限检查：仅GM（创建者）可添加AI角色
            if (team.CreatorId != msg.UserId)
                return "只有队伍创建者（GM）才能添加AI角色。";

            var scope = CreateTrpgScope(team);
            _trpgDb.EnsureTrpgWorldAsync(scope).GetAwaiter().GetResult();

            // 检查角色是否已存在
            var characterId = $"{msg.GroupId}_{teamName}_{characterName}";
            var existing = _trpgDb.GetAiCharacterAsync(scope, characterId).GetAwaiter().GetResult();
            if (existing != null)
                return $"AI角色 '{characterName}' 已在队伍 '{teamName}' 中。";

            // 分配虚拟ID并写入主库 TeamInfo.Members
            var virtualId = _trpgDb.GetNextVirtualIdAsync(scope).GetAwaiter().GetResult();
            if (!AddVirtualIdToMainDb(msg.GroupId, teamName, virtualId))
                return "写入队伍数据失败，请稍后重试。";

            // 写入 AiCharacterEntry
            var entry = new AiCharacterEntry
            {
                WorldId = scope.WorldId,
                CharacterId = characterId,
                VirtualId = virtualId,
                OwnerUserId = scope.OwnerUserId,
                GroupId = msg.GroupId,
                TeamName = teamName,
                DisplayName = characterName,
                StaticBackground = "",
                DynamicStateJson = "{}",
                SkillsJson = "{}",
                InventoryJson = "[]",
                IsActive = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _trpgDb.UpsertAiCharacterAsync(scope, entry).GetAwaiter().GetResult();

            // 刷新 TeamDataProvider 缓存
            _teamDataProvider.InvalidateCache();

            return $"✓ AI角色 '{characterName}' 已加入队伍 '{teamName}'（虚拟ID: {virtualId}）\n" +
                   $"使用 .ai set {characterName} 来配置角色设定";
        }

        /// <summary>
        /// .team removeai [角色名] — 从队伍移除AI角色
        /// 仅GM可用
        /// </summary>
        private string? HandleTeamRemoveAi(string args, object msgObj)
        {
            var msg = (MDiceV2.Models.Msg)msgObj;
            var characterName = args.Trim();
            if (string.IsNullOrEmpty(characterName))
                return "格式：.team removeai 角色名";

            if (_teamDataProvider == null || _trpgDb == null)
            {
                InitializeTrpgComponents();
                if (_teamDataProvider == null || _trpgDb == null)
                    return "AI模块未初始化，无法执行此操作。";
            }

            var teamName = _teamDataProvider.GetUserDefaultTeamName(msg.GroupId, msg.UserId);
            if (string.IsNullOrEmpty(teamName))
                return "您还没有加入任何队伍。";

            var team = _teamDataProvider.GetTeamForGroup(msg.GroupId, teamName);
            if (team == null)
                return $"队伍 '{teamName}' 不存在。";

            if (team.CreatorId != msg.UserId)
                return "只有队伍创建者（GM）才能移除AI角色。";

            var scope = CreateTrpgScope(team);
            _trpgDb.EnsureTrpgWorldAsync(scope).GetAwaiter().GetResult();

            var characterId = $"{msg.GroupId}_{teamName}_{characterName}";
            var existing = _trpgDb.GetAiCharacterAsync(scope, characterId).GetAwaiter().GetResult();
            if (existing == null)
                return $"AI角色 '{characterName}' 不在队伍 '{teamName}' 中。";

            // 从主库 TeamInfo.Members 移除虚拟ID
            if (!RemoveVirtualIdFromMainDb(msg.GroupId, teamName, existing.VirtualId))
                return "移除队伍数据失败，请稍后重试。";

            // 从 AiCharacterEntry 删除
            _trpgDb.DeleteAiCharacterAsync(scope, characterId).GetAwaiter().GetResult();

            _teamDataProvider.InvalidateCache();

            return $"✓ AI角色 '{characterName}' 已从队伍 '{teamName}' 中移除";
        }

        /// <summary>
        /// .team listai — 列出当前队伍中所有AI角色
        /// </summary>
        private string? HandleTeamListAi(string args, object msgObj)
        {
            var msg = (MDiceV2.Models.Msg)msgObj;
            if (_teamDataProvider == null || _trpgDb == null)
            {
                InitializeTrpgComponents();
                if (_teamDataProvider == null || _trpgDb == null)
                    return "AI模块未初始化，无法执行此操作。";
            }

            var teamName = _teamDataProvider.GetUserDefaultTeamName(msg.GroupId, msg.UserId);
            if (string.IsNullOrEmpty(teamName))
                return "您还没有加入任何队伍。";

            var team = _teamDataProvider.GetTeamForGroup(msg.GroupId, teamName);
            if (team == null)
                return $"队伍 '{teamName}' 不存在。";

            var scope = CreateTrpgScope(team);
            _trpgDb.EnsureTrpgWorldAsync(scope).GetAwaiter().GetResult();
            var characters = _trpgDb.GetAiCharactersForTeamAsync(scope).GetAwaiter().GetResult();
            if (characters.Count == 0)
                return $"队伍 '{teamName}' 中没有AI角色。\n使用 .team addai 角色名 来添加。";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"队伍 '{teamName}' 的AI角色：");
            foreach (var c in characters)
            {
                var status = c.IsActive ? "🟢活跃" : "⚪待命";
                var listMode = _trpgDb.GetAiRuntimeModeAsync(scope, c.CharacterId).GetAwaiter().GetResult();
                var modeTag = listMode switch
                {
                    AiRuntimeMode.Act => "act",
                    AiRuntimeMode.Silent => "silent",
                    AiRuntimeMode.Off => "off",
                    _ => "act"
                };
                sb.AppendLine($"  {c.DisplayName} ({status}, {modeTag}) VID:{c.VirtualId}");
            }
            return sb.ToString().Trim();
        }

        // ═══════════════════════════════════════════
        //  顶级指令实现
        // ═══════════════════════════════════════════

        /// <summary>
        /// .logon — 启动跑团，激活队伍中的AI角色
        /// 仅GM可用
        /// </summary>
        private string? HandleLogonCommand(string args, object msgObj)
        {
            var msg = (MDiceV2.Models.Msg)msgObj;
            if (_teamDataProvider == null || _trpgDb == null)
            {
                InitializeTrpgComponents();
                if (_teamDataProvider == null || _trpgDb == null)
                    return "AI模块未初始化，无法执行此操作。";
            }

            var teamName = _teamDataProvider.GetUserDefaultTeamName(msg.GroupId, msg.UserId);
            if (string.IsNullOrEmpty(teamName))
                return "您还没有加入任何队伍。请先使用 .team join 队伍名。";

            var team = _teamDataProvider.GetTeamForGroup(msg.GroupId, teamName);
            if (team == null)
                return $"队伍 '{teamName}' 不存在。";

            if (team.CreatorId != msg.UserId)
                return "只有队伍创建者（GM）才能启动跑团。";

            // 前置权限检查：用户必须有自己的API key 或 白名单权限(AuthLevel<=1)才能启动跑团
            var userApiSetting = GetUserApiSetting(msg.UserId);
            var hasOwnApiKey = IsConfiguredApiKey(userApiSetting.ApiKey) ||
                               IsConfiguredApiKey(userApiSetting.SubApiKey);
            if (!hasOwnApiKey)
            {
                var authLevel = _context.GetUserAuthLevel(msg.UserId);
                if (!authLevel.HasValue || authLevel.Value > 1)
                {
                    return "❌ 无法启动跑团：您没有设置自己的API密钥，也没有使用通用API的权限。\n" +
                           "请先在私聊中设置API密钥：.ai api <你的API密钥>\n" +
                           "或联系管理员获取一级白名单权限。";
                }
            }

            var scope = CreateTrpgScope(team);
            _trpgDb.EnsureTrpgWorldAsync(scope).GetAwaiter().GetResult();

            // 查找队伍中的AI角色
            var characters = _trpgDb.GetAiCharactersForTeamAsync(scope).GetAwaiter().GetResult();
            if (characters.Count == 0)
                return $"队伍 '{teamName}' 中没有AI角色。\n使用 .team addai 角色名 来添加。";

            // 激活所有AI角色并为每个角色创建独立会话
            var activatedNames = new List<string>();
            var sessions = new List<AiCharacterSession>();
            foreach (var c in characters)
            {
                if (!c.IsActive)
                {
                    _trpgDb.SetAiCharacterActiveAsync(scope, c.CharacterId, true).GetAwaiter().GetResult();
                }
                var mode = _trpgDb.GetAiRuntimeModeAsync(scope, c.CharacterId).GetAwaiter().GetResult();
                var modeTag = mode switch
                {
                    AiRuntimeMode.Act => "",
                    AiRuntimeMode.Silent => "（观望静默）",
                    AiRuntimeMode.Off => "（关闭冻结）",
                    _ => ""
                };
                activatedNames.Add($"{c.DisplayName}{modeTag}");
                var session = CreateCharacterSession(scope, c);
                session.SetRuntimeMode(msg.GroupId, mode);
                sessions.Add(session);
            }

            // 注册到该群的活跃会话列表
            lock (_activeSessions)
            {
                _activeSessions[msg.GroupId] = sessions;
            }

            var hasElevatedPermission = msg.IsMasterAccount ||
                                        (msg.UserAuthLevel.HasValue && msg.UserAuthLevel.Value <= 1);
            _activeGroupApiContexts[msg.GroupId] = new ActiveGroupApiContext
            {
                GroupId = msg.GroupId,
                TeamName = teamName,
                OwnerUserId = msg.UserId,
                OwnerHasElevatedPermission = hasElevatedPermission
            };

            var userSetting = GetUserApiSetting(msg.UserId);
            var preferredKey = string.IsNullOrWhiteSpace(userSetting.ApiKey) ? _config.DeepSeekConfig.ApiKey : userSetting.ApiKey;
            var apiSource = string.IsNullOrWhiteSpace(userSetting.ApiKey) ? "通用默认API" : "用户主API";
            var preferredDisplay = IsConfiguredApiKey(preferredKey) ? MaskApiKey(preferredKey) : "未设置";
            var subApiDisplay = string.IsNullOrWhiteSpace(userSetting.SubApiKey) ? "未设置（使用默认轻量配置）" : MaskApiKey(userSetting.SubApiKey);
            _context.SendGroupMessage(msg.GroupId, $"[AIMod:TRPG] 本次优先API：{apiSource} ({preferredDisplay})；轻量API：{subApiDisplay}");

            return $"✓ 跑团已启动！队伍 '{teamName}' 的AI角色已激活：\n" +
                   string.Join(", ", activatedNames.Select(n => $"「{n}」")) +
                   $"\n共 {sessions.Count} 个AI角色进入关注状态，将独立响应。";
        }

        /// <summary>
        /// .logoff — 关闭跑团，停用所有AI角色
        /// 仅GM可用
        /// </summary>
        private string? HandleLogoffCommand(string args, object msgObj)
        {
            var msg = (MDiceV2.Models.Msg)msgObj;
            if (_teamDataProvider == null || _trpgDb == null)
            {
                InitializeTrpgComponents();
                if (_teamDataProvider == null || _trpgDb == null)
                    return "AI模块未初始化，无法执行此操作。";
            }

            var teamName = _teamDataProvider.GetUserDefaultTeamName(msg.GroupId, msg.UserId);
            if (string.IsNullOrEmpty(teamName))
                return "您还没有加入任何队伍。";

            var team = _teamDataProvider.GetTeamForGroup(msg.GroupId, teamName);
            if (team == null)
                return $"队伍 '{teamName}' 不存在。";

            if (team.CreatorId != msg.UserId)
                return "只有队伍创建者（GM）才能关闭跑团。";

            var scope = CreateTrpgScope(team);
            _trpgDb.EnsureTrpgWorldAsync(scope).GetAwaiter().GetResult();
            var characters = _trpgDb.GetActiveAiCharactersAsync(scope).GetAwaiter().GetResult();
            if (characters.Count == 0)
                return "当前没有活跃的AI角色。";

            foreach (var c in characters)
            {
                _trpgDb.SetAiCharacterActiveAsync(scope, c.CharacterId, false).GetAwaiter().GetResult();
            }

            // 从关注状态中移除该群的所有会话
            lock (_activeSessions)
            {
                _activeSessions.Remove(msg.GroupId);
            }
            _activeGroupApiContexts.TryRemove(msg.GroupId, out _);
            _apiWarningCooldown.TryRemove(msg.GroupId, out _);

            return $"✓ 跑团已关闭。队伍 '{teamName}' 的所有AI角色已停用。\n" +
                   "聊天历史已保留，下次 .logon 将自动恢复。";
        }

        /// <summary>
        /// .ai set [角色名] [属性] [JSON值] — 修改AI角色设定
        /// .ai show [角色名] — 显示AI角色详情
        /// 仅GM可用
        /// </summary>
        private string? HandleAiCommand(string args, object msgObj)
        {
            var msg = (MDiceV2.Models.Msg)msgObj;
            if (_teamDataProvider == null || _trpgDb == null)
            {
                InitializeTrpgComponents();
                if (_teamDataProvider == null || _trpgDb == null)
                    return "AI模块未初始化。";
            }

            var trimmedArgs = (args ?? "").Trim();
            if (string.IsNullOrEmpty(trimmedArgs))
                return "用法：\n.ai set 角色名 属性 值\n.ai set 角色名 silent/off/act\n.ai show 角色名\n" +
                       "属性：background, state, skills, inventory, rule\n" +
                       "skills 格式：技能名 数值 技能名 数值...（与 .st 相同）\n" +
                       "rule 预设：{coc} / {dnd}，或直接写文本\n\n" +
                       "运行模式：\n" +
                       "  silent — 观望静默：记录和整理，但不回复\n" +
                       "  off — 关闭冻结：不回复，也不记录/整理\n" +
                       "  act — 恢复活跃：正常记录、整理和回复";

            var parts = trimmedArgs.Split(new[] { ' ' }, 4, StringSplitOptions.RemoveEmptyEntries);
            var subCmd = parts[0].ToLower();

            if (subCmd == "cost")
                return HandleAiCostCommand(parts.Length >= 2 ? parts[1] : "");

            if (subCmd == "debug")
                return HandleAiDebugCommand(msg, parts);

            if (subCmd == "api" && parts.Length >= 2 &&
                string.Equals(parts[1], "usage", StringComparison.OrdinalIgnoreCase))
            {
                return HandleAiApiUsageCommand(msg, parts);
            }

            if (subCmd == "del")
                return HandleAiDelCommand(msg, trimmedArgs);

            if (subCmd == "inventory" && parts.Length >= 3)
            {
                var action = parts[1].ToLowerInvariant();
                var charName = parts[2];
                var teamName = _teamDataProvider?.GetUserDefaultTeamName(msg.GroupId, msg.UserId);
                if (string.IsNullOrEmpty(teamName))
                    return "您还没有加入任何队伍。";

                var team = _teamDataProvider.GetTeamForGroup(msg.GroupId, teamName);
                if (team == null)
                    return $"队伍 '{teamName}' 不存在。";

                var scope = CreateTrpgScope(team);
                _trpgDb.EnsureTrpgWorldAsync(scope).GetAwaiter().GetResult();
                var characterId = $"{msg.GroupId}_{teamName}_{charName}";
                var entry = _trpgDb.GetAiCharacterAsync(scope, characterId).GetAwaiter().GetResult();
                if (entry == null)
                    return $"未找到AI角色 '{charName}'。";

                if (action == "show")
                {
                    _trpgDb.EnsureInitialInventoryImportedAsync(scope, entry).GetAwaiter().GetResult();
                    var currentItems = _trpgDb.GetActiveInventoryItemsAsync(scope, entry.CharacterId).GetAwaiter().GetResult();
                    return $"当前局内物品：\n{FormatInventoryForCommand(currentItems)}";
                }

                if (action == "reset")
                {
                    _trpgDb.ResetInventoryFromInitialSeedAsync(scope, entry).GetAwaiter().GetResult();
                    var currentItems = _trpgDb.GetActiveInventoryItemsAsync(scope, entry.CharacterId).GetAwaiter().GetResult();
                    return $"已从初始背包种子重置当前局内物品。\n{FormatInventoryForCommand(currentItems)}";
                }

                return "用法：.ai inventory show 角色名 / .ai inventory reset 角色名";
            }

            // RuntimeMode 分支：.ai set 角色名 slient/off/act
            if (subCmd == "set" && parts.Length >= 3 && AiRuntimeModeParser.TryParse(parts[2], out var runtimeMode))
            {
                var charName = parts[1];
                return HandleAiRuntimeModeSet(msg, charName, runtimeMode);
            }

            if (subCmd == "show" && parts.Length >= 2)
            {
                var charName = string.Join(" ", parts.Skip(1)).Trim();
                var teamName = _teamDataProvider?.GetUserDefaultTeamName(msg.GroupId, msg.UserId);
                if (string.IsNullOrEmpty(teamName))
                    return "您还没有加入任何队伍。";

                var team = _teamDataProvider.GetTeamForGroup(msg.GroupId, teamName);
                if (team == null)
                    return $"队伍 '{teamName}' 不存在。";

                var scope = CreateTrpgScope(team);
                _trpgDb.EnsureTrpgWorldAsync(scope).GetAwaiter().GetResult();
                var characterId = $"{msg.GroupId}_{teamName}_{charName}";
                var entry = _trpgDb.GetAiCharacterAsync(scope, characterId).GetAwaiter().GetResult();
                if (entry == null)
                    return $"未找到AI角色 '{charName}'。";

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"AI角色：{entry.DisplayName}");
                sb.AppendLine($"队伍：{entry.TeamName} | 虚拟ID：{entry.VirtualId}");
                sb.AppendLine($"状态：{(entry.IsActive ? "🟢活跃" : "⚪待命")}");
                var showMode = _trpgDb.GetAiRuntimeModeAsync(scope, entry.CharacterId).GetAwaiter().GetResult();
                sb.AppendLine($"运行模式：{AiRuntimeModeParser.ToStorageValue(showMode)} / {AiRuntimeModeParser.ToDisplayName(showMode)}");
                var activeHistory = _trpgDb.GetActiveHistoryAsync(scope, entry.CharacterId).GetAwaiter().GetResult();
                var activeTokens = _trpgDb.GetActiveTokenCountAsync(scope, entry.CharacterId).GetAwaiter().GetResult();
                var remainingHistory = Math.Max(0, _config.TrpgConfig.RecentHistoryCount - activeHistory.Count);
                var remainingTokens = Math.Max(0, _config.TrpgConfig.TokenThreshold - activeTokens);
                sb.AppendLine("历史折叠：");
                sb.AppendLine($"  折叠消息条数阈值 RecentHistoryCount：{_config.TrpgConfig.RecentHistoryCount}");
                sb.AppendLine($"  折叠 token 阈值 TokenThreshold：{_config.TrpgConfig.TokenThreshold}");
                sb.AppendLine($"  每次折叠条数 HistoryFoldCount：{_config.TrpgConfig.HistoryFoldCount}");
                sb.AppendLine($"  当前 active history 条数：{activeHistory.Count}");
                sb.AppendLine($"  当前 active history 估算 token：{activeTokens}");
                sb.AppendLine($"  距离下一次折叠：还差 {remainingHistory} 条 / {remainingTokens} token");
                sb.AppendLine("  规则：当 active history 条数 >= RecentHistoryCount，或 active history token >= TokenThreshold 时触发折叠；触发后折叠最旧的 HistoryFoldCount 条。");
                if (!string.IsNullOrEmpty(entry.StaticBackground))
                    sb.AppendLine($"人设/规则：{entry.StaticBackground}");
                if (entry.DynamicStateJson != "{}")
                    sb.AppendLine($"动态状态：{entry.DynamicStateJson}");
                if (entry.SkillsJson != "{}")
                    sb.AppendLine($"技能/属性：{entry.SkillsJson}");
                _trpgDb.EnsureInitialInventoryImportedAsync(scope, entry).GetAwaiter().GetResult();
                var currentItems = _trpgDb.GetActiveInventoryItemsAsync(scope, entry.CharacterId).GetAwaiter().GetResult();
                sb.AppendLine("当前局内物品：");
                sb.AppendLine(FormatInventoryForCommand(currentItems));
                if (entry.InitialInventoryJson != "[]")
                    sb.AppendLine($"初始背包配置：{entry.InitialInventoryJson}");
                if (!string.IsNullOrEmpty(entry.RuleText))
                    sb.AppendLine($"规则：{entry.RuleText}");
                return sb.ToString().Trim();
            }

            if (subCmd == "set" && parts.Length >= 4)
            {
                var charName = parts[1];
                var property = parts[2].ToLower();
                var jsonValue = parts[3];

                var teamName = _teamDataProvider?.GetUserDefaultTeamName(msg.GroupId, msg.UserId);
                if (string.IsNullOrEmpty(teamName))
                    return "您还没有加入任何队伍。";

                var team = _teamDataProvider.GetTeamForGroup(msg.GroupId, teamName);
                if (team == null)
                    return $"队伍 '{teamName}' 不存在。";

                var scope = CreateTrpgScope(team);
                _trpgDb.EnsureTrpgWorldAsync(scope).GetAwaiter().GetResult();
                var characterId = $"{msg.GroupId}_{teamName}_{charName}";
                var entry = _trpgDb.GetAiCharacterAsync(scope, characterId).GetAwaiter().GetResult();
                if (entry == null)
                    return $"未找到AI角色 '{charName}'。";

                switch (property)
                {
                    case "background":
                        entry.StaticBackground = jsonValue;
                        break;
                    case "state":
                        entry.DynamicStateJson = jsonValue;
                        break;
                    case "skills":
                        {
                            // 解析 key value 格式（与 .st 指令相同）：力量 50 体型 60
                            var skillDict = ParseKeyValueSkills(parts[3]);
                            entry.SkillsJson = System.Text.Json.JsonSerializer.Serialize(skillDict);
                        }
                        break;
                    case "inventory":
                        {
                            var wasImported = _trpgDb.HasInitialInventoryImportedAsync(scope, entry.CharacterId).GetAwaiter().GetResult();
                            entry.InitialInventoryJson = jsonValue;
                            _trpgDb.UpsertAiCharacterAsync(scope, entry).GetAwaiter().GetResult();
                            if (wasImported)
                                return $"✓ 已更新AI角色 '{charName}' 的初始背包种子。\n初始背包已导入，当前局内物品不会被覆盖；如需修改当前物品，请使用 .ai inventory reset 或后续 fix 命令。";
                            return $"✓ 已更新AI角色 '{charName}' 的初始背包种子。首次构建 prompt 时会导入当前局内物品。";
                        }
                    case "rule":
                        entry.RuleText = ExpandRulePreset(jsonValue);
                        break;
                    default:
                        return $"未知属性 '{property}'。支持：background, state, skills, inventory, rule";
                }

                _trpgDb.UpsertAiCharacterAsync(scope, entry).GetAwaiter().GetResult();
                return $"✓ 已更新AI角色 '{charName}' 的 {property}";
            }

            return "用法：\n.ai set 角色名 属性 值\n.ai show 角色名\n" +
                   "skills 格式：.ai set 角色名 skills 技能名 数值...\n" +
                   "rule 预设：{coc} / {dnd}，或直接写文本";
        }

        private string HandleAiCostCommand(string args)
        {
            if (_trpgDb == null)
            {
                InitializeTrpgComponents();
                if (_trpgDb == null)
                    return "AI模块未初始化，无法读取用量日志。";
            }

            var now = DateTime.UtcNow;
            var arg = (args ?? "").Trim();
            if (arg.StartsWith("turns", StringComparison.OrdinalIgnoreCase))
            {
                var recentTurns = 10;
                var suffix = arg.Length > 5 ? arg[5..].Trim() : "";
                if (!string.IsNullOrWhiteSpace(suffix) && (!int.TryParse(suffix, out recentTurns) || recentTurns <= 0))
                    return "用法：.ai cost turns [回合数]，例如 .ai cost turns 12";

                try
                {
                    var turns = _trpgDb.GetRecentLlmTurnCostsAsync(Math.Clamp(recentTurns, 1, 50)).GetAwaiter().GetResult();
                    return FormatLlmTurnCostReport(turns, Math.Clamp(recentTurns, 1, 50));
                }
                catch (Exception ex)
                {
                    _context.Log(LogLevel.Warn, $"[AIMod:TRPG] .ai cost turns failed: {ex.Message}");
                    return $"读取按回合聚合的 LLM 用量失败：{ex.Message}";
                }
            }

            var providerFilter = default(string);
            var label = "最近24小时";
            var from = now.AddHours(-24);

            if (arg.Equals("today", StringComparison.OrdinalIgnoreCase))
            {
                from = now.Date;
                label = "今天(UTC)";
            }
            else if (arg.Equals("7d", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("week", StringComparison.OrdinalIgnoreCase))
            {
                from = now.AddDays(-7);
                label = "最近7天";
            }
            else if (arg.Equals("24h", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("day", StringComparison.OrdinalIgnoreCase) ||
                     string.IsNullOrWhiteSpace(arg) ||
                     arg.Equals("agent", StringComparison.OrdinalIgnoreCase))
            {
                from = now.AddHours(-24);
                label = "最近24小时";
            }
            else
            {
                providerFilter = arg;
                label = $"最近24小时 provider={providerFilter}";
            }

            try
            {
                var report = _trpgDb.GetLlmCostReportAsync(from, now, providerFilter).GetAwaiter().GetResult();
                return FormatLlmCostReport(report, label, providerFilter);
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] .ai cost failed: {ex.Message}");
                return $"读取 LLM 用量失败：{ex.Message}";
            }
        }

        private static string FormatLlmCostReport(LlmCostReport report, string label, string? providerFilter)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"LLM 用量统计：{label}");
            sb.AppendLine($"请求：{report.RequestCount} 次，成功 {report.SuccessCount}，失败 {report.FailureCount}");
            sb.AppendLine($"Token：输入 {report.InputTokens:N0}，输出 {report.OutputTokens:N0}，缓存输入 {report.CachedInputTokens:N0}，命中 {report.CacheHitTokens:N0}，未命中 {report.CacheMissTokens:N0}");
            sb.AppendLine($"估算成本：{report.EstimatedCost:F6} USD");
            sb.AppendLine("注：若接口返回 usage 则记录真实 token；否则按字符估算 token，成本按默认单价估算。");

            if (report.ProviderModels.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Provider/Model：");
                foreach (var item in report.ProviderModels.Take(5))
                    sb.AppendLine($"- {item.Name}: {item.RequestCount} 次，in {item.InputTokens:N0}, out {item.OutputTokens:N0}, cost {item.EstimatedCost:F6}");
            }

            if (report.TopAgents.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Agent：");
                foreach (var item in report.TopAgents.Take(5))
                    sb.AppendLine($"- {item.Name}: {item.RequestCount} 次，in {item.InputTokens:N0}, out {item.OutputTokens:N0}, cost {item.EstimatedCost:F6}");
            }

            if (report.TopRequestKinds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("RequestKind：");
                foreach (var item in report.TopRequestKinds.Take(5))
                    sb.AppendLine($"- {item.Name}: {item.RequestCount} 次，in {item.InputTokens:N0}, out {item.OutputTokens:N0}, cost {item.EstimatedCost:F6}");
            }

            if (report.RequestCount == 0 && !string.IsNullOrWhiteSpace(providerFilter))
                sb.AppendLine($"\n未找到 provider={providerFilter} 的记录。");

            return sb.ToString().Trim();
        }

        private static string FormatLlmTurnCostReport(List<LlmTurnCostRow> rows, int requestedTurns)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"LLM 用量统计：最近 {requestedTurns} 个回合");

            if (rows.Count == 0)
            {
                sb.AppendLine("暂无回合级调用记录。");
                return sb.ToString().Trim();
            }

            var totalRequests = rows.Sum(x => x.RequestCount);
            var totalInput = rows.Sum(x => x.InputTokens);
            var totalOutput = rows.Sum(x => x.OutputTokens);
            var totalCachedInput = rows.Sum(x => x.CachedInputTokens);
            var totalCacheHit = rows.Sum(x => x.CacheHitTokens);
            var totalCacheMiss = rows.Sum(x => x.CacheMissTokens);
            var totalCost = rows.Sum(x => x.EstimatedCost);

            sb.AppendLine($"回合：{rows.Count} 个，请求：{totalRequests} 次");
            sb.AppendLine($"Token：输入 {totalInput:N0}，输出 {totalOutput:N0}，缓存输入 {totalCachedInput:N0}，命中 {totalCacheHit:N0}，未命中 {totalCacheMiss:N0}");
            sb.AppendLine($"估算成本：{totalCost:F6} USD");
            sb.AppendLine();
            sb.AppendLine("按回合：");

            foreach (var row in rows.Take(10))
            {
                var summary = string.IsNullOrWhiteSpace(row.SourceSummary) ? "(无摘要)" : row.SourceSummary;
                if (summary.Length > 48)
                    summary = summary[..48] + "...";

                var sourceMessage = string.IsNullOrWhiteSpace(row.SourceMessageId) ? "-" : row.SourceMessageId;
                var heavyAgent = string.IsNullOrWhiteSpace(row.MostExpensiveAgent) ? "-" : row.MostExpensiveAgent;
                sb.AppendLine($"- {row.StartedAt:MM-dd HH:mm} | turn={row.TurnId} | msg={sourceMessage}");
                sb.AppendLine($"  {summary}");
                sb.AppendLine($"  req {row.RequestCount} | in {row.InputTokens:N0} | out {row.OutputTokens:N0} | cost {row.EstimatedCost:F6} | top {heavyAgent}");
            }

            if (rows.Count > 10)
                sb.AppendLine($"... 其余 {rows.Count - 10} 个回合未展开");

            sb.AppendLine("注：按 TurnId 聚合；旧记录若缺少 TurnId，会退化为按时间戳和来源消息分组。");
            return sb.ToString().Trim();
        }

        private string HandleAiApiUsageCommand(Msg msg, string[] parts)
        {
            if (!msg.IsMasterAccount)
                return "权限不足：只有 master 可以查看通用默认 API 用量。";

            if (_trpgDb == null)
            {
                InitializeTrpgComponents();
                if (_trpgDb == null)
                    return "AI模块未初始化，无法读取通用 API 用量。";
            }

            var now = DateTime.UtcNow;
            var from = now.AddHours(-24);
            var label = "最近 24h";
            long? userFilter = null;
            long? groupFilter = null;

            if (parts.Length >= 3)
            {
                var option = parts[2].Trim().ToLowerInvariant();
                switch (option)
                {
                    case "":
                    case "24h":
                        break;
                    case "today":
                        from = now.Date;
                        label = "today (UTC)";
                        break;
                    case "7d":
                        from = now.AddDays(-7);
                        label = "最近 7d";
                        break;
                    case "user":
                        if (parts.Length < 4 || !long.TryParse(parts[3], out var parsedUserId))
                            return "用法：.ai api usage [24h|today|7d|user <QQ号>|group <群号>]";
                        userFilter = parsedUserId;
                        label = $"最近 24h / user {parsedUserId}";
                        break;
                    case "group":
                        if (parts.Length < 4 || !long.TryParse(parts[3], out var parsedGroupId))
                            return "用法：.ai api usage [24h|today|7d|user <QQ号>|group <群号>]";
                        groupFilter = parsedGroupId;
                        label = $"最近 24h / group {parsedGroupId}";
                        break;
                    default:
                        return "用法：.ai api usage [24h|today|7d|user <QQ号>|group <群号>]";
                }
            }

            try
            {
                var report = _trpgDb.GetCommonApiUsageReportAsync(from, now, userFilter, groupFilter).GetAwaiter().GetResult();
                return FormatCommonApiUsageReport(report, label);
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] .ai api usage failed: {ex.Message}");
                return $"读取通用 API 用量失败：{ex.Message}";
            }
        }

        private static string FormatCommonApiUsageReport(CommonApiUsageReport report, string label)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"通用 API 用量统计：{label}");
            sb.AppendLine("仅统计实际使用通用默认 API 的请求，不包含用户自设 ApiKey / SubApiKey。");

            if (report.Rows.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("当前时间范围内没有命中通用默认 API 的记录。");
                return sb.ToString().Trim();
            }

            sb.AppendLine();
            sb.AppendLine("用户ID        请求数  成功  输入token  输出token  总token   缓存      估算费用");
            foreach (var row in report.Rows.Take(20))
            {
                sb.AppendLine(
                    $"{row.UserId,-12} {row.RequestCount,5}  {row.SuccessCount,4}  {row.InputTokens,9}  {row.OutputTokens,9}  {row.TotalTokens,9}  {FormatCacheRate(row),-7}  ${row.EstimatedCost:F6}");
            }

            if (report.Rows.Count > 20)
                sb.AppendLine($"... 其余 {report.Rows.Count - 20} 个用户未展开");

            sb.AppendLine();
            sb.AppendLine("合计：");
            sb.AppendLine($"请求数：{report.RequestCount}");
            sb.AppendLine($"成功 / 失败：{report.SuccessCount} / {report.FailureCount}");
            sb.AppendLine($"输入 token：{report.InputTokens}");
            sb.AppendLine($"输出 token：{report.OutputTokens}");
            sb.AppendLine($"总 token：{report.TotalTokens}");
            sb.AppendLine($"缓存：{FormatCommonApiCacheSummary(report)}");
            sb.AppendLine($"估算费用：${report.EstimatedCost:F6}");
            return sb.ToString().Trim();
        }

        private static string FormatCacheRate(CommonApiUsageReportRow row)
        {
            if (row.CacheKnownTokens <= 0)
                return "N/A";

            var numerator = row.CacheHitTokens ?? row.CachedInputTokens;
            if (!numerator.HasValue)
                return "N/A";

            var rate = (double)numerator.Value / row.CacheKnownTokens;
            return $"{rate:P1}";
        }

        private static string FormatCommonApiCacheSummary(CommonApiUsageReport report)
        {
            if (report.CacheKnownTokens <= 0)
                return "N/A（provider 未返回缓存字段）";

            var numerator = report.CacheHitTokens ?? report.CachedInputTokens;
            if (!numerator.HasValue)
                return "N/A（provider 未返回缓存字段）";

            var rate = (double)numerator.Value / report.CacheKnownTokens;
            return $"{rate:P1}（部分 provider 不返回缓存字段，合计命中率仅基于可用记录计算）";
        }

        private string HandleAiDelCommand(Msg msg, string trimmedArgs)
        {
            if (!msg.IsMasterAccount)
                return "权限不足：只有 master 可以执行 .ai del。";

            if (_trpgDb == null || _teamDataProvider == null)
            {
                InitializeTrpgComponents();
                if (_trpgDb == null || _teamDataProvider == null)
                    return "AI模块未初始化。";
            }

            var suffix = trimmedArgs.Length > 3 ? trimmedArgs[3..].Trim() : "";
            if (!TryParseAiDelArguments(suffix, out var ownerUserId, out var teamName, out var confirm, out var parseError))
                return parseError;

            try
            {
                if (!confirm)
                {
                    var preview = _trpgDb.PreviewAiTeamDataDeleteAsync(ownerUserId, msg.GroupId, teamName).GetAwaiter().GetResult();
                    if (!HasAiDeleteTargetData(preview.Target))
                        return $"未找到目标数据：当前群 {msg.GroupId} 下 owner={ownerUserId}、team={teamName} 的 AIMod 本地 AI 数据不存在。";
                    return FormatAiDelPreview(preview);
                }

                var result = _trpgDb.DeleteAiTeamDataAsync(ownerUserId, msg.GroupId, teamName).GetAwaiter().GetResult();
                if (!result.Deleted)
                    return $"未找到目标数据：当前群 {msg.GroupId} 下 owner={ownerUserId}、team={teamName} 的 AIMod 本地 AI 数据不存在。";

                result.Counts["World"] = result.Counts.TryGetValue("TrpgWorld", out var deletedWorlds)
                    ? deletedWorlds
                    : result.Target.WorldIds.Count;
                result.Counts["AI角色"] = result.Counts.TryGetValue("AiCharacterEntry", out var deletedCharacters)
                    ? deletedCharacters
                    : result.Target.CharacterIds.Count;
                result.Counts["VirtualId"] = result.Target.VirtualIds.Count;
                ClearDeletedTeamRuntimeState(result.Target);
                var removedFromMainTeam = RemoveVirtualIdsFromMainDb(msg.GroupId, teamName, result.Target.VirtualIds);
                _teamDataProvider.InvalidateCache();
                return FormatAiDelResult(result, removedFromMainTeam);
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod:TRPG] .ai del failed: {ex.Message}");
                return $"删除 AIMod 本地 AI 数据失败：{ex.Message}";
            }
        }

        private static bool TryParseAiDelArguments(
            string args,
            out long ownerUserId,
            out string teamName,
            out bool confirm,
            out string error)
        {
            ownerUserId = 0;
            teamName = "";
            confirm = false;
            error = "用法：.ai del <用户QQ号> <队伍名>\n.ai del <用户QQ号> <队伍名> confirm";

            var tokens = Regex.Matches(args ?? "", "\"([^\"]*)\"|(\\S+)")
                .Cast<Match>()
                .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .ToList();

            if (tokens.Count < 2 || tokens.Count > 3)
                return false;

            if (!long.TryParse(tokens[0], out ownerUserId))
            {
                error = "用法错误：用户QQ号必须是数字。";
                return false;
            }

            teamName = tokens[1].Trim();
            if (string.IsNullOrWhiteSpace(teamName))
            {
                error = "用法错误：队伍名不能为空。";
                return false;
            }

            confirm = tokens.Count == 3 && string.Equals(tokens[2], "confirm", StringComparison.OrdinalIgnoreCase);
            if (tokens.Count == 3 && !confirm)
            {
                error = "确认删除请追加 confirm。";
                return false;
            }

            return true;
        }

        private static string FormatAiDelPreview(AiTeamDeletePreview preview)
        {
            var sb = new StringBuilder();
            sb.AppendLine("即将删除的 AIMod 数据预览：");
            sb.AppendLine();
            sb.AppendLine("目标：");
            sb.AppendLine($"- 用户：{preview.Target.OwnerUserId}");
            sb.AppendLine($"- 当前群：{preview.Target.GroupId}");
            sb.AppendLine($"- 队伍：{preview.Target.TeamName}");
            sb.AppendLine();
            sb.AppendLine("匹配到：");
            foreach (var line in BuildAiDeleteCountLines(preview.Counts))
                sb.AppendLine($"- {line}");
            sb.AppendLine();
            sb.AppendLine("不会删除：");
            sb.AppendLine("- 用户 ApiKey / SubApiKey");
            sb.AppendLine("- 主程序 .log 跑团日志文件");
            sb.AppendLine("- 其他用户 / 其他群 / 其他 team 的数据");
            sb.AppendLine("- 人类玩家成员");
            if (preview.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("注意：");
                foreach (var warning in preview.Warnings.Distinct())
                    sb.AppendLine($"- {warning}");
            }

            sb.AppendLine();
            sb.AppendLine("确认删除请执行：");
            sb.AppendLine(FormatAiDelCommand(preview.Target.OwnerUserId, preview.Target.TeamName, true));
            return sb.ToString().Trim();
        }

        private string FormatAiDelResult(AiTeamDeleteResult result, bool removedFromMainTeam)
        {
            var sb = new StringBuilder();
            sb.AppendLine("AIMod 本地 AI 数据已删除：");
            sb.AppendLine($"- 用户：{result.Target.OwnerUserId}");
            sb.AppendLine($"- 当前群：{result.Target.GroupId}");
            sb.AppendLine($"- 队伍：{result.Target.TeamName}");
            sb.AppendLine();
            sb.AppendLine("已删除：");
            foreach (var line in BuildAiDeleteCountLines(result.Counts))
                sb.AppendLine($"- {line}");
            sb.AppendLine();
            sb.AppendLine("不会删除：");
            sb.AppendLine("- 用户 ApiKey / SubApiKey");
            sb.AppendLine("- 主程序 .log 跑团日志文件");
            sb.AppendLine("- 其他用户 / 其他群 / 其他 team 的数据");
            sb.AppendLine("- 人类玩家成员");
            sb.AppendLine($"- TeamInfo AI virtualId 清理：{(removedFromMainTeam ? "已尝试移除目标 virtualId" : "移除失败，请检查日志 warning")}");
            if (result.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("注意：");
                foreach (var warning in result.Warnings.Distinct())
                    sb.AppendLine($"- {warning}");
            }

            return sb.ToString().Trim();
        }

        private static IEnumerable<string> BuildAiDeleteCountLines(IReadOnlyDictionary<string, int> counts)
        {
            foreach (var key in new[]
                     {
                         "World", "AI角色", "VirtualId", "ChatHistory", "LongTermMemory", "RawArchive",
                         "CharacterMemory", "NarrativeMemoryNode", "SceneSnapshot", "Quest",
                         "CharacterInventoryItem", "CharacterInventorySeedState", "AffectiveTagState",
                         "AffectiveTagEvent", "EntityCanonical", "EntitySalience", "NpcCanonicalState",
                         "EventLog", "CausalGraph", "TimelineNodes", "BehaviorEvidence", "CharacterSheet",
                         "AiDebugSetting", "AiCharacterRuntimeControl", "CharacterHotMeta", "SceneDictionary",
                         "LlmUsageLog", "LlmDebugLog", "CommonApiUsageLog"
                     })
            {
                if (counts.TryGetValue(key, out var value))
                    yield return $"{key}：{value} 个";
            }
        }

        private static string FormatAiDelCommand(long ownerUserId, string teamName, bool includeConfirm)
        {
            var safeTeamName = teamName.Contains(' ') ? $"\"{teamName}\"" : teamName;
            return includeConfirm
                ? $".ai del {ownerUserId} {safeTeamName} confirm"
                : $".ai del {ownerUserId} {safeTeamName}";
        }

        private static bool HasAiDeleteTargetData(AiTeamDeleteTarget target)
        {
            return target.WorldIds.Count > 0 || target.CharacterIds.Count > 0 || target.VirtualIds.Count > 0;
        }

        private void ClearDeletedTeamRuntimeState(AiTeamDeleteTarget target)
        {
            if (_messageRouter != null)
            {
                foreach (var characterId in target.CharacterIds)
                    _messageRouter.ClearCharacterState(target.GroupId, characterId);
            }

            _stateCache?.RemoveEntries(target.WorldIds, target.GroupId, target.CharacterIds);

            lock (_activeSessions)
            {
                if (_activeSessions.TryGetValue(target.GroupId, out var sessions))
                {
                    sessions.RemoveAll(session =>
                        target.WorldIds.Contains(session.Scope.WorldId, StringComparer.OrdinalIgnoreCase) ||
                        target.CharacterIds.Contains(session.Character.CharacterId, StringComparer.OrdinalIgnoreCase) ||
                        string.Equals(session.Character.TeamName, target.TeamName, StringComparison.OrdinalIgnoreCase));

                    if (sessions.Count == 0)
                        _activeSessions.Remove(target.GroupId);
                }
            }

            if (_activeGroupApiContexts.TryGetValue(target.GroupId, out var apiContext) &&
                string.Equals(apiContext.TeamName, target.TeamName, StringComparison.OrdinalIgnoreCase))
            {
                _activeGroupApiContexts.TryRemove(target.GroupId, out _);
                _apiWarningCooldown.TryRemove(target.GroupId, out _);
            }
        }

        private string? HandleAiDebugCommand(Msg msg, string[] parts)
        {
            if (_trpgDb == null || _teamDataProvider == null)
                return "AI模块未初始化。";

            var teamName = _teamDataProvider.GetUserDefaultTeamName(msg.GroupId, msg.UserId);
            if (string.IsNullOrEmpty(teamName))
                return "您还没有加入任何队伍。";

            var team = _teamDataProvider.GetTeamForGroup(msg.GroupId, teamName);
            if (team == null)
                return $"队伍 '{teamName}' 不存在。";

            var scope = CreateTrpgScope(team);
            _trpgDb.EnsureTrpgWorldAsync(scope).GetAwaiter().GetResult();

            if (parts.Length < 2)
                return "用法：.ai debug on|off|status|export [limit|agent_name]\n" +
                       ".ai debug on - 开启当前World/Group的全局debug\n" +
                       ".ai debug off - 关闭全局debug\n" +
                       ".ai debug status - 查看debug状态\n" +
                       ".ai debug export [limit] - 导出最近日志（默认50条）\n" +
                       ".ai debug export agent AgentName - 导出特定agent日志";

            var subAction = parts[1].ToLowerInvariant();

            switch (subAction)
            {
                case "on":
                    try
                    {
                        _trpgDb.SetGlobalDebugEnabledAsync(scope, true).GetAwaiter().GetResult();
                        return "✓ 已开启全局 LLM debug。日志可能很大。使用 .ai debug export 导出。";
                    }
                    catch (Exception ex)
                    {
                        return $"开启 debug 失败：{ex.Message}";
                    }

                case "off":
                    try
                    {
                        _trpgDb.SetGlobalDebugEnabledAsync(scope, false).GetAwaiter().GetResult();
                        return "✓ 已关闭全局 LLM debug。";
                    }
                    catch (Exception ex)
                    {
                        return $"关闭 debug 失败：{ex.Message}";
                    }

                case "status":
                    try
                    {
                        var enabled = _trpgDb.IsGlobalDebugEnabledAsync(scope).GetAwaiter().GetResult();
                        var count = _trpgDb.CountLlmDebugLogsAsync(scope).GetAwaiter().GetResult();
                        var logs = _trpgDb.GetRecentLlmDebugLogsAsync(scope, 1).GetAwaiter().GetResult();
                        var lastTime = logs.FirstOrDefault()?.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") ?? "无";
                        
                        return $"LLM Debug 状态：{(enabled ? "ON" : "OFF")}\n" +
                               $"日志条数：{count}\n" +
                               $"最近日志：{lastTime}\n" +
                               $"提示：使用 .ai debug export 导出完整日志";
                    }
                    catch (Exception ex)
                    {
                        return $"查询 debug 状态失败：{ex.Message}";
                    }

                case "export":
                    try
                    {
                        var aimodRoot = Path.Combine(AppContext.BaseDirectory, "data", "AIMod");
                        var exportDir = Path.Combine(aimodRoot, "debug", scope.GroupId.ToString());
                        Directory.CreateDirectory(exportDir);
                        var exporter = new DebugLogExporter(_trpgDb, _context, aimodRoot);

                        int limit = 50;
                        string? agentFilter = null;

                        if (parts.Length >= 3)
                        {
                            if (parts[2].Equals("agent", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
                            {
                                agentFilter = parts[3];
                            }
                            else if (int.TryParse(parts[2], out int parsed))
                            {
                                limit = parsed;
                            }
                        }

                        var result = exporter.ExportDebugLogsAsync(scope, limit, agentFilter).GetAwaiter().GetResult();
                        return result;
                    }
                    catch (Exception ex)
                    {
                        _context.Log(LogLevel.Warn, $"[AIMod] Debug export failed: {ex.Message}");
                        return $"导出 debug 日志失败：{ex.Message}";
                    }

                default:
                    return "未知的 debug 子命令。用法：.ai debug on|off|status|export";
            }
        }

        /// <summary>
        /// 解析 "技能名 数值 技能名 数值..." 格式的字符串（与 .st 指令相同格式）
        /// 返回 Dictionary<string, int>，与本体 CharacterSheet.Skills 格式一致
        /// </summary>
        private static Dictionary<string, int> ParseKeyValueSkills(string input)
        {
            var result = new Dictionary<string, int>();
            var tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < tokens.Length; i += 2)
            {
                var skillName = tokens[i];
                if (int.TryParse(tokens[i + 1], out int value))
                {
                    result[skillName] = value;
                }
            }
            return result;
        }

        private string? HandleAiRuntimeModeSet(
            MDiceV2.Models.Msg msg,
            string charName,
            AiRuntimeMode mode)
        {
            if (_teamDataProvider == null || _trpgDb == null)
            {
                InitializeTrpgComponents();
                if (_teamDataProvider == null || _trpgDb == null)
                    return "AI模块未初始化。";
            }

            var teamName = _teamDataProvider.GetUserDefaultTeamName(msg.GroupId, msg.UserId);
            if (string.IsNullOrEmpty(teamName))
                return "您还没有加入任何队伍。";

            var team = _teamDataProvider.GetTeamForGroup(msg.GroupId, teamName);
            if (team == null)
                return $"队伍 '{teamName}' 不存在。";

            if (team.CreatorId != msg.UserId)
                return "只有队伍创建者（GM）才能切换AI运行状态。";

            var scope = CreateTrpgScope(team);
            _trpgDb.EnsureTrpgWorldAsync(scope).GetAwaiter().GetResult();

            var entry = FindAiCharacterByDisplayName(scope, teamName, msg.GroupId, charName);
            if (entry == null)
                return $"未找到AI角色 '{charName}'。";

            _trpgDb.SetAiRuntimeModeAsync(scope, entry.CharacterId, mode, msg.UserId)
                .GetAwaiter()
                .GetResult();

            UpdateActiveSessionRuntimeMode(msg.GroupId, entry.CharacterId, mode);

            return mode switch
            {
                AiRuntimeMode.Act =>
                    $"✓ AI角色「{entry.DisplayName}」已切换为 act / 活跃：将正常响应并维护上下文。",

                AiRuntimeMode.Silent =>
                    $"✓ AI角色「{entry.DisplayName}」已切换为 silent / 观望静默：将继续记录和整理，但不主动回复。",

                AiRuntimeMode.Off =>
                    $"✓ AI角色「{entry.DisplayName}」已切换为 off / 关闭冻结：将完全停止响应、记录和时间线整理。",

                _ =>
                    $"✓ AI角色「{entry.DisplayName}」运行状态已更新。"
            };
        }

        private AiCharacterEntry? FindAiCharacterByDisplayName(
            TrpgScope scope,
            string teamName,
            long groupId,
            string charName)
        {
            var directCharacterId = $"{groupId}_{teamName}_{charName}";
            var direct = _trpgDb!.GetAiCharacterAsync(scope, directCharacterId)
                .GetAwaiter()
                .GetResult();

            if (direct != null)
                return direct;

            var all = _trpgDb.GetAiCharactersForTeamAsync(scope)
                .GetAwaiter()
                .GetResult();

            return all.FirstOrDefault(c =>
                string.Equals(c.DisplayName, charName, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateActiveSessionRuntimeMode(
            long groupId,
            string characterId,
            AiRuntimeMode mode)
        {
            lock (_activeSessions)
            {
                if (!_activeSessions.TryGetValue(groupId, out var sessions))
                    return;

                var session = sessions.FirstOrDefault(s =>
                    string.Equals(s.Character.CharacterId, characterId, StringComparison.OrdinalIgnoreCase));

                if (session != null)
                {
                    session.SetRuntimeMode(groupId, mode);
                }
            }
        }

        private bool AreAllGroupSessionsOff(long groupId)
        {
            lock (_activeSessions)
            {
                if (!_activeSessions.TryGetValue(groupId, out var sessions) || sessions == null || sessions.Count == 0)
                    return false;

                return sessions.All(session => session.RuntimeMode == AiRuntimeMode.Off);
            }
        }

        private static string FormatInventoryForCommand(IReadOnlyList<CharacterInventoryItem> items)
        {
            if (items == null || items.Count == 0)
                return "无";

            var lines = new List<string>();
            foreach (var item in items)
            {
                var assumed = item.IsAssumed ? "，推定" : "";
                var qty = item.Quantity <= 0 ? "" : $" x{item.Quantity:g}{item.Unit}";
                var desc = string.IsNullOrWhiteSpace(item.Description) ? "" : $"：{item.Description}";
                lines.Add($"- {item.DisplayName}{qty} [{item.State}{assumed}]{desc}");
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// 展开规则预设：{coc} / {dnd} 映射为硬编码规则文本，其他值原样返回
        /// </summary>
        private static string ExpandRulePreset(string input)
        {
            var trimmed = input.Trim();
            return trimmed.ToLowerInvariant() switch
            {
                "{coc}" => """
                    你正在进行克苏鲁的呼唤（Call of Cthulhu，第7版）跑团。
                    可用指令：.st 查看/设置属性，.cc{coc7} 技能名 技能数值 进行技能检定，.sc 进行理智检定。
                    属性包括：力量(STR)、体质(CON)、体型(SIZ)、敏捷(DEX)、外貌(APP)、智力(INT)、意志(POW)、教育(EDU)、幸运(LUCK)。
                    技能包括：侦查、聆听、图书馆使用、话术、战斗(斗殴/手枪/步枪/霰弹枪)、潜行、精神分析、医学、急救、攀爬、跳跃、游泳、驾驶、追踪等。
                    检定方式：d100，目标值≤技能值成功；≤技能值/2为困难成功；≤技能值/5为极难成功；96-100为大失败；1为 critical。
                    理智检定(SAN Check)：目睹恐怖时投d100，若失败则损失理智值(1d3/1d6/1d10等)。
                    你可以使用 .st 查看自己的技能和属性，并在需要时请求进行技能检定，但你绝不能自行判定结果。
                    """,
                "{dnd}" => """
                    你正在进行龙与地下城（D&D 5e）跑团。
                    可用指令：.ri+先攻价值 .rd20+命中加值
                    核心六维属性：力量(STR)、敏捷(DEX)、体质(CON)、智力(INT)、感知(WIS)、魅力(CHA)。
                    主要技能包括：杂技、动物驯养、奥秘、运动、欺瞒、历史、洞悉、威吓、调查、医药、自然、察觉、表演、说服、宗教、巧手、隐匿、生存。
                    攻击检定：d20 + 熟练加值 + 属性调整值 vs AC。
                    豁免检定：d20 + 熟练加值(若熟练) + 属性调整值 vs DC。
                    你可以使用 .init 进行先攻检定，在战斗中按照行动顺序行动，但你绝不能自行判定结果。
                    """,
                _ => trimmed
            };
        }

        // ═══════════════════════════════════════════
        //  主库 TeamInfo.Members 读写辅助
        // ═══════════════════════════════════════════

        /// <summary>
        /// 向主库的 TeamInfo.Members 添加虚拟ID
        /// 直接操作主库 SQLite，与 TeamDataProvider 读主库对称
        /// </summary>
        private bool AddVirtualIdToMainDb(long groupId, string teamName, long virtualId)
        {
            try
            {
                var launcherBaseDir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                var mainDbPath = System.IO.Path.Combine(launcherBaseDir, "data", "MDiceV2.db");

                if (!System.IO.File.Exists(mainDbPath)) return false;

                using var conn = new SQLiteConnection($"Data Source={mainDbPath};Version=3");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM GroupData WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", groupId.ToString());
                var jsonValue = cmd.ExecuteScalar() as string;
                if (jsonValue == null) return false;

                // 解析JSON，找到对应队伍，添加虚拟ID到Members
                var doc = System.Text.Json.JsonDocument.Parse(jsonValue);
                var root = doc.RootElement;
                if (!root.TryGetProperty("Teams", out var teamsObj)) return false;
                if (!teamsObj.TryGetProperty(teamName, out var teamObj)) return false;
                if (!teamObj.TryGetProperty("Members", out var membersArr)) return false;

                var membersList = new List<long>();
                foreach (var m in membersArr.EnumerateArray())
                    membersList.Add(m.GetInt64());

                if (membersList.Contains(virtualId))
                    return true; // 已存在

                membersList.Add(virtualId);

                // 重建JSON - 使用JsonSerializer操作
                using var doc2 = System.Text.Json.JsonDocument.Parse(jsonValue);
                using var stream = new System.IO.MemoryStream();
                using var writer = new System.Text.Json.Utf8JsonWriter(stream);
                writer.WriteStartObject();

                // 复制所有属性，修改目标队伍的Members
                foreach (var prop in doc2.RootElement.EnumerateObject())
                {
                    if (prop.Name == "Teams")
                    {
                        writer.WritePropertyName("Teams");
                        writer.WriteStartObject();
                        foreach (var teamProp in prop.Value.EnumerateObject())
                        {
                            if (teamProp.Name == teamName)
                            {
                                writer.WritePropertyName(teamName);
                                writer.WriteStartObject();
                                foreach (var innerProp in teamProp.Value.EnumerateObject())
                                {
                                    if (innerProp.Name == "Members")
                                    {
                                        writer.WritePropertyName("Members");
                                        writer.WriteStartArray();
                                        foreach (var id in membersList)
                                            writer.WriteNumberValue(id);
                                        writer.WriteEndArray();
                                    }
                                    else
                                    {
                                        writer.WritePropertyName(innerProp.Name);
                                        innerProp.Value.WriteTo(writer);
                                    }
                                }
                                writer.WriteEndObject();
                            }
                            else
                            {
                                writer.WritePropertyName(teamProp.Name);
                                teamProp.Value.WriteTo(writer);
                            }
                        }
                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WritePropertyName(prop.Name);
                        prop.Value.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
                writer.Flush();

                var newJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());

                // 写回主库
                using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = "UPDATE GroupData SET value = @value WHERE key = @key";
                updateCmd.Parameters.AddWithValue("@value", newJson);
                updateCmd.Parameters.AddWithValue("@key", groupId.ToString());
                updateCmd.ExecuteNonQuery();

                return true;
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod] AddVirtualIdToMainDb failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从主库的 TeamInfo.Members 移除虚拟ID
        /// </summary>
        private bool RemoveVirtualIdFromMainDb(long groupId, string teamName, long virtualId)
        {
            try
            {
                var launcherBaseDir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                var mainDbPath = System.IO.Path.Combine(launcherBaseDir, "data", "MDiceV2.db");

                if (!System.IO.File.Exists(mainDbPath)) return false;

                using var conn = new SQLiteConnection($"Data Source={mainDbPath};Version=3");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM GroupData WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", groupId.ToString());
                var jsonValue = cmd.ExecuteScalar() as string;
                if (jsonValue == null) return false;

                using var doc = System.Text.Json.JsonDocument.Parse(jsonValue);
                var root = doc.RootElement;
                if (!root.TryGetProperty("Teams", out var teamsObj)) return false;
                if (!teamsObj.TryGetProperty(teamName, out var teamObj)) return false;
                if (!teamObj.TryGetProperty("Members", out var membersArr)) return false;

                var membersList = new List<long>();
                foreach (var m in membersArr.EnumerateArray())
                    membersList.Add(m.GetInt64());

                membersList.Remove(virtualId);

                // 重建JSON
                using var doc2 = System.Text.Json.JsonDocument.Parse(jsonValue);
                using var stream = new System.IO.MemoryStream();
                using var writer = new System.Text.Json.Utf8JsonWriter(stream);
                writer.WriteStartObject();

                foreach (var prop in doc2.RootElement.EnumerateObject())
                {
                    if (prop.Name == "Teams")
                    {
                        writer.WritePropertyName("Teams");
                        writer.WriteStartObject();
                        foreach (var teamProp in prop.Value.EnumerateObject())
                        {
                            if (teamProp.Name == teamName)
                            {
                                writer.WritePropertyName(teamName);
                                writer.WriteStartObject();
                                foreach (var innerProp in teamProp.Value.EnumerateObject())
                                {
                                    if (innerProp.Name == "Members")
                                    {
                                        writer.WritePropertyName("Members");
                                        writer.WriteStartArray();
                                        foreach (var id in membersList)
                                            writer.WriteNumberValue(id);
                                        writer.WriteEndArray();
                                    }
                                    else
                                    {
                                        writer.WritePropertyName(innerProp.Name);
                                        innerProp.Value.WriteTo(writer);
                                    }
                                }
                                writer.WriteEndObject();
                            }
                            else
                            {
                                writer.WritePropertyName(teamProp.Name);
                                teamProp.Value.WriteTo(writer);
                            }
                        }
                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WritePropertyName(prop.Name);
                        prop.Value.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
                writer.Flush();

                var newJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());

                using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = "UPDATE GroupData SET value = @value WHERE key = @key";
                updateCmd.Parameters.AddWithValue("@value", newJson);
                updateCmd.Parameters.AddWithValue("@key", groupId.ToString());
                updateCmd.ExecuteNonQuery();

                return true;
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod] RemoveVirtualIdFromMainDb failed: {ex.Message}");
                return false;
            }
        }

        private bool RemoveVirtualIdsFromMainDb(long groupId, string teamName, IReadOnlyCollection<long> virtualIds)
        {
            if (virtualIds == null || virtualIds.Count == 0)
                return true;

            var allSucceeded = true;
            foreach (var virtualId in virtualIds.Distinct())
            {
                if (!RemoveVirtualIdFromMainDb(groupId, teamName, virtualId))
                    allSucceeded = false;
            }

            return allSucceeded;
        }
        public void OnUnload()
        {
            SaveUserApiSettings();
            _trpgDb?.Dispose();
        }

        public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
        {
            try
            {
                if (_config == null)
                {
                    _context.Log(LogLevel.Error, "[AIMod] Config is null, cannot process message");
                    return null;
                }

                // 检查主Bot是否已关闭（.bot off）；关闭时 AIMod 也不处理消息
                if (!_context.IsBotEnabled(groupId))
                    return null;

                // ── 检测私聊专用指令 ──
                var trimmedContent = content.Trim();
                if (trimmedContent.StartsWith(".ai api", StringComparison.OrdinalIgnoreCase) ||
                    trimmedContent.StartsWith(".ai subapi", StringComparison.OrdinalIgnoreCase) ||
                    trimmedContent.StartsWith(".ai model", StringComparison.OrdinalIgnoreCase) ||
                    trimmedContent.StartsWith(".ai cost", StringComparison.OrdinalIgnoreCase) ||
                    trimmedContent.Equals(".ai api", StringComparison.OrdinalIgnoreCase) ||
                    trimmedContent.Equals(".ai subapi", StringComparison.OrdinalIgnoreCase) ||
                    trimmedContent.Equals(".ai model", StringComparison.OrdinalIgnoreCase) ||
                    trimmedContent.Equals(".ai cost", StringComparison.OrdinalIgnoreCase))
                {
                    _context.Log(LogLevel.Info, $"[AIMod] 群聊中检测到私聊专用指令 from user {userId}: {trimmedContent}");
                    return ModMessageResult.Intercept(
                        "[AIMod] 此指令需要在私聊中使用。\n" +
                        "支持的私聊指令：\n" +
                        "  .ai api <你的API密钥> — 设置主 API Key\n" +
                        "  .ai subapi <你的API密钥> — 设置轻量 API Key\n" +
                        "  .ai api show — 查看当前设置\n" +
                        "  .ai model — 选择模型提供商\n" +
                        "  .ai cost [24h|today|7d|provider|turns N] — 查看 LLM 用量",
                        stopPropagation: true);
                }

                // ── TRPG Player 模式 ──
                if (_config.Mode == AiMode.TRPGPlayer)
                    return HandleTrpgMessage(groupId, userId, content, isAted);

                // ── Prefix / InterceptAll 模式（原有逻辑）──
                var apiKey = _config.SelectedModel switch
                {
                    AiModelType.Gemini => _config.GeminiConfig.ApiKey,
                    AiModelType.ZhipuAI => _config.ZhipuAIConfig.ApiKey,
                    AiModelType.SiliconFlow => _config.SiliconFlowConfig.ApiKey,
                    AiModelType.DeepSeek => _config.DeepSeekConfig.ApiKey,
                    _ => null
                };

                if (string.IsNullOrEmpty(apiKey) ||
                    apiKey == "YOUR_GEMINI_API_KEY" ||
                    apiKey == "YOUR_ZHIPU_API_KEY" ||
                    apiKey == "YOUR_SILICONFLOW_API_KEY" ||
                    apiKey == "YOUR_DEEPSEEK_API_KEY")
                {
                    _context.Log(LogLevel.Warn, "[AIMod] API Key not configured, ignoring message");
                    return null;
                }

                string? matchedPrefix = null;
                if (_config.InterceptAll)
                {
                    matchedPrefix = "";
                }
                else
                {
                    foreach (var rule in _config.PrefixRules.Where(r => r.Enabled))
                    {
                        if (content.StartsWith(rule.Prefix))
                        {
                            matchedPrefix = rule.Prefix;
                            break;
                        }
                    }
                }

                if (matchedPrefix != null)
                {
                    var prompt = content.Substring(matchedPrefix.Length).Trim();
                    try
                    {
                        var response = GetAiResponse(prompt, groupId).Result;
                        if (!string.IsNullOrEmpty(response))
                        {
                            return ModMessageResult.Intercept(response, stopPropagation: true);
                        }
                        else
                        {
                            _context.Log(LogLevel.Warn, $"[AIMod] AI returned empty response for message: {prompt}");
                            return ModMessageResult.Intercept(string.Empty, stopPropagation: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _context.Log(LogLevel.Error, $"[AIMod] Error getting AI response: {ex.Message}");
                        return ModMessageResult.Intercept(string.Empty, stopPropagation: true);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod] Exception in OnGroupMessage: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public ModMessageResult? OnPrivateMessage(long userId, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            var text = content.Trim();
            if (text.StartsWith("。"))
                text = "." + text[1..];

            if (_modelSelectionStates.TryGetValue(userId, out var existingState) &&
                !text.StartsWith(".", StringComparison.Ordinal))
            {
                var focusReply = existingState.Step == ModelSelectionStep.Provider
                    ? HandleModelProviderSelection(userId, text)
                    : HandleModelModelSelection(userId, text);
                return ModMessageResult.Intercept(focusReply, stopPropagation: true);
            }

            var parts = text.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return null;

            var root = parts[0].TrimStart('.').ToLowerInvariant();
            var sub = parts[1].ToLowerInvariant();
            if (root != "ai" || (sub != "api" && sub != "subapi" && sub != "model" && sub != "cost"))
                return null;

            if (sub == "cost")
            {
                var reply = HandleAiCostCommand(parts.Length > 2 ? parts[2].Trim() : "");
                return ModMessageResult.Intercept(reply, stopPropagation: true);
            }

            if (sub == "model")
            {
                var reply = HandleAiModelCommand(userId, parts.Length > 2 ? parts[2].Trim() : "");
                return ModMessageResult.Intercept(reply, stopPropagation: true);
            }

            if (sub == "api" && parts.Length > 2 &&
                parts[2].Trim().StartsWith("usage", StringComparison.OrdinalIgnoreCase))
            {
                return ModMessageResult.Intercept(".ai api usage 仅支持在群聊中由 master 查询。", stopPropagation: true);
            }

            if (parts.Length < 3)
            {
                var usage = "私聊用法：\n.ai api <你的主API Key>\n.ai subapi <你的轻量API Key>\n.ai api clear\n.ai subapi clear\n.ai api show\n.ai cost [24h|today|7d|provider|turns N]";
                return ModMessageResult.Intercept(usage, stopPropagation: true);
            }

            var value = parts[2].Trim();
            if (string.Equals(value, "show", StringComparison.OrdinalIgnoreCase))
            {
                var setting = GetUserApiSetting(userId);
                var status = $"主API: {(string.IsNullOrWhiteSpace(setting.ApiKey) ? "未设置" : MaskApiKey(setting.ApiKey))}\n" +
                             $"轻量API: {(string.IsNullOrWhiteSpace(setting.SubApiKey) ? "未设置" : MaskApiKey(setting.SubApiKey))}";
                return ModMessageResult.Intercept(status, stopPropagation: true);
            }

            var clear = string.Equals(value, "clear", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(value, "reset", StringComparison.OrdinalIgnoreCase);

            if (sub == "api")
            {
                UpdateUserApiSetting(userId, s =>
                {
                    s.ApiKey = clear ? "" : value;
                    s.UpdatedAt = DateTime.UtcNow;
                });

                var reply = clear
                    ? "已清除你的主API Key。"
                    : $"已保存你的主API Key：{MaskApiKey(value)}";
                return ModMessageResult.Intercept(reply, stopPropagation: true);
            }

            UpdateUserApiSetting(userId, s =>
            {
                s.SubApiKey = clear ? "" : value;
                s.UpdatedAt = DateTime.UtcNow;
            });

            var subReply = clear
                ? "已清除你的轻量API Key。"
                : $"已保存你的轻量API Key：{MaskApiKey(value)}";
            return ModMessageResult.Intercept(subReply, stopPropagation: true);
        }

        // ══════════════════════════════════════════
        //  TRPG Player 模式核心
        // ══════════════════════════════════════════

        private string HandleAiModelCommand(long userId, string args)
        {
            if (!string.IsNullOrWhiteSpace(args))
            {
                return _modelSelectionStates.TryGetValue(userId, out var state) &&
                       state.Step == ModelSelectionStep.Model
                    ? HandleModelModelSelection(userId, args)
                    : HandleModelProviderSelection(userId, args);
            }

            _modelSelectionStates[userId] = new ModelSelectionState
            {
                Step = ModelSelectionStep.Provider,
                CreatedAt = DateTime.UtcNow
            };

            return BuildProviderSelectionMenu(userId);
        }

        private string HandleModelProviderSelection(long userId, string input)
        {
            if (IsCancelInput(input))
            {
                _modelSelectionStates.TryRemove(userId, out _);
                return "已取消模型选择。";
            }

            if (!int.TryParse(input.Trim(), out var providerNumber) ||
                providerNumber < 1 ||
                providerNumber > AvailableAiProviders.Count)
            {
                return $"无效的提供商编号，请输入 1-{AvailableAiProviders.Count}，或输入 quit 取消。";
            }

            var providerIndex = providerNumber - 1;
            var provider = AvailableAiProviders[providerIndex];
            _modelSelectionStates[userId] = new ModelSelectionState
            {
                Step = ModelSelectionStep.Model,
                SelectedProviderIndex = providerIndex,
                CreatedAt = DateTime.UtcNow
            };

            var sb = new StringBuilder();
            sb.AppendLine($"已选择提供商: {provider.DisplayName}");
            sb.AppendLine();
            sb.AppendLine("请选择模型:");
            for (int i = 0; i < provider.Models.Count; i++)
                sb.AppendLine($"{i + 1}. {provider.Models[i].DisplayName} ({provider.Models[i].ModelId})");
            sb.AppendLine();
            sb.AppendLine("输入模型编号，back 返回提供商列表，quit 取消。");
            return sb.ToString();
        }

        private string HandleModelModelSelection(long userId, string input)
        {
            if (input.Equals("back", StringComparison.OrdinalIgnoreCase))
            {
                _modelSelectionStates[userId] = new ModelSelectionState
                {
                    Step = ModelSelectionStep.Provider,
                    CreatedAt = DateTime.UtcNow
                };
                return BuildProviderSelectionMenu(userId);
            }

            if (IsCancelInput(input))
            {
                _modelSelectionStates.TryRemove(userId, out _);
                return "已取消模型选择。";
            }

            if (!_modelSelectionStates.TryGetValue(userId, out var state) ||
                state.Step != ModelSelectionStep.Model ||
                state.SelectedProviderIndex < 0 ||
                state.SelectedProviderIndex >= AvailableAiProviders.Count)
            {
                _modelSelectionStates.TryRemove(userId, out _);
                return "模型选择状态已过期，请重新发送 .ai model。";
            }

            var provider = AvailableAiProviders[state.SelectedProviderIndex];
            if (!int.TryParse(input.Trim(), out var modelNumber) ||
                modelNumber < 1 ||
                modelNumber > provider.Models.Count)
            {
                return $"无效的模型编号，请输入 1-{provider.Models.Count}，back 返回，或 quit 取消。";
            }

            var modelIndex = modelNumber - 1;
            var model = provider.Models[modelIndex];
            UpdateUserApiSetting(userId, s =>
            {
                s.SelectedProviderIndex = state.SelectedProviderIndex;
                s.SelectedModelIndex = modelIndex;
                s.UpdatedAt = DateTime.UtcNow;
            });
            _modelSelectionStates.TryRemove(userId, out _);

            return $"模型已切换为: {provider.DisplayName} / {model.DisplayName}\n" +
                   $"如需更新 Key，请发送: .ai api <你的{provider.DisplayName} API Key>";
        }

        private string BuildProviderSelectionMenu(long userId)
        {
            var setting = GetUserApiSetting(userId);
            var selectedProvider = GetSelectedProvider(setting);
            var selectedModel = GetSelectedModel(setting);
            var sb = new StringBuilder();
            sb.AppendLine("可用AI模型提供商:");
            for (int i = 0; i < AvailableAiProviders.Count; i++)
            {
                var provider = AvailableAiProviders[i];
                var marker = provider.Id == selectedProvider.Id ? " *" : "";
                sb.AppendLine($"{i + 1}. {provider.DisplayName}{marker}");
            }
            sb.AppendLine();
            sb.AppendLine($"当前模型: {selectedProvider.DisplayName} / {selectedModel.DisplayName}");
            sb.AppendLine("输入提供商编号，或输入 quit 取消。");
            return sb.ToString();
        }

        private static bool IsCancelInput(string input)
        {
            return input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                   input.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
                   input.Equals("exit", StringComparison.OrdinalIgnoreCase);
        }

        private void InitializeTrpgComponents()
        {
            if (_trpgDb != null) return; // already initialized

            try
            {
                var launcherBaseDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                var trpgDbPath = Path.Combine(launcherBaseDir, "data", "AIMod", "trpg-data.db");
                _context.Log(LogLevel.Info, $"[AIMod:TRPG] Initializing TRPG components with DB path: {trpgDbPath}");

                _trpgDb = new ChatDatabase(trpgDbPath, _context);
                _trpgDb.InitializeSchemaAsync().Wait();
                _context.Log(LogLevel.Info, "[AIMod:TRPG] Database schema initialized");

                _teamDataProvider = new TeamDataProvider(_context);
                _messageRouter = new MessageRouter(_context);
                _promptAssembler = new PromptAssembler(_trpgDb, _config.TrpgConfig);
                _attentionBuffer = new AttentionBuffer();
                
                // 创建日志写入委托
                Action<long, long, string, string>? trpgLogWriter = null;
                try
                {
                    // 获取主程序的TRPGLogManager来写入日志
                    var msgProcessor = MDiceV2.Models.MessageProcessor.Instance;
                    if (msgProcessor?.TrpgLogManager != null)
                    {
                        trpgLogWriter = (groupId, userId, senderName, message) =>
                        {
                            msgProcessor.TrpgLogManager.WriteLog(groupId, userId, senderName, message);
                        };
                    }
                }
                catch (Exception ex)
                {
                    _context.Log(LogLevel.Warn, $"[AIMod] Failed to initialize TRPG log writer: {ex.Message}");
                }
                
                _postProcessor = new PostProcessor(_context, _trpgDb, _attentionBuffer, null, null, null, null, null, null, null, null, null, trpgLogWriter);
                _stateCache = new TrpgStateCache();
                _llmCallTracker = new LlmCallTracker(
                    _trpgDb,
                    _context,
                    messages => CallTrpgApiWithFallbackAsync(messages),
                    ConsumeLastTrpgActualUsage);

                var embeddingCaller = (string text) => CallEmbeddingApiWithFallbackAsync(text);

                // 创建四层架构组件
                var eventLog = new EventLog(_context, _trpgDb);

                // 创建语义蒸馏器
                var semanticDistiller = new SemanticDistiller(
                    _context,
                    _trpgDb,
                    eventLog,
                    messages => CallTrpgApiWithFallbackAsync(messages),
                    _llmCallTracker);

                _memoryWatchdog = new MemoryWatchdog(
                    _trpgDb, _promptAssembler, _context, _config.TrpgConfig,
                    messages => CallTrpgApiWithFallbackAsync(messages),
                    embeddingCaller,
                    _stateCache,
                    semanticDistiller,
                    _llmCallTracker);

                _stateInterceptor = new StateInterceptor(_trpgDb, _stateCache, _context, _attentionBuffer, _memoryWatchdog);

                _contextPipeline = new TrpgContextPipeline(
                    _trpgDb,
                    _stateCache,
                    _config.TrpgConfig,
                    _context,
                    messages => CallTrpgApiWithFallbackAsync(messages),
                    embeddingCaller,
                    _llmCallTracker);
                var entitySalienceService = new EntitySalienceService(_trpgDb, _context);
                var entityCanonicalizer = new EntityCanonicalizer(_context, _trpgDb, entitySalienceService);
                var objectiveLayer = new ObjectiveLayer(_context, _trpgDb);
                var validator = new RuntimeValidator(_context, _trpgDb);
                var projection = new WorldStateProjection(_context, _trpgDb, eventLog, entityCanonicalizer, objectiveLayer);
                var mutationPipeline = new StateMutationPipeline(_context, _trpgDb, validator, eventLog, entityCanonicalizer, objectiveLayer, projection);

                // 创建信息提取模型
                var infoExtractor = new InfoExtractor(
                    _context,
                    _trpgDb,
                    messages => CallTrpgApiWithFallbackAsync(messages),
                    _contextPipeline,
                    entityCanonicalizer,
                    objectiveLayer,
                    _llmCallTracker,
                    entitySalienceService);

                // 创建分层时间轴 Agent
                var episodicMemory = new EpisodicMemory(_context, _trpgDb, eventLog, _config.TrpgConfig.EnableAffectiveMemoryEncoding);
                var archiveToGraph = new ArchiveToGraph(
                    _trpgDb,
                    episodicMemory,
                    _context,
                    messages => CallTrpgApiWithFallbackAsync(messages));
                var sceneTransitionHandler = new SceneTransitionHandler(
                    _trpgDb,
                    _context,
                    messages => CallTrpgApiWithFallbackAsync(messages),
                    archiveToGraph,
                    _llmCallTracker);
                var timelineWriter = new TimelineWriter(
                    _trpgDb,
                    _context,
                    messages => CallTrpgApiWithFallbackAsync(messages),
                    _llmCallTracker);
                var affectiveTagController = _config.TrpgConfig.EnableAffectiveTags
                    ? new AffectiveTagController(_trpgDb, _context)
                    : null;

                _stateInterceptor = new StateInterceptor(_trpgDb, _stateCache, _context, _attentionBuffer, _memoryWatchdog, infoExtractor, mutationPipeline, entityCanonicalizer, timelineWriter, sceneTransitionHandler, affectiveTagController);

                _context.Log(LogLevel.Info, "[AIMod:TRPG] Shared components initialized successfully");
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod:TRPG] Initialization failed: {ex.Message}\n{ex.StackTrace}");
                _trpgDb?.Dispose();
                _trpgDb = null;
                _teamDataProvider = null;
                _messageRouter = null;
                _promptAssembler = null;
                _postProcessor = null;
                _memoryWatchdog = null;
                _stateCache = null;
                _stateInterceptor = null;
                _contextPipeline = null;
                _llmCallTracker = null;
            }
        }

        private AiCharacterSession CreateCharacterSession(TrpgScope scope, AiCharacterEntry character)
        {
            if (_trpgDb == null || _promptAssembler == null || _memoryWatchdog == null || _postProcessor == null || _messageRouter == null || _stateInterceptor == null || _contextPipeline == null)
                throw new InvalidOperationException("TRPG components not initialized");

            character.WorldId = scope.WorldId;
            character.OwnerUserId = scope.OwnerUserId;
            character.GroupId = scope.GroupId;
            character.TeamName = scope.TeamName;

            _trpgDb.UpsertCharacterHotMetaAsync(scope, character.CharacterId, "AI玩家角色", character.DisplayName).GetAwaiter().GetResult();

            // 注册 AI 角色到 EntityCanonical 表，防止 GM 叙述中提到角色名时创建重复实体
            var canonicalizer = new EntityCanonicalizer(_context, _trpgDb);
            var existingEntity = canonicalizer.GetEntityAsync(scope, character.CharacterId).GetAwaiter().GetResult();
            if (existingEntity == null)
            {
                canonicalizer.CreateEntityAsync(scope, character.CharacterId, character.DisplayName, new List<string> { character.DisplayName }).GetAwaiter().GetResult();
            }
            else if (!string.Equals(existingEntity.CurrentDisplayName, character.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                canonicalizer.UpdateDisplayNameAsync(scope, character.CharacterId, character.DisplayName).GetAwaiter().GetResult();
            }

            var session = new AiCharacterSession(
                scope,
                character,
                _trpgDb,
                _promptAssembler,
                _memoryWatchdog,
                _postProcessor,
                _messageRouter,
                _stateInterceptor,
                _contextPipeline,
                _context,
                _config.TrpgConfig,
                messages => CallTrpgApiWithFallbackAsync(messages),
                _llmCallTracker,
                EnterTrpgApiGroupScope,
                ExitTrpgApiGroupScope);

            // 设置 AI 发言广播回调：当此 session 产生回复时，通知同团其他 session 记录历史
            session.OnAiSpeechBroadcast = (sourceId, sourceName, visibleText) =>
            {
                List<AiCharacterSession>? allSessions;
                lock (_activeSessions) { _activeSessions.TryGetValue(character.GroupId, out allSessions); }
                if (allSessions == null) return;
                int skippedOff = 0, skippedSelf = 0, observers = 0;
                foreach (var obs in allSessions)
                {
                    if (obs == session) { skippedSelf++; continue; }
                    if (obs.RuntimeMode == AiRuntimeMode.Off) { skippedOff++; continue; }
                    _ = obs.RecordObservedAiMessageAsync(character.GroupId, sourceId, sourceName, visibleText);
                    observers++;
                }
                _context.Log(LogLevel.Debug,
                    $"[AIMod:TRPG] AiSpeechBroadcastDiagnostics | group={character.GroupId} | source={sourceId} | " +
                    $"observers={observers} | content_length={visibleText.Length} | skipped_off={skippedOff} | skipped_self={skippedSelf}");
            };

            return session;
        }

        private ModMessageResult? HandleTrpgMessage(long groupId, long userId, string content, bool isAted)
        {
            try
            {
                _trpgApiCurrentUserId.Value = userId;
                
                if (_teamDataProvider == null || _messageRouter == null || _trpgDb == null)
                {
                    InitializeTrpgComponents();
                    if (_teamDataProvider == null || _messageRouter == null || _trpgDb == null)
                        return null;
                }

                // 1. 检查该群是否处于 AIMod 关注状态（.logon 已激活）
                List<AiCharacterSession>? sessions;
                lock (_activeSessions)
                {
                    if (!_activeSessions.TryGetValue(groupId, out sessions))
                        return null;
                }
                if (sessions == null || sessions.Count == 0) return null;

                if (sessions.All(session => session.RuntimeMode == AiRuntimeMode.Off))
                {
                    _context.Log(
                        LogLevel.Debug,
                        $"[AIMod:TRPG] All sessions are off, skip message dispatch and API planning (Group={groupId})");
                    return null;
                }

            // 2. 确定发送者所在的队伍（用于消息分类）
            var teamName = _teamDataProvider.GetUserDefaultTeamName(groupId, userId);
            if (string.IsNullOrEmpty(teamName))
            {
                // GM 可能在队伍外，尝试从活跃会话推断队伍名
                teamName = sessions.First().Character.TeamName;
            }
            var team = _teamDataProvider.GetTeamForGroup(groupId, teamName);

            var aiSender = sessions.FirstOrDefault(s => s.Character.VirtualId == userId);
            var echoedOutput = AiOutputEchoGuard.FindRecent(groupId, content);
            if (aiSender != null || echoedOutput != null)
            {
                var sourceCharacterId = aiSender?.Character.CharacterId ?? echoedOutput?.SourceCharacterId ?? "";
                var sourceDisplayName = aiSender?.Character.DisplayName ?? echoedOutput?.SourceDisplayName ?? "AI";

                foreach (var session in sessions)
                {
                    _ = session.RecordObservedAiMessageAsync(groupId, sourceCharacterId, sourceDisplayName, content);
                }

                _context.Log(LogLevel.Debug, $"[AIMod:TRPG] 跳过 AI 输出回声触发 (Group={groupId}, Source={sourceDisplayName})");
                return null;
            }

            // 3. 将消息分发给该群的每个活跃 AI 角色会话（每个角色独立处理）
            var (speakerType, speakerName, _) = _messageRouter.ClassifyAndFormat(
                groupId, userId, content, isAted, team, _config.TrpgConfig.OocPrefix, _context);
            if (speakerType == null)
                return null;

            var actingCharacterIds = ResolveActingCharacters(
                groupId, userId, isAted, speakerType, speakerName, content, sessions, team);

            _context.Log(
                LogLevel.Info,
                $"[AIMod:TRPG] Turn dispatch (Group={groupId}, Speaker={speakerType}-{speakerName}, Actors={actingCharacterIds.Count}/{sessions.Count})");

            foreach (var session in sessions)
            {
                var allowResponse = actingCharacterIds.Contains(session.Character.CharacterId);
                _ = session.HandleMessageAsync(groupId, userId, content, isAted, team, allowResponse);
            }

                return null;
            }
            finally
            {
                _trpgApiCurrentUserId.Value = null;
            }
        }

        private async Task<string?> GetDeepSeekResponse(List<ChatMessage> messages)
        {
            return await CallOpenAiCompatibleApi(
                _config.DeepSeekConfig.ApiKey, _config.DeepSeekConfig.ModelName,
                "https://api.deepseek.com/v1/chat/completions", messages);
        }

        private HashSet<string> ResolveActingCharacters(
            long groupId,
            long userId,
            bool isAted,
            string speakerType,
            string speakerName,
            string content,
            List<AiCharacterSession> sessions,
            TeamSnapshot? team)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sessions.Count == 0)
                return result;

            // 仅 GM / PL 消息参与轮次分发，其它消息仅写入历史
            if (!string.Equals(speakerType, "GM", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(speakerType, "PL", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            try
            {
                var sampleCharacterId = sessions[0].Character.CharacterId;
                var history = _trpgDb!.GetActiveHistoryAsync(sessions[0].Scope, sampleCharacterId).GetAwaiter().GetResult();
                var recentLines = history
                    .TakeLast(12)
                    .Select(x => x.Content.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .TakeLast(8)
                    .ToList();

                var roster = sessions
                    .Select(s => s.Character.DisplayName.Trim())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var teamName = team?.TeamName ?? sessions[0].Character.TeamName;
                var prompt = BuildTurnPlannerPrompt(teamName, speakerType, speakerName, content, isAted, roster, recentLines);
                string? llmResponse;
                EnterTrpgApiGroupScope(groupId);
                try
                {
                    var messages = new List<ChatMessage>
                    {
                        new("system", $"{AimodPromptPrefixes.BackendCommonPrefixV1}\n\n你是TRPG轮次调度器。你只做一件事：判断本条消息后哪些AI角色应该立刻行动。保守判定，不确定时返回 none。"),
                        new("user", prompt)
                    };
                    llmResponse = (_llmCallTracker == null
                        ? CallTrpgApiWithFallbackAsync(messages)
                        : _llmCallTracker.CallAsync(sessions[0].Scope, sampleCharacterId, messages, "TurnPlanner", "ActingCharacterDispatch", CallTrpgApiWithFallbackAsync))
                        .GetAwaiter()
                        .GetResult();
                }
                finally
                {
                    ExitTrpgApiGroupScope();
                }

                if (!string.IsNullOrWhiteSpace(llmResponse))
                {
                    var parsed = ParseTurnPlannerResponse(llmResponse, sessions);
                    if (parsed.Count > 0 || ContainsNoneMarker(llmResponse))
                        return parsed;
                }
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Turn planner failed, fallback to heuristic: {ex.Message}");
            }

            return HeuristicActingCharacters(userId, isAted, content, sessions, team);
        }

        private static string BuildTurnPlannerPrompt(
            string teamName,
            string speakerType,
            string speakerName,
            string content,
            bool isAted,
            List<string> roster,
            List<string> recentLines)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[队伍] {teamName}");
            sb.AppendLine($"[发言者] {speakerType}-{speakerName}");
            sb.AppendLine($"[被@] {(isAted ? "是" : "否")}");
            sb.AppendLine("[AI角色名单]");
            foreach (var name in roster)
                sb.AppendLine($"- {name}");

            sb.AppendLine();
            sb.AppendLine("[最近上下文]");
            if (recentLines.Count == 0)
            {
                sb.AppendLine("- 无");
            }
            else
            {
                foreach (var line in recentLines)
                    sb.AppendLine($"- {line}");
            }

            sb.AppendLine();
            sb.AppendLine("[最新消息]");
            sb.AppendLine(content);
            sb.AppendLine();
            sb.AppendLine("判定规则：");
            sb.AppendLine("1. 选择“现在该行动，对话或者做出反应”的角色，可多选。");
            sb.AppendLine("2. “你们/所有人/全员/大家”这类群体指令可返回 all。");
            sb.AppendLine();
            sb.AppendLine("严格按以下格式输出：");
            sb.AppendLine("<act>none | all | 角色名1,角色名2</act>");
            sb.AppendLine("<reason>不超过20字</reason>");
            return sb.ToString();
        }

        private static HashSet<string> ParseTurnPlannerResponse(string response, List<AiCharacterSession> sessions)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(response))
                return result;

            var match = Regex.Match(response, @"<act>\s*(.*?)\s*</act>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var actText = match.Success ? match.Groups[1].Value.Trim() : response.Trim();
            if (string.IsNullOrWhiteSpace(actText) || ContainsNoneMarker(actText))
                return result;

            if (Regex.IsMatch(actText, @"\b(all|everyone)\b|全部|全员|所有人|大家", RegexOptions.IgnoreCase))
            {
                foreach (var session in sessions)
                    TryAddCharacterId(result, session.Character.CharacterId);
                return result;
            }

            var tokens = actText
                .Split(new[] { ',', '，', '、', ';', '；', '|', '/', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            foreach (var token in tokens)
            {
                foreach (var session in sessions)
                {
                    var displayName = session.Character.DisplayName?.Trim() ?? "";
                    var characterId = session.Character.CharacterId?.Trim() ?? "";
                    var shortId = characterId.Contains('_')
                        ? characterId[(characterId.LastIndexOf('_') + 1)..]
                        : characterId;

                    if (token.Equals(displayName, StringComparison.OrdinalIgnoreCase) ||
                        token.Equals(characterId, StringComparison.OrdinalIgnoreCase) ||
                        token.Equals(shortId, StringComparison.OrdinalIgnoreCase))
                    {
                        TryAddCharacterId(result, session.Character.CharacterId);
                    }
                }
            }

            if (result.Count == 0)
            {
                foreach (var session in sessions)
                {
                    var displayName = session.Character.DisplayName;
                    if (!string.IsNullOrWhiteSpace(displayName) &&
                        actText.Contains(displayName, StringComparison.OrdinalIgnoreCase))
                    {
                        TryAddCharacterId(result, session.Character.CharacterId);
                    }
                }
            }

            return result;
        }

        private static HashSet<string> HeuristicActingCharacters(
            long userId,
            bool isAted,
            string content,
            List<AiCharacterSession> sessions,
            TeamSnapshot? team)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sessions.Count == 0)
                return result;

            if (Regex.IsMatch(content, @"所有人|全员|大家|你们|全部", RegexOptions.IgnoreCase))
            {
                foreach (var session in sessions)
                    TryAddCharacterId(result, session.Character.CharacterId);
                return result;
            }

            foreach (var session in sessions)
            {
                if (!string.IsNullOrWhiteSpace(session.Character.DisplayName) &&
                    content.Contains(session.Character.DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    TryAddCharacterId(result, session.Character.CharacterId);
                }
            }

            if (result.Count > 0)
                return result;

            var senderSession = sessions.FirstOrDefault(s => s.Character.VirtualId == userId);
            if (senderSession != null)
                return result;

            if (team != null && team.Members.Contains(userId) && sessions.Count > 1)
                return result;

            if (isAted && sessions.Count == 1)
                result.Add(sessions[0].Character.CharacterId);

            return result;
        }

        private static bool ContainsNoneMarker(string text)
        {
            return Regex.IsMatch(text, @"\bnone\b|无|无人|不行动|不需要行动|跳过", RegexOptions.IgnoreCase);
        }

        private static void TryAddCharacterId(HashSet<string> target, string? characterId)
        {
            if (!string.IsNullOrWhiteSpace(characterId))
                target.Add(characterId);
        }

        private void EnterTrpgApiGroupScope(long groupId)
        {
            _trpgApiGroupScope.Value = groupId;
        }

        private void ExitTrpgApiGroupScope()
        {
            _trpgApiGroupScope.Value = null;
        }

        private static TrpgScope CreateTrpgScope(TeamSnapshot team)
        {
            return TrpgScope.Create(team.CreatorId, team.GroupId, team.TeamName);
        }

        private ActiveGroupApiContext? GetCurrentActiveApiContext()
        {
            var gid = _trpgApiGroupScope.Value;
            if (!gid.HasValue)
                return null;

            return _activeGroupApiContexts.TryGetValue(gid.Value, out var ctx) ? ctx : null;
        }

        private static bool IsConfiguredApiKey(string? apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return false;

            var trimmed = apiKey.Trim();
            if (trimmed.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase))
                return false;
            return trimmed.Length >= 8;
        }

        private static string MaskApiKey(string? apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return "(empty)";

            var trimmed = apiKey.Trim();
            if (trimmed.Length <= 8)
                return $"{trimmed[0]}***{trimmed[^1]}";
            return $"{trimmed[..4]}***{trimmed[^4..]}";
        }

        private void TryNotifyUserApiFallback(ActiveGroupApiContext? apiContext, string warning)
        {
            if (apiContext == null || string.IsNullOrWhiteSpace(warning))
                return;

            _context.Log(
                LogLevel.Info,
                $"[AIMod:TRPG] Suppressed fallback warning for Group={apiContext.GroupId}: {warning}");
        }

        /// <summary>
        /// 权限不足时自动关闭该群的所有AI会话，避免后续每次request都重复触发警告。
        /// </summary>
        private void AutoDeactivateLogSessions(long groupId)
        {
            List<AiCharacterSession>? sessions = null;
            lock (_activeSessions)
            {
                if (_activeSessions.TryGetValue(groupId, out var list))
                {
                    sessions = new List<AiCharacterSession>(list);
                    _activeSessions.Remove(groupId);
                }
            }

            if (sessions == null || sessions.Count == 0)
                return;

            foreach (var session in sessions)
            {
                try
                {
                    _trpgDb?.SetAiCharacterActiveAsync(session.Scope, session.Character.CharacterId, false)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Auto-deactivate character failed: {ex.Message}");
                }
            }

            _activeGroupApiContexts.TryRemove(groupId, out _);
            _apiWarningCooldown.TryRemove(groupId, out _);

            TryNotifyGroupMessage(groupId,
                "⚠️ 权限不足，跑团已自动关闭。如需重新启动，请先设置API密钥（私聊 .ai api <key>）或联系管理员获取权限。");
        }

        private void TryNotifyGroupMessage(long? groupId, string message)
        {
            if (!groupId.HasValue || string.IsNullOrWhiteSpace(message))
                return;

            try
            {
                _context.SendGroupMessage(groupId.Value, $"[AIMod:TRPG] {message}");
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] Failed to send warning message: {ex.Message}");
            }
        }

        private void InitializeUserApiSettingsStore()
        {
            var launcherBaseDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            var storeDir = Path.Combine(launcherBaseDir, "data", "AIMod");
            Directory.CreateDirectory(storeDir);
            _userApiSettingsPath = Path.Combine(storeDir, "user-api-keys.json");
        }

        private void LoadUserApiSettings()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_userApiSettingsPath) || !File.Exists(_userApiSettingsPath))
                    return;

                var json = File.ReadAllText(_userApiSettingsPath);
                var data = JsonSerializer.Deserialize<Dictionary<string, UserApiSetting>>(json) ??
                           new Dictionary<string, UserApiSetting>();
                _userApiSettings.Clear();
                foreach (var kv in data)
                {
                    if (long.TryParse(kv.Key, out var userId) && kv.Value != null)
                    {
                        _userApiSettings[userId] = kv.Value;
                    }
                }
                _context.Log(LogLevel.Info, $"[AIMod] Loaded user API settings: {_userApiSettings.Count}");
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod] Failed to load user API settings: {ex.Message}");
            }
        }

        private void SaveUserApiSettings()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_userApiSettingsPath))
                    return;

                var output = new Dictionary<string, UserApiSetting>();
                foreach (var kv in _userApiSettings)
                {
                    output[kv.Key.ToString()] = kv.Value;
                }

                var json = JsonSerializer.Serialize(output, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(_userApiSettingsPath, json);
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod] Failed to save user API settings: {ex.Message}");
            }
        }

        private UserApiSetting GetUserApiSetting(long userId)
        {
            return _userApiSettings.TryGetValue(userId, out var setting)
                ? setting
                : new UserApiSetting();
        }

        private void UpdateUserApiSetting(long userId, Action<UserApiSetting> updater)
        {
            var setting = _userApiSettings.AddOrUpdate(
                userId,
                _ => new UserApiSetting(),
                (_, old) => old);
            updater(setting);
            _userApiSettings[userId] = setting;
            SaveUserApiSettings();
        }

        private static AIProvider GetSelectedProvider(UserApiSetting setting)
        {
            var providerIndex = Math.Clamp(setting.SelectedProviderIndex, 0, AvailableAiProviders.Count - 1);
            return AvailableAiProviders[providerIndex];
        }

        private static AIModel GetSelectedModel(UserApiSetting setting)
        {
            var provider = GetSelectedProvider(setting);
            var modelIndex = Math.Clamp(setting.SelectedModelIndex, 0, provider.Models.Count - 1);
            return provider.Models[modelIndex];
        }

        private static string GetDefaultModelId(AIProvider provider)
        {
            return provider.Models.Count > 0 ? provider.Models[0].ModelId : "";
        }

        private static string ResolveProviderIdFromEndpoint(string apiUrl)
        {
            if (apiUrl.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
                return "deepseek";
            if (apiUrl.Contains("bigmodel", StringComparison.OrdinalIgnoreCase) ||
                apiUrl.Contains("zhipu", StringComparison.OrdinalIgnoreCase))
                return "zhipu";
            if (apiUrl.Contains("siliconflow", StringComparison.OrdinalIgnoreCase))
                return "siliconflow";
            if (apiUrl.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
                return "gemini";
            return "openai-compatible";
        }

        private void ExtractAndUpdateTokenUsage(long? userId, JsonElement responseBody, string providerId)
        {
            if (!userId.HasValue)
                return;

            try
            {
                var usage = ParseTokenUsage(responseBody, providerId);
                if (usage.TotalTokens > 0)
                    UpdateTokenUsage(userId.Value, usage);
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod] Failed to extract {providerId} token usage: {ex.Message}");
            }
        }

        private LlmActualUsage? ConsumeLastTrpgActualUsage()
        {
            var usage = _lastTrpgActualUsage.Value;
            _lastTrpgActualUsage.Value = null;
            return usage;
        }

        private LlmActualUsage? PeekLastTrpgActualUsage()
        {
            return _lastTrpgActualUsage.Value;
        }

        private void ClearLastTrpgActualUsage()
        {
            _lastTrpgActualUsage.Value = null;
        }

        private void CaptureLastTrpgActualUsage(string providerId, string modelName, TokenUsageStats usage)
        {
            if (usage.TotalTokens <= 0 && usage.PromptTokens <= 0 && usage.CompletionTokens <= 0)
                return;

            var existing = _lastTrpgActualUsage.Value ?? new LlmActualUsage();
            existing.Provider = string.IsNullOrWhiteSpace(providerId) ? "openai-compatible" : providerId;
            existing.Model = string.IsNullOrWhiteSpace(modelName) ? "unknown" : modelName;
            existing.InputTokens = usage.PromptTokens;
            existing.OutputTokens = usage.CompletionTokens;
            existing.TotalTokens = usage.TotalTokens;
            existing.CachedInputTokens = usage.HasCacheMetrics
                ? (usage.CacheHitTokens + usage.CacheMissTokens > 0
                    ? usage.CacheHitTokens + usage.CacheMissTokens
                    : usage.CacheHitTokens)
                : null;
            existing.CacheHitTokens = usage.HasCacheMetrics ? usage.CacheHitTokens : null;
            existing.CacheMissTokens = usage.HasCacheMetrics ? usage.CacheMissTokens : null;
            _lastTrpgActualUsage.Value = existing;
        }

        private void MarkLastTrpgApiSource(string apiSourceKind, ActiveGroupApiContext? apiContext)
        {
            var existing = _lastTrpgActualUsage.Value ?? new LlmActualUsage();
            existing.ApiSourceKind = apiSourceKind;
            existing.IsCommonDefaultApi = apiSourceKind == ApiSourceDefaultPrimary || apiSourceKind == ApiSourceDefaultSecondary;
            if (apiContext != null && apiContext.OwnerUserId > 0)
            {
                existing.OwnerUserId = apiContext.OwnerUserId;
                existing.OwnerResolution = "active_group_context";
            }
            else if (_trpgApiCurrentUserId.Value.HasValue && _trpgApiCurrentUserId.Value.Value > 0)
            {
                existing.OwnerUserId = _trpgApiCurrentUserId.Value.Value;
                existing.OwnerResolution = "current_user_context";
            }
            else
            {
                existing.OwnerUserId = 0;
                existing.OwnerResolution = "unknown";
            }

            _lastTrpgActualUsage.Value = existing;
        }

        private void TryRecordStandaloneCommonApiUsage(
            ActiveGroupApiContext? apiContext,
            bool success,
            string providerFallback,
            string modelFallback,
            string agentName,
            string requestKind,
            int inputCharCount,
            int outputCharCount = 0)
        {
            var usage = PeekLastTrpgActualUsage();
            if (usage?.IsCommonDefaultApi != true || _trpgDb == null)
            {
                ClearLastTrpgActualUsage();
                return;
            }

            var ownerResolution = string.IsNullOrWhiteSpace(usage.OwnerResolution) ? "unknown" : usage.OwnerResolution;
            var ownerUserId = usage.OwnerUserId;
            var groupId = apiContext?.GroupId ?? 0;
            var teamName = apiContext?.TeamName;
            var worldId = apiContext != null && ownerUserId > 0 && !string.IsNullOrWhiteSpace(teamName)
                ? TrpgScope.Create(ownerUserId, apiContext.GroupId, teamName).WorldId
                : null;
            var inputTokens = usage.InputTokens > 0 ? usage.InputTokens : Math.Max(0, (long)Math.Ceiling(inputCharCount / 3.5));
            var outputTokens = usage.OutputTokens > 0 ? usage.OutputTokens : Math.Max(0, (long)Math.Ceiling(outputCharCount / 3.5));
            var totalTokens = usage.TotalTokens > 0 ? usage.TotalTokens : inputTokens + outputTokens;
            var estimatedCost = (inputTokens / 1_000_000m * 0.27m) + (outputTokens / 1_000_000m * 1.10m);

            try
            {
                _trpgDb.InsertCommonApiUsageLogAsync(new CommonApiUsageLogEntry
                {
                    CreatedAt = DateTime.UtcNow,
                    UserId = ownerUserId,
                    GroupId = groupId,
                    WorldId = worldId,
                    TeamName = teamName,
                    Provider = string.IsNullOrWhiteSpace(usage.Provider) ? providerFallback : usage.Provider,
                    Model = string.IsNullOrWhiteSpace(usage.Model) ? modelFallback : usage.Model,
                    AgentName = agentName,
                    RequestKind = requestKind,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = totalTokens,
                    CachedInputTokens = usage.CachedInputTokens,
                    CacheHitTokens = usage.CacheHitTokens,
                    CacheMissTokens = usage.CacheMissTokens,
                    EstimatedCost = estimatedCost,
                    Success = success,
                    Metadata = JsonSerializer.Serialize(new
                    {
                        owner_resolution = ownerResolution,
                        api_source = usage.ApiSourceKind,
                        standalone = true
                    })
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] CommonApiUsageLog standalone insert skipped: {ex.Message}");
            }
            finally
            {
                ClearLastTrpgActualUsage();
            }
        }

        private static TokenUsageStats ParseTokenUsage(JsonElement responseBody, string providerId)
        {
            providerId = (providerId ?? "").Trim().ToLowerInvariant();
            return providerId switch
            {
                "gemini" => ParseGeminiTokenUsage(responseBody),
                "deepseek" => ParseDeepSeekTokenUsage(responseBody),
                "zhipu" => ParseOpenAiCompatibleTokenUsage(responseBody, "zhipu"),
                "siliconflow" => ParseOpenAiCompatibleTokenUsage(responseBody, "siliconflow"),
                _ => ParseOpenAiCompatibleTokenUsage(responseBody, providerId)
            };
        }

        private static TokenUsageStats ParseDeepSeekTokenUsage(JsonElement responseBody)
        {
            if (!responseBody.TryGetProperty("usage", out var usage))
                return TokenUsageStats.Empty("deepseek");

            var hasCacheHit = usage.TryGetProperty("prompt_cache_hit_tokens", out _);
            var hasCacheMiss = usage.TryGetProperty("prompt_cache_miss_tokens", out _);
            var cacheHitTokens = GetJsonLong(usage, "prompt_cache_hit_tokens") ?? 0;
            var cacheMissTokens = GetJsonLong(usage, "prompt_cache_miss_tokens") ?? 0;
            var promptTokens = GetJsonLong(usage, "prompt_tokens") ??
                               PositiveOrNull(cacheHitTokens + cacheMissTokens) ??
                               0;
            var completionTokens = GetJsonLong(usage, "completion_tokens") ?? 0;
            var reasoningTokens = 0L;
            if (usage.TryGetProperty("completion_tokens_details", out var details))
                reasoningTokens = GetJsonLong(details, "reasoning_tokens") ?? 0;

            var totalTokens = GetJsonLong(usage, "total_tokens") ??
                              PositiveOrNull(promptTokens + completionTokens) ??
                              PositiveOrNull(cacheHitTokens + cacheMissTokens + completionTokens) ??
                              0;

            return new TokenUsageStats(
                "deepseek",
                promptTokens,
                completionTokens,
                totalTokens,
                cacheHitTokens,
                cacheMissTokens,
                reasoningTokens,
                hasCacheHit || hasCacheMiss);
        }

        private static TokenUsageStats ParseGeminiTokenUsage(JsonElement responseBody)
        {
            if (!responseBody.TryGetProperty("usageMetadata", out var usage))
                return TokenUsageStats.Empty("gemini");

            var promptTokens = GetJsonLong(usage, "promptTokenCount") ?? 0;
            var completionTokens = GetJsonLong(usage, "candidatesTokenCount") ?? 0;
            var totalTokens = GetJsonLong(usage, "totalTokenCount") ??
                              PositiveOrNull(promptTokens + completionTokens) ??
                              0;

            return new TokenUsageStats("gemini", promptTokens, completionTokens, totalTokens, 0, 0, 0, false);
        }

        private static TokenUsageStats ParseOpenAiCompatibleTokenUsage(JsonElement responseBody, string providerId)
        {
            if (!responseBody.TryGetProperty("usage", out var usage))
                return TokenUsageStats.Empty(providerId);

            var promptTokens = GetJsonLong(usage, "prompt_tokens") ??
                               GetJsonLong(usage, "input_tokens") ??
                               0;
            var completionTokens = GetJsonLong(usage, "completion_tokens") ??
                                   GetJsonLong(usage, "output_tokens") ??
                                   0;
            var cachedTokens = 0L;
            var hasCachedTokens = false;
            if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails))
            {
                hasCachedTokens = promptDetails.TryGetProperty("cached_tokens", out _);
                cachedTokens = GetJsonLong(promptDetails, "cached_tokens") ?? 0;
            }

            var totalTokens = GetJsonLong(usage, "total_tokens") ??
                              PositiveOrNull(promptTokens + completionTokens) ??
                              0;

            return new TokenUsageStats(providerId, promptTokens, completionTokens, totalTokens, cachedTokens, 0, 0, hasCachedTokens);
        }

        private static long? PositiveOrNull(long value)
        {
            return value > 0 ? value : null;
        }

        private static int? GetJsonInt(JsonElement element, string propertyName)
        {
            var value = GetJsonLong(element, propertyName);
            return value.HasValue && value.Value <= int.MaxValue ? (int)value.Value : null;
        }

        private static long? GetJsonLong(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.String when long.TryParse(value.GetString(), out var longValue) => longValue,
                _ => null
            };
        }

        private void UpdateTokenUsage(long userId, TokenUsageStats usage)
        {
            var delta = Math.Max(0, usage.TotalTokens);
            if (delta <= 0)
                return;

            UserApiSetting updated = null!;
            long previousCount = 0;
            _userApiSettings.AddOrUpdate(
                userId,
                _ =>
                {
                    updated = new UserApiSetting { TokenUsageCount = delta };
                    return updated;
                },
                (_, old) =>
                {
                    previousCount = old.TokenUsageCount;
                    old.TokenUsageCount += delta;
                    old.UpdatedAt = DateTime.UtcNow;
                    updated = old;
                    return old;
                });

            var newCount = updated.TokenUsageCount;
            var previousMilestone = previousCount / TokenWarningStep;
            var newMilestone = newCount / TokenWarningStep;
            if (newMilestone > previousMilestone)
            {
                updated.LastTokenWarningAt = DateTime.UtcNow;
                TrySendTokenUsageWarning(userId, newCount, newMilestone);
            }

            SaveUserApiSettings();
            _context.Log(
                LogLevel.Info,
                $"[AIMod] User {userId} {usage.ProviderId} token usage +{delta}, total={newCount}, " +
                $"prompt={usage.PromptTokens}, completion={usage.CompletionTokens}, " +
                $"cacheHit={usage.CacheHitTokens}, cacheMiss={usage.CacheMissTokens}, reasoning={usage.ReasoningTokens}");
        }

        private void TrySendTokenUsageWarning(long userId, long tokenCount, long milestone)
        {
            try
            {
                var message = $"AI Token 使用提醒：你的累计用量已达到 {milestone:N0} 百万 token 档位。\n" +
                              $"当前累计：{tokenCount:N0} tokens。\n" +
                              "此提醒会在每新增 1,000,000 token 时再次发送。";
                _context.SendPrivateMessage(userId, message);
                _context.Log(LogLevel.Warn, $"[AIMod] Token usage warning sent to user {userId}: {tokenCount}");
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod] Failed to send token usage warning to {userId}: {ex.Message}");
            }
        }

        private async Task<string?> CallTrpgApiWithFallbackAsync(List<ChatMessage> messages)
        {
            var apiContext = GetCurrentActiveApiContext();
            var scopedGroupId = _trpgApiGroupScope.Value ?? apiContext?.GroupId;
            if (scopedGroupId.HasValue && AreAllGroupSessionsOff(scopedGroupId.Value))
            {
                ClearLastTrpgActualUsage();
                _context.Log(
                    LogLevel.Debug,
                    $"[AIMod:TRPG] All sessions are off, skip outbound chat API request (Group={scopedGroupId.Value})");
                return null;
            }

            var userSetting = apiContext == null ? new UserApiSetting() : GetUserApiSetting(apiContext.OwnerUserId);
            var secConfig = _config.TrpgConfig;
            var userPrimarySpecified = !string.IsNullOrWhiteSpace(userSetting.ApiKey);
            var userPrimaryConfigured = IsConfiguredApiKey(userSetting.ApiKey);
            var userSubConfigured = IsConfiguredApiKey(userSetting.SubApiKey);

            if (userSubConfigured && !string.IsNullOrWhiteSpace(secConfig.SecondaryEndpoint))
            {
                try
                {
                    MarkLastTrpgApiSource(ApiSourceUserSub, apiContext);
                    var result = await CallOpenAiCompatibleApi(
                        userSetting.SubApiKey, secConfig.SecondaryModel,
                        secConfig.SecondaryEndpoint, messages, apiContext?.OwnerUserId);
                    if (result != null)
                    {
                        _context.Log(LogLevel.Info, $"[AIMod:TRPG] Used user sub API for TRPG task: {MaskApiKey(userSetting.SubApiKey)}");
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _context.Log(LogLevel.Warn, $"[AIMod:TRPG] User sub API failed, fallback: {ex.Message}");
                }
            }

            var userPrimaryFailed = false;
            if (userPrimarySpecified && !userPrimaryConfigured)
            {
                userPrimaryFailed = true;
                TryNotifyUserApiFallback(apiContext, $"用户主API格式无效：{MaskApiKey(userSetting.ApiKey)}。已回退通用API。\n私聊发送：.ai api <key>\n可选轻量：.ai subapi <key>");
            }
            else if (userPrimaryConfigured)
            {
                MarkLastTrpgApiSource(ApiSourceUserPrimary, apiContext);
                var userResult = await CallSelectedProviderChatApiAsync(
                    apiContext?.OwnerUserId, userSetting.ApiKey, userSetting, messages);
                if (!string.IsNullOrWhiteSpace(userResult))
                {
                    var provider = GetSelectedProvider(userSetting);
                    var model = GetSelectedModel(userSetting);
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] Used user primary API: {provider.DisplayName}/{model.ModelId} {MaskApiKey(userSetting.ApiKey)}");
                    return userResult;
                }
                userPrimaryFailed = true;
                TryNotifyUserApiFallback(apiContext, $"用户主API调用失败：{MaskApiKey(userSetting.ApiKey)}。正在回退通用API。\n私聊发送：.ai api <key>\n可选轻量：.ai subapi <key>");
            }

            // 检查log owner是否有权限使用通用API（owner的权限在整个log期间通用）
            var ownerUserId = apiContext?.OwnerUserId;
            if (ownerUserId.HasValue)
            {
                var userAuthLevel = _context.GetUserAuthLevel(ownerUserId.Value);
                // 只有 AuthLevel <= 1 的用户才能使用通用API
                if (!userAuthLevel.HasValue || userAuthLevel.Value > 1)
                {
                    var now = DateTime.UtcNow;
                    var shouldWarn = !_apiWarningCooldown.TryGetValue(apiContext?.GroupId ?? 0, out var last) ||
                                     now - last >= TimeSpan.FromMinutes(3);

                    if (shouldWarn)
                    {
                        _apiWarningCooldown[apiContext?.GroupId ?? 0] = now;
                        if (userPrimaryFailed)
                        {
                            TryNotifyGroupMessage(apiContext?.GroupId,
                                $"用户主API调用失败，但您没有权限使用通用API。需要权限等级 <= 1（1级白名单）。");
                        }
                        else
                        {
                            TryNotifyGroupMessage(apiContext?.GroupId,
                                $"❌ 没有权限使用通用API。需要权限等级 <= 1（1级白名单）。如需使用，请联系管理员。");
                        }
                    }

                    _context.Log(LogLevel.Info,
                        $"[AIMod:TRPG] User {ownerUserId} (AuthLevel={userAuthLevel}) denied access to public API");
                    
                    // 自动关闭AI响应，避免后续每次request重复触发警告
                    if (apiContext != null)
                        AutoDeactivateLogSessions(apiContext.GroupId);
                    
                    return null;
                }
            }

            if (IsConfiguredApiKey(secConfig.SecondaryApiKey) && !string.IsNullOrWhiteSpace(secConfig.SecondaryEndpoint))
            {
                MarkLastTrpgApiSource(ApiSourceDefaultSecondary, apiContext);
                var defaultSub = await CallOpenAiCompatibleApi(
                    secConfig.SecondaryApiKey, secConfig.SecondaryModel,
                    secConfig.SecondaryEndpoint, messages, apiContext?.OwnerUserId);
                if (!string.IsNullOrWhiteSpace(defaultSub))
                {
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] Used default secondary API: {MaskApiKey(secConfig.SecondaryApiKey)}");
                    return defaultSub;
                }
            }

            if (IsConfiguredApiKey(_config.DeepSeekConfig.ApiKey))
            {
                MarkLastTrpgApiSource(ApiSourceDefaultPrimary, apiContext);
                var defaultPrimary = await CallOpenAiCompatibleApi(
                    _config.DeepSeekConfig.ApiKey, _config.DeepSeekConfig.ModelName,
                    "https://api.deepseek.com/v1/chat/completions", messages, apiContext?.OwnerUserId);
                if (!string.IsNullOrWhiteSpace(defaultPrimary))
                {
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] Used default primary API: {MaskApiKey(_config.DeepSeekConfig.ApiKey)}");
                    return defaultPrimary;
                }
            }

            return null;
        }

        private async Task<string?> CallSelectedProviderChatApiAsync(
            long? tokenUserId,
            string apiKey,
            UserApiSetting setting,
            List<ChatMessage> messages)
        {
            var provider = GetSelectedProvider(setting);
            var model = GetSelectedModel(setting);

            if (provider.Id == "gemini")
                return await CallGeminiChatApiAsync(apiKey, model.ModelId, messages, tokenUserId);

            var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
                ? "https://api.deepseek.com/v1/chat/completions"
                : provider.Endpoint;
            var modelId = string.IsNullOrWhiteSpace(model.ModelId)
                ? GetDefaultModelId(provider)
                : model.ModelId;
            return await CallOpenAiCompatibleApi(apiKey, modelId, endpoint, messages, tokenUserId);
        }

        private async Task<string?> CallGeminiChatApiAsync(
            string apiKey,
            string modelName,
            List<ChatMessage> messages,
            long? tokenUserId)
        {
            try
            {
                var prompt = new StringBuilder();
                foreach (var message in messages)
                {
                    prompt.AppendLine($"{message.Role}:");
                    prompt.AppendLine(message.Content ?? string.Empty);
                    prompt.AppendLine();
                }

                var requestBody = new
                {
                    contents = new[]
                    {
                        new { parts = new[] { new { text = prompt.ToString() } } }
                    }
                };

                var apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = JsonContent.Create(requestBody)
                };

                var response = await _retryPolicy.ExecuteAsync(() => _httpClient.SendAsync(request));
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _context.Log(LogLevel.Error, $"[AIMod] Gemini HTTP Error {response.StatusCode}: {errorContent.Substring(0, Math.Min(200, errorContent.Length))}");
                    return null;
                }

                var responseBody = await response.Content.ReadFromJsonAsync<JsonElement>();
                var usage = ParseTokenUsage(responseBody, "gemini");
                CaptureLastTrpgActualUsage("gemini", modelName, usage);
                ExtractAndUpdateTokenUsage(tokenUserId, responseBody, "gemini");
                return responseBody
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod] Gemini selected-provider API error: {ex.Message}");
                return null;
            }
        }

        private async Task<float[]?> CallEmbeddingApiWithFallbackAsync(string text)
        {
            ClearLastTrpgActualUsage();
            var apiContext = GetCurrentActiveApiContext();
            var scopedGroupId = _trpgApiGroupScope.Value ?? apiContext?.GroupId;
            if (scopedGroupId.HasValue && AreAllGroupSessionsOff(scopedGroupId.Value))
            {
                _context.Log(
                    LogLevel.Debug,
                    $"[AIMod:TRPG] All sessions are off, skip outbound embedding API request (Group={scopedGroupId.Value})");
                return null;
            }

            var userSetting = apiContext == null ? new UserApiSetting() : GetUserApiSetting(apiContext.OwnerUserId);
            var secConfig = _config.TrpgConfig;
            var userPrimarySpecified = !string.IsNullOrWhiteSpace(userSetting.ApiKey);
            var userPrimaryConfigured = IsConfiguredApiKey(userSetting.ApiKey);
            var userSubConfigured = IsConfiguredApiKey(userSetting.SubApiKey);

            if (userSubConfigured && !string.IsNullOrWhiteSpace(secConfig.SecondaryEndpoint))
            {
                try
                {
                    MarkLastTrpgApiSource(ApiSourceUserSub, apiContext);
                    var result = await CallEmbeddingApiAsync(
                        userSetting.SubApiKey, secConfig.SecondaryModel,
                        secConfig.SecondaryEndpoint, text, apiContext?.OwnerUserId);
                    if (result != null)
                    {
                        _context.Log(LogLevel.Info, $"[AIMod:TRPG] Used user sub API for embedding: {MaskApiKey(userSetting.SubApiKey)}");
                        ClearLastTrpgActualUsage();
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _context.Log(LogLevel.Warn, $"[AIMod:TRPG] User sub embedding API failed, fallback: {ex.Message}");
                }
            }

            var userPrimaryFailed = false;
            if (userPrimarySpecified && !userPrimaryConfigured)
            {
                userPrimaryFailed = true;
                TryNotifyUserApiFallback(apiContext, $"用户主API格式无效：{MaskApiKey(userSetting.ApiKey)}。已回退通用API。\n私聊发送：.ai api <key>\n可选轻量：.ai subapi <key>");
            }
            else if (userPrimaryConfigured)
            {
                MarkLastTrpgApiSource(ApiSourceUserPrimary, apiContext);
                var userEmbedding = await CallEmbeddingApiAsync(
                    userSetting.ApiKey, _config.DeepSeekConfig.ModelName,
                    "https://api.deepseek.com/v1/embeddings", text, apiContext?.OwnerUserId);
                if (userEmbedding != null)
                {
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] Used user primary embedding API: {MaskApiKey(userSetting.ApiKey)}");
                    ClearLastTrpgActualUsage();
                    return userEmbedding;
                }

                userPrimaryFailed = true;
                TryNotifyUserApiFallback(apiContext, $"用户主API向量调用失败：{MaskApiKey(userSetting.ApiKey)}。正在回退通用API。\n私聊发送：.ai api <key>\n可选轻量：.ai subapi <key>");
            }

            // 检查log owner是否有权限使用通用API（owner的权限在整个log期间通用）
            var ownerUserId = apiContext?.OwnerUserId;
            if (ownerUserId.HasValue)
            {
                var userAuthLevel = _context.GetUserAuthLevel(ownerUserId.Value);
                // 只有 AuthLevel <= 1 的用户才能使用通用API
                if (!userAuthLevel.HasValue || userAuthLevel.Value > 1)
                {
                    var now = DateTime.UtcNow;
                    var shouldWarn = !_apiWarningCooldown.TryGetValue(apiContext?.GroupId ?? 0, out var last) ||
                                     now - last >= TimeSpan.FromMinutes(3);

                    if (shouldWarn)
                    {
                        _apiWarningCooldown[apiContext?.GroupId ?? 0] = now;
                        if (userPrimaryFailed)
                        {
                            TryNotifyGroupMessage(apiContext?.GroupId,
                                $"用户主API向量调用失败，但您没有权限使用通用API。需要权限等级 <= 1（1级白名单）。");
                        }
                        else
                        {
                            TryNotifyGroupMessage(apiContext?.GroupId,
                                $"❌ 没有权限使用通用API。需要权限等级 <= 1（1级白名单）。如需使用，请联系管理员。");
                        }
                    }

                    _context.Log(LogLevel.Info,
                        $"[AIMod:TRPG] User {ownerUserId} (AuthLevel={userAuthLevel}) denied access to public embedding API");
                    
                    // 自动关闭AI响应，避免后续每次request重复触发警告
                    if (apiContext != null)
                        AutoDeactivateLogSessions(apiContext.GroupId);
                    
                    return null;
                }
            }

            if (IsConfiguredApiKey(secConfig.SecondaryApiKey) && !string.IsNullOrWhiteSpace(secConfig.SecondaryEndpoint))
            {
                MarkLastTrpgApiSource(ApiSourceDefaultSecondary, apiContext);
                var defaultSub = await CallEmbeddingApiAsync(
                    secConfig.SecondaryApiKey, secConfig.SecondaryModel,
                    secConfig.SecondaryEndpoint, text, apiContext?.OwnerUserId);
                if (defaultSub != null)
                {
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] Used default secondary embedding API: {MaskApiKey(secConfig.SecondaryApiKey)}");
                    TryRecordStandaloneCommonApiUsage(
                        apiContext,
                        success: true,
                        providerFallback: ResolveProviderIdFromEndpoint(secConfig.SecondaryEndpoint),
                        modelFallback: secConfig.SecondaryModel,
                        agentName: "Embedding",
                        requestKind: "Embedding",
                        inputCharCount: text?.Length ?? 0);
                    return defaultSub;
                }
            }

            if (IsConfiguredApiKey(_config.DeepSeekConfig.ApiKey))
            {
                MarkLastTrpgApiSource(ApiSourceDefaultPrimary, apiContext);
                var defaultPrimary = await CallEmbeddingApiAsync(
                    _config.DeepSeekConfig.ApiKey, _config.DeepSeekConfig.ModelName,
                    "https://api.deepseek.com/v1/embeddings", text, apiContext?.OwnerUserId);
                if (defaultPrimary != null)
                {
                    _context.Log(LogLevel.Info, $"[AIMod:TRPG] Used default primary embedding API: {MaskApiKey(_config.DeepSeekConfig.ApiKey)}");
                    TryRecordStandaloneCommonApiUsage(
                        apiContext,
                        success: true,
                        providerFallback: "deepseek",
                        modelFallback: _config.DeepSeekConfig.ModelName,
                        agentName: "Embedding",
                        requestKind: "Embedding",
                        inputCharCount: text?.Length ?? 0);
                    return defaultPrimary;
                }
            }

            ClearLastTrpgActualUsage();
            return null;
        }

        private async Task<float[]?> CallEmbeddingApiAsync(string apiKey, string modelName, string apiUrl, string text, long? tokenUserId = null)
        {
            try
            {
                var requestBody = new { model = modelName, input = text };
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = JsonContent.Create(requestBody)
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                var response = await _retryPolicy.ExecuteAsync(() => _httpClient.SendAsync(request));
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    var jsonDoc = JsonDocument.Parse(responseJson);
                    var providerId = ResolveProviderIdFromEndpoint(apiUrl);
                    var usage = ParseTokenUsage(jsonDoc.RootElement, providerId);
                    CaptureLastTrpgActualUsage(providerId, modelName, usage);
                    ExtractAndUpdateTokenUsage(tokenUserId, jsonDoc.RootElement, providerId);
                    var embedding = jsonDoc.RootElement.GetProperty("data")[0].GetProperty("embedding");
                    var arr = new float[embedding.GetArrayLength()];
                    for (int i = 0; i < arr.Length; i++)
                        arr[i] = embedding[i].GetSingle();
                    return arr;
                }
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod:TRPG] Embedding API error: {ex.Message}");
            }
            return null;
        }

        private async Task<string?> CallOpenAiCompatibleApi(
            string apiKey, string modelName, string apiUrl, List<ChatMessage> messages, long? tokenUserId = null)
        {
            try
            {
                var requestMessages = messages.Select(m => new { role = m.Role, content = m.Content }).ToList();
                var requestBody = new { model = modelName, messages = requestMessages, temperature = 0.8, max_tokens = 4096 };
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = JsonContent.Create(requestBody)
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                var roleTrace = string.Join(", ", messages.Select(m => m.Role));
                _context.Log(LogLevel.Info, $"[AIMod] >>> Prompt message count={messages.Count}, roles=[{roleTrace}]");
                var promptSnapshot = new StringBuilder();
                promptSnapshot.AppendLine("[AIMod] >>> OutgoingPromptSnapshot");
                promptSnapshot.AppendLine("======================== BEGIN REQUEST PROMPT ========================");
                for (int i = 0; i < messages.Count; i++)
                {
                    var msg = messages[i];
                    promptSnapshot.AppendLine($"[#{i}] role={msg.Role}");
                    promptSnapshot.AppendLine(msg.Content ?? string.Empty);
                    promptSnapshot.AppendLine("------------------------");
                }
                promptSnapshot.AppendLine("========================= END REQUEST PROMPT =========================");
                _context.Log(LogLevel.Info, promptSnapshot.ToString());
                _context.Log(LogLevel.Info, $"[AIMod] >>> Calling {apiUrl} with model {modelName}");
                var response = await _retryPolicy.ExecuteAsync(() => _httpClient.SendAsync(request));
                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadFromJsonAsync<JsonElement>();
                    var providerId = ResolveProviderIdFromEndpoint(apiUrl);
                    var usage = ParseTokenUsage(responseBody, providerId);
                    CaptureLastTrpgActualUsage(providerId, modelName, usage);
                    ExtractAndUpdateTokenUsage(tokenUserId, responseBody, providerId);
                    var aiResponse = responseBody.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                    return aiResponse;
                }
                var errorContent = await response.Content.ReadAsStringAsync();
                _context.Log(LogLevel.Error, $"[AIMod] >>> HTTP Error {response.StatusCode}: {errorContent.Substring(0, Math.Min(200, errorContent.Length))}");
                return null;
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod] >>> CallOpenAiCompatibleApi error: {ex.Message}");
                return null;
            }
        }

        private async Task<string?> GetAiResponse(string userMessage, long groupId)
        {
            try
            {
                _context.Log(LogLevel.Info, $"[AIMod] >>> GetAiResponse START: userMessage='{userMessage}', groupId={groupId}");
                _context.Log(LogLevel.Info, $"[AIMod] >>> Using model: {_config.SelectedModel}");
                
                // Initialize context for group if needed
                if (!_groupContexts.ContainsKey(groupId))
                {
                    _groupContexts[groupId] = new List<string>();
                    _context.Log(LogLevel.Info, $"[AIMod] >>> Created new context for group {groupId}");
                }
                var context = _groupContexts[groupId];

                // 根据所选模型调用对应的API
                string? aiResponse = _config.SelectedModel switch
                {
                    AiModelType.Gemini => await GetGeminiResponse(userMessage, groupId, context),
                    AiModelType.ZhipuAI => await GetZhipuAIResponse(userMessage, groupId, context),
                    AiModelType.SiliconFlow => await GetSiliconFlowResponse(userMessage, groupId, context),
                    AiModelType.DeepSeek => await GetDeepSeekResponseLegacy(userMessage, groupId, context),
                    _ => null
                };

                if (!string.IsNullOrEmpty(aiResponse))
                {
                    context.Add($"User: {userMessage}");
                    context.Add($"Assistant: {aiResponse}");
                    _context.Log(LogLevel.Info, $"[AIMod] >>> Context updated, count={context.Count}");
                    
                    if (context.Count > _config.MaxContextMessages * 2)
                    {
                        var removeCount = context.Count - (_config.MaxContextMessages * 2);
                        context.RemoveRange(0, removeCount);
                        _context.Log(LogLevel.Info, $"[AIMod] >>> Context trimmed, removed {removeCount} items");
                    }
                }

                return aiResponse;
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod] >>> Unexpected Exception ({ex.GetType().Name}): {ex.Message}");
                _context.Log(LogLevel.Error, $"[AIMod] >>> StackTrace: {ex.StackTrace}");
                return null;
            }
        }

        private async Task<string?> GetGeminiResponse(string userMessage, long groupId, List<string> context)
        {
            try
            {
                _context.Log(LogLevel.Info, $"[AIMod] >>> Calling Gemini API");
                
                // Build prompt with system prompt
                var fullPrompt = _config.SystemPrompt + $" Context: [group_id: {groupId}]\n";
                fullPrompt += string.Join("\n", context);
                fullPrompt += $"\nUser: {userMessage}\nAssistant:";
                _context.Log(LogLevel.Info, $"[AIMod] >>> Prompt built ({fullPrompt.Length} chars)");

                var requestBody = new
                {
                    contents = new[]
                    {
                        new { parts = new[] { new { text = fullPrompt } } }
                    }
                };

                var modelName = _config.GeminiConfig.ModelName ?? "gemini-2.5-flash";
                var apiKey = _config.GeminiConfig.ApiKey;
                var apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";
                _context.Log(LogLevel.Info, $"[AIMod] >>> API URL: models/{modelName}:generateContent");
                
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = JsonContent.Create(requestBody)
                };

                _context.Log(LogLevel.Info, $"[AIMod] >>> Sending Gemini HTTP POST request...");
                var response = await _httpClient.SendAsync(request);
                _context.Log(LogLevel.Info, $"[AIMod] >>> Gemini HTTP Response received: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    _context.Log(LogLevel.Info, $"[AIMod] >>> Gemini response is success, parsing JSON...");
                    var responseBody = await response.Content.ReadFromJsonAsync<JsonElement>();
                    _context.Log(LogLevel.Info, $"[AIMod] >>> Gemini JSON parsed successfully");
                    
                    try
                    {
                        _context.Log(LogLevel.Info, $"[AIMod] >>> Extracting Gemini response text...");
                        var aiResponse = responseBody.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                        _context.Log(LogLevel.Info, $"[AIMod] >>> Extracted Gemini response: '{aiResponse?.Substring(0, Math.Min(100, aiResponse?.Length ?? 0))}...'");

                        if (!string.IsNullOrEmpty(aiResponse))
                        {
                            return aiResponse;
                        }
                        else
                        {
                            _context.Log(LogLevel.Error, $"[AIMod] >>> CRITICAL: Gemini extracted response is null/empty!");
                            return null;
                        }
                    }
                    catch (Exception parseEx)
                    {
                        _context.Log(LogLevel.Error, $"[AIMod] >>> Gemini JSON Parse Error: {parseEx.GetType().Name}: {parseEx.Message}");
                        var rawContent = await response.Content.ReadAsStringAsync();
                        _context.Log(LogLevel.Error, $"[AIMod] >>> Gemini raw response: {rawContent.Substring(0, Math.Min(500, rawContent.Length))}");
                        return null;
                    }
                }
                else
                {
                    _context.Log(LogLevel.Error, $"[AIMod] >>> Gemini HTTP Error Status: {response.StatusCode}");
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _context.Log(LogLevel.Error, $"[AIMod] >>> Gemini error response: {errorContent.Substring(0, Math.Min(500, errorContent.Length))}");
                    return null;
                }
            }
            catch (HttpRequestException httpEx)
            {
                _context.Log(LogLevel.Error, $"[AIMod] >>> Gemini HttpRequestException: {httpEx.Message}");
                return null;
            }
            catch (TaskCanceledException timeEx)
            {
                _context.Log(LogLevel.Error, $"[AIMod] >>> Gemini TaskCanceledException (timeout): {timeEx.Message}");
                return null;
            }
        }

        private async Task<string?> GetZhipuAIResponse(string userMessage, long groupId, List<string> context)
        {
            try
            {
                _context.Log(LogLevel.Info, $"[AIMod] >>> Calling ZhipuAI API");
                
                // Build messages array for ZhipuAI (使用 messages 数组格式)
                var messages = new List<object>();
                
                // Add system message with custom prompt
                messages.Add(new { role = "system", content = _config.SystemPrompt });
                
                // Add context messages
                var contextMessages = string.Join("\n", context);
                if (!string.IsNullOrEmpty(contextMessages))
                {
                    messages.Add(new { role = "user", content = $"Previous context:\n{contextMessages}" });
                    messages.Add(new { role = "assistant", content = "Understood the context." });
                }
                
                // Add current user message
                messages.Add(new { role = "user", content = userMessage });
                
                _context.Log(LogLevel.Info, $"[AIMod] >>> ZhipuAI messages built with {messages.Count} items");

                var requestBody = new
                {
                    model = _config.ZhipuAIConfig.ModelName ?? "glm-4.7-flash",
                    messages = messages,
                    temperature = 1.0,
                    top_p = 0.9,
                    max_tokens = 65536
                };

                var apiKey = _config.ZhipuAIConfig.ApiKey;
                var apiUrl = "https://open.bigmodel.cn/api/paas/v4/chat/completions";
                _context.Log(LogLevel.Info, $"[AIMod] >>> ZhipuAI API URL: {apiUrl}");
                
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = JsonContent.Create(requestBody)
                };
                
                // ZhipuAI 使用 Bearer token 认证
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                _context.Log(LogLevel.Info, $"[AIMod] >>> Sending ZhipuAI HTTP POST request...");
                var response = await _httpClient.SendAsync(request);
                _context.Log(LogLevel.Info, $"[AIMod] >>> ZhipuAI HTTP Response received: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    _context.Log(LogLevel.Info, $"[AIMod] >>> ZhipuAI response is success, parsing JSON...");
                    var responseBody = await response.Content.ReadFromJsonAsync<JsonElement>();
                    _context.Log(LogLevel.Info, $"[AIMod] >>> ZhipuAI JSON parsed successfully");
                    
                    try
                    {
                        _context.Log(LogLevel.Info, $"[AIMod] >>> Extracting ZhipuAI response text...");
                        // ZhipuAI 的响应格式: choices[0].message.content
                        var aiResponse = responseBody.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                        _context.Log(LogLevel.Info, $"[AIMod] >>> Extracted ZhipuAI response: '{aiResponse?.Substring(0, Math.Min(100, aiResponse?.Length ?? 0))}...'");

                        if (!string.IsNullOrEmpty(aiResponse))
                        {
                            return aiResponse;
                        }
                        else
                        {
                            _context.Log(LogLevel.Error, $"[AIMod] >>> CRITICAL: ZhipuAI extracted response is null/empty!");
                            return null;
                        }
                    }
                    catch (Exception parseEx)
                    {
                        _context.Log(LogLevel.Error, $"[AIMod] >>> ZhipuAI JSON Parse Error: {parseEx.GetType().Name}: {parseEx.Message}");
                        var rawContent = await response.Content.ReadAsStringAsync();
                        _context.Log(LogLevel.Error, $"[AIMod] >>> ZhipuAI raw response: {rawContent.Substring(0, Math.Min(500, rawContent.Length))}");
                        return null;
                    }
                }
                else
                {
                    _context.Log(LogLevel.Error, $"[AIMod] >>> ZhipuAI HTTP Error Status: {response.StatusCode}");
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _context.Log(LogLevel.Error, $"[AIMod] >>> ZhipuAI error response: {errorContent.Substring(0, Math.Min(500, errorContent.Length))}");
                    return null;
                }
            }
            catch (HttpRequestException httpEx)
            {
                _context.Log(LogLevel.Error, $"[AIMod] >>> ZhipuAI HttpRequestException: {httpEx.Message}");
                return null;
            }
            catch (TaskCanceledException timeEx)
            {
                _context.Log(LogLevel.Error, $"[AIMod] >>> ZhipuAI TaskCanceledException (timeout): {timeEx.Message}");
                return null;
            }
        }

        private async Task<string?> GetSiliconFlowResponse(string userMessage, long groupId, List<string> context)
        {
            try
            {
                _context.Log(LogLevel.Info, $"[AIMod] >>> Calling SiliconFlow API");
                
                // Build messages array for SiliconFlow (OpenAI-compatible format)
                var messages = new List<object>();
                
                // Add system message with custom prompt
                messages.Add(new { role = "system", content = _config.SystemPrompt });
                
                // Add context messages
                var contextMessages = string.Join("\n", context);
                if (!string.IsNullOrEmpty(contextMessages))
                {
                    messages.Add(new { role = "user", content = $"Previous context:\n{contextMessages}" });
                    messages.Add(new { role = "assistant", content = "Understood the context." });
                }
                
                // Add current user message
                messages.Add(new { role = "user", content = userMessage });
                
                _context.Log(LogLevel.Info, $"[AIMod] >>> SiliconFlow messages built with {messages.Count} items");

                var requestBody = new
                {
                    model = _config.SiliconFlowConfig.ModelName ?? "Qwen/Qwen3-8B",
                    messages = messages,
                    temperature = 0.7,
                    max_tokens = 2048
                };

                var apiKey = _config.SiliconFlowConfig.ApiKey;
                var apiUrl = "https://api.siliconflow.cn/v1/chat/completions";
                _context.Log(LogLevel.Info, $"[AIMod] >>> SiliconFlow API URL: {apiUrl}");
                
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = JsonContent.Create(requestBody)
                };
                
                // SiliconFlow uses Bearer token authentication
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                _context.Log(LogLevel.Info, $"[AIMod] >>> Sending SiliconFlow HTTP POST request...");
                var response = await _httpClient.SendAsync(request);
                _context.Log(LogLevel.Info, $"[AIMod] >>> SiliconFlow HTTP Response received: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    _context.Log(LogLevel.Info, $"[AIMod] >>> SiliconFlow response is success, parsing JSON...");
                    var responseBody = await response.Content.ReadFromJsonAsync<JsonElement>();
                    _context.Log(LogLevel.Info, $"[AIMod] >>> SiliconFlow JSON parsed successfully");
                    
                    try
                    {
                        _context.Log(LogLevel.Info, $"[AIMod] >>> Extracting SiliconFlow response text...");
                        // SiliconFlow response format (OpenAI compatible): choices[0].message.content
                        var aiResponse = responseBody.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                        _context.Log(LogLevel.Info, $"[AIMod] >>> Extracted SiliconFlow response: '{aiResponse?.Substring(0, Math.Min(100, aiResponse?.Length ?? 0))}...'");

                        if (!string.IsNullOrEmpty(aiResponse))
                        {
                            return aiResponse;
                        }
                        else
                        {
                            _context.Log(LogLevel.Error, $"[AIMod] >>> CRITICAL: SiliconFlow extracted response is null/empty!");
                            return null;
                        }
                    }
                    catch (Exception parseEx)
                    {
                        _context.Log(LogLevel.Error, $"[AIMod] >>> SiliconFlow JSON Parse Error: {parseEx.GetType().Name}: {parseEx.Message}");
                        var rawContent = await response.Content.ReadAsStringAsync();
                        _context.Log(LogLevel.Error, $"[AIMod] >>> SiliconFlow raw response: {rawContent.Substring(0, Math.Min(500, rawContent.Length))}");
                        return null;
                    }
                }
                else
                {
                    _context.Log(LogLevel.Error, $"[AIMod] >>> SiliconFlow HTTP Error Status: {response.StatusCode}");
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _context.Log(LogLevel.Error, $"[AIMod] >>> SiliconFlow error response: {errorContent.Substring(0, Math.Min(500, errorContent.Length))}");
                    return null;
                }
            }
            catch (HttpRequestException httpEx)
            {
                _context.Log(LogLevel.Error, $"[AIMod] >>> SiliconFlow HttpRequestException: {httpEx.Message}");
                return null;
            }
            catch (TaskCanceledException timeEx)
            {
                _context.Log(LogLevel.Error, $"[AIMod] >>> SiliconFlow TaskCanceledException (timeout): {timeEx.Message}");
                return null;
            }
        }

        private async Task<string?> GetDeepSeekResponseLegacy(string userMessage, long groupId, List<string> context)
        {
            try
            {
                _context.Log(LogLevel.Info, $"[AIMod] >>> Calling DeepSeek API (Legacy mode)");
                var messages = new List<object>();
                messages.Add(new { role = "system", content = _config.SystemPrompt });
                var contextMessages = string.Join("\n", context);
                if (!string.IsNullOrEmpty(contextMessages))
                {
                    messages.Add(new { role = "user", content = $"Previous context:\n{contextMessages}" });
                    messages.Add(new { role = "assistant", content = "Understood the context." });
                }
                messages.Add(new { role = "user", content = userMessage });
                _context.Log(LogLevel.Info, $"[AIMod] >>> DeepSeek messages built with {messages.Count} items");
                var requestBody = new
                {
                    model = _config.DeepSeekConfig.ModelName ?? "deepseek-chat",
                    messages = messages,
                    temperature = 0.8,
                    max_tokens = 4096
                };
                var apiKey = _config.DeepSeekConfig.ApiKey;
                var apiUrl = "https://api.deepseek.com/v1/chat/completions";
                var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = JsonContent.Create(requestBody)
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                var response = await _httpClient.SendAsync(request);
                _context.Log(LogLevel.Info, $"[AIMod] >>> DeepSeek HTTP Response received: {response.StatusCode}");
                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadFromJsonAsync<JsonElement>();
                    try
                    {
                        var aiResponse = responseBody.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                        if (!string.IsNullOrEmpty(aiResponse)) return aiResponse;
                        _context.Log(LogLevel.Error, $"[AIMod] >>> CRITICAL: DeepSeek extracted response is null/empty!");
                        return null;
                    }
                    catch (Exception parseEx)
                    {
                        _context.Log(LogLevel.Error, $"[AIMod] >>> DeepSeek JSON Parse Error: {parseEx.GetType().Name}: {parseEx.Message}");
                        return null;
                    }
                }
                else
                {
                    _context.Log(LogLevel.Error, $"[AIMod] >>> DeepSeek HTTP Error Status: {response.StatusCode}");
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _context.Log(LogLevel.Error, $"[AIMod] >>> DeepSeek error response: {errorContent.Substring(0, Math.Min(500, errorContent.Length))}");
                    return null;
                }
            }
            catch (HttpRequestException httpEx)
            {
                _context.Log(LogLevel.Error, $"[AIMod] >>> DeepSeek HttpRequestException: {httpEx.Message}");
                return null;
            }
            catch (TaskCanceledException timeEx)
            {
                _context.Log(LogLevel.Error, $"[AIMod] >>> DeepSeek TaskCanceledException (timeout): {timeEx.Message}");
                return null;
            }
        }

        /// <summary>
        /// 测试方法：列出所有可用的 Gemini 模型
        /// 用于调试 API 问题，查看实际可用的模型列表
        /// </summary>
        private async Task ListAvailableModels()
        {
            try
            {
                _context.Log(LogLevel.Info, "[AIMod] ========== ListModels Test START ==========");
                
                var apiKey = _config.SelectedModel switch
                {
                    AiModelType.Gemini => _config.GeminiConfig.ApiKey,
                    AiModelType.ZhipuAI => _config.ZhipuAIConfig.ApiKey,
                    AiModelType.SiliconFlow => _config.SiliconFlowConfig.ApiKey,
                    AiModelType.DeepSeek => _config.DeepSeekConfig.ApiKey,
                    _ => null
                };

                if (string.IsNullOrEmpty(apiKey))
                {
                    _context.Log(LogLevel.Error, "[AIMod] API Key not configured for selected model");
                    return;
                }

                if (_config.SelectedModel == AiModelType.Gemini)
                {
                    var apiUrl = $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}";
                    _context.Log(LogLevel.Info, $"[AIMod] Calling Gemini ListModels API: {apiUrl.Substring(0, Math.Min(100, apiUrl.Length))}...");

                    var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                    var response = await _httpClient.SendAsync(request);

                    _context.Log(LogLevel.Info, $"[AIMod] Gemini ListModels Response Status: {response.StatusCode}");

                    var responseContent = await response.Content.ReadAsStringAsync();
                    _context.Log(LogLevel.Info, $"[AIMod] Gemini ListModels Response Content:\n{responseContent}");

                    if (response.IsSuccessStatusCode)
                    {
                        try
                        {
                            var models = JsonSerializer.Deserialize<JsonElement>(responseContent);
                            if (models.TryGetProperty("models", out var modelsArray))
                            {
                                _context.Log(LogLevel.Info, $"[AIMod] Available Gemini Models:");
                                foreach (var model in modelsArray.EnumerateArray())
                                {
                                    if (model.TryGetProperty("name", out var name))
                                    {
                                        var modelName = name.GetString() ?? "unknown";
                                        _context.Log(LogLevel.Info, $"[AIMod]   - {modelName}");
                                    }
                                }
                            }
                        }
                        catch (Exception parseEx)
                        {
                            _context.Log(LogLevel.Error, $"[AIMod] Parse Error: {parseEx.Message}");
                        }
                    }
                    else
                    {
                        _context.Log(LogLevel.Error, $"[AIMod] API Error: {response.StatusCode}");
                    }
                }
                else if (_config.SelectedModel == AiModelType.ZhipuAI)
                {
                    _context.Log(LogLevel.Info, $"[AIMod] ZhipuAI uses predefined models, using configured model: {_config.ZhipuAIConfig.ModelName}");
                }
                else if (_config.SelectedModel == AiModelType.SiliconFlow)
                {
                    _context.Log(LogLevel.Info, $"[AIMod] SiliconFlow uses predefined models, using configured model: {_config.SiliconFlowConfig.ModelName}");
                }
                else if (_config.SelectedModel == AiModelType.DeepSeek)
                {
                    _context.Log(LogLevel.Info, $"[AIMod] DeepSeek uses predefined models, using configured model: {_config.DeepSeekConfig.ModelName}");
                }

                _context.Log(LogLevel.Info, "[AIMod] ========== ListModels Test END ==========");
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod] ListModels Exception: {ex.Message}");
            }
        }

        private void LoadConfig()
        {
            try
            {
                var launcherBaseDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                // 尝试多个可能的路径以找到配置文件
                // 优先使用 data/AIMod/ai-config.json（统一数据目录）
                var possiblePaths = new[]
                {
                    Path.Combine(launcherBaseDir, "data", "AIMod", "ai-config.json"),
                    Path.Combine(AppContext.BaseDirectory, "data", "AIMod", "ai-config.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "data", "AIMod", "ai-config.json"),
                    // 旧路径（向后兼容）
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "mods", "AIMod", "ai-config.json"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mods", "AIMod", "ai-config.json"),
                };

                string? configPath = null;
                foreach (var path in possiblePaths)
                {
                    var normalizedPath = Path.GetFullPath(path);
                    if (File.Exists(normalizedPath))
                    {
                        configPath = normalizedPath;
                        break;
                    }
                }

                if (configPath != null && File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    _config = JsonSerializer.Deserialize<AiConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AiConfig();
                    _context.Log(LogLevel.Info, $"[AIMod] Config loaded from: {configPath}");
                }
                else
                {
                    _config = new AiConfig();
                    SaveConfig();
                }
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod] Error loading config: {ex.Message}");
                _config = new AiConfig();
            }
        }

        public void SaveConfig()
        {
            try
            {
                var launcherBaseDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                // 尝试多个可能的目录位置
                // 优先使用 data/AIMod/ai-config.json（统一数据目录）
                var possiblePaths = new[]
                {
                    Path.Combine(launcherBaseDir, "data", "AIMod", "ai-config.json"),
                    Path.Combine(AppContext.BaseDirectory, "data", "AIMod", "ai-config.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "data", "AIMod", "ai-config.json"),
                    // 旧路径（向后兼容）
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "mods", "AIMod", "ai-config.json"),
                };

                string? configPath = null;
                
                // 首先检查现有的配置文件位置
                foreach (var path in possiblePaths)
                {
                    var normalizedPath = Path.GetFullPath(path);
                    if (File.Exists(normalizedPath))
                    {
                        configPath = normalizedPath;
                        break;
                    }
                }

                // 如果没有找到现有文件，使用第一个可能的路径
                if (configPath == null)
                {
                    configPath = Path.GetFullPath(possiblePaths[0]);
                }

                // 确保目录存在
                var directory = Path.GetDirectoryName(configPath);
                if (directory != null && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _context.Log(LogLevel.Info, $"[AIMod] Created config directory: {directory}");
                }

                var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
                var json = JsonSerializer.Serialize(_config, options);
                File.WriteAllText(configPath, json);
                _context.Log(LogLevel.Info, $"[AIMod] Config saved to: {configPath}");
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error, $"[AIMod] Error saving config: {ex.Message}");
            }
        }

        // IConfigurable implementation
        public event ConfigChangedEventHandler? ConfigChanged;

        public IReadOnlyList<string> GetConfigKeys()
        {
            return new List<string>
            {
                "aimod.mode",
                "aimod.selectedmodel",
                "aimod.systemprompt",
                "aimod.gemini.apikey",
                "aimod.gemini.modelname",
                "aimod.zhipu.apikey",
                "aimod.zhipu.modelname",
                "aimod.siliconflow.apikey",
                "aimod.siliconflow.modelname",
                "aimod.deepseek.apikey",
                "aimod.deepseek.modelname",
                "aimod.prefix",
                "aimod.maxcontextturns",
                "aimod.interceptall",
                "aimod.trpg.boundteamname",
                "aimod.trpg.oocprefix",
                "aimod.trpg.characterid",
                "aimod.trpg.charactername",
                "aimod.trpg.cooldownseconds",
                "aimod.trpg.tokenthreshold",
                "aimod.trpg.systemprompt",
                "aimod.trpg.stateinterceptionenabled",
                "aimod.trpg.recalltopk",
                "aimod.trpg.recallminsimilarity",
                "aimod.trpg.recenthistorycount",
                "aimod.trpg.historyfoldcount",
                "aimod.trpg.staticbackground",
                "aimod.trpg.dynamicstatejson",
                "aimod.trpg.secondaryapikey",
                "aimod.trpg.secondarymodel",
                "aimod.trpg.secondaryendpoint"
            };
        }

        public string? GetConfigValue(string key)
        {
            return key switch
            {
                "aimod.mode" => _config.Mode.ToString(),
                "aimod.selectedmodel" => _config.SelectedModel.ToString(),
                "aimod.systemprompt" => _config.SystemPrompt,
                "aimod.gemini.apikey" => _config.GeminiConfig.ApiKey,
                "aimod.gemini.modelname" => _config.GeminiConfig.ModelName,
                "aimod.zhipu.apikey" => _config.ZhipuAIConfig.ApiKey,
                "aimod.zhipu.modelname" => _config.ZhipuAIConfig.ModelName,
                "aimod.siliconflow.apikey" => _config.SiliconFlowConfig.ApiKey,
                "aimod.siliconflow.modelname" => _config.SiliconFlowConfig.ModelName,
                "aimod.deepseek.apikey" => _config.DeepSeekConfig.ApiKey,
                "aimod.deepseek.modelname" => _config.DeepSeekConfig.ModelName,
                "aimod.prefix" => _config.PrefixRules.FirstOrDefault()?.Prefix ?? string.Empty,
                "aimod.maxcontextturns" => _config.MaxContextMessages.ToString(),
                "aimod.interceptall" => _config.InterceptAll.ToString(),
                "aimod.trpg.oocprefix" => _config.TrpgConfig.OocPrefix,
                "aimod.trpg.cooldownseconds" => _config.TrpgConfig.CooldownSeconds.ToString(),
                "aimod.trpg.tokenthreshold" => _config.TrpgConfig.TokenThreshold.ToString(),
                "aimod.trpg.systemprompt" => _config.TrpgConfig.SystemPromptTemplate,
                "aimod.trpg.stateinterceptionenabled" => _config.TrpgConfig.StateInterceptionEnabled.ToString(),
                "aimod.trpg.recalltopk" => _config.TrpgConfig.RecallTopK.ToString(),
                "aimod.trpg.recallminsimilarity" => _config.TrpgConfig.RecallMinSimilarity.ToString("0.##"),
                "aimod.trpg.recenthistorycount" => _config.TrpgConfig.RecentHistoryCount.ToString(),
                "aimod.trpg.historyfoldcount" => _config.TrpgConfig.HistoryFoldCount.ToString(),
                "aimod.trpg.secondaryapikey" => _config.TrpgConfig.SecondaryApiKey,
                "aimod.trpg.secondarymodel" => _config.TrpgConfig.SecondaryModel,
                "aimod.trpg.secondaryendpoint" => _config.TrpgConfig.SecondaryEndpoint,
                _ => null
            };
        }

        public ConfigValidationResult ValidateConfig(string key, string value)
        {
            switch (key)
            {
                case "aimod.mode":
                    if (!Enum.TryParse<AiMode>(value, true, out _))
                        return ConfigValidationResult.Invalid("Invalid mode. Must be 'Prefix', 'InterceptAll', or 'TRPGPlayer'.");
                    break;
                case "aimod.selectedmodel":
                    if (!Enum.TryParse<AiModelType>(value, true, out _))
                        return ConfigValidationResult.Invalid("Invalid model type. Must be 'Gemini', 'ZhipuAI', 'SiliconFlow', or 'DeepSeek'.");
                    break;
                case "aimod.systemprompt":
                    if (string.IsNullOrWhiteSpace(value))
                        return ConfigValidationResult.Invalid("System prompt cannot be empty.");
                    break;
                case "aimod.gemini.apikey":
                case "aimod.zhipu.apikey":
                case "aimod.siliconflow.apikey":
                case "aimod.deepseek.apikey":
                    if (string.IsNullOrWhiteSpace(value))
                        return ConfigValidationResult.Invalid("API Key cannot be empty.");
                    break;
                case "aimod.gemini.modelname":
                case "aimod.zhipu.modelname":
                case "aimod.siliconflow.modelname":
                case "aimod.deepseek.modelname":
                    if (string.IsNullOrWhiteSpace(value))
                        return ConfigValidationResult.Invalid("Model name cannot be empty.");
                    break;
                case "aimod.prefix":
                    break;
                case "aimod.maxcontextturns":
                    if (!int.TryParse(value, out var turns) || turns < 0)
                        return ConfigValidationResult.Invalid("Max context turns must be a non-negative integer.");
                    break;
                case "aimod.interceptall":
                    if (!bool.TryParse(value, out _))
                        return ConfigValidationResult.Invalid("Intercept all must be a boolean value (true/false).");
                    break;
                case "aimod.trpg.cooldownseconds":
                    if (!int.TryParse(value, out var cd) || cd < 0)
                        return ConfigValidationResult.Invalid("Cooldown seconds must be a non-negative integer.");
                    break;
                case "aimod.trpg.tokenthreshold":
                    if (!int.TryParse(value, out var tt) || tt < 2000)
                        return ConfigValidationResult.Invalid("折叠 token 阈值必须是正整数，且不能小于 2000。");
                    break;
                case "aimod.trpg.stateinterceptionenabled":
                    if (!bool.TryParse(value, out _))
                        return ConfigValidationResult.Invalid("State interception enabled must be true/false.");
                    break;
                case "aimod.trpg.recalltopk":
                    if (!int.TryParse(value, out var topk) || topk < 1 || topk > 10)
                        return ConfigValidationResult.Invalid("Recall Top-K must be an integer in [1, 10].");
                    break;
                case "aimod.trpg.recallminsimilarity":
                    if (!double.TryParse(value, out var minSim) || minSim < 0 || minSim > 1)
                        return ConfigValidationResult.Invalid("Recall min similarity must be a number in [0, 1].");
                    break;
                case "aimod.trpg.secondaryapikey":
                    if (string.IsNullOrWhiteSpace(value))
                        return ConfigValidationResult.Valid();
                    break;
                case "aimod.trpg.secondarymodel":
                    if (string.IsNullOrWhiteSpace(value))
                        return ConfigValidationResult.Valid();
                    break;
                case "aimod.trpg.secondaryendpoint":
                    if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out _))
                        return ConfigValidationResult.Invalid("Secondary endpoint must be a valid URL.");
                    break;
                case "aimod.trpg.recenthistorycount":
                    if (!int.TryParse(value, out var histCount) || histCount < 12 || histCount > 100)
                        return ConfigValidationResult.Invalid("折叠消息条数阈值必须是整数，范围为 [12, 100]。");
                    if (_config.TrpgConfig.HistoryFoldCount >= histCount)
                        return ConfigValidationResult.Invalid("每次折叠条数必须小于折叠消息条数阈值。");
                    break;
                case "aimod.trpg.historyfoldcount":
                    if (!int.TryParse(value, out var foldCount) || foldCount < 4 || foldCount > 50)
                        return ConfigValidationResult.Invalid("每次折叠条数必须是整数，范围为 [4, 50]。");
                    if (foldCount >= _config.TrpgConfig.RecentHistoryCount)
                        return ConfigValidationResult.Invalid("每次折叠条数必须小于折叠消息条数阈值。");
                    break;
                case "aimod.trpg.boundteamname":
                case "aimod.trpg.oocprefix":
                case "aimod.trpg.characterid":
                case "aimod.trpg.charactername":
                case "aimod.trpg.systemprompt":
                case "aimod.trpg.staticbackground":
                case "aimod.trpg.dynamicstatejson":
                    break;
                default:
                    return ConfigValidationResult.Invalid($"Unknown config key: {key}");
            }
            return ConfigValidationResult.Valid();
        }

        public Task<ConfigApplicationResult> ApplyConfigAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            var validation = ValidateConfig(key, value);
            if (!validation.IsValid)
            {
                return Task.FromResult(ConfigApplicationResult.Fail(validation.ErrorMessage ?? "Invalid configuration."));
            }

            bool changed = false;
            switch (key)
            {
                case "aimod.mode":
                    if (Enum.TryParse<AiMode>(value, true, out var mode) && _config.Mode != mode)
                    {
                        _config.Mode = mode;
                        changed = true;
                        if (mode == AiMode.TRPGPlayer && _trpgDb == null)
                            InitializeTrpgComponents();
                    }
                    break;
                case "aimod.selectedmodel":
                    if (Enum.TryParse<AiModelType>(value, true, out var modelType) && _config.SelectedModel != modelType)
                    {
                        _config.SelectedModel = modelType;
                        changed = true;
                    }
                    break;
                case "aimod.systemprompt":
                    if (_config.SystemPrompt != value)
                    {
                        _config.SystemPrompt = value;
                        changed = true;
                    }
                    break;
                case "aimod.gemini.apikey":
                    if (_config.GeminiConfig.ApiKey != value)
                    {
                        _config.GeminiConfig.ApiKey = value;
                        changed = true;
                    }
                    break;
                case "aimod.gemini.modelname":
                    if (_config.GeminiConfig.ModelName != value)
                    {
                        _config.GeminiConfig.ModelName = value;
                        changed = true;
                    }
                    break;
                case "aimod.zhipu.apikey":
                    if (_config.ZhipuAIConfig.ApiKey != value)
                    {
                        _config.ZhipuAIConfig.ApiKey = value;
                        changed = true;
                    }
                    break;
                case "aimod.zhipu.modelname":
                    if (_config.ZhipuAIConfig.ModelName != value)
                    {
                        _config.ZhipuAIConfig.ModelName = value;
                        changed = true;
                    }
                    break;
                case "aimod.siliconflow.apikey":
                    if (_config.SiliconFlowConfig.ApiKey != value)
                    {
                        _config.SiliconFlowConfig.ApiKey = value;
                        changed = true;
                    }
                    break;
                case "aimod.siliconflow.modelname":
                    if (_config.SiliconFlowConfig.ModelName != value)
                    {
                        _config.SiliconFlowConfig.ModelName = value;
                        changed = true;
                    }
                    break;
                case "aimod.deepseek.apikey":
                    if (_config.DeepSeekConfig.ApiKey != value)
                    {
                        _config.DeepSeekConfig.ApiKey = value;
                        changed = true;
                    }
                    break;
                case "aimod.deepseek.modelname":
                    if (_config.DeepSeekConfig.ModelName != value)
                    {
                        _config.DeepSeekConfig.ModelName = value;
                        changed = true;
                    }
                    break;
                case "aimod.prefix":
                    var prefixRule = _config.PrefixRules.FirstOrDefault();
                    if (prefixRule == null)
                    {
                        _config.PrefixRules.Add(new PrefixRule { Enabled = true, Prefix = value });
                        changed = true;
                    }
                    else if (prefixRule.Prefix != value)
                    {
                        prefixRule.Prefix = value;
                        changed = true;
                    }
                    break;
                case "aimod.maxcontextturns":
                    if (int.TryParse(value, out int turns) && _config.MaxContextMessages != turns)
                    {
                        _config.MaxContextMessages = turns;
                        changed = true;
                    }
                    break;
                case "aimod.interceptall":
                    if (bool.TryParse(value, out bool intercept) && _config.InterceptAll != intercept)
                    {
                        _config.InterceptAll = intercept;
                        changed = true;
                    }
                    break;
                case "aimod.trpg.oocprefix":
                    if (_config.TrpgConfig.OocPrefix != value)
                    {
                        _config.TrpgConfig.OocPrefix = value;
                        changed = true;
                    }
                    break;
                case "aimod.trpg.cooldownseconds":
                    if (int.TryParse(value, out int cd) && _config.TrpgConfig.CooldownSeconds != cd)
                    {
                        _config.TrpgConfig.CooldownSeconds = cd;
                        changed = true;
                    }
                    break;
                case "aimod.trpg.tokenthreshold":
                    if (int.TryParse(value, out int tt) && _config.TrpgConfig.TokenThreshold != tt)
                    {
                        _config.TrpgConfig.TokenThreshold = tt;
                        changed = true;
                    }
                    break;
                case "aimod.trpg.systemprompt":
                    if (_config.TrpgConfig.SystemPromptTemplate != value)
                    {
                        _config.TrpgConfig.SystemPromptTemplate = value;
                        changed = true;
                    }
                    break;
                case "aimod.trpg.stateinterceptionenabled":
                    if (bool.TryParse(value, out bool stateEnabled) && _config.TrpgConfig.StateInterceptionEnabled != stateEnabled)
                    {
                        _config.TrpgConfig.StateInterceptionEnabled = stateEnabled;
                        changed = true;
                    }
                    break;
                case "aimod.trpg.recalltopk":
                    if (int.TryParse(value, out int topk) && _config.TrpgConfig.RecallTopK != topk)
                    {
                        _config.TrpgConfig.RecallTopK = topk;
                        changed = true;
                    }
                    break;
                case "aimod.trpg.recallminsimilarity":
                    if (double.TryParse(value, out double minSim) && Math.Abs(_config.TrpgConfig.RecallMinSimilarity - minSim) > 0.0001)
                    {
                        _config.TrpgConfig.RecallMinSimilarity = minSim;
                        changed = true;
                    }
                    break;
                case "aimod.trpg.secondaryapikey":
                    if (_config.TrpgConfig.SecondaryApiKey != value)
                    {
                        _config.TrpgConfig.SecondaryApiKey = value;
                        changed = true;
                    }
                    break;
                case "aimod.trpg.secondarymodel":
                    if (_config.TrpgConfig.SecondaryModel != value)
                    {
                        _config.TrpgConfig.SecondaryModel = value;
                        changed = true;
                    }
                    break;
                case "aimod.trpg.secondaryendpoint":
                    if (_config.TrpgConfig.SecondaryEndpoint != value)
                    {
                        _config.TrpgConfig.SecondaryEndpoint = value;
                        changed = true;
                    }
                    break;
                case "aimod.trpg.recenthistorycount":
                    if (int.TryParse(value, out int histCount) && _config.TrpgConfig.RecentHistoryCount != histCount)
                    {
                        _config.TrpgConfig.RecentHistoryCount = histCount;
                        changed = true;
                    }
                    break;
                case "aimod.trpg.historyfoldcount":
                    if (int.TryParse(value, out int foldCount) && _config.TrpgConfig.HistoryFoldCount != foldCount)
                    {
                        _config.TrpgConfig.HistoryFoldCount = foldCount;
                        changed = true;
                    }
                    break;
            }

            if (changed)
            {
                SaveConfig();
                ConfigChanged?.Invoke(key, value);
            }

            return Task.FromResult(ConfigApplicationResult.Succeed(value));
        }

        /// <summary>
        /// 注册导航面板到主窗口
        /// </summary>
        private void RegisterNavigationPanel()
        {
            try
            {
                Console.WriteLine("[AIMod] >>> RegisterNavigationPanel START");
                _context.Log(LogLevel.Info, "[AIMod] RegisterNavigationPanel START");
                
                Console.WriteLine("[AIMod] >>> Checking implementation status - implements INavigationPanelProvider: true");
                _context.Log(LogLevel.Debug, "[AIMod] AIMod implements INavigationPanelProvider");
                
                Console.WriteLine($"[AIMod] >>> Panel info - Id: {PanelId}, Name: {PanelName}, Priority: {Priority}, IsModPanel: {IsModPanel}");
                _context.Log(LogLevel.Info, $"[AIMod] Panel info - Id: {PanelId}, Name: {PanelName}, Priority: {Priority}, IsModPanel: {IsModPanel}");
                
                // 通过 Context 获取导航面板注册表服务
                Console.WriteLine("[AIMod] >>> Calling _context.GetNavigationPanelRegistry()...");
                var registry = _context.GetNavigationPanelRegistry();
                Console.WriteLine($"[AIMod] >>> Registry result: {(registry != null ? "SUCCESS (not null)" : "NULL")}");
                _context.Log(LogLevel.Info, $"[AIMod] GetNavigationPanelRegistry returned: {(registry != null ? "INavigationPanelRegistry instance" : "NULL")}");
                
                if (registry == null)
                {
                    Console.WriteLine("[AIMod] >>> CRITICAL ERROR: Navigation panel registry is NULL!");
                    Console.WriteLine("[AIMod] >>> This means NavigationPanelRegistry.Instance returned null");
                    _context.Log(LogLevel.Error, "[AIMod] CRITICAL ERROR: Navigation panel registry is NULL - panel registration failed");
                    _context.Log(LogLevel.Warn, "[AIMod] Possible cause: NavigationPanelRegistry not initialized yet, or exception occurred");
                    return;
                }

                Console.WriteLine("[AIMod] >>> About to call registry.Register(this)...");
                _context.Log(LogLevel.Info, "[AIMod] Calling registry.Register() with AIMod as INavigationPanelProvider");
                
                registry.Register(this);
                
                Console.WriteLine("[AIMod] >>> registry.Register() completed without exception");
                _context.Log(LogLevel.Info, "[AIMod] ✓ Navigation panel registered successfully");
                Console.WriteLine("[AIMod] >>> Panel should now appear in main window navigation bar");
                Console.WriteLine("[AIMod] >>> RegisterNavigationPanel END - SUCCESS");
            }
            catch (InvalidOperationException ioEx)
            {
                Console.WriteLine($"[AIMod] >>> INVALIDO_OPERATION EXCEPTION (panel ID already registered?): {ioEx.Message}");
                _context.Log(LogLevel.Error, $"[AIMod] InvalidOperationException during panel registration: {ioEx.Message}");
                _context.Log(LogLevel.Error, $"[AIMod] Possible cause: PanelId '{PanelId}' already registered by another provider");
            }
            catch (ArgumentException argEx)
            {
                Console.WriteLine($"[AIMod] >>> ARGUMENT EXCEPTION: {argEx.Message}");
                _context.Log(LogLevel.Error, $"[AIMod] ArgumentException during panel registration: {argEx.Message}");
                _context.Log(LogLevel.Error, $"[AIMod] Possible causes: Missing PanelId, Empty PanelName, null provider, etc.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AIMod] >>> UNEXPECTED EXCEPTION in RegisterNavigationPanel: {ex.GetType().Name}");
                Console.WriteLine($"[AIMod] >>> Message: {ex.Message}");
                Console.WriteLine($"[AIMod] >>> StackTrace: {ex.StackTrace}");
                _context.Log(LogLevel.Error, $"[AIMod] UNEXPECTED Exception in RegisterNavigationPanel: {ex.GetType().Name}: {ex.Message}");
                _context.Log(LogLevel.Error, $"[AIMod] StackTrace: {ex.StackTrace}");
            }
        }

        // INavigationPanelProvider implementation
        public string PanelId => "com.humulus.aimod.panel";
        public string PanelName => "AI Mod";
        public int Priority => 100;
        public bool IsModPanel => true;
        public Control CreatePanel()
        {
            try
            {
                Console.WriteLine("[AIMod] >>> CreatePanel START");
                _context.Log(LogLevel.Info, "[AIMod] CreatePanel START");
                var panel = new AIModPanel(this, ListAvailableModels);
                Console.WriteLine($"[AIMod] >>> CreatePanel OK: {panel.GetType().FullName}");
                _context.Log(LogLevel.Info, $"[AIMod] CreatePanel OK: {panel.GetType().FullName}");
                return panel;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException;
                var detail = $"{ex.GetType().Name}: {ex.Message}";
                if (inner != null)
                    detail += $"\n  Inner: {inner.GetType().Name}: {inner.Message}\n  Inner StackTrace: {inner.StackTrace}";
                Console.WriteLine($"[AIMod] >>> CreatePanel FAILED: {detail}");
                Console.WriteLine($"[AIMod] >>> StackTrace: {ex.StackTrace}");
                _context.Log(LogLevel.Error, $"[AIMod] CreatePanel FAILED: {detail}");
                _context.Log(LogLevel.Error, $"[AIMod] StackTrace: {ex.StackTrace}");

                return new Border
                {
                    Padding = new Avalonia.Thickness(16),
                    Child = new TextBlock
                    {
                        Text = "AIMod panel failed to load:\n" + ex.GetType().FullName + "\n" + ex.Message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    }
                };
            }
        }

        // ============ 更新逻辑（GitHub Release -> AIMod.mod） ============

        /// <summary>
        /// 检查 GitHub Release 中的最新 UpdatePackageV*，
        /// 查找其中的 AIModPackV*.zip，
        /// 并下载到当前程序目录下的 mods/AIMod.mod。
        /// </summary>
        public async Task<ModUpdateResult> CheckAndUpdateFromGitHubAsync(string owner = "HumulusQ", string repo = "MDiceV2Public")
        {
            var result = new ModUpdateResult();

            try
            {
                _context.Log(LogLevel.Info, "[AIMod.Update] ========== CheckAndUpdateFromGitHub START ==========");

                var releases = await GetAllReleasesAsync(owner, repo);
                if (releases.Count == 0)
                {
                    result.Success = false;
                    result.Message = "未从 GitHub 获取到任何 Release";
                    _context.Log(LogLevel.Warn, "[AIMod.Update] " + result.Message);
                    return result;
                }

                var candidates = releases
                    .Select(r => new { Release = r, NumericTag = ExtractNumericVersion(r.TagName) })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Release.Name) && x.Release.Name.StartsWith("UpdatePackageV", StringComparison.OrdinalIgnoreCase))
                    .Where(x => System.Version.TryParse(x.NumericTag, out _))
                    .OrderByDescending(x => System.Version.Parse(x.NumericTag!))
                    .ToList();

                if (candidates.Count == 0)
                {
                    result.Success = false;
                    result.Message = "未找到任何 UpdatePackageV* 类型的 Release";
                    _context.Log(LogLevel.Warn, "[AIMod.Update] " + result.Message);
                    return result;
                }

                var latest = candidates[0].Release;
                _context.Log(LogLevel.Info, $"[AIMod.Update] 使用 Release: Name={latest.Name}, Tag={latest.TagName}");

                var modAsset = latest.Assets
                    .FirstOrDefault(a => a.Name.StartsWith("AIModPackV", StringComparison.OrdinalIgnoreCase)
                                         && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

                if (modAsset == null)
                {
                    result.Success = false;
                    result.Message = "在最新 UpdatePackageV* Release 中未找到 AIModPackV*.zip 资源";
                    _context.Log(LogLevel.Warn, "[AIMod.Update] " + result.Message);
                    return result;
                }

                var remoteVer = ExtractNumericVersion(modAsset.Name) ?? latest.TagName;
                result.RemoteVersion = remoteVer;
                result.AssetName = modAsset.Name;
                _context.Log(LogLevel.Info, $"[AIMod.Update] 找到远程 Mod 包: {modAsset.Name}, 标记版本={remoteVer}");

                var appBase = AppDomain.CurrentDomain.BaseDirectory;
                var modsRoot = Path.Combine(appBase, "mods");
                Directory.CreateDirectory(modsRoot);

                var targetPath = Path.Combine(modsRoot, "AIMod.mod");
                var tempPath = Path.Combine(Path.GetTempPath(), $"AIMod_{Guid.NewGuid():N}.mod");

                _context.Log(LogLevel.Info, $"[AIMod.Update] 下载目标: {targetPath}");

                await DownloadAssetAsync(modAsset, tempPath, owner, repo, latest.TagName);

                if (File.Exists(targetPath))
                {
                    try
                    {
                        var backupPath = targetPath + ".bak";
                        File.Copy(targetPath, backupPath, overwrite: true);
                        _context.Log(LogLevel.Info, $"[AIMod.Update] 已备份旧文件到: {backupPath}");
                    }
                    catch (Exception backupEx)
                    {
                        _context.Log(LogLevel.Warn, $"[AIMod.Update] 备份旧文件失败: {backupEx.Message}");
                    }
                }

                File.Copy(tempPath, targetPath, overwrite: true);

                var modFolderPath = Path.Combine(modsRoot, "AIMod");
                bool directoryInstalled = TryInstallPackageToDirectory(targetPath, modFolderPath);
                EnsureSingleStructure(targetPath, modFolderPath, directoryInstalled);

                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // ignore
                }

                if (directoryInstalled)
                {
                    result.Success = true;
                    result.Message = $"已下载并更新 '{modFolderPath}'，远程版本标记={remoteVer}";
                }
                else
                {
                    result.Success = false;
                    result.Message = "已获取压缩包但解压失败，已保留 mods/AIMod.mod 供手动处理";
                }

                _context.Log(LogLevel.Info, "[AIMod.Update] " + result.Message);
                _context.Log(LogLevel.Info, "[AIMod.Update] ========== CheckAndUpdateFromGitHub END (" + (result.Success ? "success" : "partial") + ") ==========");

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"更新失败: {ex.Message}";
                _context.Log(LogLevel.Error, "[AIMod.Update] ✗ 更新过程出现异常: " + ex.Message + "\n" + ex.StackTrace);
                _context.Log(LogLevel.Info, "[AIMod.Update] ========== CheckAndUpdateFromGitHub END (error) ==========");
                return result;
            }
        }

        public class ModUpdateResult
        {
            public bool Success { get; set; }
            public string? Message { get; set; }
            public string? RemoteVersion { get; set; }
            public string? AssetName { get; set; }
        }

        private CustomUpdateManager CreateModUpdateDownloader()
        {
            return new CustomUpdateManager(message =>
                _context.Log(LogLevel.Info, "[AIMod.Update.Downloader] " + message));
        }

        private Task<List<GitHubRelease>> GetAllReleasesAsync(string owner, string repo)
        {
            return CreateModUpdateDownloader().GetGitHubReleasesAsync(owner, repo);
        }

        private Task DownloadAssetAsync(
            GitHubAsset asset,
            string targetPath,
            string owner,
            string repo,
            string? releaseTag = null)
        {
            return CreateModUpdateDownloader().DownloadGitHubAssetAsync(asset, targetPath, owner, repo, releaseTag);
        }

        private static string? ExtractNumericVersion(string? versionText)
        {
            if (string.IsNullOrWhiteSpace(versionText))
                return null;

            var match = Regex.Match(versionText, @"^\s*([0-9]+(?:\.[0-9]+){0,3})");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            var digits = Regex.Match(versionText, @"[0-9]+");
            return digits.Success ? digits.Value : null;
        }

        private sealed class GitHubReleaseDto
        {
            public string? name { get; set; }
            public string? tag_name { get; set; }
            public DateTime published_at { get; set; }
            public string? body { get; set; }

            public List<GitHubAssetDto>? assets { get; set; }

            public string Name => name ?? string.Empty;
            public string TagName => tag_name ?? string.Empty;
            public DateTime PublishedAt => published_at;
            public string Body => body ?? string.Empty;
            public List<GitHubAssetDto> Assets => assets ?? new List<GitHubAssetDto>();
        }

        private sealed class GitHubAssetDto
        {
            public string? name { get; set; }
            public long size { get; set; }
            public string? browser_download_url { get; set; }

            public string Name => name ?? string.Empty;
            public long Size => size;
            public string BrowserDownloadUrl => browser_download_url ?? string.Empty;
        }

        private bool TryInstallPackageToDirectory(string packagePath, string targetDirectory)
        {
            try
            {
                _context.Log(LogLevel.Info,
                    $"[AIMod.Update] 正在解压 Mod 包到 {targetDirectory}，确保运行时使用最新内容");

                if (Directory.Exists(targetDirectory))
                {
                    Directory.Delete(targetDirectory, recursive: true);
                    _context.Log(LogLevel.Info, "[AIMod.Update] 已清理旧目录");
                }

                Directory.CreateDirectory(targetDirectory);
                ZipFile.ExtractToDirectory(packagePath, targetDirectory, overwriteFiles: true);

                _context.Log(LogLevel.Info,
                    $"[AIMod.Update] ✓ 解压完成，目录内容已刷新: {targetDirectory}");
                return true;
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Error,
                    $"[AIMod.Update] ✗ 解压 Mod 包失败，原因: {ex.Message}");
                return false;
            }
        }

        private void EnsureSingleStructure(string packagePath, string folderPath, bool directoryIsFinal)
        {
            if (directoryIsFinal)
            {
                try
                {
                    if (File.Exists(packagePath))
                    {
                        File.Delete(packagePath);
                        _context.Log(LogLevel.Info,
                            "[AIMod.Update] 已删除 AIMod.mod，仅保留同名目录");
                    }
                }
                catch (Exception ex)
                {
                    _context.Log(LogLevel.Warn,
                        $"[AIMod.Update] 删除 AIMod.mod 失败: {ex.Message}");
                }
            }
            else
            {
                try
                {
                    if (Directory.Exists(folderPath))
                    {
                        Directory.Delete(folderPath, recursive: true);
                        _context.Log(LogLevel.Info,
                            "[AIMod.Update] 解压失败，已移除半成品目录，仅保留 .mod 文件");
                    }
                }
                catch (Exception ex)
                {
                    _context.Log(LogLevel.Warn,
                        $"[AIMod.Update] 清理目录失败: {ex.Message}");
                }
            }
        }
    }

    public enum AiMode
    {
        Prefix,
        InterceptAll,
        TRPGPlayer
    }

    public enum AiModelType
    {
        Gemini,
        ZhipuAI,
        SiliconFlow,
        DeepSeek
    }

    public class AiConfig
    {
        // 模式选择
        public AiMode Mode { get; set; } = AiMode.TRPGPlayer;

        // 模型选择和通用设置
        public AiModelType SelectedModel { get; set; } = AiModelType.Gemini;
        public string SystemPrompt { get; set; } = "You are a helpful QQ group chat bot.";
        public List<PrefixRule> PrefixRules { get; set; } = new List<PrefixRule> { new PrefixRule() };
        public bool InterceptAll { get; set; } = false;
        public int TimeoutSeconds { get; set; } = 60;
        public int MaxContextMessages { get; set; } = 10;

        // Gemini 模型配置
        public GeminiConfig GeminiConfig { get; set; } = new GeminiConfig();

        // 智谱AI 模型配置
        public ZhipuAIConfig ZhipuAIConfig { get; set; } = new ZhipuAIConfig();

        // SiliconFlow 模型配置
        public SiliconFlowConfig SiliconFlowConfig { get; set; } = new SiliconFlowConfig();

        // DeepSeek 模型配置
        public DeepSeekConfig DeepSeekConfig { get; set; } = new DeepSeekConfig();

        // TRPG Player 配置
        public TrpgPlayerConfig TrpgConfig { get; set; } = new TrpgPlayerConfig();
    }

    public class GeminiConfig
    {
        public string ApiKey { get; set; } = "YOUR_GEMINI_API_KEY";
        public string ModelName { get; set; } = "gemini-2.5-flash";
    }

    public class ZhipuAIConfig
    {
        public string ApiKey { get; set; } = "YOUR_ZHIPU_API_KEY";
        public string ModelName { get; set; } = "glm-4.7-flash";
    }

    public class SiliconFlowConfig
    {
        public string ApiKey { get; set; } = "YOUR_SILICONFLOW_API_KEY";
        public string ModelName { get; set; } = "Qwen/Qwen3-8B";
    }

    public class DeepSeekConfig
    {
        public string ApiKey { get; set; } = "YOUR_DEEPSEEK_API_KEY";
        public string ModelName { get; set; } = "deepseek-chat";
    }

    public class TrpgPlayerConfig
    {
        public string OocPrefix { get; set; } = "【OOC】";
        public int CooldownSeconds { get; set; } = 60;
        public int TokenThreshold { get; set; } = 6000;
        public string SystemPromptTemplate { get; set; } = "";
        public bool StateInterceptionEnabled { get; set; } = true;
        public int RecallTopK { get; set; } = 1;
        public double RecallMinSimilarity { get; set; } = 0.85;
        public bool EnableNarrativeMemoryDebugLog { get; set; } = false;
        public bool EnableStructuredNarrativeContext { get; set; } = true;
        public bool EnableNarrativeContextLlm { get; set; } = false;
        public bool EnableAffectiveTags { get; set; } = true;
        public bool EnableAffectiveMemoryEncoding { get; set; } = true;

        // 滚动历史窗口配置
        public int RecentHistoryCount { get; set; } = 40; // 统一滚动窗口，不分 OOC/IC
        public int HistoryFoldCount { get; set; } = 20; // 触发归档时保留多少条（将后20条归档）

        // 次级 API 配置（用于 embedding 和 delta extraction，回退到主 API）
        public string SecondaryApiKey { get; set; } = "";
        public string SecondaryModel { get; set; } = "";
        public string SecondaryEndpoint { get; set; } = "";
    }

    public class PrefixRule
    {
        public string Prefix { get; set; } = "/ai ";
        public bool Enabled { get; set; } = true;
    }

    public class UserApiSetting
    {
        public string ApiKey { get; set; } = "";
        public string SubApiKey { get; set; } = "";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public int SelectedProviderIndex { get; set; } = 0;
        public int SelectedModelIndex { get; set; } = 0;
        public long TokenUsageCount { get; set; } = 0;
        public DateTime LastTokenWarningAt { get; set; } = DateTime.MinValue;
    }

    public class AIProvider
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public List<AIModel> Models { get; set; } = new();
    }

    public class AIModel
    {
        public string DisplayName { get; set; } = "";
        public string ModelId { get; set; } = "";
    }

    public enum ModelSelectionStep
    {
        Provider,
        Model
    }

    public class ModelSelectionState
    {
        public ModelSelectionStep Step { get; set; } = ModelSelectionStep.Provider;
        public int SelectedProviderIndex { get; set; } = -1;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public sealed class TokenUsageStats
    {
        public string ProviderId { get; }
        public long PromptTokens { get; }
        public long CompletionTokens { get; }
        public long TotalTokens { get; }
        public long CacheHitTokens { get; }
        public long CacheMissTokens { get; }
        public long ReasoningTokens { get; }
        public bool HasCacheMetrics { get; }

        public TokenUsageStats(
            string providerId,
            long promptTokens,
            long completionTokens,
            long totalTokens,
            long cacheHitTokens,
            long cacheMissTokens,
            long reasoningTokens,
            bool hasCacheMetrics)
        {
            ProviderId = providerId;
            PromptTokens = promptTokens;
            CompletionTokens = completionTokens;
            TotalTokens = totalTokens;
            CacheHitTokens = cacheHitTokens;
            CacheMissTokens = cacheMissTokens;
            ReasoningTokens = reasoningTokens;
            HasCacheMetrics = hasCacheMetrics;
        }

        public static TokenUsageStats Empty(string providerId)
        {
            return new TokenUsageStats(providerId, 0, 0, 0, 0, 0, 0, false);
        }
    }

    public class ActiveGroupApiContext
    {
        public long GroupId { get; set; }
        public string TeamName { get; set; } = "";
        public long OwnerUserId { get; set; }
        public bool OwnerHasElevatedPermission { get; set; }
    }
}
