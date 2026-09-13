namespace MDiceV2.Interfaces.Mod;

/// <summary>
/// Mod消息处理结果
/// 由Mod的OnGroupMessage和OnPrivateMessage方法返回
/// 告诉宿主程序该消息是否被处理及如何进行后续操作
/// </summary>
public class ModMessageResult
{
    /// <summary>
    /// 是否拦截此消息
    /// true: 消息已被Mod处理，不再继续传递给MessageProcessor进行命令解析
    /// false: 消息虽然已被Mod处理（可能发送了回复），但仍会继续传递给MessageProcessor
    /// 
    /// 典型场景：
    /// - CustomizedReply Mod拦截消息后，应设为true以防止触发其他指令
    /// - 仅用于日志记录的Mod可设为false以避免干扰正常流程
    /// </summary>
    public bool Intercepted { get; set; } = false;

    /// <summary>
    /// 回复内容
    /// null: 不发送回复
    /// non-null: 宿主程序会代表机器人发送此内容到原消息来源（群或私聊）
    /// 
    /// 发送方式：
    /// - 群消息：发送到该群
    /// - 私聊消息：发送给该用户
    /// 
    /// 长度限制：
    /// - 单条消息通常限制在8000字符以内（OneBot协议）
    /// - 超长回复应分多条发送
    /// </summary>
    public string? Reply { get; set; } = null;

    /// <summary>
    /// 是否阻止消息继续传播
    /// true: 停止将此消息传递给更低优先级的Mod（仅当Intercepted=true时有意义）
    /// false: 继续传递给其他Mod处理
    ///
    /// 优先级说明：
    /// - Mod按mod.json中的priority字段从高到低排序
    /// - priority值越大越先执行
    /// - 如果某Mod返回non-null且StopPropagation=true，后续Mod被跳过
    ///
    /// 推荐用法：
    /// - 重要的Mod（如权限验证）应设为true以防止其他Mod覆盖
    /// - 普通Mod设为false以允许链式处理
    /// </summary>
    public bool StopPropagation { get; set; } = false;

    /// <summary>
    /// 来源Mod ID（用于RefineMsg的modId参数）
    /// null表示主程序调用
    /// </summary>
    public string? ModId { get; set; } = null;

    /// <summary>
    /// 创建一个拦截消息的结果（带回复）
    /// </summary>
    public static ModMessageResult Intercept(string reply, bool stopPropagation = true, string? modId = null)
        => new()
        {
            Intercepted = true,
            Reply = reply,
            StopPropagation = stopPropagation,
            ModId = modId
        };

    /// <summary>
    /// 创建一个拦截消息的结果（不发送回复）
    /// </summary>
    public static ModMessageResult InterceptSilent(bool stopPropagation = true, string? modId = null)
        => new()
        {
            Intercepted = true,
            Reply = null,
            StopPropagation = stopPropagation,
            ModId = modId
        };

    /// <summary>
    /// 创建一个不拦截消息的结果（仅发送回复）
    /// </summary>
    public static ModMessageResult ReplyOnly(string content, string? modId = null)
        => new()
        {
            Intercepted = false,
            Reply = content,
            StopPropagation = false,
            ModId = modId
        };

    /// <summary>
    /// 创建一个不拦截、不回复的结果（等同于返回null）
    /// </summary>
    public static ModMessageResult NoAction(string? modId = null)
        => new()
        {
            Intercepted = false,
            Reply = null,
            StopPropagation = false,
            ModId = modId
        };
}
