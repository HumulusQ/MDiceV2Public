namespace MDiceV2.Interfaces.Mod;

/// <summary>
/// Mod插件基础接口
/// 所有Mod都必须实现此接口来与MDiceV2宿主程序交互
/// </summary>
/// <remarks>
/// Mod的生命周期：
/// 1. 程序启动时，ModPluginLoader扫描 data/mods 文件夹
/// 2. 读取每个mod文件夹中的 mod.json 文件获取元数据
/// 3. 加载对应的DLL文件，通过反射查找实现IModPlugin的类
/// 4. 调用 OnLoad() 初始化Mod（仅一次）
/// 5. 当mod的 Enabled 状态变为 true 时，调用 OnEnable()
/// 6. 消息到达时，调用 OnGroupMessage() 或 OnPrivateMessage()
/// 7. 当mod的 Enabled 状态变为 false 时，调用 OnDisable()
/// 8. 程序关闭时，调用 OnUnload()（清理资源）
/// </remarks>
public interface IModPlugin
{
    /// <summary>
    /// Mod唯一标识符
    /// 必须与mod.json中的id字段保持一致
    /// 用于日志记录、缓存键值等
    /// 建议格式：com.author.modname，如 com.example.customreply
    /// </summary>
    string ModId { get; }

    /// <summary>
    /// Mod名称（用户可读）
    /// 例如：Custom Reply System
    /// </summary>
    string ModName { get; }

    /// <summary>
    /// Mod版本号
    /// 建议遵循语义化版本（SemVer）：major.minor.patch
    /// 例如：1.2.3
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Mod作者
    /// 用于UI显示和日志记录
    /// </summary>
    string Author { get; }

    /// <summary>
    /// Mod描述（可选）
    /// 简短的功能说明，用于UI显示
    /// </summary>
    string Description => "No description provided.";

    /// <summary>
    /// Mod初始化钩子
    /// 在程序启动时调用一次，用于资源初始化
    /// 
    /// 调用时机：
    /// - 程序启动时，成功加载DLL后立即调用
    /// - 仅调用一次，即使Mod被禁用也不重复调用
    /// 
    /// 实现建议：
    /// - 初始化数据结构（如规则库、缓存表等）
    /// - 加载Mod配置文件
    /// - 注册消息处理函数
    /// - 构建索引（如正则表达式匹配表）
    /// 
    /// 错误处理：
    /// - 如果初始化失败，应抛出异常，宿主程序会捕获并记录
    /// - 不初始化Mod相关资源会导致后续处理失败
    /// </summary>
    void OnLoad();

    /// <summary>
    /// Mod启用钩子
    /// 在Mod的启用状态从禁用变为启用时调用
    /// 
    /// 调用时机：
    /// - 用户在ModManagerPanel中启用Mod时
    /// - 可能被多次调用（禁用->启用->禁用->启用...）
    /// 
    /// 实现建议：
    /// - 注册事件监听器
    /// - 启动后台任务（如定时器）
    /// - 恢复被暂停的功能
    /// 
    /// 与OnLoad的区别：
    /// - OnLoad仅在程序启动时调用一次
    /// - OnEnable可能在运行时被多次调用
    /// </summary>
    void OnEnable();

    /// <summary>
    /// Mod禁用钩子
    /// 在Mod的启用状态从启用变为禁用时调用
    /// 
    /// 调用时机：
    /// - 用户在ModManagerPanel中禁用Mod时
    /// - 可能被多次调用
    /// 
    /// 实现建议：
    /// - 取消注册事件监听器
    /// - 停止后台任务
    /// - 暂存必要的Mod状态
    /// 
    /// 注意：
    /// - DLL Mod在禁用时不会被卸载，仍在内存中
    /// - 禁用Mod应该停止响应消息，但保留加载的资源
    /// - 重新启用时应能快速恢复功能（无需重新OnLoad）
    /// </summary>
    void OnDisable();

    /// <summary>
    /// Mod卸载钩子
    /// 在程序关闭前调用一次，用于资源清理
    /// 
    /// 调用时机：
    /// - 程序关闭或Mod从系统中卸载时
    /// - 仅调用一次
    /// 
    /// 实现建议：
    /// - 关闭数据库连接
    /// - 释放文件句柄
    /// - 停止所有后台任务
    /// - 保存Mod状态到配置文件
    /// 
    /// 错误处理：
    /// - 不应该在OnUnload中抛出异常
    /// - 尽量在有限时间内完成清理（超时可能被强制杀死）
    /// </summary>
    void OnUnload();

    /// <summary>
    /// 群消息处理钩子
    /// 当接收到群消息时调用，由Mod决定是否处理
    /// 
    /// 参数说明：
    /// - groupId: 群号
    /// - userId: 发言者QQ号
    /// - content: 消息内容（已清理@前缀）
    /// - isAted: 是否@了机器人
    /// 
    /// 返回值说明：
    /// - null: 表示Mod不处理此消息，继续传递给其他Mod或原消息处理器
    /// - non-null ModMessageResult: 表示Mod已处理此消息
    ///   - Intercepted=true: 消息已被拦截，宿主程序应停止处理
    ///   - Reply: 如果非null，宿主程序会代表机器人发送此内容
    ///   - StopPropagation=true: 阻止此消息继续传递给更低优先级的Mod
    /// 
    /// 调用时机：
    /// - 程序接收到每条群消息时调用（按优先级顺序）
    /// - 仅当Mod处于Enabled状态时调用
    /// - 在MessageProcessor.OnHandleMessage()前调用
    /// 
    /// 执行顺序：
    /// - 多个Mod会按mod.json中的priority字段排序后执行
    /// - priority值越大越先执行
    /// - 如果Mod返回non-null且StopPropagation=true，后续Mod不会被调用
    /// 
    /// 实现建议：
    /// - 在此处实现群消息的自定义回复逻辑
    /// - 支持多种匹配方式（精确、正则、模糊等）
    /// - 使用缓存加速频繁查询
    /// - 处理异常避免影响宿主程序
    /// 
    /// 示例代码：
    /// <code>
    /// public ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted)
    /// {
    ///     // 检查是否匹配触发规则
    ///     if (CheckMatchRule(content, out string reply))
    ///     {
    ///         return new ModMessageResult
    ///         {
    ///             Intercepted = true,
    ///             Reply = reply,
    ///             StopPropagation = true  // 防止其他Mod处理
    ///         };
    ///     }
    ///     return null;  // 不处理，继续传递
    /// }
    /// </code>
    /// </summary>
    ModMessageResult? OnGroupMessage(long groupId, long userId, string content, bool isAted);

    /// <summary>
    /// 私聊消息处理钩子
    /// 当接收到私聊消息时调用，由Mod决定是否处理
    /// 
    /// 参数说明：
    /// - userId: 发言者QQ号
    /// - content: 消息内容
    /// 
    /// 返回值说明：
    /// - null: 不处理此消息
    /// - non-null ModMessageResult: 已处理此消息
    /// 
    /// 调用时机与群消息相同，但仅针对私聊
    /// 
    /// 实现建议：
    /// - 在私聊中提供Mod的管理、配置等功能
    /// - 区分普通用户和管理员
    /// </summary>
    ModMessageResult? OnPrivateMessage(long userId, string content);
}
