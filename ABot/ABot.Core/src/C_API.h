/**
 * @file C_API.h
 * @brief ABOT C API - 导出给C#/C++/CLI使用的接口
 * 
 * 设计原则：
 * =========
 * - 所有参数和返回值都是POD类型（Plain Old Data）
 * - 使用opaque指针(void*)来表示C++对象
 * - 错误通过返回码而非异常来表达
 * - 支持未来迁移到P/Invoke
 */

#ifndef ABOT_C_API_H
#define ABOT_C_API_H

#ifdef __cplusplus
extern "C" {
#endif

// 导出宏定义 - 用于DLL导出函数
#ifdef _MSC_VER
#define ABOT_API __declspec(dllexport)
#else
#define ABOT_API
#endif

/*
 * ============ 类型定义 ============
 */

// 不透明指针，代表一个解释器实例
typedef void* ABOT_HANDLE;

// 错误码枚举
typedef int ABOT_ERROR;
#define ABOT_OK                 0
#define ABOT_ERROR_NULL_PTR     1
#define ABOT_ERROR_INVALID_XML  2
#define ABOT_ERROR_PARSE_ERROR  3
#define ABOT_ERROR_COMPILE_ERROR 4
#define ABOT_ERROR_RUNTIME_ERROR 5
#define ABOT_ERROR_OUT_OF_MEMORY 6
#define ABOT_ERROR_UNKNOWN      -1

/*
 * ============ 生命周期管理 ============
 */

/**
 * 创建新的解释器实例
 * 
 * @return 解释器句柄，如果创建失败返回NULL
 */
ABOT_API ABOT_HANDLE abot_create(void);

/**
 * 销毁解释器实例，释放所有资源
 * 
 * @param handle 解释器句柄
 */
ABOT_API void abot_destroy(ABOT_HANDLE handle);

/*
 * ============ 脚本加载和编译 ============
 */

/**
 * 解析人物卡XML定义
 * 
 * @param handle 解释器句柄
 * @param character_xml 人物卡的XML字符串 (UTF-8)
 * @return ABOT_OK 成功，其他值表示错误
 */
ABOT_API ABOT_ERROR abot_parse_character(ABOT_HANDLE handle, const char* character_xml);

/**
 * 注册技能集
 * 
 * @param handle 解释器句柄
 * @param skillset_xml 技能集的XML字符串 (UTF-8)
 * @return ABOT_OK 成功，其他值表示错误
 */
ABOT_API ABOT_ERROR abot_register_skillset(ABOT_HANDLE handle, const char* skillset_xml);

/**
 * 注册状态集
 * 
 * @param handle 解释器句柄
 * @param stateset_xml 状态集的XML字符串 (UTF-8)
 * @return ABOT_OK 成功，其他值表示错误
 */
ABOT_API ABOT_ERROR abot_register_stateset(ABOT_HANDLE handle, const char* stateset_xml);

/**
 * 注册安科集
 * 
 * @param handle 解释器句柄
 * @param ankeset_xml 安科集的XML字符串 (UTF-8)
 * @return ABOT_OK 成功，其他值表示错误
 */
ABOT_API ABOT_ERROR abot_register_ankeset(ABOT_HANDLE handle, const char* ankeset_xml);

/*
 * ============ 战斗执行 ============
 */

/**
 * 执行战斗
 * 
 * @param handle 解释器句柄
 * @return ABOT_OK 成功，其他值表示错误
 */
ABOT_API ABOT_ERROR abot_execute_battle(ABOT_HANDLE handle);

/**
 * 初始化回合管理器
 * 
 * @param handle 解释器句柄
 * @return ABOT_OK 成功，其他值表示错误
 */
ABOT_API ABOT_ERROR abot_round_manager_init(ABOT_HANDLE handle);

/**
 * 推进一个回合
 * 
 * @param handle 解释器句柄
 * @return ABOT_OK 成功，其他值表示错误
 */
ABOT_API ABOT_ERROR abot_round_manager_advance(ABOT_HANDLE handle);

/**
 * 推进指定数量的回合
 * 
 * @param handle 解释器句柄
 * @param count 要推进的回合数
 * @return ABOT_OK 成功，其他值表示错误
 */
ABOT_API ABOT_ERROR abot_round_manager_advance_multiple(ABOT_HANDLE handle, int count);

/**
 * 跳过当前回合
 * 
 * @param handle 解释器句柄
 * @return ABOT_OK 成功，其他值表示错误
 */
ABOT_API ABOT_ERROR abot_round_manager_skip(ABOT_HANDLE handle);

/**
 * 暂停战斗
 * 
 * @param handle 解释器句柄
 */
ABOT_API void abot_round_manager_pause(ABOT_HANDLE handle);

/**
 * 恢复战斗
 * 
 * @param handle 解释器句柄
 */
ABOT_API void abot_round_manager_resume(ABOT_HANDLE handle);

/**
 * 检查战斗是否在运行中
 * 
 * @param handle 解释器句柄
 * @return 1 表示运行中，0 表示未运行或已结束
 */
ABOT_API int abot_round_manager_is_running(ABOT_HANDLE handle);

/**
 * 检查战斗是否已结束
 * 
 * @param handle 解释器句柄
 * @return 1 表示已结束，0 表示仍在进行
 */
ABOT_API int abot_round_manager_is_finished(ABOT_HANDLE handle);

/**
 * 获取当前回合数
 * 
 * @param handle 解释器句柄
 * @return 当前回合数
 */
ABOT_API int abot_round_manager_get_current_round(ABOT_HANDLE handle);

/**
 * 检查 RoundManager 是否已初始化并准备好执行
 * ✅ 防御性检查函数 - 用于验证状态恢复的完整性
 * 
 * 检查项：
 * - RoundManager 是否已创建（!= nullptr）
 * - RoundManager 是否已初始化（IsInitialized() == true）
 * - Battle 对象是否已创建（battle_ != nullptr）
 * - 至少有一个角色在场
 * 
 * @param handle 解释器句柄
 * @return 1 如果 RoundManager 完全准备好可以执行，0 否则
 * 
 * 使用场景：LoadState() 导入后进行健康检查
 *          防止 abot_import_state_json_utf8() 的假成功问题
 */
ABOT_API int abot_round_manager_is_ready(ABOT_HANDLE handle);

/**
 * 获取战斗状态摘要
 * 
 * @param handle 解释器句柄
 * @return 状态字符串 (UTF-8)，由abot库管理，调用方不应释放
 */
ABOT_API const char* abot_round_manager_get_status(ABOT_HANDLE handle);

/**
 * 获取战斗日志
 * 
 * @param handle 解释器句柄
 * @return 日志字符串 (UTF-8)，由abot库管理，调用方不应释放
 */
ABOT_API const char* abot_round_manager_get_log(ABOT_HANDLE handle);

/**
 * 获取技能触发日志
 * 
 * @param handle 解释器句柄
 * @return 技能触发日志字符串 (UTF-8)，由abot库管理，调用方不应释放
 */
ABOT_API const char* abot_round_manager_get_skill_trigger_log(ABOT_HANDLE handle);

/**
 * 执行扩展指令
 * 
 * @param handle 解释器句柄
 * @param command 指令名称
 * @param parameters 指令参数 (可选)
 * @return ABOT_OK 成功，其他值表示错误
 */
ABOT_API ABOT_ERROR abot_round_manager_execute_command(ABOT_HANDLE handle, const char* command, const char* parameters);

/**
 * 执行脚本代码片段
 * 
 * @param handle 解释器句柄
 * @param script ABOT脚本代码 (UTF-8)
 * @return ABOT_OK 成功，其他值表示错误
 */
ABOT_API ABOT_ERROR abot_execute_script(ABOT_HANDLE handle, const char* script);

/*
 * ============ 错误处理 ============
 */

/**
 * 获取最后发生的错误信息
 * 
 * @param handle 解释器句柄
 * @return 错误消息字符串(UTF-8)，如果无错误返回空字符串
 * 
 * 注意：返回的指针由abot库管理，调用方不应释放
 */
ABOT_API const char* abot_get_last_error(ABOT_HANDLE handle);

/**
 * 清除错误信息
 * 
 * @param handle 解释器句柄
 */
ABOT_API void abot_clear_error(ABOT_HANDLE handle);

/*
 * ============ 状态查询 ============
 */

/**
 * 获取解释器版本
 * 
 * @return 版本字符串(UTF-8)
 */
ABOT_API const char* abot_get_version(void);

/**
 * 检查解释器是否准备就绪
 * 
 * @param handle 解释器句柄
 * @return 1 如果准备就绪，0 否则
 */
ABOT_API int abot_is_ready(ABOT_HANDLE handle);

/*
 * ============ 参数解析 ============
 */

/**
 * 解析参数单元XML
 * 
 * @param handle 解释器句柄
 * @param parameter_xml 参数单元的XML字符串 (UTF-8)
 * @return 参数单元句柄，失败返回NULL
 */
ABOT_API ABOT_HANDLE abot_parse_parameter(ABOT_HANDLE handle, const char* parameter_xml);

/**
 * 销毁参数单元
 * 
 * @param param_handle 参数单元句柄
 */
ABOT_API void abot_parameter_destroy(ABOT_HANDLE param_handle);

/**
 * 获取参数单元名称
 * 
 * @param param_handle 参数单元句柄
 * @return 参数名称
 */
ABOT_API const char* abot_parameter_get_name(ABOT_HANDLE param_handle);

/**
 * 获取参数属性值（字符串）
 * 
 * @param param_handle 参数单元句柄
 * @param key 属性键
 * @return 属性值，如果不存在返回空字符串
 */
ABOT_API const char* abot_parameter_get_attribute(ABOT_HANDLE param_handle, const char* key);

/**
 * 获取参数属性值（整数）
 * 
 * @param param_handle 参数单元句柄
 * @param key 属性键
 * @return 属性值（整数），如果不存在或不是有效整数返回0
 */
ABOT_API int abot_parameter_get_attribute_int(ABOT_HANDLE param_handle, const char* key);

/*
 * ============ 角色管理 ============
 */

/**
 * 从参数单元创建角色
 * 
 * @param handle 解释器句柄
 * @param param_handle 参数单元句柄
 * @return 角色句柄，失败返回NULL
 */
ABOT_API ABOT_HANDLE abot_character_create(ABOT_HANDLE handle, ABOT_HANDLE param_handle);

/**
 * 销毁角色
 * 
 * @param char_handle 角色句柄
 */
ABOT_API void abot_character_destroy(ABOT_HANDLE char_handle);

/**
 * 获取角色名称
 * 
 * @param char_handle 角色句柄
 * @return 角色名称
 */
ABOT_API const char* abot_character_get_name(ABOT_HANDLE char_handle);

/**
 * 获取角色阵营
 * 
 * @param char_handle 角色句柄
 * @return 阵营编号
 */
ABOT_API int abot_character_get_camp(ABOT_HANDLE char_handle);

/**
 * 获取角色HP
 * 
 * @param char_handle 角色句柄
 * @return 当前HP
 */
ABOT_API int abot_character_get_hp(ABOT_HANDLE char_handle);

/**
 * 获取角色最大HP
 * 
 * @param char_handle 角色句柄
 * @return 最大HP
 */
ABOT_API int abot_character_get_max_hp(ABOT_HANDLE char_handle);

/**
 * 获取角色攻击值
 * 
 * @param char_handle 角色句柄
 * @return 攻击值
 */
ABOT_API int abot_character_get_atk(ABOT_HANDLE char_handle);

/**
 * 角色受伤害
 * 
 * @param char_handle 角色句柄
 * @param damage 伤害值
 * @return ABOT_OK 或错误码
 */
ABOT_API ABOT_ERROR abot_character_take_damage(ABOT_HANDLE char_handle, int damage);

/**
 * 角色治疗
 * 
 * @param char_handle 角色句柄
 * @param healing 治疗值
 * @return ABOT_OK 或错误码
 */
ABOT_API ABOT_ERROR abot_character_heal(ABOT_HANDLE char_handle, int healing);

/**
 * 检查角色是否活着
 * 
 * @param char_handle 角色句柄
 * @return 1 如果活着，0 否则
 */
ABOT_API int abot_character_is_alive(ABOT_HANDLE char_handle);

/*
 * ============ 战斗管理 ============
 */

/**
 * 创建战斗实例
 * 
 * @param handle 解释器句柄
 * @return 战斗句柄，失败返回NULL
 */
ABOT_API ABOT_HANDLE abot_battle_create(ABOT_HANDLE handle);

/**
 * 销毁战斗实例
 * 
 * @param battle_handle 战斗句柄
 */
ABOT_API void abot_battle_destroy(ABOT_HANDLE battle_handle);

/**
 * 初始化战斗（添加角色）
 * 
 * @param battle_handle 战斗句柄
 * @param characters 角色数组
 * @param count 角色数量
 * @return ABOT_OK 或错误码
 */
ABOT_API ABOT_ERROR abot_battle_initialize(ABOT_HANDLE battle_handle, ABOT_HANDLE* characters, int count);

/**
 * 开始战斗
 * 
 * @param battle_handle 战斗句柄
 * @return ABOT_OK 或错误码
 */
ABOT_API ABOT_ERROR abot_battle_start(ABOT_HANDLE battle_handle);

/**
 * 执行战斗一轮
 * 
 * @param battle_handle 战斗句柄
 * @return ABOT_OK 或错误码
 */
ABOT_API ABOT_ERROR abot_battle_execute_round(ABOT_HANDLE battle_handle);

/**
 * 检查战斗是否结束
 * 
 * @param battle_handle 战斗句柄
 * @return 1 如果结束，0 否则
 */
ABOT_API int abot_battle_is_finished(ABOT_HANDLE battle_handle);

/**
 * 获取胜利阵营
 * 
 * @param battle_handle 战斗句柄
 * @return 胜利阵营编号，如果战斗未结束返回-1
 */
ABOT_API int abot_battle_get_victory_camp(ABOT_HANDLE battle_handle);

/**
 * 获取当前回合数
 * 
 * @param battle_handle 战斗句柄
 * @return 回合数（从1开始）
 */
ABOT_API int abot_battle_get_current_round(ABOT_HANDLE battle_handle);

/*
 * ============ 状态序列化/反序列化 ============
 */

/**
 * 将当前已解析的角色序列化为JSON格式
 * 用于保存角色状态到数据库
 *
 * @param handle 解释器句柄
 * @return JSON字符串，包含角色的所有属性
 */
ABOT_API const char* abot_serialize_character_json(ABOT_HANDLE handle);

/**
 * 从JSON反序列化并创建一个新的角色
 * 解析给定的JSON，创建Character对象并添加到RoundManager
 *
 * @param handle 解释器句柄
 * @param character_json JSON字符串，包含角色数据
 * @return ABOT_OK 成功，其他值表示错误
 */
ABOT_API ABOT_ERROR abot_deserialize_character_json(ABOT_HANDLE handle, const char* character_json);

#ifdef __cplusplus
}
#endif

#endif  // ABOT_C_API_H

