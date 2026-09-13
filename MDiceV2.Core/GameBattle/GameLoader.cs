using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Reflection;
using MDiceV2.Models;
using MoonSharp.Interpreter;

#nullable enable
namespace MDiceV2.Core.GameBattle
{
    /// <summary>
    /// 内置角色卡数据提供者
    /// </summary>
    public static class BuiltinCharacterProvider
    {
        private static List<CharacterCardData>? _builtinCharacters;
        private static List<SpecialCardData>? _builtinSpecialCards;

        /// <summary>
        /// 获取内置角色卡数据列表
        /// </summary>
        public static List<CharacterCardData> GetBuiltinCharacters()
        {
            if (_builtinCharacters == null)
            {
                _builtinCharacters = LoadJsonData<CharacterCardsData>("characters.json")?.Characters ?? new List<CharacterCardData>();
            }
            return _builtinCharacters;
        }

        /// <summary>
        /// 获取内置特殊卡数据列表
        /// </summary>
        public static List<SpecialCardData> GetBuiltinSpecialCards()
        {
            if (_builtinSpecialCards == null)
            {
                _builtinSpecialCards = LoadJsonData<SpecialCardsData>("specialCards.json")?.SpecialCards ?? new List<SpecialCardData>();
            }
            return _builtinSpecialCards;
        }

        private static T LoadJsonData<T>(string fileName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"MDiceV2.Core.GameBattle.Data.{fileName}";

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    Log.Error($"Embedded resource not found: {resourceName}");
                    return default;
                }
                using (var reader = new StreamReader(stream))
                {
                    string jsonContent = reader.ReadToEnd();
                    try
                    {
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                        };
                        return JsonSerializer.Deserialize<T>(jsonContent, options);
                    }
                    catch (JsonException ex)
                    {
                        Log.Error($"Failed to parse {fileName}: {ex.Message}");
                        return default;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 游戏数据加载器，负责加载角色卡数据和技能
    /// </summary>
    public static class GameLoader
    {
        // 注：使用 Directory.GetCurrentDirectory() 确保获取根目录
        // 原因：Launcher 已设置 WorkingDirectory = 根目录，Core 从根目录启动
        // 参考：TRPGLogManager.cs
        private static readonly string RootDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            "Duel"
        );

        private static readonly string ExtensionDirectory = Path.Combine(
            RootDirectory,
            "Extension"
        );

        private static readonly string CharacterExtensionDirectory = Path.Combine(
            ExtensionDirectory,
            "Character"
        );

        private static readonly string SpecialExtensionDirectory = Path.Combine(
            ExtensionDirectory,
            "Special"
        );

        private static readonly object _initLock = new object();
        private static bool _initialized;

        // 加载的角色数据缓存
        private static Dictionary<string, Character> _characterCache = new Dictionary<string, Character>();
        private static Dictionary<Faction, List<Character>> _factionCharacters = new Dictionary<Faction, List<Character>>();
        private static Dictionary<string, CharacterCardData> _characterDataCache = new Dictionary<string, CharacterCardData>();

        // 加载的特殊卡数据缓存
        private static Dictionary<string, SpecialCard> _specialCardCache = new Dictionary<string, SpecialCard>();
        private static Dictionary<Faction, List<SpecialCard>> _factionSpecialCards = new Dictionary<Faction, List<SpecialCard>>();
        private static Dictionary<string, SpecialCardData> _specialCardDataCache = new Dictionary<string, SpecialCardData>();

        // Lua技能系统
        private static Script? _luaScript;
        private static Dictionary<string, LuaSkill> _luaSkillCache = new Dictionary<string, LuaSkill>();
        private static Table? _skillDefinitions;

        // 技能叙述文本缓存
        private static Dictionary<string, string> _skillNarratives = new Dictionary<string, string>();

        // 技能函数缓存（兼容旧系统）
        private static Dictionary<string, Delegate> _skillFunctions = new Dictionary<string, Delegate>();

        // RuleDataIO 实例，用于存储help数据到duel表
        private static RuleDataIO? _ruleDataIO;

        /// <summary>
        /// 根据阵营获取角色卡池（考虑多阵营角色）
        /// </summary>
        public static List<Character> GetCharacterPoolByFaction(Faction faction)
        {
            var pool = new List<Character>();
            foreach (var character in _characterCache.Values)
            {
                var charData = _characterDataCache.FirstOrDefault(x => x.Value.Name == character.Name).Value;
                if (charData != null && charData.Factions.Contains(faction))
                {
                    // 如果角色属于指定阵营，将其添加到池中（按权重复制）
                    int weight = GetDrawWeight(character);
                    for (int i = 0; i < weight; i++)
                    {
                        pool.Add(character);
                    }
                }
            }
            // 调试日志：输出卡池中各类卡牌的数量
            var cardCounts = pool.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.Count());
            var logMessage = $"[抽卡池调试] 阵营 {faction} 的角色卡池已构建，总数: {pool.Count}。构成: ";
            logMessage += string.Join(", ", cardCounts.Select(kv => $"{kv.Key} x{kv.Value}"));
            Log.InfoFormat(logMessage);

            return pool;
        }

        /// <summary>
        /// 根据阵营获取特殊卡池（考虑多阵营卡牌）
        /// </summary>
        public static List<SpecialCard> GetSpecialCardPoolByFaction(Faction faction)
        {
            var pool = new List<SpecialCard>();
            foreach (var specialCard in _specialCardCache.Values)
            {
                var cardData = _specialCardDataCache.FirstOrDefault(x => x.Value.Name == specialCard.Name).Value;
                if (cardData != null && cardData.Factions.Contains(faction))
                {
                    // 如果特殊卡属于指定阵营，将其添加到池中（按权重复制）
                    int weight = GetDrawWeight(specialCard);
                    for (int i = 0; i < weight; i++)
                    {
                        pool.Add(specialCard);
                    }
                }
            }
            return pool;
        }

        /// <summary>
        /// 初始化游戏数据加载器
        /// </summary>
        public static bool Initialize(bool forceReload = false)
        {
            lock (_initLock)
            {
                if (_initialized && !forceReload)
                {
                    return true;
                }

                try
                {
                    Log.InfoFormat("[GameLoader] ========== 初始化开始 ==========");
                    ResetCaches();

                    int successSteps = 0;
                    int failureSteps = 0;

                    // 步骤1：初始化Lua脚本引擎
                    try
                    {
                        InitializeLuaScript();
                        Log.InfoFormat("[GameLoader] ✓ Lua脚本引擎初始化成功");
                        successSteps++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[GameLoader] ✗ Lua脚本引擎初始化失败: {ex.Message}，但继续加载其他资源");
                        failureSteps++;
                    }

                    // 步骤2：初始化RuleDataIO
                    try
                    {
                        _ruleDataIO = new RuleDataIO();
                        _ruleDataIO.CreateTableIfNotExists("duel");
                        Log.InfoFormat("[GameLoader] ✓ RuleDataIO初始化成功");
                        successSteps++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[GameLoader] ✗ RuleDataIO初始化失败: {ex.Message}，但继续加载其他资源");
                        _ruleDataIO = null;
                        failureSteps++;
                    }

                    // 步骤3：加载硬编码规则
                    try
                    {
                        LoadHardcodedRules();
                        Log.InfoFormat("[GameLoader] ✓ 硬编码规则加载成功");
                        successSteps++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[GameLoader] ✗ 硬编码规则加载失败: {ex.Message}，但继续加载其他资源");
                        failureSteps++;
                    }

                    // 步骤4：加载技能定义
                    try
                    {
                        LoadSkills();
                        Log.InfoFormat("[GameLoader] ✓ 技能定义加载成功");
                        successSteps++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[GameLoader] ✗ 技能定义加载失败: {ex.Message}，但继续加载其他资源");
                        failureSteps++;
                        // 技能失败不影响基础卡牌数据，继续
                    }

                    // 步骤5：加载角色卡
                    try
                    {
                        LoadCharacters();
                        Log.InfoFormat("[GameLoader] ✓ 角色卡加载成功，共 {0} 个角色", _characterCache.Count);
                        successSteps++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[GameLoader] ✗ 角色卡加载失败: {ex.Message}，游戏可能无法正常进行");
                        failureSteps++;
                    }

                    // 步骤6：加载特殊卡
                    try
                    {
                        LoadSpecialCards();
                        Log.InfoFormat("[GameLoader] ✓ 特殊卡加载成功，共 {0} 个特殊卡", _specialCardCache.Count);
                        successSteps++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[GameLoader] ✗ 特殊卡加载失败: {ex.Message}，游戏可能无法正常进行");
                        failureSteps++;
                    }

                    // 步骤7：加载额外反馈库
                    try
                    {
                        LoadExtraFeedback();
                        Log.InfoFormat("[GameLoader] ✓ 额外反馈库加载成功");
                        successSteps++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[GameLoader] ✗ 额外反馈库加载失败: {ex.Message}，但继续运行");
                        failureSteps++;
                    }

                    // 关键检查：必须有角色卡和特殊卡数据才能进行游戏
                    var totalCharacters = _characterCache.Count;
                    var totalSpecialCards = _specialCardCache.Count;

                    Log.InfoFormat("[GameLoader] ========== 初始化检查 ==========");
                    Log.InfoFormat("[GameLoader] 成功的步骤: {0}, 失败的步骤: {1}", successSteps, failureSteps);
                    Log.InfoFormat("[GameLoader] 卡牌统计 - 角色卡: {0}, 特殊卡: {1}", totalCharacters, totalSpecialCards);

                    if (totalCharacters == 0 && totalSpecialCards == 0)
                    {
                        Log.Error("[GameLoader] ✗ 致命错误：没有任何卡牌数据（既没有角色卡也没有特殊卡），初始化失败");
                        _initialized = false;
                        return false;
                    }

                    if (totalCharacters == 0)
                    {
                        Log.Error("[GameLoader] ✗ 致命错误：没有角色卡数据，无法进行游戏");
                        _initialized = false;
                        return false;
                    }

                    if (totalSpecialCards == 0)
                    {
                        Log.Warn("[GameLoader] ⚠ 警告：没有特殊卡数据，可能影响游戏体验（但仍可继续）");
                    }

                    _initialized = true;
                    Log.InfoFormat("[GameLoader] ========== 初始化成功 ==========");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[GameLoader] ✗ 初始化过程中发生未捕获的异常: {ex.Message}");
                    Log.Error($"[GameLoader] 堆栈跟踪: {ex.StackTrace}");
                    _initialized = false;
                    return false;
                }
            }
        }

        private static void ResetCaches()
        {
            _luaScript = null;
            _skillDefinitions = null;

            _characterCache = new Dictionary<string, Character>();
            _factionCharacters = new Dictionary<Faction, List<Character>>();
            _characterDataCache = new Dictionary<string, CharacterCardData>();

            _specialCardCache = new Dictionary<string, SpecialCard>();
            _factionSpecialCards = new Dictionary<Faction, List<SpecialCard>>();
            _specialCardDataCache = new Dictionary<string, SpecialCardData>();

            _luaSkillCache = new Dictionary<string, LuaSkill>();
            _skillNarratives = new Dictionary<string, string>();
            _skillFunctions = new Dictionary<string, Delegate>();
        }

        /// <summary>
        /// 初始化Lua脚本引擎
        /// </summary>
        private static void InitializeLuaScript()
        {
            _luaScript = new Script();

            // 注册C#类型到Lua
            UserData.RegisterType<GameState>();
            UserData.RegisterType<Player>();
            UserData.RegisterType<Character>();
            UserData.RegisterType<FieldManager>();
            UserData.RegisterType<Field>();
            UserData.RegisterType<LuaSkill>();
            UserData.RegisterType<EntranceSkill>();
            UserData.RegisterType<ISkillContext>();
            // 让 Lua 能够接收 DiceResult 对象，便于在脚本中获取完整的掷骰明细
            UserData.RegisterType<MDiceV2.Models.DiceResult>();

            // 加载内建技能脚本
            LoadEmbeddedLuaScripts();

            // 缓存技能定义
            var skillDefs = _luaScript.Globals.Get("SkillDefinitions");
            if (skillDefs != null && skillDefs.Type == DataType.Table)
            {
                _skillDefinitions = skillDefs.Table;
            }

            // 调试：检查技能定义表加载状态
            if (_skillDefinitions != null)
            {
                Log.InfoFormat($"[Lua调试] 技能定义表已加载，包含 {_skillDefinitions.Pairs.Count()} 个技能定义");
                foreach (var pair in _skillDefinitions.Pairs)
                {
                    string skillId = pair.Key.String;
                    Table skillDef = pair.Value.Table;
                    string skillName = skillDef.Get("name")?.String ?? "未知";
                    string trigger = skillDef.Get("trigger")?.String ?? "未知";
                    string luaFunction = skillDef.Get("luaFunction")?.String ?? "未知";
                    Log.InfoFormat($"[Lua调试] 技能定义: {skillId} -> {skillName} ({trigger}, 函数: {luaFunction})");
                }
            }
            else
            {
                //Log.Warn("[Lua调试] 技能定义表为null，未正确加载");
            }

            Log.InfoFormat("Lua script engine initialized");
        }

        /// <summary>
        /// 加载嵌入的Lua脚本
        /// </summary>
        private static void LoadEmbeddedLuaScripts()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceNames = assembly.GetManifestResourceNames()
                    .Where(name => name.EndsWith(".lua"))
                    .ToArray();

                foreach (var resourceName in resourceNames)
                {
                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream != null)
                        {
                            using (var reader = new StreamReader(stream))
                            {
                                var scriptContent = reader.ReadToEnd();
                                _luaScript!.DoString(scriptContent);
                                Log.InfoFormat($"Loaded embedded Lua script: {resourceName}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load embedded Lua scripts: {ex.Message}");
            }
        }

        /// <summary>
        /// 从硬编码JSON文件加载规则数据到RuleDataIO
        /// 支持多表结构：{ "tableName": { "key": "value", ... }, ... }
        /// </summary>
        private static void LoadHardcodedRules()
        {
            if (_ruleDataIO == null)
            {
                Log.Warn("[HardcodedRules] RuleDataIO is null, skipping hardcoded rules loading");
                return;
            }

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                const string resourceName = "MDiceV2.Core.Resources.hardcoded_rules.json";

                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        Log.Warn($"[HardcodedRules] 嵌入式资源未找到: {resourceName}");
                        return;
                    }

                    using (var reader = new StreamReader(stream))
                    {
                        string jsonContent = reader.ReadToEnd();

                        // 解析多表结构
                        using (JsonDocument doc = JsonDocument.Parse(jsonContent))
                        {
                            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                            {
                                Log.Warn("[HardcodedRules] JSON根元素必须是对象");
                                return;
                            }

                            foreach (var tableProperty in doc.RootElement.EnumerateObject())
                            {
                                string tableName = tableProperty.Name;
                                
                                // 确保表存在
                                _ruleDataIO.CreateTableIfNotExists(tableName);

                                if (tableProperty.Value.ValueKind != JsonValueKind.Object)
                                {
                                    Log.Warn($"[HardcodedRules] 表 '{tableName}' 的值必须是对象，已跳过");
                                    continue;
                                }

                                int recordCount = 0;
                                foreach (var record in tableProperty.Value.EnumerateObject())
                                {
                                    string key = record.Name;
                                    string value = record.Value.GetRawText();

                                    // 如果value本身是字符串，提取字符串值
                                    if (record.Value.ValueKind == JsonValueKind.String)
                                    {
                                        value = record.Value.GetString() ?? "";
                                    }

                                    if (!string.IsNullOrEmpty(key))
                                    {
                                        _ruleDataIO.SaveData(tableName, key, value);
                                        recordCount++;
                                    }
                                }

                                Log.InfoFormat("[HardcodedRules] 表 '{0}' 已加载，记录数：{1}", tableName, recordCount);
                            }
                        }

                        Log.InfoFormat("[HardcodedRules] 硬编码规则加载完成");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[HardcodedRules] 加载硬编码规则失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载角色数据
        /// </summary>
        private static void LoadCharacters()
        {
            try
            {
                var allCharacterData = new List<CharacterCardData>();

                // 加载内置角色数据
                allCharacterData.AddRange(BuiltinCharacterProvider.GetBuiltinCharacters());

                // 加载扩展角色数据
                try
                {
                    if (Directory.Exists(CharacterExtensionDirectory))
                    {
                        var jsonFiles = Directory.GetFiles(CharacterExtensionDirectory, "*.json");
                        foreach (var jsonFile in jsonFiles)
                        {
                            try
                            {
                                string jsonContent = File.ReadAllText(jsonFile);
                                var charactersData = JsonSerializer.Deserialize<CharacterCardsData>(jsonContent, new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                });

                                if (charactersData?.Characters != null)
                                {
                                    allCharacterData.AddRange(charactersData.Characters);
                                    Log.InfoFormat($"Loaded extension characters from: {Path.GetFileName(jsonFile)}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warn($"Failed to load extension character file {jsonFile}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to load extension characters: {ex.Message}");
                }

                // 为角色数据创建Character实例并加载技能
                foreach (var charData in allCharacterData)
                {
                    if (_characterCache.ContainsKey(charData.Name))
                    {
                        Log.Warn($"Duplicate character name found: {charData.Name}, skipping");
                        continue;
                    }

                    // 缓存原始数据以便后续查询稀有度等信息
                    _characterDataCache[charData.Name] = charData;

                    var character = new Character
                    {
                        Name = charData.Name,
                        Power = charData.Power,
                        Wealth = charData.Wealth,
                        Fame = charData.Fame,
                        Rarity = charData.Rarity,
                        FieldPreference = charData.FieldPreference,
                        Tags = charData.Tags ?? new List<string>(),
                        // 兼容性: 如果JSON中未指定perTurnRecovery，则默认使用原有的Power/Wealth/Fame作为每回合贡献
                        PerTurnPower = charData.PerTurnRecovery != null ? charData.PerTurnRecovery.Power : charData.Power,
                        PerTurnWealth = charData.PerTurnRecovery != null ? charData.PerTurnRecovery.Wealth : charData.Wealth,
                        PerTurnFame = charData.PerTurnRecovery != null ? charData.PerTurnRecovery.Fame : charData.Fame,
                        // 不再加载登场加成字段（已移除），忽略 JSON 中的 entranceBonus
                    };

                    // 加载Lua技能实例
                    LoadCharacterSkills(character, charData);

                    // 缓存角色叙述文本
                    foreach (var kvp in charData.SkillNarratives)
                    {
                        _skillNarratives[kvp.Key] = kvp.Value;
                    }

                    _characterCache[charData.Name] = character;

                    // 将help数据存储到duel表
                    if (!string.IsNullOrEmpty(charData.Help) && _ruleDataIO != null)
                    {
                        _ruleDataIO.SaveData("duel", charData.Name, charData.Help);
                        charData.Help = null;  // 转存完成后释放内存
                    }

                    // 将角色添加到其所属的所有阵营
                    foreach (var faction in charData.Factions)
                    {
                        if (!_factionCharacters.ContainsKey(faction))
                        {
                            _factionCharacters[faction] = new List<Character>();
                        }
                        _factionCharacters[faction].Add(character);
                    }

                    // Log.InfoFormat($"Loaded character: {charData.Name} ({string.Join(", ", charData.Factions)})");
                }

                Log.InfoFormat($"Total characters loaded: {_characterCache.Count}");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load characters: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载特殊卡数据
        /// </summary>
        private static void LoadSpecialCards()
        {
            try
            {
                var allSpecialCardData = new List<SpecialCardData>();

                // 加载内置特殊卡数据
                allSpecialCardData.AddRange(BuiltinCharacterProvider.GetBuiltinSpecialCards());

                // 加载扩展特殊卡数据
                try
                {
                    if (Directory.Exists(SpecialExtensionDirectory))
                    {
                        var jsonFiles = Directory.GetFiles(SpecialExtensionDirectory, "*.json");
                        foreach (var jsonFile in jsonFiles)
                        {
                            try
                            {
                                string jsonContent = File.ReadAllText(jsonFile);
                                var specialCardsData = JsonSerializer.Deserialize<SpecialCardsData>(jsonContent, new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                });

                                if (specialCardsData?.SpecialCards != null)
                                {
                                    allSpecialCardData.AddRange(specialCardsData.SpecialCards);
                                    Log.InfoFormat($"Loaded extension special cards from: {Path.GetFileName(jsonFile)}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warn($"Failed to load extension special card file {jsonFile}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to load extension special cards: {ex.Message}");
                }

                _specialCardCache.Clear();
                _specialCardDataCache.Clear();
                _factionSpecialCards.Clear();
                _factionSpecialCards[Faction.Human] = new List<SpecialCard>();
                _factionSpecialCards[Faction.Demon] = new List<SpecialCard>();

                foreach (var cardData in allSpecialCardData)
                {
                    if (_specialCardCache.ContainsKey(cardData.Name ?? ""))
                    {
                        // Log.Warn($"Duplicate special card name found: {cardData.Name}, skipping");
                        continue;
                    }

                    // 缓存原始数据以便后续查询稀有度等信息
                    _specialCardDataCache[cardData.Name ?? ""] = cardData;

                    var specialCard = new SpecialCard
                    {
                        Name = cardData.Name,
                        SpecialType = cardData.Type,
                        Effect = cardData.Effect
                    };

                    // 新增：加载立即技能（取第一个固有技能作为立即技能）
                    if (cardData.InnateSkills.Count == 0)
                    {
                        Log.Error($"[特殊卡技能] 特殊卡 '{cardData.Name}' 没有配置技能！所有特殊卡都必须有技能。");
                        throw new InvalidOperationException($"特殊卡 '{cardData.Name}' 必须配置技能");
                    }

                    var skillEntry = cardData.InnateSkills[0];
                    if (_luaSkillCache.TryGetValue(skillEntry.SkillId, out var luaSkill))
                    {
                        specialCard.ImmediateSkill = CloneSkillWithParameters(luaSkill, skillEntry.Parameters);
                        Log.InfoFormat($"[特殊卡技能] 加载立即技能：{cardData.Name} -> {skillEntry.SkillId}");
                    }
                    else
                    {
                        Log.Error($"[特殊卡技能] 未找到技能定义：{skillEntry.SkillId}（特殊卡：{cardData.Name}）");
                        throw new InvalidOperationException($"特殊卡 '{cardData.Name}' 的技能 '{skillEntry.SkillId}' 未找到定义");
                    }

                    _specialCardCache[cardData.Name] = specialCard;

                    // 将help数据存储到duel表
                    if (!string.IsNullOrEmpty(cardData.Help) && _ruleDataIO != null)
                    {
                        _ruleDataIO.SaveData("duel", cardData.Name, cardData.Help);
                        cardData.Help = null;  // 转存完成后释放内存
                    }

                    // 缓存特殊卡叙述文本（参照角色系统）
                    foreach (var kvp in cardData.SkillNarratives)
                    {
                        _skillNarratives[kvp.Key] = kvp.Value;
                        Log.InfoFormat($"[特殊卡叙述] 加载叙述文本: {kvp.Key}");
                    }

                    // 将特殊卡添加到其所属的所有阵营
                    foreach (var faction in cardData.Factions)
                    {
                        if (!_factionSpecialCards.ContainsKey(faction))
                        {
                            _factionSpecialCards[faction] = new List<SpecialCard>();
                        }
                        _factionSpecialCards[faction].Add(specialCard);
                    }

                    // Log.InfoFormat($"Loaded special card: {cardData.Name} ({string.Join(", ", cardData.Factions)})");
                }

                // Log.InfoFormat($"Total special cards loaded: {_specialCardCache.Count}");
            }
            catch (Exception)
            {
                // Log.Error($"Failed to load special cards: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载技能函数（从Lua脚本加载）
        /// </summary>
        private static void LoadSkills()
        {
            try
            {
                // 加载内建技能定义
                LoadBuiltinSkills();

                Log.InfoFormat("Skills loaded successfully");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load skills: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载额外反馈库配置
        /// </summary>
        private static void LoadExtraFeedback()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = "MDiceV2.Core.GameBattle.Data.extrafeedback.json";

                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        Log.Warn($"Extra feedback resource not found: {resourceName}");
                        return;
                    }

                    using (var reader = new StreamReader(stream))
                    {
                        var jsonContent = reader.ReadToEnd();
                        using (var jsonDoc = JsonDocument.Parse(jsonContent))
                        {
                            var root = jsonDoc.RootElement;

                            if (root.TryGetProperty("feedbackDecks", out _))
                            {
                                Log.Warn("feedbackDecks is no longer supported; use defaultPublicDeck instead.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load extra feedback: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载内建技能定义
        /// </summary>
        private static void LoadBuiltinSkills()
        {
            if (_skillDefinitions == null)
            {
                Log.Warn("Skill definitions not loaded");
                return;
            }

            foreach (var pair in _skillDefinitions.Pairs)
            {
                string skillId = pair.Key.String;
                Table skillDef = pair.Value.Table;

                try
                {
                    // 根据技能类型创建相应的技能实例
                    LuaSkill luaSkill;
                    string triggerString = skillDef.Get("trigger").String;

                    if (triggerString == "Entrance")
                    {
                        luaSkill = new EntranceSkill();
                    }
                    else
                    {
                        luaSkill = new LuaSkill();
                    }

                    luaSkill.SkillId = skillId;
                    luaSkill.Name = skillDef.Get("name").String;
                    luaSkill.Trigger = ParseSkillTrigger(triggerString);
                    luaSkill.LuaFunctionName = skillDef.Get("luaFunction").String;
                    luaSkill.LuaScript = _luaScript;

                    // 加载参数（如果有）
                    var parameters = skillDef.Get("parameters");
                    if (parameters != null && parameters.Type == DataType.Table)
                    {
                        foreach (var paramPair in parameters.Table.Pairs)
                        {
                            string paramName = paramPair.Key.String;
                            var paramValue = paramPair.Value;

                            // 根据参数名设置技能属性
                            if (luaSkill is EntranceSkill entranceSkill)
                            {
                                switch (paramName)
                                {
                                    case "FrontPowerBonus":
                                        entranceSkill.FrontPowerBonus = (int)paramValue.Number;
                                        break;
                                    case "MiddleWealthBonus":
                                        entranceSkill.MiddleWealthBonus = (int)paramValue.Number;
                                        break;
                                    case "BackFameBonus":
                                        entranceSkill.BackFameBonus = (int)paramValue.Number;
                                        break;
                                }
                            }
                        }
                    }

                    _luaSkillCache[skillId] = luaSkill;
                    //Log.InfoFormat($"Loaded skill: {skillId} ({luaSkill.Name})");

                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to load skill {skillId}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 解析技能触发类型
        /// </summary>
        private static SkillTrigger ParseSkillTrigger(string triggerString)
        {
            return triggerString switch
            {
                "Entrance" => SkillTrigger.Entrance,
                "TurnEnd" => SkillTrigger.TurnEnd,
                "Field" => SkillTrigger.Field,
                "Chain" => SkillTrigger.Chain,
                "Event" => SkillTrigger.Event,
                _ => SkillTrigger.Field
            };
        }


        /// <summary>
        /// 获取所有角色
        /// </summary>
        public static List<Character> GetAllCharacters()
        {
            return _characterCache.Values.ToList();
        }

        /// <summary>
        /// 根据阵营获取角色
        /// </summary>
        public static List<Character> GetCharactersByFaction(Faction faction)
        {
            return _factionCharacters.TryGetValue(faction, out var characters) ? characters : new List<Character>();
        }

        /// <summary>
        /// 获取所有特殊卡
        /// </summary>
        public static List<SpecialCard> GetAllSpecialCards()
        {
            return _specialCardCache.Values.ToList();
        }

        /// <summary>
        /// 根据阵营获取特殊卡
        /// </summary>
        public static List<SpecialCard> GetSpecialCardsByFaction(Faction faction)
        {
            return _factionSpecialCards.TryGetValue(faction, out var specialCards) ? specialCards : new List<SpecialCard>();
        }

        /// <summary>
        /// 根据名称获取特殊卡
        /// </summary>
        public static SpecialCard? GetSpecialCardByName(string name)
        {
            return _specialCardCache.TryGetValue(name, out var specialCard) ? specialCard : null;
        }

        /// <summary>
        /// 根据名称获取角色
        /// </summary>
        public static Character? GetCharacterByName(string name)
        {
            return _characterCache.TryGetValue(name, out var character) ? character : null;
        }

        /// <summary>
        /// 获取角色抽卡权重（基于稀有度，使用自然数权重）
        /// </summary>
        public static int GetDrawWeight(Character character)
        {
            // 从_characterDataCache中获取稀有度信息
            var charData = _characterDataCache.FirstOrDefault(x => x.Value.Name == character.Name).Value;
            if (charData == null) return 1;

            // 根据稀有度返回自然数权重
            return charData.Rarity switch
            {
                Rarity.Named => 1,       // 具名：权重1
                Rarity.Legendary => 3,   // 传奇：权重3
                Rarity.Epic => 6,        // 史诗：权重6
                Rarity.Rare => 10,       // 稀有：权重10
                Rarity.Common => 15,     // 普通：权重15
                _ => 1
            };
        }

        /// <summary>
        /// 获取特殊卡抽卡权重（基于稀有度，使用自然数权重）
        /// </summary>
        public static int GetDrawWeight(SpecialCard specialCard)
        {
            // 从_specialCardDataCache中获取稀有度信息
            var cardData = _specialCardDataCache.FirstOrDefault(x => x.Value.Name == specialCard.Name).Value;
            if (cardData == null) return 1;

            // 根据稀有度返回自然数权重
            return cardData.Rarity switch
            {
                Rarity.Named => 1,       // 具名：权重1
                Rarity.Legendary => 3,   // 传奇：权重3
                Rarity.Epic => 6,        // 史诗：权重6
                Rarity.Rare => 10,       // 稀有：权重10
                Rarity.Common => 15,     // 普通：权重15
                _ => 1
            };
        }

        /// <summary>
        /// 为角色加载技能
        /// </summary>
        private static void LoadCharacterSkills(Character character, CharacterCardData charData)
        {
            //Log.InfoFormat($"[技能加载] 开始为角色 '{character.Name}' 加载技能");

            // 加载固有技能
            var innateSkillIds = charData.InnateSkills.Select(s => s.SkillId).ToList();
            //Log.InfoFormat($"[技能加载] 角色 '{character.Name}' 有 {charData.InnateSkills.Count} 个固有技能: {string.Join(", ", innateSkillIds)}");
            foreach (var skillEntry in charData.InnateSkills)
            {
                //Log.InfoFormat($"[技能加载] 处理固有技能: {skillEntry.SkillId}");
                if (_luaSkillCache.TryGetValue(skillEntry.SkillId, out var luaSkill))
                {
                    // 复制技能实例以避免共享参数
                    var skillInstance = CloneSkillWithParameters(luaSkill, skillEntry.Parameters);
                    Log.InfoFormat($"[技能加载] 从Lua缓存找到技能: {skillEntry.SkillId} ({skillInstance.Name}, 触发: {skillInstance.Trigger})");
                    character.AddLuaSkill(skillInstance);
                    Log.InfoFormat($"[技能加载] 已添加到LuaSkills列表，总数: {character.LuaSkills.Count}");
                }
                else
                {
                    Log.Error($"[技能加载] Lua缓存中未找到固有技能 {skillEntry.SkillId}，放弃此技能委托");
                }
            }

            // 加载连携技能
            var chainSkillIds = charData.ChainSkills.Select(s => s.SkillId).ToList();
            //Log.InfoFormat($"[技能加载] 角色 '{character.Name}' 有 {charData.ChainSkills.Count} 个连携技能: {string.Join(", ", chainSkillIds)}");
            foreach (var skillEntry in charData.ChainSkills)
            {
                //Log.InfoFormat($"[技能加载] 处理连携技能: {skillEntry.SkillId}");
                if (_luaSkillCache.TryGetValue(skillEntry.SkillId, out var luaSkill))
                {
                    // 复制技能实例以避免共享参数
                    var skillInstance = CloneSkillWithParameters(luaSkill, skillEntry.Parameters);
                    Log.InfoFormat($"[技能加载] 从Lua缓存找到技能: {skillEntry.SkillId} ({skillInstance.Name}, 触发: {skillInstance.Trigger})");
                    character.AddLuaSkill(skillInstance);
                    Log.InfoFormat($"[技能加载] 已添加到LuaSkills列表，总数: {character.LuaSkills.Count}");
                }
                else
                {
                    Log.Error($"[技能加载] Lua缓存中未找到连携技能 {skillEntry.SkillId}，放弃此技能委托");
                }
            }

            // 加载事件技能（亡语技能）
            if (charData.EventSkill != null && !string.IsNullOrEmpty(charData.EventSkill.SkillId))
            {
                Log.InfoFormat($"[技能加载] 处理事件技能: {charData.EventSkill.SkillId}");
                if (_luaSkillCache.TryGetValue(charData.EventSkill.SkillId, out var luaSkill))
                {
                    // 复制技能实例以避免共享参数
                    var skillInstance = CloneSkillWithParameters(luaSkill, charData.EventSkill.Parameters);
                    Log.InfoFormat($"[技能加载] 从Lua缓存找到技能: {charData.EventSkill.SkillId} ({skillInstance.Name}, 触发: {skillInstance.Trigger})");
                    
                    // 【关键】仅设置OnRemoved委托，不添加到LuaSkills（避免每回合都执行）
                    // 设置OnRemoved委托：当角色被移除时，执行该事件技能（亡语技能）
                    character.OnRemoved = (context) =>
                    {
                        Log.InfoFormat($"[亡语技能] {character.Name} 触发亡语技能: {skillInstance.Name}");
                        skillInstance.Execute(context);
                    };
                    
                    Log.InfoFormat($"[技能加载] 已配置亡语技能委托: {charData.EventSkill.SkillId}");
                }
                else
                {
                    Log.Error($"[技能加载] Lua缓存中未找到事件技能 {charData.EventSkill.SkillId}，放弃此技能委托");
                }
            }

            //Log.InfoFormat($"[技能加载] 角色 '{character.Name}' 技能加载完成 - Lua技能: {character.LuaSkills.Count}, 传统委托: {character.Skills.Count}");
        }

        /// <summary>
        /// 复制技能实例并应用参数
        /// </summary>
        private static LuaSkill CloneSkillWithParameters(LuaSkill sourceSkill, Dictionary<string, object> parameters)
        {
            // 创建技能实例副本
            LuaSkill skillInstance;
            if (sourceSkill is EntranceSkill entranceSourceSkill)
            {
                // 对于EntranceSkill，需要复制特定属性
                skillInstance = new EntranceSkill
                {
                    FrontPowerBonus = entranceSourceSkill.FrontPowerBonus,
                    MiddleWealthBonus = entranceSourceSkill.MiddleWealthBonus,
                    BackFameBonus = entranceSourceSkill.BackFameBonus
                };
            }
            else
            {
                skillInstance = new LuaSkill();
            }

            // 复制基本属性
            skillInstance.SkillId = sourceSkill.SkillId;
            skillInstance.Name = sourceSkill.Name;
            skillInstance.Description = sourceSkill.Description;
            skillInstance.Trigger = sourceSkill.Trigger;
            skillInstance.LuaFunctionName = sourceSkill.LuaFunctionName;
            skillInstance.LuaScript = sourceSkill.LuaScript;

            // 应用参数
            if (parameters != null && parameters.Any())
            {
                // 将JsonElement转换为C#基本类型，避免Lua类型转换错误
                var convertedParameters = ConvertJsonElementsToBasicTypes(parameters);
                skillInstance.Parameters = convertedParameters;

                // 如果是EntranceSkill，应用特定的参数
                if (skillInstance is EntranceSkill entranceSkill)
                {
                    // 应用前场武力加成
                    if (convertedParameters.TryGetValue("FrontPowerBonus", out var frontPowerObj))
                    {
                        if (frontPowerObj is int frontPower)
                        {
                            entranceSkill.FrontPowerBonus = frontPower;
                        }
                        else if (frontPowerObj is long frontPowerLong)
                        {
                            entranceSkill.FrontPowerBonus = (int)frontPowerLong;
                        }
                    }

                    // 应用中场财力加成
                    if (convertedParameters.TryGetValue("MiddleWealthBonus", out var middleWealthObj))
                    {
                        if (middleWealthObj is int middleWealth)
                        {
                            entranceSkill.MiddleWealthBonus = middleWealth;
                        }
                        else if (middleWealthObj is long middleWealthLong)
                        {
                            entranceSkill.MiddleWealthBonus = (int)middleWealthLong;
                        }
                    }

                    // 应用后场名声加成
                    if (convertedParameters.TryGetValue("BackFameBonus", out var backFameObj))
                    {
                        if (backFameObj is int backFame)
                        {
                            entranceSkill.BackFameBonus = backFame;
                        }
                        else if (backFameObj is long backFameLong)
                        {
                            entranceSkill.BackFameBonus = (int)backFameLong;
                        }
                    }

                    Log.InfoFormat($"[技能参数] 已应用参数到 {entranceSkill.Name}: FrontPowerBonus={entranceSkill.FrontPowerBonus}, MiddleWealthBonus={entranceSkill.MiddleWealthBonus}, BackFameBonus={entranceSkill.BackFameBonus}");
                }
            }

            return skillInstance;
        }

        /// <summary>
        /// 将JsonElement转换为C#基本类型，避免Lua类型转换错误
        /// </summary>
        private static Dictionary<string, object> ConvertJsonElementsToBasicTypes(Dictionary<string, object> parameters)
        {
            var converted = new Dictionary<string, object>();
            
            foreach (var kvp in parameters)
            {
                object value = kvp.Value;
                
                // 处理JsonElement类型
                if (value is JsonElement jsonElement)
                {
                    value = ConvertJsonElement(jsonElement);
                }
                
                converted[kvp.Key] = value;
            }
            
            return converted;
        }

        /// <summary>
        /// 将JsonElement转换为基本类型
        /// </summary>
        private static object? ConvertJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Number => element.GetInt32(),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => element.ToString()
            };
        }

        /// <summary>
        /// 获取技能叙述文本
        /// </summary>
        public static string? GetSkillNarrative(string skillId, SkillTrigger trigger)
        {
            string key = $"{skillId}_{trigger}";
            if (_skillNarratives.TryGetValue(key, out var narrative))
            {
                return narrative;
            }
            return null; // 返回null表示使用默认文本
        }

        /// <summary>
        /// 执行技能函数
        /// </summary>
        public static void ExecuteSkill(string skillId, params object[] parameters)
        {
            if (_skillFunctions.TryGetValue(skillId, out var skillFunction))
            {
                try
                {
                    skillFunction.DynamicInvoke(parameters);
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to execute skill {skillId}: {ex.Message}");
                }
            }
            else
            {
                Log.Warn($"Skill function not found: {skillId}");
            }
        }
    }
}