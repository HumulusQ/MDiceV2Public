-- 内建技能脚本和默认叙述文本
-- 这个文件会被嵌入到DLL中，作为默认技能实现

-- 获取技能参数的辅助函数
function GetSkillParameter(skill, paramName, defaultValue)
    if skill.Parameters and skill.Parameters[paramName] then
        return skill.Parameters[paramName]
    end
    return defaultValue
end

-- 简化的叙述文本获取函数（现在通过context参数调用）
function GetSkillNarrative(skillId, trigger)
    -- 此函数已简化，叙述文本现在通过context参数从JSON配置中获取
    return "[技能系统] 触发技能: " .. skillId
end

-- 获取技能整型参数的辅助函数
function GetSkillIntParameter(skill, paramName, defaultValue)
    local value = GetSkillParameter(skill, paramName, defaultValue)
    if type(value) == "number" then
        return math.floor(value)
    end
    return defaultValue
end

-- 获取技能浮点参数的辅助函数
function GetSkillFloatParameter(skill, paramName, defaultValue)
    local value = GetSkillParameter(skill, paramName, defaultValue)
    if type(value) == "number" then
        return value
    end
    return defaultValue
end

-- 获取技能字符串参数的辅助函数
function GetSkillStringParameter(skill, paramName, defaultValue)
    local value = GetSkillParameter(skill, paramName, defaultValue)
    if type(value) == "string" then
        return value
    end
    return defaultValue
end

-- 安全地获取技能叙述文本，处理nil情况（修复 attempt to concatenate a nil value 错误）
function SafeGetSkillNarrative(context, skillId, triggerOrEnum)
    -- 尝试从context获取叙述文本
    local narrative = context:GetSkillNarrative(skillId, triggerOrEnum)
    
    -- 如果返回nil或空字符串，返回默认文本
    if narrative == nil or narrative == "" then
        return "[" .. tostring(skillId) .. "]"
    end
    return narrative
end

-- 基础技能函数
function entrance_skill_adjust_field(context, skill)
    local player = context.CurrentPlayer
    local char = context.CurrentCharacter

    -- 使用新的参数系统获取数值
    local frontPowerBonus = GetSkillIntParameter(skill, "FrontPowerBonus", 0)
    local middleWealthBonus = GetSkillIntParameter(skill, "MiddleWealthBonus", 0)
    local backFameBonus = GetSkillIntParameter(skill, "BackFameBonus", 0)

    -- 调整场地三维
    player.TotalPower = player.TotalPower + frontPowerBonus
    player.TotalWealth = player.TotalWealth + middleWealthBonus
    player.TotalFame = player.TotalFame + backFameBonus

    -- 发送精简叙述：先显示叙述文本，然后追加简洁的数值修正
    local narrative = SafeGetSkillNarrative(context, skill.SkillId, skill.Trigger)
    local effect = string.format("(P:+%d,W:+%d,F:+%d)", frontPowerBonus, middleWealthBonus, backFameBonus)
    context:LogMessage(narrative .. " " .. effect)
end

function field_power_drain(context, skill)
    if context:GetRandomInt(1, 10) <= 3 then
        local beforePower = context.CurrentPlayer.TotalPower
        local beforeOpponentPower = context.OpponentPlayer.TotalPower
        context.CurrentPlayer.TotalPower = context.CurrentPlayer.TotalPower + 1
        context.OpponentPlayer.TotalPower = math.max(0, context.OpponentPlayer.TotalPower - 1)
        local afterPower = context.CurrentPlayer.TotalPower
        local afterOpponentPower = context.OpponentPlayer.TotalPower

        -- 简化输出：叙述 + 简短效果
        local narrative = SafeGetSkillNarrative(context, "field_power_drain", "Field")
        local effect = "(P:+1,P:-1)"
        context:LogMessage(narrative .. " " .. effect)
    end
end

function chain_weather_boost(context, skill)
    if context.GameState.CurrentWeather == "Clear" then
        context.CurrentPlayer.TotalFame = context.CurrentPlayer.TotalFame + 2

        -- 简化输出：叙述 + 简短效果
        local narrative = SafeGetSkillNarrative(context, "chain_weather_boost", "Chain")
        local effect = "(F:+2)"
        context:LogMessage(narrative .. " " .. effect)
    end
end

function event_random_damage(context, skill)
    local beforePower = context.OpponentPlayer.TotalPower
    local damage = context:GetRandomInt(1, 3)
    context.OpponentPlayer.TotalPower = math.max(0, context.OpponentPlayer.TotalPower - damage)
    local afterPower = context.OpponentPlayer.TotalPower

    -- 简化输出：叙述 + 简短效果
    local narrative = SafeGetSkillNarrative(context, "event_random_damage", "Event")
    local effect = string.format("(P:-%d)", damage)
    context:LogMessage(narrative .. " " .. effect)
end

function innate_defense_skill(context, skill)
    local beforeWealth = context.CurrentPlayer.TotalWealth
    context.CurrentPlayer.TotalWealth = context.CurrentPlayer.TotalWealth + 2
    local afterWealth = context.CurrentPlayer.TotalWealth

    -- 简化输出：叙述 + 简短效果
    local narrative = SafeGetSkillNarrative(context, "innate_defense", "Field")
    local effect = "(W:+2)"
    context:LogMessage(narrative .. " " .. effect)
end

function chain_leader_bonus(context, skill)
    local hasOtherLeader = false
    for i, c in ipairs(context.CurrentPlayer.FieldCharacters) do
        if string.find(c.Name, "王") or string.find(c.Name, "皇") or string.find(c.Name, "主") then
            hasOtherLeader = true
            break
        end
    end

    if hasOtherLeader then
        context.CurrentPlayer.TotalPower = context.CurrentPlayer.TotalPower + 3

        -- 简化输出：叙述 + 简短效果
        local narrative = SafeGetSkillNarrative(context, "chain_leader_bonus", "Chain")
        local effect = "(P:+3)"
        context:LogMessage(narrative .. " " .. effect)
    end
end

-- 参数化伤害技能示例
function parametric_damage_skill(context, skill)
    local damage = GetSkillIntParameter(skill, "Damage", 1)
    local targetAttribute = GetSkillStringParameter(skill, "TargetAttribute", "TotalPower")
    local targetPlayer = GetSkillStringParameter(skill, "TargetPlayer", "opponent")
    
    local player = targetPlayer == "opponent" and context.OpponentPlayer or context.CurrentPlayer
    local beforeValue = player[targetAttribute]
    player[targetAttribute] = math.max(0, player[targetAttribute] - damage)
    local afterValue = player[targetAttribute]
    
    local effect = string.format("(%s:-%d)", targetAttribute, damage)
    local narrative = SafeGetSkillNarrative(context, skill.SkillId, "Immediate")
    -- 仅显示叙述文本与简短效果
    context:LogMessage(narrative .. " " .. effect)
end

-- 特殊卡技能函数

-- 暴走卷心菜技能：投掷d10，高于当前武力值时财力+10%，否则-10%
function cabbage_rampage_skill(context, skill)
    -- 获取当前玩家当前回合的武力值（作为目标值）
    local currentPower = context.CurrentPlayer.TotalPower or 0

    -- 投掷 D10，次数等于当前回合数（CurrentTurn），并累加
    local turnCount = context.GameState and context.GameState.CurrentTurn or 1
    if type(turnCount) ~= "number" or turnCount < 1 then turnCount = 1 end

    -- 使用工程内的 Dice.CalculateExpression，通过 context:RollDice 调用，获得完整的掷骰表达式与明细
    local expr = string.format("%dd10", turnCount)
    local dice = context:RollDice(expr)
    local sum = 0
    local detail = expr
    if dice ~= nil and type(dice.Total) == "number" then
        sum = dice.Total
        detail = dice.Detail or expr
    end

    local rollTextShort = string.format("%s", detail)

    -- 成功条件：合计小于当前武力值
    if sum < currentPower then
        local currentWealth = context.CurrentPlayer.TotalWealth
        local wealthBonus = math.floor(currentWealth * 0.1)
        context.CurrentPlayer.TotalWealth = context.CurrentPlayer.TotalWealth + wealthBonus

        local narrative = SafeGetSkillNarrative(context, skill.SkillId .. "_success", "Immediate")
        local effect = string.format("(W:+%d)", wealthBonus)
        -- 输出：叙述 + 简短掷骰摘要 + 简短效果
        context:LogMessage(narrative .. " " .. rollTextShort .. " " .. effect)
    else
        local currentWealth = context.CurrentPlayer.TotalWealth
        local wealthLoss = math.floor(currentWealth * 0.1)
        context.CurrentPlayer.TotalWealth = math.max(0, context.CurrentPlayer.TotalWealth - wealthLoss)

        local narrative = SafeGetSkillNarrative(context, skill.SkillId .. "_failure", "Immediate")
        local effect = string.format("(W:-%d)", wealthLoss)
        context:LogMessage(narrative .. " " .. rollTextShort .. " " .. effect)
    end
end

-- 阿克西斯教游行技能：增加当前玩家名声
function axis_parade_skill(context, skill)
    local turnCount = context.GameState and context.GameState.CurrentTurn or 1
    if type(turnCount) ~= "number" or turnCount < 1 then turnCount = 1 end

    -- 掷骰表达式支持格式化占位符（如"%dd6"），使用当前回合数填充
    local diceExprTemplate = GetSkillStringParameter(skill, "DiceExpr", "%dd6")
    local expr = diceExprTemplate
    local ok, formatted = pcall(string.format, diceExprTemplate, turnCount)
    if ok then expr = formatted end

    local dice = nil
    if context.RollDice ~= nil then
        dice = context:RollDice(expr)
    end

    local sum = 0
    local detail = expr
    if dice ~= nil then
        if type(dice.Total) == "number" then sum = dice.Total end
        if type(dice.Detail) == "string" then detail = dice.Detail end
    end

    local currentPower = context.CurrentPlayer.TotalPower or 0
    local success = sum > currentPower

    local powerDelta = success and GetSkillIntParameter(skill, "SuccessPowerDelta", -6)
        or GetSkillIntParameter(skill, "FailurePowerDelta", -12)
    local wealthDelta = success and GetSkillIntParameter(skill, "SuccessWealthDelta", 8)
        or GetSkillIntParameter(skill, "FailureWealthDelta", 8)
    local fameDelta = success and GetSkillIntParameter(skill, "SuccessFameDelta", 8)
        or GetSkillIntParameter(skill, "FailureFameDelta", 8)

    context.CurrentPlayer.TotalPower = math.max(0, (context.CurrentPlayer.TotalPower or 0) + powerDelta)
    context.CurrentPlayer.TotalWealth = math.max(0, (context.CurrentPlayer.TotalWealth or 0) + wealthDelta)
    context.CurrentPlayer.TotalFame = math.max(0, (context.CurrentPlayer.TotalFame or 0) + fameDelta)

    local narrativeKey = skill.SkillId .. (success and "_success" or "_failure")
    local narrative = SafeGetSkillNarrative(context, narrativeKey, "Immediate")
    local effect = string.format("(P:%+d,W:%+d,F:%+d)", powerDelta, wealthDelta, fameDelta)
    local rollTextShort = string.format("%s", detail or expr)
    context:LogMessage(narrative .. " " .. rollTextShort .. " " .. effect)
end

-- 技能定义表
SkillDefinitions = {
    -- 登场技能
    entrance_boost = {
        name = "登场增强",
        trigger = "Entrance",
        luaFunction = "entrance_skill_adjust_field",
        parameters = { FrontPowerBonus = 1, MiddleWealthBonus = 1, BackFameBonus = 0 }
    },

    king_entrance_buff = {
        name = "登基",
        trigger = "Entrance",
        luaFunction = "entrance_skill_adjust_field",
        parameters = { FrontPowerBonus = 5, MiddleWealthBonus = 5, BackFameBonus = 5 }
    },


    entrance_skill_adjust_field = {
        name = "登场-按场地调整（兼容）",
        trigger = "Entrance",
        luaFunction = "entrance_skill_adjust_field",
        parameters = { FrontPowerBonus = 0, MiddleWealthBonus = 0, BackFameBonus = 0 }
    },

    -- 法棍骑士的专有登场技能
    knight_bread_entrance = {
        name = "面包骑士的登场",
        trigger = "Entrance",
        luaFunction = "entrance_skill_adjust_field",
        parameters = { FrontPowerBonus = 2, MiddleWealthBonus = 0, BackFameBonus = 0 }
    },

    field_power_drain = {
        name = "力量汲取",
        trigger = "Field",
        luaFunction = "field_power_drain"
    },

    chain_weather_boost = {
        name = "天气增幅",
        trigger = "Chain",
        luaFunction = "chain_weather_boost"
    },

    event_random_damage = {
        name = "随机伤害",
        trigger = "Event",
        luaFunction = "event_random_damage"
    },

    innate_defense = {
        name = "防御加成",
        trigger = "Field",
        luaFunction = "innate_defense_skill"
    },

    chain_leader_bonus = {
        name = "领袖加成",
        trigger = "Chain",
        luaFunction = "chain_leader_bonus"
    },

    -- 参数化技能示例
    parametric_power_steal = {
        name = "力量窃取",
        trigger = "Event",
        luaFunction = "parametric_damage_skill",
        parameters = {
            Damage = 3,
            TargetAttribute = "TotalPower",
            TargetPlayer = "opponent"
        }
    },

    parametric_wealth_drain = {
        name = "财力汲取",
        trigger = "Field",
        luaFunction = "parametric_damage_skill",
        parameters = {
            Damage = 2,
            TargetAttribute = "TotalWealth",
            TargetPlayer = "opponent"
        }
    },

    parametric_fame_attack = {
        name = "名声攻击",
        trigger = "Event",
        luaFunction = "parametric_damage_skill",
        parameters = {
            Damage = 4,
            TargetAttribute = "TotalFame",
            TargetPlayer = "opponent"
        }
    },

    parametric_healing = {
        name = "回复技能",
        trigger = "Entrance",
        luaFunction = "parametric_damage_skill",
        parameters = {
            Damage = -2,
            TargetAttribute = "TotalPower",
            TargetPlayer = "self"
        }
    },

    -- 新增：特殊卡技能
    cabbage_rampage = {
        name = "暴走卷心菜",
        trigger = "Immediate",
        luaFunction = "cabbage_rampage_skill",
        parameters = {} -- 新的d10投掷逻辑不需要预设参数
    },
    
    axis_parade = {
        name = "阿克西斯教游行",
        trigger = "Immediate",
        luaFunction = "axis_parade_skill",
        parameters = {
            DiceExpr = "%dd6",
            SuccessPowerDelta = -6,
            SuccessWealthDelta = 8,
            SuccessFameDelta = 8,
            FailurePowerDelta = -12,
            FailureWealthDelta = 8,
            FailureFameDelta = 8
        }
    },
    
    -- 万圣节之夜技能：添加场地效果 + 手牌伤害
    halloween_night_field = {
        name = "万圣节之夜-场地",
        trigger = "Immediate",
        luaFunction = "halloween_night_field_skill",
        parameters = {
            FieldTag = "Halloween",
            Duration = 3
        }
    },
    
    halloween_night_damage = {
        name = "万圣节之夜-伤害",
        trigger = "Immediate",
        luaFunction = "halloween_night_damage_skill",
        parameters = {
            DamagePerCard = 2
        }
    },
    
    -- 通用场地效果技能（可以被其他卡复用）
    add_field_effect = {
        name = "添加场地效果",
        trigger = "Immediate",
        luaFunction = "add_field_effect_skill",
        parameters = {
            FieldTag = "Generic",
            Duration = 1
        }
    },

    field_harassment = {
        name = "骚扰",
        trigger = "Field",
        luaFunction = "field_harassment",
        parameters = {
            Probability = 30,
            OpponentPowerAdj = -1,
            OpponentWealthAdj = -1,
            OpponentFameAdj = -1
        }
    },

    field_eat_bread = {
        name = "啃面包",
        trigger = "Field",
        luaFunction = "field_eat_bread",
        parameters = {
            Probability = 40,
            SelfPowerAdj = 2,
            SelfWealthAdj = 0,
            SelfFameAdj = 0
        }
    },

    turn_end_fake_accounts = {
        name = "假账",
        trigger = "TurnEnd",
        luaFunction = "turn_end_fake_accounts",
        parameters = {
            Probability = 40,
            SuccessPowerAdj = 0,
            SuccessWealthAdj = 5,
            SuccessFameAdj = 0,
            FailPowerAdj = 0,
            FailWealthAdj = -2,
            FailFameAdj = 0
        }
    },

    persuasion = {
        name = "劝降",
        trigger = "Immediate",
        luaFunction = "persuasion_skill",
        parameters = {
            TargetField = 1,
            CommonSuccessRate = 100,
            RareSuccessRate = 80,
            EpicSuccessRate = 50,
            LegendarySuccessRate = 20
        }
    },

    bubble_wine = {
        name = "气泡酒",
        trigger = "Immediate",
        luaFunction = "bubble_wine_skill",
        parameters = {}
    },

    escape = {
        name = "逃跑",
        trigger = "Immediate",
        luaFunction = "escape_skill",
        parameters = {
            TargetField = 1
        }
    },

    deathrattle_summon_zombie = {
        name = "亡语：召唤僵尸",
        trigger = "Immediate",
        luaFunction = "deathrattle_summon_zombie",
        parameters = {}
    },

    goblin_scrap_equipment = {
        name = "拼凑装备",
        trigger = "TurnEnd",
        luaFunction = "goblin_scrap_equipment",
        parameters = {
            BaseTriggerRate = 10,
            HighPopulationThreshold = 5,
            HighPopulationTriggerRate = 50,
            LowPowerBonus = 5,
            HighPowerBonus = 8
        }
    },

    goblin_slayer = {
        name = "哥布林杀手",
        trigger = "TurnEnd",
        luaFunction = "goblin_slayer_skill",
        parameters = {}
    },

    giant_frog_entrance = {
        name = "巨型青蛙登场",
        trigger = "Entrance",
        luaFunction = "giant_frog_entrance",
        parameters = {
            MaxRemoveCount = 3,
            PowerPerRemoval = 2
        }
    },

    giant_frog_removal_wealth = {
        name = "巨型青蛙亡语",
        trigger = "Event",
        luaFunction = "giant_frog_removal_wealth",
        parameters = {
            WealthBonus = 5
        }
    },

    fledgling_adventurer_frenzied_charge = {
        name = "亢奋冲锋",
        trigger = "Field",
        luaFunction = "fledgling_adventurer_frenzied_charge",
        parameters = {
            TriggerProbability = 50,
            CommonSuccessRate = 100,
            RareSuccessRate = 80,
            EpicSuccessRate = 50,
            LegendarySuccessRate = 20,
            CommonPowerCost = 6,
            RarePowerCost = 4,
            EpicPowerCost = 8,
            LegendaryPowerCost = 10,
            FameMultiplier = 1.5,
            PowerDamageMultiplier = 1.5
        }
    },
}

-- 亢奋的新人冒险者技能：亢奋冲锋
function fledgling_adventurer_frenzied_charge(context, skill)
    local triggerProb = GetSkillIntParameter(skill, "TriggerProbability", 50)
    if context:GetRandomInt(1, 100) > triggerProb then
        return  -- 未触发
    end

    -- 收集所有包含"demon"或"beast"词条的角色（己方和对方）
    local allCharacters = {}
    local characterOwnerMap = {}  -- 记录每个角色属于哪一方

    -- 己方场地
    for fieldIdx = 1, 3 do
        local field = nil
        if fieldIdx == 1 then field = context.CurrentPlayer.FieldManager.FrontField
        elseif fieldIdx == 2 then field = context.CurrentPlayer.FieldManager.MiddleField
        else field = context.CurrentPlayer.FieldManager.BackField
        end
        
        if field and field.Characters then
            for _, character in ipairs(field.Characters) do
                if character ~= context.CurrentCharacter and character.Tags then
                    for _, tag in ipairs(character.Tags) do
                        if tag == "demon" or tag == "Demon" or tag == "beast" or tag == "Beast" then
                            if not table_find(allCharacters, character) then
                                table.insert(allCharacters, character)
                                characterOwnerMap[character] = "self"
                            end
                            break
                        end
                    end
                end
            end
        end
    end

    -- 对方场地
    for fieldIdx = 1, 3 do
        local field = nil
        if fieldIdx == 1 then field = context.OpponentPlayer.FieldManager.FrontField
        elseif fieldIdx == 2 then field = context.OpponentPlayer.FieldManager.MiddleField
        else field = context.OpponentPlayer.FieldManager.BackField
        end
        
        if field and field.Characters then
            for _, character in ipairs(field.Characters) do
                if character.Tags then
                    for _, tag in ipairs(character.Tags) do
                        if tag == "demon" or tag == "Demon" or tag == "beast" or tag == "Beast" then
                            if not table_find(allCharacters, character) then
                                table.insert(allCharacters, character)
                                characterOwnerMap[character] = "opponent"
                            end
                            break
                        end
                    end
                end
            end
        end
    end

    -- 如果找不到目标，空操作（无输出）
    if #allCharacters == 0 then
        return
    end

    -- 随机选择一个目标
    local targetCharacter = allCharacters[context:GetRandomInt(1, #allCharacters)]
    local targetName = targetCharacter.Name or "未知单位"
    local isAlly = characterOwnerMap[targetCharacter] == "self"
    
    -- 彩蛋特效：如果目标是"巨型青蛙"，特殊处理
    if targetName == "巨型青蛙" then
        local frogEasterEggPower = GetSkillIntParameter(skill, "FrogEasterEggPower", 5)
        context.CurrentPlayer.TotalPower = (context.CurrentPlayer.TotalPower or 0) + frogEasterEggPower
        context:RemoveCharacterFromCurrentPlayer(context.CurrentCharacter, 0)
        
        -- 获取彩蛋叙述
        local narrative = SafeGetSkillNarrative(context, skill.SkillId .. "_frog_easter_egg", "Field")
        local effect = string.format("(P:+%d)", frogEasterEggPower)
        context:LogMessage(narrative .. " " .. effect)
        return
    end
    
    -- 计算成功率
    local rarity = targetCharacter.Rarity or "Common"
    local successRate = 100
    if rarity == "Common" then
        successRate = GetSkillIntParameter(skill, "CommonSuccessRate", 100)
    elseif rarity == "Rare" then
        successRate = GetSkillIntParameter(skill, "RareSuccessRate", 80)
    elseif rarity == "Epic" then
        successRate = GetSkillIntParameter(skill, "EpicSuccessRate", 50)
    elseif rarity == "Legendary" then
        successRate = GetSkillIntParameter(skill, "LegendarySuccessRate", 20)
    end

    -- 判定移除是否成功
    local roll = context:GetRandomInt(1, 100)
    local success = roll <= successRate

    if success then
        -- 计算成本（根据稀有度）
        local powerCost = 0
        if rarity == "Common" then
            powerCost = GetSkillIntParameter(skill, "CommonPowerCost", 6)
        elseif rarity == "Rare" then
            powerCost = GetSkillIntParameter(skill, "RarePowerCost", 4)
        elseif rarity == "Epic" then
            powerCost = GetSkillIntParameter(skill, "EpicPowerCost", 8)
        elseif rarity == "Legendary" then
            powerCost = GetSkillIntParameter(skill, "LegendaryPowerCost", 10)
        end

        local fameMultiplier = GetSkillFloatParameter(skill, "FameMultiplier", 1.5)
        local powerDamageMultiplier = GetSkillFloatParameter(skill, "PowerDamageMultiplier", 1.5)

        -- 扣除己方对应稀有度的 power
        context.CurrentPlayer.TotalPower = math.max(0, (context.CurrentPlayer.TotalPower or 0) - powerCost)

        if isAlly then
            -- 友方目标：添加对应数值 1.5 倍的 Fame
            local fameBenefit = math.floor(powerCost * fameMultiplier)
            context.CurrentPlayer.TotalFame = (context.CurrentPlayer.TotalFame or 0) + fameBenefit
            
            -- 移除己方单位
            context:RemoveCharacterFromCurrentPlayer(targetCharacter, 0)
            
            -- 获取叙述并替换 {0} 为目标名称
            local baseNarrative = SafeGetSkillNarrative(context, skill.SkillId .. "_success_ally", "Field")
            local narrative = baseNarrative:gsub("{0}", targetName)
            local effect = string.format("(F:+%d, P:-%d)", fameBenefit, powerCost)
            context:LogMessage(narrative .. " " .. effect)
        else
            -- 敌方目标：扣除其对应数值 1.5 倍的 Power
            local powerDamage = math.floor(powerCost * powerDamageMultiplier)
            context.OpponentPlayer.TotalPower = math.max(0, (context.OpponentPlayer.TotalPower or 0) - powerDamage)
            
            -- 移除对方单位
            context:RemoveCharacterFromOpponent(targetCharacter, 0)
            
            -- 获取叙述并替换 {0} 为目标名称
            local baseNarrative = SafeGetSkillNarrative(context, skill.SkillId .. "_success_opponent", "Field")
            local narrative = baseNarrative:gsub("{0}", targetName)
            local effect = string.format("(Opponent P:-%d, Self P:-%d)", powerDamage, powerCost)
            context:LogMessage(narrative .. " " .. effect)
        end
    else
        -- 移除尝试失败，自己被移除，无亡语
        context:RemoveCharacterFromCurrentPlayer(context.CurrentCharacter, 0)
        
        -- 获取叙述并替换 {0} 为目标名称
        local baseNarrative = SafeGetSkillNarrative(context, skill.SkillId .. "_failure", "Field")
        local narrative = baseNarrative:gsub("{0}", targetName)
        local effect = string.format("[命中率失败: %d%% < %d%%]", roll, successRate)
        context:LogMessage(narrative .. " " .. effect)
    end
end

-- 辅助函数：检查表中是否存在元素
function table_find(tbl, value)
    for _, v in ipairs(tbl) do
        if v == value then return true end
    end
    return false
end

-- 哥布林杀手技能：回合结束时从对方场上移除一只带有goblin tag的单位
-- 辅助函数：根据角色找到其所在的场地编号 (1=Front, 2=Middle, 3=Back)
function GetCharacterFieldNum(fieldManager, character)
    if fieldManager.FrontField and fieldManager.FrontField.Characters then
        for _, char in ipairs(fieldManager.FrontField.Characters) do
            if char == character then return 1 end
        end
    end
    
    if fieldManager.MiddleField and fieldManager.MiddleField.Characters then
        for _, char in ipairs(fieldManager.MiddleField.Characters) do
            if char == character then return 2 end
        end
    end
    
    if fieldManager.BackField and fieldManager.BackField.Characters then
        for _, char in ipairs(fieldManager.BackField.Characters) do
            if char == character then return 3 end
        end
    end
    
    return 0  -- 未找到（不应该发生）
end

function goblin_slayer_skill(context, skill)
    -- 按优先级搜索对方场地上带有goblin tag的角色（优先前场）
    local targetCharacter = nil
    local targetFieldNum = 0
    
    -- 优先搜索前场
    local frontCharacters = context.OpponentPlayer.FieldManager.FrontField.Characters
    if frontCharacters ~= nil and #frontCharacters > 0 then
        for _, character in ipairs(frontCharacters) do
            if character ~= nil and character.Tags ~= nil then
                for _, tag in ipairs(character.Tags) do
                    if tag == "goblin" or tag == "Goblin" then
                        targetCharacter = character
                        targetFieldNum = 1
                        break
                    end
                end
                if targetCharacter ~= nil then break end
            end
        end
    end
    
    -- 如果前场没找到，搜索中场
    if targetCharacter == nil then
        local middleCharacters = context.OpponentPlayer.FieldManager.MiddleField.Characters
        if middleCharacters ~= nil and #middleCharacters > 0 then
            for _, character in ipairs(middleCharacters) do
                if character ~= nil and character.Tags ~= nil then
                    for _, tag in ipairs(character.Tags) do
                        if tag == "goblin" or tag == "Goblin" then
                            targetCharacter = character
                            targetFieldNum = 2
                            break
                        end
                    end
                    if targetCharacter ~= nil then break end
                end
            end
        end
    end
    
    -- 如果中场没找到，搜索后场
    if targetCharacter == nil then
        local backCharacters = context.OpponentPlayer.FieldManager.BackField.Characters
        if backCharacters ~= nil and #backCharacters > 0 then
            for _, character in ipairs(backCharacters) do
                if character ~= nil and character.Tags ~= nil then
                    for _, tag in ipairs(character.Tags) do
                        if tag == "goblin" or tag == "Goblin" then
                            targetCharacter = character
                            targetFieldNum = 3
                            break
                        end
                    end
                    if targetCharacter ~= nil then break end
                end
            end
        end
    end
    
    -- 如果找到了目标，移除它并添加奖励
    if targetCharacter ~= nil then
        context:RemoveCharacterFromOpponent(targetCharacter, targetFieldNum)
        -- 为我方添加5点名声，扣除1点财力
        context.CurrentPlayer.TotalFame = (context.CurrentPlayer.TotalFame or 0) + 5
        context.CurrentPlayer.TotalWealth = math.max(0, (context.CurrentPlayer.TotalWealth or 0) - 1)
        
        local narrative = SafeGetSkillNarrative(context, skill.SkillId, "TurnEnd")
        local effect = string.format("(F:+5,W:-1) [移除] %s", targetCharacter.Name)
        context:LogMessage(narrative .. " " .. effect)
    else
        -- 未找到目标，移除自己并扣除4点power
        -- 首先找到自己所在的场地
        local selfFieldNum = GetCharacterFieldNum(context.CurrentPlayer.FieldManager, context.CurrentCharacter)
        if selfFieldNum > 0 then
            context:RemoveCharacterFromCurrentPlayer(context.CurrentCharacter, selfFieldNum)
        else
            -- 防守：如果找不到自己的位置，通过枚举所有场地尝试移除
            context:RemoveCharacterFromCurrentPlayer(context.CurrentCharacter, 2)  -- 尝试从中场移除
        end
        
        context.CurrentPlayer.TotalPower = math.max(0, (context.CurrentPlayer.TotalPower or 0) - 4)
        
        local narrative = SafeGetSkillNarrative(context, skill.SkillId .. "_fail", "TurnEnd")
        local effect = string.format("(P:-4) [未找到目标，自我移除]")
        context:LogMessage(narrative .. " " .. effect)
    end
end

-- 创建技能实例的辅助函数
function CreateSkillInstance(skillId)
    local def = SkillDefinitions[skillId]
    if not def then
        return nil
    end

    local skill = {
        SkillId = skillId,
        Name = def.name,
        Trigger = def.trigger,
        LuaFunctionName = def.luaFunction,
        Parameters = def.parameters or {}
    }

    return skill
end

-- 骚扰：每回合概率发动，对对方造成三维调整
function field_harassment(context, skill)
    local probability = GetSkillIntParameter(skill, "Probability", 30)
    if context:GetRandomInt(1, 100) <= probability then
        local powerAdj = GetSkillIntParameter(skill, "OpponentPowerAdj", 0)
        local wealthAdj = GetSkillIntParameter(skill, "OpponentWealthAdj", 0)
        local fameAdj = GetSkillIntParameter(skill, "OpponentFameAdj", 0)

        context.OpponentPlayer.TotalPower = context.OpponentPlayer.TotalPower + powerAdj
        context.OpponentPlayer.TotalWealth = context.OpponentPlayer.TotalWealth + wealthAdj
        context.OpponentPlayer.TotalFame = context.OpponentPlayer.TotalFame + fameAdj

        local narrative = SafeGetSkillNarrative(context, skill.SkillId, "Field")
        local effect = string.format("(Opponent P:%+d, W:%+d, F:%+d)", powerAdj, wealthAdj, fameAdj)
        context:LogMessage(narrative .. " " .. effect)
    end
end

-- 啃面包：每回合概率发动，为己方添加数值
function field_eat_bread(context, skill)
    local probability = GetSkillIntParameter(skill, "Probability", 30)
    if context:GetRandomInt(1, 100) <= probability then
        local powerAdj = GetSkillIntParameter(skill, "SelfPowerAdj", 0)
        local wealthAdj = GetSkillIntParameter(skill, "SelfWealthAdj", 0)
        local fameAdj = GetSkillIntParameter(skill, "SelfFameAdj", 0)

        context.CurrentPlayer.TotalPower = context.CurrentPlayer.TotalPower + powerAdj
        context.CurrentPlayer.TotalWealth = context.CurrentPlayer.TotalWealth + wealthAdj
        context.CurrentPlayer.TotalFame = context.CurrentPlayer.TotalFame + fameAdj

        local narrative = SafeGetSkillNarrative(context, skill.SkillId, "Field")
        local effect = string.format("(Self P:%+d, W:%+d, F:%+d)", powerAdj, wealthAdj, fameAdj)
        context:LogMessage(narrative .. " " .. effect)
    end
end

-- 假账：每回合末发动，进行名声鉴定（概率触发）
function turn_end_fake_accounts(context, skill)
    local probability = GetSkillIntParameter(skill, "Probability", 40)
    if context:GetRandomInt(1, 100) > probability then
        return  -- 未触发，直接返回
    end

    local currentTurn = context.GameState.CurrentTurn
    local d3 = context:GetRandomInt(1, 3)
    local target = currentTurn * d3
    local currentFame = context.CurrentPlayer.TotalFame

    if currentFame > target then
        -- Success
        local powerAdj = GetSkillIntParameter(skill, "SuccessPowerAdj", 0)
        local wealthAdj = GetSkillIntParameter(skill, "SuccessWealthAdj", 0)
        local fameAdj = GetSkillIntParameter(skill, "SuccessFameAdj", 0)

        context.CurrentPlayer.TotalPower = context.CurrentPlayer.TotalPower + powerAdj
        context.CurrentPlayer.TotalWealth = context.CurrentPlayer.TotalWealth + wealthAdj
        context.CurrentPlayer.TotalFame = context.CurrentPlayer.TotalFame + fameAdj

        context:LogMessage("会计师拨着算盘，他本就不怎么看得清。本年度的粮食收入被他多报了一个零。所有人皆大欢喜。 (W:+" .. wealthAdj .. ")")
    elseif currentFame <= target then
        -- Fail
        local powerAdj = GetSkillIntParameter(skill, "FailPowerAdj", 0)
        local wealthAdj = GetSkillIntParameter(skill, "FailWealthAdj", 0)
        local fameAdj = GetSkillIntParameter(skill, "FailFameAdj", 0)

        context.CurrentPlayer.TotalPower = context.CurrentPlayer.TotalPower + powerAdj
        context.CurrentPlayer.TotalWealth = context.CurrentPlayer.TotalWealth + wealthAdj
        context.CurrentPlayer.TotalFame = context.CurrentPlayer.TotalFame + fameAdj

        context:LogMessage("税务官涂写的3被识成了8，你开始忧虑今年要有多少破产的资本家。 (W:" .. wealthAdj .. ")")
    end
end

-- 亡语：召唤僵尸 (Placeholder)
function deathrattle_summon_zombie(context, skill)
    -- Suppress message if not implemented
    -- context:LogMessage("亡语触发：召唤僵尸 (尚未实现)")
end

-- 巨型青蛙登场技能：移除己方登场场地中的Common或Rare角色，腾出位置登场，增加战力
-- 设计意图：无论如何都会检索至多3个common或rare的角色移除，此优先于场地人数判断
-- 改进：使用 context.AssignedFieldType 获取指令指定的登场场地，若不可用则回退到 FieldPreference
function giant_frog_entrance(context, skill)
    local maxRemoveCount = GetSkillIntParameter(skill, "MaxRemoveCount", 3)
    local powerPerRemoval = GetSkillIntParameter(skill, "PowerPerRemoval", 2)
    
    local removedCharacters = {}
    local removedCount = 0
    
    -- 获取Giant Frog实际登场的目标场地
    if context.CurrentPlayer and context.CurrentPlayer.FieldManager and context.CurrentCharacter then
        local fieldManager = context.CurrentPlayer.FieldManager
        local targetField = nil
        local fieldNum = 0
        local fieldName = "未知"
        
        -- 优先使用 AssignedFieldType（指令指定的场地），若不可用则回退到 FieldPreference
        if context.AssignedFieldType then
            -- 使用指令指定的场地
            if context.AssignedFieldType == 1 then
                targetField = fieldManager.FrontField
                fieldNum = 1
                fieldName = "前场"
            elseif context.AssignedFieldType == 2 then
                targetField = fieldManager.MiddleField
                fieldNum = 2
                fieldName = "中场"
            elseif context.AssignedFieldType == 3 then
                targetField = fieldManager.BackField
                fieldNum = 3
                fieldName = "后场"
            end
        else
            -- 回退：使用 FieldPreference
            if context.CurrentCharacter.FieldPreference == "Front" then
                targetField = fieldManager.FrontField
                fieldNum = 1
                fieldName = "前场"
            elseif context.CurrentCharacter.FieldPreference == "Back" then
                targetField = fieldManager.BackField
                fieldNum = 3
                fieldName = "后场"
            else
                targetField = fieldManager.MiddleField
                fieldNum = 2
                fieldName = "中场"
            end
        end
        
        -- 在目标场地中搜索并移除 Common 或 Rare 的角色
        if targetField and targetField.Characters then
            -- 建立待移除列表（不直接修改迭代中的列表）
            local toRemove = {}
            for idx, character in ipairs(targetField.Characters) do
                -- 跳过自己（防止青蛙移除自己）
                if character == context.CurrentCharacter then
                    -- skip self
                else
                    -- Rarity 是数值 enum: 0=Common, 1=Rare, 2=Epic, 3=Legendary
                    local rarityValue = character.Rarity
                    
                    -- 检查稀有度，移除 Common(0) 或 Rare(1)
                    if removedCount < maxRemoveCount then
                        if rarityValue == 0 or rarityValue == 1 then
                            table.insert(toRemove, {idx = idx, char = character, fieldNum = fieldNum})
                            removedCount = removedCount + 1
                        end
                    end
                end
            end
            
            -- 执行移除（按倒序，避免索引错位）
            for i = #toRemove, 1, -1 do
                local target = toRemove[i]
                context:RemoveCharacterFromCurrentPlayer(target.char, target.fieldNum)
                table.insert(removedCharacters, target.char.Name)
            end
        end
    end
    
    -- 计算增加的战力
    local powerGain = removedCount * powerPerRemoval
    if powerGain > 0 then
        context.CurrentPlayer.TotalPower = (context.CurrentPlayer.TotalPower or 0) + powerGain
    end
    
    -- 记录日志
    local narrative = SafeGetSkillNarrative(context, skill.SkillId, "Entrance")
    local effect
    if removedCount > 0 then
        local targetNames = table.concat(removedCharacters, "、")
        effect = string.format("(腾位:%s, 获得P:+%d)", targetNames, powerGain)
    else
        effect = "(场地无可移除单位，直接登场)"
    end
end

-- 巨型青蛙移除事件：角色被移除时为己方追加财力
-- 注：此技能应在角色被移除时触发，需要在C#端实现OnRemoved委托
function giant_frog_removal_wealth(context, skill)
    local wealthBonus = GetSkillIntParameter(skill, "WealthBonus", 5)
    context.CurrentPlayer.TotalWealth = (context.CurrentPlayer.TotalWealth or 0) + wealthBonus
    
    local narrative = SafeGetSkillNarrative(context, skill.SkillId, "Immediate")
    local effect = string.format("(财力+%d)", wealthBonus)
    context:LogMessage(narrative .. " " .. effect)
end

-- 战术性骚扰：对手财力 -5，使用方战力 +8，并抽取一张手牌
function tactical_harassment_skill(context, skill)
    -- 目标：对手财力减少5
    local beforeOpponentWealth = context.OpponentPlayer.TotalWealth or 0
    context.OpponentPlayer.TotalWealth = math.max(0, context.OpponentPlayer.TotalWealth - 5)

    -- 使用方战力增加8
    context.CurrentPlayer.TotalPower = (context.CurrentPlayer.TotalPower or 0) + 8

    -- 抽一张牌到使用方手牌（通过SkillExecutionContext实现）
    local drawn = nil
    if context.DrawOneCardToCurrentPlayer ~= nil then
        drawn = context:DrawOneCardToCurrentPlayer()
    end

    -- 组合简短效果文本
    local effect = string.format("(Opponent W:-5, Self P:+8)%s", drawn and (" Draw:" .. (drawn.Name or "?")) or "")

    local narrative = SafeGetSkillNarrative(context, skill.SkillId, "Immediate")
    context:LogMessage(narrative .. " " .. effect)
end

-- 通用场地效果添加技能：为当前回合添加指定标签的场地效果，持续指定回合数
-- 参数：FieldTag (字符串，场地标签名), Intensity (整数1-3，强度，默认3), Duration (整数，持续回合数)
function add_field_effect_skill(context, skill)
    local fieldTag = GetSkillStringParameter(skill, "FieldTag", "")
    local intensity = GetSkillIntParameter(skill, "Intensity", 3)
    local duration = GetSkillIntParameter(skill, "Duration", 1)
    
    if fieldTag == "" then
        context:LogMessage("[场地效果] 场地标签不能为空！")
        return
    end
    
    -- 调用C#接口方法设置场地效果
    local success = context:SetFieldEffect(fieldTag, intensity, duration)
    
    if success then
        local narrative = SafeGetSkillNarrative(context, skill.SkillId, "Immediate")
        local effect = string.format("(场地效果已激活：'%s'，强度%d，持续%d回合)", fieldTag, intensity, duration)
        context:LogMessage(narrative .. " " .. effect)
    else
        local narrative = SafeGetSkillNarrative(context, skill.SkillId, "Immediate")
        context:LogMessage(narrative .. " (场地效果设置失败：存在强度更低的效果)")
    end
end

-- 万圣节之夜技能1：添加万圣节场地效果
function halloween_night_field_skill(context, skill)
    -- 使用通用场地效果技能
    add_field_effect_skill(context, skill)
end

-- 万圣节之夜技能2：手中每有一张自己阵营的角色卡，就对对方武力值造成2点损害
function halloween_night_damage_skill(context, skill)
    local damagePerCard = GetSkillIntParameter(skill, "DamagePerCard", 2)
    
    -- 获取当前玩家手牌中自己阵营的角色卡数量
    local handCards = context.CurrentPlayer.HandCards or {}
    local factionCount = 0
    
    -- 遍历手牌统计符合阵营的角色卡
    for i = 1, #handCards do
        local card = handCards[i]
        if card ~= nil then
            -- 检查是否为角色卡并且属于当前玩家阵营
            -- 由于Lua中无法直接判断C#对象类型，通过检查Character字段
            if card.Character ~= nil then
                -- 检查阵营是否匹配（简化判断，假设角色卡的阵营与玩家一致）
                factionCount = factionCount + 1
            end
        end
    end
    
    -- 计算总伤害
    local totalDamage = factionCount * damagePerCard
    
    -- 对对手武力造成伤害
    if totalDamage > 0 then
        context.OpponentPlayer.TotalPower = math.max(0, (context.OpponentPlayer.TotalPower or 0) - totalDamage)
    end
    
    local narrative = SafeGetSkillNarrative(context, skill.SkillId, "Immediate")
    local effect = string.format("(Cards:%d, Opponent P:-%d)", factionCount, totalDamage)
    context:LogMessage(narrative .. " " .. effect)
end

-- 劝降技能：从对方指定场地随机移除一个角色
function persuasion_skill(context, skill)
    -- 获取目标场地
    local targetFieldNum = GetSkillIntParameter(skill, "TargetField", 1)
    local targetField = nil
    local fieldName = ""
    
    if targetFieldNum == 1 then
        targetField = context.OpponentPlayer.FieldManager.FrontField
        fieldName = "前场"
    elseif targetFieldNum == 2 then
        targetField = context.OpponentPlayer.FieldManager.MiddleField
        fieldName = "中场"
    elseif targetFieldNum == 3 then
        targetField = context.OpponentPlayer.FieldManager.BackField
        fieldName = "后场"
    else
        context:LogMessage("[劝降] 无效的场地编号")
        return
    end
    
    -- 获取该场地的所有角色
    local characters = targetField.Characters
    if characters == nil or #characters == 0 then
        context:LogMessage("[劝降] " .. fieldName .. "没有角色存在！卡牌消耗但效果未生效。")
        return
    end
    
    -- 随机选择一个角色
    local randomIndex = context:GetRandomInt(1, #characters)
    local targetCharacter = characters[randomIndex]
    
    if targetCharacter == nil then
        context:LogMessage("[劝降] 无法获取目标角色")
        return
    end
    
    -- 根据稀有度计算成功概率
    local successRate = 100
    local rarity = targetCharacter.Rarity
    
    if rarity == "Common" then
        successRate = GetSkillIntParameter(skill, "CommonSuccessRate", 100)
    elseif rarity == "Rare" then
        successRate = GetSkillIntParameter(skill, "RareSuccessRate", 80)
    elseif rarity == "Epic" then
        successRate = GetSkillIntParameter(skill, "EpicSuccessRate", 50)
    elseif rarity == "Legendary" then
        successRate = GetSkillIntParameter(skill, "LegendarySuccessRate", 20)
    elseif rarity == "Named" then
        successRate = GetSkillIntParameter(skill, "NamedSuccessRate", 0)
    end
    
    -- 掷骰判定
    local roll = context:GetRandomInt(1, 100)
    local success = roll <= successRate
    
    if success then
        -- 成功：移除角色
        context:RemoveCharacterFromOpponent(targetCharacter, targetFieldNum)
        local narrative = SafeGetSkillNarrative(context, skill.SkillId, "Immediate")
        context:LogMessage(narrative .. " [成功] 移除了对方" .. fieldName .. "的《" .. targetCharacter.Name .. "》")
    else
        -- 失败：仅输出消息
        context:LogMessage("[劝降] 对方" .. fieldName .. "的《" .. targetCharacter.Name .. "》拒绝了你的劝降！(掷骰: " .. roll .. " > " .. successRate .. ")")
    end
end

-- 气泡酒技能：消耗2D10财力，再抽两张卡
function bubble_wine_skill(context, skill)
    -- 掷骰 2D10
    local dice = context:RollDice("2d10")
    local cost = dice.Total or 0
    
    -- 消耗财力
    local currentWealth = context.CurrentPlayer.TotalWealth or 0
    if cost > currentWealth then
        context:LogMessage("[气泡酒] 财力不足！需要" .. cost .. "点，但你只有" .. currentWealth .. "点。")
        return
    end
    
    context.CurrentPlayer.TotalWealth = currentWealth - cost
    
    -- 抽两张卡
    local card1 = context:DrawOneCardToCurrentPlayer()
    local card2 = context:DrawOneCardToCurrentPlayer()
    
    local narrative = SafeGetSkillNarrative(context, skill.SkillId, "Immediate")
    local cardInfo = ""
    if card1 ~= nil then cardInfo = cardInfo .. card1.Name end
    if card2 ~= nil then 
        if cardInfo ~= "" then cardInfo = cardInfo .. "、" end
        cardInfo = cardInfo .. card2.Name 
    end
    
    context:LogMessage(narrative .. " (掷骰: " .. (dice.Detail or "2d10") .. " = " .. cost .. ") [抽取] " .. cardInfo)
end

-- 逃跑技能：消耗D10名声，指定自己的场地并移除一个角色，再抽一张角色卡
function escape_skill(context, skill)
    -- 掷骰 D10
    local dice = context:RollDice("d10")
    local cost = dice.Total or 0
    
    -- 消耗名声
    local currentFame = context.CurrentPlayer.TotalFame or 0
    if cost > currentFame then
        context:LogMessage("[逃跑] 名声不足！需要" .. cost .. "点，但你只有" .. currentFame .. "点。")
        return
    end
    
    context.CurrentPlayer.TotalFame = currentFame - cost
    
    -- 获取目标场地（参数中应该有TargetField）
    local targetFieldNum = GetSkillIntParameter(skill, "TargetField", 1)
    local targetField = nil
    local fieldName = ""
    
    if targetFieldNum == 1 then
        targetField = context.CurrentPlayer.FieldManager.FrontField
        fieldName = "前场"
    elseif targetFieldNum == 2 then
        targetField = context.CurrentPlayer.FieldManager.MiddleField
        fieldName = "中场"
    elseif targetFieldNum == 3 then
        targetField = context.CurrentPlayer.FieldManager.BackField
        fieldName = "后场"
    else
        context:LogMessage("[逃跑] 无效的场地编号")
        return
    end
    
    -- 获取该场地的所有角色
    local characters = targetField.Characters
    if characters == nil or #characters == 0 then
        context:LogMessage("[逃跑] 你的" .. fieldName .. "没有角色！")
        return
    end
    
    -- 随机选择一个角色
    local randomIndex = context:GetRandomInt(1, #characters)
    local targetCharacter = characters[randomIndex]
    
    if targetCharacter == nil then
        context:LogMessage("[逃跑] 无法获取目标角色")
        return
    end
    
    -- 从本队场地移除角色（使用C#接口方法，确保正确处理）
    context:RemoveCharacterFromCurrentPlayer(targetCharacter, targetFieldNum)
    
    -- 抽一张角色卡
    local newCard = context:DrawOneCardToCurrentPlayer()
    
    local narrative = SafeGetSkillNarrative(context, skill.SkillId, "Immediate")
    local newCardName = newCard ~= nil and newCard.Name or "未知卡"
    context:LogMessage(narrative .. " (掷骰: " .. (dice.Detail or "d10") .. " = " .. cost .. " 名声) [移除] " .. targetCharacter.Name .. " [招募] " .. newCardName)
end

-- 哥布林拼凑装备技能：在回合末检定，如果触发则所有哥布林都获得power加成
function goblin_scrap_equipment(context, skill)
    -- 获取基础参数
    local baseTriggerRate = GetSkillIntParameter(skill, "BaseTriggerRate", 10)
    local highPopulationThreshold = GetSkillIntParameter(skill, "HighPopulationThreshold", 5)
    local highPopulationTriggerRate = GetSkillIntParameter(skill, "HighPopulationTriggerRate", 50)
    local lowPowerBonus = GetSkillIntParameter(skill, "LowPowerBonus", 5)
    local highPowerBonus = GetSkillIntParameter(skill, "HighPowerBonus", 8)
    local lowPopulationNarrative = GetSkillStringParameter(skill, "LowPopulationNarrative", "")
    local highPopulationNarrative = GetSkillStringParameter(skill, "HighPopulationNarrative", "")
    
    -- 计算处于场地上的所有哥布林数量
    local goblinCount = 0
    local goblinCharacters = {}
    
    for _, character in ipairs(context.CurrentPlayer.FieldCharacters) do
        if character ~= nil and character.Name == "哥布林" then
            goblinCount = goblinCount + 1
            table.insert(goblinCharacters, character)
        end
    end
    
    -- 如果没有哥布林在场，技能无效
    if goblinCount == 0 then
        return
    end
    
    -- 根据哥布林数量决定触发概率，只检查一次
    local triggerRate = baseTriggerRate
    if goblinCount > highPopulationThreshold then
        triggerRate = highPopulationTriggerRate
    end
    
    -- 掷骰判定是否触发
    local roll = context:GetRandomInt(1, 100)
    if roll > triggerRate then
        return  -- 未触发，技能无效
    end
    
    -- 触发：效果只生效一次
    local powerBonus = lowPowerBonus
    if goblinCount > highPopulationThreshold then
        powerBonus = highPowerBonus
    end
    
    -- 只给第一个哥布林应用加成（技能只生效一次）
    if #goblinCharacters > 0 then
        goblinCharacters[1].Power = (goblinCharacters[1].Power or 0) + powerBonus
    end
    
    -- 输出日志，根据人口数量选择不同的描述
    local narrative = goblinCount > highPopulationThreshold and highPopulationNarrative or lowPopulationNarrative
    local messages = narrative .. " [数量:" .. goblinCount .. "][Power+" .. powerBonus .. "]"
    context:LogMessage(messages)
end