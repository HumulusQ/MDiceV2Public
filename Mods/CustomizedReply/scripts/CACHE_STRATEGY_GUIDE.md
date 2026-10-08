# Feedpet 脚本缓存策略实现指南

## 📋 修改概览

在 feedpet.lua 中将 `initial()` 和 `dispose()` 函数改为完整演示**全局变量表（table）缓存策略**，展示了如何有效地加载和保存好感度数据。

## 🔄 修改前后对比

### 修改前（简化版）

```lua
function initial()
    _G.affinity_cache = {}
    _G.cache_dirty = false
    _script_registry["feedpet"] = {...}
end

function dispose()
    if _G.affinity_cache and _G.cache_dirty then
        for uid, affinity_value in pairs(_G.affinity_cache) do
            mod_storage_write("cache_" .. uid, tostring(affinity_value))
        end
    end
    _G.affinity_cache = nil
    _G.cache_dirty = nil
end
```

### 修改后（完整版）

#### initial() 函数增强

**新增部分：**

1. **详细的缓存表结构注释**
   - 展示缓存表的完整数据结构
   - 说明键值对的含义
   - 提供可视化的例子

2. **从持久化存储加载数据（演示）**
   - 伪代码演示如何从 Mod Storage 加载已有数据
   - 说明如何处理缓存热启动
   - 讨论冷加载 vs 热加载的权衡

3. **初始化脚本元数据表**
   ```lua
   _G.script_metadata = {
       version = "1.0.0",
       initialized_at = os.time(),
       total_users_cached = 0,
       last_save_time = 0
   }
   ```
   - 记录脚本版本
   - 追踪初始化时间
   - 统计缓存用户数
   - 记录最后保存时间

4. **扩展 API 导出**
   ```lua
   _script_registry["feedpet"] = {
       --...
       get_cache = function() return _G.affinity_cache end,
       get_cache_dirty = function() return _G.cache_dirty end
   }
   ```
   - 导出缓存访问接口
   - 允许外部脚本查看缓存状态

#### dispose() 函数增强

**新增部分：**

1. **详细的保存过程说明**
   - 第一步：检查缓存脏标志
   - 第二步：遍历所有用户数据
   - 第三步：逐个保存到 Mod Storage
   - 说明每个步骤的目的和策略

2. **数据验证演示**
   ```lua
   -- 验证保存（可选）
   local verify_value = mod_storage_read(store_key)
   if verify_value ~= store_value then
       print(string.format("[feedpet] ⚠ Save verification failed for user %s", uid))
   end
   ```
   - 演示如何验证保存结果
   - 提供调试便利

3. **完整的清理流程**
   - 方法1：逐个删除表元素
   - 方法2：将表设为 nil
   - 清理脏标志
   - 清理元数据表

4. **其他资源清理的接口**
   - 注释说明可能需要清理的资源
   - 文件句柄、数据库连接等
   - 展示生产级代码的最佳实践

5. **容错处理示例**
   ```lua
   local success, error_msg = pcall(function()
       -- 执行可能失败的操作
   end)
   ```
   - 展示如何使用 pcall 包装关键操作
   - 提供错误处理的实践

## 🎯 核心概念演示

### 1. 缓存策略（Cache Pattern）

**设计目标：**
- 减少 Mod Storage 的频繁访问
- 提高查询性能
- 通过批量保存优化 IO

**实现方式：**
```
内存缓存（热数据） <---> Mod Storage（冷数据）
     ↓                           ↑
  MainProcess() 读写       dispose() 保存
     ↓                           ↑
  首次访问时加载          程序卸载时保存
```

### 2. 脏标志机制（Dirty Flag）

**用途：**
- 标记缓存数据是否有改动
- 只在必要时执行保存操作

**工作流程：**
```
1. initial()：_G.cache_dirty = false
2. MainProcess()：修改数据，_G.cache_dirty = true
3. dispose()：如果 cache_dirty == true，执行保存
```

### 3. 元数据管理（Metadata）

**记录的信息：**
- 脚本版本号
- 初始化时间戳
- 缓存的用户总数
- 最后保存时间

**用途：**
- 监控脚本运行状态
- 调试性能问题
- 统计数据

## 📊 缓存策略的性能优势

### 场景1：频繁查询
```
直接访问 Mod Storage：1000个查询 = 1000次 IO
使用缓存：1次加载 + 999次内存查询 = 1次 IO （节省 99%）
```

### 场景2：批量修改
```
立即保存：100次修改 = 100次 IO
延迟保存：100次修改 + 1次批量保存 = 101次 IO （节省 1%，但更高效）
```

### 场景3：程序正常卸载
```
无缓存：可能丢失未保存的数据
有缓存：dispose() 自动保存所有数据
```

## 💾 数据流程示意

### initial() 的执行流程

```
脚本首次被调用
    ↓
initial() 被自动调用（只调用一次）
    ↓
1️⃣ 初始化缓存表 _G.affinity_cache = {}
    ↓
2️⃣ 初始化脏标志 _G.cache_dirty = false
    ↓
3️⃣ 【演示】从 Mod Storage 加载已有数据（伪代码示例）
    └─ for each stored_key in mod_storage
       └─ if key matches pattern "cache_*"
          └─ uid = extract user id from key
          └─ _G.affinity_cache[uid] = load value
    ↓
4️⃣ 初始化元数据表
    ├─ version = "1.0.0"
    ├─ initialized_at = os.time()
    ├─ total_users_cached = 0
    └─ last_save_time = 0
    ↓
5️⃣ 导出 API 到 _script_registry
    ├─ get_affinity()
    ├─ query_affinity()
    ├─ talk_to_bot()
    ├─ get_cache()
    └─ get_cache_dirty()
    ↓
完成初始化
```

### dispose() 的执行流程

```
Mod 卸载被触发
    ↓
dispose() 被自动调用（只调用一次）
    ↓
1️⃣ 检查缓存是否有改动
    └─ if _G.affinity_cache and _G.cache_dirty then
    ↓
2️⃣ 遍历缓存中的所有用户
    └─ for uid, affinity_value in pairs(_G.affinity_cache)
    ↓
3️⃣ 将每个用户的好感度保存到 Mod Storage
    ├─ store_key = "cache_" .. uid
    ├─ store_value = tostring(affinity_value)
    ├─ mod_storage_write(store_key, store_value)
    └─ 【演示】validate saved data
    ↓
4️⃣ 标记缓存为"干净"
    └─ _G.cache_dirty = false
    ↓
5️⃣ 更新元数据
    └─ _G.script_metadata.last_save_time = os.time()
    ↓
6️⃣ 清理内存
    ├─ for uid in pairs(_G.affinity_cache)
    │  └─ _G.affinity_cache[uid] = nil
    ├─ _G.affinity_cache = nil
    ├─ _G.cache_dirty = nil
    └─ _G.script_metadata = nil
    ↓
完成卸载和清理
```

## 🔍 关键实现细节

### 1. 缓存键生成
```lua
local store_key = "cache_" .. uid
-- 示例: uid = 123456789
--       store_key = "cache_123456789"
```

### 2. 类型转换
```lua
-- 保存时：数字 → 字符串
local store_value = tostring(affinity_value)
-- 读取时：字符串 → 数字
local affinity = tonumber(mod_storage_read(key)) or 100
```

### 3. 表的迭代
```lua
-- 使用 pairs() 遍历表中的所有键值对
for uid, affinity_value in pairs(_G.affinity_cache) do
    -- uid: 键（用户ID）
    -- affinity_value: 值（好感度）
end
```

## 📝 注释增加内容

| 函数 | 增加行数 | 新增说明 |
|------|---------|---------|
| initial() | +80 | 缓存结构、加载演示、元数据、API 扩展 |
| dispose() | +130 | 保存流程、验证方法、自动清理、容错处理 |
| **总计** | **+210** | **展示完整的缓存策略实现** |

## ✅ 修改检查清单

- ✓ initial() 函数显示了缓存表的完整结构
- ✓ 演示了如何从 Mod Storage 加载数据（伪代码）
- ✓ 添加了元数据表用于追踪脚本状态
- ✓ 扩展了 API 导出以支持缓存查询
- ✓ dispose() 显示了完整的保存流程
- ✓ 演示了数据验证方法
- ✓ 展示了两种内存清理方式
- ✓ 提供了容错处理的示例（pcall）
- ✓ 添加了生产级代码的最佳实践

## 🎓 学习要点

通过此修改，开发者可以学到：

1. **内存缓存策略** - 如何使用全局表实现高效缓存
2. **脏标志模式** - 如何优化 IO 操作
3. **生命周期管理** - initial/dispose 的完整用法
4. **元数据追踪** - 如何记录脚本运行状态
5. **API 导出** - 如何编写模块化的脚本接口
6. **数据验证** - 如何确保保存的数据完整
7. **错误处理** - 如何使用 pcall 实现容错
8. **性能优化** - 批量操作的优势

---

**文档更新时间**：2026年3月31日  
**修改类型**：缓存策略完整演示  
**影响范围**：initial() 和 dispose() 函数
